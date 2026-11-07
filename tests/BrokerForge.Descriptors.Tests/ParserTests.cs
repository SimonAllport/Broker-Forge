using BrokerForge.Descriptors.Model;
using BrokerForge.Descriptors.Parsing;
using static BrokerForge.Descriptors.Tests.Fixtures;

namespace BrokerForge.Descriptors.Tests;

public class ParserTests
{
    public static TheoryData<string> OrdersFiles() => new(Directory.GetFiles(Path.Combine(RepoRoot, "descriptors", "OrdersAPI"), "*.yaml").Select(Path.GetFileNameWithoutExtension)!);

    [Theory]
    [MemberData(nameof(OrdersFiles))]
    public void Parse_AllOrdersDescriptors_Succeeds(string operation)
    {
        var d = Orders(operation);
        Assert.Equal(operation, d.Operation);
        Assert.Equal(1, d.Version);
    }

    [Fact]
    public void Parse_ListOrders_BuildsModel()
    {
        var d = Orders("listOrders");
        Assert.Equal("RetrieveMultiple", d.Binds);
        var status = Assert.IsType<FilterBinding>(d.Request.Query.Single(q => q.Key == "status").Value);
        Assert.Equal("ext_status", status.Column);
        Assert.Equal(new[] { "eq", "in" }, status.Ops);
        Assert.Equal("csv", status.Modifiers.Format);
        var sort = Assert.IsType<SortBinding>(d.Request.Query.Single(q => q.Key == "sort").Value);
        Assert.Equal("reference", sort.Fields["ext_name"]);
        Assert.Equal("-{field}", sort.DescFormat);
        Assert.Equal("cursor", d.Paging!.Strategy);
        Assert.Equal(100, d.Paging.PageSize!.Max);
        Assert.Equal("$.meta.nextCursor", d.Paging.Token!.Next);
        Assert.Equal(5, d.Paging.MaxCallsPerPage);
        Assert.Equal("optionset:orderStatus", d.Response!.Map["ext_status"].Type);
        Assert.Equal(100000001, d.Optionsets["orderStatus"].Values["shipped"]);
        Assert.True(d.Filters.AllowOr);
        Assert.True(d.SortStable);
    }

    [Fact]
    public void Parse_CreateOrder_BodyAndRetry()
    {
        var d = Orders("createOrder");
        var body = Assert.IsType<BodyObject>(d.Request.Body!.Template);
        var customer = Assert.IsType<BodyObject>(body.Members.Single(m => m.Key == "customer").Value);
        var leaf = Assert.IsType<BodyLeaf>(customer.Members.Single().Value);
        Assert.Equal("ext_customerref", Assert.IsType<ColBinding>(leaf.Binding).Column);
        Assert.Equal("application/json", d.Request.Body.ContentType);
        Assert.Equal(new[] { "429", "503", "network" }, d.Retry!.On);
        Assert.Equal("Validation", d.Errors!.Statuses[422].Exception);
        Assert.Equal("$.error.detail", d.Errors.Statuses[422].MessagePath);
    }

    [Fact]
    public void Parse_UnknownKey_ThrowsWithPath()
    {
        var yaml = Mutate(OrdersYaml("listOrders"), "  query:\n", "  qeury:\n");
        var ex = Assert.Throws<DescriptorException>(() => DescriptorParser.Parse(yaml, "listOrders.yaml"));
        Assert.Equal("request.qeury", ex.YamlPath);
        Assert.Equal("listOrders.yaml", ex.Source);
    }

    [Fact]
    public void Parse_BindingWithTwoSources_Throws()
    {
        var yaml = Mutate(OrdersYaml("deleteOrder"), "{ context: correlationId }", "{ context: correlationId, static: x }");
        var ex = Assert.Throws<DescriptorException>(() => DescriptorParser.Parse(yaml, "d"));
        Assert.Equal("request.headers.X-Correlation", ex.YamlPath);
    }

    [Fact]
    public void Parse_BindingWithNoSource_Throws()
    {
        var yaml = Mutate(OrdersYaml("deleteOrder"), "{ context: correlationId }", "{ omitIfNull: true }");
        var ex = Assert.Throws<DescriptorException>(() => DescriptorParser.Parse(yaml, "d"));
        Assert.Equal("request.headers.X-Correlation", ex.YamlPath);
    }

    [Fact]
    public void Parse_UnknownModifier_Throws()
    {
        var yaml = Mutate(OrdersYaml("deleteOrder"), "{ context: correlationId }", "{ context: correlationId, omitifnull: true }");
        Assert.Throws<DescriptorException>(() => DescriptorParser.Parse(yaml, "d"));
    }

    [Theory]
    [InlineData("steps", "steps: []\n", "a later phase")]
    [InlineData("hook", "hook: { pre: X, reason: y }\n", "a later phase")]
    [InlineData("cache", "cache: { ttl: 60s }\n", "phase 5")]
    [InlineData("jsonata", null, "a later phase")]
    public void Parse_DeferredFeature_ThrowsNotSupportedUntilPhase(string feature, string? topLevel, string phase)
    {
        var yaml = OrdersYaml("listOrders");
        yaml = feature switch
        {
            "jsonata" => Mutate(yaml, "ext_name:        $.reference", "ext_name:        { jsonata: \"reference\" }"),
            _ => yaml + topLevel,
        };
        var ex = Assert.Throws<DescriptorException>(() => DescriptorParser.Parse(yaml, "d"));
        Assert.Contains($"not supported until {phase}", ex.Message);
    }

    [Fact]
    public void Parse_InvalidJsonPath_Throws()
    {
        var yaml = Mutate(OrdersYaml("getOrder"), "key: $.id", "key: $.[[");
        var ex = Assert.Throws<DescriptorException>(() => DescriptorParser.Parse(yaml, "d"));
        Assert.Equal("response.key", ex.YamlPath);
    }

    [Fact]
    public void Parse_ScalarStyle_RecordedForBodyTyping()
    {
        var yaml = Mutate(OrdersYaml("createOrder"), "      placedBy: { context: userObjectId }", "      dryRun: false\n      note: \"false\"");
        var body = (BodyObject)DescriptorParser.Parse(yaml, "d").Request.Body!.Template!;
        Assert.False(((LiteralBinding)((BodyLeaf)body.Members.Single(m => m.Key == "dryRun").Value).Binding).Quoted);
        Assert.True(((LiteralBinding)((BodyLeaf)body.Members.Single(m => m.Key == "note").Value).Binding).Quoted);
    }

    [Fact]
    public void Parse_RawBody_PlaceholdersBecomeBindings()
    {
        var yaml = Mutate(OrdersYaml("updateOrder"), "    template:\n      customer: { ref: { col: ext_customerref } }\n      state:    { code: { col: ext_status, optionset: orderStatus } }\n      lines:    { col: ext_lines_json, format: json }\n      totals:   { gross: { col: ext_total } }\n",
            "    contentType: text/xml\n    raw: \"<o><c>{{ col.ext_customerref }}</c><id>{{ context.correlationId }}</id></o>\"\n");
        var body = DescriptorParser.Parse(yaml, "d").Request.Body!;
        Assert.Equal("text/xml", body.ContentType);
        Assert.Collection(body.RawPlaceholders,
            p => Assert.Equal("ext_customerref", Assert.IsType<ColBinding>(p.Binding).Column),
            p => Assert.Equal("correlationId", Assert.IsType<ContextBinding>(p.Binding).Name));
    }

    [Fact]
    public void Parse_MoneyType_RejectedBecauseVirtualTablesHaveNoMoneyColumns()
    {
        var yaml = Mutate(OrdersYaml("listOrders"), "{ path: $.totals.gross, type: decimal }", "{ path: $.totals.gross, type: money }");
        var ex = Assert.Throws<DescriptorException>(() => DescriptorParser.Parse(yaml, "d"));
        Assert.Contains("decimal", ex.Message);
        Assert.Contains("Money", ex.Message);
    }
}
