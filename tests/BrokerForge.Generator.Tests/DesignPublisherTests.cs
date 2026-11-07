using System.Text;
using System.Text.Json.Nodes;
using BrokerForge.Connections;

namespace BrokerForge.Generator.Tests;

public class DesignPublisherTests
{
    private const string Sentinel = "s3cr3t-sentinel";
    private const string Name = "ext_/brokerforge/InventoryAPI/design.xml";

    private static ConnectionDesign Design(string openApi = "openapi: 3.0.3") => new(
        1, "InventoryAPI", 0, "rest", openApi, new Dictionary<string, string> { ["op.listItems.paging"] = "offset" },
        new DesignAuth("Static", "ApiKeyQuery", null, "api_key", null, null, null, null, null, ["apiKey"]),
        new DesignObjectMap("ext_item", "reversible", "int", new Dictionary<string, string> { ["RetrieveMultiple"] = "listItems" }),
        "table: ext_item\n", new Dictionary<string, string> { ["listItems"] = "binds: RetrieveMultiple\n" });

    private static DataverseClient Client(FakeDataverse fake) =>
        new(new HttpClient(fake) { BaseAddress = new Uri(FakeDataverse.Org + "/api/data/v9.2/") }, _ => Task.FromResult("token"), false);

    private static Task<int> Publish(FakeDataverse fake, ConnectionDesign design, string? baseUrl = "https://inventory.contoso.example", string? auth = null) =>
        DesignPublisher.PublishAsync(Client(fake), design, "ext", "BrokerForgeInventory", baseUrl, auth, CancellationToken.None);

    private static ConnectionDesign Stored(FakeDataverse fake)
    {
        var content = (string)fake.WebResource(Name)["content"]!;
        return DesignSerializer.Parse(DesignCarrier.Unwrap(Encoding.UTF8.GetString(Convert.FromBase64String(content)), Name), Name);
    }

    [Fact]
    public async Task Publish_New_Version1WebResourceAndBothVariables()
    {
        var fake = new FakeDataverse();
        Assert.Equal(1, await Publish(fake, Design(), auth: "{\"queryParam\":\"key\"}"));
        var post = fake.Writes.Single(w => w.Method == "POST" && w.Path == "webresourceset");
        Assert.Equal("BrokerForgeInventory", post.Solution);
        Assert.Equal(4, (int)JsonNode.Parse(post.Body!)!["webresourcetype"]!);
        Assert.Equal(1, Stored(fake).Version);
        Assert.Contains(fake.Writes, w => w.Path == "PublishXml" && ((string)JsonNode.Parse(w.Body!)!["ParameterXml"]!).Contains("<webresource>"));

        var definitions = fake.Writes.Where(w => w.Method == "POST" && w.Path == "environmentvariabledefinitions").ToList();
        Assert.Equal(new[] { ("bf_InventoryAPI_BaseUrl", 100000000), ("bf_InventoryAPI_Auth", 100000003) },
            definitions.Select(d => JsonNode.Parse(d.Body!)!).Select(d => ((string)d["schemaname"]!, (int)d["type"]!)));
        Assert.All(definitions, d => Assert.Equal("BrokerForgeInventory", d.Solution));
        Assert.All(definitions, d => Assert.Null(JsonNode.Parse(d.Body!)!["defaultvalue"]));
        // This environment's values: not in the solution, so they never travel (spec 4, "never call the dev URL").
        var values = fake.Writes.Where(w => w.Method == "POST" && w.Path == "environmentvariablevalues").ToList();
        Assert.Equal(2, values.Count);
        Assert.All(values, v => Assert.Null(v.Solution));
        Assert.Equal("https://inventory.contoso.example", fake.VariableValue("bf_InventoryAPI_BaseUrl"));
        Assert.Equal("{\"queryParam\":\"key\"}", fake.VariableValue("bf_InventoryAPI_Auth"));
    }

    [Fact]
    public async Task Publish_Changed_VersionIncremented()
    {
        var fake = new FakeDataverse();
        await Publish(fake, Design());
        Assert.Equal(2, await Publish(fake, Design("openapi: 3.1.0")));
        Assert.Equal(2, Stored(fake).Version);
        Assert.Equal("openapi: 3.1.0", Stored(fake).OpenApi);
        Assert.Single(fake.Writes, w => w.Method == "POST" && w.Path == "webresourceset");
        Assert.Single(fake.Writes, w => w.Method == "PATCH" && w.Path.StartsWith("webresourceset("));
    }

    [Fact]
    public async Task Publish_Unchanged_NoWrites()
    {
        var fake = new FakeDataverse();
        await Publish(fake, Design());
        var before = fake.Writes.Count();
        Assert.Equal(1, await Publish(fake, Design()));
        Assert.Equal(before, fake.Writes.Count());
    }

    [Fact]
    public async Task Publish_BaseUrlChangedOnly_ValueUpdatedVersionKept()
    {
        var fake = new FakeDataverse();
        await Publish(fake, Design());
        var before = fake.Writes.Count();
        Assert.Equal(1, await Publish(fake, Design(), baseUrl: "https://inventory2.contoso.example"));
        Assert.Equal(new[] { "PATCH environmentvariablevalues" }, fake.Writes.Skip(before).Select(w => $"{w.Method} {w.Path.Split('(')[0]}"));
    }

    [Theory]
    [InlineData("{\"apiKeyValue\":\"s3cr3t-sentinel\"}")]
    [InlineData("{\"password\":\"s3cr3t-sentinel\"}")]
    [InlineData("{\"mode\":\"s3cr3t-sentinel\"}")]
    [InlineData("[\"s3cr3t-sentinel\"]")]
    [InlineData("{\"queryParam\":7}")]
    public async Task Publish_NoSecretValueInAnyWrite(string auth)
    {
        var fake = new FakeDataverse();
        var ex = await Assert.ThrowsAsync<DesignFormatException>(() => Publish(fake, Design(), auth: auth));
        Assert.DoesNotContain(Sentinel, ex.Message);
        Assert.Empty(fake.Writes);
        await Publish(fake, Design(), auth: "{\"queryParam\":\"key\"}");
        Assert.All(fake.Requests, r => Assert.DoesNotContain(Sentinel, (r.Body ?? "") + r.Path));
    }

    [Fact]
    public async Task Publish_ContentRoundTripsThroughDesignSource()
    {
        var fake = new FakeDataverse();
        await Publish(fake, Design());
        Assert.Equal(DesignSerializer.Serialize(Design() with { Version = 1 }), DesignSerializer.Serialize(Stored(fake)));
    }

    [Theory]
    [InlineData("Inventory API")]
    [InlineData("1Inventory")]
    [InlineData("Inventory/../x")]
    public async Task Publish_BadInstanceName_NoWrites(string instance)
    {
        var fake = new FakeDataverse();
        await Assert.ThrowsAsync<DesignFormatException>(() => Publish(fake, Design() with { Instance = instance }));
        Assert.Empty(fake.Writes);
    }

    // HLD 15 (portal-5 I5): a read of the table's metadata returns its unpublished names, so a rename whose publish was
    // lost looks done. The design is written last, so that run left its new design unwritten: the publish that goes with
    // the design publishes the table too.
    [Fact]
    public async Task Generate_RenamePublishLost_NextRunPublishesTheTableWithTheDesign()
    {
        var fake = new FakeDataverse();
        GenerateOptions With(ConnectionDesign d) => new("InventoryAPI", "BrokerForgeInventory", "brokerforgeext", "https://bf-host.example", "api://host-app/.default", false,
            new HashSet<string> { "RetrieveMultiple" }, Design: new DesignPublish(d, "https://inventory.contoso.example", null));
        var schema = BrokerForge.Schema.SchemaParser.Parse(GeneratorTests.InventorySchema, "InventoryAPI.yaml");
        await Generator.RunAsync(Client(fake), schema, With(Design()), CancellationToken.None);

        var renamed = BrokerForge.Schema.SchemaParser.Parse(GeneratorTests.InventorySchema.Replace("displayCollectionName: Items", "displayCollectionName: Stock items"), "InventoryAPI.yaml");
        var newDesign = With(Design("openapi: 3.0.3\n# Stock items"));
        var plan = await Generator.PreviewAsync(Client(fake), renamed, newDesign, CancellationToken.None);
        fake.FailPublishes = 1;
        await Assert.ThrowsAnyAsync<Exception>(() => Generator.RunAsync(Client(fake), renamed, newDesign with { ConfirmedFingerprint = plan.Changes.Fingerprint }, CancellationToken.None));

        var again = await Generator.PreviewAsync(Client(fake), renamed, newDesign, CancellationToken.None);
        Assert.DoesNotContain(again.Changes.Changes, c => c.Kind == ChangeKind.UpdateTable);
        var before = fake.Requests.Count;
        await Generator.RunAsync(Client(fake), renamed, newDesign with { ConfirmedFingerprint = again.Changes.Fingerprint }, CancellationToken.None);
        var published = fake.Requests.Skip(before).Where(r => r.Path == "PublishXml")
            .Select(r => (string)System.Text.Json.Nodes.JsonNode.Parse(r.Body!)!["ParameterXml"]!).ToList();
        Assert.Contains(published, x => x.Contains("<entity>ext_item</entity>") && x.Contains("<webresource>"));
    }

    [Fact]
    public async Task Generate_WithDesign_PublishedLastAndVersionReturned()
    {
        var fake = new FakeDataverse();
        var options = new GenerateOptions("InventoryAPI", "BrokerForgeInventory", "brokerforgeext", "https://bf-host.example", "api://host-app/.default", false,
            new HashSet<string> { "RetrieveMultiple" }, Design: new DesignPublish(Design(), "https://inventory.contoso.example", null));
        var schema = BrokerForge.Schema.SchemaParser.Parse(GeneratorTests.InventorySchema, "InventoryAPI.yaml");
        var result = await Generator.RunAsync(Client(fake), schema, options, CancellationToken.None);
        Assert.Equal(1, result.DesignVersion);
        Assert.True(fake.Writes.ToList().FindIndex(w => w.Path == "webresourceset") > fake.Writes.ToList().FindIndex(w => w.Path == "roles"));
        Assert.Contains((await Generator.PreviewAsync(Client(new FakeDataverse()), schema, options, CancellationToken.None)).Parts, p => p.Contains("design"));
        var again = await Generator.RunAsync(Client(fake), schema, options, CancellationToken.None);
        Assert.Empty(again.Writes);
        Assert.Equal(1, again.DesignVersion);
    }
}
