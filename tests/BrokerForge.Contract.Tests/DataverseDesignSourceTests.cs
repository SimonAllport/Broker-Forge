using System.Net;
using System.Text;
using System.Text.Json;
using Azure.Core;
using BrokerForge.Abstractions;
using BrokerForge.Auth.Credentials;
using BrokerForge.Connections;
using BrokerForge.Contracts;
using BrokerForge.Host.Config;
using BrokerForge.Host.Ingress;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BrokerForge.Contract.Tests;

/// <summary>
/// Connection designs read from each environment's Dataverse with the host's own identity (HLD D-34): the web
/// resource carrier, the bf_&lt;Instance&gt;_* environment variables, and what happens when a design is broken or
/// Dataverse cannot be read.
/// </summary>
[Collection(MockApiCollection.Name)]
public class DataverseDesignSourceTests(MockApiFixture mock)
{
    private const string EnvA = "00000000-0000-0000-0000-00000000000a";
    private const string EnvB = "00000000-0000-0000-0000-00000000000b";
    private const string OrgA = "https://contoso-dev.crm4.dynamics.com";
    private const string OrgB = "https://contoso-test.crm4.dynamics.com";

    /// <summary>Answers the two reads from in-memory rows per org; <see cref="Down"/> makes every read fail as unreachable.</summary>
    public sealed class FakeDataverse : IDataverseReader
    {
        public Dictionary<string, List<object>> WebResources { get; } = new();
        public Dictionary<string, List<object>> Variables { get; } = new();
        public List<(string Org, string Url)> Calls { get; } = new();
        public string? Down { get; set; }
        public bool DownTransient { get; set; } = true;

        public Task<JsonDocument> GetAsync(string orgUrl, string relativeUrl, CancellationToken ct)
        {
            lock (Calls)
                Calls.Add((orgUrl, relativeUrl));
            if (Down != null)
                throw new DataverseUnavailableException(Down, DownTransient);
            var rows = relativeUrl.StartsWith("webresourceset?", StringComparison.Ordinal) ? WebResources.GetValueOrDefault(orgUrl)
                : relativeUrl.StartsWith("environmentvariabledefinitions?", StringComparison.Ordinal) ? Variables.GetValueOrDefault(orgUrl)
                : throw new InvalidOperationException("unexpected read " + relativeUrl);
            return Task.FromResult(JsonDocument.Parse(JsonSerializer.Serialize(new { value = rows ?? new List<object>() })));
        }

        public void Design(string org, string name, ConnectionDesign design) => Raw(org, name, DesignCarrier.Wrap(DesignSerializer.Serialize(design)));

        public void Raw(string org, string name, string body, int type = 4) => Add(WebResources, org, new
        {
            webresourceid = Guid.NewGuid(),
            name,
            webresourcetype = type,
            content = Convert.ToBase64String(Encoding.UTF8.GetBytes(body)),
        });

        public void Variable(string org, string schemaName, string? defaultValue, string? current = null) => Add(Variables, org, new Dictionary<string, object?>
        {
            ["schemaname"] = schemaName,
            ["defaultvalue"] = defaultValue,
            ["environmentvariabledefinition_environmentvariablevalue"] = current == null ? Array.Empty<object>() : new object[] { new { value = current } },
        });

        private static void Add(Dictionary<string, List<object>> rows, string org, object row)
        {
            if (!rows.TryGetValue(org, out var list))
                rows[org] = list = new();
            list.Add(row);
        }
    }

    public sealed class ListLogger : ILogger
    {
        public List<(LogLevel Level, string Text)> Lines { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add((logLevel, formatter(state, exception)));
    }

    private readonly FakeDataverse _dataverse = new();

    private static IOptions<TrustOptions> Trust(string? orgB = OrgB) => Options.Create(new TrustOptions
    {
        TenantId = HostFactory.Tenant,
        Audience = HostFactory.Audience,
        AllowedCallers =
        {
            new AllowedCaller { ClientId = HostFactory.PluginApp, EnvironmentId = EnvA, OrgUrl = OrgA + "/" },
            new AllowedCaller { ClientId = HostFactory.OtherApp, EnvironmentId = EnvB.ToUpperInvariant(), OrgUrl = orgB },
        },
    });

    private DataverseDesignSource Source() => new(Trust(), _dataverse);

    private CompositeConfigProvider Provider(TimeProvider time, ILogger? log = null) =>
        new(new HostConfigStore(new ConfigInput()), Source(), time, TimeSpan.FromMinutes(5), log);

    [Fact]
    public async Task ReadsDesignAndEnvironmentVariables()
    {
        _dataverse.Design(OrgA, "contoso_/brokerforge/WarehouseAPI/design.xml", EnvironmentConfigTests.Warehouse());
        _dataverse.Variable(OrgA, "bf_WarehouseAPI_BaseUrl", "https://default.contoso.example", "https://current.contoso.example");
        _dataverse.Variable(OrgA, "bf_WarehouseAPI_Auth", "{\"queryParam\":\"key\"}");
        _dataverse.Variable(OrgA, "bf_OtherAPI_BaseUrl", "https://other.contoso.example");

        var (design, settings) = Assert.Single((await Source().LoadAsync(EnvA, default)).Designs);

        Assert.Equal("WarehouseAPI", design.Instance);
        Assert.Equal(3, design.Version);
        Assert.Equal("https://current.contoso.example", settings.BaseUrl);
        Assert.Equal("key", settings.AuthOverrides["queryParam"]);
        // The org URL comes from Trust:AllowedCallers, without its trailing slash; these are the exact reads Task 7 runs live.
        Assert.Equal(new[]
        {
            (OrgA, DataverseDesignSource.DesignQuery),
            (OrgA, "environmentvariabledefinitions?$select=schemaname,defaultvalue&$expand=environmentvariabledefinition_environmentvariablevalue($select=value)"
                + "&$filter=" + Uri.EscapeDataString("schemaname eq 'bf_WarehouseAPI_BaseUrl' or schemaname eq 'bf_WarehouseAPI_Auth'")),
        }, _dataverse.Calls);
        Assert.Equal("webresourceset?$select=webresourceid,name,webresourcetype,content&$filter="
            + Uri.EscapeDataString("contains(name,'/brokerforge/') and endswith(name,'/design.xml')"), DataverseDesignSource.DesignQuery);
    }

    [Fact]
    public async Task DefaultValueUsedWithoutCurrentValueAndMissingVariablesLeaveSettingsEmpty()
    {
        _dataverse.Design(OrgA, "ext_/brokerforge/WarehouseAPI/design.xml", EnvironmentConfigTests.Warehouse());
        _dataverse.Design(OrgA, "ext_/brokerforge/StockAPI/design.xml", EnvironmentConfigTests.Warehouse("StockAPI"));
        _dataverse.Variable(OrgA, "bf_WarehouseAPI_BaseUrl", "https://default.contoso.example");

        var loaded = (await Source().LoadAsync(EnvA, default)).Designs.ToDictionary(d => d.Design.Instance, d => d.Settings);

        Assert.Equal("https://default.contoso.example", loaded["WarehouseAPI"].BaseUrl);
        Assert.Empty(loaded["WarehouseAPI"].AuthOverrides);
        Assert.Null(loaded["StockAPI"].BaseUrl);
        Assert.Empty(loaded["StockAPI"].AuthOverrides);
    }

    [Fact]
    public async Task NoDesignsReadsNoVariables()
    {
        Assert.Empty((await Source().LoadAsync(EnvA, default)).Designs);
        Assert.Single(_dataverse.Calls);
    }

    [Theory]
    [InlineData("ext_/brokerforge/WarehouseAPI/design.xml", "<brokerforgeDesign", "not valid XML")]
    [InlineData("ext_/brokerforge/OtherAPI/design.xml", null, "holds the design of instance WarehouseAPI")]
    [InlineData("ext_/brokerforge/a/WarehouseAPI/design.xml", null, "is not named <prefix>_/brokerforge/<Instance>/design.xml")]
    public async Task BadCarrierNamesTheWebResource(string name, string? body, string problem)
    {
        if (body == null)
            _dataverse.Design(OrgA, name, EnvironmentConfigTests.Warehouse());
        else
            _dataverse.Raw(OrgA, name, body);

        var load = await Source().LoadAsync(EnvA, default);
        Assert.Empty(load.Designs);
        var failure = Assert.Single(load.Failures);
        Assert.Contains(name, failure);
        Assert.Contains(problem, failure);
    }

    [Fact]
    public async Task OneBadDesignLeavesTheOthers()
    {
        _dataverse.Design(OrgA, "ext_/brokerforge/WarehouseAPI/design.xml", EnvironmentConfigTests.Warehouse());
        _dataverse.Raw(OrgA, "ext_/brokerforge/StockAPI/design.xml", "<brokerforgeDesign");
        _dataverse.Raw(OrgA, "ext_/brokerforge/notes/old/design.xml", "<notes/>");
        _dataverse.Variable(OrgA, "bf_WarehouseAPI_BaseUrl", "https://inv.contoso.example");

        var load = await Source().LoadAsync(EnvA, default);

        Assert.Equal("WarehouseAPI", Assert.Single(load.Designs).Design.Instance);
        Assert.Equal(2, load.Failures.Count);
        Assert.Contains(load.Failures, f => f.Contains("ext_/brokerforge/StockAPI/design.xml: not valid XML"));
        Assert.Contains(load.Failures, f => f.Contains("ext_/brokerforge/notes/old/design.xml is not named"));
    }

    [Fact]
    public async Task InstanceDifferingOnlyInCaseIsNamed()
    {
        _dataverse.Design(OrgA, "ext_/brokerforge/warehouseapi/design.xml", EnvironmentConfigTests.Warehouse());
        var load = await Source().LoadAsync(EnvA, default);
        Assert.Empty(load.Designs);
        Assert.Contains("ext_/brokerforge/warehouseapi/design.xml: the instance in the name, warehouseapi, differs in case from the design's instance WarehouseAPI", Assert.Single(load.Failures));
    }

    [Fact]
    public async Task WrongWebResourceTypeFails()
    {
        _dataverse.Raw(OrgA, "ext_/brokerforge/WarehouseAPI/design.xml", DesignCarrier.Wrap(DesignSerializer.Serialize(EnvironmentConfigTests.Warehouse())), type: 3);
        var load = await Source().LoadAsync(EnvA, default);
        Assert.Empty(load.Designs);
        Assert.Contains("ext_/brokerforge/WarehouseAPI/design.xml has web resource type 3, not 4 (XML)", Assert.Single(load.Failures));
    }

    [Fact]
    public async Task TwoPrefixesForOneInstanceFail()
    {
        _dataverse.Design(OrgA, "ext_/brokerforge/WarehouseAPI/design.xml", EnvironmentConfigTests.Warehouse());
        _dataverse.Design(OrgA, "contoso_/brokerforge/WarehouseAPI/design.xml", EnvironmentConfigTests.Warehouse());
        var load = await Source().LoadAsync(EnvA, default);
        Assert.Empty(load.Designs);
        Assert.Contains("Instance WarehouseAPI has two connection designs in environment " + EnvA
            + ": contoso_/brokerforge/WarehouseAPI/design.xml and ext_/brokerforge/WarehouseAPI/design.xml", Assert.Single(load.Failures));
    }

    [Fact]
    public async Task AuthVariableThatIsNotAStringMapFails()
    {
        _dataverse.Design(OrgA, "ext_/brokerforge/WarehouseAPI/design.xml", EnvironmentConfigTests.Warehouse());
        _dataverse.Variable(OrgA, "bf_WarehouseAPI_Auth", "{\"queryParam\":1}");
        _dataverse.Design(OrgA, "ext_/brokerforge/StockAPI/design.xml", EnvironmentConfigTests.Warehouse("StockAPI"));
        var load = await Source().LoadAsync(EnvA, default);
        // Only the design whose variable is wrong is left out.
        Assert.Equal("StockAPI", Assert.Single(load.Designs).Design.Instance);
        Assert.Contains("bf_WarehouseAPI_Auth in environment " + EnvA + " must be a JSON object of strings", Assert.Single(load.Failures));
    }

    [Fact]
    public async Task InvalidDesignFailsOnlyItsEnvironment()
    {
        _dataverse.Raw(OrgA, "ext_/brokerforge/WarehouseAPI/design.xml", "<brokerforgeDesign formatVersion=\"1\"><![CDATA[{\"formatVersion\":1}]]></brokerforgeDesign>");
        _dataverse.Design(OrgB, "ext_/brokerforge/WarehouseAPI/design.xml", EnvironmentConfigTests.Warehouse());
        _dataverse.Variable(OrgB, "bf_WarehouseAPI_BaseUrl", "https://inv.contoso.example");
        var provider = Provider(new ConnectTests.MutableTime());

        var a = await provider.ForEnvironmentAsync(EnvA, default);
        Assert.Null(a.Instance("WarehouseAPI"));
        var failure = Assert.Single(a.DesignFailures);
        Assert.Contains("ext_/brokerforge/WarehouseAPI/design.xml", failure);
        Assert.Contains("$.instance", failure);
        Assert.NotNull((await provider.ForEnvironmentAsync(EnvB, default)).Instance("WarehouseAPI"));
        Assert.Empty((await provider.ForEnvironmentAsync(EnvB, default)).DesignFailures);
        Assert.NotNull(provider.Files);
        // Every request for the broken environment gets the same store.
        Assert.Same(a, await provider.ForEnvironmentAsync(EnvA, default));
    }

    [Theory]
    [InlineData("HTTP 401 (Unauthorized)")]
    [InlineData("HTTP 403 (Forbidden)")]
    [InlineData("timed out after 30 s")]
    public async Task DataverseDownColdFailsTyped(string reason)
    {
        _dataverse.Down = reason;
        var store = await Provider(new ConnectTests.MutableTime()).ForEnvironmentAsync(EnvA, default);
        // The files still serve; what only a design could resolve gets this text.
        Assert.Equal(new[] { $"Could not read connection designs for environment {EnvA} from Dataverse: {reason}." }, store.DesignFailures);
    }

    [Fact]
    public async Task DataverseDownWarmServesThenFails()
    {
        _dataverse.Design(OrgA, "ext_/brokerforge/WarehouseAPI/design.xml", EnvironmentConfigTests.Warehouse());
        var time = new ConnectTests.MutableTime();
        var start = time.Now;
        var log = new ListLogger();
        var provider = Provider(time, log);
        var good = await provider.ForEnvironmentAsync(EnvA, default);

        _dataverse.Down = "HTTP 503 (ServiceUnavailable)";
        time.Now = start + TimeSpan.FromMinutes(6);
        Assert.Same(good, await provider.ForEnvironmentAsync(EnvA, default));
        time.Now = start + TimeSpan.FromMinutes(9.9);
        Assert.Same(good, await provider.ForEnvironmentAsync(EnvA, default));
        var warnings = log.Lines.Where(l => l.Level == LogLevel.Warning).Select(l => l.Text).ToList();
        Assert.Equal(2, warnings.Count);
        Assert.All(warnings, w => Assert.Contains($"Could not read connection designs for environment {EnvA} from Dataverse: HTTP 503 (ServiceUnavailable).", w));

        time.Now = start + TimeSpan.FromMinutes(10.1);
        var after = await provider.ForEnvironmentAsync(EnvA, default);
        Assert.Null(after.Instance("WarehouseAPI"));
        Assert.Equal(new[] { $"Could not read connection designs for environment {EnvA} from Dataverse: HTTP 503 (ServiceUnavailable)." }, after.DesignFailures);
    }

    [Theory]
    [InlineData("HTTP 403 (Forbidden): Principal user is missing prvReadWebResource privilege")]
    public Task DataverseForbiddenWarmFailsAtOnce(string reason) => WarmFailsAtOnce(reason);

    [Theory]
    [InlineData("HTTP 400 (BadRequest): Could not find a property named 'nme'")]
    public Task DataverseBadRequestWarmFailsAtOnce(string reason) => WarmFailsAtOnce(reason);

    /// <summary>Only a transient failure may serve the last good store; anything else is wrong and fails the first call after the TTL.</summary>
    private async Task WarmFailsAtOnce(string reason)
    {
        _dataverse.Design(OrgA, "ext_/brokerforge/WarehouseAPI/design.xml", EnvironmentConfigTests.Warehouse());
        var time = new ConnectTests.MutableTime();
        var log = new ListLogger();
        var provider = Provider(time, log);
        await provider.ForEnvironmentAsync(EnvA, default);

        _dataverse.Down = reason;
        _dataverse.DownTransient = false;
        time.Now += TimeSpan.FromMinutes(6);
        var store = await provider.ForEnvironmentAsync(EnvA, default);
        Assert.Null(store.Instance("WarehouseAPI"));
        Assert.Equal(new[] { $"Could not read connection designs for environment {EnvA} from Dataverse: {reason}." }, store.DesignFailures);
        Assert.DoesNotContain(log.Lines, l => l.Level == LogLevel.Warning);
        // Still cached for the failure period, so Dataverse is not read again at once.
        var calls = _dataverse.Calls.Count;
        Assert.Same(store, await provider.ForEnvironmentAsync(EnvA, default));
        Assert.Equal(calls, _dataverse.Calls.Count);
    }

    [Fact]
    public async Task FailedBuildIsCachedBriefly()
    {
        var time = new ConnectTests.MutableTime();
        var provider = Provider(time);
        _dataverse.Down = "HTTP 503 (ServiceUnavailable)";
        Assert.NotEmpty((await provider.ForEnvironmentAsync(EnvA, default)).DesignFailures);
        Assert.NotEmpty((await provider.ForEnvironmentAsync(EnvA, default)).DesignFailures);
        Assert.Single(_dataverse.Calls);

        _dataverse.Down = null;
        time.Now += CompositeConfigProvider.FailureCacheTime - TimeSpan.FromSeconds(1);
        Assert.NotEmpty((await provider.ForEnvironmentAsync(EnvA, default)).DesignFailures);
        Assert.Single(_dataverse.Calls);
        time.Now += TimeSpan.FromSeconds(2);
        Assert.Empty((await provider.ForEnvironmentAsync(EnvA, default)).DesignFailures);
        Assert.Equal(2, _dataverse.Calls.Count);
    }

    [Fact]
    public void CacheSecondsAbove300Rejected()
    {
        using var host = new HostFactory(mock.BaseUrl, extra: new Dictionary<string, string?> { ["Config:Designs:CacheSeconds"] = "301" });
        var ex = Assert.Throws<ConfigurationException>(() => host.CreateClient());
        Assert.Contains("Config:Designs:CacheSeconds must be between 1 and 300", ex.Message);
    }

    [Fact]
    public void MissingOrgUrlRejectedWhenEnabled()
    {
        Assert.Empty(Trust(orgB: null).Value.Problems(designs: false));
        Assert.Equal(new[] { $"Trust:AllowedCallers entry for environment {EnvB.ToUpperInvariant()} needs OrgUrl when Config:Designs:Enabled is true" },
            Trust(orgB: null).Value.Problems(designs: true));
        Assert.Contains("OrgUrl must be an absolute https URL", Assert.Single(Trust(orgB: "contoso.crm.dynamics.com").Value.Problems(designs: false)));

        using var host = new HostFactory(mock.BaseUrl, extra: new Dictionary<string, string?>
        {
            ["Config:Designs:Enabled"] = "true",
            ["Credentials:InMemory"] = "true",
            ["Trust:AllowedCallers:0:OrgUrl"] = OrgA,
        });
        var ex = Assert.Throws<ConfigurationException>(() => host.CreateClient());
        Assert.Contains($"Trust:AllowedCallers entry for environment {HostFactory.Environment2} needs OrgUrl", ex.Message);
    }

    [Fact]
    public async Task EnabledHostServesDesignFromDataverseAndLogsItsVersion()
    {
        _dataverse.Design(OrgA, "ext_/brokerforge/WarehouseAPI/design.xml", EnvironmentConfigTests.Warehouse(version: 7));
        _dataverse.Variable(OrgA, "bf_WarehouseAPI_BaseUrl", "https://default.contoso.example", mock.BaseUrl);
        var secrets = new InMemoryInstanceSecretStore();
        await secrets.SetAsync(HostFactory.Environment1, "WarehouseAPI", "apiKey", MockApiFixture.InventoryKey, default);
        using var host = new HostFactory(mock.BaseUrl, extra: new Dictionary<string, string?>
        {
            ["Config:Designs:Enabled"] = "true",
            ["Credentials:InMemory"] = "true",
            ["Trust:AllowedCallers:0:OrgUrl"] = OrgA,
            ["Trust:AllowedCallers:1:OrgUrl"] = OrgB,
            ["Config:Designs:AllowedPrivateHosts:0"] = "127.0.0.1",
        }, services: s =>
        {
            s.AddSingleton<IDataverseReader>(_dataverse);
            s.AddSingleton<IInstanceSecretStore>(secrets);
        });
        var request = HostFactory.Request("ext_item", Messages.RetrieveMultiple, "WarehouseAPI");
        request.Query = new CanonicalQuery { Page = new PageRequest { Size = 50 } };

        var (status, response, error) = await host.Execute(request);

        Assert.True(status == 200, error?.Message);
        Assert.NotEmpty(response!.Rows);
        Assert.Contains(host.Logs, l => l.StartsWith("Executing RetrieveMultiple on ext_item with listItems v", StringComparison.Ordinal) && l.Contains("(design v7)"));
        // File instances are not labelled.
        var orders = HostFactory.Request("ext_order", Messages.RetrieveMultiple);
        orders.Query = new CanonicalQuery { Page = new PageRequest { Size = 5 } };
        Assert.Equal(200, (await host.Execute(orders)).Status);
        Assert.Contains(host.Logs, l => l.StartsWith("Executing RetrieveMultiple on ext_order with listOrders v", StringComparison.Ordinal) && !l.Contains("design v"));
    }

    [Fact]
    public void DisabledHostRegistersNoDesignSource()
    {
        using var host = new HostFactory(mock.BaseUrl);
        host.CreateClient().Dispose();
        Assert.Null(host.Services.GetService<IDesignSource>());
    }

    private sealed class FakeCredential : TokenCredential
    {
        public List<string> Scopes { get; } = new();
        public bool Fail { get; init; }
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) => throw new NotSupportedException();
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            if (Fail)
                throw new Azure.Identity.AuthenticationFailedException("ManagedIdentityCredential authentication unavailable.");
            Scopes.AddRange(requestContext.Scopes);
            return ValueTask.FromResult(new AccessToken("dataverse-test-token", DateTimeOffset.UtcNow.AddHours(1)));
        }
    }

    private sealed class Pages(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(answer(request));
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task ReaderFollowsNextLinkWithTheOrgToken()
    {
        var credential = new FakeCredential();
        var handler = new Pages(r => r.RequestUri!.Query.Contains("skiptoken")
            ? Json("{\"value\":[{\"name\":\"b\"}]}")
            : Json($"{{\"value\":[{{\"name\":\"a\"}}],\"@odata.nextLink\":\"{OrgA}/api/data/v9.2/webresourceset?$skiptoken=2\"}}"));
        var reader = new DataverseReader(credential, new HttpClient(handler));

        using var doc = await reader.GetAsync(OrgA + "/", "webresourceset?$select=name", default);

        Assert.Equal(new[] { "a", "b" }, doc.RootElement.GetProperty("value").EnumerateArray().Select(e => e.GetProperty("name").GetString()));
        Assert.Equal(new[] { OrgA + "/.default", OrgA + "/.default" }, credential.Scopes);
        Assert.Equal(OrgA + "/api/data/v9.2/webresourceset?$select=name", handler.Requests[0].RequestUri!.OriginalString);
        Assert.All(handler.Requests, r => Assert.Equal("Bearer dataverse-test-token", r.Headers.Authorization!.ToString()));
        Assert.All(handler.Requests, r => Assert.Equal("4.0", r.Headers.GetValues("OData-Version").Single()));
    }

    [Fact]
    public async Task ReaderRefusesNextLinkOffTheOrg()
    {
        var handler = new Pages(_ => Json("{\"value\":[],\"@odata.nextLink\":\"https://elsewhere.example/api/data/v9.2/webresourceset?$skiptoken=2\"}"));
        var ex = await Assert.ThrowsAsync<DataverseUnavailableException>(() =>
            new DataverseReader(new FakeCredential(), new HttpClient(handler)).GetAsync(OrgA, "webresourceset", default));
        Assert.Contains("next page link is not under " + OrgA, ex.Message);
        Assert.False(ex.Transient);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "HTTP 401 (Unauthorized)")]
    [InlineData(HttpStatusCode.Forbidden, "HTTP 403 (Forbidden): Principal user is missing prvReadWebResource privilege")]
    public async Task ReaderFailureNamesTheStatus(HttpStatusCode status, string reason)
    {
        await ReaderFailureIsClassified(status, reason, transient: false);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, false)]
    [InlineData(HttpStatusCode.NotFound, false)]
    [InlineData(HttpStatusCode.RequestTimeout, true)]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.InternalServerError, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]
    public Task ReaderStatusTransience(HttpStatusCode status, bool transient) =>
        ReaderFailureIsClassified(status, $"HTTP {(int)status} ({status})", transient);

    [Fact]
    public async Task ReaderNetworkAndTokenFailuresAreTransientAndShapeErrorsAreNot()
    {
        var down = await Assert.ThrowsAsync<DataverseUnavailableException>(() =>
            new DataverseReader(new FakeCredential(), new HttpClient(new Pages(_ => throw new HttpRequestException("connection refused")))).GetAsync(OrgA, "x", default));
        Assert.True(down.Transient);
        var token = await Assert.ThrowsAsync<DataverseUnavailableException>(() =>
            new DataverseReader(new FakeCredential { Fail = true }, new HttpClient(new Pages(_ => Json("{}")))).GetAsync(OrgA, "x", default));
        Assert.True(token.Transient);
        var shape = await Assert.ThrowsAsync<DataverseUnavailableException>(() =>
            new DataverseReader(new FakeCredential(), new HttpClient(new Pages(_ => Json("{}")))).GetAsync(OrgA, "x", default));
        Assert.False(shape.Transient);
        Assert.Contains("has no value array", shape.Message);
    }

    private static async Task ReaderFailureIsClassified(HttpStatusCode status, string reason, bool transient)
    {
        var body = status == HttpStatusCode.Forbidden ? "{\"error\":{\"code\":\"0x80040220\",\"message\":\"Principal user is missing prvReadWebResource privilege\"}}" : "";
        var handler = new Pages(_ => Json(body, status));
        var ex = await Assert.ThrowsAsync<DataverseUnavailableException>(() =>
            new DataverseReader(new FakeCredential(), new HttpClient(handler)).GetAsync(OrgA, "webresourceset", default));
        Assert.Equal(reason, ex.Message);
        Assert.Equal(transient, ex.Transient);
    }
}
