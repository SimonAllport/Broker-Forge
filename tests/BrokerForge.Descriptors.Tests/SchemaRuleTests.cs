using BrokerForge.Descriptors.Parsing;
using BrokerForge.Descriptors.Validation;
using BrokerForge.Schema;
using static BrokerForge.Descriptors.Tests.Fixtures;

namespace BrokerForge.Descriptors.Tests;

public class SchemaRuleTests
{
    private static readonly SourceSchema OrdersSchema =
        SchemaParser.Parse(File.ReadAllText(Path.Combine(RepoRoot, "schemas", "OrdersAPI.yaml")), "OrdersAPI.yaml");

    private static IReadOnlyList<DescriptorError> Validate(string yaml) =>
        DescriptorValidator.Validate(DescriptorParser.Parse(yaml, "test.yaml"), NoAuthNames, OrdersSchema);

    public static TheoryData<string> OrdersFiles() => ParserTests.OrdersFiles();

    [Theory]
    [MemberData(nameof(OrdersFiles))]
    public void OrdersDescriptors_PassWithSchema(string operation)
    {
        Assert.Empty(DescriptorValidator.Validate(Orders(operation), NoAuthNames, OrdersSchema));
    }

    [Fact]
    public void Rule7_UnmappedColumn_Rejected()
    {
        var yaml = Mutate(OrdersYaml("listOrders"), "    ext_total:       { path: $.totals.gross, type: decimal }\n", "");
        Assert.Empty(DescriptorValidator.Validate(DescriptorParser.Parse(yaml, "t"), NoAuthNames));
        var error = Assert.Single(Validate(yaml));
        Assert.Equal(7, error.Rule);
        Assert.Equal("response.map", error.YamlPath);
        Assert.Contains("ext_total", error.Message);
    }

    [Fact]
    public void Rule7_CreateColumnNeitherBoundNorUnmapped_Rejected()
    {
        var yaml = Mutate(OrdersYaml("createOrder"), "unmapped: [ext_name, ext_placed]", "unmapped: [ext_name]");
        var error = Assert.Single(Validate(yaml));
        Assert.Equal(7, error.Rule);
        Assert.Contains("ext_placed", error.Message);
    }

    [Fact]
    public void Validate_ColumnNotInSchema_Rejected()
    {
        var yaml = Mutate(OrdersYaml("getOrder"), "    ext_customerref: $.customer.ref\n", "    ext_customerref: $.customer.ref\n    ext_colour: $.colour\n");
        var error = Assert.Single(Validate(yaml));
        Assert.Equal(7, error.Rule);
        Assert.Equal("response.map.ext_colour", error.YamlPath);
    }

    [Fact]
    public void Validate_BoundColumnNotInSchema_Rejected()
    {
        var yaml = Mutate(OrdersYaml("updateOrder"), "      totals:   { gross: { col: ext_total } }", "      totals:   { gross: { col: ext_gross } }");
        Assert.Contains(Validate(yaml), e => e.Rule == 7 && e.YamlPath == "request.body.template.totals.gross" && e.Message.Contains("ext_gross"));
    }

    [Fact]
    public void Validate_OptionSetMissingSchemaOption_Rejected()
    {
        var yaml = Mutate(OrdersYaml("getOrder"), "orderStatus: { open: 100000000, shipped: 100000001, cancelled: 100000002 }", "orderStatus: { open: 100000000, shipped: 100000001 }");
        var error = Assert.Single(Validate(yaml));
        Assert.Equal(7, error.Rule);
        Assert.Equal("optionsets.orderStatus", error.YamlPath);
        Assert.Contains("cancelled", error.Message);
    }

    [Fact]
    public void Validate_TableDiffersFromSchema_Rejected()
    {
        var yaml = Mutate(OrdersYaml("deleteOrder"), "table: ext_order", "table: ext_shipment");
        Assert.Contains(Validate(yaml), e => e.Rule == 7 && e.YamlPath == "table");
    }

    [Fact]
    public void Rule7_CustomApiInputNeverSent_Rejected()
    {
        var schema = SchemaParser.Parse("customApis:\n  ext_SubmitOrder:\n    displayName: S\n    inputs: { OrderId: { type: string, required: true }, Reason: { type: string }, NotifyCustomer: { type: bool }, Extra: { type: string } }\n    outputs: { SubmittedAt: datetime, Status: string }\n", "s.yaml");
        var errors = DescriptorValidator.Validate(DescriptorParser.Parse(CustomApiFeatureTests.SubmitOrder, "t"), NoAuthNames, schema);
        var error = Assert.Single(errors);
        Assert.Equal(7, error.Rule);
        Assert.Contains("Extra", error.Message);
    }
}
