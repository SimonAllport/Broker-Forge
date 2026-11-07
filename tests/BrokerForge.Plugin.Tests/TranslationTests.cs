using BrokerForge.Contracts;
using BrokerForge.Plugin.Translation;
using BrokerForge.Plugin.Transport;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace BrokerForge.Plugin.Tests;

public class TranslationTests
{
    private static QueryExpression Query(params ConditionExpression[] conditions)
    {
        var q = new QueryExpression("ext_order") { ColumnSet = new ColumnSet("ext_name", "ext_status"), PageInfo = new PagingInfo { Count = 25, PageNumber = 2, PagingCookie = "tok" } };
        foreach (var c in conditions)
            q.Criteria.AddCondition(c);
        return q;
    }

    [Fact]
    public void Translate_EqualAndIn()
    {
        var q = QueryTranslator.Translate(Query(
            new ConditionExpression("ext_customerref", ConditionOperator.Equal, "CUST-01"),
            new ConditionExpression("ext_status", ConditionOperator.In, 100000000, 100000001)), "c");
        Assert.Equal("and", q.Filter.Type);
        Assert.Equal(new[] { "ext_customerref", "ext_status" }, q.Filter.Conditions.Select(c => c.Col));
        Assert.Equal(Ops.Eq, q.Filter.Conditions[0].Op);
        Assert.Equal(new[] { "CUST-01" }, q.Filter.Conditions[0].Values);
        Assert.Equal(Ops.In, q.Filter.Conditions[1].Op);
        Assert.Equal(new[] { "100000000", "100000001" }, q.Filter.Conditions[1].Values);
        Assert.Equal(25, q.Page.Size);
        Assert.Equal("tok", q.Page.Token);
        Assert.Equal(new[] { "ext_name", "ext_status" }, q.Columns);
    }

    [Theory]
    [InlineData(ConditionOperator.NotEqual, "ne")]
    [InlineData(ConditionOperator.GreaterThan, "gt")]
    [InlineData(ConditionOperator.GreaterEqual, "ge")]
    [InlineData(ConditionOperator.LessThan, "lt")]
    [InlineData(ConditionOperator.LessEqual, "le")]
    public void Translate_Comparisons(ConditionOperator op, string expected)
    {
        Assert.Equal(expected, QueryTranslator.Translate(Query(new ConditionExpression("ext_total", op, 5)), "c").Filter.Conditions[0].Op);
    }

    [Fact]
    public void Translate_BeginsWithAndTrailingLike_ToLike()
    {
        var q = QueryTranslator.Translate(Query(
            new ConditionExpression("ext_name", ConditionOperator.BeginsWith, "REF"),
            new ConditionExpression("ext_customerref", ConditionOperator.Like, "CUST%")), "c");
        Assert.All(q.Filter.Conditions, c => Assert.Equal(Ops.Like, c.Op));
        Assert.Equal(new[] { "REF" }, q.Filter.Conditions[0].Values);
        Assert.Equal(new[] { "CUST" }, q.Filter.Conditions[1].Values);
    }

    [Theory]
    [InlineData("%CUST%")]
    [InlineData("CU%ST")]
    [InlineData("CU_T%")]
    [InlineData("CUST")]
    public void Translate_ContainsLike_Throws(string pattern)
    {
        var ex = Assert.Throws<UnsupportedQueryException>(() => QueryTranslator.Translate(Query(new ConditionExpression("ext_name", ConditionOperator.Like, pattern)), "corr-1"));
        Assert.Contains("ext_name", ex.Message);
        Assert.Equal("corr-1", ex.CorrelationId);
    }

    [Fact]
    public void Translate_LinkEntity_Throws()
    {
        var q = Query();
        q.LinkEntities.Add(new LinkEntity("ext_order", "account", "ext_accountid", "accountid", JoinOperator.Inner));
        Assert.Throws<UnsupportedQueryException>(() => QueryTranslator.Translate(q, "c"));
        var aliased = Query(new ConditionExpression("acc", "name", ConditionOperator.Equal, "x"));
        Assert.Throws<UnsupportedQueryException>(() => QueryTranslator.Translate(aliased, "c"));
    }

    [Fact]
    public void Translate_NullOperator_Throws()
    {
        var ex = Assert.Throws<UnsupportedQueryException>(() => QueryTranslator.Translate(Query(new ConditionExpression("ext_name", ConditionOperator.Null)), "c"));
        Assert.Contains("Null", ex.Message);
        Assert.Throws<UnsupportedQueryException>(() => QueryTranslator.Translate(Query(new ConditionExpression("ext_name", ConditionOperator.Contains, "x")), "c"));
    }

    [Fact]
    public void Translate_EntityReferenceValue_Throws()
    {
        Assert.Throws<UnsupportedQueryException>(() => QueryTranslator.Translate(Query(new ConditionExpression("ext_acc", ConditionOperator.Equal, new EntityReference("account", Guid.NewGuid()))), "c"));
    }

    [Fact]
    public void Translate_OrGroupPreserved()
    {
        var q = Query();
        var or = new FilterExpression(LogicalOperator.Or);
        or.AddCondition("ext_status", ConditionOperator.Equal, 100000000);
        or.AddCondition("ext_status", ConditionOperator.Equal, 100000001);
        q.Criteria.AddFilter(or);
        var t = QueryTranslator.Translate(q, "c");
        var group = Assert.Single(t.Filter.Groups);
        Assert.Equal("or", group.Type);
        Assert.Equal(2, group.Conditions.Count);
    }

    [Fact]
    public void Translate_OrderAndAllColumns()
    {
        var q = Query();
        q.ColumnSet = new ColumnSet(true);
        q.AddOrder("ext_placed", OrderType.Descending);
        var t = QueryTranslator.Translate(q, "c");
        Assert.Null(t.Columns);
        Assert.Equal("ext_placed", t.OrderBy.Single().Col);
        Assert.True(t.OrderBy.Single().Desc);
    }

    [Fact]
    public void Translate_PageNumberAboveOneWithoutCookie_Throws()
    {
        var q = new QueryExpression("ext_order") { PageInfo = new PagingInfo { Count = 25, PageNumber = 3 } };
        var ex = Assert.Throws<UnsupportedQueryException>(() => QueryTranslator.Translate(q, "c"));
        Assert.Contains("page 3", ex.Message);
        q.PageInfo.PageNumber = 1;
        Assert.Null(QueryTranslator.Translate(q, "c").Page.Token);
    }

    [Fact]
    public void Translate_TopCountWhenNoPageInfo()
    {
        var q = new QueryExpression("ext_order") { TopCount = 7 };
        Assert.Equal(7, QueryTranslator.Translate(q, "c").Page.Size);
    }

    public static TheoryData<object, string> Values() => new()
    {
        { new DateTime(2026, 10, 31, 9, 5, 1, DateTimeKind.Utc), "2026-10-31T09:05:01.0000000Z" },
        { 12.5m, "12.5" },
        { 3.25d, "3.25" },
        { new OptionSetValue(100000002), "100000002" },
        { new Money(9.99m), "9.99" },
        { Guid.Parse("8b875594-0000-0000-0000-000000000001"), "8b875594-0000-0000-0000-000000000001" },
        { true, "true" },
        { 42L, "42" },
    };

    [Theory]
    [MemberData(nameof(Values))]
    public void Translate_ValuesInvariant(object value, string expected)
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-CH");
        try
        {
            Assert.Equal(expected, QueryTranslator.Translate(Query(new ConditionExpression("ext_x", ConditionOperator.Equal, value)), "c").Filter.Conditions[0].Values[0]);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    [Fact]
    public void ToCells_TypesFromRuntimeValues()
    {
        var e = new Entity("ext_order", Guid.NewGuid())
        {
            ["ext_name"] = "REF",
            ["ext_count"] = 3,
            ["ext_total"] = 1.5m,
            ["ext_money"] = new Money(2.5m),
            ["ext_flag"] = false,
            ["ext_placed"] = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            ["ext_status"] = new OptionSetValue(100000001),
            ["ext_empty"] = null,
        };
        e["ext_orderid"] = e.Id;
        var cells = RecordTranslator.ToCells(e, "c").ToDictionary(c => c.Col);
        Assert.False(cells.ContainsKey("ext_orderid"));
        Assert.Equal((CellTypes.String, "REF"), (cells["ext_name"].Type, cells["ext_name"].Value));
        Assert.Equal((CellTypes.Int, "3"), (cells["ext_count"].Type, cells["ext_count"].Value));
        Assert.Equal((CellTypes.Decimal, "1.5"), (cells["ext_total"].Type, cells["ext_total"].Value));
        Assert.Equal((CellTypes.Decimal, "2.5"), (cells["ext_money"].Type, cells["ext_money"].Value));
        Assert.Equal((CellTypes.Bool, "false"), (cells["ext_flag"].Type, cells["ext_flag"].Value));
        Assert.Equal((CellTypes.DateTime, "2026-01-01T00:00:00.0000000Z"), (cells["ext_placed"].Type, cells["ext_placed"].Value));
        Assert.Equal((CellTypes.OptionSet, "100000001"), (cells["ext_status"].Type, cells["ext_status"].Value));
        Assert.Null(cells["ext_empty"].Value);
    }

    [Fact]
    public void ToCells_Lookup_Throws()
    {
        var e = new Entity("ext_order") { ["ext_account"] = new EntityReference("account", Guid.NewGuid()) };
        Assert.Throws<UnsupportedQueryException>(() => RecordTranslator.ToCells(e, "c"));
    }

    [Fact]
    public void ToEntity_SetsPrimaryKeyAttribute()
    {
        var id = Guid.NewGuid();
        var row = new Row
        {
            Id = id.ToString(),
            Cells = new List<Cell>
            {
                new() { Col = "ext_name", Type = CellTypes.String, Value = "REF-1" },
                new() { Col = "ext_status", Type = CellTypes.OptionSet, Value = "100000001" },
                new() { Col = "ext_placed", Type = CellTypes.DateTime, Value = "2026-01-01T01:00:00.0000000Z" },
                new() { Col = "ext_total", Type = CellTypes.Decimal, Value = "12.5" },
                new() { Col = "ext_count", Type = CellTypes.Int, Value = "4" },
                new() { Col = "ext_flag", Type = CellTypes.Bool, Value = "true" },
                new() { Col = "ext_note", Type = CellTypes.String, Value = null },
            },
        };
        var e = RecordTranslator.ToEntity("ext_order", row);
        Assert.Equal(id, e.Id);
        Assert.Equal(id, e["ext_orderid"]);
        Assert.Equal("REF-1", e["ext_name"]);
        Assert.Equal(100000001, ((OptionSetValue)e["ext_status"]).Value);
        var placed = (DateTime)e["ext_placed"];
        Assert.Equal((DateTimeKind.Utc, 1), (placed.Kind, placed.Hour));
        Assert.Equal(12.5m, e["ext_total"]);
        Assert.Equal(4, e["ext_count"]);
        Assert.Equal(true, e["ext_flag"]);
        Assert.Null(e["ext_note"]);
    }

    [Theory]
    [InlineData("NotFound", -2147220969)]
    [InlineData("Conflict", -2147220937)]
    [InlineData("Forbidden", -2147220960)]
    [InlineData("SourceException", -2147220989)]
    [InlineData("UnsupportedQueryException", -2147220989)]
    public void ErrorRaiser_NotFoundCode(string error, int code)
    {
        var ex = ErrorRaiser.ToException(new ErrorPayload { Error = error, Message = "m", CorrelationId = "c" });
        Assert.Equal(code, ex.ErrorCode);
        Assert.Equal(OperationStatus.Failed, ex.Status);
    }

    [Fact]
    public void ErrorRaiser_MessageEndsWithCorrelationId()
    {
        var ex = ErrorRaiser.ToException(new ErrorPayload { Error = "SourceException", Message = "Source returned HTTP 500 for operation listOrders: boom", CorrelationId = "abc-123" });
        Assert.StartsWith("SourceException: Source returned HTTP 500", ex.Message);
        Assert.EndsWith("(correlationId abc-123)", ex.Message);
        var local = ErrorRaiser.ToException(new BrokerUnavailableException("host down", "x-1"));
        Assert.StartsWith("BrokerUnavailableException: host down", local.Message);
        Assert.EndsWith("(correlationId x-1)", local.Message);
    }

    [Fact]
    public void ErrorRaiser_ConnectUrlIncluded()
    {
        var ex = ErrorRaiser.ToException(new ErrorPayload { Error = "ConnectionRequiredException", Message = "Connect first.", ConnectUrl = "https://bf/connect", CorrelationId = "c" });
        Assert.Contains("https://bf/connect", ex.Message);
    }
}
