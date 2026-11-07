using System.Diagnostics;
using BrokerForge.Contracts;
using BrokerForge.Descriptors.Planning;
using BrokerForge.Host.Execution;

namespace BrokerForge.Contract.Tests;

[Collection(MockApiCollection.Name)]
public class PagingTests(MockApiFixture mock) : IAsyncLifetime
{
    public Task InitializeAsync() => mock.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static Deadline Budget() => Deadline.After(TimeSpan.FromSeconds(30));

    private static QueryPlan Plan(OperationContext op, int size, string? token = null, CanonicalQuery? query = null)
    {
        var q = query ?? new CanonicalQuery();
        q.Page = new PageRequest { Size = size, Token = token };
        return QueryPlanner.Plan(op.Descriptor, q, "corr-1");
    }

    private async Task<(List<string> Ids, int Pages)> ReadAll(string descriptor, int size, CanonicalQuery? query = null)
    {
        var op = await Operations.Context(mock.BaseUrl, Operations.Load(descriptor));
        var ids = new List<string>();
        string? token = null;
        var pages = 0;
        do
        {
            var (rows, next) = await Operations.Coordinator().ReadPageAsync(op, Plan(op, size, token, query), Budget(), CancellationToken.None);
            Assert.True(rows.Count <= size, $"page {pages} returned {rows.Count} rows for a page size of {size}");
            ids.AddRange(rows.Select(r => r.Key.Value));
            token = next;
            pages++;
            Assert.True(pages < 100, "paging did not terminate");
        } while (token != null);
        return (ids, pages);
    }

    private static void AssertCoverOnce(List<string> ids, int expected = 120)
    {
        Assert.Equal(expected, ids.Count);
        Assert.Equal(expected, ids.Distinct().Count());
    }

    [Fact]
    public async Task Cursor_AllPagesCover120Once()
    {
        var (ids, pages) = await ReadAll("listOrders", 50);
        AssertCoverOnce(ids);
        Assert.Equal(3, pages);
    }

    [Fact]
    public async Task Offset_AllPagesCover120Once() => AssertCoverOnce((await ReadAll("listOffset", 50)).Ids);

    [Fact]
    public async Task PageNumber_AllPagesCover120Once() => AssertCoverOnce((await ReadAll("listPaged", 50)).Ids);

    [Fact]
    public async Task Link_AllPagesCover120Once() => AssertCoverOnce((await ReadAll("listLink", 50)).Ids);

    [Fact]
    public async Task Cursor_FilterAppliesAcrossPages()
    {
        var query = new CanonicalQuery
        {
            Filter = new FilterGroup { Type = "and", Conditions = new() { new Condition { Col = "ext_status", Op = Ops.Eq, Values = new() { "100000000" } } } },
        };
        AssertCoverOnce((await ReadAll("listOrders", 25, query)).Ids, 40);
    }

    [Fact]
    public async Task None_FirstPageReturnsAll_SecondPageThrowsUnsupported()
    {
        var op = await Operations.Context(mock.BaseUrl, Operations.Load("listAll"));
        var (rows, next) = await Operations.Coordinator().ReadPageAsync(op, Plan(op, 50), Budget(), CancellationToken.None);
        Assert.Equal(120, rows.Count);
        Assert.Null(next);
        var token = PageToken.Create(op, "none", "1", Plan(op, 50), Operations.TokenKey);
        await Assert.ThrowsAsync<UnsupportedQueryException>(() => Operations.Coordinator().ReadPageAsync(op, Plan(op, 50, token), Budget(), CancellationToken.None));
    }

    [Fact]
    public async Task Page_SourceSmallerThanRequest_FillsAcrossCalls()
    {
        var op = await Operations.Context(mock.BaseUrl, Operations.Load("listOrders", y => y.Replace("max: 100", "max: 10")));
        var (rows, next) = await Operations.Coordinator().ReadPageAsync(op, Plan(op, 25), Budget(), CancellationToken.None);
        Assert.Equal(25, rows.Count);
        Assert.NotNull(next);
        var (rest, _) = await Operations.Coordinator().ReadPageAsync(op, Plan(op, 25, next), Budget(), CancellationToken.None);
        Assert.Equal("ord_0026", rest[0].Key.Value);
    }

    [Fact]
    public async Task Page_RequestLargerThanFillBudget_ReturnsFullCallsWithNextToken()
    {
        var op = await Operations.Context(mock.BaseUrl, Operations.Load("listOrders", y => y.Replace("max: 100", "max: 10").Replace("  token:", "  maxCallsPerPage: 3\n  token:")));
        var (rows, next) = await Operations.Coordinator().ReadPageAsync(op, Plan(op, 5000), Budget(), CancellationToken.None);
        Assert.Equal(30, rows.Count);
        Assert.NotNull(next);
    }

    [Fact]
    public async Task PageNumber_SizeKeptFromTokenAcrossPages()
    {
        var op = await Operations.Context(mock.BaseUrl, Operations.Load("listPaged"));
        var (first, next) = await Operations.Coordinator().ReadPageAsync(op, Plan(op, 30), Budget(), CancellationToken.None);
        Assert.Equal(30, first.Count);
        var (second, _) = await Operations.Coordinator().ReadPageAsync(op, Plan(op, 30, next), Budget(), CancellationToken.None);
        Assert.Equal("ord_0031", second[0].Key.Value);
    }

    [Fact]
    public async Task Token_FromOtherOperation_ThrowsUnsupportedQuery()
    {
        var cursorOp = await Operations.Context(mock.BaseUrl, Operations.Load("listOrders"));
        var (_, next) = await Operations.Coordinator().ReadPageAsync(cursorOp, Plan(cursorOp, 10), Budget(), CancellationToken.None);
        var offsetOp = await Operations.Context(mock.BaseUrl, Operations.Load("listOffset"));
        var ex = await Assert.ThrowsAsync<UnsupportedQueryException>(() => Operations.Coordinator().ReadPageAsync(offsetOp, Plan(offsetOp, 10, next), Budget(), CancellationToken.None));
        Assert.Contains("listOrders", ex.Message);
    }

    [Fact]
    public async Task Token_FromOtherServiceInstance_ThrowsUnsupportedQuery()
    {
        var op = await Operations.Context(mock.BaseUrl, Operations.Load("listOrders"));
        var (_, next) = await Operations.Coordinator().ReadPageAsync(op, Plan(op, 10), Budget(), CancellationToken.None);
        var other = op with { Instance = op.Instance with { Name = "PartnerAPI" }, Map = op.Map with { ServiceInstance = "PartnerAPI" } };
        var ex = await Assert.ThrowsAsync<UnsupportedQueryException>(() => Operations.Coordinator().ReadPageAsync(other, Plan(other, 10, next), Budget(), CancellationToken.None));
        Assert.Contains("OrdersAPI", ex.Message);
    }

    [Fact]
    public async Task Token_FromOtherTable_ThrowsUnsupportedQuery()
    {
        var op = await Operations.Context(mock.BaseUrl, Operations.Load("listOrders"));
        var (_, next) = await Operations.Coordinator().ReadPageAsync(op, Plan(op, 10), Budget(), CancellationToken.None);
        var other = op with { Map = op.Map with { Table = "ext_archivedorder" } };
        var ex = await Assert.ThrowsAsync<UnsupportedQueryException>(() => Operations.Coordinator().ReadPageAsync(other, Plan(other, 10, next), Budget(), CancellationToken.None));
        Assert.Contains("ext_order", ex.Message);
    }

    [Theory]
    [InlineData("not-a-token")]
    [InlineData("eyJvcCI6Imxpc3RPcmRlcnMifQ")]
    [InlineData("")]
    public async Task Token_Corrupt_ThrowsUnsupportedQuery(string token)
    {
        var op = await Operations.Context(mock.BaseUrl, Operations.Load("listOrders"));
        var plan = Plan(op, 10) with { Token = token };
        await Assert.ThrowsAsync<UnsupportedQueryException>(() => Operations.Coordinator().ReadPageAsync(op, plan, Budget(), CancellationToken.None));
    }

    [Fact]
    public async Task Token_OlderDescriptorVersion_ThrowsUnsupportedQuery()
    {
        var v1 = await Operations.Context(mock.BaseUrl, Operations.Load("listOrders"));
        var (_, next) = await Operations.Coordinator().ReadPageAsync(v1, Plan(v1, 10), Budget(), CancellationToken.None);
        var v2 = await Operations.Context(mock.BaseUrl, Operations.Load("listOrders", y => y.Replace("version: 1", "version: 2")));
        var ex = await Assert.ThrowsAsync<UnsupportedQueryException>(() => Operations.Coordinator().ReadPageAsync(v2, Plan(v2, 10, next), Budget(), CancellationToken.None));
        Assert.Contains("version", ex.Message);
    }

    [Fact]
    public async Task Token_WithDifferentFilter_ThrowsUnsupportedQuery()
    {
        var op = await Operations.Context(mock.BaseUrl, Operations.Load("listOrders"));
        var (_, next) = await Operations.Coordinator().ReadPageAsync(op, Plan(op, 10), Budget(), CancellationToken.None);
        var filtered = new CanonicalQuery
        {
            Filter = new FilterGroup { Type = "and", Conditions = new() { new Condition { Col = "ext_customerref", Op = Ops.Eq, Values = new() { "CUST-01" } } } },
        };
        await Assert.ThrowsAsync<UnsupportedQueryException>(() => Operations.Coordinator().ReadPageAsync(op, Plan(op, 10, next, filtered), Budget(), CancellationToken.None));
    }

    [Theory]
    [InlineData("http://evil.example/v2/orders-link?offset=10")]
    [InlineData("{base}.evil.example/v2/orders-link")]
    [InlineData("{base}@evil.example/v2")]
    public async Task LinkToken_OutsideBaseUrl_ThrowsUnsupportedQuery(string url)
    {
        var op = await Operations.Context(mock.BaseUrl, Operations.Load("listLink"));
        var token = PageToken.Create(op, "link", "10 " + url.Replace("{base}", mock.BaseUrl), Plan(op, 10), Operations.TokenKey);
        await Assert.ThrowsAsync<UnsupportedQueryException>(() => Operations.Coordinator().ReadPageAsync(op, Plan(op, 10, token), Budget(), CancellationToken.None));
    }

    [Fact]
    public async Task Token_Tampered_ThrowsUnsupportedQuery()
    {
        var op = await Operations.Context(mock.BaseUrl, Operations.Load("listOffset"));
        var (_, next) = await Operations.Coordinator().ReadPageAsync(op, Plan(op, 10), Budget(), CancellationToken.None);
        var dot = next!.IndexOf('.');
        var payload = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(Pad(next[..dot].Replace('-', '+').Replace('_', '/'))));
        Assert.Contains("\"c\":\"10\"", payload);
        var forged = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(payload.Replace("\"c\":\"10\"", "\"c\":\"90\""))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        await Assert.ThrowsAsync<UnsupportedQueryException>(() => Operations.Coordinator().ReadPageAsync(op, Plan(op, 10, forged + next[dot..]), Budget(), CancellationToken.None));
    }

    private static string Pad(string s) => s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');

    [Fact]
    public async Task Token_SignedWithOtherKey_ThrowsUnsupportedQuery()
    {
        var op = await Operations.Context(mock.BaseUrl, Operations.Load("listOffset"));
        var token = PageToken.Create(op, "offset", "10", Plan(op, 10), System.Text.Encoding.UTF8.GetBytes("another-key-another-key-another-key!!"));
        await Assert.ThrowsAsync<UnsupportedQueryException>(() => Operations.Coordinator().ReadPageAsync(op, Plan(op, 10, token), Budget(), CancellationToken.None));
    }

    [Theory]
    [InlineData("/v2/orders/ord_0001")]
    [InlineData("/v2/orders-link/../orders")]
    [InlineData("/v2/orders-link%2F..%2Forders")]
    public async Task LinkToken_SignedButOtherPath_ThrowsUnsupportedQuery(string path)
    {
        var op = await Operations.Context(mock.BaseUrl, Operations.Load("listLink"));
        var token = PageToken.Create(op, "link", "10 " + mock.BaseUrl + path + "?limit=10&offset=10", Plan(op, 10), Operations.TokenKey);
        await Assert.ThrowsAsync<UnsupportedQueryException>(() => Operations.Coordinator().ReadPageAsync(op, Plan(op, 10, token), Budget(), CancellationToken.None));
    }

    [Fact]
    public async Task LinkToken_NeverCarriesQuerySecrets()
    {
        // The mock builds its next link from the incoming query, as many sources do, so the secret comes back in it.
        var op = await Operations.Context(mock.BaseUrl, Operations.Load("listLink", y => y.Replace("    customer:", "    k: { secret: partnerKey }\n    customer:")));
        var ids = new List<string>();
        string? token = null;
        do
        {
            var (rows, next) = await Operations.Coordinator().ReadPageAsync(op, Plan(op, 50, token), Budget(), CancellationToken.None);
            ids.AddRange(rows.Select(r => r.Key.Value));
            if (next != null)
                Assert.DoesNotContain(MockApiFixture.ApiKey, System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(Pad(next[..next.IndexOf('.')].Replace('-', '+').Replace('_', '/')))));
            token = next;
        } while (token != null);
        AssertCoverOnce(ids);
    }

    private sealed class ScriptedBroker(params string[] bodies) : BrokerForge.Abstractions.IBroker
    {
        private int _next;
        public string BrokerType => "rest";
        public BrokerForge.Abstractions.BrokerCapabilities Capabilities { get; } = new(new HashSet<string>());
        public Task<BrokerForge.Abstractions.DiscoveryResult> DiscoverAsync(BrokerForge.Abstractions.ServiceInstance instance, CancellationToken ct) => throw new NotSupportedException();
        public Task<BrokerForge.Abstractions.BrokerResult> ExecuteAsync(BrokerForge.Abstractions.RenderedRequest request, BrokerForge.Abstractions.IAuthenticatedChannel channel, string correlationId, CancellationToken ct) =>
            Task.FromResult(new BrokerForge.Abstractions.BrokerResult(200, new Dictionary<string, string[]>(), System.Text.Encoding.UTF8.GetBytes(bodies[_next++])));
    }

    private static string Order(int i) => $$$"""{"id":"ord_{{{i:D4}}}","reference":"REF-{{{i:D4}}}","state":{"code":"open"},"customer":{"ref":"C"},"placedAt":"2026-01-01T00:00:00Z","lines":[],"totals":{"gross":1.5}}""";

    private static PagingCoordinator Scripted(params string[] bodies) => new(new OperationExecutor(new ScriptedBroker(bodies)), new Operations.MockKey(), Operations.TokenKey);

    [Fact]
    public async Task Offset_EmptyPageBeforeTotal_ThrowsSource()
    {
        var op = await Operations.Context(mock.BaseUrl, Operations.Load("listOffset", y => y.Replace("max: 40", "max: 2")));
        var coordinator = Scripted("{\"items\":[" + Order(1) + "," + Order(2) + "],\"total\":10}", """{"items":[],"total":10}""");
        var ex = await Assert.ThrowsAsync<SourceException>(() => coordinator.ReadPageAsync(op, Plan(op, 4), Budget(), CancellationToken.None));
        Assert.Contains("total", ex.Message);
    }

    [Fact]
    public async Task Page_SourceReturnsMoreThanAsked_ThrowsSource()
    {
        var op = await Operations.Context(mock.BaseUrl, Operations.Load("listOrders", y => y.Replace("max: 100", "max: 2")));
        var coordinator = Scripted("{\"data\":[" + string.Join(",", Order(1), Order(2), Order(3)) + "],\"meta\":{\"nextCursor\":null}}");
        var ex = await Assert.ThrowsAsync<SourceException>(() => coordinator.ReadPageAsync(op, Plan(op, 2), Budget(), CancellationToken.None));
        Assert.Contains("page size", ex.Message);
    }

    [Fact]
    public async Task Page_SourceRepeatsAKey_ThrowsSource()
    {
        var op = await Operations.Context(mock.BaseUrl, Operations.Load("listOrders"));
        var coordinator = Scripted("{\"data\":[" + string.Join(",", Order(1), Order(2), Order(1)) + "],\"meta\":{\"nextCursor\":null}}");
        var ex = await Assert.ThrowsAsync<SourceException>(() => coordinator.ReadPageAsync(op, Plan(op, 10), Budget(), CancellationToken.None));
        Assert.Contains("ord_0001", ex.Message);
    }

    [Fact]
    public async Task Deadline_AppliesAcrossPageCalls()
    {
        var op = await Operations.Context(mock.BaseUrl, Operations.Load("listOrders"));
        var q = new CanonicalQuery { Filter = new FilterGroup { Type = "and", Conditions = new() { new Condition { Col = "ext_customerref", Op = Ops.Eq, Values = new() { "__slow" } } } } };
        var watch = Stopwatch.StartNew();
        await Assert.ThrowsAsync<SourceTimeoutException>(() => Operations.Coordinator().ReadPageAsync(op, Plan(op, 10, null, q), Deadline.After(TimeSpan.FromSeconds(1)), CancellationToken.None));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2));
    }
}
