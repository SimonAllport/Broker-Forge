using BrokerForge.Abstractions;
using BrokerForge.Auth;
using BrokerForge.Contracts;
using BrokerForge.Descriptors.Model;
using BrokerForge.Descriptors.Parsing;
using BrokerForge.Descriptors.Validation;
using BrokerForge.Keys;
using BrokerForge.Schema;

namespace BrokerForge.Host.Config;

/// <summary>
/// The configuration one request runs against: service instances and object maps from host configuration, descriptors
/// from <c>Config:DescriptorsPath/&lt;instance&gt;/*.yaml</c> and table schemas from
/// <c>Config:SchemasPath/&lt;instance&gt;.yaml</c> (D-10, D-16), plus, per environment, the connection designs
/// (<see cref="DesignConfigInput"/>). Everything is parsed and validated when the store is built; any error fails the
/// whole store (at startup for the files), so a bad descriptor never reaches a request. An environment's store holds
/// only the designs that are valid beside the files; the others are listed in <see cref="DesignFailures"/>
/// (<see cref="CompositeConfigProvider"/>). The host reads a schema only for the
/// Custom API parameters it declares: CRUD descriptors are checked against schemas by <c>bf validate</c> in CI.
/// </summary>
public sealed class HostConfigStore
{
    private static readonly HashSet<AuthMode> PerUserModes = new() { AuthMode.OAuth, AuthMode.Impersonate, AuthMode.Sso };
    private static readonly HashSet<string> CrudMessages = new() { Messages.Retrieve, Messages.RetrieveMultiple, Messages.Create, Messages.Update, Messages.Delete };

    private readonly Dictionary<string, ServiceInstance> _instances = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ResolvedMap> _maps = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Instance, string Operation), OperationDescriptor> _descriptors = new();
    private readonly Dictionary<string, SourceSchema> _schemas = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ResolvedCustomApi> _customApis = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OAuthResource> _oauthResources = new(StringComparer.Ordinal);
    private string? _connectBaseUrl;
    private string? _connectClientId;

    public sealed record ResolvedMap(ObjectMap Map, IKeyStrategy Keys);

    /// <summary>An unbound Custom API: the instance and descriptor that serve it, and its declared parameters.</summary>
    public sealed record ResolvedCustomApi(ServiceInstance Instance, OperationDescriptor Descriptor, CustomApiSchema Schema);

    private readonly Dictionary<string, DesignOrigin> _designs;

    public HostConfigStore(ConfigInput input) : this(input, out var problems)
    {
        if (problems.Count > 0)
            throw new ConfigurationException(
                (input.EnvironmentId == null ? "Host configuration is invalid:" : $"Configuration for environment {input.EnvironmentId} is invalid:")
                + string.Concat(problems.Select(p => "\n - " + p)));
    }

    /// <summary>The store, or null with every problem when the input is not valid.</summary>
    internal static HostConfigStore? TryCreate(ConfigInput input, out IReadOnlyList<string> problems)
    {
        var store = new HostConfigStore(input, out var found);
        problems = found;
        return found.Count == 0 ? store : null;
    }

    private HostConfigStore(ConfigInput input, out List<string> problems)
    {
        Input = input;
        _designs = input.Designs;
        problems = new List<string>();
        LoadOAuthResources(input.OAuthResources, problems);
        _connectBaseUrl = input.ConnectBaseUrl?.TrimEnd('/');
        _connectClientId = input.ConnectClientId;
        LoadInstances(input.Instances, problems);
        problems.AddRange(input.Problems);
        LoadSchemas(input.SchemaTexts, problems);
        problems.AddRange(input.DescriptorProblems);
        LoadDescriptors(input.DescriptorTexts, problems);
        LoadMaps(input.ObjectMaps, problems);
    }

    /// <summary>What the store was built from; an environment's store merges its designs into the file store's input.</summary>
    internal ConfigInput Input { get; }

    /// <summary>Why connection designs of this environment were left out (<see cref="ConfigInput.DesignFailures"/>); empty when all loaded.</summary>
    public IReadOnlyList<string> DesignFailures => Input.DesignFailures;

    /// <summary>
    /// For a request this store cannot resolve: when connection designs of its environment were left out, the
    /// <see cref="ConfigurationException"/> to throw instead of "unknown", since the missing design may be what would
    /// have resolved it. Null when every design loaded.
    /// </summary>
    public ConfigurationException? NotLoaded(string what) => DesignFailures.Count == 0 ? null : new ConfigurationException(
        $"{what} cannot be served in environment {Input.EnvironmentId}, where connection designs could not be loaded:"
        + string.Concat(DesignFailures.Select(f => "\n - " + f)));

    public static HostConfigStore FromConfiguration(IConfiguration configuration) => new(FileConfigInput.Read(configuration));

    public IReadOnlyCollection<string> DescriptorNames => _descriptors.Keys.Select(k => $"{k.Instance}/{k.Operation}").ToList();

    public ServiceInstance? Instance(string name) => _instances.GetValueOrDefault(name);

    public IReadOnlyCollection<ServiceInstance> Instances => _instances.Values;

    /// <summary>Names of the instances in OAuth, Impersonate or SSO mode.</summary>
    public IReadOnlyList<string> PerUserInstances =>
        _instances.Values.Where(i => PerUserModes.Contains(i.Auth.Mode)).Select(i => i.Name).Order(StringComparer.Ordinal).ToList();

    public ResolvedMap? Map(string table) => _maps.GetValueOrDefault(table);

    public OperationDescriptor? Descriptor(string instance, string operation) => _descriptors.GetValueOrDefault((instance, operation));

    public ResolvedCustomApi? CustomApi(string uniqueName) => _customApis.GetValueOrDefault(uniqueName);

    /// <summary>A resource an instance names; startup has already checked every name exists.</summary>
    public OAuthResource OAuthResource(string name) =>
        _oauthResources.GetValueOrDefault(name) ?? throw new ConfigurationException($"OAuthResources:{name} is not configured.");

    /// <summary>
    /// The connect page for an instance (HLD D-26): the connectUrl of its ConnectionRequiredException. A design
    /// instance's page carries its environment, because its name is unique only there.
    /// </summary>
    public string ConnectUrl(ServiceInstance instance) =>
        (_connectBaseUrl ?? throw new ConfigurationException("Connect:PublicBaseUrl is not set.")) + ConnectPath(instance);

    /// <summary>The host-relative path of an instance's connect page.</summary>
    public static string ConnectPath(ServiceInstance instance) =>
        "/connect/" + (instance.EnvironmentId == null ? "" : Uri.EscapeDataString(instance.EnvironmentId) + "/") + Uri.EscapeDataString(instance.Name);

    private string DesignSuffix(string instance) => _designs.TryGetValue(instance, out var d) ? $" (design v{d.Version})" : "";

    private string SchemaSource(string instance) => $"schemas/{instance}.yaml{DesignSuffix(instance)}";

    /// <summary>The redirect URI every OAuth resource returns to (the connect page's callback).</summary>
    public string ConnectCallbackUrl =>
        (_connectBaseUrl ?? throw new ConfigurationException("Connect:PublicBaseUrl is not set.")) + "/connect/callback";

    private void LoadOAuthResources(Dictionary<string, OAuthResourceOptions> resources, List<string> problems)
    {
        foreach (var (key, o) in resources)
        {
            var where = $"OAuthResources:{key}";
            foreach (var (name, value) in new[] { ("AuthorizeUrl", o.AuthorizeUrl), ("TokenUrl", o.TokenUrl) })
                if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
                    problems.Add($"{where}:{name} must be an absolute http(s) URL");
            if (o.Scopes.Count == 0 || o.Scopes.Any(string.IsNullOrWhiteSpace))
                problems.Add($"{where}:Scopes needs at least one scope");
            if (o.UsesConnectApp)
            {
                if (o.ClientId != null)
                    problems.Add($"{where}:ClientId must be absent: UsesConnectApp takes the client from Connect settings");
                if (o.ClientSecretRef != null)
                    problems.Add($"{where}:ClientSecretRef must be absent: UsesConnectApp takes the client from Connect settings");
            }
            else if (string.IsNullOrWhiteSpace(o.ClientId))
                problems.Add($"{where}:ClientId is required");
            if (o.ClientSecretRef != null)
                try { SecretRef.Parse(o.ClientSecretRef); }
                catch (FormatException ex) { problems.Add($"{where}: {ex.Message}"); }
            _oauthResources[key] = new OAuthResource(key, o.AuthorizeUrl, o.TokenUrl, o.Scopes, o.ClientId, o.ClientSecretRef, o.UsesConnectApp);
        }
    }

    /// <summary>The per-user settings of one instance (D-28, D-29); <paramref name="where"/> is its Instances:&lt;name&gt; path.</summary>
    private AuthConfig? PerUserAuth(AuthMode mode, AuthOptions a, string where, List<string> problems)
    {
        var path = where + ":Auth";
        if (!Enum.TryParse<AsyncContext>(a.AsyncContext ?? nameof(AsyncContext.TriggeringUser), out var asyncContext))
            problems.Add($"{path}:AsyncContext must be TriggeringUser or ServiceIdentity");
        var perUser = PerUserModes.Contains(mode);
        if (!perUser)
        {
            if (a.AsyncAuth != null || a.AsyncContext != null)
                problems.Add($"{path}:AsyncContext and AsyncAuth are only for per-user modes; {mode} already runs as one identity");
            return null;
        }

        switch (mode)
        {
            case AuthMode.OAuth or AuthMode.Impersonate:
                if (a.OAuthResource == null || !_oauthResources.TryGetValue(a.OAuthResource, out var resource))
                    problems.Add($"{path}:OAuthResource must name an entry in OAuthResources");
                else if (resource.UsesConnectApp != (mode == AuthMode.Impersonate))
                    problems.Add(mode == AuthMode.Impersonate
                        ? $"{path}:OAuthResource {resource.Name} must set UsesConnectApp: Impersonate is the Entra flow through the connect app"
                        : $"{path}:OAuthResource {resource.Name} uses the connect app, which is for Impersonate; OAuth needs the source's own client");
                break;
            case AuthMode.Sso:
                if (a.SsoKind is not ("basic" or "apiKeyHeader"))
                    problems.Add($"{path}:SsoKind must be basic or apiKeyHeader");
                else if (a.SsoKind == "apiKeyHeader" && string.IsNullOrWhiteSpace(a.HeaderName))
                    problems.Add($"{path}:HeaderName is required for SsoKind apiKeyHeader");
                break;
        }
        if (_connectBaseUrl == null || !Uri.TryCreate(_connectBaseUrl, UriKind.Absolute, out var connect) || connect.Scheme is not ("https" or "http"))
            problems.Add($"Connect:PublicBaseUrl must be an absolute http(s) URL: {where} uses {mode}, whose ConnectionRequiredException carries the connect URL");
        if (string.IsNullOrWhiteSpace(_connectClientId))
            problems.Add($"Connect:ClientId is required: {where} uses {mode}, and its users sign in to the connect page with the BrokerForge Connect app");

        if (asyncContext == AsyncContext.ServiceIdentity && a.AsyncAuth == null)
            problems.Add($"{path}:AsyncAuth is required when AsyncContext is ServiceIdentity");
        if (asyncContext == AsyncContext.TriggeringUser && a.AsyncAuth != null)
            problems.Add($"{path}:AsyncAuth is set but AsyncContext is TriggeringUser; set AsyncContext to ServiceIdentity or remove AsyncAuth");
        if (a.AsyncAuth == null || asyncContext != AsyncContext.ServiceIdentity)
            return null;
        var asyncPath = path + ":AsyncAuth";
        if (!Enum.GetNames<AuthMode>().Contains(a.AsyncAuth.Mode, StringComparer.Ordinal) || !Enum.TryParse<AuthMode>(a.AsyncAuth.Mode, out var asyncMode) || asyncMode is not (AuthMode.ServiceAccount or AuthMode.Static))
        {
            problems.Add($"{asyncPath}:Mode must be ServiceAccount or Static");
            return null;
        }
        if (a.AsyncAuth.AsyncAuth != null || a.AsyncAuth.AsyncContext != null)
            problems.Add($"{asyncPath} cannot have its own AsyncContext or AsyncAuth");
        if (a.AsyncAuth.SecretRef != null)
            try { SecretRef.Parse(a.AsyncAuth.SecretRef); }
            catch (FormatException ex) { problems.Add($"{asyncPath}: {ex.Message}"); }
        var x = a.AsyncAuth;
        return new AuthConfig(asyncMode, x.StaticKind, x.HeaderName, x.QueryParam, x.Username, x.SecretRef, x.Scope);
    }

    private void LoadSchemas(Dictionary<string, string> texts, List<string> problems)
    {
        foreach (var instance in _instances.Keys)
        {
            if (!texts.TryGetValue(instance, out var text))
                continue;
            try
            {
                _schemas[instance] = SchemaParser.Parse(text, SchemaSource(instance));
            }
            catch (SchemaException ex)
            {
                problems.Add(ex.Message);
            }
        }
    }

    private void LoadInstances(Dictionary<string, InstanceOptions> instances, List<string> problems)
    {
        foreach (var (key, o) in instances)
        {
            var where = $"Instances:{key}";
            var design = _designs.GetValueOrDefault(key);
            if (o.BrokerType != "rest")
                problems.Add($"{where}:BrokerType must be rest in phase 1");
            // A design instance without its base URL is Incomplete: its calls fail naming the variable to set.
            if (design is not { Incomplete.Count: > 0 } && (!Uri.TryCreate(o.BaseUrl, UriKind.Absolute, out var baseUri) || baseUri.Scheme is not ("https" or "http") || !string.IsNullOrEmpty(baseUri.Query)))
                problems.Add($"{where}:BaseUrl must be an absolute http(s) URL without a query");
            // Exactly a name: Enum.TryParse would also take "0", " Static" or "Static, OAuth".
            if (!Enum.GetNames<AuthMode>().Contains(o.Auth.Mode, StringComparer.Ordinal) || !Enum.TryParse<AuthMode>(o.Auth.Mode, out var mode))
            {
                problems.Add($"{where}:Auth:Mode must be one of {string.Join(", ", Enum.GetNames<AuthMode>())}");
                continue;
            }
            var asyncAuth = PerUserAuth(mode, o.Auth, where, problems);
            foreach (var reference in o.Secrets.Values.Append(o.Auth.SecretRef).Where(r => r != null))
                try
                {
                    if (SecretRef.Parse(reference!).Scheme == "store" && design == null)
                        problems.Add($"{where}: {reference} is an instance secret, which only a connection design can use; use kv or env");
                }
                catch (FormatException ex) { problems.Add($"{where}: {ex.Message}"); }

            _instances[key] = new ServiceInstance(
                key, o.BrokerType, o.BaseUrl.TrimEnd('/'),
                new AuthConfig(mode, o.Auth.StaticKind, o.Auth.HeaderName, o.Auth.QueryParam, o.Auth.Username, o.Auth.SecretRef, o.Auth.Scope,
                    o.Auth.OAuthResource, o.Auth.SsoKind,
                    Enum.TryParse<AsyncContext>(o.Auth.AsyncContext, out var ac) ? ac : AsyncContext.TriggeringUser, asyncAuth),
                o.Config, o.Secrets, design?.EnvironmentId)
            {
                Incomplete = design?.Incomplete ?? Array.Empty<string>(),
                DesignVersion = design?.Version,
            };
        }
    }

    private void LoadDescriptors(Dictionary<(string Instance, string File), string> texts, List<string> problems)
    {
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var instance in _instances.Values)
        {
            var reserved = AuthReservedNames.For(instance);
            var schema = _schemas.GetValueOrDefault(instance.Name);
            foreach (var (file, text) in texts.Where(t => t.Key.Instance == instance.Name)
                         .Select(t => (t.Key.File, t.Value)).OrderBy(t => t.File, StringComparer.Ordinal))
            {
                var source = $"{instance.Name}/{file}{DesignSuffix(instance.Name)}";
                try
                {
                    var d = DescriptorParser.Parse(text, source);
                    problems.AddRange(DescriptorValidator.Validate(d, reserved, d.CustomApi != null ? schema : null).Select(e => $"{source}: {e}"));
                    if (!_descriptors.TryAdd((instance.Name, d.Operation), d))
                        problems.Add($"{source}: operation {d.Operation} is declared twice for {instance.Name}");
                    if (d.CustomApi is { } api)
                        AddCustomApi(instance, d, api, schema, source, sources, problems);
                }
                catch (DescriptorException ex)
                {
                    problems.Add(ex.Message);
                }
            }
            foreach (var api in schema?.CustomApis.Keys ?? Enumerable.Empty<string>())
                if (_customApis.GetValueOrDefault(api)?.Instance != instance)
                    problems.Add($"{SchemaSource(instance.Name)} declares Custom API {api}, but no {instance.Name} descriptor binds it");
        }
    }

    private void AddCustomApi(ServiceInstance instance, OperationDescriptor d, string api, SourceSchema? schema, string source,
        Dictionary<string, string> sources, List<string> problems)
    {
        // Custom API names are global in Dataverse, so one name must resolve to exactly one descriptor.
        if (sources.TryGetValue(api, out var other))
        {
            problems.Add($"Custom API {api} is bound by both {other} and {source}");
            return;
        }
        sources[api] = source;
        if (schema?.CustomApis.GetValueOrDefault(api) is not { } declared)
        {
            problems.Add($"{source}: binds Custom API {api}, but {SchemaSource(instance.Name)} does not declare it, so its parameters are unknown");
            return;
        }
        _customApis[api] = new ResolvedCustomApi(instance, d, declared);
    }

    private void LoadMaps(Dictionary<string, ObjectMapOptions> maps, List<string> problems)
    {
        foreach (var (key, o) in maps)
        {
            var where = $"ObjectMaps:{key}";
            if (!_instances.ContainsKey(o.ServiceInstance))
            {
                problems.Add($"{where}:ServiceInstance `{o.ServiceInstance}` is not configured");
                continue;
            }
            if (!Enum.TryParse<KeyKind>(o.KeyKind, out var kind))
            {
                problems.Add($"{where}:KeyKind must be one of {string.Join(", ", Enum.GetNames<KeyKind>())}");
                continue;
            }
            IKeyStrategy? keys = (o.KeyStrategy, kind) switch
            {
                ("native", KeyKind.Guid) => new NativeGuidKeyStrategy(),
                ("reversible", not KeyKind.Guid) => new ReversibleKeyStrategy(kind),
                _ => null,
            };
            if (keys == null)
            {
                problems.Add($"{where}: KeyStrategy {o.KeyStrategy} with KeyKind {o.KeyKind} is not available in phase 1 (native needs Guid; reversible needs Int32, Int64 or String; keymap is phase 5)");
                continue;
            }
            foreach (var (message, operation) in o.Operations)
            {
                if (!CrudMessages.Contains(message))
                    problems.Add($"{where}:Operations:{message} is not a virtual table message");
                else if (!_descriptors.TryGetValue((o.ServiceInstance, operation), out var d))
                    problems.Add($"{where}:Operations:{message}: no descriptor {operation} for {o.ServiceInstance}");
                else if (d.Binds != message)
                    problems.Add($"{where}:Operations:{message}: {operation} binds {d.Binds}");
                else if (d.Table != null && d.Table != key)
                    problems.Add($"{where}:Operations:{message}: {operation} is for table {d.Table}");
            }
            _maps[key] = new ResolvedMap(new ObjectMap(o.ServiceInstance, key, o.KeyStrategy, o.KeyKind, o.Operations), keys);
        }
    }
}
