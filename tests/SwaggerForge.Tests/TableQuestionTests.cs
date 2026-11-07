using BrokerForge.Descriptors.Model;
using BrokerForge.Descriptors.Parsing;
using BrokerForge.Schema;
using BrokerForge.SwaggerForge;

namespace SwaggerForge.Tests;

public class TableQuestionTests
{
    private const string TwoCollections = """
        openapi: 3.0.3
        info: { title: Two, version: "1" }
        components:
          schemas:
            Thing:
              type: object
              properties:
                id: { type: integer }
                name: { type: string, maxLength: 50 }
        paths:
          /widgets/{id}:
            get:
              operationId: getWidget
              parameters: [{ name: id, in: path, required: true, schema: { type: integer } }]
              responses: { "200": { description: ok, content: { application/json: { schema: { $ref: "#/components/schemas/Thing" } } } } }
          /gadgets/{id}:
            get:
              operationId: getGadget
              parameters: [{ name: id, in: path, required: true, schema: { type: integer } }]
              responses: { "200": { description: ok, content: { application/json: { schema: { $ref: "#/components/schemas/Thing" } } } } }
        """;

    private static Dictionary<string, string> With(string fixture, params (string Key, string Value)[] extra)
    {
        var answers = new Dictionary<string, string>(DrafterTests.AnswersFor(fixture));
        foreach (var (k, v) in extra)
            answers[k] = v;
        return answers;
    }

    [Fact]
    public void Review_TableExternalName_ProposedFromTheTableNameAndAnswerable()
    {
        var proposed = DraftServiceTests.Review("orders-openapi.yaml", With("orders-openapi.yaml", ("table.name", "ext_purchase")));
        Assert.Equal("purchase", proposed.Table!.Questions.Single(q => q.Key == "table.externalName").Value);
        Assert.Contains("externalName: purchase", DrafterTests.Ready(proposed).SchemaYaml);

        var answered = DraftServiceTests.Review("orders-openapi.yaml", With("orders-openapi.yaml", ("table.name", "ext_purchase"), ("table.externalName", "order")));
        Assert.Contains("externalName: order", DrafterTests.Ready(answered).SchemaYaml);

        var refused = DraftServiceTests.Review("orders-openapi.yaml", With("orders-openapi.yaml", ("table.externalName", "Order Lines")));
        Assert.NotNull(refused.Table!.Questions.Single(q => q.Key == "table.externalName").Error);
    }

    private static Question Table(DraftReview r, string key) => Assert.Single(r.Table!.Questions, q => q.Key == key);

    [Fact]
    public void Review_TwoCollections_AsksWhichAndProposesNothingUntilAnswered()
    {
        var r = DraftService.Review(TwoCollections, DraftServiceTests.Options, new Dictionary<string, string>());
        var q = Table(r, "table.collection");
        Assert.Equal(new[] { "/gadgets", "/widgets" }, q.Choices.Select(c => c.Value));
        Assert.Null(r.Table!.Collection);
        Assert.All(r.Endpoints, e => Assert.Equal(EndpointStatus.NeedsAnswers, e.Status));
        Assert.All(r.Endpoints, e => Assert.Equal("Open", e.Becomes));
        Assert.Null(r.Result);

        var answered = DraftService.Review(TwoCollections, DraftServiceTests.Options, new Dictionary<string, string> { ["table.collection"] = "/widgets" });
        Assert.Equal("ext_widget", answered.Table!.LogicalName);
        Assert.Equal(EndpointStatus.Ready, Assert.Single(answered.Endpoints, e => e.Operation == "getWidget").Status);
        Assert.Equal(EndpointStatus.Unsupported, Assert.Single(answered.Endpoints, e => e.Operation == "getGadget").Status);
    }

    [Fact]
    public void Review_StringWithoutMaxLength_Asked()
    {
        Assert.DoesNotContain(DraftServiceTests.Review("orders-openapi.yaml").Table!.Questions, q => q.Key == "column.$.customer.ref.maxLength");

        string Edit(string t) => t.Replace("ref: { type: string, maxLength: 100 }", "ref: { type: string }");
        var open = DraftServiceTests.Review("orders-openapi.yaml", edit: Edit);
        Assert.True(Table(open, "column.$.customer.ref.maxLength").Open);
        Assert.Null(open.Result);
        foreach (var bad in new[] { "0", "4001", "ten" })
            Assert.NotNull(Table(DraftServiceTests.Review("orders-openapi.yaml", With("orders-openapi.yaml", ("column.$.customer.ref.maxLength", bad)), Edit), "column.$.customer.ref.maxLength").Error);

        var r = DraftServiceTests.Review("orders-openapi.yaml", With("orders-openapi.yaml", ("column.$.customer.ref.maxLength", "60")), Edit);
        Assert.Equal(60, SchemaParser.Parse(DrafterTests.Ready(r).SchemaYaml, "x.yaml").Columns["ext_customerref"].MaxLength);
    }

    [Fact]
    public void Review_NumberPrecision_AskedUnlessMultipleOf()
    {
        var asked = DraftServiceTests.Review("orders-openapi.yaml", new Dictionary<string, string>());
        Assert.True(Table(asked, "column.$.totals.gross.precision").Open);

        var r = DraftServiceTests.Review("orders-openapi.yaml", edit: t => t.Replace("gross: { type: number }\n    OrderInput", "gross: { type: number, multipleOf: 0.01 }\n    OrderInput"));
        Assert.DoesNotContain(r.Table!.Questions, q => q.Key == "column.$.totals.gross.precision");
        Assert.Equal(2, SchemaParser.Parse(DrafterTests.Ready(r).SchemaYaml, "x.yaml").Columns["ext_totalsgross"].Precision);
    }

    [Fact]
    public void Review_PrimaryName()
    {
        var q = Table(DraftServiceTests.Review("orders-openapi.yaml", new Dictionary<string, string>()), "table.primaryName");
        Assert.Equal(new[] { "$.reference", "$.customer.ref" }, q.Choices.Select(c => c.Value));
        var r = DraftServiceTests.Review("orders-openapi.yaml");
        Assert.Equal("ext_reference", SchemaParser.Parse(DrafterTests.Ready(r).SchemaYaml, "x.yaml").PrimaryName);

        var inventory = DraftServiceTests.Review("inventory-openapi.yaml");
        Assert.DoesNotContain(inventory.Table!.Questions, x => x.Key == "table.primaryName");
        Assert.Equal("ext_name", inventory.Table.PrimaryName);
    }

    [Fact]
    public void Review_NoScalarId_AsksKey()
    {
        string Edit(string t) => t.Replace("id: { type: integer }\n        name:", "code: { type: string, maxLength: 10 }\n        name:");
        var open = DraftServiceTests.Review("unsupported.yaml", new Dictionary<string, string>(), Edit);
        var q = Table(open, "table.key");
        Assert.Equal(new[] { "$.code", "$.name", "$.onHand" }, q.Choices.Select(c => c.Value));

        var r = DraftServiceTests.Review("unsupported.yaml", With("unsupported.yaml", ("table.key", "$.code")), Edit);
        Assert.Equal(new PathKey("$.code"), DescriptorParser.Parse(DrafterTests.Ready(r).Descriptors["getWidget"], "g.yaml").Response!.Key);
        Assert.DoesNotContain(r.Table!.Columns, c => c.SourcePath == "$.code");
    }

    [Fact]
    public void Review_ColumnRename_AppliedEverywhere()
    {
        var r = DraftServiceTests.Review("orders-openapi.yaml", With("orders-openapi.yaml",
            ("column.$.reference.name", "ext_name"), ("column.$.reference.displayName", "Reference"),
            ("column.$.customer.ref.name", "ext_customer")));
        var result = DrafterTests.Ready(r);
        var schema = SchemaParser.Parse(result.SchemaYaml, "x.yaml");
        Assert.Equal("Reference", schema.Columns["ext_name"].DisplayName);
        Assert.Equal("ext_name", schema.PrimaryName);
        Assert.Equal("$.reference", DescriptorParser.Parse(result.Descriptors["listOrders"], "l.yaml").Response!.Map["ext_name"].Path);
        Assert.Contains("col: ext_customer", result.Descriptors["createOrder"]);
        Assert.Contains("col: ext_customer", result.Descriptors["updateOrder"]);
        Assert.DoesNotContain("ext_customerref", string.Concat(result.Descriptors.Values) + result.SchemaYaml);
    }

    [Theory]
    [InlineData("ext_same", "ext_same")]
    [InlineData("order_ref", null)]
    [InlineData("ext_orderid", null)]
    [InlineData("Ext_Ref", null)]
    public void Review_ColumnRenameCollision_ErrorOnBothNoResult(string first, string? second)
    {
        var answers = With("orders-openapi.yaml", ("column.$.reference.name", first));
        if (second != null)
            answers["column.$.customer.ref.name"] = second;
        var r = DraftServiceTests.Review("orders-openapi.yaml", answers);
        Assert.NotNull(Table(r, "column.$.reference.name").Error);
        if (second != null)
            Assert.NotNull(Table(r, "column.$.customer.ref.name").Error);
        Assert.Null(r.Result);
    }
}
