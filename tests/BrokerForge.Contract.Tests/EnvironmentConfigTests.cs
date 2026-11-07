using System.Net;
using System.Text.RegularExpressions;
using System.Web;
using BrokerForge.Abstractions;
using BrokerForge.Auth.Credentials;
using BrokerForge.Connections;
using BrokerForge.Contracts;
using BrokerForge.Host.Config;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace BrokerForge.Contract.Tests;

/// <summary>
/// One host, several Dataverse environments: connection designs are loaded per environment beside the file
/// configuration, and the same instance name in two environments never shares config, credentials or URLs.
/// </summary>
[Collection(MockApiCollection.Name)]
public partial class EnvironmentConfigTests(MockApiFixture mock) : IAsyncLifetime
{
    private const string EnvA = "00000000-0000-0000-0000-00000000000a";
    private const string EnvB = "00000000-0000-0000-0000-00000000000b";

    public sealed class FakeDesigns : IDesignSource
    {
        public Dictionary<string, List<(ConnectionDesign, EnvironmentSettings)>> ByEnvironment { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, List<string>> Failures { get; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Thrown by every load: the environment's Dataverse cannot be read.</summary>
        public Exception? Down { get; set; }
        public int Loads;

        public void Add(string environmentId, ConnectionDesign design, EnvironmentSettings settings)
        {
            if (!ByEnvironment.TryGetValue(environmentId, out var list))
                ByEnvironment[environmentId] = list = new();
            list.Add((design, settings));
        }

        public void Fail(string environmentId, string failure)
        {
            if (!Failures.TryGetValue(environmentId, out var list))
                Failures[environmentId] = list = new();
            list.Add(failure);
        }

        public Task<DesignLoad> LoadAsync(string environmentId, CancellationToken ct)
        {
            Interlocked.Increment(ref Loads);
            if (Down != null)
                throw Down;
            return Task.FromResult(new DesignLoad(
                ByEnvironment.TryGetValue(environmentId, out var list) ? list.ToList() : new(),
                Failures.TryGetValue(environmentId, out var failures) ? failures.ToList() : new()));
        }
    }

    private sealed class CountingHandler(Counter counter) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref counter.Value);
            return base.SendAsync(request, ct);
        }
    }

    private sealed class Counter { public int Value; }

    private readonly FakeDesigns _designs = new();
    private readonly UnreadableSecrets _secretStore = new(new InMemoryInstanceSecretStore());
    private InMemoryInstanceSecretStore _secrets => _secretStore.Inner;

    /// <summary>The host's instance secret store: <see cref="_secrets"/>, unless <see cref="Unreadable"/> makes every read fail as undecryptable.</summary>
    private sealed class UnreadableSecrets(InMemoryInstanceSecretStore inner) : IInstanceSecretStore
    {
        public InMemoryInstanceSecretStore Inner => inner;
        public bool Unreadable { get; set; }
        public Task<string?> GetAsync(string environmentId, string instance, string name, CancellationToken ct) =>
            Unreadable ? throw new CredentialUnreadableException($"Instance secret {name} of {instance} could not be decrypted.") : inner.GetAsync(environmentId, instance, name, ct);
        public Task SetAsync(string environmentId, string instance, string name, string value, CancellationToken ct) => inner.SetAsync(environmentId, instance, name, value, ct);
        public Task<bool> ExistsAsync(string environmentId, string instance, string name, CancellationToken ct) => inner.ExistsAsync(environmentId, instance, name, ct);
        public Task DeleteAsync(string environmentId, string instance, string name, CancellationToken ct) => inner.DeleteAsync(environmentId, instance, name, ct);
    }
    private readonly Counter _sent = new();
    private readonly Counter _tokenCalls = new();
    private readonly List<IDisposable> _owned = new();

    public Task InitializeAsync() => mock.ResetAsync();

    public async Task DisposeAsync()
    {
        foreach (var o in _owned)
            o.Dispose();
        foreach (var m in _mocks)
            await m.DisposeAsync();
    }

    private readonly List<MockApiFixture> _mocks = new();

    private HostFactory Host(params (string, string?)[] edits)
    {
        Environment.SetEnvironmentVariable("BF_TEST_CONNECT_SECRET", "bf-test-connect-secret");
        var settings = new Dictionary<string, string?>
        {
            ["Trust:AllowedCallers:0:EnvironmentId"] = EnvA,
            ["Trust:AllowedCallers:1:EnvironmentId"] = EnvB,
            ["Connect:PublicBaseUrl"] = Tickets.ConnectBase,
            ["Connect:ClientId"] = "connect-test-client",
            ["Connect:ClientSecretRef"] = "env:BF_TEST_CONNECT_SECRET",
            ["Credentials:InMemory"] = "true",
            // The mock runs on loopback: the design egress rule is relaxed by name and flag, never switched off.
            ["Config:Designs:AllowedPrivateHosts:0"] = "127.0.0.1",
            ["Config:Designs:AllowLoopbackForTests"] = "true",
        };
        foreach (var (k, v) in edits)
            settings[k] = v;
        var host = new HostFactory(mock.BaseUrl, extra: settings, services: s =>
        {
            ConnectTests.TestSignIn(s);
            s.AddSingleton<IDesignSource>(_designs);
            s.AddSingleton<IInstanceSecretStore>(_secretStore);
            // Stands in for the host's managed identity, so no test reaches Azure.
            s.AddSingleton<Azure.Core.TokenCredential>(_hostIdentity);
            // The connect app's client authentication, so tests can see where it is sent.
            s.AddSingleton<BrokerForge.Auth.PerUser.IConnectAppClient>(new FakeConnectApp());
            s.AddHttpClient("sources").AddHttpMessageHandler(() => new CountingHandler(_sent));
            s.AddHttpClient("tokens").AddHttpMessageHandler(() => new CountingHandler(_tokenCalls)).AddHttpMessageHandler(() => new TokenTrap(_tokenPosts));
        });
        _owned.Add(host);
        return host;
    }

    private static Dictionary<string, string> Descriptors(string folder) =>
        Directory.GetFiles(Path.Combine(MockApiFixture.RepoRoot, "descriptors", folder), "*.yaml")
            .ToDictionary(f => Path.GetFileNameWithoutExtension(f), File.ReadAllText);

    private static string Schema(string name) => File.ReadAllText(Path.Combine(MockApiFixture.RepoRoot, "schemas", name + ".yaml"));

    /// <summary>The Inventory mock's descriptors and schema as a design: Static, the key as the api_key query parameter.</summary>
    public static ConnectionDesign Warehouse(string instance = "WarehouseAPI", string queryParam = "api_key", int version = 3) => new(
        1, instance, version, "rest", "openapi: 3.0.0", new Dictionary<string, string>(),
        new DesignAuth("Static", "apiKeyQuery", null, queryParam, null, null, null, null, null, new[] { "apiKey" }),
        new DesignObjectMap("ext_item", "reversible", "Int32", new Dictionary<string, string>
        {
            ["RetrieveMultiple"] = "listItems", ["Retrieve"] = "getItem", ["Create"] = "createItem", ["Update"] = "updateItem", ["Delete"] = "deleteItem",
        }),
        Schema("InventoryAPI"), Descriptors("InventoryAPI"));

    private ConnectionDesign TicketsDesign() => new(
        1, "TicketsAPI", 1, "rest", "openapi: 3.0.0", new Dictionary<string, string>(),
        new DesignAuth("OAuth", null, null, null, null, null, null, null,
            new DesignOAuth(mock.BaseUrl + "/tk/oauth/authorize", mock.BaseUrl + "/tk/oauth/token", new[] { "tickets.read" }, Tickets.ClientId, false),
            Array.Empty<string>()),
        new DesignObjectMap("ext_ticket", "reversible", "String", new Dictionary<string, string>
        {
            ["RetrieveMultiple"] = "listTickets", ["Retrieve"] = "getTicket", ["Create"] = "createTicket",
        }),
        Schema("TicketsAPI"), Descriptors("TicketsAPI"));

    private static EnvironmentSettings At(string? baseUrl, params (string, string)[] overrides) =>
        new(baseUrl, overrides.ToDictionary(o => o.Item1, o => o.Item2));

    private static CanonicalRequest List(string table, string instance, string environment, string? user = "user-1")
    {
        var request = HostFactory.Request(table, Messages.RetrieveMultiple, instance);
        request.EnvironmentId = environment;
        request.Query = new CanonicalQuery { Page = new PageRequest { Size = 50 } };
        request.Caller = new CallerContext { UserObjectId = user };
        return request;
    }

    private static string TokenFor(string environment) => HostFactory.Token(azp: environment == EnvA ? HostFactory.PluginApp : HostFactory.OtherApp);

    private static Task<(int Status, CanonicalResponse? Response, ErrorPayload? Error)> Run(HostFactory host, CanonicalRequest request) =>
        host.Execute(request, TokenFor(request.EnvironmentId!));

    private static string[] Names(CanonicalResponse response) => response.Rows.Select(r => r.Cells.Single(c => c.Col == "ext_name").Value!).ToArray();

    private async Task KeyFor(string environment, string instance = "WarehouseAPI") =>
        await _secrets.SetAsync(environment, instance, "apiKey", MockApiFixture.InventoryKey, default);

    [Fact]
    public async Task DesignInstanceServesItsEnvironment()
    {
        _designs.Add(EnvA, Warehouse(), At(mock.BaseUrl));
        await KeyFor(EnvA);
        var (status, response, error) = await Run(Host(), List("ext_item", "WarehouseAPI", EnvA));
        Assert.True(status == 200, error?.Message);
        Assert.NotEmpty(response!.Rows);
    }

    [Fact]
    public async Task DesignInstanceUnknownInOtherEnvironment()
    {
        _designs.Add(EnvA, Warehouse(), At(mock.BaseUrl));
        await KeyFor(EnvA);
        var (status, _, error) = await Run(Host(), List("ext_item", "WarehouseAPI", EnvB));
        Assert.Equal(400, status);
        Assert.Equal(nameof(UnsupportedQueryException), error!.Error);
        Assert.Equal("Table ext_item is not mapped to a BrokerForge source.", error.Message);
    }

    [Fact]
    public async Task SameInstanceInTwoEnvironmentsStaysSeparate()
    {
        var other = new MockApiFixture();
        _mocks.Add(other);
        await other.InitializeAsync();
        using (var http = new HttpClient())
            (await http.PostAsync(other.BaseUrl + "/inv/v1/items?api_key=" + MockApiFixture.InventoryKey,
                new StringContent("{\"name\":\"Only in B\"}", System.Text.Encoding.UTF8, "application/json"))).EnsureSuccessStatusCode();
        _designs.Add(EnvA, Warehouse(), At(mock.BaseUrl));
        _designs.Add(EnvB, Warehouse(), At(other.BaseUrl));
        await KeyFor(EnvA);
        await KeyFor(EnvB);
        var host = Host();

        var a = await Run(host, List("ext_item", "WarehouseAPI", EnvA));
        var b = await Run(host, List("ext_item", "WarehouseAPI", EnvB));
        Assert.True(a.Status == 200, a.Error?.Message);
        Assert.True(b.Status == 200, b.Error?.Message);
        Assert.DoesNotContain("Only in B", Names(a.Response!));
        Assert.Contains("Only in B", Names(b.Response!));
    }

    [Fact]
    public async Task InstanceSecretIsPerEnvironment()
    {
        _designs.Add(EnvA, Warehouse(), At(mock.BaseUrl));
        _designs.Add(EnvB, Warehouse(), At(mock.BaseUrl));
        await KeyFor(EnvA);
        var (status, _, error) = await Run(Host(), List("ext_item", "WarehouseAPI", EnvB));
        Assert.Equal(500, status);
        Assert.Equal("ConfigurationError", error!.Error);
        Assert.Equal("Service instance WarehouseAPI needs its key `apiKey` in this environment: an administrator sets it on the BrokerForge secret page.", error.Message);
        Assert.Equal(0, _sent.Value);
    }

    private ConnectionDesign OAuthStoreDesign() => TicketsDesign() with
    {
        Auth = TicketsDesign().Auth with { SecretNames = new[] { "clientSecret" } },
    };

    private async Task ExpiredAliceToken(HostFactory host)
    {
        var tokens = await Tickets.SignInAsync(mock.BaseUrl, "alice");
        await host.Services.GetRequiredService<IUserCredentialStore>().SaveAsync($"{Guid.Parse(EnvA):N}_TicketsAPI", Tickets.AliceOid,
            Tickets.OAuthCredential(tokens, DateTimeOffset.UtcNow.AddSeconds(-5)), default);
    }

    [Fact]
    public async Task DesignOAuthClientSecretFromStore()
    {
        _designs.Add(EnvA, OAuthStoreDesign(), At(mock.BaseUrl));
        await _secrets.SetAsync(EnvA, "TicketsAPI", "clientSecret", Tickets.ClientSecret, default);
        var host = Host();
        await ExpiredAliceToken(host);

        // The refresh reaches the mock's token endpoint with the stored client secret, which the mock checks.
        var (status, response, error) = await Run(host, List("ext_ticket", "TicketsAPI", EnvA, Tickets.AliceOid));
        Assert.True(status == 200, error?.Message);
        Assert.NotEmpty(response!.Rows);
        var saved = await host.Services.GetRequiredService<IUserCredentialStore>().GetAsync($"{Guid.Parse(EnvA):N}_TicketsAPI", Tickets.AliceOid, default);
        Assert.True(saved!.ExpiresAt > DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task MissingStoredClientSecretFailsBeforeTokenCall()
    {
        _designs.Add(EnvA, OAuthStoreDesign(), At(mock.BaseUrl));
        var host = Host();
        await ExpiredAliceToken(host);
        var (status, _, error) = await Run(host, List("ext_ticket", "TicketsAPI", EnvA, Tickets.AliceOid));
        Assert.Equal(500, status);
        Assert.Equal("ConfigurationError", error!.Error);
        Assert.Equal("Service instance TicketsAPI needs its key `clientSecret` in this environment: an administrator sets it on the BrokerForge secret page.", error.Message);
        Assert.Equal(0, _sent.Value);
        Assert.Equal(0, _tokenCalls.Value);
    }

    /// <summary>Runs the connect page sign-in for alice up to the callback, and returns the callback's status and body.</summary>
    private async Task<(HttpStatusCode Status, string Body)> ConnectCallback(HostFactory host)
    {
        var browser = host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        browser.DefaultRequestHeaders.Add(ConnectTests.OidHeader, Tickets.AliceOid);
        var path = $"/connect/{EnvA}/TicketsAPI";
        var html = await (await browser.GetAsync(path)).Content.ReadAsStringAsync();
        var token = HttpUtility.HtmlDecode(AntiforgeryField().Match(html).Groups[1].Value);
        var start = await browser.PostAsync(path + "/start", new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token }));
        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        var form = HttpUtility.ParseQueryString(start.Headers.Location!.Query).AllKeys.ToDictionary(k => k!, k => HttpUtility.ParseQueryString(start.Headers.Location!.Query)[k]!);
        form["user"] = "alice";
        form["password"] = Tickets.Password("alice");
        using var plain = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        var signedIn = await plain.PostAsync(mock.BaseUrl + "/tk/oauth/authorize", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.Redirect, signedIn.StatusCode);

        var callback = await browser.GetAsync(signedIn.Headers.Location!.PathAndQuery);
        return (callback.StatusCode, await callback.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ConnectCallbackWithMissingClientSecretLogsTheKeyUnderTheReferenceShown()
    {
        _designs.Add(EnvA, OAuthStoreDesign(), At(mock.BaseUrl));
        var host = Host();
        var (status, body) = await ConnectCallback(host);
        Assert.Equal(HttpStatusCode.InternalServerError, status);
        Assert.Contains("TicketsAPI", body);
        var reference = System.Text.RegularExpressions.Regex.Match(body, "reference ([0-9A-Za-z:]+)").Groups[1].Value;
        Assert.NotEmpty(reference);
        Assert.Contains(host.Logs, l => l.Contains(reference) && l.Contains("needs its key `clientSecret`"));
        Assert.Equal(0, _tokenCalls.Value);
        Assert.DoesNotContain(Tickets.ClientSecret, body);
    }

    [Fact]
    public async Task ConnectCallbackWithUnreadableClientSecretIs503()
    {
        _designs.Add(EnvA, OAuthStoreDesign(), At(mock.BaseUrl));
        await _secrets.SetAsync(EnvA, "TicketsAPI", "clientSecret", Tickets.ClientSecret, default);
        _secretStore.Unreadable = true;
        var host = Host();
        var (status, body) = await ConnectCallback(host);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Contains("TicketsAPI", body);
        Assert.Contains("clientSecret", body);
        Assert.Contains("unreadable", body);
        Assert.Equal(0, _tokenCalls.Value);
        Assert.DoesNotContain(Tickets.ClientSecret, body);
        Assert.DoesNotContain(host.Logs, l => l.Contains(Tickets.ClientSecret));
    }

    [Fact]
    public async Task SecretValueNeverInMessages()
    {
        const string wrong = "bf-test-wrong-stored-value-7f3a";
        // A wrong stored API key: the source refuses it.
        _designs.Add(EnvA, Warehouse(), At(mock.BaseUrl));
        _designs.Add(EnvA, OAuthStoreDesign(), At(mock.BaseUrl));
        _designs.Add(EnvB, Warehouse(), At(mock.BaseUrl));
        await _secrets.SetAsync(EnvA, "WarehouseAPI", "apiKey", wrong, default);
        await _secrets.SetAsync(EnvA, "TicketsAPI", "clientSecret", wrong, default);
        var host = Host();
        await ExpiredAliceToken(host);

        var failures = new[]
        {
            await Run(host, List("ext_item", "WarehouseAPI", EnvA)),
            await Run(host, List("ext_ticket", "TicketsAPI", EnvA, Tickets.AliceOid)),
            // Missing key in another environment.
            await Run(host, List("ext_item", "WarehouseAPI", EnvB)),
        };
        Assert.All(failures.Take(2), f => Assert.NotEqual(200, f.Status));
        Assert.Equal(new[] { 502, 500, 500 }, failures.Select(f => f.Status).ToArray());
        foreach (var f in failures)
            Assert.DoesNotContain(wrong, System.Text.Json.JsonSerializer.Serialize(f.Error) + System.Text.Json.JsonSerializer.Serialize(f.Response));
        Assert.DoesNotContain(host.Logs, line => line.Contains(wrong));
    }

    [Fact]
    public async Task NameClashBetweenFilesAndDesignFails()
    {
        _designs.Add(EnvA, Warehouse("OrdersAPI"), At(mock.BaseUrl));
        await KeyFor(EnvA, "OrdersAPI");
        var host = Host();
        // The design is left out, never the file instance, and neither is picked for the design's table.
        var orders = await Run(host, List("ext_order", "OrdersAPI", EnvA));
        Assert.True(orders.Status == 200, orders.Error?.Message);
        var (status, _, error) = await Run(host, List("ext_item", "OrdersAPI", EnvA));
        Assert.Equal(500, status);
        Assert.Equal("ConfigurationError", error!.Error);
        Assert.Contains($"Instance OrdersAPI is configured both in host settings and as a connection design in environment {EnvA}", error.Message);
    }

    [Fact]
    public async Task MissingBaseUrlFailsNamingTheVariable()
    {
        _designs.Add(EnvA, Warehouse(), At(null));
        await KeyFor(EnvA);
        var (status, _, error) = await Run(Host(), List("ext_item", "WarehouseAPI", EnvA));
        Assert.Equal(500, status);
        Assert.Equal("ConfigurationError", error!.Error);
        Assert.Equal("Service instance WarehouseAPI needs its base URL in this environment: set environment variable bf_WarehouseAPI_BaseUrl.", error.Message);
        Assert.Equal(0, _sent.Value);
    }

    [Fact]
    public async Task FileInstancesUnchanged()
    {
        _designs.Add(EnvA, Warehouse(), At(mock.BaseUrl));
        await KeyFor(EnvA);
        var (status, response, error) = await Run(Host(), List("ext_order", "OrdersAPI", EnvA));
        Assert.True(status == 200, error?.Message);
        Assert.Equal(50, response!.Rows.Count);
    }

    [Fact]
    public async Task AuthOverrideReplacesField()
    {
        _designs.Add(EnvA, Warehouse(queryParam: "wrong_param"), At(mock.BaseUrl, ("queryParam", "api_key")));
        await KeyFor(EnvA);
        var (status, response, error) = await Run(Host(), List("ext_item", "WarehouseAPI", EnvA));
        Assert.True(status == 200, error?.Message);
        Assert.NotEmpty(response!.Rows);
    }

    [Fact]
    public async Task UnknownAuthOverrideFails()
    {
        _designs.Add(EnvA, Warehouse(), At(mock.BaseUrl, ("colour", "blue")));
        var (status, _, error) = await Run(Host(), List("ext_item", "WarehouseAPI", EnvA));
        Assert.Equal(500, status);
        Assert.Equal("ConfigurationError", error!.Error);
        Assert.Contains("colour", error.Message);
        Assert.Contains("WarehouseAPI", error.Message);
    }

    [Theory]
    [InlineData("mode", "ServiceAccount")]
    [InlineData("asyncContext", "TriggeringUser")]
    public async Task ModeAndAsyncContextCannotBeOverridden(string key, string value)
    {
        _designs.Add(EnvA, Warehouse(), At(mock.BaseUrl, (key, value)));
        await KeyFor(EnvA);
        var (status, _, error) = await Run(Host(), List("ext_item", "WarehouseAPI", EnvA));
        Assert.Equal(500, status);
        Assert.Equal("ConfigurationError", error!.Error);
        Assert.Contains($"bf_WarehouseAPI_Auth in environment {EnvA} overrides `{key}`", error.Message);
        Assert.Equal(0, _sent.Value);
    }

    [Fact]
    public void DesignProblemsNameTheDesignVersion()
    {
        var design = Warehouse(version: 7);
        var broken = new Dictionary<string, string>(design.Descriptors) { ["listItems"] = "operation: listItems\nversion: 1\nbinds: Nonsense\n" };
        var input = DesignConfigInput.Merge(new ConfigInput(), EnvA, new[] { (design with { Descriptors = broken }, At("https://inv.example")) });
        var ex = Assert.Throws<ConfigurationException>(() => new HostConfigStore(input));
        Assert.Contains("WarehouseAPI/listItems.yaml (design v7)", ex.Message);
        Assert.Contains(EnvA, ex.Message);
    }

    [Fact]
    public async Task DesignConnectUrlCarriesEnvironment()
    {
        _designs.Add(EnvA, TicketsDesign(), At(mock.BaseUrl));
        var (status, _, error) = await Run(Host(), List("ext_ticket", "TicketsAPI", EnvA, Tickets.AliceOid));
        Assert.Equal(401, status);
        Assert.Equal(nameof(ConnectionRequiredException), error!.Error);
        Assert.Equal($"{Tickets.ConnectBase}/connect/{EnvA}/TicketsAPI", error.ConnectUrl);
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" value=\"([^\"]+)\"")]
    private static partial Regex AntiforgeryField();

    [Fact]
    public async Task DesignConnectPageStoresUnderTheEnvironment()
    {
        var sso = TicketsDesign() with { Auth = new DesignAuth("Sso", null, null, null, null, null, "basic", null, null, Array.Empty<string>()) };
        _designs.Add(EnvA, sso, At(mock.BaseUrl));
        var host = Host();
        var browser = host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        browser.DefaultRequestHeaders.Add(ConnectTests.OidHeader, Tickets.AliceOid);

        var page = await browser.GetAsync($"/connect/{EnvA}/TicketsAPI");
        var html = await page.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains($"action=\"/connect/{EnvA}/TicketsAPI/sso\"", html);
        var token = HttpUtility.HtmlDecode(AntiforgeryField().Match(html).Groups[1].Value);
        var saved = await browser.PostAsync($"/connect/{EnvA}/TicketsAPI/sso", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = "alice", ["secret"] = Tickets.AlicePassword, ["__RequestVerificationToken"] = token,
        }));
        Assert.Equal(HttpStatusCode.SeeOther, saved.StatusCode);
        Assert.Equal($"/connect/{EnvA}/TicketsAPI", saved.Headers.Location!.ToString());

        var store = host.Services.GetRequiredService<IUserCredentialStore>();
        Assert.NotNull(await store.GetAsync($"{Guid.Parse(EnvA):N}_TicketsAPI", Tickets.AliceOid, default));
        Assert.Null(await store.GetAsync("TicketsAPI", Tickets.AliceOid, default));
        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync("/connect/TicketsAPI")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync($"/connect/{EnvB}/TicketsAPI")).StatusCode);

        var (status, response, error) = await Run(host, List("ext_ticket", "TicketsAPI", EnvA, Tickets.AliceOid));
        Assert.True(status == 200, error?.Message);
        Assert.NotEmpty(response!.Rows);
        Assert.Contains(host.Logs, l => l.Contains($"Connected Sso for TicketsAPI in environment {EnvA} user {Tickets.AliceOid}"));
        Assert.All(host.Logs.Where(l => l.Contains("Executing RetrieveMultiple")), l => Assert.Contains($"environmentId={EnvA}", l));
        Assert.DoesNotContain(host.Logs, l => l.Contains(Tickets.AlicePassword));
    }

    [Fact]
    public async Task TwoDesignsMappingSameTableFail()
    {
        _designs.Add(EnvA, Warehouse(), At(mock.BaseUrl));
        _designs.Add(EnvA, Warehouse("StockAPI"), At(mock.BaseUrl));
        await KeyFor(EnvA);
        await KeyFor(EnvA, "StockAPI");
        var host = Host();
        // Neither design is picked: both are left out.
        foreach (var instance in new[] { "WarehouseAPI", "StockAPI" })
        {
            var (status, _, error) = await Run(host, List("ext_item", instance, EnvA));
            Assert.Equal(500, status);
            Assert.Equal("ConfigurationError", error!.Error);
            Assert.Contains($"Table ext_item is mapped by both connection designs StockAPI and WarehouseAPI in environment {EnvA}", error.Message);
        }
        Assert.Equal(0, _sent.Value);
        Assert.Equal(200, (await Run(host, List("ext_order", "OrdersAPI", EnvA))).Status);

        var twice = DesignConfigInput.Merge(new ConfigInput(), EnvA, new[] { (Warehouse(), At("https://a.example")), (Warehouse(), At("https://b.example")) });
        Assert.Contains($"Instance WarehouseAPI has two connection designs in environment {EnvA}", Assert.Throws<ConfigurationException>(() => new HostConfigStore(twice)).Message);
    }

    [Fact]
    public async Task ConnectForUnknownEnvironmentIs404WithoutLoading()
    {
        const string unknown = "00000000-0000-0000-0000-00000000000c";
        _designs.Add(unknown, TicketsDesign(), At(mock.BaseUrl));
        var browser = Host().CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        browser.DefaultRequestHeaders.Add(ConnectTests.OidHeader, Tickets.AliceOid);

        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync($"/connect/{unknown}/TicketsAPI")).StatusCode);
        Assert.Equal(0, _designs.Loads);
    }

    [Fact]
    public async Task PerUserCredentialDoesNotCrossEnvironments()
    {
        var sso = TicketsDesign() with { Auth = new DesignAuth("Sso", null, null, null, null, null, "basic", null, null, Array.Empty<string>()) };
        _designs.Add(EnvA, sso, At(mock.BaseUrl));
        _designs.Add(EnvB, sso, At(mock.BaseUrl));
        var host = Host();
        var credential = new UserCredential(CredentialKind.Sso, CredentialStatus.Connected, null, null, null, "alice", Tickets.AlicePassword, DateTimeOffset.UtcNow);
        await host.Services.GetRequiredService<IUserCredentialStore>().SaveAsync($"{Guid.Parse(EnvA):N}_TicketsAPI", Tickets.AliceOid, credential, default);

        var b = await Run(host, List("ext_ticket", "TicketsAPI", EnvB, Tickets.AliceOid));
        Assert.Equal(401, b.Status);
        Assert.Equal(nameof(ConnectionRequiredException), b.Error!.Error);
        Assert.Equal($"{Tickets.ConnectBase}/connect/{EnvB}/TicketsAPI", b.Error.ConnectUrl);

        var a = await Run(host, List("ext_ticket", "TicketsAPI", EnvA, Tickets.AliceOid));
        Assert.True(a.Status == 200, a.Error?.Message);
        Assert.NotEmpty(a.Response!.Rows);
    }

    [Fact]
    public async Task ProviderCachesPerEnvironmentUntilTtlOrInvalidate()
    {
        var time = new ConnectTests.MutableTime();
        _designs.Add(EnvA, Warehouse(), At("https://inv.example"));
        var provider = new CompositeConfigProvider(new HostConfigStore(new ConfigInput()), _designs, time, TimeSpan.FromMinutes(5));

        var first = await provider.ForEnvironmentAsync(EnvA, default);
        Assert.Same(first, await provider.ForEnvironmentAsync(EnvA.ToUpperInvariant(), default));
        Assert.Equal(1, _designs.Loads);
        Assert.NotNull(first.Instance("WarehouseAPI"));
        Assert.Null((await provider.ForEnvironmentAsync(EnvB, default)).Instance("WarehouseAPI"));
        Assert.Equal(2, _designs.Loads);

        time.Now += TimeSpan.FromMinutes(6);
        Assert.NotSame(first, await provider.ForEnvironmentAsync(EnvA, default));
        Assert.Equal(3, _designs.Loads);
        provider.Invalidate(EnvA);
        await provider.ForEnvironmentAsync(EnvA, default);
        Assert.Equal(4, _designs.Loads);

        var files = new HostConfigStore(new ConfigInput());
        Assert.Same(files, await new CompositeConfigProvider(files, null, time, TimeSpan.FromMinutes(5)).ForEnvironmentAsync(EnvA, default));
    }
}
