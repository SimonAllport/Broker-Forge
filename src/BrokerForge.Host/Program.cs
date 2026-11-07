using System.Text.Json.Serialization;
using Azure.Core;
using Azure.Identity;
using BrokerForge.Abstractions;
using BrokerForge.Adapters.Rest;
using BrokerForge.Auth;
using BrokerForge.Auth.Credentials;
using BrokerForge.Auth.PerUser;
using BrokerForge.Host.Admin;
using BrokerForge.Host.Generation;
using BrokerForge.Host.Config;
using BrokerForge.Host.Connect;
using BrokerForge.Host.Endpoints;
using BrokerForge.Host.Execution;
using BrokerForge.Host.Ingress;
using BrokerForge.Host.Routing;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

// BrokerForge host (HLD 5.2): validates the plug-in's managed identity token, routes the canonical
// request to its descriptor and adapter, and replies with rows, a record, an id or a typed error.
// `set-instance-secret ...` and `generate ...` run one admin command with the host's own configuration and identity, then exit.
args = SetInstanceSecretCommand.Split(args, out var adminCommand);
args = GenerateCommand.Split(args, out var generateCommand);
var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddJsonConsole(o => o.IncludeScopes = true);

builder.Services.Configure<TrustOptions>(builder.Configuration.GetSection("Trust"));
// /v1/execute takes only the plug-in's bearer token (the default scheme). The connect page signs users in with
// Entra through the Connect cookie and the ConnectOidc scheme (HLD D-26); neither opens /v1/execute.
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer()
    .AddCookie(ConnectEndpoints.Scheme)
    .AddOpenIdConnect(ConnectEndpoints.OidcScheme, _ => { });
builder.Services.AddOptions<CookieAuthenticationOptions>(ConnectEndpoints.Scheme)
    .Configure<IConfiguration>((o, configuration) =>
    {
        o.Cookie.Name = "bf_connect";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;
        o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        o.ExpireTimeSpan = TimeSpan.FromHours(1);
        o.SlidingExpiration = false;
        // Without a connect app there is nobody to sign in with: answer 401 rather than a login page that does not exist.
        if (!string.IsNullOrWhiteSpace(configuration["Connect:ClientId"]))
            o.ForwardChallenge = ConnectEndpoints.OidcScheme;
        else
            o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; };
    });
builder.Services.AddOptions<OpenIdConnectOptions>(ConnectEndpoints.OidcScheme)
    .Configure<IOptions<TrustOptions>, IConfiguration>((o, trustOptions, configuration) =>
    {
        var tenant = trustOptions.Value.TenantId;
        o.Authority = $"https://login.microsoftonline.com/{tenant}/v2.0";
        // The handler validates ClientId on every request, configured or not; per-user instances require the real one at startup.
        o.ClientId = configuration["Connect:ClientId"] is { Length: > 0 } id ? id : "connect-app-not-configured";
        o.ResponseType = "code";
        o.UsePkce = true;
        o.Scope.Clear();
        o.Scope.Add("openid");
        o.Scope.Add("profile");
        o.CallbackPath = "/signin-oidc";
        o.SignInScheme = ConnectEndpoints.Scheme;
        o.MapInboundClaims = false;
        o.SaveTokens = false;
        o.TokenValidationParameters.NameClaimType = "name";
        o.Events = new OpenIdConnectEvents
        {
            // The connect app authenticates with the host's managed identity (or the fallback secret), never a stored secret value.
            OnAuthorizationCodeReceived = async ctx =>
            {
                var client = ctx.HttpContext.RequestServices.GetRequiredService<IConnectAppClient>();
                foreach (var (k, v) in await client.ClientAuthAsync(ctx.HttpContext.RequestAborted))
                    ctx.TokenEndpointRequest!.SetParameter(k, v);
            },
            OnTokenValidated = ctx =>
            {
                if (ctx.Principal!.FindFirst("tid")?.Value != tenant)
                    ctx.Fail($"Sign-in tenant is not {tenant}.");
                else if (!Guid.TryParse(ctx.Principal.FindFirst("oid")?.Value, out _))
                    ctx.Fail("Sign-in has no Entra object ID.");
                return Task.CompletedTask;
            },
        };
    });
// Configured from TrustOptions when first used, so the values come from the final configuration.
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<IOptions<TrustOptions>>((options, trustOptions) =>
    {
        var trust = trustOptions.Value;
        options.Authority = $"https://login.microsoftonline.com/{trust.TenantId}/v2.0";
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidAudiences = [trust.Audience],
            // Entra issues v1 or v2 tokens depending on the resource app's accessTokenAcceptedVersion.
            ValidIssuers =
            [
                $"https://sts.windows.net/{trust.TenantId}/",
                $"https://login.microsoftonline.com/{trust.TenantId}/v2.0",
            ],
        };
        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = ctx =>
            {
                if (ctx.Principal!.FindFirst("tid")?.Value != trust.TenantId)
                    ctx.Fail($"Token tenant is not {trust.TenantId}.");
                return Task.CompletedTask;
            },
            OnAuthenticationFailed = ctx =>
            {
                ctx.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("BrokerForge.Ingress")
                    .LogWarning("Token rejected: {Reason}", ctx.Exception.Message);
                return Task.CompletedTask;
            },
        };
    });
builder.Services.AddAuthorization(o => o.AddPolicy(ConnectEndpoints.Policy, p => p.AddAuthenticationSchemes(ConnectEndpoints.Scheme).RequireClaim("oid")));
builder.Services.AddAntiforgery(o => o.Cookie.Name = "bf_af");
// Container Apps ends TLS at its ingress; the connect cookies and redirects need the original https scheme.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedProto;
    o.KnownNetworks.Clear();
    o.KnownProxies.Clear();
});
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull);

// Design instances' requests (sources and token endpoints) go through the design egress rule (HLD D-34): their own
// pool, which connects only to addresses that are not private unless the host is in Config:Designs:AllowedPrivateHosts.
static string[] AllowedPrivateHosts(IServiceProvider sp) =>
    sp.GetRequiredService<IConfiguration>().GetSection("Config:Designs:AllowedPrivateHosts").Get<string[]>() ?? [];
builder.Services.AddHttpClient("sources", c => c.Timeout = Timeout.InfiniteTimeSpan)
    .ConfigurePrimaryHttpMessageHandler(sp => DesignEgress.Handler(
        () => new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5), AllowAutoRedirect = false }, AllowedPrivateHosts(sp)))
    // .NET 8's client logging writes the full request URI, and a query can carry a secret (principle 4).
    .RemoveAllLoggers();
var azureCredential = new DefaultAzureCredential();
builder.Services.AddSingleton<TokenCredential>(azureCredential);
builder.Services.AddSingleton<ISecretResolver>(sp => new SecretResolverRouter(
    new KeyVaultSecretResolver(sp.GetRequiredService<TokenCredential>()),
    new EnvironmentSecretResolver(),
    builder.Configuration.GetValue("Secrets:AllowEnvironment", false),
    sp.GetService<IInstanceSecretStore>()));
builder.Services.AddSingleton<IAuthProvider>(sp => new StaticAuthProvider(sp.GetRequiredService<ISecretResolver>(), sp.GetRequiredService<IHttpClientFactory>().CreateClient("sources")));
builder.Services.AddSingleton<IAuthProvider>(sp => new ServiceAccountAuthProvider(sp.GetRequiredService<TokenCredential>(), sp.GetRequiredService<IHttpClientFactory>().CreateClient("sources")));
// Per-user modes (HLD 9.1, D-25, D-28). Each provider serves only its own mode; none can reach another identity.
builder.Services.AddHttpClient("tokens", c => c.Timeout = TimeSpan.FromSeconds(30))
    .ConfigurePrimaryHttpMessageHandler(sp => DesignEgress.Handler(() => new SocketsHttpHandler { AllowAutoRedirect = false }, AllowedPrivateHosts(sp)))
    .RemoveAllLoggers();
builder.Services.AddSingleton(TimeProvider.System);
// Stored credentials, the connect cookie and OAuth state are protected with these keys (HLD D-25). They must
// outlive the process whenever the durable store is used, or every stored credential becomes unreadable.
var dataProtection = builder.Services.AddDataProtection().SetApplicationName("BrokerForge");
if (builder.Configuration["DataProtection:BlobUri"] is { Length: > 0 } keyBlob && builder.Configuration["DataProtection:KeyId"] is { Length: > 0 } keyId)
    dataProtection.PersistKeysToAzureBlobStorage(new Uri(keyBlob), azureCredential).ProtectKeysWithAzureKeyVault(new Uri(keyId), azureCredential);
if (builder.Configuration["Connect:ClientId"] is { Length: > 0 } connectClientId)
    builder.Services.AddSingleton<IConnectAppClient>(sp => builder.Configuration["Connect:ClientSecretRef"] is { Length: > 0 } connectSecret
        ? new SecretConnectAppClient(connectClientId, SecretRef.Parse(connectSecret), sp.GetRequiredService<ISecretResolver>())
        : new ManagedIdentityConnectAppClient(connectClientId, new ManagedIdentityCredential(builder.Configuration["AZURE_CLIENT_ID"])));
builder.Services.AddSingleton<IUserCredentialStore>(sp => CredentialStores.Create(sp.GetRequiredService<IConfiguration>(), sp.GetRequiredService<HostConfigStore>(),
    sp.GetService<IDesignSource>() != null, sp.GetRequiredService<TokenCredential>(), sp.GetRequiredService<IDataProtectionProvider>()));
builder.Services.AddSingleton<IInstanceSecretStore>(sp => CredentialStores.CreateInstanceSecrets(sp.GetRequiredService<IConfiguration>(),
    sp.GetService<IDesignSource>() != null, sp.GetRequiredService<TokenCredential>(), sp.GetRequiredService<IDataProtectionProvider>()));
builder.Services.AddSingleton(sp => new TokenEndpointClient(sp.GetRequiredService<IHttpClientFactory>().CreateClient("tokens"),
    sp.GetRequiredService<ISecretResolver>(), sp.GetService<IConnectAppClient>(), sp.GetRequiredService<TimeProvider>()));
// An instance's OAuth resource is looked up in its own environment's configuration; the connect base URL is the host's own.
static async Task<OAuthResource> OAuthResourceOf(IHostConfigProvider configs, ServiceInstance instance, CancellationToken ct) =>
    (instance.EnvironmentId == null ? configs.Files : await configs.ForEnvironmentAsync(instance.EnvironmentId, ct)).OAuthResource(instance.Auth.OAuthResource!);
foreach (var mode in new[] { AuthMode.OAuth, AuthMode.Impersonate })
    builder.Services.AddSingleton<IAuthProvider>(sp => new DelegatedTokenAuthProvider(mode, sp.GetRequiredService<IUserCredentialStore>(),
        (instance, ct) => OAuthResourceOf(sp.GetRequiredService<IHostConfigProvider>(), instance, ct), sp.GetRequiredService<TokenEndpointClient>(),
        sp.GetRequiredService<HostConfigStore>().ConnectUrl,
        sp.GetRequiredService<IHttpClientFactory>().CreateClient("sources"), sp.GetRequiredService<ILoggerFactory>().CreateLogger("BrokerForge.Auth"),
        sp.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton<IAuthProvider>(sp => new SsoAuthProvider(sp.GetRequiredService<IUserCredentialStore>(),
    sp.GetRequiredService<HostConfigStore>().ConnectUrl, sp.GetRequiredService<IHttpClientFactory>().CreateClient("sources")));
builder.Services.AddSingleton<IBroker, RestBroker>();
// Connection designs live in each environment's Dataverse and are read with the host's own identity (HLD D-34).
// Off by default: the design source is then null and every environment gets the file configuration. Read when
// resolved, not here, so the setting comes from the final configuration.
builder.Services.AddHttpClient("dataverse", c => c.Timeout = TimeSpan.FromSeconds(30))
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false })
    .RemoveAllLoggers();
builder.Services.AddSingleton<IDataverseReader>(sp => new DataverseReader(sp.GetRequiredService<TokenCredential>(),
    sp.GetRequiredService<IHttpClientFactory>().CreateClient("dataverse")));
builder.Services.AddSingleton<IDesignSource>(sp => sp.GetRequiredService<IConfiguration>().GetValue("Config:Designs:Enabled", false)
    ? new DataverseDesignSource(sp.GetRequiredService<IOptions<TrustOptions>>(), sp.GetRequiredService<IDataverseReader>())
    : null!);
// One store per environment: the files plus that environment's connection designs (none until a design source is
// registered). HostConfigStore stays registered as the file store for what is not per environment.
builder.Services.AddSingleton<IHostConfigProvider>(sp =>
{
    var configuration = sp.GetRequiredService<IConfiguration>();
    var ttl = TimeSpan.FromSeconds(configuration.GetValue("Config:Designs:CacheSeconds", 300));
    if (ttl <= TimeSpan.Zero || ttl > TimeSpan.FromMinutes(5))
        throw new ConfigurationException("Host configuration is invalid:\n - Config:Designs:CacheSeconds must be between 1 and 300: a solution import must reach the host within 5 minutes");
    return new CompositeConfigProvider(HostConfigStore.FromConfiguration(configuration), sp.GetService<IDesignSource>(), sp.GetRequiredService<TimeProvider>(), ttl,
        sp.GetRequiredService<ILoggerFactory>().CreateLogger("BrokerForge.Config"), env => new DesignTrust(sp.GetRequiredService<IOptions<TrustOptions>>().Value.DesignScopesFor(env), sp.GetRequiredService<IOptions<TrustOptions>>().Value.TenantId,
            AllowedPrivateHosts(sp), configuration.GetValue("Config:Designs:AllowLoopbackForTests", false)));
});
builder.Services.AddSingleton(sp => sp.GetRequiredService<IHostConfigProvider>().Files);
// Generation (plan 3, D-36) writes metadata as the host's application user; a table create can take minutes per call.
builder.Services.AddHttpClient("generation", c => c.Timeout = TimeSpan.FromMinutes(10)).RemoveAllLoggers();
builder.Services.AddSingleton<IGenerationClientFactory>(sp => new HostDataverseClientFactory(sp.GetRequiredService<TokenCredential>(), sp.GetRequiredService<IHttpClientFactory>()));
builder.Services.AddSingleton<LoggingGenerationProgress>();
builder.Services.AddSingleton<IGenerationProgress>(sp => new ConnectionRowProgress(sp.GetRequiredService<LoggingGenerationProgress>(), sp.GetRequiredService<IOptions<TrustOptions>>(),
    sp.GetRequiredService<IGenerationClientFactory>(), sp.GetRequiredService<ILogger<ConnectionRowProgress>>()));
// One lease for Generate and the administrator's step, so the two never run at once for a source.
builder.Services.AddSingleton<IGenerationLease>(sp => GenerationLeases.Create(sp.GetRequiredService<IConfiguration>(), sp.GetRequiredService<TokenCredential>(),
    TimeProvider.System, sp.GetService<IDesignSource>() != null));
builder.Services.AddSingleton(sp => new GenerationJobs(sp.GetRequiredService<IOptions<TrustOptions>>(), sp.GetRequiredService<IGenerationClientFactory>(),
    sp.GetRequiredService<IHostConfigProvider>(), sp.GetRequiredService<IGenerationProgress>(), sp.GetRequiredService<ILogger<GenerationJobs>>(), null,
    sp.GetRequiredService<IGenerationLease>()));
builder.Services.AddSingleton(sp => new AdminApi(sp.GetRequiredService<IGenerationClientFactory>(),
    sp.GetRequiredService<GenerationJobs>(), sp.GetRequiredService<IInstanceSecretStore>(), sp.GetRequiredService<IConfiguration>(), sp.GetRequiredService<IHostConfigProvider>()));
// The role page (HLD D-36): the administrator's own Dataverse token, from the connect app, for that request only.
builder.Services.AddSingleton<IDelegatedDataverseFactory>(sp => (HostDataverseClientFactory)sp.GetRequiredService<IGenerationClientFactory>());
builder.Services.AddSingleton<IAdminTokenExchange>(sp => new EntraAdminTokenExchange(sp.GetRequiredService<IOptions<TrustOptions>>().Value.TenantId,
    () => sp.GetService<IConnectAppClient>(),
    sp.GetRequiredService<IHttpClientFactory>().CreateClient("tokens")));
builder.Services.AddSingleton<CallerGuard>();
builder.Services.AddSingleton<RequestRouter>();
builder.Services.AddSingleton(sp => PageTokenKey.LoadAsync(sp.GetRequiredService<IConfiguration>(), sp.GetRequiredService<ISecretResolver>()).GetAwaiter().GetResult());

var app = builder.Build();

// Validate trust and load every descriptor before taking traffic; any problem stops the host here.
var trustProblems = app.Services.GetRequiredService<IOptions<TrustOptions>>().Value
    .Problems(app.Configuration.GetValue("Config:Designs:Enabled", false)).ToList();
if (trustProblems.Count > 0)
    throw new ConfigurationException("Host configuration is invalid:" + string.Concat(trustProblems.Select(p => "\n - " + p)));
// The generate admin command needs trust and Dataverse only, not the serving stores below, so an administrator can run
// it locally as themselves (with --with-role) with just the Trust settings and the repo's descriptor folders.
if (generateCommand != null)
{
    Environment.ExitCode = await GenerateCommand.RunAsync(generateCommand, app.Services.GetRequiredService<GenerationJobs>(), Console.Out, CancellationToken.None);
    return;
}
var store = app.Services.GetRequiredService<HostConfigStore>();
app.Logger.LogInformation("Loaded descriptors: {Descriptors}", string.Join(", ", store.DescriptorNames));
app.Services.GetRequiredService<PageTokenKey>();
app.Services.GetRequiredService<IUserCredentialStore>();
app.Services.GetRequiredService<IInstanceSecretStore>();

if (adminCommand != null)
{
    Environment.ExitCode = await SetInstanceSecretCommand.RunAsync(adminCommand,
        app.Services.GetRequiredService<IOptions<TrustOptions>>().Value.AllowedCallers.Select(c => c.EnvironmentId),
        app.Services.GetRequiredService<IHostConfigProvider>(), app.Services.GetRequiredService<IInstanceSecretStore>(), app.Services.GetRequiredService<ISecretResolver>(), Console.Out, CancellationToken.None);
    return;
}

app.UseForwardedHeaders();
app.UseAuthentication();
app.UseAuthorization();
app.MapGet("/healthz", () => Results.Ok("ok"));
ExecuteEndpoint.Map(app);
ConnectEndpoints.Map(app);
AdminEndpoints.Map(app);
app.Run();

public partial class Program;
