using System.Text;
using System.Text.Json.Nodes;
using BrokerForge.Abstractions;
using BrokerForge.Contracts;
using BrokerForge.Descriptors.Model;
using BrokerForge.Descriptors.Parsing;
using BrokerForge.Descriptors.Planning;
using BrokerForge.Descriptors.Rendering;
using BrokerForge.Keys;

namespace BrokerForge.Descriptors.Tests;

public class RequestRendererTests
{
    internal const string DummySecret = "bf-test-dummy-secret";

    internal sealed class FakeSecrets : ISecretResolver
    {
        public List<SecretRef> Asked { get; } = new();
        public Task<string> GetAsync(SecretRef reference, CancellationToken ct) { Asked.Add(reference); return Task.FromResult(DummySecret); }
    }

    internal static readonly ServiceInstance Instance = new(
        "OrdersAPI", "rest", "https://orders.example",
        new AuthConfig(AuthMode.Static, "apiKeyHeader", HeaderName: "X-Api-Key", SecretRef: "env:ORDERS_KEY"),
        new Dictionary<string, string> { ["accountId"] = "acc-1", ["region"] = "eu" },
        new Dictionary<string, string> { ["partnerKey"] = "env:PARTNER_KEY" });

    private static OperationDescriptor Op(string binds, string request, string extra = "") =>
        DescriptorParser.Parse($"operation: op\nversion: 1\nbinds: {binds}\ntable: ext_order\nrequest:\n{request}\n{extra}", "test.yaml");

    private static BindingContext Ctx(
        Dictionary<string, Cell>? record = null, ISet<string>? changed = null, ExternalKey? id = null, QueryPlan? plan = null, PagingValues? paging = null) =>
        new(Instance, "corr-1", "env-1", "user-1", false, record, changed, id, plan, paging);

    private static Cell Cell(string col, string type, string value) => new() { Col = col, Type = type, Value = value };

    private static Task<RenderedRequest> Render(OperationDescriptor d, BindingContext ctx, FakeSecrets? secrets = null) =>
        RequestRenderer.RenderAsync(d.Request, d, ctx, secrets ?? new FakeSecrets(), CancellationToken.None);

    private static string Header(RenderedRequest r, string name) => r.Headers.Single(h => h.Key == name).Value;

    private static JsonNode BodyJson(RenderedRequest r) => JsonNode.Parse(r.Body!)!;

    [Fact]
    public async Task Path_PlaceholdersUrlEncoded()
    {
        var d = Op("Retrieve", "  method: GET\n  path: /v2/{acc}/orders/{orderId}\n  pathParams: { acc: { instance: accountId }, orderId: { id: true } }");
        var r = await Render(d, Ctx(id: ExternalKey.FromString("CUST/01 & x")));
        Assert.Equal("/v2/acc-1/orders/CUST%2F01%20%26%20x", r.PathAndQuery);
        Assert.Equal("https://orders.example", r.BaseUrl);
        Assert.Equal("GET", r.Method);
        Assert.Null(r.Body);
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    public async Task Path_DotSegmentValue_ThrowsValidation(string value)
    {
        var d = Op("Retrieve", "  method: GET\n  path: /v2/orders/{orderId}\n  pathParams: { orderId: { id: true } }");
        var ex = await Assert.ThrowsAsync<MappedSourceException>(() => Render(d, Ctx(id: ExternalKey.FromString(value))));
        Assert.Equal(SourceErrorKinds.Validation, ex.Kind);
    }

    [Fact]
    public async Task Path_NullValue_ThrowsValidation()
    {
        var d = Op("Create", "  method: POST\n  path: /v2/{c}\n  pathParams: { c: { col: ext_customerref } }\n  body: { template: { a: 1 } }");
        var ex = await Assert.ThrowsAsync<MappedSourceException>(() => Render(d, Ctx(record: new())));
        Assert.Equal(SourceErrorKinds.Validation, ex.Kind);
    }

    [Fact]
    public async Task Query_OmitIfNullDefaultTrue()
    {
        var d = Op("Create", "  method: POST\n  path: /v2/o\n  query:\n    a: { col: ext_a }\n    b: { col: ext_b, omitIfNull: false }\n    c: { static: \"x y\" }\n  body: { template: { z: 1 } }");
        var r = await Render(d, Ctx(record: new()));
        Assert.Equal("/v2/o?b=&c=x%20y", r.PathAndQuery);
    }

    [Fact]
    public async Task Query_InFormatCsv()
    {
        var d = Fixtures.Orders("listOrders");
        var plan = new QueryPlan(new Dictionary<string, BoundFilter> { ["status"] = new(Ops.In, new[] { "open", "shipped" }) }, null, 25, null, 25);
        var r = await Render(d, Ctx(plan: plan, paging: new PagingValues(25, null, null, null)));
        Assert.Equal("/v2/orders?status=open%2Cshipped&limit=25", r.PathAndQuery);
    }

    [Fact]
    public async Task Query_SortAndCursorToken()
    {
        var d = Fixtures.Orders("listOrders");
        var plan = new QueryPlan(new Dictionary<string, BoundFilter>(), new BoundSort("placedAt", true), 10, "abc", 10);
        var r = await Render(d, Ctx(plan: plan, paging: new PagingValues(10, "abc", null, null)));
        Assert.Equal("/v2/orders?sort=-placedAt&limit=10&cursor=abc", r.PathAndQuery);
    }

    [Fact]
    public async Task Query_RequiredFilterMissing_ThrowsUnsupportedQuery()
    {
        var yaml = Fixtures.Mutate(Fixtures.OrdersYaml("listOrders"), "customer: { filter: ext_customerref, ops: [eq] }", "customer: { filter: ext_customerref, ops: [eq], required: true }");
        var d = DescriptorParser.Parse(yaml, "listOrders.yaml");
        var plan = new QueryPlan(new Dictionary<string, BoundFilter>(), null, 10, null, 10);
        var ex = await Assert.ThrowsAsync<UnsupportedQueryException>(() => Render(d, Ctx(plan: plan, paging: new PagingValues(10, null, null, null))));
        Assert.Contains("ext_customerref", ex.Message);
    }

    [Fact]
    public async Task Header_ContextCorrelationId()
    {
        var d = Fixtures.Orders("deleteOrder");
        var r = await Render(d, Ctx(id: ExternalKey.FromString("ord_0001")));
        Assert.Equal("corr-1", Header(r, "X-Correlation"));
        Assert.Equal("/v2/orders/ord_0001", r.PathAndQuery);
    }

    [Fact]
    public async Task Header_SecretResolvedNotLogged()
    {
        var d = Op("Delete", "  method: DELETE\n  path: /v2/o/{id}\n  pathParams: { id: { id: true } }\n  headers:\n    X-Partner: { secret: partnerKey }\n  query:\n    key: { secret: partnerKey }");
        var secrets = new FakeSecrets();
        var r = await Render(d, Ctx(id: ExternalKey.FromInt32(7)), secrets);
        Assert.Equal(DummySecret, Header(r, "X-Partner"));
        Assert.Contains("key=" + DummySecret, r.PathAndQuery);
        Assert.Equal(new[] { "key" }, r.SecretQueryParams);
        Assert.All(secrets.Asked, s => Assert.Equal("env:PARTNER_KEY", s.ToString()));
    }

    [Fact]
    public async Task Header_UnknownSecretName_ThrowsWithoutValue()
    {
        var d = Op("Delete", "  method: DELETE\n  path: /v2/o\n  headers:\n    X-Partner: { secret: nope }");
        var ex = await Assert.ThrowsAsync<ConfigurationException>(() => Render(d, Ctx()));
        Assert.Contains("nope", ex.Message);
    }

    [Fact]
    public async Task Header_ValueWithNewline_Rejected()
    {
        var d = Op("Create", "  method: POST\n  path: /v2/o\n  headers:\n    X-Ref: { col: ext_a }\n  body: { template: { z: 1 } }");
        var ex = await Assert.ThrowsAsync<MappedSourceException>(() => Render(d, Ctx(record: new() { ["ext_a"] = Cell("ext_a", CellTypes.String, "a\r\nX-Evil: 1") })));
        Assert.Equal(SourceErrorKinds.Validation, ex.Kind);
    }

    [Fact]
    public async Task Computed_UuidAndUtcnow()
    {
        var d = Op("Delete", "  method: DELETE\n  path: /v2/o\n  headers:\n    Idempotency-Key: { computed: uuid }\n    X-At: { computed: utcnow }");
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        var r = await Render(d, Ctx());
        Assert.True(Guid.TryParse(Header(r, "Idempotency-Key"), out _));
        var at = DateTimeOffset.Parse(Header(r, "X-At"), System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(at, before, DateTimeOffset.UtcNow.AddSeconds(1));
        Assert.EndsWith("Z", Header(r, "X-At"));
    }

    [Fact]
    public async Task Body_TemplateMirrorsYaml()
    {
        var d = Op("Create", "  method: POST\n  path: /v2/o\n  body:\n    template:\n      customer: { ref: { col: ext_customerref } }\n      count: { col: ext_count }\n      flag: true\n      label: \"true\"\n      nothing: null\n      tags: [a, { context: environmentId }]\n      by: { context: userObjectId }\n      missing: { col: ext_missing }\n      dropped: { col: ext_missing, omitIfNull: true }");
        var r = await Render(d, Ctx(record: new()
        {
            ["ext_customerref"] = Cell("ext_customerref", CellTypes.String, "CUST-01"),
            ["ext_count"] = Cell("ext_count", CellTypes.Int, "3"),
        }));
        Assert.Equal("application/json", r.ContentType);
        var expected = JsonNode.Parse("""{"customer":{"ref":"CUST-01"},"count":3,"flag":true,"label":"true","nothing":null,"tags":["a","env-1"],"by":"user-1","missing":null}""");
        Assert.True(JsonNode.DeepEquals(expected, BodyJson(r)), BodyJson(r).ToJsonString());
    }

    [Fact]
    public async Task Body_FormatJsonParsesColumn()
    {
        var d = Fixtures.Orders("createOrder");
        var r = await Render(d, Ctx(record: new()
        {
            ["ext_customerref"] = Cell("ext_customerref", CellTypes.String, "CUST-01"),
            ["ext_lines_json"] = Cell("ext_lines_json", CellTypes.String, """[{"sku":"A","qty":2}]"""),
        }));
        Assert.Equal(2, BodyJson(r)["lines"]![0]!["qty"]!.GetValue<int>());
        Assert.Equal("/v2/accounts/acc-1/orders", r.PathAndQuery);
    }

    [Fact]
    public async Task Body_FormatJsonInvalid_ThrowsMapping()
    {
        var d = Fixtures.Orders("createOrder");
        var ex = await Assert.ThrowsAsync<MappingException>(() => Render(d, Ctx(record: new()
        {
            ["ext_customerref"] = Cell("ext_customerref", CellTypes.String, "CUST-01"),
            ["ext_lines_json"] = Cell("ext_lines_json", CellTypes.String, "[{not json"),
        })));
        Assert.Contains("ext_lines_json", ex.Message);
        Assert.Equal("corr-1", ex.CorrelationId);
    }

    [Fact]
    public async Task Body_RawXmlEscapesValues()
    {
        var d = Op("Create", "  method: POST\n  path: /v2/o\n  body:\n    contentType: text/xml\n    raw: \"<o><c>{{ col.ext_c }}</c><id>{{ context.correlationId }}</id></o>\"");
        var r = await Render(d, Ctx(record: new() { ["ext_c"] = Cell("ext_c", CellTypes.String, "<a & 'b'>") }));
        Assert.Equal("<o><c>&lt;a &amp; &apos;b&apos;&gt;</c><id>corr-1</id></o>", Encoding.UTF8.GetString(r.Body!));
        Assert.Equal("text/xml", r.ContentType);
    }

    [Fact]
    public async Task Body_FormUrlEncoded()
    {
        var d = Op("Create", "  method: POST\n  path: /v2/o\n  body:\n    contentType: application/x-www-form-urlencoded\n    template:\n      name: { col: ext_name }\n      kind: order\n      none: { col: ext_none }");
        var r = await Render(d, Ctx(record: new() { ["ext_name"] = Cell("ext_name", CellTypes.String, "a b&c") }));
        Assert.Equal("name=a+b%26c&kind=order", Encoding.UTF8.GetString(r.Body!));
    }

    [Fact]
    public async Task Update_UnchangedColumnOmittedUnlessAlwaysSend()
    {
        var d = Fixtures.Orders("updateOrder");
        var record = new Dictionary<string, Cell> { ["ext_customerref"] = Cell("ext_customerref", CellTypes.String, "CUST-99") };
        var r = await Render(d, Ctx(record: record, changed: new HashSet<string> { "ext_customerref" }, id: ExternalKey.FromString("ord_0001")));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"customer":{"ref":"CUST-99"}}"""), BodyJson(r)), BodyJson(r).ToJsonString());

        var none = await Render(d, Ctx(record: new(), changed: new HashSet<string>(), id: ExternalKey.FromString("ord_0001")));
        Assert.Equal("{}", Encoding.UTF8.GetString(none.Body!));

        var always = Op("Update", "  method: PATCH\n  path: /v2/o/{id}\n  pathParams: { id: { id: true } }\n  body:\n    template:\n      ref: { col: ext_customerref, alwaysSend: true }\n      state: { col: ext_state }");
        var sent = await Render(always, Ctx(record: record, changed: new HashSet<string> { "ext_customerref" }, id: ExternalKey.FromInt32(1)));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"ref":"CUST-99"}"""), BodyJson(sent)));
        // alwaysSend for a column the update does not carry: the value is unknown, so fail rather than send null.
        var ex = await Assert.ThrowsAsync<MappedSourceException>(() => Render(always, Ctx(record: new(), changed: new HashSet<string>(), id: ExternalKey.FromInt32(1))));
        Assert.Equal(SourceErrorKinds.Validation, ex.Kind);
        Assert.Contains("ext_customerref", ex.Message);
    }

    [Theory]
    [InlineData("iso8601", "2026-10-31T09:05:01.0000000Z", "2026-10-31T09:05:01.000Z")]
    [InlineData("iso8601", "2026-10-31T10:05:01+01:00", "2026-10-31T09:05:01.000Z")]
    [InlineData("date", "2026-10-31T09:05:01.0000000Z", "2026-10-31")]
    [InlineData("epochSeconds", "1970-01-01T00:01:40Z", "100")]
    [InlineData("epochMillis", "1970-01-01T00:01:40Z", "100000")]
    [InlineData("lower", "MiXeD", "mixed")]
    [InlineData("upper", "MiXeD", "MIXED")]
    public async Task Format_Iso8601DateEpochLowerUpper(string format, string input, string expected)
    {
        var d = Op("Create", $"  method: POST\n  path: /v2/o\n  headers:\n    X-V: {{ col: ext_v, format: {format} }}\n  body: {{ template: {{ z: 1 }} }}");
        var r = await Render(d, Ctx(record: new() { ["ext_v"] = Cell("ext_v", CellTypes.String, input) }));
        Assert.Equal(expected, Header(r, "X-V"));
    }

    [Fact]
    public async Task Format_DateOnUnparseableValue_ThrowsMapping()
    {
        var d = Op("Create", "  method: POST\n  path: /v2/o\n  headers:\n    X-V: { col: ext_v, format: date }\n  body: { template: { z: 1 } }");
        await Assert.ThrowsAsync<MappingException>(() => Render(d, Ctx(record: new() { ["ext_v"] = Cell("ext_v", CellTypes.String, "not a date") })));
    }

    [Fact]
    public async Task Col_OptionSetValue_RefusedUntilReverseMapped()
    {
        var d = Op("Create", "  method: POST\n  path: /v2/o\n  body:\n    template:\n      state: { col: ext_status }");
        var ex = await Assert.ThrowsAsync<UnsupportedQueryException>(() => Render(d, Ctx(record: new() { ["ext_status"] = Cell("ext_status", CellTypes.OptionSet, "100000000") })));
        Assert.Contains("ext_status", ex.Message);
    }

    [Fact]
    public async Task RequiredMissing_ThrowsValidation()
    {
        var d = Op("Create", "  method: POST\n  path: /v2/o\n  body:\n    template:\n      ref: { col: ext_customerref, required: true }");
        var ex = await Assert.ThrowsAsync<MappedSourceException>(() => Render(d, Ctx(record: new())));
        Assert.Equal(SourceErrorKinds.Validation, ex.Kind);
        Assert.Contains("ext_customerref", ex.Message);
        Assert.Equal("corr-1", ex.CorrelationId);
    }
}
