using System.Text.Json;
using BrokerForge.Connections;
using BrokerForge.Contracts;
using BrokerForge.Host.Admin;

namespace BrokerForge.Contract.Tests.Admin;

/// <summary>A connection row plus the draft service gives the design Generate publishes (plan 4 task 1).</summary>
public class DesignBuilderTests
{
    private static string Repo(params string[] parts) => Path.Combine(new[] { MockApiFixture.RepoRoot }.Concat(parts).ToArray());

    internal static string InventoryOpenApi => File.ReadAllText(Repo("tools", "mock-api", "inventory-openapi.yaml"));
    internal static string InventoryAnswers => File.ReadAllText(Repo("tests", "SwaggerForge.Tests", "fixtures", "inventory-answers.json"));

    internal static ConnectionRow Inventory(string name = "InventoryAPI", string? answers = null, string mode = "Static") =>
        new(Guid.Parse("00000000-0000-0000-0000-00000000c001"), name, "https://inventory.contoso.example", mode, mode == "Static" ? "apiKeyHeader" : null,
            mode == "Static" ? "X-Api-Key" : null, null, null, null, null, null, null, null, null, false, "ext", null, InventoryOpenApi, answers ?? InventoryAnswers);

    [Fact]
    public void Build_Inventory_MatchesCommittedDescriptors()
    {
        var row = Inventory();
        var design = DesignBuilder.Build(row, DesignBuilder.Review(row));

        Assert.Equal(File.ReadAllText(Repo("schemas", "InventoryAPI.yaml")), design.Schema);
        var committed = Directory.GetFiles(Repo("descriptors", "InventoryAPI"), "*.yaml")
            .ToDictionary(f => Path.GetFileNameWithoutExtension(f), File.ReadAllText);
        Assert.Equal(committed.OrderBy(p => p.Key), design.Descriptors.OrderBy(p => p.Key));
        Assert.Equal("ext_item", design.ObjectMap!.Table);
        Assert.Equal("reversible", design.ObjectMap.KeyStrategy);
        Assert.Equal("Int32", design.ObjectMap.KeyKind);
        Assert.Equal(new Dictionary<string, string>
        {
            ["RetrieveMultiple"] = "listItems", ["Retrieve"] = "getItem", ["Create"] = "createItem", ["Update"] = "updateItem", ["Delete"] = "deleteItem",
        }, design.ObjectMap.Operations.OrderBy(p => p.Key).ToDictionary());
        Assert.Equal(new[] { "key" }, design.Auth.SecretNames);
        Assert.Equal("Static", design.Auth.Mode);
        Assert.Equal("X-Api-Key", design.Auth.HeaderName);
        Assert.Equal("rest", design.BrokerType);
        Assert.Equal(1, design.FormatVersion);
        Assert.Equal("InventoryAPI", design.Instance);
        Assert.Equal(row.OpenApi, design.OpenApi);
        Assert.Equal(JsonSerializer.Deserialize<Dictionary<string, string>>(InventoryAnswers), design.Answers);
    }

    [Fact]
    public void Build_NotReady_RefusedNamingOpenQuestions()
    {
        var row = Inventory(answers: "{}");
        var ex = Assert.Throws<MappedSourceException>(() => DesignBuilder.Build(row, DesignBuilder.Review(row)));
        Assert.Equal(SourceErrorKinds.Validation, ex.Kind);
        Assert.Contains("op.listItems.paging", ex.Message);
    }

    [Theory]
    [InlineData("[1]")]
    [InlineData("{\"a\": 1}")]
    [InlineData("not json")]
    public void Answers_NotAnObjectOfStrings_RefusedNamingColumn(string json)
    {
        var ex = Assert.Throws<MappedSourceException>(() => DesignBuilder.Answers(Inventory(answers: json)));
        Assert.Contains("bf_answers", ex.Message);
    }

    [Fact]
    public void Answers_Empty_NoAnswers() => Assert.Empty(DesignBuilder.Answers(Inventory() with { AnswersJson = null }));

    [Fact]
    public void Build_OAuthOwnClient_SecretNamesClientSecret()
    {
        var row = Inventory(mode: "OAuth") with
        {
            AuthorizeUrl = "https://login.contoso.example/authorize", TokenUrl = "https://login.contoso.example/token", OAuthScopes = "items.read items.write", ClientId = "inventory-client",
        };
        var design = DesignBuilder.Build(row, DesignBuilder.Review(row));
        Assert.Equal(new[] { "clientSecret" }, design.Auth.SecretNames);
        Assert.Equal(new[] { "items.read", "items.write" }, design.Auth.OAuth!.Scopes);
        Assert.False(design.Auth.OAuth.UsesConnectApp);
    }

    [Fact]
    public void Build_ConnectApp_NoSecretNames()
    {
        var row = Inventory(mode: "OAuth") with { UsesConnectApp = true, OAuthScopes = "api://inventory/.default" };
        var design = DesignBuilder.Build(row, DesignBuilder.Review(row));
        Assert.Empty(design.Auth.SecretNames);
        Assert.Null(design.Auth.OAuth!.AuthorizeUrl);
    }

    [Fact]
    public void Build_DesignSerializesAndParses()
    {
        var row = Inventory();
        var design = DesignBuilder.Build(row, DesignBuilder.Review(row));
        var back = DesignSerializer.Parse(DesignSerializer.Serialize(design), "test");
        Assert.Equal(DesignSerializer.Serialize(design), DesignSerializer.Serialize(back));
    }

    private static string Fixture(string name) => File.ReadAllText(Repo("tests", "SwaggerForge.Tests", "fixtures", name));

    /// <summary>A Tickets connection as plan 5 migrates it from the Bicep settings it replaces.</summary>
    internal static ConnectionRow Tickets(string name, string mode, string answers) =>
        new(Guid.Parse("00000000-0000-0000-0000-00000000c002"), name, "https://tickets.contoso.example", mode, null, null, null, null, null, null,
            mode == "OAuth" ? "https://tickets.contoso.example/tk/oauth/authorize" : null, mode == "OAuth" ? "https://tickets.contoso.example/tk/oauth/token" : null,
            mode == "OAuth" ? "tickets.read" : "api://00000000-0000-0000-0000-000000000000/user_impersonation offline_access", mode == "OAuth" ? "bf-tickets" : null,
            mode == "Impersonate", "ext", "BrokerForgeTickets", File.ReadAllText(Repo("tools", "mock-api", "tickets-openapi.yaml")), Fixture(answers));

    [Fact]
    public void Build_TicketsOAuthOwnClient_LikeTheBicepSettings()
    {
        var row = Tickets("TicketsAPI", "OAuth", "tickets-answers.json");
        var design = DesignBuilder.Build(row, DesignBuilder.Review(row));
        Assert.Equal("OAuth", design.Auth.Mode);
        Assert.Equal(new[] { "tickets.read" }, design.Auth.OAuth!.Scopes);
        Assert.Equal("bf-tickets", design.Auth.OAuth.ClientId);
        Assert.False(design.Auth.OAuth.UsesConnectApp);
        Assert.Equal(new[] { "clientSecret" }, design.Auth.SecretNames);
        Assert.Equal("ext_ticket", design.ObjectMap!.Table);
        Assert.Equal("reversible", design.ObjectMap.KeyStrategy);
        Assert.Equal("String", design.ObjectMap.KeyKind);
        Assert.Equal(new[] { "Create", "Retrieve", "RetrieveMultiple" }, design.ObjectMap.Operations.Keys.Order());
    }

    [Fact]
    public void Build_TicketsEntraImpersonate_ConnectApp()
    {
        var row = Tickets("TicketsEntraAPI", "Impersonate", "tickets-entra-answers.json");
        var design = DesignBuilder.Build(row, DesignBuilder.Review(row));
        Assert.Equal("Impersonate", design.Auth.Mode);
        Assert.True(design.Auth.OAuth!.UsesConnectApp);
        Assert.Equal(new[] { "api://00000000-0000-0000-0000-000000000000/user_impersonation", "offline_access" }, design.Auth.OAuth.Scopes);
        Assert.Null(design.Auth.OAuth.AuthorizeUrl);
        Assert.Null(design.Auth.OAuth.ClientId);
        Assert.Empty(design.Auth.SecretNames);
        Assert.Equal("ext_entraticket", design.ObjectMap!.Table);
    }

    [Fact]
    public void SolutionAndPublisher_FollowTheConvention()
    {
        Assert.Equal("BrokerForgeInventoryAPI", DesignBuilder.SolutionOf(Inventory()));
        Assert.Equal("BrokerForgeStock", DesignBuilder.SolutionOf(Inventory() with { Solution = "BrokerForgeStock" }));
        Assert.Equal("brokerforgeext", DesignBuilder.PublisherOf(Inventory()));
    }
}
