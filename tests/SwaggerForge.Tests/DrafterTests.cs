using BrokerForge.Descriptors.Model;
using BrokerForge.Descriptors.Parsing;
using BrokerForge.Descriptors.Validation;
using BrokerForge.Schema;
using BrokerForge.SwaggerForge;
using Microsoft.OpenApi.Models;
using Microsoft.OpenApi.Readers;

namespace SwaggerForge.Tests;

public class DrafterTests
{
    internal static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    internal static OpenApiDocument Read(string name, Func<string, string>? edit = null)
    {
        var text = File.ReadAllText(Fixture(name));
        if (edit != null)
        {
            var edited = edit(text);
            Assert.NotEqual(text, edited);
            text = edited;
        }
        var doc = new OpenApiStringReader().Read(text, out var diagnostic);
        Assert.Empty(diagnostic.Errors);
        return doc;
    }

    /// <summary>The answers each fixture needs to be ready, with the drafter's own names (no renames).</summary>
    internal static IReadOnlyDictionary<string, string> AnswersFor(string fixture) => fixture switch
    {
        "orders-openapi.yaml" => new Dictionary<string, string>
        {
            ["column.$.totals.gross.precision"] = "2",
            ["table.primaryName"] = "$.reference",
            ["op.listOrders.paging"] = "cursor",
            ["op.listOrders.filter.status"] = "eq",
            ["op.listOrders.filter.customer"] = "eq",
            ["op.listOrders.sort"] = "none",
            ["op.listOrders.sortStable"] = "no",
            ["op.updateOrder.error.422.message"] = "none",
            ["op.createOrder.value.accountId"] = "acc-1",
            ["op.createOrder.error.422.message"] = "$.error.detail",
        },
        "inventory-openapi.yaml" => new Dictionary<string, string>
        {
            ["column.$.price.precision"] = "2",
            ["op.listItems.paging"] = "offset",
            ["op.listItems.filter.state"] = "eq",
            ["op.listItems.filter.name"] = "eq",
            ["op.listItems.sortStable"] = "yes",
            ["op.createItem.error.422.message"] = "$.error.detail",
        },
        "unsupported.yaml" => new Dictionary<string, string>
        {
            ["op.listWidgets.paging"] = "offset",
            ["op.listWidgets.filter.name"] = "eq",
            ["op.listWidgets.sortStable"] = "no",
        },
        _ => new Dictionary<string, string>(),
    };

    /// <summary>The result of a ready review; fails the test with the open endpoints when it is not ready.</summary>
    internal static DraftResult Ready(DraftReview r) =>
        r.Result ?? throw new Xunit.Sdk.XunitException("not ready: " + string.Join("; ", r.Problems.Concat(
            r.Endpoints.Where(e => e.Status is not (EndpointStatus.Ready or EndpointStatus.NotUsed or EndpointStatus.Unsupported))
                .Select(e => $"{e.Operation} {e.Status} [{string.Join(", ", e.Questions.Where(q => q.Open).Select(q => q.Key).Concat(e.Problems))}]"))
            .Concat(r.Table?.Questions.Where(q => q.Open).Select(q => $"table {q.Key} {q.Error}") ?? Array.Empty<string>())));

    private static DraftResult Orders(Func<string, string>? edit = null, params (string Key, string Value)[] extra)
    {
        var answers = new Dictionary<string, string>(AnswersFor("orders-openapi.yaml"));
        foreach (var (k, v) in extra)
            answers[k] = v;
        return Ready(DraftServiceTests.Review("orders-openapi.yaml", answers, edit));
    }

    private static DraftResult Widgets() => Ready(DraftServiceTests.Review("unsupported.yaml"));

    private static OperationDescriptor Descriptor(DraftResult r, string operation) => DescriptorParser.Parse(r.Descriptors[operation], operation + ".yaml");

    [Fact]
    public void Draft_OrdersOpenApi_EmitsFiveCrudDescriptorsThatValidate()
    {
        var r = Orders();
        Assert.Equal(new[] { "createOrder", "deleteOrder", "getOrder", "listOrders", "updateOrder" }, r.Descriptors.Keys.Order());
        var schema = SchemaParser.Parse(r.SchemaYaml, "OrdersAPI.yaml");
        foreach (var (operation, yaml) in r.Descriptors)
        {
            var d = DescriptorParser.Parse(yaml, operation + ".yaml");
            Assert.Empty(DescriptorValidator.Validate(d, ReservedNames.For(new[] { "X-Api-Key" }), schema));
        }
        Assert.Equal("RetrieveMultiple", Descriptor(r, "listOrders").Binds);
        Assert.Equal("Create", Descriptor(r, "createOrder").Binds);
        Assert.Equal("Update", Descriptor(r, "updateOrder").Binds);
        Assert.Equal("Delete", Descriptor(r, "deleteOrder").Binds);
        Assert.Empty(r.Skipped);
    }

    [Fact]
    public void Draft_OrdersOpenApi_SchemaMatchesOrdersColumns()
    {
        var r = Orders();
        var schema = SchemaParser.Parse(r.SchemaYaml, "OrdersAPI.yaml");
        Assert.Equal("ext_order", schema.Table);
        var list = Descriptor(r, "listOrders");
        // The same source fields and types as the hand-written Orders descriptors and schemas/OrdersAPI.yaml.
        var byPath = list.Response!.Map.ToDictionary(m => m.Value.Path, m => (Column: m.Key, schema.Columns[m.Key].Type));
        Assert.Equal(new[] { "$.customer.ref", "$.lines", "$.placedAt", "$.reference", "$.state.code", "$.totals.gross" }, byPath.Keys.Order());
        Assert.Equal(("ext_reference", "string"), byPath["$.reference"]);
        Assert.Equal(("ext_statecode", "optionset"), byPath["$.state.code"]);
        Assert.Equal(("ext_customerref", "string"), byPath["$.customer.ref"]);
        Assert.Equal(("ext_placedat", "datetime"), byPath["$.placedAt"]);
        Assert.Equal(("ext_lines", "memo"), byPath["$.lines"]);
        Assert.Equal(("ext_totalsgross", "decimal"), byPath["$.totals.gross"]);
        Assert.Equal("json", list.Response.Map["ext_lines"].Type);
        Assert.Equal(new Dictionary<string, int> { ["open"] = 100000000, ["shipped"] = 100000001, ["cancelled"] = 100000002 }, schema.Columns["ext_statecode"].Options);
        Assert.Equal(100, schema.Columns["ext_reference"].MaxLength);
        Assert.Equal("ext_reference", schema.PrimaryName);

        var create = Descriptor(r, "createOrder");
        Assert.Equal(new StaticBinding("acc-1", false, BindingModifiers.None), create.Request.PathParams["accountId"]);
        Assert.Equal(new PathKey("$.id"), create.Response!.Key);
    }

    [Fact]
    public void Draft_FiltersFromQueryParameters()
    {
        var list = Descriptor(Orders(), "listOrders");
        var filters = list.Request.Query.Where(q => q.Value is FilterBinding).ToDictionary(q => q.Key, q => (FilterBinding)q.Value);
        Assert.Equal("ext_statecode", filters["status"].Column);
        Assert.Equal("ext_customerref", filters["customer"].Column);
        Assert.Equal(new[] { "eq" }, filters["status"].Ops);
        Assert.DoesNotContain(list.Request.Query, q => q.Key == "sort");
        Assert.False(list.SortStable);
    }

    [Fact]
    public void Draft_CursorPagingIdentified()
    {
        var paging = Descriptor(Orders(), "listOrders").Paging!;
        Assert.Equal("cursor", paging.Strategy);
        Assert.Equal(new PageSizeSpec("limit", 100), paging.PageSize);
        Assert.Equal(new TokenSpec("cursor", "$.meta.nextCursor"), paging.Token);
    }

    [Fact]
    public void Draft_OffsetPagingWithTotalIdentified()
    {
        var r = Widgets();
        var paging = Descriptor(r, "listWidgets").Paging!;
        Assert.Equal("offset", paging.Strategy);
        Assert.Equal(new OffsetSpec("offset", "$.total"), paging.Offset);
        Assert.Equal(new PageSizeSpec("limit", 50), paging.PageSize);
        Assert.Equal("$.items[*]", Descriptor(r, "listWidgets").Response!.Rows);
    }

    [Fact]
    public void Draft_UnsupportedShapes_SkippedWithReasons()
    {
        var r = Widgets();
        string Reason(string path) => Assert.Single(r.Skipped, s => s.Path == path).Reason;
        Assert.Contains("oneOf", Reason("/widgets/{widgetId}/variant"));
        Assert.Contains("refers to itself", Reason("/widgets/{widgetId}/tree"));
        Assert.Contains("operationId", Reason("/widgets/{widgetId}/ping"));
        Assert.Contains("response schema", Reason("/widgets/{widgetId}/touch"));
        Assert.Equal(4, r.Skipped.Count);
        Assert.Equal(new[] { "adjustWidget", "getWidget", "listWidgets" }, r.Descriptors.Keys.Order());
    }

    [Fact]
    public void Draft_ActionBecomesCustomApi()
    {
        var r = Widgets();
        var d = Descriptor(r, "adjustWidget");
        Assert.Equal("customapi:ext_AdjustWidget", d.Binds);
        Assert.Equal(new ParamBinding("WidgetId", BindingModifiers.None), d.Request.PathParams["widgetId"]);
        var api = SchemaParser.Parse(r.SchemaYaml, "WidgetsAPI.yaml").CustomApis["ext_AdjustWidget"];
        Assert.Equal(new ParamSchema("int", true), api.Inputs["WidgetId"]);
        Assert.Equal(new ParamSchema("int", true), api.Inputs["Delta"]);
        Assert.Equal(new ParamSchema("string", false), api.Inputs["Reason"]);
        Assert.Equal(new Dictionary<string, string> { ["OnHand"] = "int", ["AdjustedAt"] = "datetime" }, api.Outputs);
        Assert.Equal(new MapEntry("$.adjustedAt", "datetime", true), d.Response!.Outputs["AdjustedAt"]);
    }

    [Fact]
    public void Draft_IntegerKeyNotAColumn()
    {
        var r = Widgets();
        var schema = SchemaParser.Parse(r.SchemaYaml, "WidgetsAPI.yaml");
        Assert.Equal(new[] { "ext_name", "ext_onhand" }, schema.Columns.Keys);
        Assert.Equal("ext_name", schema.PrimaryName);
        Assert.Equal(new PathKey("$.id"), Descriptor(r, "getWidget").Response!.Key);
    }

    [Fact]
    public void Draft_AwkwardPropertyNames_BracketPathOrSkippedWithNote()
    {
        var r = Orders(t => t.Replace("        id: { type: string }\n        reference:", "        id: { type: string }\n        unit-price: { type: number }\n        café: { type: string }\n        reference:"),
            ("column.$['unit-price'].precision", "2"));
        var list = Descriptor(r, "listOrders");
        Assert.Equal("$['unit-price']", list.Response!.Map["ext_unitprice"].Path);
        Assert.DoesNotContain(list.Response.Map.Keys, k => k.Contains('é'));
        Assert.Contains(r.Notes, n => n.Contains("café"));
        Assert.Empty(r.Skipped);
    }
}
