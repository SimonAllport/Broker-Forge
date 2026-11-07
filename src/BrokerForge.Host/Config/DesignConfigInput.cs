using BrokerForge.Abstractions;
using BrokerForge.Connections;

namespace BrokerForge.Host.Config;

/// <summary>
/// One environment's input: the file input plus that environment's connection designs (spec 6 step 5, 7). Each design
/// becomes an instance whose base URL and auth overrides come from its <see cref="EnvironmentSettings"/>, whose
/// secrets are <c>store:&lt;name&gt;</c> references, and whose descriptors and schema are labelled with the design
/// version. A name already used by the files is refused rather than one of the two being picked.
/// </summary>
public static class DesignConfigInput
{
    /// <summary>
    /// The <see cref="DesignAuth"/> string fields an environment's <c>bf_&lt;Instance&gt;_Auth</c> may override, by JSON
    /// name. Not <c>mode</c> or <c>asyncContext</c>: who the source sees the call as is the design's, approved with it,
    /// and an environment variable must not change it (final review I2).
    /// </summary>
    private static readonly Dictionary<string, Action<AuthOptions, string>> Overrides = new(StringComparer.Ordinal)
    {
        ["staticKind"] = (a, v) => a.StaticKind = v,
        ["headerName"] = (a, v) => a.HeaderName = v,
        ["queryParam"] = (a, v) => a.QueryParam = v,
        ["username"] = (a, v) => a.Username = v,
        ["scope"] = (a, v) => a.Scope = v,
        ["ssoKind"] = (a, v) => a.SsoKind = v,
    };

    /// <param name="trust">What the host allows the environment's designs (<see cref="DesignTrust"/>); nothing when null.</param>
    public static ConfigInput Merge(ConfigInput files, string environmentId, IEnumerable<(ConnectionDesign Design, EnvironmentSettings Settings)> designs,
        DesignTrust? trust = null)
    {
        trust ??= DesignTrust.None;
        var input = Copy(files);
        input.EnvironmentId = environmentId;
        var designTables = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (design, settings) in designs)
        {
            var name = design.Instance;
            var clash = false;
            void Clash(string what)
            {
                input.Problems.Add($"{what} is configured both in host settings and as a connection design in environment {environmentId}");
                clash = true;
            }
            if (input.Designs.ContainsKey(name))
            {
                input.Problems.Add($"Instance {name} has two connection designs in environment {environmentId}");
                continue;
            }
            if (files.Instances.ContainsKey(name))
                Clash($"Instance {name}");
            if (design.Auth.OAuth != null && files.OAuthResources.ContainsKey(name))
                Clash($"OAuth resource {name} (of instance {name})");
            if (design.ObjectMap is { } m && files.ObjectMaps.ContainsKey(m.Table))
                Clash($"Table {m.Table} (of instance {name})");
            if (design.ObjectMap is { } t && designTables.TryGetValue(t.Table, out var owner))
            {
                input.Problems.Add($"Table {t.Table} is mapped by both connection designs {owner} and {name} in environment {environmentId}");
                clash = true;
            }
            if (clash)
                continue;
            if (design.ObjectMap is { } own)
                designTables[own.Table] = name;

            var incomplete = new List<string>();
            if (string.IsNullOrWhiteSpace(settings.BaseUrl))
                incomplete.Add($"base URL (environment variable bf_{name}_BaseUrl)");
            input.Designs[name] = new DesignOrigin(environmentId, design.Version, incomplete);

            var auth = new AuthOptions
            {
                Mode = design.Auth.Mode,
                StaticKind = design.Auth.StaticKind,
                HeaderName = design.Auth.HeaderName,
                QueryParam = design.Auth.QueryParam,
                Username = design.Auth.Username,
                Scope = design.Auth.Scope,
                SsoKind = design.Auth.SsoKind,
                AsyncContext = design.Auth.AsyncContext,
                OAuthResource = design.Auth.OAuth != null ? name : null,
            };
            foreach (var (key, value) in settings.AuthOverrides.OrderBy(o => o.Key, StringComparer.Ordinal))
                if (Overrides.TryGetValue(key, out var set))
                    set(auth, value);
                else
                    input.Problems.Add($"bf_{name}_Auth in environment {environmentId} overrides `{key}`, which is not an auth setting of instance {name} (use one of {string.Join(", ", Overrides.Keys)})");
            // Exactly a mode name: Enum.TryParse would also take "0", " ServiceAccount" or "Static, OAuth", and the scope
            // check below must run on the mode the host will use.
            if (!Enum.GetNames<AuthMode>().Contains(auth.Mode, StringComparer.Ordinal))
                input.Problems.Add($"Instance {name} in environment {environmentId}: auth.mode `{auth.Mode}` must be exactly one of {string.Join(", ", Enum.GetNames<AuthMode>())}");
            else
                // After the overrides, so an environment cannot override its way to a scope it may not use.
                input.Problems.AddRange(DesignScopeRules.Problems(name, environmentId, Enum.Parse<AuthMode>(auth.Mode), auth.Scope, design.Auth.OAuth?.Scopes, trust.Scopes));
            if (design.Auth.OAuth is { } endpoints)
                input.Problems.AddRange(EndpointProblems(name, environmentId, endpoints, trust));
            if (!string.IsNullOrWhiteSpace(settings.BaseUrl)
                && UrlProblem($"bf_{name}_BaseUrl in environment {environmentId}", settings.BaseUrl.Trim(), trust, oauth: false) is { } baseProblem)
                input.Problems.Add(baseProblem);
            var secrets = design.Auth.SecretNames.ToDictionary(n => n, n => "store:" + n, StringComparer.Ordinal);
            if (auth.Mode == "Static" && design.Auth.SecretNames.Count > 0)
                auth.SecretRef = "store:" + design.Auth.SecretNames[0];

            input.Instances[name] = new InstanceOptions
            {
                BrokerType = design.BrokerType,
                BaseUrl = settings.BaseUrl?.Trim() ?? "",
                Auth = auth,
                Secrets = secrets,
            };
            if (design.Auth.OAuth is { } o)
                input.OAuthResources[name] = new OAuthResourceOptions
                {
                    // The connect app's credential and the user's code go only to the tenant's Entra endpoints.
                    AuthorizeUrl = o.UsesConnectApp ? EntraEndpoint(trust.TenantId, "authorize") : o.AuthorizeUrl ?? "",
                    TokenUrl = o.UsesConnectApp ? EntraEndpoint(trust.TenantId, "token") : o.TokenUrl ?? "",
                    Scopes = o.Scopes.ToList(),
                    ClientId = o.ClientId,
                    UsesConnectApp = o.UsesConnectApp,
                    // A confidential client's secret is an instance secret like any other: kept in the host's store (HLD D-34).
                    ClientSecretRef = !o.UsesConnectApp && design.Auth.SecretNames.Contains("clientSecret") ? "store:clientSecret" : null,
                };
            if (design.ObjectMap is { } map)
                input.ObjectMaps[map.Table] = new ObjectMapOptions
                {
                    ServiceInstance = name,
                    KeyStrategy = map.KeyStrategy,
                    KeyKind = map.KeyKind,
                    Operations = map.Operations.ToDictionary(p => p.Key, p => p.Value),
                };
            foreach (var (operation, text) in design.Descriptors)
                input.DescriptorTexts[(name, operation + ".yaml")] = text;
            input.SchemaTexts[name] = design.Schema;
        }
        return input;
    }

    private static string EntraEndpoint(string tenantId, string what) =>
        $"https://login.microsoftonline.com/{Uri.EscapeDataString(tenantId)}/oauth2/v2.0/{what}";

    /// <summary>
    /// A design with <c>usesConnectApp</c> names no endpoint: the host posts its connect app's credential, and the user's
    /// code and PKCE verifier, so they must go to Entra. A design with its own client keeps its endpoints, over https
    /// (http only to loopback, for local runs), and is sent only its own <c>store:clientSecret</c>.
    /// </summary>
    private static IEnumerable<string> EndpointProblems(string name, string environmentId, DesignOAuth o, DesignTrust trust)
    {
        var tenantId = trust.TenantId;
        if (o.UsesConnectApp)
        {
            foreach (var (key, value) in new[] { ("authorizeUrl", o.AuthorizeUrl), ("tokenUrl", o.TokenUrl) })
                if (value != null)
                    yield return $"Instance {name} in environment {environmentId} uses the connect app (usesConnectApp), so auth.oauth.{key} must be absent: the host uses its tenant's Entra endpoints";
            if (!Guid.TryParse(tenantId, out _))
                yield return $"Instance {name} in environment {environmentId} uses the connect app, which needs Trust:TenantId as a GUID for the Entra endpoints";
            yield break;
        }
        foreach (var (key, value) in new[] { ("authorizeUrl", o.AuthorizeUrl), ("tokenUrl", o.TokenUrl) })
            if (UrlProblem($"Instance {name} in environment {environmentId}: auth.oauth.{key}", value, trust, oauth: true) is { } problem)
                yield return problem;
    }

    /// <summary>
    /// The design egress rule at merge time (HLD D-34; at connect time it is <see cref="BrokerForge.Auth.DesignEgress"/>): an
    /// absolute https URL without user info, whose host is not a literal loopback, link-local or private address. A
    /// host in Config:Designs:AllowedPrivateHosts may be private, and a base URL to it may use http; an OAuth URL may use
    /// http (and loopback) only to loopback with Config:Designs:AllowLoopbackForTests.
    /// </summary>
    private static string? UrlProblem(string what, string? value, DesignTrust trust, bool oauth)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.UserInfo.Length > 0 || uri.DnsSafeHost.Length == 0)
            return $"{what} must be an absolute https URL";
        var host = uri.DnsSafeHost;
        var listed = (trust.AllowedPrivateHosts ?? Array.Empty<string>()).Contains(host, StringComparer.OrdinalIgnoreCase);
        var testLoopback = oauth && trust.AllowLoopbackForTests && uri.IsLoopback;
        var httpAllowed = oauth ? testLoopback : listed;
        if (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && httpAllowed))
            return $"{what} must be an absolute https URL (http only for a host listed in Config:Designs:AllowedPrivateHosts{(oauth ? ", and for an OAuth URL only to loopback in tests" : "")})";
        if (System.Net.IPAddress.TryParse(host, out var address) && BrokerForge.Auth.DesignEgress.IsPrivate(address) && !listed && !testLoopback)
            return $"{what} points at private address {host}; list it in Config:Designs:AllowedPrivateHosts to allow";
        return null;
    }

    /// <summary>The file input is shared by every environment, so each one builds on its own copy.</summary>
    private static ConfigInput Copy(ConfigInput files)
    {
        var input = new ConfigInput { ConnectBaseUrl = files.ConnectBaseUrl, ConnectClientId = files.ConnectClientId };
        foreach (var (k, v) in files.Instances) input.Instances[k] = v;
        foreach (var (k, v) in files.OAuthResources) input.OAuthResources[k] = v;
        foreach (var (k, v) in files.ObjectMaps) input.ObjectMaps[k] = v;
        foreach (var (k, v) in files.DescriptorTexts) input.DescriptorTexts[k] = v;
        foreach (var (k, v) in files.SchemaTexts) input.SchemaTexts[k] = v;
        input.Problems.AddRange(files.Problems);
        input.DescriptorProblems.AddRange(files.DescriptorProblems);
        return input;
    }
}
