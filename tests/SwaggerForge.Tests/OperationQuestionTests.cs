using BrokerForge.Descriptors.Model;
using BrokerForge.Descriptors.Parsing;
using BrokerForge.SwaggerForge;

namespace SwaggerForge.Tests;

public class OperationQuestionTests
{
    private static Dictionary<string, string> With(string fixture, params (string Key, string Value)[] extra)
    {
        var answers = new Dictionary<string, string>(DrafterTests.AnswersFor(fixture));
        foreach (var (k, v) in extra)
            answers[k] = v;
        return answers;
    }

    private static Question Q(DraftReview r, string key) => r.Endpoints.SelectMany(e => e.Questions).Single(q => q.Key == key);

    private static bool Asked(DraftReview r, string key) => r.Endpoints.SelectMany(e => e.Questions).Any(q => q.Key == key);

    private static OperationDescriptor Descriptor(DraftReview r, string op) => DescriptorParser.Parse(DrafterTests.Ready(r).Descriptors[op], op + ".yaml");

    [Fact]
    public void Review_NonIdPathParameter_AsksFixedValue()
    {
        var answers = With("orders-openapi.yaml");
        answers.Remove("op.createOrder.value.accountId");
        var open = DraftServiceTests.Review("orders-openapi.yaml", answers);
        Assert.True(Q(open, "op.createOrder.value.accountId").Open);
        Assert.Equal(EndpointStatus.NeedsAnswers, open.Endpoints.Single(e => e.Operation == "createOrder").Status);

        var r = DraftServiceTests.Review("orders-openapi.yaml", With("orders-openapi.yaml", ("op.createOrder.value.accountId", "acc-1")));
        Assert.Equal(new StaticBinding("acc-1", false, BindingModifiers.None), Descriptor(r, "createOrder").Request.PathParams["accountId"]);
        Assert.DoesNotContain("instance:", string.Concat(DrafterTests.Ready(r).Descriptors.Values));
    }

    [Fact]
    public void Review_RequiredQueryParameterMatchingNoColumn_AsksFixedValue()
    {
        string Edit(string t) => t.Replace("        - { name: cursor, in: query, schema: { type: string } }\n",
            "        - { name: cursor, in: query, schema: { type: string } }\n        - { name: region, in: query, required: true, schema: { type: string } }\n");
        Assert.True(Q(DraftServiceTests.Review("orders-openapi.yaml", edit: Edit), "op.listOrders.value.region").Open);
        var r = DraftServiceTests.Review("orders-openapi.yaml", With("orders-openapi.yaml", ("op.listOrders.value.region", "eu")), Edit);
        Assert.Equal(new StaticBinding("eu", false, BindingModifiers.None), Descriptor(r, "listOrders").Request.Query.Single(q => q.Key == "region").Value);
    }

    [Fact]
    public void Review_DocumentedStatus_AskedUnlessFixed()
    {
        string Edit(string t) => t.Replace("        \"422\":\n          description: Invalid order",
            "        \"400\": { description: Bad request }\n        \"422\":\n          description: Invalid order");
        var open = DraftServiceTests.Review("orders-openapi.yaml", edit: Edit);
        Assert.Equal(new[] { "NotFound", "Conflict", "Forbidden", "Validation", "Fail" }, Q(open, "op.createOrder.error.400").Choices.Select(c => c.Value));
        Assert.False(Asked(open, "op.createOrder.error.422"));
        Assert.False(Asked(open, "op.getOrder.error.404"));

        var r = DraftServiceTests.Review("orders-openapi.yaml", With("orders-openapi.yaml", ("op.createOrder.error.400", "Validation")), Edit);
        Assert.Equal(new ErrorMapping("Validation", null), Descriptor(r, "createOrder").Errors!.Statuses[400]);
    }

    [Fact]
    public void Review_ValidationMessage_AskedWithStringPaths()
    {
        var answers = With("orders-openapi.yaml");
        answers.Remove("op.createOrder.error.422.message");
        var q = Q(DraftServiceTests.Review("orders-openapi.yaml", answers), "op.createOrder.error.422.message");
        Assert.Equal(new[] { "$.error.detail", "none" }, q.Choices.Select(c => c.Value));
        Assert.True(q.Open);

        var detail = DraftServiceTests.Review("orders-openapi.yaml", With("orders-openapi.yaml", ("op.createOrder.error.422.message", "$.error.detail")));
        Assert.Equal(new ErrorMapping("Validation", "$.error.detail"), Descriptor(detail, "createOrder").Errors!.Statuses[422]);
        var none = DraftServiceTests.Review("orders-openapi.yaml", With("orders-openapi.yaml", ("op.createOrder.error.422.message", "none")));
        Assert.Equal(new ErrorMapping("Validation", null), Descriptor(none, "createOrder").Errors!.Statuses[422]);
    }

    [Fact]
    public void Review_CustomApiErrors_SameQuestions()
    {
        var r = DraftServiceTests.Review("inventory-openapi.yaml");
        var q = Q(r, "op.adjustStock.error.422.message");
        Assert.Equal(QuestionKind.Text, q.Kind);
        Assert.False(q.Required);

        var answered = DraftServiceTests.Review("inventory-openapi.yaml", With("inventory-openapi.yaml", ("op.adjustStock.error.422.message", "$.error.detail")));
        Assert.Equal(new ErrorMapping("Validation", "$.error.detail"), Descriptor(answered, "adjustStock").Errors!.Statuses[422]);
        Assert.NotNull(Q(DraftServiceTests.Review("inventory-openapi.yaml", With("inventory-openapi.yaml", ("op.adjustStock.error.422.message", "error detail"))),
            "op.adjustStock.error.422.message").Error);
    }

    [Fact]
    public void Review_ReservedParameter_NotAskedSentByAuth()
    {
        string Edit(string t) => t.Replace("        - { name: cursor, in: query, schema: { type: string } }\n",
            "        - { name: cursor, in: query, schema: { type: string } }\n        - { name: apiKey, in: query, required: true, schema: { type: string } }\n");
        var text = Edit(DraftServiceTests.Text("orders-openapi.yaml"));
        var r = DraftService.Review(text, DraftServiceTests.Options with { ReservedNames = new[] { "APIKEY" } }, DrafterTests.AnswersFor("orders-openapi.yaml"));
        Assert.False(Asked(r, "op.listOrders.value.apiKey"));
        Assert.DoesNotContain(Descriptor(r, "listOrders").Request.Query, q => q.Key == "apiKey");
        Assert.Contains(DrafterTests.Ready(r).Notes, n => n.Contains("apiKey") && n.Contains("authentication"));
    }

    [Fact]
    public void Review_ValidatorError_EndpointInvalidNotThrown()
    {
        var r = DrafterTests.Ready(DraftServiceTests.Review("orders-openapi.yaml"));
        var broken = r.Descriptors["getOrder"].Replace("ext_reference:", "ext_nosuchcolumn:");
        var errors = Drafter.Check(r.SchemaYaml, new Dictionary<string, string> { ["getOrder"] = broken, ["listOrders"] = r.Descriptors["listOrders"] }, DraftServiceTests.Options);
        Assert.Contains(errors["getOrder"], e => e.Contains("ext_nosuchcolumn"));
        Assert.Empty(errors["listOrders"]);
        Assert.NotEmpty(Drafter.Check("table: [", new Dictionary<string, string> { ["getOrder"] = broken }, DraftServiceTests.Options)["getOrder"]);
    }
}
