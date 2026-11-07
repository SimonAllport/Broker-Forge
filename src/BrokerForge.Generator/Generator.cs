using System.Text.Json.Nodes;
using BrokerForge.Schema;

namespace BrokerForge.Generator;

/// <summary>
/// Creates and changes a source's Dataverse components from its table schema (HLD 5.3, D-36): the solution and
/// publisher, the data source record (host URL, scope and instance name; no secrets), the virtual table and its
/// columns, the main form and default view showing every column, the Custom APIs and the <c>&lt;Instance&gt; user</c>
/// role. Everything is read first. A run applies only when nothing is refused or blocked and, for any change other
/// than an add, the change list is the one confirmed.
/// </summary>
public static class Generator
{
    public const string ProviderName = "BrokerForge";
    private const string Setup = "tools/dev/setup-dataverse.sh";

    private sealed record State(string ProviderId, string? PluginTypeId, JsonObject? Publisher, JsonObject? Solution, JsonObject? DataSource,
        ExistingTable? Table, IReadOnlyDictionary<string, ExistingCustomApi?> Apis, GeneratedParts? Parts, string Prefix, GenerationPreview Preview);

    /// <summary>What <see cref="RunAsync"/> would do. Reads only.</summary>
    public static async Task<GenerationPreview> PreviewAsync(DataverseClient client, SourceSchema schema, GenerateOptions o, CancellationToken ct) =>
        (await ReadAsync(client, schema, o, ct)).Preview;

    public static async Task<GenerationResult> RunAsync(DataverseClient client, SourceSchema schema, GenerateOptions o, CancellationToken ct)
    {
        var state = await ReadAsync(client, schema, o, ct);
        var preview = state.Preview;
        if (preview.Changes.Refusals.Count > 0)
            throw new GenerationRefusedException(preview.Changes.Refusals);
        if (preview.Blocked.Count > 0)
            throw new GenerationRefusedException(preview.Blocked.Select(b => $"{b.Change.Text} is blocked: {b.Dependent} uses it; remove it from there first").ToList());
        var lines = string.Concat(preview.Changes.Changes.Select(c => "\n - " + c.Text));
        // A dry run writes nothing, so it plans every change without a fingerprint; the real run asks for one.
        if (!o.DryRun && o.ConfirmedFingerprint != null && o.ConfirmedFingerprint != preview.Changes.Fingerprint)
            throw new GenerationConfirmationException(preview,
                $"The changes are not the ones confirmed ({o.ConfirmedFingerprint}). Nothing was written. They are now ({preview.Changes.Fingerprint}):{lines}");
        if (!o.DryRun && o.ConfirmedFingerprint == null && preview.NeedsConfirmation)
            throw new GenerationConfirmationException(preview, $"These changes need confirming with fingerprint {preview.Changes.Fingerprint}. Nothing was written:{lines}");

        var step = "start";
        int? version;
        try
        {
            version = await ApplyAsync(client, schema, o, state, s => step = s, ct);
        }
        // Past the checks above, anything that stops the run (a refusal such as a privilege that doesn't exist, a network
        // error, an HttpClient timeout, a token failure) can come after writes: it is a failure naming the step and the
        // writes made, never "made no changes". Only the caller's own cancellation passes through.
        catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            throw new GenerationFailedException(step, client.Writes.ToList(), ex);
        }
        return new GenerationResult(client.Writes, version, preview.Pending);
    }

    private static async Task<State> ReadAsync(DataverseClient client, SourceSchema schema, GenerateOptions o, CancellationToken ct)
    {
        var provider = (await client.ListAsync($"entitydataproviders?$select=entitydataproviderid&$filter=name eq {DataverseClient.Literal(ProviderName)}", ct)).FirstOrDefault();
        var refusals = new List<string>();
        if (provider == null)
            refusals.Add($"the {ProviderName} data provider is not registered; run {Setup} (core) first");
        string? pluginTypeId = null;
        if (schema.CustomApis.Count > 0 || schema.Table != null)
        {
            pluginTypeId = (string?)(await client.ListAsync($"plugintypes?$select=plugintypeid&$filter=typename eq {DataverseClient.Literal(CustomApiGenerator.PluginTypeName)}", ct)).FirstOrDefault()?["plugintypeid"];
            if (pluginTypeId == null && schema.CustomApis.Count > 0)
                refusals.Add($"plug-in type {CustomApiGenerator.PluginTypeName} is not registered; run {Setup} (core) first");
        }
        if (refusals.Count > 0)
            throw new GenerationRefusedException(refusals);
        var providerId = (string)provider!["entitydataproviderid"]!;

        var publisher = (await client.ListAsync($"publishers?$select=publisherid,customizationprefix&$filter=uniquename eq {DataverseClient.Literal(o.Publisher)}", ct)).FirstOrDefault();
        var solution = (await client.ListAsync($"solutions?$select=solutionid&$filter=uniquename eq {DataverseClient.Literal(o.Solution)}", ct)).FirstOrDefault();
        var dataSource = schema.Table == null ? null
            : (await client.ListAsync($"bf_brokerdatasources?$select=bf_brokerdatasourceid,bf_hosturl,bf_scope&$filter=bf_serviceinstance eq {DataverseClient.Literal(o.Instance)}", ct)).FirstOrDefault();
        var table = schema.Table == null ? null : await TableGenerator.ReadAsync(client, schema, ct);
        var apis = new Dictionary<string, ExistingCustomApi?>(StringComparer.Ordinal);
        var names = schema.CustomApis.Keys.ToList();
        if (pluginTypeId != null)
            names.AddRange((await CustomApiGenerator.InSolutionAsync(client, (string?)solution?["solutionid"], pluginTypeId, ct)).Where(n => !schema.CustomApis.ContainsKey(n)));
        foreach (var api in names.Distinct(StringComparer.Ordinal))
            apis[api] = await CustomApiGenerator.ReadAsync(client, api, ct);

        // The schema parser holds every table, column and Custom API name to one prefix, so one name gives it.
        var prefix = (schema.Table ?? schema.CustomApis.Keys.First()).Split('_')[0];
        var plan = GenerationPlan.Diff(schema, table, apis, providerId, (string?)dataSource?["bf_brokerdatasourceid"], pluginTypeId, o.Instance);
        if (publisher != null && (string?)publisher["customizationprefix"] is { } existingPrefix && existingPrefix != prefix)
            plan = plan with { Refusals = plan.Refusals.Append($"publisher {o.Publisher} has prefix {existingPrefix}, but the schema uses {prefix}").ToList() };

        if (o.Design != null)
        {
            try
            {
                DesignPublisher.Check(o.Design.Design, o.Design.AuthJson);
            }
            catch (BrokerForge.Connections.DesignFormatException ex)
            {
                plan = plan with { Refusals = plan.Refusals.Append(ex.Message).ToList() };
            }
        }

        if (schema.Table != null && o.Messages != null
            && await RoleGenerator.ConflictAsync(client, o.Instance, o.Solution, (string?)solution?["solutionid"], ct) is { } conflict)
            plan = plan with { Refusals = plan.Refusals.Append(conflict).ToList() };

        var parts = table == null ? null : await FormsAndViews.ReadAsync(client, schema.Table!, ct);
        var blocked = new List<Blocked>();
        var generated = parts == null ? new HashSet<string>() : new HashSet<string>(new[] { parts.FormId, parts.ViewId }, StringComparer.OrdinalIgnoreCase);
        foreach (var change in plan.Changes)
        {
            var (type, id) = change.Kind switch
            {
                ChangeKind.RemoveColumn or ChangeKind.RecreateColumn => (Dependencies.Attribute, table!.Columns.Single(c => $"{schema.Table}.{c.LogicalName}" == change.Target).MetadataId),
                ChangeKind.RemoveApi or ChangeKind.RecreateApi => (Dependencies.CustomApi, apis[change.Target]!.Id),
                _ => (0, null),
            };
            if (id != null)
                blocked.AddRange((await Dependencies.ForDeleteAsync(client, type, id, generated, ct)).Select(d => new Blocked(change, d)));
        }

        var described = new List<string>();
        var pending = new List<string>();
        // Actions are an administrator's too: creating a Custom API needs prvCreateSdkMessageProcessingStep, which Dataverse
        // leaves out of the host's effective privileges whatever its role holds (portal-5 findings).
        if (!o.ApplyRole)
            pending.AddRange(plan.Changes.Where(c => c.Kind is ChangeKind.AddApi or ChangeKind.RecreateApi or ChangeKind.RemoveApi).Select(c => c.Text));
        if (schema.Table != null)
        {
            var all = FormsAndViews.Order(schema, schema.Columns.Keys);
            if (parts == null || !FormsAndViews.Matches(schema, parts, all))
                described.Add($"show every column on the main form and the default view of {schema.Table}");
            if (o.Messages != null)
            {
                var role = table == null
                    ? $"create role {RoleGenerator.RoleName(o.Instance)} with {string.Join(", ", RoleGenerator.Wanted(o.Messages))} on {schema.Table}"
                    : await RoleGenerator.DescribeAsync(client, schema.Table, o.Instance, o.Messages, (string?)solution?["solutionid"], ct);
                if (role != null)
                    (o.ApplyRole ? described : pending).Add(role);
            }
        }
        if (o.Design != null)
            described.Add($"publish the {o.Instance} design and its environment variables bf_{o.Instance}_BaseUrl and bf_{o.Instance}_Auth (the design version goes up only if it changed)");
        return new State(providerId, pluginTypeId, publisher, solution, dataSource, table, apis, parts, prefix, new GenerationPreview(plan, blocked, described, pending));
    }

    private static async Task<int?> ApplyAsync(DataverseClient client, SourceSchema schema, GenerateOptions o, State s, Action<string> at, CancellationToken ct)
    {
        var changes = s.Preview.Changes.Changes;
        IEnumerable<string> Columns(params ChangeKind[] kinds) =>
            changes.Where(c => kinds.Contains(c.Kind)).Select(c => c.Target[(c.Target.IndexOf('.') + 1)..]);

        at("publisher");
        if (s.Publisher == null)
            await client.WriteAsync(HttpMethod.Post, "publishers", new JsonObject
            {
                ["uniquename"] = o.Publisher, ["friendlyname"] = o.Publisher, ["customizationprefix"] = s.Prefix, ["customizationoptionvalueprefix"] = 10000,
            }, null, ct);
        at("solution");
        if (s.Solution == null)
        {
            var publisherId = (string?)s.Publisher?["publisherid"]
                ?? (string?)(await ListAfterWrite(client, $"publishers?$select=publisherid&$filter=uniquename eq {DataverseClient.Literal(o.Publisher)}", ct))?["publisherid"] ?? "(new publisher)";
            await client.WriteAsync(HttpMethod.Post, "solutions", new JsonObject
            {
                ["uniquename"] = o.Solution, ["friendlyname"] = o.Solution, ["version"] = "1.0.0.0", ["publisherid@odata.bind"] = $"/publishers({publisherId})",
            }, null, ct);
        }

        if (schema.Table is { } table)
        {
            var entity = $"EntityDefinitions(LogicalName='{table}')";
            at("data source record");
            var dataSourceId = await EnsureDataSource(client, s.DataSource, o, ct);
            at("data source record in the solution");
            await DataSourceInSolution(client, s.Solution, o.Solution, dataSourceId, ct);
            var created = changes.Any(c => c.Kind == ChangeKind.AddTable);
            if (created)
            {
                at("create table");
                await client.WriteAsync(HttpMethod.Post, "EntityDefinitions", TableGenerator.TablePayload(schema, s.ProviderId, dataSourceId), o.Solution, ct);
            }

            // Columns to delete come off the generated form and view first, or the platform refuses the delete.
            var deleting = Columns(ChangeKind.RemoveColumn, ChangeKind.RecreateColumn).ToHashSet(StringComparer.Ordinal);
            if (deleting.Count > 0 && s.Parts != null)
            {
                at("take columns off the form and view");
                var keep = FormsAndViews.Order(schema, s.Table!.Columns.Select(c => c.LogicalName).Where(c => schema.Columns.ContainsKey(c) && !deleting.Contains(c)));
                await FormsAndViews.ShowAsync(client, schema, s.Parts, keep, ct);
            }
            foreach (var column in deleting.Order(StringComparer.Ordinal))
            {
                at($"delete column {column}");
                await client.DeleteAsync($"{entity}/Attributes(LogicalName='{column}')", ct);
            }
            // Shown in target order, created in schema order.
            var adding = Columns(ChangeKind.AddColumn, ChangeKind.RecreateColumn).ToHashSet(StringComparer.Ordinal);
            foreach (var column in schema.Columns.Keys.Where(adding.Contains))
            {
                at($"add column {column}");
                await client.WriteAsync(HttpMethod.Post, $"{entity}/Attributes", TableGenerator.ColumnPayload(column, schema.Columns[column]), o.Solution, ct);
            }
            foreach (var column in Columns(ChangeKind.UpdateColumn))
            {
                at($"rename column {column}");
                await TableGenerator.RelabelAsync(client, $"{entity}/Attributes(LogicalName='{column}')",
                    new Dictionary<string, string> { ["DisplayName"] = schema.Columns[column].DisplayName }, o.Solution, ct);
            }
            if (changes.Any(c => c.Kind == ChangeKind.UpdateTable))
            {
                at("rename table");
                await TableGenerator.RelabelAsync(client, entity,
                    new Dictionary<string, string> { ["DisplayName"] = schema.DisplayName!, ["DisplayCollectionName"] = schema.DisplayCollectionName! }, o.Solution, ct);
            }
            if (created || adding.Count > 0 || deleting.Count > 0 || changes.Any(c => c.Kind is ChangeKind.UpdateColumn or ChangeKind.UpdateTable))
            {
                at("publish");
                await PublishAsync(client, table, ct);
            }
            if (created && !client.DryRun)
                await CheckTable(client, schema, s.ProviderId, dataSourceId, ct);

            at("show every column on the form and view");
            var all = FormsAndViews.Order(schema, schema.Columns.Keys);
            if (client.DryRun && (created || deleting.Count > 0))
            {
                await client.WriteAsync(HttpMethod.Patch, $"systemforms(main form of {table})", null, null, ct);
                await client.WriteAsync(HttpMethod.Patch, $"savedqueries(default view of {table})", null, null, ct);
            }
            else
            {
                var parts = created || deleting.Count > 0 || adding.Count > 0 ? await FormsAndViews.ReadAsync(client, table, ct) : s.Parts;
                if (parts == null)
                    throw new DataverseException($"{table} has no main form or default public view to show its columns on");
                await FormsAndViews.ShowAsync(client, schema, parts, all, ct);
            }
        }

        foreach (var change in changes.Where(c => o.ApplyRole && c.Kind is ChangeKind.RemoveApi or ChangeKind.RecreateApi))
        {
            at($"delete action {change.Target}");
            await client.DeleteAsync($"customapis({s.Apis[change.Target]!.Id})", ct);
        }
        foreach (var change in changes.Where(c => o.ApplyRole && c.Kind is ChangeKind.AddApi or ChangeKind.RecreateApi))
        {
            at($"create action {change.Target}");
            await client.WriteAsync(HttpMethod.Post, "customapis", CustomApiGenerator.Payload(change.Target, schema.CustomApis[change.Target], s.PluginTypeId!), o.Solution, ct);
        }
        foreach (var change in changes.Where(c => c.Kind == ChangeKind.UpdateApi))
        {
            at($"rename action {change.Target}");
            var name = schema.CustomApis[change.Target].DisplayName;
            await client.WriteAsync(HttpMethod.Patch, $"customapis({s.Apis[change.Target]!.Id})", new JsonObject { ["displayname"] = name, ["description"] = name }, null, ct);
        }

        if (schema.Table != null && o.Messages != null && o.ApplyRole)
        {
            at("role");
            if (client.DryRun && changes.Any(c => c.Kind == ChangeKind.AddTable))
                await client.WriteAsync(HttpMethod.Post, $"roles ({RoleGenerator.RoleName(o.Instance)})", null, o.Solution, ct);
            else
                await RoleGenerator.ApplyAsync(client, schema.Table, o.Instance, o.Messages, o.Solution, ct);
        }

        if (o.Design == null)
            return null;
        at("design");
        return await DesignPublisher.PublishAsync(client, o.Design.Design, s.Prefix, o.Solution, o.Design.BaseUrl, o.Design.AuthJson, ct, schema.Table);
    }

    public static Task PublishAsync(DataverseClient client, string table, CancellationToken ct) =>
        client.WriteAsync(HttpMethod.Post, "PublishXml", new JsonObject
        {
            ["ParameterXml"] = $"<importexportxml><entities><entity>{table}</entity></entities></importexportxml>",
        }, null, ct);

    private static async Task<JsonObject?> ListAfterWrite(DataverseClient client, string path, CancellationToken ct) =>
        client.DryRun ? null : (await client.ListAsync(path, ct)).FirstOrDefault();

    /// <summary>The instance's data source record. Host URL and scope follow the options: they are environment values, not metadata.</summary>
    private static async Task<string> EnsureDataSource(DataverseClient client, JsonObject? existing, GenerateOptions o, CancellationToken ct)
    {
        var values = new JsonObject { ["bf_hosturl"] = o.HostUrl, ["bf_scope"] = o.Scope };
        if (existing != null)
        {
            var id = (string)existing["bf_brokerdatasourceid"]!;
            if ((string?)existing["bf_hosturl"] != o.HostUrl || (string?)existing["bf_scope"] != o.Scope)
                await client.WriteAsync(HttpMethod.Patch, $"bf_brokerdatasources({id})", values, null, ct);
            return id;
        }
        values["bf_name"] = $"{o.Instance} host";
        values["bf_serviceinstance"] = o.Instance;
        var created = await client.WriteAsync(HttpMethod.Post, "bf_brokerdatasources", values, null, ct);
        return (string?)created?["bf_brokerdatasourceid"] ?? "(new bf_brokerdatasource)";
    }

    /// <summary>A data source record's solution component type: what an import names when the record is missing (plan 5).</summary>
    public const int DataSourceComponent = 183;

    /// <summary>
    /// The data source record goes in the source's solution, or another environment refuses the solution as a missing
    /// dependency. Its host URL and scope come with it; the host is the same for every environment it serves.
    /// </summary>
    private static async Task DataSourceInSolution(DataverseClient client, JsonObject? existingSolution, string solution, string dataSourceId, CancellationToken ct)
    {
        var solutionId = (string?)existingSolution?["solutionid"]
            ?? (string?)(await ListAfterWrite(client, $"solutions?$select=solutionid&$filter=uniquename eq {DataverseClient.Literal(solution)}", ct))?["solutionid"];
        if (solutionId != null)
        {
            var present = await client.ListAsync($"solutioncomponents?$select=objectid&$filter=_solutionid_value eq {solutionId} and componenttype eq {DataSourceComponent}", ct);
            if (present.Any(c => GenerationPlan.SameId((string?)c["objectid"], dataSourceId)))
                return;
        }
        await client.WriteAsync(HttpMethod.Post, "AddSolutionComponent", new JsonObject
        {
            ["ComponentId"] = dataSourceId, ["ComponentType"] = DataSourceComponent, ["SolutionUniqueName"] = solution,
            ["AddRequiredComponents"] = false, ["DoNotIncludeSubcomponents"] = false,
        }, null, ct);
    }

    /// <summary>The platform drops DataProviderId on a create it does not accept as a virtual table (HLD 15), so read it back.</summary>
    private static async Task CheckTable(DataverseClient client, SourceSchema schema, string providerId, string dataSourceId, CancellationToken ct)
    {
        var table = await client.GetAsync($"EntityDefinitions(LogicalName='{schema.Table}')?$select=TableType,DataProviderId,DataSourceId", ct);
        var problems = new List<string>();
        if ((string?)table?["TableType"] != "Virtual")
            problems.Add($"{schema.Table} TableType is {(string?)table?["TableType"] ?? "(missing)"} after create, not Virtual (HLD 15)");
        if (!GenerationPlan.SameId((string?)table?["DataProviderId"], providerId))
            problems.Add($"{schema.Table} DataProviderId is {(string?)table?["DataProviderId"] ?? "(none)"} after create: the platform dropped the provider (HLD 15); {GenerationPlan.DeleteByHand(schema.Table!)}");
        if (!GenerationPlan.SameId((string?)table?["DataSourceId"], dataSourceId))
            problems.Add($"{schema.Table} DataSourceId is {(string?)table?["DataSourceId"] ?? "(none)"} after create, expected {dataSourceId} (HLD 15)");
        if (problems.Count > 0)
            throw new GenerationRefusedException(problems);
    }
}
