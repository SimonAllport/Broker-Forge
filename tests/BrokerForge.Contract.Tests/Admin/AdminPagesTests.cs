using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Web;
using BrokerForge.Abstractions;
using BrokerForge.Auth.Credentials;
using BrokerForge.Connections;
using BrokerForge.Contracts;
using BrokerForge.Host.Admin;
using BrokerForge.Host.Config;
using BrokerForge.Host.Generation;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BrokerForge.Contract.Tests.Admin;

/// <summary>The host's secret and role pages, bf_ListConnections, and the connect page's design-load message (plan 4 task 5).</summary>
[Collection(MockApiCollection.Name)]
public sealed partial class AdminPagesTests(MockApiFixture mock) : IDisposable
{
    private const string Env = HostFactory.Environment1;
    private const string Org = "https://contoso-dev.crm4.dynamics.com";
    private const string Other = "00000000-0000-0000-0000-00000000a0ff";
    private const string AdminToken = "bf-test-admin-dataverse-token";
    private const string Value = "bf-test-dummy-inventory-key";

    private sealed class Exchange : IAdminTokenExchange
    {
        public List<(string Org, string Code, string Verifier, string Redirect)> Calls { get; } = new();
        public Task<string> RedeemAsync(string orgUrl, string code, string verifier, string redirectUri, CancellationToken ct)
        {
            Calls.Add((orgUrl, code, verifier, redirectUri));
            return Task.FromResult(AdminToken);
        }
    }

    private readonly AdminDataverse _dv = new();
    private readonly EnvironmentConfigTests.FakeDesigns _designs = new();
    private readonly InMemoryInstanceSecretStore _secrets = new();
    private readonly Exchange _exchange = new();
    private readonly ConnectTests.MutableTime _time = new();
    private HostFactory? _host;

    private HostFactory Host => _host ??= Create();

    private HostFactory Create()
    {
        Environment.SetEnvironmentVariable("BF_TEST_CONNECT_SECRET", "bf-test-connect-secret");
        return new HostFactory(mock.BaseUrl, extra: new Dictionary<string, string?>
        {
            ["Trust:AllowedCallers:0:OrgUrl"] = Org,
            ["Connect:PublicBaseUrl"] = Tickets.ConnectBase,
            ["Connect:ClientId"] = "connect-test-client",
            ["Connect:ClientSecretRef"] = "env:BF_TEST_CONNECT_SECRET",
            ["Credentials:InMemory"] = "true",
        }, services: s =>
        {
            ConnectTests.TestSignIn(s);
            var clients = new AdminApiTests.Clients(_dv);
            s.Replace(ServiceDescriptor.Singleton<IGenerationClientFactory>(clients));
            s.Replace(ServiceDescriptor.Singleton<IDelegatedDataverseFactory>(clients));
            s.Replace(ServiceDescriptor.Singleton<IAdminTokenExchange>(_exchange));
            s.AddSingleton<IDesignSource>(_designs);
            s.AddSingleton<IInstanceSecretStore>(_secrets);
            s.AddSingleton<TimeProvider>(_time);
        });
    }

    private HttpClient Browser(string? oid = AdminDataverse.AdminOid)
    {
        var client = Host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true, BaseAddress = new Uri("https://localhost") });
        if (oid != null)
            client.DefaultRequestHeaders.Add(ConnectTests.OidHeader, oid);
        return client;
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" value=\"([^\"]+)\"")]
    private static partial Regex AntiforgeryField();

    private static async Task<(HttpResponseMessage Page, string Html, string Token)> Page(HttpClient browser, string path)
    {
        var page = await browser.GetAsync(path);
        var html = await page.Content.ReadAsStringAsync();
        var match = AntiforgeryField().Match(html);
        return (page, html, match.Success ? HttpUtility.HtmlDecode(match.Groups[1].Value) : "");
    }

    /// <summary>The change list's fingerprint the administrator's page shows, in its form.</summary>
    private static string Fingerprint(string html) =>
        System.Text.RegularExpressions.Regex.Match(html, "name=\"fingerprint\" value=\"([0-9a-f]+)\"").Groups[1].Value;

    private static Task<HttpResponseMessage> Post(HttpClient browser, string path, string? token, params (string, string)[] fields) =>
        browser.PostAsync(path, new FormUrlEncodedContent(fields.Select(f => new KeyValuePair<string, string>(f.Item1, f.Item2))
            .Concat(token == null ? Array.Empty<KeyValuePair<string, string>>() : new[] { new KeyValuePair<string, string>("__RequestVerificationToken", token) })));

    private static string SecretPath(string env = Env, string instance = "InventoryAPI") => $"/admin/{env}/{instance}/secret";
    private static string RolePath => $"/admin/{Env}/InventoryAPI/role";

    private Guid Row() => _dv.AddRow(DesignBuilderTests.Inventory());

    [Fact]
    public async Task Secret_Admin_StoresAndShowsSet()
    {
        Row();
        var browser = Browser();
        var (page, html, token) = await Page(browser, SecretPath());
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("needs key", html);

        var saved = await Post(browser, SecretPath(), token, ("name", "key"), ("value", Value));
        Assert.Equal(HttpStatusCode.SeeOther, saved.StatusCode);
        Assert.Equal(Value, await _secrets.GetAsync(Env, "InventoryAPI", "key", CancellationToken.None));
        var (_, after, _) = await Page(browser, SecretPath());
        Assert.Contains("key: set", after);
        Assert.DoesNotContain(Value, after);
    }

    [Fact]
    public async Task Secret_NotAdmin_Forbidden()
    {
        Row();
        var (page, _, _) = await Page(Browser(Other), SecretPath());
        Assert.Equal(HttpStatusCode.Forbidden, page.StatusCode);
    }

    [Fact]
    public async Task Secret_UnknownEnvironment_NotFound()
    {
        Row();
        var (page, _, _) = await Page(Browser(), SecretPath(env: "00000000-0000-0000-0000-0000000000ee"));
        Assert.Equal(HttpStatusCode.NotFound, page.StatusCode);
    }

    [Fact]
    public async Task Secret_UnknownInstance_NotFound()
    {
        var (page, _, _) = await Page(Browser(), SecretPath(instance: "NoSuchAPI"));
        Assert.Equal(HttpStatusCode.NotFound, page.StatusCode);
    }

    [Fact]
    public async Task Secret_NameNotInDesign_NotFound()
    {
        Row();
        var browser = Browser();
        var (_, _, token) = await Page(browser, SecretPath());
        var saved = await Post(browser, SecretPath(), token, ("name", "clientSecret"), ("value", Value));
        Assert.Equal(HttpStatusCode.NotFound, saved.StatusCode);
        Assert.False(await _secrets.ExistsAsync(Env, "InventoryAPI", "clientSecret", CancellationToken.None));
    }

    [Theory]
    [InlineData("")]
    [InlineData("two\nlines")]
    public async Task Secret_BadValue_Refused(string value)
    {
        Row();
        var browser = Browser();
        var (_, _, token) = await Page(browser, SecretPath());
        var saved = await Post(browser, SecretPath(), token, ("name", "key"), ("value", value));
        Assert.Equal(HttpStatusCode.BadRequest, saved.StatusCode);
        Assert.False(await _secrets.ExistsAsync(Env, "InventoryAPI", "key", CancellationToken.None));
    }

    [Fact]
    public async Task Secret_ValueNeverLogged()
    {
        Row();
        var browser = Browser();
        var (_, _, token) = await Page(browser, SecretPath());
        await Post(browser, SecretPath(), token, ("name", "key"), ("value", Value));
        Assert.Contains(Host.Logs, l => l.Contains("instance secret key of InventoryAPI"));
        Assert.DoesNotContain(Host.Logs, l => l.Contains(Value));
    }

    [Fact]
    public async Task Secret_MissingAntiforgery_Refused()
    {
        Row();
        var saved = await Post(Browser(), SecretPath(), null, ("name", "key"), ("value", Value));
        Assert.Equal(HttpStatusCode.BadRequest, saved.StatusCode);
        Assert.False(await _secrets.ExistsAsync(Env, "InventoryAPI", "key", CancellationToken.None));
    }

    private async Task Generated(Guid id)
    {
        var request = AdminApiTests.Request("bf_Generate", id);
        request.EnvironmentId = Env;
        var (status, response, error) = await Host.Execute(request);
        Assert.True(status == 200, error?.Message);
        var job = await Host.Services.GetRequiredService<GenerationJobs>().WaitAsync(Guid.Parse(response!.Outputs.Single(o => o.Col == "JobId").Value!));
        Assert.True(job.State == JobState.Succeeded, job.Failure);
    }

    /// <summary>The role page, start, and Entra's redirect back: returns the callback path and query the browser would follow.</summary>
    private static async Task<string> StartRole(HttpClient browser)
    {
        var (page, html, token) = await Page(browser, RolePath);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("InventoryAPI user", html);
        var start = await Post(browser, RolePath + "/start", token, ("fingerprint", Fingerprint(html)));
        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        var authorize = start.Headers.Location!;
        Assert.StartsWith($"https://login.microsoftonline.com/{HostFactory.Tenant}/oauth2/v2.0/authorize?", authorize.ToString());
        var q = HttpUtility.ParseQueryString(authorize.Query);
        Assert.Equal(("connect-test-client", $"{Org}/user_impersonation", "S256", "code"), (q["client_id"], q["scope"], q["code_challenge_method"], q["response_type"]));
        Assert.Equal($"{Tickets.ConnectBase}/admin/callback", q["redirect_uri"]);
        return $"/admin/callback?code=bf-test-code&state={Uri.EscapeDataString(q["state"]!)}";
    }

    [Fact]
    public async Task Role_Callback_AppliesWithAdminToken()
    {
        var id = Row();
        await Generated(id);
        var browser = Browser();
        var callback = await browser.GetAsync(await StartRole(browser));
        var html = await callback.Content.ReadAsStringAsync();
        Assert.True(callback.StatusCode == HttpStatusCode.OK, html);
        Assert.Contains("InventoryAPI user", html);

        var (org, code, verifier, redirect) = Assert.Single(_exchange.Calls);
        Assert.Equal((Org, "bf-test-code", $"{Tickets.ConnectBase}/admin/callback"), (org, code, redirect));
        Assert.NotEmpty(verifier);
        var roleWrites = _dv.Fake.Writes.Where(w => w.Path == "roles" || w.Path.Contains("PrivilegesRole")).ToList();
        Assert.NotEmpty(roleWrites);
        Assert.All(roleWrites, w => Assert.Equal($"Bearer {AdminToken}", w.Authorization));
        Assert.Contains("prvReadext_item", _dv.Fake.RolePrivileges("InventoryAPI user").Keys);
    }

    // Plan 5: the host can't create a Custom API, so Generate leaves the design's actions to this page, with the role.
    [Fact]
    public async Task Role_Callback_CreatesPendingActionsWithAdminToken()
    {
        var id = Row();
        await Generated(id);
        Assert.DoesNotContain(_dv.Fake.Writes, w => w.Path.StartsWith("customapis", StringComparison.Ordinal));
        var browser = Browser();
        var (_, page, _) = await Page(browser, RolePath);
        Assert.Contains("add action", page);
        Assert.Contains("ext_AdjustStock", page);
        var callback = await browser.GetAsync(await StartRole(browser));
        var html = await callback.Content.ReadAsStringAsync();
        Assert.True(callback.StatusCode == HttpStatusCode.OK, html);
        var apiWrites = _dv.Fake.Writes.Where(w => w.Path.StartsWith("customapis", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(apiWrites);
        Assert.All(apiWrites, w => Assert.Equal($"Bearer {AdminToken}", w.Authorization));
        var (_, again, _) = await Page(Browser(), RolePath);
        Assert.Contains("up to date", again);
    }

    // Final review I1: the page applies only the administrator's steps; changes Generate as the host would make come first.
    [Fact]
    public async Task Role_HostChangesLeft_PageRefusesAndNamesThem()
    {
        var id = Row();
        await Generated(id);
        _dv.Fake.SetAttributeType("ext_item", "ext_onhand", "String");
        var (page, html, _) = await Page(Browser(), RolePath);
        Assert.Equal(HttpStatusCode.Conflict, page.StatusCode);
        Assert.Contains("On hand", html);
        Assert.Contains("Generate", html);
        Assert.DoesNotContain("Do it as me", html);
    }

    // Final review I2: the list confirmed is the one the page showed, not the one when the button is pressed.
    [Fact]
    public async Task Role_StartWithAnotherList_Refused()
    {
        var id = Row();
        await Generated(id);
        var browser = Browser();
        var (_, _, token) = await Page(browser, RolePath);
        var start = await Post(browser, RolePath + "/start", token, ("fingerprint", "0123456789abcdef"));
        Assert.Equal(HttpStatusCode.Conflict, start.StatusCode);
        Assert.Contains("changed", await start.Content.ReadAsStringAsync());
    }

    // Final review I3: the administrator's run takes the same lease as Generate, so the two never run at once.
    [Fact]
    public async Task Role_CallbackWhileGenerateRuns_RefusedNothingWritten()
    {
        var id = Row();
        await Generated(id);
        var browser = Browser();
        var callback = await StartRole(browser);
        var lease = Host.Services.GetRequiredService<BrokerForge.Host.Generation.IGenerationLease>();
        Assert.NotNull(await lease.TryAcquireAsync(Env, "InventoryAPI", TimeSpan.FromMinutes(5), CancellationToken.None));
        var response = await browser.GetAsync(callback);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("running", await response.Content.ReadAsStringAsync());
        Assert.DoesNotContain(_dv.Fake.Writes, w => w.Path.StartsWith("customapis", StringComparison.Ordinal));
    }

    // Plan 5 deferred minor: the list changes between the start and Entra's redirect back (here a second administrator
    // finishes first), so the callback applies nothing.
    [Fact]
    public async Task Role_ListChangedBeforeCallback_RefusedNothingWritten()
    {
        var id = Row();
        await Generated(id);
        var browser = Browser();
        var callback = await StartRole(browser);
        var other = Browser();
        var first = await other.GetAsync(await StartRole(other));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var writes = _dv.Fake.Writes.Count();
        var response = await browser.GetAsync(callback);
        var html = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Conflict, html);
        Assert.Contains("changed after the page was shown", html);
        Assert.Equal(writes, _dv.Fake.Writes.Count());
    }

    // Final review I4: once done, the row no longer says an administrator step is left.
    [Fact]
    public async Task Role_CallbackDone_RowHasNothingPending()
    {
        var id = Row();
        await Generated(id);
        Assert.Contains("create role", (string)_dv.Rows[id]["bf_generationresult"]!);
        var browser = Browser();
        var response = await browser.GetAsync(await StartRole(browser));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = System.Text.Json.Nodes.JsonNode.Parse((string)_dv.Rows[id]["bf_generationresult"]!)!;
        Assert.Empty(result["pending"]!.AsArray());
        Assert.Equal("Succeeded", (string)_dv.Rows[id]["bf_generationstate"]!);
    }

    [Fact]
    public async Task Role_CallbackOtherUser_Refused()
    {
        var id = Row();
        await Generated(id);
        var browser = Browser();
        var path = await StartRole(browser);
        browser.DefaultRequestHeaders.Remove(ConnectTests.OidHeader);
        browser.DefaultRequestHeaders.Add(ConnectTests.OidHeader, Other);
        var callback = await browser.GetAsync(path);
        Assert.Equal(HttpStatusCode.BadRequest, callback.StatusCode);
        Assert.Empty(_exchange.Calls);
    }

    [Fact]
    public async Task Role_StartWithoutAntiforgery_Refused()
    {
        var id = Row();
        await Generated(id);
        var start = await Post(Browser(), RolePath + "/start", null);
        Assert.Equal(HttpStatusCode.BadRequest, start.StatusCode);
    }

    [Fact]
    public async Task Role_CallbackInAnotherBrowser_Refused()
    {
        // No nonce cookie: the sign-in was not started in this browser.
        var id = Row();
        await Generated(id);
        var path = await StartRole(Browser());
        var callback = await Browser().GetAsync(path);
        Assert.Equal(HttpStatusCode.BadRequest, callback.StatusCode);
        Assert.Contains("not started in this browser", await callback.Content.ReadAsStringAsync());
        Assert.Empty(_exchange.Calls);
    }

    [Fact]
    public async Task Role_CallbackAfterTenMinutes_Refused()
    {
        var id = Row();
        await Generated(id);
        var browser = Browser();
        var path = await StartRole(browser);
        _time.Now += TimeSpan.FromMinutes(11);
        var callback = await browser.GetAsync(path);
        Assert.Equal(HttpStatusCode.BadRequest, callback.StatusCode);
        Assert.Contains("longer than 10 minutes", await callback.Content.ReadAsStringAsync());
        Assert.Empty(_exchange.Calls);
    }

    [Fact]
    public async Task Role_CallbackWithConnectPageState_Refused()
    {
        // A state the connect page issued (no role: prefix) is not a role sign-in.
        var protection = Host.Services.GetRequiredService<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>();
        var state = new BrokerForge.Host.Connect.ConnectState("InventoryAPI", AdminDataverse.AdminOid, "verifier", "nonce", _time.Now, Env).Protect(protection);
        var callback = await Browser().GetAsync($"/admin/callback?code=c&state={Uri.EscapeDataString(state)}");
        Assert.Equal(HttpStatusCode.BadRequest, callback.StatusCode);
        Assert.Empty(_exchange.Calls);
    }

    [Fact]
    public async Task AdminPages_RefuseFraming()
    {
        // M6: the secret and role pages are forms; another site must not frame them.
        Row();
        var (page, _, _) = await Page(Browser(), SecretPath());
        Assert.Contains("frame-ancestors 'none'", page.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("DENY", page.Headers.GetValues("X-Frame-Options").Single());
    }

    [Fact]
    public async Task Role_NotAdmin_Forbidden()
    {
        Row();
        var (page, _, _) = await Page(Browser(Other), RolePath);
        Assert.Equal(HttpStatusCode.Forbidden, page.StatusCode);
    }

    [Fact]
    public async Task Role_TokenNeverLogged()
    {
        var id = Row();
        await Generated(id);
        var browser = Browser();
        await browser.GetAsync(await StartRole(browser));
        Assert.Contains(Host.Logs, l => l.Contains("Administrator step for InventoryAPI") && l.Contains("create role InventoryAPI user"));
        Assert.DoesNotContain(Host.Logs, l => l.Contains(AdminToken));
    }

    private async Task<JsonArray> List()
    {
        var request = HostFactory.Request(null, "bf_ListConnections", null);
        request.Caller = new CallerContext { UserObjectId = AdminDataverse.AdminOid };
        var (status, response, error) = await Host.Execute(request);
        Assert.True(status == 200, error?.Message);
        return JsonNode.Parse(response!.Outputs.Single(o => o.Col == "Connections").Value!)!.AsArray();
    }

    [Fact]
    public async Task List_ImportedDesignReadOnlyNeedsKey()
    {
        var row = DesignBuilderTests.Inventory("StockAPI");
        var design = DesignBuilder.Build(row, DesignBuilder.Review(row)) with { Version = 3 };
        _designs.Add(Env, design, new EnvironmentSettings("https://inventory.contoso.example", new Dictionary<string, string>()));
        var entry = Assert.Single(await List())!;
        Assert.Equal("StockAPI", (string?)entry["name"]);
        Assert.True((bool)entry["readOnly"]!);
        Assert.Null(entry["rowId"]);
        Assert.Equal("needs key", (string?)entry["status"]);
        Assert.Equal(3, (int?)entry["designVersion"]);
        Assert.Equal("bf_StockAPI_BaseUrl", (string?)entry["baseUrlVariable"]);
        Assert.Equal($"{Tickets.ConnectBase}/admin/{Env}/StockAPI/secret", (string?)entry["secretUrl"]);

        await _secrets.SetAsync(Env, "StockAPI", "key", Value, CancellationToken.None);
        Assert.Equal("ready", (string?)Assert.Single(await List())!["status"]);
    }

    [Fact]
    public async Task List_ImportedDesignWithoutBaseUrl_NeedsUrl()
    {
        var row = DesignBuilderTests.Inventory("StockAPI");
        _designs.Add(Env, DesignBuilder.Build(row, DesignBuilder.Review(row)) with { Version = 1 }, new EnvironmentSettings(null, new Dictionary<string, string>()));
        Assert.Equal("needs URL", (string?)Assert.Single(await List())!["status"]);
    }

    [Fact]
    public async Task List_DuplicateNames_BothShownNamedAsDuplicates()
    {
        // Final review I2: the list must not fail; both rows show, each saying the name is taken twice.
        Row();
        _dv.AddRow(DesignBuilderTests.Inventory("inventoryapi") with { Id = Guid.Parse("00000000-0000-0000-0000-00000000c002") });
        var list = await List();
        Assert.Equal(2, list.Count);
        Assert.All(list, e => Assert.Equal("duplicate name", (string?)e!["status"]));
    }

    [Fact]
    public async Task Secret_DuplicateName_Conflict()
    {
        Row();
        _dv.AddRow(DesignBuilderTests.Inventory("inventoryapi") with { Id = Guid.Parse("00000000-0000-0000-0000-00000000c002") });
        var (page, html, _) = await Page(Browser(), SecretPath());
        Assert.Equal(HttpStatusCode.Conflict, page.StatusCode);
        Assert.Contains("2 connections are called InventoryAPI", html);
    }

    [Fact]
    public async Task List_RowWithoutDesign_NotGenerated()
    {
        var id = Row();
        var entry = Assert.Single(await List())!;
        Assert.Equal(id.ToString("D"), (string?)entry["rowId"]);
        Assert.False((bool)entry["readOnly"]!);
        Assert.Equal("not generated", (string?)entry["status"]);
    }

    [Fact]
    public async Task Secret_ImportedDesign_NamesFromDesign()
    {
        var row = DesignBuilderTests.Inventory("StockAPI");
        _designs.Add(Env, DesignBuilder.Build(row, DesignBuilder.Review(row)) with { Version = 1 }, new EnvironmentSettings("https://inventory.contoso.example", new Dictionary<string, string>()));
        var (page, html, _) = await Page(Browser(), SecretPath(instance: "StockAPI"));
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("key: needs key", html);
    }

    [Fact]
    public async Task Connect_DesignsFailToLoad_MessagePage()
    {
        _designs.Down = new ConfigurationException("Environment cannot be read: contoso test failure.");
        var page = await Browser().GetAsync($"/connect/{Env}/TicketsAPI");
        var html = await page.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.InternalServerError, page.StatusCode);
        Assert.StartsWith("text/html", page.Content.Headers.ContentType!.ToString());
        Assert.Contains("<h1>Not available</h1>", html);
        Assert.Contains("contoso test failure", html);
    }

    public void Dispose() => _host?.Dispose();
}
