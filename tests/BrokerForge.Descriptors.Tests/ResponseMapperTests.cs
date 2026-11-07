using System.Text;
using BrokerForge.Abstractions;
using BrokerForge.Contracts;
using BrokerForge.Descriptors.Mapping;
using BrokerForge.Descriptors.Parsing;
using BrokerForge.Keys;
using static BrokerForge.Descriptors.Tests.Fixtures;

namespace BrokerForge.Descriptors.Tests;

public class ResponseMapperTests
{
    internal static BrokerResult Result(int status, string body, params (string Name, string Value)[] headers) =>
        new(status, headers.ToDictionary(h => h.Name, h => new[] { h.Value }, StringComparer.OrdinalIgnoreCase), Encoding.UTF8.GetBytes(body));

    private const string Order1 = """{"id":"ord_0001","reference":"REF-0001","state":{"code":"shipped"},"customer":{"ref":"CUST-01"},"placedAt":"2026-01-01T01:00:00+01:00","lines":[],"totals":{"gross":1.5}}""";

    private static IReadOnlyList<MappedRow> List(string body, string? yaml = null) =>
        ResponseMapper.MapRows(yaml == null ? Orders("listOrders") : DescriptorParser.Parse(yaml, "listOrders.yaml"), Result(200, body), KeyKind.String, "corr-1");

    [Fact]
    public void Rows_MapsTypedCells()
    {
        var rows = List("{\"data\":[" + Order1 + "],\"meta\":{\"nextCursor\":null}}");
        var row = Assert.Single(rows);
        Assert.Equal(ExternalKey.FromString("ord_0001"), row.Key);
        Assert.Equal("REF-0001", row.Cells.Single(c => c.Col == "ext_name").Value);
        var status = row.Cells.Single(c => c.Col == "ext_status");
        Assert.Equal(("optionset", "100000001"), (status.Type, status.Value));
        var placed = row.Cells.Single(c => c.Col == "ext_placed");
        Assert.Equal(("datetime", "2026-01-01T00:00:00.0000000Z"), (placed.Type, placed.Value));
    }

    [Fact]
    public void Rows_Retrieve_SingleObject()
    {
        var row = Assert.Single(ResponseMapper.MapRows(Orders("getOrder"), Result(200, Order1), KeyKind.String, "corr-1"));
        Assert.Equal("ord_0001", row.Key.Value);
    }

    [Fact]
    public void Rows_EmptyArray_ReturnsEmpty()
    {
        Assert.Empty(List("""{"data":[],"meta":{}}"""));
    }

    [Fact]
    public void Rows_MissingPath_ThrowsMappingNamingPath()
    {
        var ex = Assert.Throws<MappingException>(() => List("""{"notData":[]}"""));
        Assert.Contains("$.data[*]", ex.Message);
        Assert.Equal("corr-1", ex.CorrelationId);
    }

    [Fact]
    public void Rows_NotJson_ThrowsMapping()
    {
        Assert.Throws<MappingException>(() => List("<html>"));
    }

    [Fact]
    public void Map_MissingPathNotOptional_ThrowsMappingNamingPath()
    {
        var ex = Assert.Throws<MappingException>(() => List("""{"data":[{"id":"ord_1","state":{"code":"open"},"customer":{"ref":"C"},"placedAt":"2026-01-01T00:00:00Z","lines":[],"totals":{"gross":1.5}}]}"""));
        Assert.Contains("ext_name", ex.Message);
        Assert.Contains("$.reference", ex.Message);
    }

    [Fact]
    public void Map_OptionalMissing_Null()
    {
        var yaml = Mutate(OrdersYaml("listOrders"), "ext_name:        $.reference", "ext_name:        { path: $.reference, optional: true }");
        var row = Assert.Single(List("""{"data":[{"id":"ord_1","state":{"code":"open"},"customer":{"ref":"C"},"placedAt":"2026-01-01T00:00:00Z","lines":[],"totals":{"gross":1.5}}]}""", yaml));
        Assert.Null(row.Cells.Single(c => c.Col == "ext_name").Value);
    }

    [Fact]
    public void Map_JsonNull_Null()
    {
        var row = Assert.Single(List("""{"data":[{"id":"ord_1","reference":null,"state":{"code":"open"},"customer":{"ref":"C"},"placedAt":null,"lines":[],"totals":{"gross":null}}]}"""));
        Assert.Null(row.Cells.Single(c => c.Col == "ext_name").Value);
        Assert.Null(row.Cells.Single(c => c.Col == "ext_placed").Value);
        Assert.Equal((CellTypes.Decimal, null), (row.Cells.Single(c => c.Col == "ext_total").Type, row.Cells.Single(c => c.Col == "ext_total").Value));
    }

    [Fact]
    public void Map_TypeCoercionFailure_ThrowsNamingColumnAndPath()
    {
        var ex = Assert.Throws<MappingException>(() => List("""{"data":[{"id":"ord_1","reference":"R","state":{"code":"open"},"customer":{"ref":"C"},"placedAt":"yesterday","lines":[],"totals":{"gross":1.5}}]}"""));
        Assert.Contains("ext_placed", ex.Message);
        Assert.Contains("$.placedAt", ex.Message);
    }

    [Fact]
    public void Map_ObjectForStringColumn_Throws()
    {
        Assert.Throws<MappingException>(() => List("""{"data":[{"id":"ord_1","reference":{"a":1},"state":{"code":"open"},"customer":{"ref":"C"},"placedAt":"2026-01-01T00:00:00Z","lines":[],"totals":{"gross":1.5}}]}"""));
    }

    [Fact]
    public void Map_OptionSetUnknown_ThrowsUnlessUnknownNull()
    {
        const string body = """{"data":[{"id":"ord_1","reference":"R","state":{"code":"lost"},"customer":{"ref":"C"},"placedAt":"2026-01-01T00:00:00Z","lines":[],"totals":{"gross":1.5}}]}""";
        var ex = Assert.Throws<MappingException>(() => List(body));
        Assert.Contains("lost", ex.Message);
        var yaml = Mutate(OrdersYaml("listOrders"), "cancelled: 100000002 }", "cancelled: 100000002, _unknown: null }");
        Assert.Null(Assert.Single(List(body, yaml)).Cells.Single(c => c.Col == "ext_status").Value);
    }

    [Fact]
    public void Key_MissingOrNull_Throws()
    {
        Assert.Throws<MappingException>(() => List("""{"data":[{"reference":"R","state":{"code":"open"},"customer":{"ref":"C"},"placedAt":"2026-01-01T00:00:00Z","lines":[],"totals":{"gross":1.5}}]}"""));
        Assert.Throws<MappingException>(() => List("""{"data":[{"id":null,"reference":"R","state":{"code":"open"},"customer":{"ref":"C"},"placedAt":"2026-01-01T00:00:00Z","lines":[],"totals":{"gross":1.5}}]}"""));
    }

    [Fact]
    public void Key_CompositeRejectedInPhase1()
    {
        var yaml = Mutate(OrdersYaml("listOrders"), "  key: $.id\n", "  key: { accountId: $.accountId, orderId: $.id }\n");
        var ex = Assert.Throws<MappingException>(() => List("{\"data\":[" + Order1 + "]}", yaml));
        Assert.Contains("composite", ex.Message);
    }

    [Fact]
    public void CreatedKey_FromBodyPath()
    {
        Assert.Equal(ExternalKey.FromString("ord_0121"), ResponseMapper.MapCreatedKey(Orders("createOrder"), Result(201, """{"id":"ord_0121"}"""), KeyKind.String, "c"));
    }

    [Fact]
    public void CreatedKey_FromLocationHeaderPattern()
    {
        var yaml = Mutate(OrdersYaml("createOrder"), "  key: $.id\n", "  key: { header: Location, pattern: \"/orders/(?<id>[^/]+)$\" }\n");
        var d = DescriptorParser.Parse(yaml, "createOrder.yaml");
        Assert.Equal(ExternalKey.FromString("ord_0122"), ResponseMapper.MapCreatedKey(d, Result(201, "", ("location", "/v2/orders/ord_0122")), KeyKind.String, "c"));
        var ex = Assert.Throws<MappingException>(() => ResponseMapper.MapCreatedKey(d, Result(201, ""), KeyKind.String, "c"));
        Assert.Contains("Location", ex.Message);
    }

    [Fact]
    public void CreatedKey_HeaderPatternThatBacktracks_MappingExceptionWithinTimeout()
    {
        var yaml = Mutate(OrdersYaml("createOrder"), "  key: $.id\n", "  key: { header: Location, pattern: \"^(a+)+(?<id>b)$\" }\n");
        var d = DescriptorParser.Parse(yaml, "createOrder.yaml");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var ex = Assert.Throws<MappingException>(() => ResponseMapper.MapCreatedKey(d, Result(201, "", ("location", new string('a', 40) + "!")), KeyKind.String, "c"));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"took {watch.Elapsed}");
        Assert.Contains("time", ex.Message);
    }

}
