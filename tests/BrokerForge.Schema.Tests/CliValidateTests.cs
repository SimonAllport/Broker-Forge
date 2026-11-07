using BrokerForge.Cli;

namespace BrokerForge.Schema.Tests;

public class CliValidateTests
{
    private static (int Code, string Output) Run(params string[] args)
    {
        var output = new StringWriter();
        var code = ValidateCommand.Run(args, output);
        return (code, output.ToString());
    }

    [Fact]
    public void Cli_Validate_RepoDescriptors_ExitsZero()
    {
        var root = SchemaParserTests.RepoRoot;
        var (code, output) = Run(Path.Combine(root, "descriptors"), "--schemas", Path.Combine(root, "schemas"));
        Assert.True(code == 0, output);
    }

    [Fact]
    public void Cli_Validate_BrokenDescriptor_ExitsOneWithFileAndRule()
    {
        var root = SchemaParserTests.RepoRoot;
        var temp = Directory.CreateTempSubdirectory("bf-validate-");
        try
        {
            var descriptors = Directory.CreateDirectory(Path.Combine(temp.FullName, "descriptors", "OrdersAPI"));
            var schemas = Directory.CreateDirectory(Path.Combine(temp.FullName, "schemas"));
            File.Copy(Path.Combine(root, "schemas", "OrdersAPI.yaml"), Path.Combine(schemas.FullName, "OrdersAPI.yaml"));
            var list = File.ReadAllText(Path.Combine(root, "descriptors", "OrdersAPI", "listOrders.yaml"));
            Assert.Contains("    ext_total:       { path: $.totals.gross, type: decimal }\n", list);
            File.WriteAllText(Path.Combine(descriptors.FullName, "listOrders.yaml"), list.Replace("    ext_total:       { path: $.totals.gross, type: decimal }\n", ""));
            File.WriteAllText(Path.Combine(descriptors.FullName, "getOrder.yaml"), "operation: getOrder\ncolour: red\n");

            var (code, output) = Run(Path.Combine(temp.FullName, "descriptors"), "--schemas", schemas.FullName);
            Assert.Equal(1, code);
            Assert.Contains("listOrders.yaml: rule 7 response.map: ", output);
            Assert.Contains("ext_total", output);
            Assert.Contains("getOrder.yaml: rule 1 ", output);
        }
        finally
        {
            temp.Delete(true);
        }
    }

    /// <summary>A copy of the repo's descriptors and schemas to break one way per test.</summary>
    private static (int Code, string Output) RunOnCopy(Action<string, string> change)
    {
        var root = SchemaParserTests.RepoRoot;
        var temp = Directory.CreateTempSubdirectory("bf-validate-");
        try
        {
            var descriptors = Path.Combine(temp.FullName, "descriptors");
            var schemas = Path.Combine(temp.FullName, "schemas");
            foreach (var file in Directory.GetFiles(Path.Combine(root, "descriptors"), "*.yaml", SearchOption.AllDirectories))
            {
                var target = Path.Combine(descriptors, Path.GetRelativePath(Path.Combine(root, "descriptors"), file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
            }
            Directory.CreateDirectory(schemas);
            foreach (var file in Directory.GetFiles(Path.Combine(root, "schemas"), "*.yaml"))
                File.Copy(file, Path.Combine(schemas, Path.GetFileName(file)));
            change(descriptors, schemas);
            return Run(descriptors, "--schemas", schemas);
        }
        finally
        {
            temp.Delete(true);
        }
    }

    [Fact]
    public void Cli_Validate_SameCustomApiInTwoInstances_ExitsOneNamingBoth()
    {
        var (code, output) = RunOnCopy((descriptors, schemas) =>
        {
            Directory.CreateDirectory(Path.Combine(descriptors, "OtherAPI"));
            File.Copy(Path.Combine(descriptors, "InventoryAPI", "adjustStock.yaml"), Path.Combine(descriptors, "OtherAPI", "adjustStock.yaml"));
            File.Copy(Path.Combine(schemas, "InventoryAPI.yaml"), Path.Combine(schemas, "OtherAPI.yaml"));
        });
        Assert.Equal(1, code);
        Assert.Contains("ext_AdjustStock", output);
        Assert.Contains("InventoryAPI", output);
        Assert.Contains("OtherAPI", output);
    }

    [Fact]
    public void Cli_Validate_SchemaApiWithNoDescriptor_ExitsOne()
    {
        var (code, output) = RunOnCopy((descriptors, _) => File.Delete(Path.Combine(descriptors, "InventoryAPI", "adjustStock.yaml")));
        Assert.Equal(1, code);
        Assert.Contains("InventoryAPI.yaml: rule 7 customApis.ext_AdjustStock: ", output);
    }

    [Fact]
    public void Cli_Validate_MissingSchema_ExitsOne()
    {
        var root = SchemaParserTests.RepoRoot;
        var temp = Directory.CreateTempSubdirectory("bf-validate-");
        try
        {
            var (code, output) = Run(Path.Combine(root, "descriptors"), "--schemas", temp.FullName);
            Assert.Equal(1, code);
            Assert.Contains("OrdersAPI.yaml", output);
        }
        finally
        {
            temp.Delete(true);
        }
    }
}
