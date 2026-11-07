using BrokerForge.Contracts;
using BrokerForge.Descriptors.Parsing;
using BrokerForge.Descriptors.Planning;
using static BrokerForge.Descriptors.Tests.Fixtures;

namespace BrokerForge.Descriptors.Tests;

public class QueryPlannerTests
{
    private static Condition C(string col, string op, params string[] values) => new() { Col = col, Op = op, Values = values.ToList() };

    private static CanonicalQuery Q(FilterGroup? filter = null, List<OrderBy>? sort = null, int size = 50, string? token = null) => new()
    {
        Filter = filter,
        OrderBy = sort,
        Page = new PageRequest { Size = size, Token = token },
    };

    private static FilterGroup And(params Condition[] conditions) => new() { Type = "and", Conditions = conditions.ToList(), Groups = new() };

    private static QueryPlan Plan(CanonicalQuery q, string? yaml = null) =>
        QueryPlanner.Plan(yaml == null ? Orders("listOrders") : DescriptorParser.Parse(yaml, "listOrders.yaml"), q, "corr-1");

    private static UnsupportedQueryException Rejects(CanonicalQuery q, string? yaml = null)
    {
        var ex = Assert.Throws<UnsupportedQueryException>(() => Plan(q, yaml));
        Assert.Equal("corr-1", ex.CorrelationId);
        return ex;
    }

    [Fact]
    public void Plan_NoFilterNoSort_DefaultsOnly()
    {
        var plan = Plan(Q());
        Assert.Empty(plan.ByParam);
        Assert.Null(plan.Sort);
        Assert.Equal(50, plan.PageSize);
        Assert.Null(plan.Token);
    }

    [Fact]
    public void Plan_DeclaredEqAndIn_BindsToParams()
    {
        var plan = Plan(Q(And(C("ext_status", Ops.In, "100000000", "100000001"), C("ext_customerref", Ops.Eq, "CUST-01"))));
        Assert.Equal(Ops.In, plan.ByParam["status"].Op);
        Assert.Equal(new[] { "open", "shipped" }, plan.ByParam["status"].SourceValues);
        Assert.Equal(Ops.Eq, plan.ByParam["customer"].Op);
        Assert.Equal(new[] { "CUST-01" }, plan.ByParam["customer"].SourceValues);
    }

    [Fact]
    public void Plan_UndeclaredColumn_ThrowsNamingColumnAndOp()
    {
        var ex = Rejects(Q(And(C("ext_total", Ops.Eq, "10"))));
        Assert.Contains("ext_total", ex.Message);
        Assert.Contains("eq", ex.Message);
        Assert.Contains("listOrders", ex.Message);
    }

    [Fact]
    public void Plan_DeclaredColumnUndeclaredOp_Throws()
    {
        var ex = Rejects(Q(And(C("ext_status", Ops.Gt, "100000000"))));
        Assert.Contains("ext_status", ex.Message);
        Assert.Contains("gt", ex.Message);
    }

    [Fact]
    public void Plan_TwoConditionsOneParam_Throws()
    {
        var ex = Rejects(Q(And(C("ext_customerref", Ops.Eq, "CUST-01"), C("ext_customerref", Ops.Eq, "CUST-02"))));
        Assert.Contains("ext_customerref", ex.Message);
    }

    [Fact]
    public void Plan_OrGroupNotAllowed_Throws()
    {
        var yaml = Mutate(OrdersYaml("listOrders"), "  allowOr: true\n", "  allowOr: false\n");
        var or = new FilterGroup { Type = "or", Conditions = new() { C("ext_status", Ops.Eq, "100000000"), C("ext_status", Ops.Eq, "100000001") }, Groups = new() };
        var ex = Rejects(Q(or), yaml);
        Assert.Contains("or", ex.Message);
        Assert.Contains("ext_status", ex.Message);
    }

    [Fact]
    public void Plan_OrSameColumnEqAllowed_CollapsesToIn()
    {
        var or = new FilterGroup { Type = "or", Conditions = new() { C("ext_status", Ops.Eq, "100000000"), C("ext_status", Ops.Eq, "100000002") }, Groups = new() };
        var plan = Plan(Q(new FilterGroup { Type = "and", Conditions = new() { C("ext_customerref", Ops.Eq, "CUST-01") }, Groups = new() { or } }));
        Assert.Equal(Ops.In, plan.ByParam["status"].Op);
        Assert.Equal(new[] { "open", "cancelled" }, plan.ByParam["status"].SourceValues);
        Assert.Equal(new[] { "CUST-01" }, plan.ByParam["customer"].SourceValues);
    }

    [Fact]
    public void Plan_OrAcrossColumns_Throws()
    {
        var or = new FilterGroup { Type = "or", Conditions = new() { C("ext_status", Ops.Eq, "100000000"), C("ext_customerref", Ops.Eq, "CUST-01") }, Groups = new() };
        var ex = Rejects(Q(or));
        Assert.Contains("ext_customerref", ex.Message);
    }

    [Fact]
    public void Plan_OptionSetValue_ReverseMapped()
    {
        Assert.Equal(new[] { "shipped" }, Plan(Q(And(C("ext_status", Ops.Eq, "100000001")))).ByParam["status"].SourceValues);
    }

    [Fact]
    public void Plan_OptionSetValueUnmapped_Throws()
    {
        var ex = Rejects(Q(And(C("ext_status", Ops.Eq, "100000009"))));
        Assert.Contains("100000009", ex.Message);
        Assert.Contains("ext_status", ex.Message);
    }

    [Fact]
    public void Plan_LikeWithWildcardInValue_Throws()
    {
        var yaml = Mutate(OrdersYaml("listOrders"), "customer: { filter: ext_customerref, ops: [eq] }", "customer: { filter: ext_customerref, ops: [eq, like] }");
        Assert.Equal(new[] { "CUST" }, Plan(Q(And(C("ext_customerref", Ops.Like, "CUST"))), yaml).ByParam["customer"].SourceValues);
        Rejects(Q(And(C("ext_customerref", Ops.Like, "CU%T"))), yaml);
        Rejects(Q(And(C("ext_customerref", Ops.Like, "CU_T"))), yaml);
    }

    [Fact]
    public void Plan_CsvValueContainingComma_Throws()
    {
        var yaml = Mutate(OrdersYaml("listOrders"), "customer: { filter: ext_customerref, ops: [eq] }", "customer: { filter: ext_customerref, ops: [eq, in], format: csv }");
        Assert.Equal(new[] { "A", "B" }, Plan(Q(And(C("ext_customerref", Ops.In, "A", "B"))), yaml).ByParam["customer"].SourceValues);
        var ex = Rejects(Q(And(C("ext_customerref", Ops.In, "Smith, J", "ACME"))), yaml);
        Assert.Contains("ext_customerref", ex.Message);
        Assert.Contains(",", ex.Message);
        Rejects(Q(And(C("ext_customerref", Ops.Eq, "a,b"))), yaml);
    }

    [Fact]
    public void Plan_EqWithTwoValues_Throws()
    {
        Rejects(Q(And(C("ext_customerref", Ops.Eq, "A", "B"))));
    }

    [Fact]
    public void Plan_DeclaredSort_BindsSourceField()
    {
        var plan = Plan(Q(sort: new() { new OrderBy { Col = "ext_placed", Desc = true } }));
        Assert.Equal(new BoundSort("placedAt", true), plan.Sort);
    }

    [Fact]
    public void Plan_UndeclaredSort_MessageNamesColumn()
    {
        var ex = Rejects(Q(sort: new() { new OrderBy { Col = "ext_customerref" } }));
        Assert.Contains("ext_customerref", ex.Message);
        Assert.Contains("sort", ex.Message);
    }

    [Fact]
    public void Plan_DescSortWithoutDescFormat_Throws()
    {
        var yaml = Mutate(OrdersYaml("listOrders"), ", descFormat: \"-{field}\"", "");
        Assert.NotNull(Plan(Q(sort: new() { new OrderBy { Col = "ext_name" } }), yaml).Sort);
        var ex = Rejects(Q(sort: new() { new OrderBy { Col = "ext_name", Desc = true } }), yaml);
        Assert.Contains("descending", ex.Message);
    }

    [Fact]
    public void Plan_TwoSortColumns_Throws()
    {
        Rejects(Q(sort: new() { new OrderBy { Col = "ext_name" }, new OrderBy { Col = "ext_placed" } }));
    }

    [Fact]
    public void Plan_PageSizeCappedAtMax()
    {
        Assert.Equal(100, Plan(Q(size: 5000)).PageSize);
        Assert.Equal(5000, Plan(Q(size: 5000)).RequestedSize);
        Assert.Equal(25, Plan(Q(size: 25)).PageSize);
        Assert.Equal("tok", Plan(Q(token: "tok")).Token);
    }
}
