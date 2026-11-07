using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using BrokerForge.Abstractions;
using BrokerForge.Auth.Credentials;
using BrokerForge.Auth.PerUser;
using BrokerForge.Contracts;
using Microsoft.Extensions.Logging;

namespace BrokerForge.Auth.Tests;

/// <summary>OAuth, Impersonate and SSO providers (HLD 7.3, 9.1, D-28): no credential means ConnectionRequiredException, never another identity.</summary>
public class PerUserProviderTests
{
    private const string Alice = "a11ce000-0000-0000-0000-000000000001";
    private const string ConnectUrl = "https://bf-host.example/connect/Tickets";
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-01T19:00:00Z");

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>
    /// A token endpoint: each refresh token works once and is rotated; records every form posted. Like a real
    /// server it decides the answer when the request arrives, then waits the next of <see cref="Delays"/> (50 ms
    /// when empty) before answering, so a test can make the refused call answer before the one that won.
    /// </summary>
    private sealed class TokenEndpoint : HttpMessageHandler
    {
        public ConcurrentQueue<Dictionary<string, string>> Forms { get; } = new();
        public HashSet<string> ValidRefreshTokens { get; } = new() { "rt-1" };
        public Func<HttpResponseMessage>? Override { get; set; }
        public ConcurrentQueue<int> Delays { get; } = new();
        public TaskCompletionSource Arrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _issued;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var form = (await request.Content!.ReadAsStringAsync(ct)).Split('&')
                .Select(p => p.Split('=', 2)).ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1].Replace('+', ' ')));
            Forms.Enqueue(form);
            var response = Answer(form);
            Arrived.TrySetResult();
            await Task.Delay(Delays.TryDequeue(out var delay) ? delay : 50, ct);
            return response;
        }

        private HttpResponseMessage Answer(Dictionary<string, string> form)
        {
            if (Override != null)
                return Override();
            lock (ValidRefreshTokens)
            {
                if (form["grant_type"] == "refresh_token" && !ValidRefreshTokens.Remove(form["refresh_token"]))
                    return Json(HttpStatusCode.BadRequest, new { error = "invalid_grant", error_description = "refresh token " + form["refresh_token"] + " is not valid" });
                var n = Interlocked.Increment(ref _issued);
                ValidRefreshTokens.Add("rt-" + (n + 1));
                return Json(HttpStatusCode.OK, new { access_token = "at-" + (n + 1), refresh_token = "rt-" + (n + 1), expires_in = 3600, token_type = "Bearer" });
            }
        }

        public static HttpResponseMessage Json(HttpStatusCode status, object body) =>
            new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
    }

    private sealed class Capture : HttpMessageHandler
    {
        public HttpRequestMessage? Last { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Last = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private sealed class FixedSecrets : ISecretResolver
    {
        public Task<string> GetAsync(SecretRef reference, CancellationToken ct) => Task.FromResult("tk-client-secret-dummy");
    }

    private sealed class FakeConnectApp : IConnectAppClient
    {
        public string ClientId => "connect-app-id";
        public Task<IReadOnlyList<KeyValuePair<string, string>>> ClientAuthAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<KeyValuePair<string, string>>>(new[]
            {
                new KeyValuePair<string, string>("client_assertion_type", "urn:ietf:params:oauth:client-assertion-type:jwt-bearer"),
                new KeyValuePair<string, string>("client_assertion", "mi-assertion"),
            });
    }

    private sealed class LogSink : ILogger
    {
        public ConcurrentQueue<string> Lines { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Enqueue(formatter(state, exception) + (exception == null ? "" : " | " + exception));
    }

    private static readonly OAuthResource Tickets = new("Tickets", "https://tk.example/tk/oauth/authorize", "https://tk.example/tk/oauth/token",
        new[] { "tickets.read" }, "bf-tickets", "env:BF_TEST_TK_CLIENT_SECRET", false);
    private static readonly OAuthResource Entra = new("Entra", "https://login.microsoftonline.com/00000000-0000-0000-0000-000000000001/oauth2/v2.0/authorize",
        "https://login.microsoftonline.com/00000000-0000-0000-0000-000000000001/oauth2/v2.0/token",
        new[] { "api://tickets/user_impersonation", "offline_access" }, null, null, true);

    private readonly InMemoryUserCredentialStore _store = new();
    private readonly TokenEndpoint _tokens = new();
    private readonly Capture _source = new();
    private readonly LogSink _log = new();

    private static ServiceInstance Instance(AuthConfig auth) =>
        new("Tickets", "rest", "https://tk.example", auth, new Dictionary<string, string>(), new Dictionary<string, string>());

    private static CallerContext Caller(string? user = Alice) => new() { UserObjectId = user };

    private TokenEndpointClient Client() => new(new HttpClient(_tokens), new FixedSecrets(), new FakeConnectApp(), new FixedTime(Now));

    private DelegatedTokenAuthProvider Delegated(AuthMode mode = AuthMode.OAuth) =>
        new(mode, _store, (i, _) => Task.FromResult(i.Auth.OAuthResource == "Entra" ? Entra : Tickets), Client(), _ => ConnectUrl, new HttpClient(_source), _log, new FixedTime(Now));

    private static UserCredential Tokens(DateTimeOffset expires, CredentialKind kind = CredentialKind.OAuth, CredentialStatus status = CredentialStatus.Connected) =>
        new(kind, status, "at-1", "rt-1", expires, null, null, Now.AddHours(-2));

    private async Task<HttpRequestMessage> Send(IAuthProvider provider, ServiceInstance instance, CallerContext? caller = null)
    {
        var channel = await provider.AuthenticateAsync(instance, caller ?? Caller(), "corr-1", CancellationToken.None);
        await channel.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://tk.example/tk/v1/tickets"), CancellationToken.None);
        return _source.Last!;
    }

    private static readonly ServiceInstance OAuthInstance = Instance(new AuthConfig(AuthMode.OAuth, OAuthResource: "Tickets"));

    [Fact]
    public async Task OAuth_NoCredential_ConnectionRequiredWithUrl()
    {
        var ex = await Assert.ThrowsAsync<ConnectionRequiredException>(() => Delegated().AuthenticateAsync(OAuthInstance, Caller(), "corr-1", default));
        Assert.Equal(ConnectUrl, ex.ConnectUrl);
        Assert.Equal("corr-1", ex.CorrelationId);
        Assert.Contains("Tickets", ex.Message);
        Assert.Contains("no credential", ex.Message);
        Assert.Empty(_tokens.Forms);
    }

    [Fact]
    public async Task OAuth_TokenStoredBeforeDesigns_ConnectionRequiredForTheDesignInstance()
    {
        // A token saved while the instance came from host settings (bare name) is not the design instance's: after the move
        // to a connection design the user connects again, and is never served with the old row or another identity.
        await _store.SaveAsync("Tickets", Alice, Tokens(Now.AddMinutes(30)), default);
        var design = OAuthInstance with { EnvironmentId = "00000000-0000-0000-0000-0000000000e1" };
        var ex = await Assert.ThrowsAsync<ConnectionRequiredException>(() => Delegated().AuthenticateAsync(design, Caller(), "corr-1", default));
        Assert.Equal(ConnectUrl, ex.ConnectUrl);
        Assert.Empty(_tokens.Forms);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-guid")]
    public async Task OAuth_NoUserObjectId_ConnectionRequired(string? user)
    {
        var ex = await Assert.ThrowsAsync<ConnectionRequiredException>(() => Delegated().AuthenticateAsync(OAuthInstance, Caller(user), "corr-1", default));
        Assert.Contains("Entra object ID", ex.Message);
    }

    [Fact]
    public async Task OAuth_ValidToken_AttachedAsBearer()
    {
        await _store.SaveAsync("Tickets", Alice, Tokens(Now.AddMinutes(30)), default);
        var sent = await Send(Delegated(), OAuthInstance);
        Assert.Equal("Bearer at-1", sent.Headers.Authorization!.ToString());
        Assert.Empty(_tokens.Forms);
    }

    [Fact]
    public async Task OAuth_ExpiringToken_RefreshedAndSaved()
    {
        await _store.SaveAsync("Tickets", Alice, Tokens(Now.AddSeconds(30)), default);
        var sent = await Send(Delegated(), OAuthInstance);
        Assert.Equal("Bearer at-2", sent.Headers.Authorization!.ToString());
        var form = Assert.Single(_tokens.Forms);
        Assert.Equal("refresh_token", form["grant_type"]);
        Assert.Equal("rt-1", form["refresh_token"]);
        Assert.Equal("bf-tickets", form["client_id"]);
        Assert.Equal("tk-client-secret-dummy", form["client_secret"]);
        Assert.Equal("tickets.read", form["scope"]);
        var saved = (await _store.GetAsync("Tickets", Alice, default))!;
        Assert.Equal(("at-2", "rt-2", Now.AddSeconds(3600)), (saved.AccessToken, saved.RefreshToken, saved.ExpiresAt));
    }

    [Fact]
    public async Task OAuth_ConcurrentExpiredCalls_RefreshOnce()
    {
        await _store.SaveAsync("Tickets", Alice, Tokens(Now.AddSeconds(-5)), default);
        var provider = Delegated();
        var channels = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => provider.AuthenticateAsync(OAuthInstance, Caller(), "corr-1", default)));
        Assert.Single(_tokens.Forms);
        Assert.All(channels, c => Assert.Contains("at-2", c.CredentialValues));
        Assert.Equal("rt-2", (await _store.GetAsync("Tickets", Alice, default))!.RefreshToken);
    }

    // Two replicas (two providers, so two refresh gates) share the store. The source consumes rt-1 for the
    // first request to arrive and refuses the second with invalid_grant.
    private async Task<(IAuthenticatedChannel? First, Exception? Second)[]> TwoReplicasRefresh(int firstDelay, int secondDelay)
    {
        await _store.SaveAsync("Tickets", Alice, Tokens(Now.AddSeconds(-5)), default);
        _tokens.Delays.Enqueue(firstDelay);
        _tokens.Delays.Enqueue(secondDelay);
        var calls = new[] { Delegated(), Delegated() }.Select(async p =>
        {
            try { return ((IAuthenticatedChannel?)await p.AuthenticateAsync(OAuthInstance, Caller(), "corr-1", default), (Exception?)null); }
            catch (Exception ex) { return (null, ex); }
        });
        return await Task.WhenAll(calls);
    }

    [Fact]
    public async Task OAuth_TwoReplicas_WinnerSavesFirst_BothUseNewToken()
    {
        var results = await TwoReplicasRefresh(firstDelay: 20, secondDelay: 300);
        Assert.Equal(2, _tokens.Forms.Count);
        Assert.All(results, r => Assert.Null(r.Second));
        Assert.All(results, r => Assert.Contains("at-2", r.First!.CredentialValues));
        var saved = (await _store.GetAsync("Tickets", Alice, default))!;
        Assert.Equal((CredentialStatus.Connected, "at-2", "rt-2"), (saved.Status, saved.AccessToken, saved.RefreshToken));
    }

    [Fact]
    public async Task OAuth_TwoReplicas_RefusalAnswersFirst_StoreKeepsWinnersToken()
    {
        var results = await TwoReplicasRefresh(firstDelay: 300, secondDelay: 20);
        Assert.All(results, r => Assert.Null(r.Second));
        Assert.All(results, r => Assert.Contains("at-2", r.First!.CredentialValues));
        var saved = (await _store.GetAsync("Tickets", Alice, default))!;
        Assert.Equal((CredentialStatus.Connected, "at-2", "rt-2"), (saved.Status, saved.AccessToken, saved.RefreshToken));
        var next = await Send(Delegated(), OAuthInstance);
        Assert.Equal("Bearer at-2", next.Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task OAuth_DisconnectDuringRefresh_RowNotRecreated()
    {
        await _store.SaveAsync("Tickets", Alice, Tokens(Now.AddSeconds(-5)), default);
        _tokens.Delays.Enqueue(200);
        var call = Delegated().AuthenticateAsync(OAuthInstance, Caller(), "corr-1", default);
        await _tokens.Arrived.Task;
        await _store.DeleteAsync("Tickets", Alice, default);
        var ex = await Assert.ThrowsAsync<ConnectionRequiredException>(() => call);
        Assert.Contains("disconnected", ex.Message);
        Assert.Null(await _store.GetAsync("Tickets", Alice, default));
    }

    [Fact]
    public async Task OAuth_ReconnectDuringRefusedRefresh_NewConnectionKept()
    {
        _tokens.ValidRefreshTokens.Clear();
        await _store.SaveAsync("Tickets", Alice, Tokens(Now.AddSeconds(-5)), default);
        _tokens.Delays.Enqueue(200);
        var call = Delegated().AuthenticateAsync(OAuthInstance, Caller(), "corr-1", default);
        await _tokens.Arrived.Task;
        await _store.SaveAsync("Tickets", Alice, Tokens(Now.AddMinutes(30)) with { AccessToken = "at-new", RefreshToken = "rt-new" }, default);
        var channel = await call;
        Assert.Contains("at-new", channel.CredentialValues);
        var saved = (await _store.GetAsync("Tickets", Alice, default))!;
        Assert.Equal((CredentialStatus.Connected, "at-new"), (saved.Status, saved.AccessToken));
    }

    [Fact]
    public async Task OAuth_ReconnectDuringSuccessfulRefresh_NewConnectionKept()
    {
        await _store.SaveAsync("Tickets", Alice, Tokens(Now.AddSeconds(-5)), default);
        _tokens.Delays.Enqueue(200);
        var call = Delegated().AuthenticateAsync(OAuthInstance, Caller(), "corr-1", default);
        await _tokens.Arrived.Task;
        await _store.SaveAsync("Tickets", Alice, Tokens(Now.AddMinutes(30)) with { AccessToken = "at-new", RefreshToken = "rt-new" }, default);
        var channel = await call;
        Assert.Contains("at-new", channel.CredentialValues);
        Assert.Equal("at-new", (await _store.GetAsync("Tickets", Alice, default))!.AccessToken);
    }

    [Fact]
    public async Task OAuth_RefreshRejected_MarksExpiredAndRequiresConnect()
    {
        _tokens.ValidRefreshTokens.Clear();
        await _store.SaveAsync("Tickets", Alice, Tokens(Now.AddSeconds(-5)), default);
        var ex = await Assert.ThrowsAsync<ConnectionRequiredException>(() => Delegated().AuthenticateAsync(OAuthInstance, Caller(), "corr-1", default));
        Assert.Equal(ConnectUrl, ex.ConnectUrl);
        Assert.Contains("expired", ex.Message);
        Assert.Equal(CredentialStatus.Expired, (await _store.GetAsync("Tickets", Alice, default))!.Status);
        Assert.Single(_tokens.Forms);
        await Assert.ThrowsAsync<ConnectionRequiredException>(() => Delegated().AuthenticateAsync(OAuthInstance, Caller(), "corr-2", default));
        Assert.Single(_tokens.Forms);
        Assert.DoesNotContain(_log.Lines, l => l.Contains("rt-1"));
    }

    [Fact]
    public async Task OAuth_TokenEndpointFailure_NoCredentialInMessageOrLogs()
    {
        _tokens.Override = () => TokenEndpoint.Json(HttpStatusCode.InternalServerError, new { error = "server_error", echo = "refresh_token=rt-1 client_secret=tk-client-secret-dummy" });
        await _store.SaveAsync("Tickets", Alice, Tokens(Now.AddSeconds(-5)), default);
        var ex = await Assert.ThrowsAsync<CredentialUnavailableException>(() => Delegated().AuthenticateAsync(OAuthInstance, Caller(), "corr-1", default));
        Assert.Contains("https://tk.example/tk/oauth/token", ex.Message);
        Assert.Contains("500", ex.Message);
        foreach (var text in _log.Lines.Append(ex.ToString()))
        {
            Assert.DoesNotContain("rt-1", text);
            Assert.DoesNotContain("at-1", text);
            Assert.DoesNotContain("tk-client-secret-dummy", text);
        }
        Assert.Equal(CredentialStatus.Connected, (await _store.GetAsync("Tickets", Alice, default))!.Status);
    }

    [Theory]
    [InlineData(CredentialStatus.Expired, "expired")]
    [InlineData(CredentialStatus.Revoked, "revoked")]
    public async Task OAuth_ExpiredStatus_ConnectionRequired(CredentialStatus status, string reason)
    {
        await _store.SaveAsync("Tickets", Alice, Tokens(Now.AddMinutes(30), status: status), default);
        var ex = await Assert.ThrowsAsync<ConnectionRequiredException>(() => Delegated().AuthenticateAsync(OAuthInstance, Caller(), "corr-1", default));
        Assert.Contains(reason, ex.Message);
        Assert.Empty(_tokens.Forms);
    }

    [Fact]
    public async Task OAuth_KindMismatch_ConnectionRequired()
    {
        await _store.SaveAsync("Tickets", Alice, Tokens(Now.AddMinutes(30), CredentialKind.Sso), default);
        var ex = await Assert.ThrowsAsync<ConnectionRequiredException>(() => Delegated().AuthenticateAsync(OAuthInstance, Caller(), "corr-1", default));
        Assert.Contains("another mode", ex.Message);
    }

    [Fact]
    public async Task OAuth_NoRefreshToken_ExpiredRequiresConnect()
    {
        await _store.SaveAsync("Tickets", Alice, Tokens(Now.AddSeconds(-5)) with { RefreshToken = null }, default);
        var ex = await Assert.ThrowsAsync<ConnectionRequiredException>(() => Delegated().AuthenticateAsync(OAuthInstance, Caller(), "corr-1", default));
        Assert.Contains("expired", ex.Message);
        Assert.Empty(_tokens.Forms);
    }

    [Fact]
    public async Task Impersonate_UsesConnectAppResource()
    {
        var instance = Instance(new AuthConfig(AuthMode.Impersonate, OAuthResource: "Entra"));
        await _store.SaveAsync("Tickets", Alice, Tokens(Now.AddSeconds(-5), CredentialKind.Impersonate), default);
        var sent = await Send(Delegated(AuthMode.Impersonate), instance);
        Assert.Equal("Bearer at-2", sent.Headers.Authorization!.ToString());
        var form = Assert.Single(_tokens.Forms);
        Assert.Equal("connect-app-id", form["client_id"]);
        Assert.Equal("mi-assertion", form["client_assertion"]);
        Assert.False(form.ContainsKey("client_secret"));
        Assert.Equal("api://tickets/user_impersonation offline_access", form["scope"]);
    }

    [Theory]
    [InlineData("https://tokens.contoso.example/token")]
    [InlineData("http://login.microsoftonline.com/t/oauth2/v2.0/token")]
    [InlineData("https://login.microsoftonline.com.contoso.example/t/oauth2/v2.0/token")]
    public async Task TokenClient_ConnectAppCredentialOnlyGoesToEntra(string tokenUrl)
    {
        var app = new CountingConnectApp();
        var client = new TokenEndpointClient(new HttpClient(_tokens), new FixedSecrets(), app, new FixedTime(Now));
        var resource = Entra with { TokenUrl = tokenUrl };
        var ex = await Assert.ThrowsAsync<ConfigurationException>(() => client.RefreshAsync(resource, Instance(new AuthConfig(AuthMode.Impersonate, OAuthResource: "Entra")), "rt-1", default));
        Assert.Contains("https://login.microsoftonline.com/", ex.Message);
        Assert.Empty(_tokens.Forms);
        Assert.Equal(0, app.Calls);
    }

    private sealed class CountingConnectApp : IConnectAppClient
    {
        public int Calls;
        public string ClientId => "connect-app-id";
        public Task<IReadOnlyList<KeyValuePair<string, string>>> ClientAuthAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult<IReadOnlyList<KeyValuePair<string, string>>>(new[] { new KeyValuePair<string, string>("client_assertion", "mi-assertion") });
        }
    }

    [Fact]
    public async Task TokenClient_RedeemCode_SendsPkceVerifierAndRedirect()
    {
        var set = await Client().RedeemCodeAsync(Tickets, OAuthInstance, "code-1", "verifier-1", "https://bf-host.example/connect/callback", default);
        Assert.Equal(("at-2", "rt-2", Now.AddSeconds(3600)), (set.AccessToken, set.RefreshToken, set.ExpiresAt));
        var form = Assert.Single(_tokens.Forms);
        Assert.Equal(("authorization_code", "code-1", "verifier-1", "https://bf-host.example/connect/callback"),
            (form["grant_type"], form["code"], form["code_verifier"], form["redirect_uri"]));
    }

    [Fact]
    public async Task TokenClient_ExpiresInAsString_Accepted()
    {
        _tokens.Override = () => TokenEndpoint.Json(HttpStatusCode.OK, new { access_token = "at-s", refresh_token = "rt-s", expires_in = "3599", token_type = "Bearer" });
        var set = await Client().RedeemCodeAsync(Tickets, OAuthInstance, "code-1", "verifier-1", "https://bf-host.example/connect/callback", default);
        Assert.Equal(("at-s", Now.AddSeconds(3599)), (set.AccessToken, set.ExpiresAt));
    }

    [Theory]
    [InlineData("soon")]
    [InlineData("-5")]
    [InlineData(true)]
    public async Task TokenClient_ExpiresInNotAPositiveInteger_Refused(object value)
    {
        _tokens.Override = () => TokenEndpoint.Json(HttpStatusCode.OK, new { access_token = "at-s", expires_in = value });
        var ex = await Assert.ThrowsAsync<CredentialUnavailableException>(() =>
            Client().RedeemCodeAsync(Tickets, OAuthInstance, "code-1", "verifier-1", "https://bf-host.example/connect/callback", default));
        Assert.Contains("expires_in", ex.Message);
        Assert.DoesNotContain("at-s", ex.Message);
    }

    [Fact]
    public async Task TokenClient_EndpointDoesNotAnswer_CredentialUnavailable()
    {
        _tokens.Delays.Enqueue(5000);
        var client = new TokenEndpointClient(new HttpClient(_tokens) { Timeout = TimeSpan.FromMilliseconds(100) }, new FixedSecrets(), new FakeConnectApp(), new FixedTime(Now));
        var ex = await Assert.ThrowsAsync<CredentialUnavailableException>(() =>
            client.RedeemCodeAsync(Tickets, OAuthInstance, "code-1", "verifier-1", "https://bf-host.example/connect/callback", default));
        Assert.Contains("did not answer", ex.Message);
        Assert.DoesNotContain("code-1", ex.Message);
    }

    [Fact]
    public async Task TokenClient_CallerCancels_StillCancellation()
    {
        _tokens.Delays.Enqueue(5000);
        using var cancel = new CancellationTokenSource(100);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Client().RedeemCodeAsync(Tickets, OAuthInstance, "code-1", "verifier-1", "https://bf-host.example/connect/callback", cancel.Token));
    }

    [Fact]
    public async Task RefreshGate_EntryRemovedWhenLastCallerLeaves()
    {
        var gate = new RefreshGate();
        var first = await gate.EnterAsync("Tickets", Alice, default);
        var second = gate.EnterAsync("Tickets", Alice, default);
        Assert.False(second.IsCompleted);
        Assert.Equal(1, gate.Count);
        first.Dispose();
        (await second).Dispose();
        Assert.Equal(0, gate.Count);
        using var cancel = new CancellationTokenSource();
        var held = await gate.EnterAsync("Tickets", Alice, default);
        var waiting = gate.EnterAsync("Tickets", Alice, cancel.Token);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        held.Dispose();
        Assert.Equal(0, gate.Count);
    }

    /// <summary>A store whose row for Alice cannot be decrypted (key ring lost or row corrupted).</summary>
    private sealed class UnreadableStore : IUserCredentialStore
    {
        private static CredentialUnreadableException Unreadable() => new("The stored credential for Tickets could not be read.");
        public Task<UserCredential?> GetAsync(string instance, string userObjectId, CancellationToken ct) => throw Unreadable();
        public Task<VersionedCredential?> GetVersionedAsync(string instance, string userObjectId, CancellationToken ct) => throw Unreadable();
        public Task SaveAsync(string instance, string userObjectId, UserCredential credential, CancellationToken ct) => Task.CompletedTask;
        public Task<bool> ReplaceIfUnchangedAsync(string instance, string userObjectId, UserCredential credential, string version, CancellationToken ct) => Task.FromResult(false);
        public Task<bool> SetStatusIfUnchangedAsync(string instance, string userObjectId, CredentialStatus status, string version, CancellationToken ct) => Task.FromResult(false);
        public Task DeleteAsync(string instance, string userObjectId, CancellationToken ct) => Task.CompletedTask;
    }

    [Fact]
    public async Task UnreadableCredential_ConnectionRequired()
    {
        var oauth = new DelegatedTokenAuthProvider(AuthMode.OAuth, new UnreadableStore(), (_, _) => Task.FromResult(Tickets), Client(), _ => ConnectUrl, new HttpClient(_source), _log, new FixedTime(Now));
        var ex = await Assert.ThrowsAsync<ConnectionRequiredException>(() => oauth.AuthenticateAsync(OAuthInstance, Caller(), "corr-1", default));
        Assert.Contains("could not be read", ex.Message);
        Assert.Equal(ConnectUrl, ex.ConnectUrl);
        var sso = new SsoAuthProvider(new UnreadableStore(), _ => ConnectUrl, new HttpClient(_source));
        ex = await Assert.ThrowsAsync<ConnectionRequiredException>(() => sso.AuthenticateAsync(Instance(new AuthConfig(AuthMode.Sso, SsoKind: "basic")), Caller(), "corr-1", default));
        Assert.Contains("could not be read", ex.Message);
    }

    [Fact]
    public async Task Sso_Basic_AttachesStoredCredential()
    {
        var instance = Instance(new AuthConfig(AuthMode.Sso, SsoKind: "basic"));
        await _store.SaveAsync("Tickets", Alice, new UserCredential(CredentialKind.Sso, CredentialStatus.Connected, null, null, null, "alice", "pw-123", Now), default);
        var sent = await Send(new SsoAuthProvider(_store, _ => ConnectUrl, new HttpClient(_source)), instance);
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("alice:pw-123")), sent.Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task Sso_ApiKeyHeader_AttachesStoredSecret()
    {
        var instance = Instance(new AuthConfig(AuthMode.Sso, HeaderName: "X-User-Key", SsoKind: "apiKeyHeader"));
        await _store.SaveAsync("Tickets", Alice, new UserCredential(CredentialKind.Sso, CredentialStatus.Connected, null, null, null, null, "key-123", Now), default);
        var provider = new SsoAuthProvider(_store, _ => ConnectUrl, new HttpClient(_source));
        var channel = await provider.AuthenticateAsync(instance, Caller(), "corr-1", default);
        await channel.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://tk.example/tk/v1/tickets"), default);
        Assert.Equal("key-123", _source.Last!.Headers.GetValues("X-User-Key").Single());
        Assert.Contains("key-123", channel.CredentialValues);
    }

    [Fact]
    public async Task Sso_NoCredential_ConnectionRequired()
    {
        var instance = Instance(new AuthConfig(AuthMode.Sso, SsoKind: "basic"));
        var ex = await Assert.ThrowsAsync<ConnectionRequiredException>(() =>
            new SsoAuthProvider(_store, _ => ConnectUrl, new HttpClient(_source)).AuthenticateAsync(instance, Caller(), "corr-1", default));
        Assert.Equal(ConnectUrl, ex.ConnectUrl);
    }
}
