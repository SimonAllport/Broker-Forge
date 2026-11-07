using System.Text.RegularExpressions;
using BrokerForge.Abstractions;
using BrokerForge.Auth;
using BrokerForge.Auth.Credentials;
using BrokerForge.Auth.PerUser;
using BrokerForge.Host.Config;
using BrokerForge.Host.Ingress;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace BrokerForge.Host.Connect;

/// <summary>
/// The connect page (HLD 9.1, D-26). The user signs in with Entra (the Connect cookie scheme), and what they
/// grant is stored under their Entra object ID, the ID the plug-in sends as caller.userObjectId:
/// OAuth and Impersonate through the authorisation-code flow with PKCE, SSO through a form. Every POST checks
/// antiforgery. No credential is logged, echoed or put in a page. A host-settings instance's page is
/// <c>/connect/&lt;instance&gt;</c>; a connection design's is <c>/connect/&lt;environmentId&gt;/&lt;instance&gt;</c>, read from that
/// environment's configuration and stored under its <see cref="CredentialPartition"/>.
/// </summary>
public static partial class ConnectEndpoints
{
    public const string Scheme = "Connect";
    public const string OidcScheme = "ConnectOidc";
    public const string Policy = "Connect";
    private const int MaxSecret = 1024;
    private const int MaxUsername = 256;

    [GeneratedRegex("^[a-z_]{1,64}$")]
    private static partial Regex OAuthErrorCode();

    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/connect").RequireAuthorization(Policy);
        group.MapGet("/callback", Callback);
        foreach (var prefix in new[] { "", "/{environmentId:guid}" })
        {
            group.MapGet(prefix + "/{instance}", Status);
            group.MapPost(prefix + "/{instance}/start", Start);
            group.MapPost(prefix + "/{instance}/sso", SaveSso);
            group.MapPost(prefix + "/{instance}/disconnect", Disconnect);
        }
    }

    private static string? UserObjectId(HttpContext http) =>
        Guid.TryParse(http.User.FindFirst("oid")?.Value, out var oid) ? oid.ToString("D") : null;

    private static ServiceInstance? PerUser(HostConfigStore config, string name) =>
        config.Instance(name) is { Auth.Mode: AuthMode.OAuth or AuthMode.Impersonate or AuthMode.Sso } instance ? instance : null;

    /// <summary>
    /// The per-user instance a page is for, and the configuration it comes from: the files without an environment,
    /// else that environment's designs only (a host-settings instance never answers under an environment). Only an
    /// environment in Trust:AllowedCallers is loaded, so a typed GUID cannot make the host read or cache anything.
    /// </summary>
    private static async Task<(ServiceInstance? Instance, HostConfigStore Config)> Find(HttpContext http, IHostConfigProvider configs, string? environmentId, string name, CancellationToken ct)
    {
        if (environmentId == null)
            return (PerUser(configs.Files, name), configs.Files);
        var trust = http.RequestServices.GetRequiredService<IOptions<TrustOptions>>().Value;
        if (!trust.AllowedCallers.Any(c => Guid.TryParse(c.EnvironmentId, out var allowed) && allowed == Guid.Parse(environmentId)))
            return (null, configs.Files);
        var config = await configs.ForEnvironmentAsync(environmentId, ct);
        return (PerUser(config, name) is { EnvironmentId: not null } si ? si : null, config);
    }

    private static string? EnvironmentOf(HttpContext http) =>
        http.Request.RouteValues.TryGetValue("environmentId", out var env) && Guid.TryParse(env?.ToString(), out var id) ? id.ToString("D") : null;

    private static CredentialKind KindFor(AuthMode mode) => mode switch
    {
        AuthMode.OAuth => CredentialKind.OAuth,
        AuthMode.Impersonate => CredentialKind.Impersonate,
        _ => CredentialKind.Sso,
    };

    /// <summary>404, unless designs of the environment were left out: one of them may be the instance, so say why (final review I1).</summary>
    private static IResult NotFound(HostConfigStore config, string name) =>
        config.NotLoaded($"Service instance {name}") is { } notLoaded
            ? ConnectPages.Message(500, "Not available", notLoaded.Message)
            : ConnectPages.Message(404, "Not found", $"There is no per-user service instance called {name}.");

    private static string PagePath(ServiceInstance si) => HostConfigStore.ConnectPath(si);

    private static IResult NoIdentity() => ConnectPages.Message(403, "Not signed in", "Your sign-in did not include an Entra object ID, so nothing can be stored for you.");

    private static async Task<bool> AntiforgeryValid(HttpContext http, IAntiforgery antiforgery)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(http);
            return true;
        }
        catch (AntiforgeryValidationException)
        {
            return false;
        }
    }

    private static IResult Refused(string message) => ConnectPages.Message(400, "Connection not saved", message);

    private static async Task<IResult> Status(string instance, HttpContext http, IHostConfigProvider configs, IUserCredentialStore store, IAntiforgery antiforgery, CancellationToken ct)
    {
        var (found, config) = await Find(http, configs, EnvironmentOf(http), instance, ct);
        if (found is not { } si)
            return NotFound(config, instance);
        if (UserObjectId(http) is not { } user)
            return NoIdentity();
        UserCredential? credential = null;
        var unreadable = false;
        try
        {
            credential = await store.GetAsync(CredentialPartition.For(si), user, ct);
        }
        catch (CredentialUnreadableException)
        {
            unreadable = true;
        }
        var token = antiforgery.GetAndStoreTokens(http).RequestToken!;
        var status = credential switch
        {
            null when unreadable => "the stored connection could not be read; connect again",
            null => "not connected",
            { Kind: var k } when k != KindFor(si.Auth.Mode) => "not connected (an earlier connection was for another mode)",
            { Status: CredentialStatus.Connected } => "connected",
            { Status: var s } => s.ToString().ToLowerInvariant() + "; connect again",
        };
        var body = ConnectPages.Paragraph($"Service: {si.Name}. Status: {status}.");
        body += si.Auth.Mode == AuthMode.Sso
            ? ConnectPages.Form($"{PagePath(si)}/sso", token, ConnectPages.SsoFields(si.Auth.SsoKind == "basic"), "Save")
            : ConnectPages.Form($"{PagePath(si)}/start", token, "", credential == null && !unreadable ? "Connect" : "Connect again");
        if (credential != null || unreadable)
            body += ConnectPages.Form($"{PagePath(si)}/disconnect", token, "", "Disconnect");
        return ConnectPages.Page(200, $"Connect to {si.Name}", body);
    }

    private static async Task<IResult> Start(string instance, HttpContext http, IHostConfigProvider configs, IAntiforgery antiforgery,
        IDataProtectionProvider protection, TimeProvider time, IServiceProvider services, CancellationToken ct)
    {
        var (found, config) = await Find(http, configs, EnvironmentOf(http), instance, ct);
        if (found is not { } si)
            return NotFound(config, instance);
        if (UserObjectId(http) is not { } user)
            return NoIdentity();
        if (!await AntiforgeryValid(http, antiforgery))
            return Refused("The form has expired. Open the connect page again.");
        if (si.Auth.Mode is not (AuthMode.OAuth or AuthMode.Impersonate))
            return Refused($"{si.Name} uses {si.Auth.Mode}; there is no sign-in to start.");

        var resource = config.OAuthResource(si.Auth.OAuthResource!);
        var clientId = resource.UsesConnectApp
            ? (services.GetService<IConnectAppClient>() ?? throw new ConfigurationException("Connect:ClientId is not set.")).ClientId
            : resource.ClientId!;
        var state = new ConnectState(si.Name, user, ConnectState.Random(32), ConnectState.Random(16), time.GetUtcNow(), si.EnvironmentId);
        http.Response.Cookies.Append(state.NonceCookieName, state.Nonce, new CookieOptions
        {
            HttpOnly = true, Secure = http.Request.IsHttps, SameSite = SameSiteMode.Lax, Path = "/connect/callback", MaxAge = ConnectState.Lifetime,
        });
        var query = QueryString.Create(new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = clientId,
            ["redirect_uri"] = config.ConnectCallbackUrl,
            ["scope"] = string.Join(' ', resource.Scopes),
            ["state"] = state.Protect(protection),
            ["code_challenge"] = ConnectState.Challenge(state.Verifier),
            ["code_challenge_method"] = "S256",
        });
        var separator = resource.AuthorizeUrl.Contains('?') ? "&" : "?";
        return Results.Redirect(resource.AuthorizeUrl + separator + query.Value![1..]);
    }

    private static async Task<IResult> Callback(HttpContext http, IHostConfigProvider configs, IUserCredentialStore store, IDataProtectionProvider protection,
        TokenEndpointClient tokens, TimeProvider time, ILoggerFactory loggers, CancellationToken ct)
    {
        var log = loggers.CreateLogger("BrokerForge.Connect");
        var q = http.Request.Query;
        var state = ConnectState.Unprotect(protection, q["state"]);
        if (state == null)
            return Refused("This sign-in link is not valid. Start again from the connect page.");
        var nonce = http.Request.Cookies[state.NonceCookieName];
        http.Response.Cookies.Delete(state.NonceCookieName, new CookieOptions { Path = "/connect/callback", Secure = http.Request.IsHttps, SameSite = SameSiteMode.Lax });
        var age = time.GetUtcNow() - state.IssuedAt;
        if (age < TimeSpan.Zero || age > ConnectState.Lifetime)
            return Refused("This sign-in took longer than 10 minutes. Start again from the connect page.");
        if (nonce == null || nonce != state.Nonce)
            return Refused("This sign-in was not started in this browser, or has already been used. Start again from the connect page.");
        if (UserObjectId(http) != state.UserObjectId)
        {
            log.LogWarning("Connect callback for {Instance} in environment {EnvironmentId} refused: started by another user", state.Instance, state.EnvironmentId);
            return Refused("This sign-in was started by another user. Start again from the connect page.");
        }
        var (found, config) = await Find(http, configs, state.EnvironmentId, state.Instance, ct);
        if (found is not { Auth.Mode: AuthMode.OAuth or AuthMode.Impersonate } si)
            return NotFound(config, state.Instance);
        if (q["error"].ToString() is { Length: > 0 } error)
            return Refused($"The sign-in at the source did not complete ({(OAuthErrorCode().IsMatch(error) ? error : "unknown error")}).");
        if (q["code"].ToString() is not { Length: > 0 } code)
            return Refused("The source did not return a sign-in code.");

        TokenSet set;
        try
        {
            set = await tokens.RedeemCodeAsync(config.OAuthResource(si.Auth.OAuthResource!), si, code, state.Verifier, config.ConnectCallbackUrl, ct);
        }
        catch (RefreshRejectedException)
        {
            return Refused("The source refused the sign-in code. Start again from the connect page.");
        }
        catch (Exception ex) when (ex is ConfigurationException or CredentialUnreadableException)
        {
            // A configuration problem (the client secret not set, a token URL the egress rule refuses) or an unreadable secret:
            // an administrator's job. The page is for end users and a configuration message can name hosts, so the page gives
            // a reference and the log line carries the message with it.
            log.LogWarning("Connect for {Instance} in environment {EnvironmentId} user {UserObjectId} failed (reference {Reference}): {Reason}", si.Name, si.EnvironmentId, state.UserObjectId, http.TraceIdentifier, ex.Message);
            return ConnectPages.Message(ex is ConfigurationException ? 500 : 503, "Connection not saved", ex is ConfigurationException
                ? $"{si.Name} cannot be connected yet because of a configuration problem. Ask an administrator, quoting reference {http.TraceIdentifier}."
                : $"{si.Name} cannot be connected yet: its stored key `clientSecret` is unreadable. Ask an administrator to set it on the BrokerForge secret page.");
        }
        catch (CredentialUnavailableException ex)
        {
            log.LogWarning("Connect for {Instance} in environment {EnvironmentId} user {UserObjectId} failed: {Reason}", si.Name, si.EnvironmentId, state.UserObjectId, ex.Message);
            return ConnectPages.Message(502, "Connection not saved", "The source's sign-in service did not answer properly. Try again later.");
        }
        await store.SaveAsync(CredentialPartition.For(si), state.UserObjectId,
            new UserCredential(KindFor(si.Auth.Mode), CredentialStatus.Connected, set.AccessToken, set.RefreshToken, set.ExpiresAt, null, null, time.GetUtcNow()), ct);
        log.LogInformation("Connected {Mode} for {Instance} in environment {EnvironmentId} user {UserObjectId}", si.Auth.Mode, si.Name, si.EnvironmentId, state.UserObjectId);
        return SeeOther(PagePath(si));
    }

    private static async Task<IResult> SaveSso(string instance, HttpContext http, IHostConfigProvider configs, IUserCredentialStore store, IAntiforgery antiforgery,
        TimeProvider time, ILoggerFactory loggers, CancellationToken ct)
    {
        var (found, config) = await Find(http, configs, EnvironmentOf(http), instance, ct);
        if (found is not { } si)
            return NotFound(config, instance);
        if (UserObjectId(http) is not { } user)
            return NoIdentity();
        if (!await AntiforgeryValid(http, antiforgery))
            return Refused("The form has expired. Open the connect page again.");
        if (si.Auth.Mode != AuthMode.Sso)
            return Refused($"{si.Name} uses {si.Auth.Mode}, not a username and password.");
        var form = await http.Request.ReadFormAsync(ct);
        var basic = si.Auth.SsoKind == "basic";
        var username = basic ? form["username"].ToString() : null;
        var secret = form["secret"].ToString();
        if (secret.Length is 0 or > MaxSecret || secret.IndexOfAny(new[] { '\r', '\n' }) >= 0)
            return Refused($"Enter a password or key of 1 to {MaxSecret} characters on one line.");
        if (basic && (username!.Length is 0 or > MaxUsername || username.IndexOfAny(new[] { '\r', '\n', ':' }) >= 0))
            return Refused($"Enter a username of 1 to {MaxUsername} characters without a colon.");
        await store.SaveAsync(CredentialPartition.For(si), user, new UserCredential(CredentialKind.Sso, CredentialStatus.Connected, null, null, null, username, secret, time.GetUtcNow()), ct);
        loggers.CreateLogger("BrokerForge.Connect").LogInformation("Connected Sso for {Instance} in environment {EnvironmentId} user {UserObjectId}", si.Name, si.EnvironmentId, user);
        return SeeOther(PagePath(si));
    }

    private static async Task<IResult> Disconnect(string instance, HttpContext http, IHostConfigProvider configs, IUserCredentialStore store, IAntiforgery antiforgery,
        ILoggerFactory loggers, CancellationToken ct)
    {
        var (found, config) = await Find(http, configs, EnvironmentOf(http), instance, ct);
        if (found is not { } si)
            return NotFound(config, instance);
        if (UserObjectId(http) is not { } user)
            return NoIdentity();
        if (!await AntiforgeryValid(http, antiforgery))
            return Refused("The form has expired. Open the connect page again.");
        await store.DeleteAsync(CredentialPartition.For(si), user, ct);
        loggers.CreateLogger("BrokerForge.Connect").LogInformation("Disconnected {Instance} in environment {EnvironmentId} user {UserObjectId}", si.Name, si.EnvironmentId, user);
        return SeeOther(PagePath(si));
    }

    /// <summary>303, so the browser follows a POST with a GET of the status page.</summary>
    private static IResult SeeOther(string location) => new SeeOtherResult(location);

    private sealed class SeeOtherResult(string location) : IResult
    {
        public Task ExecuteAsync(HttpContext http)
        {
            http.Response.StatusCode = StatusCodes.Status303SeeOther;
            http.Response.Headers.Location = location;
            return Task.CompletedTask;
        }
    }
}
