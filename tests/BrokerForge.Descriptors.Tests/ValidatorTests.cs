using BrokerForge.Descriptors.Model;
using BrokerForge.Descriptors.Parsing;
using BrokerForge.Descriptors.Validation;
using static BrokerForge.Descriptors.Tests.Fixtures;

namespace BrokerForge.Descriptors.Tests;

public class ValidatorTests
{
    private static IReadOnlyList<DescriptorError> Validate(string yaml, ReservedNames? reserved = null) =>
        DescriptorValidator.Validate(DescriptorParser.Parse(yaml, "test.yaml"), reserved ?? NoAuthNames);

    private static void AssertRule(int rule, IReadOnlyList<DescriptorError> errors, string? pathContains = null)
    {
        Assert.Contains(errors, e => e.Rule == rule && (pathContains == null || e.YamlPath.Contains(pathContains)));
    }

    public static TheoryData<string> OrdersFiles() => ParserTests.OrdersFiles();

    [Theory]
    [MemberData(nameof(OrdersFiles))]
    public void OrdersDescriptors_PassValidation(string operation)
    {
        Assert.Empty(DescriptorValidator.Validate(Orders(operation), ReservedNames.For(new[] { "X-Api-Key" })));
    }

    [Fact]
    public void Rule1_UnknownKey_Rejected()
    {
        Assert.Throws<DescriptorException>(() => DescriptorParser.Parse(OrdersYaml("getOrder") + "colour: red\n", "d"));
    }

    [Fact]
    public void Rule2_ColBindingOnRetrieveMultiple_Rejected()
    {
        var yaml = Mutate(OrdersYaml("listOrders"), "    customer: { filter: ext_customerref, ops: [eq] }", "    customer: { col: ext_customerref }");
        AssertRule(2, Validate(yaml), "request.query.customer");
    }

    [Fact]
    public void Rule2_FilterInHeader_Rejected()
    {
        var yaml = Mutate(OrdersYaml("listOrders"), "  query:\n", "  headers:\n    X-Status: { filter: ext_status, ops: [eq] }\n  query:\n");
        AssertRule(2, Validate(yaml), "request.headers.X-Status");
    }

    [Fact]
    public void Rule2_IdOnCreate_Rejected()
    {
        var yaml = Mutate(OrdersYaml("createOrder"), "{ context: userObjectId }", "{ id: true }");
        AssertRule(2, Validate(yaml), "placedBy");
    }

    [Fact]
    public void Rule2_FormatJsonOutsideBody_Rejected()
    {
        var yaml = Mutate(OrdersYaml("createOrder"), "X-Correlation:   { context: correlationId }", "X-Correlation:   { context: correlationId, format: json }");
        AssertRule(2, Validate(yaml), "X-Correlation");
    }

    [Fact]
    public void Rule2_UnknownContextName_Rejected()
    {
        var yaml = Mutate(OrdersYaml("deleteOrder"), "{ context: correlationId }", "{ context: tenant }");
        AssertRule(2, Validate(yaml), "X-Correlation");
    }

    [Fact]
    public void Rule3_PlaceholderWithoutPathParam_Rejected()
    {
        var yaml = Mutate(OrdersYaml("getOrder"), "path: /v2/orders/{orderId}", "path: /v2/{tenant}/orders/{orderId}");
        AssertRule(3, Validate(yaml), "request.path");
    }

    [Fact]
    public void Rule3_UnusedPathParam_Rejected()
    {
        var yaml = Mutate(OrdersYaml("getOrder"), "pathParams: { orderId: { id: true } }", "pathParams: { orderId: { id: true }, extra: { static: x } }");
        AssertRule(3, Validate(yaml), "request.pathParams.extra");
    }

    [Fact]
    public void Rule4_AuthorizationHeader_Rejected()
    {
        var yaml = Mutate(OrdersYaml("deleteOrder"), "X-Correlation:", "authorization:");
        AssertRule(4, Validate(yaml), "request.headers.authorization");
    }

    [Fact]
    public void Rule4_ReservedQueryParamFromAuthMode_Rejected()
    {
        var reserved = ReservedNames.For(Array.Empty<string>(), new[] { "Customer" });
        AssertRule(4, Validate(OrdersYaml("listOrders"), reserved), "request.query.customer");
        Assert.DoesNotContain(Validate(OrdersYaml("listOrders")), e => e.Rule == 4);
    }

    [Fact]
    public void Rule4_ReservedQueryParamUsedByPaging_Rejected()
    {
        AssertRule(4, Validate(OrdersYaml("listOrders"), ReservedNames.For(Array.Empty<string>(), new[] { "cursor" })), "paging.token.param");
        AssertRule(4, Validate(OrdersYaml("listOrders"), ReservedNames.For(Array.Empty<string>(), new[] { "limit" })), "paging.pageSize.param");
    }

    [Fact]
    public void Rule4_ReservedHeaderFromAuthMode_Rejected()
    {
        var yaml = Mutate(OrdersYaml("deleteOrder"), "X-Correlation:", "X-API-KEY:");
        AssertRule(4, Validate(yaml, ReservedNames.For(new[] { "X-Api-Key" })), "X-API-KEY");
        Assert.DoesNotContain(Validate(yaml), e => e.Rule == 4);
    }

    [Fact]
    public void Rule5_GetWithBody_Rejected()
    {
        var yaml = Mutate(OrdersYaml("getOrder"), "  pathParams: { orderId: { id: true } }\n", "  pathParams: { orderId: { id: true } }\n  body:\n    template: { a: 1 }\n");
        AssertRule(5, Validate(yaml), "request.body");
    }

    [Fact]
    public void Rule6_RetrieveMultipleWithoutPaging_Rejected()
    {
        var yaml = Mutate(OrdersYaml("listOrders"), "paging:\n  strategy: cursor\n  pageSize: { param: limit, max: 100 }\n  token:    { param: cursor, next: $.meta.nextCursor }\n", "");
        AssertRule(6, Validate(yaml), "paging");
    }

    [Fact]
    public void Rule6_RetrieveWithoutRows_Rejected()
    {
        var yaml = Mutate(OrdersYaml("getOrder"), "  rows: $\n", "");
        AssertRule(6, Validate(yaml), "response.rows");
    }

    [Fact]
    public void Rule6_CreateWithoutKey_Rejected()
    {
        var yaml = Mutate(OrdersYaml("createOrder"), "  key: $.id\n", "");
        AssertRule(6, Validate(yaml), "response.key");
    }

    [Fact]
    public void Rule6_RetrieveMultipleWithoutSortStable_Rejected()
    {
        var yaml = Mutate(OrdersYaml("listOrders"), "sortStable: true\n", "");
        AssertRule(6, Validate(yaml), "sortStable");
    }

    [Fact]
    public void Rule6_OptionSetTypeWithUnknownMap_Rejected()
    {
        var yaml = Mutate(OrdersYaml("getOrder"), "type: optionset:orderStatus", "type: optionset:nope");
        AssertRule(6, Validate(yaml), "response.map.ext_status");
    }

    [Fact]
    public void Rule6_PagingParamsMissingForStrategy_Rejected()
    {
        var yaml = Mutate(OrdersYaml("listOrders"), "  token:    { param: cursor, next: $.meta.nextCursor }\n", "");
        AssertRule(6, Validate(yaml), "paging.token");
    }

    [Fact]
    public void Rule8_DefaultNotFail_Rejected()
    {
        var yaml = Mutate(OrdersYaml("getOrder"), "  default: Fail", "  default: NotFound");
        AssertRule(8, Validate(yaml), "errors.default");
    }

    [Fact]
    public void Rule9_RetryNotIdempotent_Rejected()
    {
        var yaml = Mutate(OrdersYaml("createOrder"), "idempotent: true", "idempotent: false");
        AssertRule(9, Validate(yaml), "retry");
    }

    [Fact]
    public void Rule9_CreateRetryWithoutIdempotencyKey_Rejected()
    {
        var yaml = Mutate(OrdersYaml("createOrder"), "    Idempotency-Key: { computed: uuid }\n", "");
        AssertRule(9, Validate(yaml), "retry");
    }

    private static string AdjustStockYaml() => File.ReadAllText(Path.Combine(RepoRoot, "descriptors", "InventoryAPI", "adjustStock.yaml"));

    private const string Retry = "retry:\n  idempotent: true\n  attempts: 3\n  on: [503]\n";

    // Plan 5 deferred minor: a POST action is no safer to repeat than a create.
    [Fact]
    public void Rule9_PostActionRetryWithoutIdempotencyKey_Rejected()
    {
        AssertRule(9, Validate(AdjustStockYaml() + Retry), "retry");
    }

    [Fact]
    public void Rule9_PostActionRetryWithIdempotencyKey_Accepted()
    {
        var yaml = Mutate(AdjustStockYaml(), "  pathParams:\n", "  headers:\n    Idempotency-Key: { computed: uuid }\n  pathParams:\n") + Retry;
        Assert.DoesNotContain(Validate(yaml), e => e.Rule == 9);
    }

    [Fact]
    public void Rule12_SecretInPath_Rejected()
    {
        var yaml = Mutate(OrdersYaml("createOrder"), "{ accountId: { instance: accountId } }", "{ accountId: { secret: accountKey } }");
        AssertRule(12, Validate(yaml), "request.pathParams.accountId");
    }

    [Fact]
    public void Rule12_SecretInBody_Rejected()
    {
        var yaml = Mutate(OrdersYaml("createOrder"), "{ context: userObjectId }", "{ secret: partnerKey }");
        AssertRule(12, Validate(yaml), "request.body.template.placedBy");
    }

    [Fact]
    public void Rule12_SecretInRawBody_Rejected()
    {
        var yaml = Mutate(OrdersYaml("updateOrder"), "    template:\n      customer: { ref: { col: ext_customerref } }\n      state:    { code: { col: ext_status, optionset: orderStatus } }\n      lines:    { col: ext_lines_json, format: json }\n      totals:   { gross: { col: ext_total } }\n",
            "    contentType: text/xml\n    raw: \"<k>{{ secret.partnerKey }}</k>\"\n");
        AssertRule(12, Validate(yaml), "request.body.raw");
    }
}
