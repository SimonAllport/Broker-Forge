using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using BrokerForge.Contracts;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace BrokerForge.Contract.Tests;

/// <summary>
/// The real host against the mock API. Only the token signing key is swapped for a test RSA key, so the
/// production validation code (issuer, audience, tenant, caller, environment) runs unchanged.
/// </summary>
public sealed class HostFactory : WebApplicationFactory<Program>
{
    public const string Tenant = "65351497-0000-0000-0000-000000000001";
    public const string Audience = "api://bf-host-test";
    public const string PluginApp = "f7912100-0000-0000-0000-000000000001";
    public const string OtherApp = "f7912100-0000-0000-0000-000000000002";
    public const string Environment1 = "a4c0d8aa-0000-0000-0000-000000000001";
    public const string Environment2 = "a4c0d8aa-0000-0000-0000-000000000002";
    public const string PartnerSecret = "bf-test-dummy-secret";

    private static readonly RsaSecurityKey SigningKey = new(RSA.Create(2048)) { KeyId = "bf-test" };
    private readonly string _mockBaseUrl;
    private readonly int _timeoutSeconds;
    private readonly IReadOnlyDictionary<string, string?> _extra;
    private readonly Action<IServiceCollection>? _services;
    private readonly string _descriptors;
    private readonly string _schemas;
    private readonly bool _files;

    public ConcurrentQueue<string> Logs { get; } = new();

    /// <param name="timeoutSeconds">The host's request budget. Generous by default so a cold first call is not a timeout;
    /// the timeout test passes a short one under <see cref="MockApiFixture.SlowMs"/>.</param>
    /// <param name="extra">Settings added after the defaults; a null value removes a default.</param>
    /// <param name="services">Service replacements applied after the host's own registrations.</param>
    /// <param name="files">False for a host as deployed since plan 5: no file instances, object maps or descriptor and schema paths.</param>
    public HostFactory(string mockBaseUrl, int timeoutSeconds = 30, IReadOnlyDictionary<string, string?>? extra = null, Action<IServiceCollection>? services = null,
        bool files = true)
    {
        _files = files;
        _services = services;
        _mockBaseUrl = mockBaseUrl;
        _timeoutSeconds = timeoutSeconds;
        _extra = extra ?? new Dictionary<string, string?>();
        _descriptors = Path.Combine(Path.GetTempPath(), "bf-descriptors-" + Guid.NewGuid().ToString("N"));
        Copy(Path.Combine(MockApiFixture.RepoRoot, "descriptors"), _descriptors);
        Copy(Path.Combine(MockApiFixture.RepoRoot, "tests", "BrokerForge.Contract.Tests", "descriptors", "host"), _descriptors);
        _schemas = Path.Combine(Path.GetTempPath(), "bf-schemas-" + Guid.NewGuid().ToString("N"));
        Copy(Path.Combine(MockApiFixture.RepoRoot, "schemas"), _schemas);
        Copy(Path.Combine(MockApiFixture.RepoRoot, "tests", "BrokerForge.Contract.Tests", "schemas"), _schemas);
        System.Environment.SetEnvironmentVariable("BF_TEST_MOCK_KEY", MockApiFixture.ApiKey);
        System.Environment.SetEnvironmentVariable("BF_TEST_PARTNER_KEY", PartnerSecret);
        System.Environment.SetEnvironmentVariable("BF_TEST_PAGE_TOKEN_KEY", "bf-test-page-token-key-0123456789abcdef");
    }

    private static void Copy(string from, string to)
    {
        foreach (var file in Directory.GetFiles(from, "*.yaml", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Trust:TenantId"] = Tenant,
            ["Trust:Audience"] = Audience,
            ["Trust:AllowedCallers:0:ClientId"] = PluginApp,
            ["Trust:AllowedCallers:0:EnvironmentId"] = Environment1,
            ["Trust:AllowedCallers:1:ClientId"] = OtherApp,
            ["Trust:AllowedCallers:1:EnvironmentId"] = Environment2,
            ["Host:TimeoutSeconds"] = _timeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Secrets:AllowEnvironment"] = "true",
            ["Host:PageTokenKeyRef"] = "env:BF_TEST_PAGE_TOKEN_KEY",
        };
        if (_files)
        {
            settings["Config:DescriptorsPath"] = _descriptors;
            settings["Config:SchemasPath"] = _schemas;
        }
        void Instance(string name, params (string Key, string Value)[] extra)
        {
            settings[$"Instances:{name}:BrokerType"] = "rest";
            settings[$"Instances:{name}:BaseUrl"] = _mockBaseUrl;
            settings[$"Instances:{name}:Auth:Mode"] = "Static";
            settings[$"Instances:{name}:Auth:StaticKind"] = "apiKeyHeader";
            settings[$"Instances:{name}:Auth:HeaderName"] = "X-Api-Key";
            settings[$"Instances:{name}:Auth:SecretRef"] = "env:BF_TEST_MOCK_KEY";
            foreach (var (k, v) in extra)
                settings[$"Instances:{name}:{k}"] = v;
        }
        void Map(string table, string instance, params (string Message, string Operation)[] ops)
        {
            settings[$"ObjectMaps:{table}:ServiceInstance"] = instance;
            settings[$"ObjectMaps:{table}:KeyStrategy"] = "reversible";
            settings[$"ObjectMaps:{table}:KeyKind"] = "String";
            foreach (var (m, o) in ops)
                settings[$"ObjectMaps:{table}:Operations:{m}"] = o;
        }
        if (!_files)
            goto Edits;
        Instance("OrdersAPI", ("Config:accountId", "acc-1"));
        Instance("EchoAPI", ("Config:accountId", "acc-9"), ("Config:region", "eu"), ("Secrets:partnerKey", "env:BF_TEST_PARTNER_KEY"));
        Map("ext_order", "OrdersAPI", ("RetrieveMultiple", "listOrders"), ("Retrieve", "getOrder"), ("Create", "createOrder"), ("Update", "updateOrder"), ("Delete", "deleteOrder"));
        Map("ext_broken", "OrdersAPI", ("RetrieveMultiple", "listBroken"));
        Map("ext_echo", "EchoAPI", ("Create", "echoCreateJson"), ("Update", "echoUpdate"), ("Retrieve", "echoGet"), ("RetrieveMultiple", "echoList"));
        Map("ext_echoxml", "EchoAPI", ("Create", "echoCreateXml"));
        Map("ext_echoform", "EchoAPI", ("Create", "echoCreateForm"));
        Map("ext_echoq", "EchoAPI", ("Retrieve", "echoQuerySecret"));
        Map("ext_echofail", "EchoAPI", ("Retrieve", "echoFail"));

    Edits:
        foreach (var (k, v) in _extra)
            if (v == null) settings.Remove(k); else settings[k] = v;
        builder.ConfigureAppConfiguration(c => c.AddInMemoryCollection(settings));
        builder.ConfigureLogging(l => l.AddProvider(new CaptureLoggerProvider(Logs)));
        builder.ConfigureTestServices(services =>
        {
            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, o =>
            {
                var config = new OpenIdConnectConfiguration { Issuer = Issuer };
                config.SigningKeys.Add(SigningKey);
                o.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(config);
            });
            _services?.Invoke(services);
        });
    }

    public static string Issuer => $"https://login.microsoftonline.com/{Tenant}/v2.0";

    public static string Token(string azp = PluginApp, string aud = Audience, string tid = Tenant, string? issuer = null) =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer ?? Issuer,
            Audience = aud,
            Expires = DateTime.UtcNow.AddMinutes(10),
            Claims = new Dictionary<string, object> { ["tid"] = tid, ["azp"] = azp, ["azpacr"] = "2", ["oid"] = Guid.NewGuid().ToString() },
            SigningCredentials = new SigningCredentials(SigningKey, SecurityAlgorithms.RsaSha256),
        });

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public async Task<(int Status, CanonicalResponse? Response, ErrorPayload? Error)> Execute(CanonicalRequest request, string? token = "default")
    {
        using var client = CreateClient();
        using var message = new HttpRequestMessage(HttpMethod.Post, "/v1/execute") { Content = JsonContent.Create(request, options: Json) };
        if (token != null)
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token == "default" ? Token() : token);
        using var response = await client.SendAsync(message);
        var status = (int)response.StatusCode;
        var text = await response.Content.ReadAsStringAsync();
        if (status == 200)
            return (status, JsonSerializer.Deserialize<CanonicalResponse>(text, Json), null);
        return (status, null, text.Length > 0 ? JsonSerializer.Deserialize<ErrorPayload>(text, Json) : null);
    }

    public static CanonicalRequest Request(string? table, string message, string? instance = "OrdersAPI") => new()
    {
        CorrelationId = Guid.NewGuid().ToString(),
        EnvironmentId = Environment1,
        ServiceInstance = instance,
        Table = table,
        Message = message,
        Caller = new CallerContext { UserObjectId = "user-1", Async = false },
    };

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { Directory.Delete(_descriptors, recursive: true); } catch (IOException) { }
        try { Directory.Delete(_schemas, recursive: true); } catch (IOException) { }
    }

    private sealed class CaptureLoggerProvider(ConcurrentQueue<string> sink) : ILoggerProvider, ISupportExternalScope
    {
        private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();
        public ILogger CreateLogger(string categoryName) => new CaptureLogger(sink, () => _scopes);
        public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;
        public void Dispose() { }
    }

    private sealed class CaptureLogger(ConcurrentQueue<string> sink, Func<IExternalScopeProvider> scopes) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => scopes().Push(state);
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var line = new System.Text.StringBuilder(formatter(state, exception));
            scopes().ForEachScope((scope, sb) => sb.Append(" | scope ").Append(scope is IEnumerable<KeyValuePair<string, object>> kv ? string.Join(",", kv.Select(p => $"{p.Key}={p.Value}")) : scope), line);
            if (exception != null)
                line.Append(" | ").Append(exception);
            sink.Enqueue(line.ToString());
        }
    }
}
