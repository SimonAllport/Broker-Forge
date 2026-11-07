using System.Text.Json.Nodes;
using BrokerForge.Contracts;
using BrokerForge.Host.Generation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BrokerForge.Contract.Tests.Admin;

/// <summary>Admin messages through /v1/execute: answered by the host before any descriptor lookup (plan 4 task 2).</summary>
[Collection(MockApiCollection.Name)]
public sealed class AdminEndpointTests(MockApiFixture mock) : IDisposable
{
    private const string Org = "https://contoso-dev.crm4.dynamics.com";
    private readonly AdminDataverse _dv = new();
    private HostFactory? _host;

    private HostFactory Host => _host ??= new HostFactory(mock.BaseUrl,
        extra: new Dictionary<string, string?> { ["Trust:AllowedCallers:0:OrgUrl"] = Org, ["Connect:PublicBaseUrl"] = "https://bf-host.contoso.example" },
        services: s => s.Replace(ServiceDescriptor.Singleton<IGenerationClientFactory>(new AdminApiTests.Clients(_dv))));

    private static CanonicalRequest Admin(string message, Guid id, string? oid = AdminDataverse.AdminOid)
    {
        var request = HostFactory.Request(null, message, null);
        request.Caller = new CallerContext { UserObjectId = oid };
        request.Inputs = new List<Cell> { new() { Col = "ConnectionId", Type = CellTypes.Guid, Value = id.ToString("D") } };
        return request;
    }

    [Fact]
    public async Task Execute_AdminMessage_ReviewReturned()
    {
        var id = _dv.AddRow(DesignBuilderTests.Inventory());
        var (status, response, error) = await Host.Execute(Admin("bf_ReadDefinition", id));
        Assert.True(status == 200, error?.Message);
        var review = JsonNode.Parse(response!.Outputs.Single(o => o.Col == "Review").Value!)!;
        Assert.Equal("Ready", (string?)review["status"]);
    }

    [Fact]
    public async Task Execute_AdminMessageNotAdmin_403()
    {
        var id = _dv.AddRow(DesignBuilderTests.Inventory());
        var (status, _, error) = await Host.Execute(Admin("bf_ReadDefinition", id, "00000000-0000-0000-0000-00000000a0ff"));
        Assert.Equal(403, status);
        Assert.Equal("Forbidden", error!.Error);
    }

    [Fact]
    public async Task Execute_AdminMessage_NotRoutedToDescriptors()
    {
        // A descriptor-bound Custom API still answers next to the admin messages.
        var request = HostFactory.Request(null, "ext_EchoAction", null);
        request.Inputs = new List<Cell> { new() { Col = "Id", Type = CellTypes.String, Value = "a1" }, new() { Col = "Count", Type = CellTypes.Int, Value = "1" } };
        var (status, _, error) = await Host.Execute(request);
        Assert.True(status == 200, error?.Message);
    }

    public void Dispose() => _host?.Dispose();
}
