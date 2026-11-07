using System.Net.Http.Json;
using System.Text.Json.Nodes;
using BrokerForge.Contracts;
using BrokerForge.Keys;

namespace BrokerForge.Contract.Tests;

public abstract class HostTestBase(MockApiFixture mock) : IAsyncLifetime
{
    protected MockApiFixture Mock { get; } = mock;
    protected HostFactory Host { get; private set; } = null!;
    protected static readonly ReversibleKeyStrategy Keys = new(KeyKind.String);

    public async Task InitializeAsync()
    {
        await Mock.ResetAsync();
        Host = new HostFactory(Mock.BaseUrl);
    }

    public Task DisposeAsync()
    {
        Host.Dispose();
        return Task.CompletedTask;
    }

    protected static string Id(string sourceKey) => Keys.Encode(ExternalKey.FromString(sourceKey)).ToString();

    protected static Cell Cell(string col, string value, string type = CellTypes.String) => new() { Col = col, Type = type, Value = value };

    protected static string? Value(Row row, string col) => row.Cells.Single(c => c.Col == col).Value;

    protected static CanonicalRequest List(string table, int size, FilterGroup? filter = null, string? token = null, string instance = "OrdersAPI")
    {
        var request = HostFactory.Request(table, Messages.RetrieveMultiple, instance);
        request.Query = new CanonicalQuery { Filter = filter, Page = new PageRequest { Size = size, Token = token } };
        return request;
    }

    protected static FilterGroup Where(string col, string op, params string[] values) =>
        new() { Type = "and", Conditions = new() { new Condition { Col = col, Op = op, Values = values.ToList() } }, Groups = new() };

    /// <summary>What the mock's echo route last received.</summary>
    protected async Task<JsonNode> LastEcho()
    {
        using var http = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, Mock.BaseUrl + "/__echo/last");
        request.Headers.Add("X-Api-Key", MockApiFixture.ApiKey);
        using var response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonNode>())!;
    }
}
