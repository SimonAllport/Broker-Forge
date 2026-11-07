using System.Text.Json.Nodes;
using BrokerForge.Schema;

namespace BrokerForge.Generator;

/// <summary>Reads a virtual table's metadata and builds the Web API payloads for the table and its columns.</summary>
public static class TableGenerator
{
    // Metadata the platform requires for a virtual table to be accepted with its provider (phase 0 findings section 1).
    private static JsonObject Managed(bool value, string name) => new() { ["Value"] = value, ["CanBeChanged"] = false, ["ManagedPropertyLogicalName"] = name };

    public static JsonObject Label(string text) =>
        new() { ["LocalizedLabels"] = new JsonArray(new JsonObject { ["Label"] = text, ["LanguageCode"] = 1033 }) };

    public static async Task<ExistingTable?> ReadAsync(DataverseClient client, SourceSchema schema, CancellationToken ct)
    {
        var name = schema.Table!;
        var entity = $"EntityDefinitions(LogicalName='{name}')";
        var table = await client.GetAsync($"{entity}?$select=TableType,DataProviderId,DataSourceId,ExternalName,PrimaryNameAttribute,DisplayName,DisplayCollectionName", ct);
        if (table == null)
            return null;

        var prefix = name[..(name.IndexOf('_') + 1)];
        // AttributeOf marks attributes the platform derives from another column (ext_statusname for the ext_status picklist).
        var attributes = (await client.ListAsync($"{entity}/Attributes?$select=LogicalName,AttributeType,IsCustomAttribute,ExternalName,AttributeOf,DisplayName,MetadataId", ct))
            .Where(a => (bool?)a["IsCustomAttribute"] == true && ((string)a["LogicalName"]!).StartsWith(prefix, StringComparison.Ordinal)
                && (string)a["LogicalName"]! != schema.PrimaryKey && string.IsNullOrEmpty((string?)a["AttributeOf"]))
            .ToList();
        async Task<Dictionary<string, JsonObject>> Cast(string type, string select) =>
            (await client.ListAsync($"{entity}/Attributes/Microsoft.Dynamics.CRM.{type}AttributeMetadata?{select}", ct)).ToDictionary(a => (string)a["LogicalName"]!);
        var strings = await Cast("String", "$select=LogicalName,MaxLength");
        var memos = await Cast("Memo", "$select=LogicalName,MaxLength");
        var decimals = await Cast("Decimal", "$select=LogicalName,Precision");
        var dates = await Cast("DateTime", "$select=LogicalName,Format");
        var picklists = await Cast("Picklist", "$select=LogicalName&$expand=OptionSet($select=Options)");

        var columns = attributes.Select(a =>
        {
            var logical = (string)a["LogicalName"]!;
            var attributeType = (string)a["AttributeType"]!;
            var type = attributeType switch
            {
                "String" => "string",
                "Memo" => "memo",
                "Integer" => "int",
                "Decimal" => "decimal",
                "Boolean" => "bool",
                "DateTime" => (string?)dates.GetValueOrDefault(logical)?["Format"] == "DateOnly" ? "date" : "datetime",
                "Picklist" => "optionset",
                _ => attributeType,
            };
            int? maxLength = (int?)(strings.GetValueOrDefault(logical) ?? memos.GetValueOrDefault(logical))?["MaxLength"];
            var options = picklists.GetValueOrDefault(logical)?["OptionSet"]?["Options"]?.AsArray().Select(o => (int)o!["Value"]!).ToList();
            return new ExistingColumn(logical, type, (string?)a["ExternalName"], maxLength, (int?)decimals.GetValueOrDefault(logical)?["Precision"], options, LabelText(a["DisplayName"]), (string?)a["MetadataId"]);
        }).ToList();
        return new ExistingTable((string?)table["TableType"], (string?)table["DataProviderId"], (string?)table["DataSourceId"], (string?)table["ExternalName"], columns,
            (string?)table["PrimaryNameAttribute"], LabelText(table["DisplayName"]), LabelText(table["DisplayCollectionName"]));
    }

    /// <summary>A label's text: the user's localised label, else the first one; null when there is none.</summary>
    public static string? LabelText(JsonNode? label) =>
        (string?)label?["UserLocalizedLabel"]?["Label"] ?? (string?)label?["LocalizedLabels"]?.AsArray().FirstOrDefault()?["Label"];

    /// <summary>Changes display labels in place: the metadata is read whole, relabelled and PUT back with MSCRM.MergeLabels.</summary>
    public static async Task RelabelAsync(DataverseClient client, string path, IReadOnlyDictionary<string, string> labels, string? solution, CancellationToken ct)
    {
        var metadata = client.DryRun ? new JsonObject() : await client.GetAsync(path, ct) ?? throw new DataverseException($"GET {path} returned 404.");
        foreach (var key in metadata.Select(p => p.Key).Where(k => k.StartsWith("@odata.", StringComparison.Ordinal) && k != "@odata.type").ToList())
            metadata.Remove(key);
        foreach (var (key, text) in labels)
            metadata[key] = Label(text);
        await client.WriteAsync(HttpMethod.Put, path, metadata, solution, ct, mergeLabels: true);
    }

    public static JsonObject TablePayload(SourceSchema schema, string providerId, string dataSourceId) => new()
    {
        ["@odata.type"] = "Microsoft.Dynamics.CRM.EntityMetadata",
        ["SchemaName"] = schema.Table,
        ["DisplayName"] = Label(schema.DisplayName!),
        ["DisplayCollectionName"] = Label(schema.DisplayCollectionName!),
        ["DataProviderId"] = providerId,
        ["DataSourceId"] = dataSourceId,
        ["ExternalName"] = schema.ExternalName,
        ["ExternalCollectionName"] = schema.ExternalName + "s",
        ["OwnershipType"] = "OrganizationOwned",
        ["HasActivities"] = false,
        ["HasNotes"] = false,
        ["IsActivity"] = false,
        ["ChangeTrackingEnabled"] = false,
        ["CanChangeTrackingBeEnabled"] = Managed(false, "canchangetrackingbeenabled"),
        ["CanCreateCharts"] = Managed(false, "cancreatecharts"),
        ["IsAuditEnabled"] = Managed(false, "canmodifyauditsettings"),
        ["IsDuplicateDetectionEnabled"] = Managed(false, "canmodifyduplicatedetectionsettings"),
        ["Attributes"] = new JsonArray(ColumnPayload(schema.PrimaryName!, schema.Columns[schema.PrimaryName!], primary: true)),
    };

    /// <summary>One column. <c>ExternalName</c> is the logical name: descriptors map by column, not by source field.</summary>
    public static JsonObject ColumnPayload(string column, ColumnSchema c, bool primary = false)
    {
        var payload = new JsonObject
        {
            ["SchemaName"] = column,
            ["DisplayName"] = Label(c.DisplayName),
            ["RequiredLevel"] = new JsonObject { ["Value"] = "None" },
            ["ExternalName"] = column,
        };
        void Type(string metadata) => payload["@odata.type"] = $"Microsoft.Dynamics.CRM.{metadata}AttributeMetadata";
        switch (c.Type)
        {
            case "string":
                Type("String");
                payload["MaxLength"] = c.MaxLength;
                payload["FormatName"] = new JsonObject { ["Value"] = "Text" };
                if (primary)
                    payload["IsPrimaryName"] = true;
                break;
            case "memo":
                Type("Memo");
                payload["MaxLength"] = c.MaxLength ?? 100000;
                payload["Format"] = "TextArea";
                break;
            case "int":
                Type("Integer");
                payload["Format"] = "None";
                payload["MinValue"] = int.MinValue;
                payload["MaxValue"] = int.MaxValue;
                break;
            case "decimal":
                // Decimal, not Money: virtual tables refuse Money columns (HLD D-15).
                Type("Decimal");
                payload["Precision"] = c.Precision;
                payload["MinValue"] = -100000000000m;
                payload["MaxValue"] = 100000000000m;
                break;
            case "bool":
                Type("Boolean");
                payload["OptionSet"] = new JsonObject
                {
                    ["@odata.type"] = "Microsoft.Dynamics.CRM.BooleanOptionSetMetadata",
                    ["TrueOption"] = new JsonObject { ["Value"] = 1, ["Label"] = Label("Yes") },
                    ["FalseOption"] = new JsonObject { ["Value"] = 0, ["Label"] = Label("No") },
                };
                break;
            case "datetime" or "date":
                Type("DateTime");
                payload["Format"] = c.Type == "date" ? "DateOnly" : "DateAndTime";
                break;
            case "optionset":
                Type("Picklist");
                payload["OptionSet"] = new JsonObject
                {
                    ["@odata.type"] = "Microsoft.Dynamics.CRM.OptionSetMetadata",
                    ["IsGlobal"] = false,
                    ["OptionSetType"] = "Picklist",
                    ["Options"] = new JsonArray(c.Options!.Select(o => (JsonNode)new JsonObject { ["Value"] = o.Value, ["Label"] = Label(Capitalise(o.Key)) }).ToArray()),
                };
                break;
            default:
                throw new ArgumentException($"column {column} has type {c.Type}, which the generator cannot create");
        }
        return payload;
    }

    private static string Capitalise(string label) => label.Length == 0 ? label : char.ToUpperInvariant(label[0]) + label[1..];
}
