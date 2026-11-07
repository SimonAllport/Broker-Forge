using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using System.Web;
using BrokerForge.Auth.Credentials;
using BrokerForge.Contracts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace BrokerForge.Contract.Tests;

/// <summary>
/// The connect page (HLD D-26): the signed-in user grants OAuth consent or enters SSO credentials, stored
/// under their Entra object ID. The Entra sign-in itself is replaced by a header-driven test scheme; the
/// OpenID Connect challenge still runs against a static configuration.
/// </summary>
[Collection(MockApiCollection.Name)]
public partial class ConnectTests(MockApiFixture mock) : IAsyncLifetime
{
    public const string TestScheme = "TestUser";
    public const string OidHeader = "X-Test-Oid";

    /// <summary>Signs in whoever the X-Test-Oid header names, as Entra would after OpenID Connect.</summary>
    public sealed class TestUserHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(OidHeader, out var oid))
                return Task.FromResult(AuthenticateResult.NoResult());
            var identity = new ClaimsIdentity(new[] { new Claim("oid", oid.ToString()), new Claim("tid", HostFactory.Tenant), new Claim("name", "Test user") }, TestScheme);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), TestScheme)));
        }
    }

    public sealed class MutableTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private readonly MutableTime _time = new();
    private readonly List<HostFactory> _hosts = new();

    public Task InitializeAsync() => mock.ResetAsync();

    public Task DisposeAsync()
    {
        foreach (var h in _hosts)
            h.Dispose();
        return Task.CompletedTask;
    }

    public static (string, string?)[] ConnectSettings => new (string, string?)[]
    {
        ("Connect:ClientId", "connect-test-client"),
        ("Connect:ClientSecretRef", "env:BF_TEST_CONNECT_SECRET"),
    };

    /// <summary>Test services every connect host needs: the header sign-in and a static OpenID configuration.</summary>
    public static void TestSignIn(IServiceCollection s)
    {
        s.AddAuthentication().AddScheme<AuthenticationSchemeOptions, TestUserHandler>(TestScheme, _ => { });
        s.PostConfigure<CookieAuthenticationOptions>("Connect", o => o.ForwardAuthenticate = TestScheme);
        s.PostConfigure<OpenIdConnectOptions>("ConnectOidc", o =>
        {
            var config = new OpenIdConnectConfiguration { AuthorizationEndpoint = "https://login.test/authorize", TokenEndpoint = "https://login.test/token" };
            o.Configuration = config;
            o.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(config);
        });
    }

    private HostFactory Host(params (string, string?)[] edits) => Host(null, edits);

    private HostFactory Host(Action<IServiceCollection>? more, params (string, string?)[] edits)
    {
        Environment.SetEnvironmentVariable("BF_TEST_CONNECT_SECRET", "bf-test-connect-secret");
        var host = new HostFactory(mock.BaseUrl, extra: Tickets.HostSettings(mock.BaseUrl, ConnectSettings.Concat(edits).ToArray()), services: s =>
        {
            TestSignIn(s);
            s.AddSingleton<TimeProvider>(_time);
            more?.Invoke(s);
        });
        _hosts.Add(host);
        return host;
    }

    private static readonly (string, string?)[] Sso =
    {
        ("Instances:TicketsAPI:Auth:Mode", "Sso"), ("Instances:TicketsAPI:Auth:SsoKind", "basic"), ("Instances:TicketsAPI:Auth:OAuthResource", null),
    };

    private static HttpClient Browser(HostFactory host, string? oid)
    {
        var client = host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        if (oid != null)
            client.DefaultRequestHeaders.Add(OidHeader, oid);
        return client;
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" value=\"([^\"]+)\"")]
    private static partial Regex AntiforgeryField();

    private static async Task<(HttpResponseMessage Page, string Html, string Token)> Page(HttpClient browser, string path = "/connect/TicketsAPI")
    {
        var page = await browser.GetAsync(path);
        var html = await page.Content.ReadAsStringAsync();
        var match = AntiforgeryField().Match(html);
        return (page, html, match.Success ? HttpUtility.HtmlDecode(match.Groups[1].Value) : "");
    }

    private static Task<HttpResponseMessage> Post(HttpClient browser, string path, string token, params (string, string)[] fields) =>
        browser.PostAsync(path, new FormUrlEncodedContent(fields.Select(f => new KeyValuePair<string, string>(f.Item1, f.Item2))
            .Append(new KeyValuePair<string, string>("__RequestVerificationToken", token))));

    /// <summary>Connect page, start, and the user signing in at the mock: returns the callback path and query the browser would follow.</summary>
    private async Task<string> StartAndSignIn(HttpClient browser, string user)
    {
        var (_, _, token) = await Page(browser);
        var start = await Post(browser, "/connect/TicketsAPI/start", token);
        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        var authorize = start.Headers.Location!;
        Assert.StartsWith(mock.BaseUrl + "/tk/oauth/authorize?", authorize.ToString());
        var q = HttpUtility.ParseQueryString(authorize.Query);
        Assert.Equal(("bf-tickets", Tickets.Redirect, "S256", "code", "tickets.read"), (q["client_id"], q["redirect_uri"], q["code_challenge_method"], q["response_type"], q["scope"]));
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        var form = q.AllKeys.ToDictionary(k => k!, k => q[k]!);
        form["user"] = user;
        form["password"] = Tickets.Password(user);
        var signedIn = await http.PostAsync(mock.BaseUrl + "/tk/oauth/authorize", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.Redirect, signedIn.StatusCode);
        var callback = signedIn.Headers.Location!;
        Assert.StartsWith(Tickets.Redirect + "?", callback.ToString());
        return callback.PathAndQuery;
    }

    private static async Task<string[]> ListTitles(HostFactory host, string oid)
    {
        var request = HostFactory.Request("ext_ticket", Messages.RetrieveMultiple, "TicketsAPI");
        request.Query = new CanonicalQuery { Page = new PageRequest { Size = 50 } };
        request.Caller = new CallerContext { UserObjectId = oid };
        var (status, response, error) = await host.Execute(request);
        Assert.True(status == 200, error?.Message);
        return response!.Rows.Select(r => r.Cells.Single(c => c.Col == "ext_title").Value!).ToArray();
    }

    [Fact]
    public async Task Connect_Unauthenticated_ChallengesOidc()
    {
        var response = await Browser(Host(), null).GetAsync("/connect/TicketsAPI");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!;
        Assert.StartsWith("https://login.test/authorize?", location.ToString());
        Assert.Equal("connect-test-client", HttpUtility.ParseQueryString(location.Query)["client_id"]);
    }

    [Fact]
    public async Task Connect_OAuthFlow_StoresConnectedCredential()
    {
        var host = Host();
        var browser = Browser(host, Tickets.AliceOid);
        var (page, html, _) = await Page(browser);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("Status: not connected", html);

        var callback = await browser.GetAsync(await StartAndSignIn(browser, "alice"));
        Assert.Equal(HttpStatusCode.SeeOther, callback.StatusCode);
        Assert.Equal("/connect/TicketsAPI", callback.Headers.Location!.ToString());

        var stored = await host.Services.GetRequiredService<IUserCredentialStore>().GetAsync("TicketsAPI", Tickets.AliceOid, default);
        Assert.Equal((CredentialKind.OAuth, CredentialStatus.Connected), (stored!.Kind, stored.Status));
        Assert.StartsWith("tk-rt-", stored.RefreshToken);
        Assert.Contains("Status: connected", (await Page(browser)).Html);
        Assert.Equal(Tickets.AliceTickets.Select(id => "Ticket " + id[3..]), await ListTitles(host, Tickets.AliceOid));
        Assert.DoesNotContain(host.Logs, l => l.Contains(stored.AccessToken!) || l.Contains(stored.RefreshToken!));
    }

    [Theory]
    [InlineData("other user")]
    [InlineData("tampered")]
    [InlineData("reused")]
    [InlineData("11 minutes old")]
    public async Task Callback_StateForOtherUserOrReplayed_Refused(string variant)
    {
        var host = Host();
        var store = host.Services.GetRequiredService<IUserCredentialStore>();
        var alice = Browser(host, Tickets.AliceOid);
        var callback = await StartAndSignIn(alice, "alice");
        HttpResponseMessage refused;
        string reason;
        switch (variant)
        {
            case "other user":
                // Same browser, so the nonce cookie matches; only the signed-in user has changed.
                alice.DefaultRequestHeaders.Remove(OidHeader);
                alice.DefaultRequestHeaders.Add(OidHeader, Tickets.BobOid);
                refused = await alice.GetAsync(callback);
                reason = "started by another user";
                break;
            case "tampered":
                var q = HttpUtility.ParseQueryString(callback[(callback.IndexOf('?') + 1)..]);
                var state = q["state"]!;
                q["state"] = state[..^2] + (state[^2] == 'A' ? 'B' : 'A') + state[^1];
                refused = await alice.GetAsync("/connect/callback?" + q);
                reason = "sign-in link is not valid";
                break;
            case "reused":
                Assert.Equal(HttpStatusCode.SeeOther, (await alice.GetAsync(callback)).StatusCode);
                await store.DeleteAsync("TicketsAPI", Tickets.AliceOid, default);
                refused = await alice.GetAsync(callback);
                reason = "has already been used";
                break;
            default:
                _time.Now += TimeSpan.FromMinutes(11);
                refused = await alice.GetAsync(callback);
                reason = "longer than 10 minutes";
                break;
        }
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains(reason, await refused.Content.ReadAsStringAsync());
        Assert.Null(await store.GetAsync("TicketsAPI", Tickets.AliceOid, default));
        Assert.Null(await store.GetAsync("TicketsAPI", Tickets.BobOid, default));
    }

    [Fact]
    public async Task Callback_TwoTabs_BothComplete()
    {
        var host = Host();
        var browser = Browser(host, Tickets.AliceOid);
        var first = await StartAndSignIn(browser, "alice");
        var second = await StartAndSignIn(browser, "alice");
        Assert.Equal(HttpStatusCode.SeeOther, (await browser.GetAsync(first)).StatusCode);
        Assert.Equal(HttpStatusCode.SeeOther, (await browser.GetAsync(second)).StatusCode);
        Assert.NotNull(await host.Services.GetRequiredService<IUserCredentialStore>().GetAsync("TicketsAPI", Tickets.AliceOid, default));
    }

    /// <summary>Alice's row cannot be decrypted (key ring lost, row corrupted) until it is deleted.</summary>
    private sealed class UnreadableForAlice : IUserCredentialStore
    {
        private readonly InMemoryUserCredentialStore _inner = new();
        private bool _broken = true;
        private void Check(string user)
        {
            if (_broken && user == Tickets.AliceOid)
                throw new CredentialUnreadableException("The stored credential for TicketsAPI could not be read.");
        }
        public Task<UserCredential?> GetAsync(string i, string u, CancellationToken ct) { Check(u); return _inner.GetAsync(i, u, ct); }
        public Task<VersionedCredential?> GetVersionedAsync(string i, string u, CancellationToken ct) { Check(u); return _inner.GetVersionedAsync(i, u, ct); }
        public Task SaveAsync(string i, string u, UserCredential c, CancellationToken ct) { _broken = false; return _inner.SaveAsync(i, u, c, ct); }
        public Task<bool> ReplaceIfUnchangedAsync(string i, string u, UserCredential c, string v, CancellationToken ct) => _inner.ReplaceIfUnchangedAsync(i, u, c, v, ct);
        public Task<bool> SetStatusIfUnchangedAsync(string i, string u, CredentialStatus s, string v, CancellationToken ct) => _inner.SetStatusIfUnchangedAsync(i, u, s, v, ct);
        public Task DeleteAsync(string i, string u, CancellationToken ct) { _broken = false; return _inner.DeleteAsync(i, u, ct); }
    }

    [Fact]
    public async Task Status_UnreadableCredential_ShownAndCanDisconnect()
    {
        var host = Host(s => s.AddSingleton<IUserCredentialStore, UnreadableForAlice>());
        var browser = Browser(host, Tickets.AliceOid);
        var (page, html, token) = await Page(browser);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("could not be read", html);
        Assert.Contains("/connect/TicketsAPI/disconnect", html);
        var disconnected = await Post(browser, "/connect/TicketsAPI/disconnect", token);
        Assert.Equal(HttpStatusCode.SeeOther, disconnected.StatusCode);
        Assert.Contains("Status: not connected", (await Page(browser)).Html);
    }

    [Fact]
    public async Task Callback_SourceError_ShownAndNothingStored()
    {
        var host = Host();
        var alice = Browser(host, Tickets.AliceOid);
        var callback = await StartAndSignIn(alice, "alice");
        var state = HttpUtility.ParseQueryString(callback[(callback.IndexOf('?') + 1)..])["state"];
        var response = await alice.GetAsync($"/connect/callback?error=access_denied&error_description=%3Cscript%3E&state={Uri.EscapeDataString(state!)}");
        var html = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("access_denied", html);
        Assert.DoesNotContain("<script>", html);
        Assert.Null(await host.Services.GetRequiredService<IUserCredentialStore>().GetAsync("TicketsAPI", Tickets.AliceOid, default));
    }

    [Fact]
    public async Task Connect_Sso_StoresCredential_NeverEchoesSecret()
    {
        var host = Host(Sso);
        var browser = Browser(host, Tickets.AliceOid);
        var (_, html, token) = await Page(browser);
        Assert.Contains("name=\"username\"", html);
        var saved = await Post(browser, "/connect/TicketsAPI/sso", token, ("username", "alice"), ("secret", Tickets.AlicePassword));
        Assert.Equal(HttpStatusCode.SeeOther, saved.StatusCode);
        Assert.DoesNotContain(Tickets.AlicePassword, await saved.Content.ReadAsStringAsync());
        var after = await Page(browser);
        Assert.Contains("Status: connected", after.Html);
        Assert.DoesNotContain(Tickets.AlicePassword, after.Html);
        var stored = await host.Services.GetRequiredService<IUserCredentialStore>().GetAsync("TicketsAPI", Tickets.AliceOid, default);
        Assert.Equal((CredentialKind.Sso, "alice", Tickets.AlicePassword), (stored!.Kind, stored.Username, stored.Secret));
        Assert.Equal(Tickets.AliceTickets.Select(id => "Ticket " + id[3..]), await ListTitles(host, Tickets.AliceOid));
        Assert.DoesNotContain(host.Logs, l => l.Contains(Tickets.AlicePassword));
    }

    [Theory]
    [InlineData("", "pw")]
    [InlineData("alice", "")]
    [InlineData("ali:ce", "pw")]
    [InlineData("alice", "line\nbreak")]
    public async Task Connect_Sso_InvalidInput_RefusedNothingStored(string username, string secret)
    {
        var host = Host(Sso);
        var browser = Browser(host, Tickets.AliceOid);
        var (_, _, token) = await Page(browser);
        var response = await Post(browser, "/connect/TicketsAPI/sso", token, ("username", username), ("secret", secret));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(await host.Services.GetRequiredService<IUserCredentialStore>().GetAsync("TicketsAPI", Tickets.AliceOid, default));
    }

    [Fact]
    public async Task Connect_PostWithoutAntiforgery_Refused()
    {
        var host = Host(Sso);
        var browser = Browser(host, Tickets.AliceOid);
        await Page(browser);
        var response = await Post(browser, "/connect/TicketsAPI/sso", "not-the-token", ("username", "alice"), ("secret", "pw"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(await host.Services.GetRequiredService<IUserCredentialStore>().GetAsync("TicketsAPI", Tickets.AliceOid, default));
    }

    [Fact]
    public async Task Disconnect_RemovesCredential_NextCallConnectionRequired()
    {
        var host = Host();
        var store = host.Services.GetRequiredService<IUserCredentialStore>();
        await store.SaveAsync("TicketsAPI", Tickets.AliceOid, Tickets.OAuthCredential(await Tickets.SignInAsync(mock.BaseUrl, "alice")), default);
        var browser = Browser(host, Tickets.AliceOid);
        var (_, _, token) = await Page(browser);
        Assert.Equal(HttpStatusCode.SeeOther, (await Post(browser, "/connect/TicketsAPI/disconnect", token)).StatusCode);
        Assert.Null(await store.GetAsync("TicketsAPI", Tickets.AliceOid, default));
        var request = HostFactory.Request("ext_ticket", Messages.RetrieveMultiple, "TicketsAPI");
        request.Query = new CanonicalQuery { Page = new PageRequest { Size = 10 } };
        request.Caller = new CallerContext { UserObjectId = Tickets.AliceOid };
        var (status, _, error) = await host.Execute(request);
        Assert.Equal((401, nameof(ConnectionRequiredException)), (status, error!.Error));
    }

    [Theory]
    [InlineData("/connect/Nope")]
    [InlineData("/connect/OrdersAPI")]
    public async Task Connect_UnknownOrNonPerUserInstance_404(string path)
    {
        var response = await Browser(Host(), Tickets.AliceOid).GetAsync(path);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Execute_WithConnectSignInOnly_401()
    {
        var browser = Browser(Host(), Tickets.AliceOid);
        var request = HostFactory.Request("ext_ticket", Messages.RetrieveMultiple, "TicketsAPI");
        var response = await browser.PostAsync("/v1/execute", System.Net.Http.Json.JsonContent.Create(request));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
