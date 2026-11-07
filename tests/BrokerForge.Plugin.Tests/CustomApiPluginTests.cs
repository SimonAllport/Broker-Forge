using System.Text.Json.Nodes;
using BrokerForge.Contracts;
using BrokerForge.Plugin.Translation;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using static BrokerForge.Plugin.Tests.ProviderPluginTests;

namespace BrokerForge.Plugin.Tests;

/// <summary>The unbound Custom API handler (HLD D-18, D-20) against SDK fakes and a stub host.</summary>
public class CustomApiPluginTests
{
    private static readonly Guid Correlation = Guid.Parse("c0ffee00-0000-0000-0000-000000000002");

    /// <summary>Environment variable definitions as RetrieveMultiple returns them: default value, and the current value through the link alias.</summary>
    private static EntityCollection Variables(IDictionary<string, (string? Default, string? Current)> values)
    {
        var rows = new EntityCollection();
        foreach (var (schema, (def, current)) in values)
        {
            var row = new Entity("environmentvariabledefinition", Guid.NewGuid()) { ["schemaname"] = schema };
            if (def != null)
                row["defaultvalue"] = def;
            if (current != null)
                row["v.value"] = new AliasedValue("environmentvariablevalue", "value", current);
            rows.Entities.Add(row);
        }
        return rows;
    }

    private static (IServiceProvider Services, ParameterCollection Outputs, List<QueryExpression> Queries) Services(
        string message, ParameterCollection inputs, EntityCollection variables)
    {
        var outputs = new ParameterCollection();
        var context = Fake<IPluginExecutionContext6>.WithProperties(new Dictionary<string, object>
        {
            ["CorrelationId"] = Correlation,
            ["MessageName"] = message,
            ["PrimaryEntityName"] = "none",
            ["InputParameters"] = inputs,
            ["OutputParameters"] = outputs,
            ["EnvironmentId"] = "a4c0d8aa-0000-0000-0000-000000000001",
        });
        var queries = new List<QueryExpression>();
        var organization = Fake<IOrganizationService>.With((m, args) =>
        {
            if (m.Name != nameof(IOrganizationService.RetrieveMultiple))
                throw new InvalidOperationException("unexpected organization call " + m.Name);
            queries.Add((QueryExpression)args[0]);
            return variables;
        });
        var factory = Fake<IOrganizationServiceFactory>.With((_, _) => organization);
        var services = new Dictionary<Type, object>
        {
            [typeof(IPluginExecutionContext)] = context,
            [typeof(ITracingService)] = Fake<ITracingService>.With((_, _) => null!),
            [typeof(IManagedIdentityService)] = Identity(),
            [typeof(IOrganizationServiceFactory)] = factory,
        };
        return (Fake<IServiceProvider>.With((_, args) => services.GetValueOrDefault((Type)args[0])!), outputs, queries);
    }

    private static EntityCollection Both(string hostUrl) => Variables(new Dictionary<string, (string?, string?)>
    {
        ["bf_HostUrl"] = ("https://default.example", hostUrl),
        ["bf_HostScope"] = ("api://brokerforge-test/.default", null),
    });

    [Fact]
    public void Execute_CustomApi_SendsInputsAndSetsTypedOutputs()
    {
        using var host = new StubHost(200, $$"""
            {"correlationId":"{{Correlation}}","outputs":[{"col":"OnHand","type":"int","value":"12"},{"col":"AdjustedAt","type":"datetime","value":"2026-11-01T09:30:00.0000000Z"},{"col":"Note","type":"string"}]}
            """);
        var inputs = new ParameterCollection { ["ItemId"] = 7, ["Delta"] = -2, ["Reason"] = "count" };
        var (services, outputs, queries) = Services("ext_AdjustStock", inputs, Both(host.Url));

        new CustomApiPlugin().Execute(services);

        var sent = JsonNode.Parse(host.LastBody!)!;
        Assert.Equal("ext_AdjustStock", (string)sent["message"]!);
        Assert.Null(sent["serviceInstance"]);
        Assert.Null(sent["table"]);
        var cells = sent["inputs"]!.AsArray().ToDictionary(c => (string)c!["col"]!, c => ((string)c!["type"]!, (string)c["value"]!));
        Assert.Equal(("int", "7"), cells["ItemId"]);
        Assert.Equal(("int", "-2"), cells["Delta"]);
        Assert.Equal(("string", "count"), cells["Reason"]);

        Assert.Equal(12, outputs["OnHand"]);
        var at = Assert.IsType<DateTime>(outputs["AdjustedAt"]);
        Assert.Equal((DateTimeKind.Utc, new DateTime(2026, 11, 1, 9, 30, 0)), (at.Kind, at));
        Assert.Null(outputs["Note"]);
        Assert.Single(queries);
    }

    [Fact]
    public void Execute_CustomApi_DefaultValueUsedWithoutCurrentValue()
    {
        using var host = new StubHost(200, $$"""{"correlationId":"{{Correlation}}","outputs":[]}""");
        var variables = Variables(new Dictionary<string, (string?, string?)> { ["bf_HostUrl"] = (host.Url, null), ["bf_HostScope"] = ("api://x/.default", null) });
        var (services, _, _) = Services("ext_Ping", new ParameterCollection(), variables);
        new CustomApiPlugin().Execute(services);
        Assert.Equal(1, host.Calls);
    }

    [Fact]
    public void Execute_CustomApi_NoHostUrlVariable_BrokerUnavailableNamingIt()
    {
        using var host = new StubHost(200, "{}");
        var variables = Variables(new Dictionary<string, (string?, string?)> { ["bf_HostScope"] = ("api://x/.default", null) });
        var (services, _, _) = Services("ext_AdjustStock", new ParameterCollection { ["ItemId"] = 1 }, variables);
        var ex = Assert.Throws<InvalidPluginExecutionException>(() => new CustomApiPlugin().Execute(services));
        Assert.StartsWith(nameof(BrokerUnavailableException), ex.Message);
        Assert.Contains("bf_HostUrl", ex.Message);
        Assert.EndsWith($"(correlationId {Correlation})", ex.Message);
        Assert.Equal(0, host.Calls);
    }

    [Fact]
    public void Execute_CustomApi_HostError_PassesThrough()
    {
        using var host = new StubHost(400, $$"""{"error":"Validation","message":"Custom API ext_AdjustStock needs input Delta.","correlationId":"{{Correlation}}"}""");
        var (services, _, _) = Services("ext_AdjustStock", new ParameterCollection { ["ItemId"] = 1 }, Both(host.Url));
        var ex = Assert.Throws<InvalidPluginExecutionException>(() => new CustomApiPlugin().Execute(services));
        Assert.Equal($"Validation: Custom API ext_AdjustStock needs input Delta. (correlationId {Correlation})", ex.Message);
    }

    [Fact]
    public void ToInputs_UnsupportedType_UnsupportedQuery()
    {
        var inputs = new ParameterCollection { ["Item"] = new EntityReference("ext_item", Guid.NewGuid()) };
        var ex = Assert.Throws<UnsupportedQueryException>(() => ParameterTranslator.ToInputs(inputs, "corr-1"));
        Assert.Contains("Item", ex.Message);
        Assert.Equal("corr-1", ex.CorrelationId);
    }

    [Fact]
    public void ToInputs_NullValue_Omitted()
    {
        var cells = ParameterTranslator.ToInputs(new ParameterCollection { ["Reason"] = null, ["ItemId"] = 3 }, "corr-1");
        Assert.Equal("ItemId", Assert.Single(cells).Col);
    }

    [Theory]
    [InlineData("optionset", "100000000")]
    [InlineData("int", "twelve")]
    [InlineData(null, "x")]
    public void SetOutputs_UnknownOutput_Mapping(string? type, string value)
    {
        var ex = Assert.Throws<MappingException>(() =>
            ParameterTranslator.SetOutputs(new ParameterCollection(), new List<Cell> { new() { Col = "OnHand", Type = type, Value = value } }, "corr-1"));
        Assert.Contains("OnHand", ex.Message);
        Assert.DoesNotContain(value, ex.Message);
    }

    [Fact]
    public void SetOutputs_UndeclaredOutput_MappingWhenThePlatformListsOutputs()
    {
        var outputs = new ParameterCollection { ["OnHand"] = null };
        var ex = Assert.Throws<MappingException>(() =>
            ParameterTranslator.SetOutputs(outputs, new List<Cell> { new() { Col = "Colour", Type = "string", Value = "red" } }, "corr-1"));
        Assert.Contains("Colour", ex.Message);
    }
}
