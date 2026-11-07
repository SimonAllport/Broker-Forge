using System.Text.Json.Nodes;
using BrokerForge.Schema;

namespace BrokerForge.Generator.Tests;

public class GeneratorTests
{
    private const string Token = "eyJ0eXAiOiJKV1QifQ.fake-dataverse-token";

    internal const string InventorySchema = """
        table: ext_item
        displayName: Item
        displayCollectionName: Items
        externalName: item
        primaryName: ext_name
        columns:
          ext_name:   { type: string, maxLength: 100, displayName: Name }
          ext_onhand: { type: int, displayName: On hand }
          ext_price:  { type: decimal, precision: 2, displayName: Price }
          ext_state:  { type: optionset, options: { active: 100000000, discontinued: 100000001 }, displayName: State }
          ext_active: { type: bool, displayName: Active }
          ext_added:  { type: date, displayName: Added }
          ext_notes:  { type: memo, displayName: Notes }
        customApis:
          ext_AdjustStock:
            displayName: Adjust stock
            inputs:  { ItemId: { type: int, required: true }, Delta: { type: int, required: true }, Reason: { type: string } }
            outputs: { OnHand: int, AdjustedAt: datetime }
        """;

    private static readonly GenerateOptions Options = new("InventoryAPI", "BrokerForgeInventory", "brokerforgeext", "https://bf-host.example", "api://host-app/.default", DryRun: false);

    private static SourceSchema Schema(string yaml = InventorySchema) => SchemaParser.Parse(yaml, "InventoryAPI.yaml");

    private static Task<GenerationResult> Run(FakeDataverse fake, SourceSchema schema, GenerateOptions? options = null) =>
        Generator.RunAsync(new DataverseClient(new HttpClient(fake) { BaseAddress = new Uri(FakeDataverse.Org + "/api/data/v9.2/") }, _ => Task.FromResult(Token), options?.DryRun ?? false),
            schema, options ?? Options, CancellationToken.None);

    [Fact]
    public async Task Generate_NewTable_PostsEntityThenColumns()
    {
        var fake = new FakeDataverse();
        await Run(fake, Schema());
        var writes = fake.Writes.ToList();

        var source = writes.Single(w => w.Path == "bf_brokerdatasources");
        var record = JsonNode.Parse(source.Body!)!;
        Assert.Equal("InventoryAPI", (string)record["bf_serviceinstance"]!);
        Assert.Equal("https://bf-host.example", (string)record["bf_hosturl"]!);
        Assert.Equal("api://host-app/.default", (string)record["bf_scope"]!);

        var entityIndex = writes.FindIndex(w => w.Method == "POST" && w.Path == "EntityDefinitions");
        var entity = JsonNode.Parse(writes[entityIndex].Body!)!;
        Assert.Equal("ext_item", (string)entity["SchemaName"]!);
        Assert.Equal(FakeDataverse.ProviderId, (string)entity["DataProviderId"]!);
        Assert.Equal("item", (string)entity["ExternalName"]!);
        var createAt = fake.Requests.FindIndex(r => r.Method == "POST" && r.Path == "EntityDefinitions");
        Assert.Contains(fake.Requests.Skip(createAt + 1), r => r.Method == "GET" && r.Path.StartsWith("EntityDefinitions(LogicalName='ext_item')?"));
        var primary = entity["Attributes"]!.AsArray().Single()!;
        Assert.Equal(("ext_name", true, "ext_name"), ((string)primary["SchemaName"]!, (bool)primary["IsPrimaryName"]!, (string)primary["ExternalName"]!));
        Assert.Equal("BrokerForgeInventory", writes[entityIndex].Solution);

        var columns = writes.Where(w => w.Path == "EntityDefinitions(LogicalName='ext_item')/Attributes").Select(w => JsonNode.Parse(w.Body!)!).ToList();
        Assert.True(writes.FindIndex(w => w.Path.EndsWith("/Attributes")) > entityIndex);
        Assert.Equal(new[] { "ext_onhand", "ext_price", "ext_state", "ext_active", "ext_added", "ext_notes" }, columns.Select(c => (string)c["SchemaName"]!));
        Assert.All(columns, c => Assert.Equal((string)c["SchemaName"]!, (string)c["ExternalName"]!));
        Assert.Equal("Microsoft.Dynamics.CRM.DecimalAttributeMetadata", (string)columns[1]["@odata.type"]!);
        Assert.Equal(2, (int)columns[1]["Precision"]!);
        Assert.Equal(new[] { 100000000, 100000001 }, columns[2]["OptionSet"]!["Options"]!.AsArray().Select(o => (int)o!["Value"]!));
        Assert.Equal("DateOnly", (string)columns[4]["Format"]!);
        Assert.Contains(writes, w => w.Path == "PublishXml");
    }

    [Fact]
    public async Task Generate_Rerun_NoWrites()
    {
        var fake = new FakeDataverse();
        await Run(fake, Schema());
        var before = fake.Writes.Count();
        var second = await Run(fake, Schema());
        Assert.Equal(before, fake.Writes.Count());
        Assert.Empty(second.Writes);
    }

    [Fact]
    public async Task Generate_ColumnTypeChanged_NeedsConfirmationWithoutWriting()
    {
        var fake = new FakeDataverse();
        await Run(fake, Schema());
        var before = fake.Writes.Count();
        var changed = InventorySchema.Replace("ext_onhand: { type: int, displayName: On hand }", "ext_onhand: { type: decimal, precision: 0, displayName: On hand }");
        Assert.NotEqual(InventorySchema, changed);

        var ex = await Assert.ThrowsAsync<GenerationConfirmationException>(() => Run(fake, Schema(changed)));
        Assert.Contains("ext_onhand", ex.Message);
        Assert.Contains("int", ex.Message);
        Assert.Contains("decimal", ex.Message);
        Assert.Equal(before, fake.Writes.Count());
    }

    [Fact]
    public async Task Generate_ColumnRemoved_NeedsConfirmation()
    {
        var fake = new FakeDataverse();
        await Run(fake, Schema());
        var before = fake.Writes.Count();
        var removed = InventorySchema.Replace("  ext_notes:  { type: memo, displayName: Notes }\n", "");
        Assert.NotEqual(InventorySchema, removed);

        var ex = await Assert.ThrowsAsync<GenerationConfirmationException>(() => Run(fake, Schema(removed)));
        Assert.Contains("remove column Notes (ext_notes)", ex.Message);
        Assert.Equal(before, fake.Writes.Count());
    }

    [Fact]
    public async Task Generate_ColumnAdded_CreatesOnlyThatColumn()
    {
        var fake = new FakeDataverse();
        await Run(fake, Schema());
        var before = fake.Writes.Count();
        var added = InventorySchema.Replace("  ext_notes:  { type: memo, displayName: Notes }\n", "  ext_notes:  { type: memo, displayName: Notes }\n  ext_sku:    { type: string, maxLength: 40, displayName: SKU }\n");
        await Run(fake, Schema(added));
        var newWrites = fake.Writes.Skip(before).ToList();
        Assert.Equal(new[] { "EntityDefinitions", "PublishXml", "systemforms", "savedqueries", "PublishXml" },
            newWrites.Select(w => w.Path.Split('(')[0]));
        Assert.Contains("ext_sku", newWrites[0].Body);
    }

    [Fact]
    public async Task Generate_CustomApi_CreatesApiParamsAndProperties()
    {
        var fake = new FakeDataverse();
        await Run(fake, Schema());
        var post = fake.Writes.Single(w => w.Path == "customapis");
        var api = JsonNode.Parse(post.Body!)!;
        Assert.Equal("ext_AdjustStock", (string)api["uniquename"]!);
        Assert.Equal(0, (int)api["bindingtype"]!);
        Assert.False((bool)api["isfunction"]!);
        Assert.Equal($"/plugintypes({FakeDataverse.CustomApiPluginTypeId})", (string)api["PluginTypeId@odata.bind"]!);
        Assert.Equal("BrokerForgeInventory", post.Solution);
        var inputs = api["CustomAPIRequestParameters"]!.AsArray().ToDictionary(p => (string)p!["uniquename"]!, p => ((int)p!["type"]!, (bool)p["isoptional"]!));
        Assert.Equal((7, false), inputs["ItemId"]);
        Assert.Equal((7, false), inputs["Delta"]);
        Assert.Equal((10, true), inputs["Reason"]);
        var outputs = api["CustomAPIResponseProperties"]!.AsArray().ToDictionary(p => (string)p!["uniquename"]!, p => (int)p!["type"]!);
        Assert.Equal(new Dictionary<string, int> { ["OnHand"] = 7, ["AdjustedAt"] = 1 }, outputs);
    }

    [Fact]
    public async Task Generate_CustomApiParameterChanged_NeedsConfirmation()
    {
        var fake = new FakeDataverse();
        await Run(fake, Schema());
        var changed = InventorySchema.Replace("Reason: { type: string }", "Reason: { type: string, required: true }");
        var ex = await Assert.ThrowsAsync<GenerationConfirmationException>(() => Run(fake, Schema(changed)));
        Assert.Contains("ext_AdjustStock", ex.Message);
        Assert.Contains("Reason", ex.Message);
    }

    [Theory]
    [InlineData("bindingtype", 1, "bindingtype")]
    [InlineData("isfunction", true, "isfunction")]
    public async Task Generate_CustomApiShapeChanged_NeedsConfirmation(string key, object value, string named)
    {
        var fake = new FakeDataverse();
        await Run(fake, Schema());
        fake.SetCustomApi("ext_AdjustStock", key, JsonValue.Create(value)!);
        var before = fake.Writes.Count();
        var ex = await Assert.ThrowsAsync<GenerationConfirmationException>(() => Run(fake, Schema()));
        Assert.Contains("ext_AdjustStock", ex.Message);
        Assert.Contains(named, ex.Message);
        Assert.Equal(before, fake.Writes.Count());
    }

    [Fact]
    public async Task Generate_PlatformDroppedProvider_RerunSaysDeleteByHand()
    {
        var fake = new FakeDataverse { DropProviderOnCreate = true };
        var first = await Assert.ThrowsAsync<GenerationFailedException>(() => Run(fake, Schema()));
        Assert.Contains("delete ext_item by hand", first.Message);
        var again = await Assert.ThrowsAsync<GenerationRefusedException>(() => Run(fake, Schema()));
        Assert.Contains("DataProviderId", again.Message);
        Assert.Contains("delete ext_item by hand", again.Message);
    }

    [Fact]
    public async Task Generate_QuoteInInstanceName_EscapedInFilters()
    {
        var fake = new FakeDataverse();
        var options = Options with { Instance = "Inventory'API" };
        await Run(fake, Schema(), options);
        Assert.Contains(fake.Requests, r => r.Method == "GET" && r.Path.Contains("bf_serviceinstance eq 'Inventory''API'"));
        var before = fake.Writes.Count();
        await Run(fake, Schema(), options);
        Assert.Equal(before, fake.Writes.Count());
    }

    [Fact]
    public async Task Generate_PlatformDroppedProvider_Fails()
    {
        var fake = new FakeDataverse { DropProviderOnCreate = true };
        var ex = await Assert.ThrowsAsync<GenerationFailedException>(() => Run(fake, Schema()));
        Assert.Contains("DataProviderId", ex.Message);
        Assert.Contains("HLD 15", ex.Message);
    }

    [Fact]
    public async Task Generate_TableExistsButNoDataSourceRecord_Refuses()
    {
        var fake = new FakeDataverse();
        await Run(fake, Schema());
        fake.RenameDataSources("Renamed");
        var before = fake.Writes.Count();
        var ex = await Assert.ThrowsAsync<GenerationRefusedException>(() => Run(fake, Schema()));
        Assert.Contains("bf_serviceinstance", ex.Message);
        Assert.Contains("InventoryAPI", ex.Message);
        Assert.Equal(before, fake.Writes.Count());
    }

    [Fact]
    public async Task Generate_DryRun_NoWrites()
    {
        var fake = new FakeDataverse();
        var result = await Run(fake, Schema(), Options with { DryRun = true });
        Assert.Empty(fake.Writes);
        Assert.Contains(result.Writes, w => w.StartsWith("POST EntityDefinitions"));
        Assert.Contains(result.Writes, w => w.StartsWith("POST customapis"));
        Assert.Contains(result.Writes, w => w.StartsWith("POST EntityDefinitions(LogicalName='ext_item')/Attributes"));
    }

    [Fact]
    public async Task Generate_NoSecretInAnyPayload()
    {
        var fake = new FakeDataverse();
        await Run(fake, Schema());
        Assert.All(fake.Requests, r => Assert.Equal("Bearer " + Token, r.Authorization));
        Assert.All(fake.Writes, w =>
        {
            Assert.DoesNotContain(Token, w.Body ?? "");
            Assert.DoesNotContain("secret", w.Body ?? "", StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("password", w.Body ?? "", StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task Generate_ProviderMissing_FailsNamingSetup()
    {
        var fake = new FakeDataverse();
        var ex = await Assert.ThrowsAsync<GenerationRefusedException>(() =>
            Generator.RunAsync(new DataverseClient(new HttpClient(new NoProvider(fake)) { BaseAddress = new Uri(FakeDataverse.Org + "/api/data/v9.2/") }, _ => Task.FromResult(Token), false),
                Schema(), Options, CancellationToken.None));
        Assert.Contains("setup-dataverse.sh", ex.Message);
        Assert.Empty(fake.Writes);
    }

    private sealed class NoProvider(FakeDataverse inner) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            request.RequestUri!.AbsolutePath.EndsWith("entitydataproviders")
                ? Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("{\"value\":[]}") })
                : base.SendAsync(request, ct);
    }
}
