using System.Net;
using System.Web;
using Azure.Core;
using BrokerForge.Abstractions;
using BrokerForge.Auth.Credentials;
using BrokerForge.Connections;
using BrokerForge.Contracts;
using BrokerForge.Host.Ingress;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace BrokerForge.Contract.Tests;

/// <summary>
/// A connection design never borrows the host's identity for an audience the host did not allow (HLD D-34, final
/// review C1): ServiceAccount and Impersonate designs need their scope in <c>Trust:AllowedCallers[i]:DesignScopes</c>,
/// and Azure management, Key Vault, Storage, Dataverse and Graph are refused whatever the list says.
/// </summary>
public partial class EnvironmentConfigTests
{
    private const string AllowedScope = "api://contoso-inventory/.default";

    /// <summary>Stands in for the host's managed identity; records every scope it is asked for.</summary>
    private sealed class RecordingCredential : TokenCredential
    {
        public List<string> Scopes { get; } = new();

        public override AccessToken GetToken(TokenRequestContext request, CancellationToken ct)
        {
            lock (Scopes)
                Scopes.AddRange(request.Scopes);
            return new AccessToken("test-only-host-token", DateTimeOffset.UtcNow.AddHours(1));
        }

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext request, CancellationToken ct) => new(GetToken(request, ct));
    }

    private readonly RecordingCredential _hostIdentity = new();
    private readonly List<(Uri Url, string Body)> _tokenPosts = new();

    /// <summary>
    /// Records every token endpoint call with its form. Only the mock (loopback) is really called: anything else, such
    /// as Entra, gets invalid_grant here, so no test reaches the internet.
    /// </summary>
    private sealed class TokenTrap(List<(Uri, string)> posts) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct);
            lock (posts)
                posts.Add((request.RequestUri!, body));
            if (request.RequestUri!.IsLoopback)
                return await base.SendAsync(request, ct);
            return new HttpResponseMessage(System.Net.HttpStatusCode.BadRequest)
            {
                Content = new StringContent("{\"error\":\"invalid_grant\"}", System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }

    private const string ConnectAssertion = "test-only-connect-app-assertion";

    private sealed class FakeConnectApp : BrokerForge.Auth.PerUser.IConnectAppClient
    {
        public string ClientId => "connect-test-client";
        public Task<IReadOnlyList<KeyValuePair<string, string>>> ClientAuthAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<KeyValuePair<string, string>>>(new[]
            {
                new KeyValuePair<string, string>("client_assertion_type", "urn:ietf:params:oauth:client-assertion-type:jwt-bearer"),
                new KeyValuePair<string, string>("client_assertion", ConnectAssertion),
            });
    }

    private static string EntraEndpoint(string what) => $"https://login.microsoftonline.com/{HostFactory.Tenant}/oauth2/v2.0/{what}";

    private HostFactory ScopedHost(params string[] designScopesForA)
    {
        var edits = designScopesForA.Select((s, i) => ($"Trust:AllowedCallers:0:DesignScopes:{i}", (string?)s)).ToArray();
        return Host(edits);
    }

    private static ConnectionDesign ServiceAccountDesign(string scope) => Warehouse() with
    {
        Auth = new DesignAuth("ServiceAccount", null, null, null, null, scope, null, null, null, Array.Empty<string>()),
    };

    private ConnectionDesign ImpersonateDesign(params string[] scopes) => ImpersonateDesignAt(null, null, scopes);

    private ConnectionDesign ImpersonateDesignAt(string? authorizeUrl, string? tokenUrl, params string[] scopes) => TicketsDesign() with
    {
        Auth = new DesignAuth("Impersonate", null, null, null, null, null, null, null,
            new DesignOAuth(authorizeUrl, tokenUrl, scopes, null, true), Array.Empty<string>()),
    };

    private static void AssertRefused((int Status, CanonicalResponse? Response, ErrorPayload? Error) result, params string[] parts)
    {
        Assert.Equal(500, result.Status);
        Assert.Equal("ConfigurationError", result.Error!.Error);
        foreach (var part in parts)
            Assert.Contains(part, result.Error.Message);
    }

    [Fact]
    public async Task DesignServiceAccountScopeNotAllowedFails()
    {
        _designs.Add(EnvA, ServiceAccountDesign(AllowedScope), At(mock.BaseUrl));
        var result = await Run(ScopedHost(), List("ext_item", "WarehouseAPI", EnvA));
        AssertRefused(result, "WarehouseAPI", AllowedScope, EnvA, "DesignScopes");
        Assert.Empty(_hostIdentity.Scopes);
        Assert.Equal(0, _sent.Value);
    }

    [Theory]
    [InlineData("https://vault.azure.net/.default")]
    [InlineData("https://contoso-kv.vault.azure.net/.default")]
    [InlineData("https://management.azure.com/.default")]
    [InlineData("https://management.core.windows.net//.default")]
    [InlineData("https://contosostore.blob.core.windows.net/.default")]
    [InlineData("https://storage.azure.com/user_impersonation")]
    [InlineData("https://contoso.crm4.dynamics.com/.default")]
    [InlineData("https://graph.microsoft.com/.default")]
    [InlineData("00000003-0000-0000-c000-000000000000/.default")]
    [InlineData("cfa8b339-82a2-471a-a3c9-0fc0be7a4093/.default")]
    public async Task DesignServiceAccountKeyVaultScopeAlwaysRefused(string reserved)
    {
        _designs.Add(EnvA, ServiceAccountDesign(reserved), At(mock.BaseUrl));
        var result = await Run(ScopedHost(AllowedScope), List("ext_item", "WarehouseAPI", EnvA));
        AssertRefused(result, "WarehouseAPI", reserved, EnvA, "never");
        Assert.Empty(_hostIdentity.Scopes);
        Assert.Equal(0, _sent.Value);
    }

    [Fact]
    public async Task DesignServiceAccountReservedScopeThroughOverrideRefused()
    {
        _designs.Add(EnvA, ServiceAccountDesign(AllowedScope), At(mock.BaseUrl, ("scope", "https://vault.azure.net/.default")));
        var result = await Run(ScopedHost(AllowedScope), List("ext_item", "WarehouseAPI", EnvA));
        AssertRefused(result, "WarehouseAPI", "https://vault.azure.net/.default", "never");
        Assert.Empty(_hostIdentity.Scopes);
    }

    [Fact]
    public async Task DesignServiceAccountAllowedScopeWorks()
    {
        _designs.Add(EnvA, ServiceAccountDesign(AllowedScope), At(mock.BaseUrl));
        var result = await Run(ScopedHost(AllowedScope), List("ext_item", "WarehouseAPI", EnvA));
        // The Inventory mock wants its API key, so it refuses the bearer token: what matters is that the call was made.
        Assert.NotEqual("ConfigurationError", result.Error?.Error);
        Assert.Equal(new[] { AllowedScope }, _hostIdentity.Scopes);
        Assert.Equal(1, _sent.Value);
    }

    [Fact]
    public async Task DesignScopesForOneEnvironmentDoNotAllowAnother()
    {
        _designs.Add(EnvB, ServiceAccountDesign(AllowedScope), At(mock.BaseUrl));
        var result = await Run(ScopedHost(AllowedScope), List("ext_item", "WarehouseAPI", EnvB));
        AssertRefused(result, "WarehouseAPI", AllowedScope, EnvB);
        Assert.Empty(_hostIdentity.Scopes);
    }

    [Fact]
    public async Task DesignImpersonateScopeNotAllowedFails()
    {
        _designs.Add(EnvA, ImpersonateDesign(Tickets.EntraAudience + "/user_impersonation", "offline_access"), At(mock.BaseUrl));
        var result = await Run(ScopedHost(AllowedScope), List("ext_ticket", "TicketsAPI", EnvA, Tickets.AliceOid));
        AssertRefused(result, "TicketsAPI", Tickets.EntraAudience + "/user_impersonation", EnvA, "DesignScopes");
    }

    [Theory]
    [InlineData("https://graph.microsoft.com/User.Read")]
    [InlineData("User.Read")]
    [InlineData("https://vault.azure.net/user_impersonation")]
    public async Task DesignImpersonateReservedScopeAlwaysRefused(string reserved)
    {
        _designs.Add(EnvA, ImpersonateDesign(reserved), At(mock.BaseUrl));
        var result = await Run(ScopedHost(Tickets.EntraAudience + "/user_impersonation"), List("ext_ticket", "TicketsAPI", EnvA, Tickets.AliceOid));
        AssertRefused(result, "TicketsAPI", reserved, "never");
    }

    [Fact]
    public async Task DesignImpersonateWithOnlySignInScopesRefused()
    {
        // openid and offline_access alone still give a Microsoft Graph access token.
        _designs.Add(EnvA, ImpersonateDesign("openid", "offline_access"), At(mock.BaseUrl));
        var result = await Run(ScopedHost(AllowedScope), List("ext_ticket", "TicketsAPI", EnvA, Tickets.AliceOid));
        AssertRefused(result, "TicketsAPI", "Microsoft Graph");
    }

    [Fact]
    public async Task DesignImpersonateAllowedScopeWorks()
    {
        var scope = Tickets.EntraAudience + "/user_impersonation";
        _designs.Add(EnvA, ImpersonateDesign(scope, "offline_access"), At(mock.BaseUrl));
        var host = ScopedHost(scope);
        await host.Services.GetRequiredService<IUserCredentialStore>().SaveAsync($"{Guid.Parse(EnvA):N}_TicketsAPI", Tickets.AliceOid,
            new UserCredential(CredentialKind.Impersonate, CredentialStatus.Connected, Tickets.EntraToken(Tickets.AliceOid), "entra-rt-unused",
                DateTimeOffset.UtcNow.AddHours(1), null, null, DateTimeOffset.UtcNow), default);
        var (status, response, error) = await Run(host, List("ext_ticket", "TicketsAPI", EnvA, Tickets.AliceOid));
        Assert.True(status == 200, error?.Message);
        Assert.NotEmpty(response!.Rows);
    }

    [Theory]
    [InlineData("https://vault.azure.net/.default")]
    [InlineData("https://management.azure.com/.default")]
    [InlineData("https://contoso.crm.dynamics.com/.default")]
    [InlineData("https://graph.microsoft.com/.default")]
    [InlineData("https://contosostore.table.core.windows.net/.default")]
    public void DesignScopesListWithReservedAudienceRejectedAtStartup(string reserved)
    {
        var trust = new TrustOptions
        {
            TenantId = HostFactory.Tenant,
            Audience = HostFactory.Audience,
            AllowedCallers = { new AllowedCaller { ClientId = HostFactory.PluginApp, EnvironmentId = EnvA, DesignScopes = { AllowedScope, reserved } } },
        };
        var problem = Assert.Single(trust.Problems());
        Assert.Contains($"Trust:AllowedCallers entry for environment {EnvA}: DesignScopes lists {reserved}", problem);

        using var host = new HostFactory(mock.BaseUrl, extra: new Dictionary<string, string?> { ["Trust:AllowedCallers:0:DesignScopes:0"] = reserved });
        var ex = Assert.Throws<ConfigurationException>(() => host.CreateClient());
        Assert.Contains($"DesignScopes lists {reserved}", ex.Message);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("2")]
    [InlineData(" ServiceAccount")]
    [InlineData("ServiceAccount ")]
    [InlineData("serviceaccount")]
    [InlineData("ServiceAccount, Static")]
    public async Task DesignModeMustBeAnExactModeName(string mode)
    {
        var design = ServiceAccountDesign("https://vault.azure.net/.default");
        _designs.Add(EnvA, design with { Auth = design.Auth with { Mode = mode } }, At(mock.BaseUrl));
        var result = await Run(ScopedHost(AllowedScope), List("ext_item", "WarehouseAPI", EnvA));
        AssertRefused(result, "WarehouseAPI", "auth.mode");
        Assert.Empty(_hostIdentity.Scopes);
        Assert.Equal(0, _sent.Value);
    }

    [Theory]
    [InlineData("inventory.contoso.example/.default")]
    [InlineData("https://contoso inventory/.default")]
    [InlineData("http://inventory.contoso.example/.default")]
    [InlineData("https://user@inventory.contoso.example/.default")]
    [InlineData("ftp://inventory.contoso.example/.default")]
    [InlineData("11111111-0000-0000-0000-000000000000/.default")]
    public async Task DesignScopeThatIsNotAnHttpsOrApiUriRefused(string scope)
    {
        _designs.Add(EnvA, ServiceAccountDesign(scope), At(mock.BaseUrl));
        var result = await Run(ScopedHost(AllowedScope), List("ext_item", "WarehouseAPI", EnvA));
        AssertRefused(result, "WarehouseAPI", scope, "must be an absolute https:// or api:// URI");
        Assert.Empty(_hostIdentity.Scopes);
    }

    [Theory]
    [InlineData("https://tokens.contoso.example/token", null)]
    [InlineData(null, "https://login.contoso.example/authorize")]
    public async Task ImpersonateDesignWithTokenUrlRefused(string? tokenUrl, string? authorizeUrl)
    {
        var scope = Tickets.EntraAudience + "/user_impersonation";
        _designs.Add(EnvA, ImpersonateDesignAt(authorizeUrl, tokenUrl, scope), At(mock.BaseUrl));
        var result = await Run(ScopedHost(scope), List("ext_ticket", "TicketsAPI", EnvA, Tickets.AliceOid));
        AssertRefused(result, "TicketsAPI", "usesConnectApp", tokenUrl != null ? "tokenUrl" : "authorizeUrl");
        Assert.Empty(_tokenPosts);
    }

    [Fact]
    public async Task ImpersonateDesignUsesTenantEntraEndpoints()
    {
        var scope = Tickets.EntraAudience + "/user_impersonation";
        _designs.Add(EnvA, ImpersonateDesign(scope, "offline_access"), At(mock.BaseUrl));
        var host = ScopedHost(scope);

        // The connect page sends the browser to the tenant's Entra authorize endpoint.
        var browser = host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        browser.DefaultRequestHeaders.Add(ConnectTests.OidHeader, Tickets.AliceOid);
        var path = $"/connect/{EnvA}/TicketsAPI";
        var html = await (await browser.GetAsync(path)).Content.ReadAsStringAsync();
        var token = HttpUtility.HtmlDecode(AntiforgeryField().Match(html).Groups[1].Value);
        var start = await browser.PostAsync(path + "/start", new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token }));
        Assert.True(start.StatusCode == HttpStatusCode.Redirect, await start.Content.ReadAsStringAsync());
        Assert.StartsWith(EntraEndpoint("authorize") + "?", start.Headers.Location!.ToString());

        // A refresh posts the connect app's credential to the tenant's Entra token endpoint only.
        await host.Services.GetRequiredService<IUserCredentialStore>().SaveAsync($"{Guid.Parse(EnvA):N}_TicketsAPI", Tickets.AliceOid,
            new UserCredential(CredentialKind.Impersonate, CredentialStatus.Connected, Tickets.EntraToken(Tickets.AliceOid), "entra-rt",
                DateTimeOffset.UtcNow.AddSeconds(-5), null, null, DateTimeOffset.UtcNow), default);
        var (status, _, error) = await Run(host, List("ext_ticket", "TicketsAPI", EnvA, Tickets.AliceOid));
        Assert.Equal(401, status);
        Assert.Equal(nameof(ConnectionRequiredException), error!.Error);
        var post = Assert.Single(_tokenPosts);
        Assert.Equal(EntraEndpoint("token"), post.Url.ToString());
        Assert.Contains("client_id=connect-test-client", post.Body);
        Assert.Contains("client_assertion=" + ConnectAssertion, post.Body);
    }

    [Fact]
    public async Task HostCredentialNeverPostedToNonEntraUrl()
    {
        // An OAuth design (its own client) refreshes at its own token URL with its own stored secret only.
        _designs.Add(EnvA, OAuthStoreDesign(), At(mock.BaseUrl));
        await _secrets.SetAsync(EnvA, "TicketsAPI", "clientSecret", Tickets.ClientSecret, default);
        var host = ScopedHost();
        await ExpiredAliceToken(host);
        var (status, _, error) = await Run(host, List("ext_ticket", "TicketsAPI", EnvA, Tickets.AliceOid));
        Assert.True(status == 200, error?.Message);

        Assert.NotEmpty(_tokenPosts);
        foreach (var (url, body) in _tokenPosts.Where(p => p.Url.Host != "login.microsoftonline.com"))
        {
            Assert.DoesNotContain("connect-test-client", body);
            Assert.DoesNotContain("client_assertion", body);
            Assert.DoesNotContain("bf-test-connect-secret", body);
            Assert.DoesNotContain(ConnectAssertion, body);
        }
    }

    [Fact]
    public async Task OAuthDesignTokenUrlMustBeHttps()
    {
        var design = TicketsDesign();
        _designs.Add(EnvA, design with { Auth = design.Auth with { OAuth = design.Auth.OAuth! with { TokenUrl = "http://tokens.contoso.example/token" } } }, At(mock.BaseUrl));
        var result = await Run(ScopedHost(), List("ext_ticket", "TicketsAPI", EnvA, Tickets.AliceOid));
        AssertRefused(result, "TicketsAPI", "tokenUrl", "https");
    }
}
