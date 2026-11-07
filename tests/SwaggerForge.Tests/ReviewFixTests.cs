using System.Diagnostics;
using System.Text;
using BrokerForge.Descriptors.Parsing;
using BrokerForge.Schema;
using BrokerForge.SwaggerForge;

namespace SwaggerForge.Tests;

/// <summary>Findings of the plan 2 final review: inputs an admin can paste that must not throw, hang or leave an endpoint unfixable.</summary>
public class ReviewFixTests
{
    private static string Widgets(string extraProps = "", string listParams = "", string pathLevel = "", string itemOps = "", string listOpId = "listWidgets",
        string listResponses = "") => $$"""
        openapi: 3.0.3
        info: { title: W, version: "1" }
        components:
          schemas:
            Widget:
              type: object
              properties:
                id: { type: integer }
                name: { type: string, maxLength: 50 }
        {{extraProps}}
        paths:
          /widgets:
        {{pathLevel}}
            get:
              operationId: {{listOpId}}
              parameters: [{{listParams}}]
              responses:
                "200": { description: ok, content: { application/json: { schema: { type: array, items: { $ref: "#/components/schemas/Widget" } } } } }
        {{listResponses}}
          /widgets/{id}:
            parameters: [{ name: id, in: path, required: true, schema: { type: integer } }]
            get:
              operationId: getWidget
              parameters: [{ name: id, in: path, required: true, schema: { type: integer } }]
              responses: { "200": { description: ok, content: { application/json: { schema: { $ref: "#/components/schemas/Widget" } } } } }
        {{itemOps}}
        """;

    private static readonly Dictionary<string, string> ListAnswers = new() { ["op.listWidgets.paging"] = "none", ["op.listWidgets.sortStable"] = "yes" };

    private static DraftReview Review(string text, params (string Key, string Value)[] extra)
    {
        var answers = new Dictionary<string, string>(ListAnswers);
        foreach (var (k, v) in extra)
            answers[k] = v;
        return DraftService.Review(text, DraftServiceTests.Options, answers);
    }

    private static IEnumerable<Question> All(DraftReview r) => (r.Table?.Questions ?? Array.Empty<Question>()).Concat(r.Endpoints.SelectMany(e => e.Questions));

    [Fact]
    public void Review_ParameterAtPathAndOperationLevel_OperationWinsNoThrow()
    {
        var text = Widgets(pathLevel: "    parameters: [{ name: name, in: query, schema: { type: string } }]",
            listParams: "{ name: name, in: query, schema: { type: string } }");
        var r = Review(text, ("op.listWidgets.filter.name", "eq"));
        Assert.True(r.Ready, string.Join("; ", r.Problems.Concat(r.Endpoints.Select(e => $"{e.Operation} {e.Status} {string.Join(",", e.Problems)}"))));
        Assert.Single(r.Endpoints.Single(e => e.Operation == "listWidgets").Questions, q => q.Key == "op.listWidgets.filter.name");
        Assert.Equal(EndpointStatus.Ready, r.Endpoints.Single(e => e.Operation == "getWidget").Status);
    }

    [Theory]
    [InlineData("GetWidget")]
    [InlineData("getWidget")]
    public void Review_SameOperationName_BothUnsupportedNoThrow(string listOpId)
    {
        var r = Review(Widgets(listOpId: listOpId));
        var clash = r.Endpoints.Where(e => e.Reason?.Contains("more than one operation") == true).ToList();
        Assert.Equal(2, clash.Count);
        Assert.All(clash, e => Assert.Equal(EndpointStatus.Unsupported, e.Status));
    }

    [Theory]
    [InlineData("{ name: limit, in: query, schema: { type: integer, maximum: 99999999999 } }, { name: offset, in: query, schema: { type: integer } }", "offset", "op.listWidgets.pageSizeMax")]
    [InlineData("{ name: limit, in: query, schema: { type: integer, maximum: 0 } }, { name: offset, in: query, schema: { type: integer } }", "offset", "op.listWidgets.pageSizeMax")]
    [InlineData("{ name: page, in: query, schema: { type: integer, minimum: 99999999999 } }, { name: pageSize, in: query, schema: { type: integer, maximum: 20 } }", "pageNumber", "op.listWidgets.pageBase")]
    [InlineData("{ name: page, in: query, schema: { type: integer, minimum: 7 } }, { name: pageSize, in: query, schema: { type: integer, maximum: 20 } }", "pageNumber", "op.listWidgets.pageBase")]
    public void Review_PagingBoundOutOfRange_AskedNotThrown(string parameters, string paging, string asked)
    {
        var r = Review(Widgets(listParams: parameters), ("op.listWidgets.paging", paging));
        Assert.True(All(r).Single(q => q.Key == asked).Open);
    }

    [Fact]
    public void Review_SharedSchemasDeep_CheckedInLinearTime()
    {
        var schemas = new StringBuilder();
        const int levels = 40;
        for (var i = 0; i < levels; i++)
            schemas.Append($"    S{i}:\n      type: object\n      properties:\n        a: {{ $ref: \"#/components/schemas/S{i + 1}\" }}\n        b: {{ $ref: \"#/components/schemas/S{i + 1}\" }}\n");
        schemas.Append($"    S{levels}:\n      type: object\n      properties:\n        x: {{ type: string }}\n");
        var text = Widgets(extraProps: schemas.ToString(),
            listResponses: "        \"400\": { description: bad, content: { application/json: { schema: { $ref: \"#/components/schemas/S0\" } } } }");
        var clock = Stopwatch.StartNew();
        DraftService.Review(text, DraftServiceTests.Options, ListAnswers);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"took {clock.Elapsed}");
    }

    [Fact]
    public void Review_StatusRange_NotADescriptorKey()
    {
        var r = Review(Widgets(listResponses: "        \"5XX\": { description: server error }"));
        Assert.True(r.Ready, string.Join("; ", r.Endpoints.SelectMany(e => e.Problems)));
        Assert.DoesNotContain("5XX", DrafterTests.Ready(r).Descriptors["listWidgets"]);
    }

    [Theory]
    [InlineData("list-widgets")]
    [InlineData("widgets.list")]
    [InlineData("list_widgets")]
    public void Review_OperationIdNotCamelCase_NameMadeCamelCase(string id)
    {
        var r = DraftService.Review(Widgets(listOpId: id), DraftServiceTests.Options, new Dictionary<string, string>
        {
            ["op.listWidgets.paging"] = "none", ["op.listWidgets.sortStable"] = "yes", ["op.widgetsList.paging"] = "none", ["op.widgetsList.sortStable"] = "yes",
        });
        Assert.True(r.Ready, string.Join("; ", r.Endpoints.Select(e => $"{e.Operation} {e.Status} {e.Reason} {string.Join(",", e.Problems)}")));
        Assert.Contains(DrafterTests.Ready(r).Descriptors.Keys, k => k is "listWidgets" or "widgetsList");
    }

    [Fact]
    public void Review_ProposedColumnIsTableKey_ErrorOnNameQuestion()
    {
        var text = Widgets(extraProps: "", pathLevel: "").Replace("        name: { type: string, maxLength: 50 }\n", "        name: { type: string, maxLength: 50 }\n        widgetId: { type: integer }\n");
        var r = Review(text);
        var q = r.Table!.Questions.Single(x => x.Key == "column.$.widgetId.name");
        Assert.NotNull(q.Error);
        Assert.Null(r.Result);
        var renamed = Review(text, ("column.$.widgetId.name", "ext_widgetref"));
        Assert.True(renamed.Ready);
    }

    [Fact]
    public void Review_ProposedNameCollision_RenameAskedNotDropped()
    {
        var text = Widgets().Replace("        name: { type: string, maxLength: 50 }\n", "        name: { type: string, maxLength: 50 }\n        first-name: { type: string, maxLength: 20 }\n        firstName: { type: string, maxLength: 20 }\n");
        var r = Review(text);
        Assert.Null(r.Result);
        Assert.Contains(r.Table!.Questions, q => q.Key.StartsWith("column.$") && q.Key.EndsWith(".name") && q.Error != null);
        var fixedUp = Review(text, ("column.$.firstName.name", "ext_firstname2"));
        Assert.True(fixedUp.Ready);
        Assert.Equal(2, fixedUp.Table!.Columns.Count(c => c.SourcePath.Contains("irst")));
    }

    [Fact]
    public void Review_PutAndPatchOnItem_AskWhichIsSave()
    {
        const string ops = """
                patch:
                  operationId: patchWidget
                  requestBody: { content: { application/json: { schema: { type: object, properties: { name: { type: string } } } } } }
                  responses: { "204": { description: ok } }
                put:
                  operationId: putWidget
                  requestBody: { content: { application/json: { schema: { type: object, properties: { name: { type: string } } } } } }
                  responses: { "204": { description: ok } }
            """;
        var text = Widgets(itemOps: ops);
        var open = Review(text);
        var patch = open.Endpoints.Single(e => e.Operation == "patchWidget");
        Assert.Equal(EndpointStatus.NeedsAnswers, patch.Status);
        var q = patch.Questions.Single(x => x.Key == "table.save");
        Assert.Equal(new[] { "patchWidget", "putWidget" }, q.Choices.Select(c => c.Value));
        Assert.Null(open.Result);

        var r = Review(text, ("table.save", "putWidget"));
        Assert.Equal(EndpointStatus.Unsupported, r.Endpoints.Single(e => e.Operation == "patchWidget").Status);
        Assert.Single(DrafterTests.Ready(r).Descriptors.Values, y => y.Contains("binds: Update"));
    }

    [Fact]
    public void Review_IntegerEnum_IntColumnNotUnsupported()
    {
        var text = Widgets().Replace("        name: { type: string, maxLength: 50 }\n", "        name: { type: string, maxLength: 50 }\n        prio: { type: integer, enum: [1, 2, 3] }\n");
        var r = Review(text);
        Assert.True(r.Ready, string.Join("; ", r.Endpoints.Select(e => $"{e.Operation} {e.Status} {e.Reason}")));
        Assert.Equal("int", SchemaParser.Parse(DrafterTests.Ready(r).SchemaYaml, "x.yaml").Columns["ext_prio"].Type);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100000)]
    public void Review_DefinitionMaxLengthOutOfRange_Asked(int length)
    {
        var text = Widgets().Replace("name: { type: string, maxLength: 50 }", $"name: {{ type: string, maxLength: {length} }}");
        var q = Review(text).Table!.Questions.Single(x => x.Key == "column.$.name.maxLength");
        Assert.NotNull(q.Error);
        Assert.Equal(200, SchemaParser.Parse(DrafterTests.Ready(Review(text, ("column.$.name.maxLength", "200"))).SchemaYaml, "x.yaml").Columns["ext_name"].MaxLength);
    }

    [Fact]
    public void Review_NotesVisibleBeforeReady()
    {
        var r = DraftService.Review(Widgets(listParams: "{ name: q, in: query, schema: { type: object } }"), DraftServiceTests.Options, new Dictionary<string, string>());
        Assert.Null(r.Result);
        Assert.Contains(r.Notes, n => n.Contains("query parameter q"));
    }

    [Fact]
    public void Review_AnsweredNameClashesWithTwoProposals_ProposalsStillRefused()
    {
        var text = Widgets().Replace("        name: { type: string, maxLength: 50 }\n", "        name: { type: string, maxLength: 50 }\n        first-name: { type: string, maxLength: 20 }\n        firstName: { type: string, maxLength: 20 }\n");
        var r = Review(text, ("column.$.name.name", "ext_firstname"));
        Assert.Null(r.Result);
        var unrefused = r.Table!.Questions.Where(q => q.Key.StartsWith("column.") && q.Key.EndsWith(".name") && q.Error == null);
        Assert.All(unrefused.GroupBy(q => q.Value), g => Assert.Single(g));
    }

    [Fact]
    public void Review_OnlyActionsInUse_NoTableQuestionsLeftOpen()
    {
        const string ops = """
              /widgets/{id}/archive:
                post:
                  operationId: archiveWidget
                  parameters: [{ name: id, in: path, required: true, schema: { type: integer } }]
                  responses: { "200": { description: ok, content: { application/json: { schema: { type: object, properties: { archivedAt: { type: string } } } } } } }
            """;
        var text = Widgets(itemOps: ops).Replace("        name: { type: string, maxLength: 50 }\n", "        name: { type: string, maxLength: 50 }\n        price: { type: number }\n");
        var r = Review(text, ("op.listWidgets.use", "no"), ("op.getWidget.use", "no"));
        Assert.True(r.Ready, string.Join("; ", r.Endpoints.Select(e => $"{e.Operation} {e.Status} {e.Reason} {string.Join(",", e.Questions.Where(q => q.Open).Select(q => q.Key))}")));
        Assert.DoesNotContain(All(r), q => q.Open);
        Assert.Contains(r.Notes, n => n.Contains("no table"));
    }

    [Fact]
    public void Review_NullDefinitionOrAnswers_NoThrow()
    {
        Assert.Single(DraftService.Review(null!, DraftServiceTests.Options, new Dictionary<string, string>()).Problems);
        Assert.Null(DraftService.Review(Widgets(), DraftServiceTests.Options, null!).Result);
        var r = DraftService.Review(Widgets(), DraftServiceTests.Options, new Dictionary<string, string> { ["op.listWidgets.paging"] = null!, ["op.listWidgets.sortStable"] = "yes" });
        Assert.True(All(r).Single(q => q.Key == "op.listWidgets.paging").Open);
    }

    [Fact]
    public void Review_WaitingForCollection_EndpointShowsTheQuestion()
    {
        var two = Widgets().Replace("paths:\n", "paths:\n  /gadgets/{id}:\n    get:\n      operationId: getGadget\n      parameters: [{ name: id, in: path, required: true, schema: { type: integer } }]\n      responses: { \"200\": { description: ok, content: { application/json: { schema: { $ref: \"#/components/schemas/Widget\" } } } } }\n");
        var r = DraftService.Review(two, DraftServiceTests.Options, new Dictionary<string, string>());
        Assert.All(r.Endpoints.Where(e => e.Status == EndpointStatus.NeedsAnswers), e => Assert.Contains(e.Questions, q => q.Key == "table.collection"));
    }
}
