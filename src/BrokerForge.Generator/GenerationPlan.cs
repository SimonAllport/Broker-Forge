using BrokerForge.Schema;

namespace BrokerForge.Generator;

/// <param name="Instance">Service instance name; the data source record is found by it.</param>
/// <param name="Solution">Unique name of the source solution that receives the table and Custom APIs.</param>
/// <param name="Publisher">Unique name of the publisher whose prefix the schema uses.</param>
/// <param name="Messages">The table messages the descriptors bind (RetrieveMultiple, Retrieve, Create, Update, Delete); they decide the role's privileges.</param>
/// <param name="ConfirmedFingerprint">The fingerprint of the change list the admin confirmed. Needed for any change other than an add.</param>
/// <param name="Design">The connection design to publish with its parts, last (DesignPublisher); null to leave the design alone.</param>
/// <summary>
/// <see cref="ApplyRole"/> false leaves the role, and creating, recreating or removing actions, to an administrator. Dataverse
/// lets a caller add a privilege to a role only when it holds that privilege itself, and the host doesn't hold a generated
/// table's (portal-3 findings section 3); and it leaves prvCreateSdkMessageProcessingStep, which a Custom API needs, out of
/// the host's effective privileges whatever its role holds (portal-5 findings). Both are reported as pending, never skipped silently.
/// </summary>
public sealed record GenerateOptions(string Instance, string Solution, string Publisher, string HostUrl, string Scope, bool DryRun,
    IReadOnlySet<string>? Messages = null, string? ConfirmedFingerprint = null, DesignPublish? Design = null, bool ApplyRole = true);

/// <summary>A design and this environment's own values for its two variables (null leaves a value as it is).</summary>
public sealed record DesignPublish(BrokerForge.Connections.ConnectionDesign Design, string? BaseUrl, string? AuthJson);

/// <summary>A change that can't be applied because something outside BrokerForge's generated parts depends on what it deletes.</summary>
public sealed record Blocked(Change Change, string Dependent);

/// <summary>
/// What a run would do. <see cref="Parts"/> lists the form, view and role writes that follow from the schema; they
/// need no confirmation. Generate applies the run only when nothing is refused or blocked and the change list's
/// fingerprint is the confirmed one.
/// </summary>
/// <summary><see cref="Pending"/> lists what this run will not do and an administrator must (the role, when <see cref="GenerateOptions.ApplyRole"/> is false).</summary>
public sealed record GenerationPreview(ChangeList Changes, IReadOnlyList<Blocked> Blocked, IReadOnlyList<string> Parts, IReadOnlyList<string> Pending)
{
    public bool NeedsConfirmation => Changes.Changes.Any(c => c.Kind is not (ChangeKind.AddTable or ChangeKind.AddColumn or ChangeKind.AddApi));
}

/// <summary>The change list has changes that need confirming, or not the ones confirmed. Nothing was written.</summary>
public sealed class GenerationConfirmationException(GenerationPreview preview, string message) : Exception(message)
{
    public GenerationPreview Preview { get; } = preview;
}

/// <summary>A Dataverse call failed while applying. <see cref="WritesDone"/> lists the writes made before it; a fresh preview and run continue from there.</summary>
public sealed class GenerationFailedException(string step, IReadOnlyList<string> writesDone, Exception inner)
    : Exception($"Generation failed at step '{step}' after {writesDone.Count} write(s): "
        + (inner is GenerationRefusedException r ? string.Join("; ", r.Differences) : inner.Message), inner)
{
    public string Step { get; } = step;
    public IReadOnlyList<string> WritesDone { get; } = writesDone;
}

/// <summary><see cref="Writes"/> lists the write calls made, or planned in a dry run; empty when nothing changed. <see cref="DesignVersion"/> is the design version now published, when a design was given. <see cref="Pending"/> is what an administrator still has to do.</summary>
public sealed record GenerationResult(IReadOnlyList<string> Writes, int? DesignVersion = null, IReadOnlyList<string>? Pending = null)
{
    /// <summary>What an administrator still has to do; empty when the run did everything.</summary>
    public IReadOnlyList<string> Pending { get; init; } = Pending ?? Array.Empty<string>();
}

/// <summary>The schema and the environment disagree in a way the generator will not change. Nothing was written.</summary>
public sealed class GenerationRefusedException(IReadOnlyList<string> differences)
    : Exception("The generator made no changes:" + string.Concat(differences.Select(d => "\n - " + d)))
{
    public IReadOnlyList<string> Differences { get; } = differences;
}

/// <summary>A column as it exists in Dataverse, described with schema type names (descriptor spec 14).</summary>
public sealed record ExistingColumn(string LogicalName, string Type, string? ExternalName, int? MaxLength, int? Precision, IReadOnlyList<int>? Options, string? DisplayName = null, string? MetadataId = null);

/// <summary><see cref="PrimaryName"/>, <see cref="DisplayName"/> and <see cref="DisplayCollectionName"/> are null when not read.</summary>
public sealed record ExistingTable(string? TableType, string? DataProviderId, string? DataSourceId, string? ExternalName, IReadOnlyList<ExistingColumn> Columns,
    string? PrimaryName = null, string? DisplayName = null, string? DisplayCollectionName = null);

public sealed record ExistingCustomApi(string PluginTypeId, int BindingType, bool IsFunction, IReadOnlyDictionary<string, (int Type, bool Optional)> Inputs,
    IReadOnlyDictionary<string, int> Outputs, string? DisplayName = null, string? Id = null);

/// <summary>The order is the order a change list is shown and applied in.</summary>
public enum ChangeKind { AddTable, UpdateTable, AddColumn, UpdateColumn, RemoveColumn, RecreateColumn, AddApi, UpdateApi, RemoveApi, RecreateApi }

/// <summary>One change, in plain words. <see cref="Target"/> is <c>table.column</c>, the table, or the Custom API's unique name.</summary>
public sealed record Change(ChangeKind Kind, string Target, string Text);

/// <summary>
/// What generating would change, ordered by kind then target. <see cref="Refusals"/> are differences the generator
/// will not change at all. <see cref="Fingerprint"/> identifies the list, so that what is applied is what was confirmed.
/// </summary>
public sealed record ChangeList(IReadOnlyList<Change> Changes, IReadOnlyList<string> Refusals, string Fingerprint)
{
    public static string FingerprintOf(IEnumerable<Change> changes)
    {
        var text = string.Join("\n", changes.Select(c => c.Text));
        return text.Length == 0 ? "none"
            : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)))[..16].ToLowerInvariant();
    }
}

/// <summary>
/// Compares the schema with what exists. A display name change is an update in place. Any other column or Custom API
/// change is a recreate (remove, then add under the same name): virtual tables hold no data, so nothing is lost. A
/// column or Custom API that the schema no longer has is a removal. The table itself is never re-pointed, re-keyed or
/// given another primary name: those are refusals.
/// </summary>
public static class GenerationPlan
{
    /// <summary>Custom API parameter type codes (customapirequestparameter.type).</summary>
    public static readonly IReadOnlyDictionary<string, int> ParamTypeCodes = new Dictionary<string, int>
    {
        ["bool"] = 0, ["datetime"] = 1, ["decimal"] = 2, ["int"] = 7, ["string"] = 10, ["guid"] = 12,
    };

    public static ChangeList Diff(SourceSchema schema, ExistingTable? table, IReadOnlyDictionary<string, ExistingCustomApi?> apis,
        string providerId, string? dataSourceId, string? customApiPluginTypeId, string instance)
    {
        var refusals = new List<string>();
        var changes = new List<Change>();
        if (schema.Table is { } name)
        {
            if (table == null)
            {
                changes.Add(new Change(ChangeKind.AddTable, name, $"add table {schema.DisplayName} ({name})"));
                foreach (var (column, declared) in schema.Columns.Where(c => c.Key != schema.PrimaryName))
                    changes.Add(new Change(ChangeKind.AddColumn, $"{name}.{column}", $"add column {declared.DisplayName} ({column}, {Describe(declared)})"));
            }
            else
            {
                if (table.TableType != "Virtual")
                    refusals.Add($"{name} exists as a {table.TableType ?? "(unknown)"} table, not a virtual table; {DeleteByHand(name)}");
                if (!SameId(table.DataProviderId, providerId))
                    refusals.Add($"{name} DataProviderId is {table.DataProviderId ?? "(none)"}, not the BrokerForge provider {providerId}; {DeleteByHand(name)}");
                if (dataSourceId == null)
                    refusals.Add($"{name} exists and uses data source {table.DataSourceId ?? "(none)"}, but no bf_brokerdatasource record has bf_serviceinstance = {instance}; creating one would leave the table pointing elsewhere");
                else if (!SameId(table.DataSourceId, dataSourceId))
                    refusals.Add($"{name} DataSourceId is {table.DataSourceId ?? "(none)"}, not the {schema.Table} data source record {dataSourceId}");
                if (table.ExternalName != schema.ExternalName)
                    refusals.Add($"{name} ExternalName is {table.ExternalName ?? "(none)"}, the schema says {schema.ExternalName}; {DeleteByHand(name)}");
                if (table.PrimaryName != null && table.PrimaryName != schema.PrimaryName)
                    refusals.Add($"{name}'s primary name column is {table.PrimaryName} in Dataverse but {schema.PrimaryName} in the schema; a table's primary name can't change, so {DeleteByHand(name)}");
                if ((table.DisplayName != null && table.DisplayName != schema.DisplayName) || (table.DisplayCollectionName != null && table.DisplayCollectionName != schema.DisplayCollectionName))
                    changes.Add(new Change(ChangeKind.UpdateTable, name,
                        $"rename table from {table.DisplayName} / {table.DisplayCollectionName} to {schema.DisplayName} / {schema.DisplayCollectionName}"));

                var existing = table.Columns.ToDictionary(c => c.LogicalName);
                foreach (var (column, declared) in schema.Columns)
                {
                    var target = $"{name}.{column}";
                    if (!existing.TryGetValue(column, out var actual))
                    {
                        changes.Add(new Change(ChangeKind.AddColumn, target, $"add column {declared.DisplayName} ({column}, {Describe(declared)})"));
                        continue;
                    }
                    if (Differs(column, declared, actual) is { } why)
                    {
                        if (column == schema.PrimaryName || column == table.PrimaryName)
                            refusals.Add($"{column} is the primary name column, which can't be recreated: {why}; {DeleteByHand(name)}");
                        else
                            changes.Add(new Change(ChangeKind.RecreateColumn, target, $"change {declared.DisplayName} ({column}) {why}"));
                    }
                    else if (actual.DisplayName != null && actual.DisplayName != declared.DisplayName)
                        changes.Add(new Change(ChangeKind.UpdateColumn, target, $"rename {column} from {actual.DisplayName} to {declared.DisplayName}"));
                }
                foreach (var actual in table.Columns.Where(c => !schema.Columns.ContainsKey(c.LogicalName) && c.LogicalName != table.PrimaryName))
                    changes.Add(new Change(ChangeKind.RemoveColumn, $"{name}.{actual.LogicalName}", $"remove column {actual.DisplayName ?? actual.LogicalName} ({actual.LogicalName})"));
            }
        }

        foreach (var (api, declared) in schema.CustomApis)
        {
            if (apis.GetValueOrDefault(api) is not { } actual)
            {
                changes.Add(new Change(ChangeKind.AddApi, api, $"add action {declared.DisplayName} ({api})"));
                continue;
            }
            var differences = new List<string>();
            if (customApiPluginTypeId != null && !SameId(actual.PluginTypeId, customApiPluginTypeId))
                differences.Add($"it runs plug-in type {actual.PluginTypeId}, not BrokerForge.Plugin.CustomApiPlugin {customApiPluginTypeId}");
            // The generator creates unbound actions only (HLD D-19).
            if (actual.BindingType != 0)
                differences.Add($"bindingtype is {actual.BindingType} in Dataverse; the schema's APIs are unbound (0)");
            if (actual.IsFunction)
                differences.Add("isfunction is true in Dataverse; the schema's APIs are actions (false)");
            var wantedInputs = declared.Inputs.ToDictionary(i => i.Key, i => (ParamTypeCodes[i.Value.Type], !i.Value.Required));
            differences.AddRange(CompareSets("input", wantedInputs, actual.Inputs, v => $"type {v.Item1}{(v.Item2 ? ", optional" : ", required")}"));
            var wantedOutputs = declared.Outputs.ToDictionary(o => o.Key, o => ParamTypeCodes[o.Value]);
            differences.AddRange(CompareSets("output", wantedOutputs, actual.Outputs, v => $"type {v}"));
            if (differences.Count > 0)
                changes.Add(new Change(ChangeKind.RecreateApi, api, $"change action {declared.DisplayName} ({api}): {string.Join("; ", differences)}"));
            else if (actual.DisplayName != null && actual.DisplayName != declared.DisplayName)
                changes.Add(new Change(ChangeKind.UpdateApi, api, $"rename action {api} from {actual.DisplayName} to {declared.DisplayName}"));
        }
        foreach (var (api, actual) in apis.Where(a => a.Value != null && !schema.CustomApis.ContainsKey(a.Key)))
            changes.Add(new Change(ChangeKind.RemoveApi, api, $"remove action {actual!.DisplayName ?? api} ({api})"));

        var ordered = changes.OrderBy(c => c.Kind).ThenBy(c => c.Target, StringComparer.Ordinal).ToList();
        return new ChangeList(ordered, refusals, ChangeList.FingerprintOf(ordered));
    }

    /// <summary>The column as the schema describes it: type, then length, precision or options.</summary>
    public static string Describe(ColumnSchema c) => Describe(c.Type, EffectiveMaxLength(c), c.Precision, c.Options?.Values.ToList());

    private static string Describe(string type, int? maxLength, int? precision, IReadOnlyList<int>? options) => type switch
    {
        "string" or "memo" when maxLength != null => $"{type} {maxLength}",
        "decimal" when precision != null => $"decimal {precision}",
        "optionset" when options != null => $"optionset ({string.Join(", ", options.Order())})",
        _ => type,
    };

    /// <summary>A memo the schema gives no length is created at 100000 (TableGenerator.ColumnPayload).</summary>
    private static int? EffectiveMaxLength(ColumnSchema c) => c.Type == "memo" ? c.MaxLength ?? 100000 : c.MaxLength;

    /// <summary>Why <paramref name="actual"/> has to be recreated to match <paramref name="declared"/>, or null when it matches.</summary>
    private static string? Differs(string column, ColumnSchema declared, ExistingColumn actual)
    {
        var from = Describe(actual.Type, actual.MaxLength, actual.Precision, actual.Options);
        var to = Describe(declared);
        var sameShape = actual.Type == declared.Type
            && (declared.Type is not ("string" or "memo") || actual.MaxLength == EffectiveMaxLength(declared))
            && (declared.Type != "decimal" || actual.Precision == declared.Precision)
            && (declared.Type != "optionset" || (declared.Options?.Values.Order() ?? Enumerable.Empty<int>()).SequenceEqual(actual.Options?.Order() ?? Enumerable.Empty<int>()));
        if (!sameShape)
            return $"from {from} to {to}";
        return actual.ExternalName != column ? $"because its ExternalName is {actual.ExternalName ?? "(none)"}, not {column}" : null;
    }

    private static IEnumerable<string> CompareSets<T>(string what, IReadOnlyDictionary<string, T> wanted, IReadOnlyDictionary<string, T> actual, Func<T, string> describe)
    {
        foreach (var (name, value) in wanted)
            if (!actual.TryGetValue(name, out var existing))
                yield return $"{what} {name} is in the schema but not in Dataverse";
            else if (!EqualityComparer<T>.Default.Equals(existing, value))
                yield return $"{what} {name} is {describe(existing)} in Dataverse but {describe(value)} in the schema";
        foreach (var name in actual.Keys.Where(n => !wanted.ContainsKey(n)))
            yield return $"{what} {name} is in Dataverse but not in the schema";
    }

    /// <summary>The generator never deletes or re-points a table, so a table the platform created wrongly has to go by hand.</summary>
    public static string DeleteByHand(string table) =>
        $"delete {table} by hand (Power Apps, or DELETE EntityDefinitions(LogicalName='{table}')) and run generate again";

    public static bool SameId(string? a, string? b) => a != null && b != null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
