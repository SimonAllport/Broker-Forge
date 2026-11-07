using BrokerForge.Descriptors.Model;
using BrokerForge.Descriptors.Parsing;
using BrokerForge.SwaggerForge;

namespace SwaggerForge.Tests;

public class ListQuestionTests
{
    /// <summary>A one-table definition whose list takes <paramref name="parameters"/> (YAML flow items).</summary>
    private static string Things(params string[] parameters) => $$"""
        openapi: 3.0.3
        info: { title: Things, version: "1" }
        components:
          schemas:
            Thing:
              type: object
              properties:
                id: { type: integer }
                name: { type: string, maxLength: 50 }
        paths:
          /things:
            get:
              operationId: listThings
              parameters: [{{string.Join(", ", parameters)}}]
              responses:
                "200": { description: ok, content: { application/json: { schema: { type: array, items: { $ref: "#/components/schemas/Thing" } } } } }
          /things/{id}:
            get:
              operationId: getThing
              parameters: [{ name: id, in: path, required: true, schema: { type: integer } }]
              responses: { "200": { description: ok, content: { application/json: { schema: { $ref: "#/components/schemas/Thing" } } } } }
        """;

    private static DraftReview Review(string text, params (string Key, string Value)[] answers) =>
        DraftService.Review(text, DraftServiceTests.Options, answers.ToDictionary(a => a.Key, a => a.Value));

    private static DraftReview Orders(params (string Key, string Value)[] extra)
    {
        var answers = new Dictionary<string, string>(DrafterTests.AnswersFor("orders-openapi.yaml"));
        foreach (var (k, v) in extra)
            answers[k] = v;
        return DraftServiceTests.Review("orders-openapi.yaml", answers);
    }

    private static Binding Query(OperationDescriptor d, string name) => d.Request.Query.Single(q => q.Key == name).Value;

    private static Question Q(DraftReview r, string key) => r.Endpoints.SelectMany(e => e.Questions).Single(q => q.Key == key);

    private static OperationDescriptor Descriptor(DraftReview r, string op) => DescriptorParser.Parse(DrafterTests.Ready(r).Descriptors[op], op + ".yaml");

    [Fact]
    public void Review_Paging_AlwaysAskedWithCandidates()
    {
        var q = Q(DraftServiceTests.Review("orders-openapi.yaml", new Dictionary<string, string>()), "op.listOrders.paging");
        Assert.Equal(new[] { "cursor", "none" }, q.Choices.Select(c => c.Value));
        Assert.Contains("limit", q.Choices[0].Label);
        Assert.Contains("$.meta.nextCursor", q.Choices[0].Label);
        Assert.Null(q.Answer);

        var paging = Descriptor(Orders(), "listOrders").Paging!;
        Assert.Equal(new TokenSpec("cursor", "$.meta.nextCursor"), paging.Token);
        Assert.Equal(new PageSizeSpec("limit", 100), paging.PageSize);
    }

    [Fact]
    public void Review_PagingNotStated_QuestionAndNotReady()
    {
        var text = Things();
        var r = Review(text, ("op.listThings.sortStable", "yes"));
        var q = Q(r, "op.listThings.paging");
        Assert.Equal(new[] { "none" }, q.Choices.Select(c => c.Value));
        Assert.Equal(EndpointStatus.NeedsAnswers, r.Endpoints.Single(e => e.Operation == "listThings").Status);
        Assert.Null(r.Result);

        var answered = Review(text, ("op.listThings.sortStable", "yes"), ("op.listThings.paging", "none"));
        Assert.Equal("none", Descriptor(answered, "listThings").Paging!.Strategy);
    }

    [Fact]
    public void Review_PageNumberBase_AskedOnlyWithoutMinimum()
    {
        var withMin = Things("{ name: page, in: query, schema: { type: integer, minimum: 0 } }", "{ name: pageSize, in: query, schema: { type: integer, maximum: 25 } }");
        var r = Review(withMin, ("op.listThings.paging", "pageNumber"), ("op.listThings.sortStable", "yes"));
        Assert.DoesNotContain(r.Endpoints.SelectMany(e => e.Questions), x => x.Key == "op.listThings.pageBase");
        Assert.Equal(new PageNumberSpec("page", 0), Descriptor(r, "listThings").Paging!.Page);

        var noMin = Things("{ name: page, in: query, schema: { type: integer } }", "{ name: pageSize, in: query, schema: { type: integer, maximum: 25 } }");
        Assert.True(Q(Review(noMin, ("op.listThings.paging", "pageNumber")), "op.listThings.pageBase").Open);
        var based = Review(noMin, ("op.listThings.paging", "pageNumber"), ("op.listThings.pageBase", "1"), ("op.listThings.sortStable", "yes"));
        Assert.Equal(new PageNumberSpec("page", 1), Descriptor(based, "listThings").Paging!.Page);
    }

    [Fact]
    public void Review_PageSizeMax_AskedOnlyWithoutMaximum()
    {
        var text = Things("{ name: limit, in: query, schema: { type: integer } }", "{ name: offset, in: query, schema: { type: integer } }");
        Assert.True(Q(Review(text, ("op.listThings.paging", "offset")), "op.listThings.pageSizeMax").Open);
        var r = Review(text, ("op.listThings.paging", "offset"), ("op.listThings.pageSizeMax", "200"), ("op.listThings.sortStable", "yes"));
        Assert.Equal(new PageSizeSpec("limit", 200), Descriptor(r, "listThings").Paging!.PageSize);
        Assert.DoesNotContain(Orders().Endpoints.SelectMany(e => e.Questions), x => x.Key == "op.listOrders.pageSizeMax");
    }

    [Fact]
    public void Review_Filters_AskedPerParameter()
    {
        var open = DraftServiceTests.Review("orders-openapi.yaml", new Dictionary<string, string>());
        Assert.Equal(new[] { "none", "eq", "in", "eq,in" }, Q(open, "op.listOrders.filter.status").Choices.Select(c => c.Value));
        Assert.Equal(new[] { "none", "eq", "in", "eq,in", "like" }, Q(open, "op.listOrders.filter.customer").Choices.Select(c => c.Value));
        Assert.DoesNotContain(open.Endpoints.SelectMany(e => e.Questions), x => x.Key is "op.listOrders.filter.limit" or "op.listOrders.filter.cursor" or "op.listOrders.filter.sort");

        var d = Descriptor(Orders(("op.listOrders.filter.status", "eq,in")), "listOrders");
        var status = (FilterBinding)Query(d, "status");
        Assert.Equal(("ext_statecode", "csv"), (status.Column, status.M.Format));
        Assert.Equal(new[] { "eq", "in" }, status.Ops);
        Assert.Equal(new[] { "eq" }, ((FilterBinding)Query(d, "customer")).Ops);
        Assert.Equal(new FiltersSpec(true, "in"), d.Filters);

        var none = Descriptor(Orders(("op.listOrders.filter.customer", "none")), "listOrders");
        Assert.DoesNotContain(none.Request.Query, q => q.Key == "customer");
        Assert.Equal(FiltersSpec.Default, none.Filters);
    }

    [Fact]
    public void Review_FilterAnswerNotAllowedForType_QuestionReopenedWithError()
    {
        var r = Orders(("op.listOrders.filter.status", "like"));
        Assert.NotNull(Q(r, "op.listOrders.filter.status").Error);
        Assert.Equal(EndpointStatus.NeedsAnswers, r.Endpoints.Single(e => e.Operation == "listOrders").Status);
    }

    [Fact]
    public void Review_Sort()
    {
        Assert.Equal(new[] { "none", "asc", "-{field}", "{field} desc" }, Q(Orders(), "op.listOrders.sort").Choices.Select(c => c.Value));

        var d = Descriptor(Orders(("op.listOrders.sort", "-{field}"), ("op.listOrders.sort.$.reference", "reference"), ("op.listOrders.sort.$.placedAt", "placedAt")), "listOrders");
        var sort = (SortBinding)Query(d, "sort");
        Assert.Equal(new Dictionary<string, string> { ["ext_reference"] = "reference", ["ext_placedat"] = "placedAt" }, sort.Fields);
        Assert.Equal("-{field}", sort.DescFormat);

        var asc = (SortBinding)Query(Descriptor(Orders(("op.listOrders.sort", "asc"), ("op.listOrders.sort.$.reference", "reference")), "listOrders"), "sort");
        Assert.Null(asc.DescFormat);

        var nothing = Orders(("op.listOrders.sort", "asc"));
        Assert.NotNull(Q(nothing, "op.listOrders.sort").Error);
        Assert.Null(nothing.Result);
    }

    [Fact]
    public void Review_SortStable_Asked()
    {
        var answers = new Dictionary<string, string>(DrafterTests.AnswersFor("orders-openapi.yaml"));
        answers.Remove("op.listOrders.sortStable");
        var r = DraftServiceTests.Review("orders-openapi.yaml", answers);
        Assert.True(Q(r, "op.listOrders.sortStable").Open);
        Assert.Equal(EndpointStatus.NeedsAnswers, r.Endpoints.Single(e => e.Operation == "listOrders").Status);
        Assert.True(Descriptor(Orders(("op.listOrders.sortStable", "yes")), "listOrders").SortStable);
    }
}
