using BrokerForge.SwaggerForge;

namespace SwaggerForge.Tests;

/// <summary>Headers, body properties with no column, and retry are questions, never guesses (plan 5 task 1).</summary>
public class HeaderBodyRetryTests
{
    private static readonly DraftOptions Options = new("Widgets", "ext", new[] { "X-Api-Key" });

    /// <summary>A widget source whose create takes <paramref name="headers"/> (parameter YAML) and body <paramref name="extraBody"/>.</summary>
    private static string Definition(string headers = "", string extraBody = "", string requiredBody = "[name]") => $$"""
        openapi: 3.0.3
        info: { title: Widgets, version: "1" }
        paths:
          /widgets:
            post:
              operationId: createWidget
              parameters: [{{headers}}]
              requestBody:
                content:
                  application/json:
                    schema:
                      type: object
                      required: {{requiredBody}}
                      properties:
                        name: { type: string, maxLength: 50 }
        {{extraBody}}
              responses:
                "201":
                  description: created
                  content:
                    application/json:
                      schema: { $ref: "#/components/schemas/Widget" }
          /widgets/{id}:
            get:
              operationId: getWidget
              parameters: [{ name: id, in: path, required: true, schema: { type: string } }]
              responses:
                "200":
                  description: ok
                  content:
                    application/json:
                      schema: { $ref: "#/components/schemas/Widget" }
        components:
          schemas:
            Widget:
              type: object
              required: [id, name]
              properties:
                id: { type: string }
                name: { type: string, maxLength: 50 }
        """;

    private const string PlacedBy = "                placedBy: { type: string }";

    private static DraftReview Review(string text, params (string Key, string Value)[] answers) =>
        DraftService.Review(text, Options, answers.ToDictionary(a => a.Key, a => a.Value));

    private static EndpointReview Create(DraftReview r) => r.Endpoints.Single(e => e.Operation == "createWidget");

    private static string CreateYaml(DraftReview r) => DrafterTests.Ready(r).Descriptors["createWidget"];

    private static Question Q(DraftReview r, string key) => Create(r).Questions.Single(q => q.Key == key);

    [Fact]
    public void Review_RequiredHeader_AskedWithoutNone()
    {
        var q = Q(Review(Definition("{ name: X-Correlation, in: header, required: true, schema: { type: string } }")), "op.createWidget.header.X-Correlation");
        Assert.True(q.Required);
        Assert.Equal(new[] { "correlationId", "uuid", "userObjectId", "value" }, q.Choices.Select(c => c.Value));
        Assert.Equal(EndpointStatus.NeedsAnswers, Create(Review(Definition("{ name: X-Correlation, in: header, required: true, schema: { type: string } }"))).Status);
    }

    [Fact]
    public void Review_OptionalHeaderUnanswered_NotSent()
    {
        var r = Review(Definition("{ name: X-Correlation, in: header, schema: { type: string } }"));
        Assert.Equal(EndpointStatus.Ready, Create(r).Status);
        Assert.Contains("none", Q(r, "op.createWidget.header.X-Correlation").Choices.Select(c => c.Value));
        Assert.DoesNotContain("headers:", CreateYaml(r));
    }

    [Fact]
    public void Review_OptionalHeaderNone_NotSent() =>
        Assert.DoesNotContain("headers:", CreateYaml(Review(Definition("{ name: X-Correlation, in: header, schema: { type: string } }"),
            ("op.createWidget.header.X-Correlation", "none"))));

    [Theory]
    [InlineData("correlationId", """{"context":"correlationId"}""")]
    [InlineData("userObjectId", """{"context":"userObjectId"}""")]
    [InlineData("uuid", """{"computed":"uuid"}""")]
    public void Review_HeaderAnswered_Bound(string answer, string expected) =>
        Assert.Equal(expected, At(CreateYaml(Review(Definition("{ name: X-Correlation, in: header, required: true, schema: { type: string } }"),
            ("op.createWidget.header.X-Correlation", answer))), "request", "headers", "X-Correlation"));

    [Fact]
    public void Review_HeaderValue_AsksForTheValueAndSendsItStatic()
    {
        var text = Definition("{ name: X-Tenant, in: header, required: true, schema: { type: string } }");
        var open = Review(text, ("op.createWidget.header.X-Tenant", "value"));
        Assert.True(Q(open, "op.createWidget.value.X-Tenant").Open);
        var yaml = CreateYaml(Review(text, ("op.createWidget.header.X-Tenant", "value"), ("op.createWidget.value.X-Tenant", "t-1")));
        Assert.Equal("""{"static":"t-1"}""", At(yaml, "request", "headers", "X-Tenant"));
    }

    [Fact]
    public void Review_AuthHeader_NotAsked()
    {
        var r = Review(Definition("{ name: X-Api-Key, in: header, required: true, schema: { type: string } }"));
        Assert.DoesNotContain(Create(r).Questions, q => q.Key.Contains("header", StringComparison.Ordinal));
        Assert.DoesNotContain("headers:", CreateYaml(r));
    }

    [Fact]
    public void Review_AuthorizationHeader_LeftToAuthentication()
    {
        var r = Review(Definition("{ name: Authorization, in: header, required: true, schema: { type: string } }"));
        Assert.DoesNotContain(Create(r).Questions, q => q.Key.Contains("header", StringComparison.Ordinal));
        Assert.DoesNotContain("headers:", CreateYaml(r));
    }

    [Fact]
    public void Review_BodyPropertyWithNoColumn_AskedAndBound()
    {
        var text = Definition(extraBody: PlacedBy);
        var q = Q(Review(text), "op.createWidget.body.placedBy");
        Assert.False(q.Required);
        Assert.Equal(new[] { "userObjectId", "correlationId", "none" }, q.Choices.Select(c => c.Value));
        Assert.Equal("""{"context":"userObjectId"}""", At(CreateYaml(Review(text, ("op.createWidget.body.placedBy", "userObjectId"))), "request", "body", "template", "placedBy"));
        Assert.DoesNotContain("placedBy", CreateYaml(Review(text)));
    }

    [Fact]
    public void Review_RequiredBodyPropertyWithNoColumn_AskedWithoutNone()
    {
        var r = Review(Definition(extraBody: PlacedBy, requiredBody: "[name, placedBy]"));
        var q = Q(r, "op.createWidget.body.placedBy");
        Assert.True(q.Required);
        Assert.DoesNotContain("none", q.Choices.Select(c => c.Value));
        Assert.Equal(EndpointStatus.NeedsAnswers, Create(r).Status);
    }

    [Fact]
    public void Review_RetryYesOnCreateWithoutIdempotencyKey_InvalidAnswerNamingKey()
    {
        var r = Review(Definition("{ name: X-Correlation, in: header, schema: { type: string } }"),
            ("op.createWidget.header.X-Correlation", "uuid"), ("op.createWidget.retry", "yes"));
        var q = Q(r, "op.createWidget.retry");
        Assert.NotNull(q.Error);
        Assert.Contains("Idempotency-Key", q.Error);
        Assert.Equal(EndpointStatus.NeedsAnswers, Create(r).Status);
    }

    [Fact]
    public void Review_RetryYesOnCreateWithIdempotencyKey_EmitsRetry()
    {
        var yaml = CreateYaml(Review(Definition("{ name: Idempotency-Key, in: header, schema: { type: string } }"),
            ("op.createWidget.header.Idempotency-Key", "uuid"), ("op.createWidget.retry", "yes")));
        Assert.Equal("""{"computed":"uuid"}""", At(yaml, "request", "headers", "Idempotency-Key"));
        Assert.Equal("""{"attempts":"3","idempotent":"true","on":["429","503","network"]}""", At(yaml, "retry"));
    }

    // Plan 5 deferred minor: a drafted action is a POST, so retry needs the same key as a create.
    [Fact]
    public void Review_RetryYesOnActionWithoutIdempotencyKey_InvalidAnswerNamingKey()
    {
        var action = Definition().Replace("\ncomponents:", """

              /widgets/{id}/rename:
                post:
                  operationId: renameWidget
                  parameters: [{ name: id, in: path, required: true, schema: { type: string } }]
                  responses:
                    "200":
                      description: ok
                      content:
                        application/json:
                          schema: { $ref: "#/components/schemas/Widget" }
            components:
            """.TrimEnd('\n'));
        var r = Review(action, ("op.renameWidget.retry", "yes"));
        var endpoint = r.Endpoints.Single(e => e.Operation == "renameWidget");
        Assert.StartsWith("Action", endpoint.Becomes);
        var q = endpoint.Questions.Single(q => q.Key == "op.renameWidget.retry");
        Assert.NotNull(q.Error);
        Assert.Contains("Idempotency-Key", q.Error);
    }

    [Fact]
    public void Review_RetryOnRetrieve_NeedsNoHeader() =>
        Assert.Contains("retry:", DrafterTests.Ready(Review(Definition(), ("op.getWidget.retry", "yes"))).Descriptors["getWidget"]);

    [Fact]
    public void Review_RetryAbsent_NoRetry()
    {
        var r = Review(Definition());
        Assert.False(Q(r, "op.createWidget.retry").Required);
        Assert.DoesNotContain("retry", CreateYaml(r));
    }

    /// <summary>The node at <paramref name="steps"/> as compact JSON, with sorted keys and scalars as text (<see cref="ReproduceTests.Canonical"/>).</summary>
    private static string? At(string yaml, params string[] steps)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(ReproduceTests.Canonical(yaml));
        foreach (var step in steps)
            node = node?[step];
        return node?.ToJsonString();
    }
}
