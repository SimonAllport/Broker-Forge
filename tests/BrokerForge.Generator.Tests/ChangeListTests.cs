using BrokerForge.Schema;

namespace BrokerForge.Generator.Tests;

public class ChangeListTests
{
    private const string Provider = FakeDataverse.ProviderId;
    private const string DataSource = "00000000-0000-0000-0000-0000000000d5";
    private const string PluginType = FakeDataverse.CustomApiPluginTypeId;

    private static SourceSchema Schema(string yaml = GeneratorTests.InventorySchema) => SchemaParser.Parse(yaml, "InventoryAPI.yaml");

    /// <summary>What Dataverse would hold after generating <paramref name="s"/> exactly.</summary>
    private static ExistingTable Existing(SourceSchema s) => new("Virtual", Provider, DataSource, s.ExternalName,
        s.Columns.Select(c => new ExistingColumn(c.Key, c.Value.Type, c.Key, c.Value.MaxLength ?? (c.Value.Type == "memo" ? 100000 : null), c.Value.Precision,
            c.Value.Options?.Values.ToList(), c.Value.DisplayName)).ToList(), s.PrimaryName, s.DisplayName, s.DisplayCollectionName);

    private static Dictionary<string, ExistingCustomApi?> Apis(SourceSchema s) => s.CustomApis.ToDictionary(a => a.Key, a => (ExistingCustomApi?)new ExistingCustomApi(PluginType, 0, false,
        a.Value.Inputs.ToDictionary(i => i.Key, i => (GenerationPlan.ParamTypeCodes[i.Value.Type], !i.Value.Required)),
        a.Value.Outputs.ToDictionary(o => o.Key, o => GenerationPlan.ParamTypeCodes[o.Value]), a.Value.DisplayName));

    private static ChangeList Diff(SourceSchema wanted, SourceSchema? had) =>
        GenerationPlan.Diff(wanted, had == null ? null : Existing(had), had == null ? wanted.CustomApis.ToDictionary(a => a.Key, _ => (ExistingCustomApi?)null) : Apis(had),
            Provider, had == null ? null : DataSource, PluginType, "InventoryAPI");

    [Fact]
    public void Diff_NothingExists_AddTableAndColumnsAndApis()
    {
        var list = Diff(Schema(), null);
        Assert.Empty(list.Refusals);
        Assert.Equal(ChangeKind.AddTable, list.Changes[0].Kind);
        Assert.Equal(6, list.Changes.Count(c => c.Kind == ChangeKind.AddColumn));
        Assert.DoesNotContain(list.Changes, c => c.Target == "ext_item.ext_name" && c.Kind == ChangeKind.AddColumn);
        Assert.Single(list.Changes, c => c is { Kind: ChangeKind.AddApi, Target: "ext_AdjustStock" });
        Assert.Contains(list.Changes, c => c.Text == "add column On hand (ext_onhand, int)");
    }

    [Fact]
    public void Diff_Same_NoChangesFingerprintNone()
    {
        var list = Diff(Schema(), Schema());
        Assert.Empty(list.Changes);
        Assert.Empty(list.Refusals);
        Assert.Equal("none", list.Fingerprint);
    }

    [Fact]
    public void Diff_PropertyGone_RemoveColumn()
    {
        var list = Diff(Schema(GeneratorTests.InventorySchema.Replace("  ext_notes:  { type: memo, displayName: Notes }\n", "")), Schema());
        var c = Assert.Single(list.Changes);
        Assert.Equal((ChangeKind.RemoveColumn, "ext_item.ext_notes", "remove column Notes (ext_notes)"), (c.Kind, c.Target, c.Text));
    }

    [Fact]
    public void Diff_TypeChanged_RecreateWithBothTypesInText()
    {
        var list = Diff(Schema(GeneratorTests.InventorySchema.Replace("ext_onhand: { type: int, displayName: On hand }", "ext_onhand: { type: decimal, precision: 2, displayName: On hand }")), Schema());
        var c = Assert.Single(list.Changes);
        Assert.Equal((ChangeKind.RecreateColumn, "change On hand (ext_onhand) from int to decimal 2"), (c.Kind, c.Text));
    }

    [Fact]
    public void Diff_MaxLengthChanged_Recreate()
    {
        var list = Diff(Schema(GeneratorTests.InventorySchema.Replace("maxLength: 100, displayName: Name", "maxLength: 200, displayName: Name")
            .Replace("ext_notes:  { type: memo, displayName: Notes }", "ext_notes:  { type: memo, maxLength: 2000, displayName: Notes }")), Schema());
        Assert.Contains(list.Refusals, r => r.Contains("ext_name") && r.Contains("primary name"));
        Assert.Single(list.Changes, c => c is { Kind: ChangeKind.RecreateColumn, Target: "ext_item.ext_notes" });
    }

    [Fact]
    public void Diff_DisplayNameOnly_UpdateInPlace()
    {
        var list = Diff(Schema(GeneratorTests.InventorySchema.Replace("displayName: On hand", "displayName: In stock")), Schema());
        var c = Assert.Single(list.Changes);
        Assert.Equal((ChangeKind.UpdateColumn, "rename ext_onhand from On hand to In stock"), (c.Kind, c.Text));
    }

    [Fact]
    public void Diff_TableDisplayName_UpdateTable()
    {
        var list = Diff(Schema(GeneratorTests.InventorySchema.Replace("displayCollectionName: Items", "displayCollectionName: Stock items")), Schema());
        var c = Assert.Single(list.Changes);
        Assert.Equal((ChangeKind.UpdateTable, "ext_item"), (c.Kind, c.Target));
        Assert.Contains("Stock items", c.Text);
    }

    [Fact]
    public void Diff_PrimaryNameGone_Refusal()
    {
        var yaml = GeneratorTests.InventorySchema.Replace("primaryName: ext_name", "primaryName: ext_title")
            .Replace("ext_name:   { type: string, maxLength: 100, displayName: Name }", "ext_title:  { type: string, maxLength: 100, displayName: Title }");
        var list = Diff(Schema(yaml), Schema());
        Assert.Contains(list.Refusals, r => r.Contains("primary name") && r.Contains("ext_name") && r.Contains("ext_title"));
    }

    [Fact]
    public void Diff_ApiInputChanged_RecreateApi()
    {
        var list = Diff(Schema(GeneratorTests.InventorySchema.Replace("Reason: { type: string }", "Reason: { type: string, required: true }")), Schema());
        var c = Assert.Single(list.Changes);
        Assert.Equal((ChangeKind.RecreateApi, "ext_AdjustStock"), (c.Kind, c.Target));
        Assert.Contains("Reason", c.Text);
    }

    [Fact]
    public void Diff_ApiGone_RemoveApi()
    {
        var yaml = GeneratorTests.InventorySchema[..GeneratorTests.InventorySchema.IndexOf("customApis:", StringComparison.Ordinal)];
        var list = Diff(Schema(yaml), Schema());
        var c = Assert.Single(list.Changes);
        Assert.Equal((ChangeKind.RemoveApi, "remove action Adjust stock (ext_AdjustStock)"), (c.Kind, c.Text));
    }

    [Fact]
    public void Diff_SameInputTwice_SameFingerprint()
    {
        var changed = Schema(GeneratorTests.InventorySchema.Replace("  ext_notes:  { type: memo, displayName: Notes }\n", ""));
        Assert.Equal(Diff(changed, Schema()).Fingerprint, Diff(changed, Schema()).Fingerprint);
        Assert.Matches("^[0-9a-f]{16}$", Diff(changed, Schema()).Fingerprint);
    }

    [Fact]
    public void Diff_DifferentList_DifferentFingerprint()
    {
        var a = Diff(Schema(GeneratorTests.InventorySchema.Replace("  ext_notes:  { type: memo, displayName: Notes }\n", "")), Schema());
        var b = Diff(Schema(GeneratorTests.InventorySchema.Replace("  ext_added:  { type: date, displayName: Added }\n", "")), Schema());
        Assert.NotEqual(a.Fingerprint, b.Fingerprint);
    }

    [Fact]
    public void Diff_OrderedByKindThenTarget()
    {
        var yaml = GeneratorTests.InventorySchema.Replace("  ext_notes:  { type: memo, displayName: Notes }\n", "  ext_zeta:   { type: int, displayName: Zeta }\n  ext_alpha:  { type: int, displayName: Alpha }\n")
            .Replace("displayName: On hand", "displayName: In stock");
        var list = Diff(Schema(yaml), Schema());
        Assert.Equal(new[] { "AddColumn ext_item.ext_alpha", "AddColumn ext_item.ext_zeta", "UpdateColumn ext_item.ext_onhand", "RemoveColumn ext_item.ext_notes" },
            list.Changes.Select(c => $"{c.Kind} {c.Target}"));
    }
}
