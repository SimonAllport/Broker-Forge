using BrokerForge.Schema;

namespace BrokerForge.Schema.Tests;

public class SchemaParserTests
{
    internal static string RepoRoot { get; } = FindRoot();

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "BrokerForge.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("BrokerForge.slnx not found above the test directory.");
    }

    internal static string OrdersSchemaYaml => File.ReadAllText(Path.Combine(RepoRoot, "schemas", "OrdersAPI.yaml"));

    [Fact]
    public void SchemaParser_ParsesOrdersSchema()
    {
        var s = SchemaParser.Parse(OrdersSchemaYaml, "OrdersAPI.yaml");
        Assert.Equal("ext_order", s.Table);
        Assert.Equal("Order", s.DisplayName);
        Assert.Equal("Orders", s.DisplayCollectionName);
        Assert.Equal("order", s.ExternalName);
        Assert.Equal("ext_name", s.PrimaryName);
        Assert.Equal(new[] { "ext_name", "ext_status", "ext_customerref", "ext_placed", "ext_lines_json", "ext_total" }, s.Columns.Keys);
        Assert.Equal(new ColumnSchema("string", "Reference", 100, null, null), s.Columns["ext_name"]);
        Assert.Equal("memo", s.Columns["ext_lines_json"].Type);
        Assert.Equal(2, s.Columns["ext_total"].Precision);
        Assert.Equal(new Dictionary<string, int> { ["open"] = 100000000, ["shipped"] = 100000001, ["cancelled"] = 100000002 }, s.Columns["ext_status"].Options);
        Assert.Empty(s.CustomApis);
    }

    [Fact]
    public void SchemaParser_ParsesCustomApis()
    {
        var s = SchemaParser.Parse("""
            customApis:
              ext_AdjustStock:
                displayName: Adjust stock
                inputs:  { ItemId: { type: int, required: true }, Reason: { type: string } }
                outputs: { OnHand: int, AdjustedAt: datetime }
            """, "s.yaml");
        Assert.Null(s.Table);
        var api = s.CustomApis["ext_AdjustStock"];
        Assert.Equal(new ParamSchema("int", true), api.Inputs["ItemId"]);
        Assert.Equal(new ParamSchema("string", false), api.Inputs["Reason"]);
        Assert.Equal("datetime", api.Outputs["AdjustedAt"]);
    }

    [Fact]
    public void SchemaParser_UnknownKey_Throws()
    {
        Assert.Contains("displayName: Total }", OrdersSchemaYaml);
        var ex = Assert.Throws<SchemaException>(() => SchemaParser.Parse(OrdersSchemaYaml.Replace("displayName: Total }", "displayName: Total, colour: red }"), "OrdersAPI.yaml"));
        Assert.Equal("columns.ext_total.colour", ex.YamlPath);
        Assert.StartsWith("OrdersAPI.yaml: ", ex.Message);
        var ex2 = Assert.Throws<SchemaException>(() => SchemaParser.Parse(OrdersSchemaYaml + "colour: red\n", "OrdersAPI.yaml"));
        Assert.Equal("colour", ex2.YamlPath);
    }

    [Fact]
    public void SchemaParser_MoneyType_RefusedNamingD15()
    {
        var yaml = OrdersSchemaYaml.Replace("ext_total:       { type: decimal, precision: 2, displayName: Total }", "ext_total:       { type: money, displayName: Total }");
        Assert.NotEqual(OrdersSchemaYaml, yaml);
        var ex = Assert.Throws<SchemaException>(() => SchemaParser.Parse(yaml, "OrdersAPI.yaml"));
        Assert.Equal("columns.ext_total.type", ex.YamlPath);
        Assert.Contains("D-15", ex.Message);
    }

    [Theory]
    [InlineData("ext_name:        { type: string, maxLength: 100, displayName: Reference }", "ext_name:        { type: string, displayName: Reference }", "columns.ext_name.maxLength")]
    [InlineData("primaryName: ext_name", "primaryName: ext_total", "primaryName")]
    [InlineData("primaryName: ext_name", "primaryName: ext_nope", "primaryName")]
    [InlineData("table: ext_order", "table: order", "table")]
    public void SchemaParser_StructuralErrors_NamePath(string from, string to, string path)
    {
        Assert.Contains(from, OrdersSchemaYaml);
        var ex = Assert.Throws<SchemaException>(() => SchemaParser.Parse(OrdersSchemaYaml.Replace(from, to), "OrdersAPI.yaml"));
        Assert.Equal(path, ex.YamlPath);
    }

    [Fact]
    public void SchemaParser_ColumnPrefixDiffersFromTable_Refused()
    {
        var ex = Assert.Throws<SchemaException>(() => SchemaParser.Parse(OrdersSchemaYaml.Replace("ext_customerref:", "abc_customerref:"), "OrdersAPI.yaml"));
        Assert.Equal("columns.abc_customerref", ex.YamlPath);
        Assert.Contains("prefix ext_", ex.Message);
    }

    [Fact]
    public void SchemaParser_CustomApiPrefixDiffersFromTable_Refused()
    {
        var ex = Assert.Throws<SchemaException>(() => SchemaParser.Parse(OrdersSchemaYaml + "customApis:\n  abc_Submit: { displayName: Submit }\n", "OrdersAPI.yaml"));
        Assert.Equal("customApis.abc_Submit", ex.YamlPath);
        Assert.Contains("prefix ext_", ex.Message);
    }

    [Fact]
    public void SchemaParser_CustomApiPrefixesDiffer_Refused()
    {
        var ex = Assert.Throws<SchemaException>(() => SchemaParser.Parse("customApis:\n  ext_A: { displayName: A }\n  abc_B: { displayName: B }\n", "s.yaml"));
        Assert.Equal("customApis.abc_B", ex.YamlPath);
        Assert.Contains("prefix ext_", ex.Message);
    }

    [Fact]
    public void SchemaParser_CustomApiParamType_Checked()
    {
        var ex = Assert.Throws<SchemaException>(() => SchemaParser.Parse("customApis:\n  ext_X:\n    displayName: X\n    inputs: { A: { type: optionset } }\n", "s.yaml"));
        Assert.Equal("customApis.ext_X.inputs.A.type", ex.YamlPath);
    }
}
