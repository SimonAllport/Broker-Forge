using BrokerForge.Abstractions;
using BrokerForge.Contracts;
using BrokerForge.Host.Config;
using Microsoft.Extensions.Configuration;

namespace BrokerForge.Contract.Tests;

/// <summary>Unbound Custom APIs through the host (HLD D-18, D-19): inputs checked against the schema, outputs mapped.</summary>
[Collection(MockApiCollection.Name)]
public class CustomApiTests(MockApiFixture mock) : HostTestBase(mock)
{
    private static CanonicalRequest Action(string name, params Cell[] inputs)
    {
        var request = HostFactory.Request(null, name, null);
        request.Inputs = inputs.ToList();
        return request;
    }

    private static string? Output(CanonicalResponse response, string name) => response.Outputs.Single(c => c.Col == name).Value;

    [Fact]
    public async Task CustomApi_EchoesInputsToSource_ReturnsOutputs()
    {
        var (status, response, error) = await Host.Execute(Action("ext_EchoAction",
            Cell("Id", "a1"), Cell("Count", "3", CellTypes.Int), Cell("Note", "hello"), Cell("Flag", "true", CellTypes.Bool)));
        Assert.True(status == 200, error?.Message);
        Assert.Equal("/v2/echo/action/a1", Output(response!, "Path"));
        Assert.Equal("3", Output(response!, "Count"));
        Assert.Equal(CellTypes.Int, response!.Outputs.Single(c => c.Col == "Count").Type);
        Assert.Equal("hello", Output(response, "Note"));
        Assert.Equal("""{"flag":true}""", Output(response, "Body"));
        Assert.Equal(new[] { "Path", "Count", "Note", "Body" }, response.Outputs.Select(c => c.Col));
    }

    [Fact]
    public async Task CustomApi_OptionalInputOmitted_OutputNull()
    {
        var (status, response, _) = await Host.Execute(Action("ext_EchoAction", Cell("Id", "a2"), Cell("Count", "1", CellTypes.Int)));
        Assert.Equal(200, status);
        Assert.Null(Output(response!, "Note"));
    }

    [Fact]
    public async Task CustomApi_MissingRequiredInput_400Validation()
    {
        var (status, _, error) = await Host.Execute(Action("ext_EchoAction", Cell("Id", "a1")));
        Assert.Equal(400, status);
        Assert.Equal("Validation", error!.Error);
        Assert.Contains("Count", error.Message);
        Assert.Contains("ext_EchoAction", error.Message);

        var (nullStatus, _, nullError) = await Host.Execute(Action("ext_EchoAction", Cell("Id", "a1"), new Cell { Col = "Count", Type = CellTypes.Int }));
        Assert.Equal(400, nullStatus);
        Assert.Contains("Count", nullError!.Message);
    }

    [Theory]
    [InlineData(CellTypes.String, "3")]
    [InlineData(CellTypes.Int, "three")]
    [InlineData(CellTypes.Int, "3.5")]
    public async Task CustomApi_WrongInputType_400Validation(string type, string value)
    {
        var (status, _, error) = await Host.Execute(Action("ext_EchoAction", Cell("Id", "a1"), Cell("Count", value, type)));
        Assert.Equal(400, status);
        Assert.Equal("Validation", error!.Error);
        Assert.Contains("Count", error.Message);
        Assert.Contains("int", error.Message);
        Assert.DoesNotContain(value, error.Message);
    }

    [Fact]
    public async Task CustomApi_UndeclaredInput_400Validation()
    {
        var (status, _, error) = await Host.Execute(Action("ext_EchoAction", Cell("Id", "a1"), Cell("Count", "1", CellTypes.Int), Cell("Colour", "red")));
        Assert.Equal(400, status);
        Assert.Contains("Colour", error!.Message);
    }

    [Fact]
    public async Task CustomApi_Unknown_400Unsupported()
    {
        var (status, _, error) = await Host.Execute(Action("ext_Nope"));
        Assert.Equal(400, status);
        Assert.Equal("UnsupportedQueryException", error!.Error);
        Assert.Contains("ext_Nope", error.Message);
    }

    [Fact]
    public async Task CustomApi_ServiceInstanceMismatch_400()
    {
        var request = Action("ext_EchoAction", Cell("Id", "a1"), Cell("Count", "1", CellTypes.Int));
        request.ServiceInstance = "OrdersAPI";
        var (status, _, error) = await Host.Execute(request);
        Assert.Equal(400, status);
        Assert.Equal("UnsupportedQueryException", error!.Error);
        Assert.Contains("EchoAPI", error.Message);

        request.ServiceInstance = null;
        request.Table = "ext_order";
        Assert.Equal(400, (await Host.Execute(request)).Status);
    }

    [Fact]
    public async Task CustomApi_SourceError_MappedThroughErrors()
    {
        var (status, _, error) = await Host.Execute(Action("ext_EchoLookup", Cell("OrderId", "ord_9999")));
        Assert.Equal(404, status);
        Assert.Equal("NotFound", error!.Error);

        var (ok, response, _) = await Host.Execute(Action("ext_EchoLookup", Cell("OrderId", "ord_0001")));
        Assert.Equal(200, ok);
        Assert.Equal("REF-0001", Output(response!, "Reference"));
    }

    [Fact]
    public void Config_DuplicateCustomApiName_StartupFails()
    {
        var root = Directory.CreateTempSubdirectory("bf-dup-");
        try
        {
            foreach (var instance in new[] { "AlphaAPI", "BetaAPI" })
            {
                Directory.CreateDirectory(Path.Combine(root.FullName, "descriptors", instance));
                File.Copy(Path.Combine(MockApiFixture.RepoRoot, "tests", "BrokerForge.Contract.Tests", "descriptors", "host", "EchoAPI", "echoLookup.yaml"),
                    Path.Combine(root.FullName, "descriptors", instance, "echoLookup.yaml"));
                Directory.CreateDirectory(Path.Combine(root.FullName, "schemas"));
                File.WriteAllText(Path.Combine(root.FullName, "schemas", instance + ".yaml"),
                    "customApis:\n  ext_EchoLookup:\n    displayName: L\n    inputs: { OrderId: { type: string, required: true } }\n    outputs: { Reference: string }\n");
            }
            var settings = new Dictionary<string, string?>
            {
                ["Config:DescriptorsPath"] = Path.Combine(root.FullName, "descriptors"),
                ["Config:SchemasPath"] = Path.Combine(root.FullName, "schemas"),
            };
            foreach (var instance in new[] { "AlphaAPI", "BetaAPI" })
            {
                settings[$"Instances:{instance}:BrokerType"] = "rest";
                settings[$"Instances:{instance}:BaseUrl"] = "https://example.test";
                settings[$"Instances:{instance}:Auth:Mode"] = "ServiceAccount";
                settings[$"Instances:{instance}:Auth:Scope"] = "api://x/.default";
            }
            var ex = Assert.Throws<ConfigurationException>(() => HostConfigStore.FromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(settings).Build()));
            Assert.Contains("ext_EchoLookup", ex.Message);
            Assert.Contains("AlphaAPI/echoLookup.yaml", ex.Message);
            Assert.Contains("BetaAPI/echoLookup.yaml", ex.Message);
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Fact]
    public void Config_CustomApiWithoutSchema_StartupFails()
    {
        var root = Directory.CreateTempSubdirectory("bf-noschema-");
        try
        {
            Directory.CreateDirectory(Path.Combine(root.FullName, "descriptors", "AlphaAPI"));
            Directory.CreateDirectory(Path.Combine(root.FullName, "schemas"));
            File.Copy(Path.Combine(MockApiFixture.RepoRoot, "tests", "BrokerForge.Contract.Tests", "descriptors", "host", "EchoAPI", "echoLookup.yaml"),
                Path.Combine(root.FullName, "descriptors", "AlphaAPI", "echoLookup.yaml"));
            var settings = new Dictionary<string, string?>
            {
                ["Config:DescriptorsPath"] = Path.Combine(root.FullName, "descriptors"),
                ["Config:SchemasPath"] = Path.Combine(root.FullName, "schemas"),
                ["Instances:AlphaAPI:BrokerType"] = "rest",
                ["Instances:AlphaAPI:BaseUrl"] = "https://example.test",
                ["Instances:AlphaAPI:Auth:Mode"] = "ServiceAccount",
                ["Instances:AlphaAPI:Auth:Scope"] = "api://x/.default",
            };
            var ex = Assert.Throws<ConfigurationException>(() => HostConfigStore.FromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(settings).Build()));
            Assert.Contains("AlphaAPI", ex.Message);
            Assert.Contains("schema", ex.Message);
        }
        finally
        {
            root.Delete(true);
        }
    }
}
