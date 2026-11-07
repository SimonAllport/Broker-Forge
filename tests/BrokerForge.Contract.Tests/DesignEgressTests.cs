using BrokerForge.Contracts;

namespace BrokerForge.Contract.Tests;

/// <summary>
/// A connection design cannot reach internal addresses (HLD D-34, final re-review SSRF): its base URL and OAuth URLs
/// may not be loopback, link-local, private or carrier-grade NAT, by literal or by what a name resolves to, unless the
/// host is listed in Config:Designs:AllowedPrivateHosts. File instances are unaffected.
/// </summary>
public partial class EnvironmentConfigTests
{
    private int MockPort => new Uri(mock.BaseUrl).Port;

    /// <summary>A host without the test allow list and loopback flag.</summary>
    private HostFactory StrictHost(params (string, string?)[] edits) =>
        Host(new (string, string?)[] { ("Config:Designs:AllowedPrivateHosts:0", null), ("Config:Designs:AllowLoopbackForTests", null) }.Concat(edits).ToArray());

    [Theory]
    [InlineData("http://169.254.169.254/metadata")]
    [InlineData("https://169.254.169.254")]
    [InlineData("https://10.0.0.5")]
    [InlineData("https://172.20.1.1")]
    [InlineData("https://192.168.1.1")]
    [InlineData("https://100.64.0.1")]
    [InlineData("https://0.0.0.0")]
    [InlineData("https://127.0.0.1")]
    [InlineData("https://[::1]")]
    [InlineData("https://[::ffff:169.254.169.254]")]
    [InlineData("https://[fe80::1]")]
    [InlineData("https://[fd00::1]")]
    public async Task DesignBaseUrlPrivateLiteralRefused(string baseUrl)
    {
        _designs.Add(EnvA, Warehouse(), At(baseUrl));
        await KeyFor(EnvA);
        var host = StrictHost();
        AssertDesignFailure(await Run(host, List("ext_item", "WarehouseAPI", EnvA)), "WarehouseAPI", "AllowedPrivateHosts");
        Assert.Equal(0, _sent.Value);
        // The file instances are not touched by the rule.
        await AssertOrdersServe(host);
    }

    [Fact]
    public async Task DesignHttpBaseUrlRefusedUnlessListed()
    {
        _designs.Add(EnvA, Warehouse(), At($"http://inventory.contoso.example:{MockPort}"));
        AssertDesignFailure(await Run(StrictHost(), List("ext_item", "WarehouseAPI", EnvA)), "bf_WarehouseAPI_BaseUrl", "https");
    }

    [Fact]
    public async Task DesignHostResolvingToLoopbackRefusedAtConnect()
    {
        // localhost is a name, so only the connection can find out it is loopback.
        _designs.Add(EnvA, Warehouse(), At($"https://localhost:{MockPort}"));
        await KeyFor(EnvA);
        var (status, _, error) = await Run(StrictHost(), List("ext_item", "WarehouseAPI", EnvA));
        Assert.Equal(500, status);
        Assert.Equal("ConfigurationError", error!.Error);
        Assert.Equal($"Service instance WarehouseAPI: host localhost resolves to a private address; list it in Config:Designs:AllowedPrivateHosts to allow.", error.Message);
        Assert.DoesNotContain(MockApiFixture.InventoryKey, error.Message);
    }

    [Fact]
    public async Task DesignAllowListedHostWorks()
    {
        _designs.Add(EnvA, Warehouse(), At($"http://localhost:{MockPort}"));
        await KeyFor(EnvA);
        var (status, response, error) = await Run(StrictHost(("Config:Designs:AllowedPrivateHosts:0", "LOCALHOST")), List("ext_item", "WarehouseAPI", EnvA));
        Assert.True(status == 200, error?.Message);
        Assert.NotEmpty(response!.Rows);
    }

    [Fact]
    public async Task FileInstanceToLoopbackUnaffected()
    {
        _designs.Add(EnvA, Warehouse(), At("https://169.254.169.254"));
        await AssertOrdersServe(StrictHost());
    }

    [Fact]
    public async Task DesignTokenUrlResolvingToLoopbackRefusedAtConnect()
    {
        var design = OAuthStoreDesign();
        var oauth = design.Auth.OAuth! with { TokenUrl = $"https://localhost:{MockPort}/tk/oauth/token" };
        _designs.Add(EnvA, design with { Auth = design.Auth with { OAuth = oauth } }, At(mock.BaseUrl));
        await _secrets.SetAsync(EnvA, "TicketsAPI", "clientSecret", Tickets.ClientSecret, default);
        var host = Host();
        await ExpiredAliceToken(host);
        var (status, _, error) = await Run(host, List("ext_ticket", "TicketsAPI", EnvA, Tickets.AliceOid));
        Assert.Equal(500, status);
        Assert.Equal("ConfigurationError", error!.Error);
        Assert.Contains("Service instance TicketsAPI: host localhost resolves to a private address", error.Message);
        Assert.DoesNotContain(Tickets.ClientSecret, error.Message);
    }

    [Fact]
    public async Task ConnectCallbackWithTokenUrlRefusedGivesAReferenceNotTheKey()
    {
        // Found live in plan 5: the token URL's host resolved to a private address, and the page said the key was not set.
        // The page is for end users, so it names no host (deferred minor): it gives a reference the log line carries.
        var design = OAuthStoreDesign();
        var oauth = design.Auth.OAuth! with { TokenUrl = $"https://localhost:{MockPort}/tk/oauth/token" };
        _designs.Add(EnvA, design with { Auth = design.Auth with { OAuth = oauth } }, At(mock.BaseUrl));
        await _secrets.SetAsync(EnvA, "TicketsAPI", "clientSecret", Tickets.ClientSecret, default);
        var (status, body) = await ConnectCallback(Host());
        Assert.Equal(System.Net.HttpStatusCode.InternalServerError, status);
        Assert.Contains("configuration problem", body);
        Assert.Matches("reference [0-9A-Za-z:]+", body);
        Assert.DoesNotContain("localhost", body);
        Assert.DoesNotContain("is not set", body);
        Assert.DoesNotContain(Tickets.ClientSecret, body);
    }

    [Fact]
    public async Task OAuthDesignLoopbackRefusedWithoutTestFlag()
    {
        _designs.Add(EnvA, TicketsDesign(), At(mock.BaseUrl));
        var result = await Run(Host(("Config:Designs:AllowLoopbackForTests", null)), List("ext_ticket", "TicketsAPI", EnvA, Tickets.AliceOid));
        AssertDesignFailure(result, "TicketsAPI", "auth.oauth.authorizeUrl", "https");
    }

    [Theory]
    [InlineData("Metadata")]
    [InlineData("X-IDENTITY-HEADER")]
    [InlineData("x-identity-header")]
    public async Task ReservedHeadersRefused(string header)
    {
        var design = Warehouse();
        var list = design.Descriptors["listItems"].Replace("  path: \"/inv/v1/items\"\n", $"  path: \"/inv/v1/items\"\n  headers:\n    {header}: {{ static: \"true\" }}\n");
        Assert.Contains(header, list);
        _designs.Add(EnvA, design with { Descriptors = new Dictionary<string, string>(design.Descriptors) { ["listItems"] = list } }, At(mock.BaseUrl));
        await KeyFor(EnvA);
        AssertDesignFailure(await Run(Host(), List("ext_item", "WarehouseAPI", EnvA)), $"`{header}` is reserved");
        Assert.Equal(0, _sent.Value);
    }
}
