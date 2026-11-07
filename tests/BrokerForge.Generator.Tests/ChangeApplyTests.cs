using System.Text.Json.Nodes;
using BrokerForge.Schema;

namespace BrokerForge.Generator.Tests;

/// <summary>Preview, confirmation and apply (plan 3 task 3): view, form, role, removals and recreates.</summary>
public class ChangeApplyTests
{
    private static readonly IReadOnlySet<string> AllMessages = new HashSet<string> { "RetrieveMultiple", "Retrieve", "Create", "Update", "Delete" };
    private static readonly GenerateOptions Options = new("InventoryAPI", "BrokerForgeInventory", "brokerforgeext", "https://bf-host.example", "api://host-app/.default",
        DryRun: false, Messages: AllMessages);

    private const string WithoutNotes = "  ext_notes:  { type: memo, displayName: Notes }\n";

    private static SourceSchema Schema(string yaml = GeneratorTests.InventorySchema) => SchemaParser.Parse(yaml, "InventoryAPI.yaml");
    private static string Without(string line) => GeneratorTests.InventorySchema.Replace(line, "");

    private static DataverseClient Client(FakeDataverse fake) =>
        new(new HttpClient(fake) { BaseAddress = new Uri(FakeDataverse.Org + "/api/data/v9.2/") }, _ => Task.FromResult("token"), false);

    private static Task<GenerationResult> Run(FakeDataverse fake, SourceSchema schema, GenerateOptions? o = null) =>
        Generator.RunAsync(Client(fake), schema, o ?? Options, CancellationToken.None);

    private static Task<GenerationPreview> Preview(FakeDataverse fake, SourceSchema schema, GenerateOptions? o = null) =>
        Generator.PreviewAsync(Client(fake), schema, o ?? Options, CancellationToken.None);

    private static async Task<GenerationResult> Confirmed(FakeDataverse fake, SourceSchema schema)
    {
        var preview = await Preview(fake, schema);
        return await Run(fake, schema, Options with { ConfirmedFingerprint = preview.Changes.Fingerprint });
    }

    private static IReadOnlyList<string> FormFields(FakeDataverse fake) =>
        System.Xml.Linq.XElement.Parse((string)fake.MainForm("ext_item")["formxml"]!).Descendants("control").Select(c => (string)c.Attribute("datafieldname")!).ToList();

    private static IReadOnlyList<string> ViewCells(FakeDataverse fake) =>
        System.Xml.Linq.XElement.Parse((string)fake.DefaultView("ext_item")["layoutxml"]!).Descendants("cell").Select(c => (string)c.Attribute("name")!).ToList();

    [Fact]
    public async Task Run_NewSource_FormAndViewShowEveryColumn()
    {
        var fake = new FakeDataverse();
        await Run(fake, Schema());
        var all = new[] { "ext_name", "ext_onhand", "ext_price", "ext_state", "ext_active", "ext_added", "ext_notes" };
        Assert.Equal(all, FormFields(fake));
        Assert.Equal(all, ViewCells(fake));
        var fetch = (string)fake.DefaultView("ext_item")["fetchxml"]!;
        Assert.All(all, c => Assert.Contains($"<attribute name=\"{c}\" />", fetch));
        Assert.Contains("{C3EFE0C3-0EC6-42be-8349-CBD9079DFD8E}", (string)fake.MainForm("ext_item")["formxml"]!);
    }

    // Found while recording plan 5: a view sorted on a column the list declares no sort for fails loudly in the app
    // (UnsupportedQueryException), so the generated view never sorts. Rows come in the source's order.
    [Fact]
    public async Task Run_NewSource_ViewDoesNotSort()
    {
        var fake = new FakeDataverse();
        await Run(fake, Schema());
        Assert.DoesNotContain("<order", (string)fake.DefaultView("ext_item")["fetchxml"]!);
    }

    [Fact]
    public async Task Run_GeneratedViewThatSorts_Rewritten()
    {
        var fake = new FakeDataverse();
        await Run(fake, Schema());
        var view = fake.DefaultView("ext_item");
        view["fetchxml"] = ((string)view["fetchxml"]!).Replace("</entity>", "<order attribute=\"ext_name\" descending=\"false\" /></entity>");
        Assert.Contains((await Preview(fake, Schema())).Parts, p => p.StartsWith("show every column", StringComparison.Ordinal));
        await Run(fake, Schema());
        Assert.DoesNotContain("<order", (string)fake.DefaultView("ext_item")["fetchxml"]!);
    }

    // Found importing into a second environment (plan 5): without its data source record the source's solution is
    // refused there as a missing dependency (component type 183).
    [Fact]
    public async Task Run_NewSource_DataSourceRecordInTheSolution()
    {
        var fake = new FakeDataverse();
        await Run(fake, Schema());
        var id = (string)fake.DataSources.Single()["bf_brokerdatasourceid"]!;
        Assert.Equal(new[] { id }, fake.Components("BrokerForgeInventory", 183));
        var before = fake.Writes.Count();
        await Run(fake, Schema());
        Assert.DoesNotContain(fake.Writes.Skip(before), w => w.Path == "AddSolutionComponent");
    }

    [Theory]
    [InlineData("RetrieveMultiple,Retrieve,Create,Update,Delete", "Create,Delete,Read,Write")]
    [InlineData("RetrieveMultiple", "Read")]
    public async Task Run_NewSource_RoleWithPrivilegesFromMessages(string messages, string verbs)
    {
        var fake = new FakeDataverse();
        await Run(fake, Schema(), Options with { Messages = messages.Split(',').ToHashSet() });
        var held = fake.RolePrivileges("InventoryAPI user");
        Assert.Equal(verbs.Split(','), held.Keys.Where(k => k.EndsWith("ext_item")).Select(k => k[3..^"ext_item".Length]).Order());
        Assert.All(held.Where(k => k.Key.EndsWith("ext_item")), p => Assert.Equal(8, p.Value));
        Assert.Contains("prvReadPluginType", held.Keys);
        Assert.Equal("BrokerForgeInventory", fake.Writes.Single(w => w.Path == "roles").Solution);
    }

    [Fact]
    public async Task Run_RoleNarrowed_OnlyThisTablesPrivilegesRemoved()
    {
        var fake = new FakeDataverse();
        await Run(fake, Schema());
        await Run(fake, Schema(), Options with { Messages = new HashSet<string> { "RetrieveMultiple" } });
        var held = fake.RolePrivileges("InventoryAPI user");
        Assert.Equal(new[] { "prvReadPluginType", "prvReadext_item" }, held.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Run_RemovalWithoutFingerprint_Refused()
    {
        var fake = new FakeDataverse();
        await Run(fake, Schema());
        var before = fake.Writes.Count();
        var ex = await Assert.ThrowsAsync<GenerationConfirmationException>(() => Run(fake, Schema(Without(WithoutNotes))));
        Assert.Contains(ex.Preview.Changes.Fingerprint, ex.Message);
        Assert.Equal(before, fake.Writes.Count());
    }

    [Fact]
    public async Task Run_FingerprintStale_RefusedNothingWritten()
    {
        var fake = new FakeDataverse();
        await Run(fake, Schema());
        var confirmed = (await Preview(fake, Schema(Without(WithoutNotes)))).Changes.Fingerprint;
        // Before Generate, the schema also loses another column: the list is no longer the confirmed one.
        var now = Schema(Without(WithoutNotes).Replace("  ext_added:  { type: date, displayName: Added }\n", ""));
        var before = fake.Writes.Count();
        var ex = await Assert.ThrowsAsync<GenerationConfirmationException>(() => Run(fake, now, Options with { ConfirmedFingerprint = confirmed }));
        Assert.Contains("not the ones confirmed", ex.Message);
        Assert.Contains("remove column Added (ext_added)", ex.Message);
        Assert.Equal(before, fake.Writes.Count());
    }

    [Fact]
    public async Task Run_BlockedRemoval_NoWritesAtAll()
    {
        var fake = new FakeDataverse();
        await Run(fake, Schema());
        fake.AddHandView("ext_item", "ext_notes", "Notes view");
        var schema = Schema(Without(WithoutNotes) + "");
        var withAdd = Schema(Without(WithoutNotes).Replace("  ext_added:", "  ext_sku:    { type: string, maxLength: 40, displayName: SKU }\n  ext_added:"));
        var preview = await Preview(fake, withAdd);
        var blocked = Assert.Single(preview.Blocked);
        Assert.Equal("remove column Notes (ext_notes)", blocked.Change.Text);
        Assert.StartsWith("view \"Notes view\"", blocked.Dependent);

        var before = fake.Writes.Count();
        var ex = await Assert.ThrowsAsync<GenerationRefusedException>(() => Run(fake, withAdd, Options with { ConfirmedFingerprint = preview.Changes.Fingerprint }));
        Assert.Contains("Notes view", ex.Message);
        Assert.Equal(before, fake.Writes.Count());

        fake.RemoveHandViews();
        await Confirmed(fake, schema);
        Assert.DoesNotContain("ext_notes", FormFields(fake));
    }

    [Fact]
    public async Task Run_RemoveColumn_TakenOffFormAndViewThenDeleted()
    {
        var fake = new FakeDataverse();
        await Run(fake, Schema());
        var before = fake.Writes.Count();
        await Confirmed(fake, Schema(Without(WithoutNotes)));
        var writes = fake.Writes.Skip(before).Select(w => $"{w.Method} {w.Path.Split('(')[0]}").ToList();
        Assert.Equal(new[] { "PATCH systemforms", "PATCH savedqueries", "POST PublishXml", "DELETE EntityDefinitions", "POST PublishXml" }, writes);
        Assert.DoesNotContain("ext_notes", FormFields(fake));
        Assert.DoesNotContain("ext_notes", ViewCells(fake));
        Assert.Empty((await Preview(fake, Schema(Without(WithoutNotes)))).Changes.Changes);
    }

    [Fact]
    public async Task Run_TypeChange_DeleteThenAddSameName()
    {
        var fake = new FakeDataverse();
        await Run(fake, Schema());
        var changed = Schema(GeneratorTests.InventorySchema.Replace("ext_onhand: { type: int, displayName: On hand }", "ext_onhand: { type: decimal, precision: 2, displayName: On hand }"));
        var before = fake.Writes.Count();
        await Confirmed(fake, changed);
        var writes = fake.Writes.Skip(before).ToList();
        var delete = writes.FindIndex(w => w.Method == "DELETE" && w.Path.EndsWith("Attributes(LogicalName='ext_onhand')"));
        var add = writes.FindIndex(w => w.Method == "POST" && w.Path.EndsWith("/Attributes") && w.Body!.Contains("ext_onhand"));
        Assert.True(delete >= 0 && add > delete, string.Join("\n", writes.Select(w => $"{w.Method} {w.Path}")));
        Assert.Contains("DecimalAttributeMetadata", writes[add].Body);
        Assert.Equal(FormFields(fake), ViewCells(fake));
        Assert.Contains("ext_onhand", FormFields(fake));
        Assert.Empty((await Preview(fake, changed)).Changes.Changes);
    }

    [Fact]
    public async Task Run_DisplayNames_RelabelledInPlace()
    {
        var fake = new FakeDataverse();
        await Run(fake, Schema());
        var renamed = Schema(GeneratorTests.InventorySchema.Replace("displayName: On hand", "displayName: In stock").Replace("displayCollectionName: Items", "displayCollectionName: Stock items"));
        var before = fake.Writes.Count();
        await Confirmed(fake, renamed);
        var puts = fake.Writes.Skip(before).Where(w => w.Method == "PUT").Select(w => w.Path).ToList();
        Assert.Equal(new[] { "EntityDefinitions(LogicalName='ext_item')/Attributes(LogicalName='ext_onhand')", "EntityDefinitions(LogicalName='ext_item')" }, puts);
        Assert.DoesNotContain(fake.Writes.Skip(before), w => w.Method == "DELETE");
        Assert.Empty((await Preview(fake, renamed)).Changes.Changes);
    }

    [Fact]
    public async Task Run_ApiInputChange_DeleteThenCreate()
    {
        var fake = new FakeDataverse();
        await Run(fake, Schema());
        var changed = Schema(GeneratorTests.InventorySchema.Replace("Reason: { type: string }", "Reason: { type: string, required: true }"));
        var before = fake.Writes.Count();
        await Confirmed(fake, changed);
        var writes = fake.Writes.Skip(before).Select(w => $"{w.Method} {w.Path.Split('(')[0]}").ToList();
        Assert.Equal(new[] { "DELETE customapis", "POST customapis" }, writes);
        Assert.Empty((await Preview(fake, changed)).Changes.Changes);
    }

    [Fact]
    public async Task Run_ApiGone_RemovedOnlyFromThisSolution()
    {
        var fake = new FakeDataverse();
        await Run(fake, Schema());
        // Another source in another solution uses the same prefix: its action is not this source's to remove.
        await Run(fake, Schema(GeneratorTests.InventorySchema.Replace("ext_AdjustStock", "ext_OtherAction")).Without("table"), Options with { Instance = "OtherAPI", Solution = "BrokerForgeOther" });
        var noApis = Schema(GeneratorTests.InventorySchema[..GeneratorTests.InventorySchema.IndexOf("customApis:", StringComparison.Ordinal)]);
        var preview = await Preview(fake, noApis);
        Assert.Equal(new[] { "remove action Adjust stock (ext_AdjustStock)" }, preview.Changes.Changes.Select(c => c.Text));
    }

    [Fact]
    public async Task Run_FailsHalfway_RerunConverges()
    {
        var fake = new FakeDataverse { FailWrite = 4 };
        var ex = await Assert.ThrowsAsync<GenerationFailedException>(() => Run(fake, Schema()));
        Assert.Equal(3, ex.WritesDone.Count);
        Assert.Contains("injected failure", ex.Message);
        Assert.False(string.IsNullOrEmpty(ex.Step));
        // The failed call was the fourth write (the table); the re-run makes it once and repeats nothing done before.
        var rerun = await Run(fake, Schema());
        Assert.DoesNotContain(rerun.Writes, w => ex.WritesDone.Contains(w) && w.StartsWith("POST", StringComparison.Ordinal));
        Assert.Single(rerun.Writes, w => w == "POST EntityDefinitions");
        Assert.Single(fake.Writes, w => w.Method == "POST" && w.Path == "customapis");
        Assert.Single(fake.Writes, w => w.Method == "POST" && w.Path == "roles");
        Assert.Empty((await Preview(fake, Schema())).Changes.Changes);
    }

    [Fact]
    public async Task Run_Twice_SecondRunNoWrites()
    {
        var fake = new FakeDataverse();
        await Run(fake, Schema());
        var before = fake.Writes.Count();
        var second = await Run(fake, Schema());
        Assert.Empty(second.Writes);
        Assert.Equal(before, fake.Writes.Count());
        Assert.Empty((await Preview(fake, Schema())).Parts);
    }

    [Fact]
    public async Task Preview_WritesNothing()
    {
        var fake = new FakeDataverse();
        await Run(fake, Schema());
        var before = fake.Writes.Count();
        var preview = await Preview(fake, Schema(Without(WithoutNotes)));
        Assert.Single(preview.Changes.Changes);
        Assert.Equal(before, fake.Writes.Count());
        var fresh = await Preview(new FakeDataverse(), Schema());
        Assert.Contains(fresh.Parts, p => p.StartsWith("create role InventoryAPI user with Read, Create, Write, Delete"));
        Assert.Contains(fresh.Parts, p => p.StartsWith("show every column"));
    }

    [Fact]
    public async Task Run_RenameOnly_FormLabelUpdated()
    {
        var fake = new FakeDataverse();
        await Run(fake, Schema());
        var renamed = Schema(GeneratorTests.InventorySchema.Replace("displayName: On hand", "displayName: In stock"));
        await Confirmed(fake, renamed);
        var form = (string)fake.MainForm("ext_item")["formxml"]!;
        Assert.Contains("description=\"In stock\"", form);
        Assert.DoesNotContain("description=\"On hand\"", form);
        Assert.Empty((await Preview(fake, renamed)).Parts);
    }

    [Fact]
    public async Task Run_RefusalAfterWrites_FailedNamingWrites()
    {
        var fake = new FakeDataverse { DropProviderOnCreate = true };
        var ex = await Assert.ThrowsAsync<GenerationFailedException>(() => Run(fake, Schema()));
        Assert.Contains("HLD 15", ex.Message);
        Assert.Contains("POST EntityDefinitions", ex.WritesDone);
        Assert.DoesNotContain("made no changes", ex.Message);
    }

    [Theory]
    [InlineData("{\"password\":\"x\"}", "InventoryAPI")]
    [InlineData(null, "Inventory API")]
    public async Task Preview_BadDesignValues_RefusedBeforeAnyWrite(string? auth, string instance)
    {
        var fake = new FakeDataverse();
        var design = new BrokerForge.Connections.ConnectionDesign(1, instance, 0, "rest", "openapi: 3.0.3", new Dictionary<string, string>(),
            new BrokerForge.Connections.DesignAuth("Static", "ApiKeyQuery", null, "api_key", null, null, null, null, null, ["apiKey"]), null,
            GeneratorTests.InventorySchema, new Dictionary<string, string>());
        var o = Options with { Design = new DesignPublish(design, null, auth) };
        var preview = await Preview(fake, Schema(), o);
        Assert.NotEmpty(preview.Changes.Refusals);
        await Assert.ThrowsAsync<GenerationRefusedException>(() => Run(fake, Schema(), o));
        Assert.Empty(fake.Writes);
    }

    [Fact]
    public async Task Run_RoleLeftToAdmin_PendingNamedNoRoleWrites()
    {
        var fake = new FakeDataverse();
        var asHost = Options with { ApplyRole = false };
        var preview = await Preview(fake, Schema(), asHost);
        Assert.Equal(new[] { "add action Adjust stock (ext_AdjustStock)", "create role InventoryAPI user with Read, Create, Write, Delete on ext_item" }, preview.Pending);
        Assert.DoesNotContain(preview.Parts, p => p.Contains("role"));
        var result = await Run(fake, Schema(), asHost);
        Assert.Equal(preview.Pending, result.Pending);
        Assert.DoesNotContain(fake.Writes, w => w.Path.StartsWith("roles", StringComparison.Ordinal));

        await Run(fake, Schema());
        Assert.Empty((await Preview(fake, Schema(), asHost)).Pending);
        Assert.Empty((await Run(fake, Schema(), asHost)).Pending);
    }

    // Live in plan 5: Dataverse leaves prvCreateSdkMessageProcessingStep out of the host's effective privileges, so the
    // host cannot create a Custom API; actions are an administrator's step like the role (6 November 2026).
    [Fact]
    public async Task Run_ActionsLeftToAdmin_PendingNamedNoActionWrites()
    {
        var fake = new FakeDataverse();
        var asHost = Options with { ApplyRole = false };
        var result = await Run(fake, Schema(), asHost);
        Assert.Contains("add action Adjust stock (ext_AdjustStock)", result.Pending);
        Assert.DoesNotContain(fake.Writes, w => w.Path.StartsWith("customapis", StringComparison.Ordinal));

        var admin = await Run(fake, Schema());
        Assert.Contains(fake.Writes, w => w.Path.StartsWith("customapis", StringComparison.Ordinal));
        Assert.Empty((await Preview(fake, Schema(), asHost)).Pending);
    }

    [Fact]
    public async Task Run_ActionRemovalLeftToAdmin_NoDelete()
    {
        var fake = new FakeDataverse();
        await Run(fake, Schema());
        var without = Schema(GeneratorTests.InventorySchema[..GeneratorTests.InventorySchema.IndexOf("customApis:", StringComparison.Ordinal)]);
        var asHost = Options with { ApplyRole = false, ConfirmedFingerprint = (await Preview(fake, without, Options with { ApplyRole = false })).Changes.Fingerprint };
        var result = await Run(fake, without, asHost);
        Assert.Contains("remove action Adjust stock (ext_AdjustStock)", result.Pending);
        Assert.DoesNotContain(fake.Writes, w => w.Method == "DELETE" && w.Path.StartsWith("customapis", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Run_RoleLeftToAdmin_HandMadeRoleStillRefused()
    {
        var fake = new FakeDataverse();
        fake.AddRole("InventoryAPI user");
        await Assert.ThrowsAsync<GenerationRefusedException>(() => Run(fake, Schema(), Options with { ApplyRole = false }));
        Assert.Empty(fake.Writes);
    }

    [Fact]
    public async Task Run_NetworkFailureHalfway_FailedNamingStepAndWrites()
    {
        var fake = new FakeDataverse { ThrowWrite = 4 };
        var ex = await Assert.ThrowsAsync<GenerationFailedException>(() => Run(fake, Schema()));
        Assert.Equal(3, ex.WritesDone.Count);
        Assert.False(string.IsNullOrEmpty(ex.Step));
        Assert.Contains("injected network failure", ex.Message);
        Assert.IsType<HttpRequestException>(ex.InnerException);
    }

    [Fact]
    public async Task Run_CallerCancels_NotWrapped()
    {
        var fake = new FakeDataverse { Delay = TimeSpan.FromSeconds(5) };
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Generator.RunAsync(Client(fake), Schema(), Options, cts.Token));
    }

    [Fact]
    public async Task Preview_SolutionInternalDependent_NotBlocking()
    {
        var fake = new FakeDataverse();
        await Run(fake, Schema());
        fake.AddDependent("ext_item", "ext_notes", 60, "00000000-0000-0000-0000-00000000ff01", dependencyType: 1);
        var preview = await Preview(fake, Schema(Without(WithoutNotes)));
        Assert.Empty(preview.Blocked);
        await Confirmed(fake, Schema(Without(WithoutNotes)));
        Assert.DoesNotContain("ext_notes", FormFields(fake));
    }

    [Theory]
    [InlineData(61, null, "web resource (00000000-0000-0000-0000-00000000ff02)")]
    [InlineData(10099, "msdyn_widget", "msdyn_widget (00000000-0000-0000-0000-00000000ff02)")]
    [InlineData(10098, null, "component of type 10098 (00000000-0000-0000-0000-00000000ff02)")]
    public async Task Preview_OtherDependentTypes_Named(int type, string? definition, string named)
    {
        var fake = new FakeDataverse();
        await Run(fake, Schema());
        if (definition != null)
            fake.AddComponentDefinition(type, definition);
        fake.AddDependent("ext_item", "ext_notes", type, "00000000-0000-0000-0000-00000000ff02");
        var blocked = Assert.Single((await Preview(fake, Schema(Without(WithoutNotes)))).Blocked);
        Assert.Equal(named, blocked.Dependent);
    }

    [Fact]
    public async Task Run_HandMadeRoleWithTheName_RefusedNothingWritten()
    {
        var fake = new FakeDataverse();
        var hand = fake.AddRole("InventoryAPI user");
        var preview = await Preview(fake, Schema());
        Assert.Contains("role InventoryAPI user exists but is not in solution BrokerForgeInventory; rename it or add it to the solution", preview.Changes.Refusals);
        var ex = await Assert.ThrowsAsync<GenerationRefusedException>(() => Run(fake, Schema()));
        Assert.Contains("InventoryAPI user", ex.Message);
        Assert.Empty(fake.Writes);
        Assert.Empty(fake.RolePrivileges("InventoryAPI user"));
        _ = hand;
    }

    [Fact]
    public async Task Run_DryRunWithRemoval_PlansWithoutConfirmation()
    {
        var fake = new FakeDataverse();
        await Run(fake, Schema());
        var before = fake.Writes.Count();
        var dry = new DataverseClient(new HttpClient(fake) { BaseAddress = new Uri(FakeDataverse.Org + "/api/data/v9.2/") }, _ => Task.FromResult("token"), true);
        var result = await Generator.RunAsync(dry, Schema(Without(WithoutNotes)), Options with { DryRun = true }, CancellationToken.None);
        Assert.Contains(result.Writes, w => w.StartsWith("DELETE", StringComparison.Ordinal) && w.Contains("ext_notes"));
        Assert.Equal(before, fake.Writes.Count());
    }
}

internal static class SchemaTestExtensions
{
    /// <summary>The schema without its table, keeping only its Custom APIs.</summary>
    public static SourceSchema Without(this SourceSchema s, string _) => s with { Table = null, DisplayName = null, DisplayCollectionName = null, ExternalName = null, PrimaryName = null, Columns = new Dictionary<string, ColumnSchema>() };
}
