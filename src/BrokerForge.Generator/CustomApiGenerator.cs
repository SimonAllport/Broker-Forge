using System.Text.Json.Nodes;
using BrokerForge.Schema;

namespace BrokerForge.Generator;

/// <summary>Reads and creates unbound Custom APIs (HLD D-19) bound to <c>BrokerForge.Plugin.CustomApiPlugin</c>.</summary>
public static class CustomApiGenerator
{
    public const string PluginTypeName = "BrokerForge.Plugin.CustomApiPlugin";

    public static async Task<ExistingCustomApi?> ReadAsync(DataverseClient client, string uniqueName, CancellationToken ct)
    {
        var rows = await client.ListAsync(
            $"customapis?$select=customapiid,uniquename,displayname,bindingtype,isfunction,_plugintypeid_value&$filter=uniquename eq {DataverseClient.Literal(uniqueName)}" +
            "&$expand=CustomAPIRequestParameters($select=uniquename,type,isoptional),CustomAPIResponseProperties($select=uniquename,type)", ct);
        if (rows.Count == 0)
            return null;
        var api = rows[0];
        return new ExistingCustomApi(
            (string?)api["_plugintypeid_value"] ?? "(none)",
            (int?)api["bindingtype"] ?? -1,
            (bool?)api["isfunction"] ?? false,
            api["CustomAPIRequestParameters"]!.AsArray().ToDictionary(p => (string)p!["uniquename"]!, p => ((int)p!["type"]!, (bool)p["isoptional"]!)),
            api["CustomAPIResponseProperties"]!.AsArray().ToDictionary(p => (string)p!["uniquename"]!, p => (int)p!["type"]!),
            (string?)api["displayname"], (string?)api["customapiid"]);
    }

    /// <summary>
    /// The unique names of the Custom APIs in <paramref name="solution"/> that run BrokerForge's plug-in type. Scoped to
    /// the source's solution: another source may use the same prefix, and its actions are not this source's to remove.
    /// </summary>
    public static async Task<IReadOnlyList<string>> InSolutionAsync(DataverseClient client, string? solutionId, string pluginTypeId, CancellationToken ct)
    {
        if (solutionId == null)
            return Array.Empty<string>();
        var ids = (await client.ListAsync($"solutioncomponents?$select=objectid&$filter=_solutionid_value eq {solutionId} and componenttype eq {Dependencies.CustomApi}", ct))
            .Select(c => (string)c["objectid"]!).ToList();
        var names = new List<string>();
        foreach (var id in ids)
            if (await client.GetAsync($"customapis({id})?$select=uniquename,_plugintypeid_value", ct) is { } api && GenerationPlan.SameId((string?)api["_plugintypeid_value"], pluginTypeId))
                names.Add((string)api["uniquename"]!);
        return names;
    }

    /// <summary>The Custom API with its parameters and response properties, created in one deep insert.</summary>
    public static JsonObject Payload(string uniqueName, CustomApiSchema api, string pluginTypeId) => new()
    {
        ["uniquename"] = uniqueName,
        ["name"] = uniqueName,
        ["displayname"] = api.DisplayName,
        ["description"] = api.DisplayName,
        ["bindingtype"] = 0,
        ["isfunction"] = false,
        ["isprivate"] = false,
        ["allowedcustomprocessingsteptype"] = 0,
        // The navigation property is PluginTypeId; Web API binding names are case-sensitive (phase 2 findings).
        ["PluginTypeId@odata.bind"] = $"/plugintypes({pluginTypeId})",
        ["CustomAPIRequestParameters"] = new JsonArray(api.Inputs.Select(i => (JsonNode)new JsonObject
        {
            ["uniquename"] = i.Key, ["name"] = $"{uniqueName}.{i.Key}", ["displayname"] = i.Key, ["description"] = i.Key,
            ["type"] = GenerationPlan.ParamTypeCodes[i.Value.Type], ["isoptional"] = !i.Value.Required,
        }).ToArray()),
        ["CustomAPIResponseProperties"] = new JsonArray(api.Outputs.Select(o => (JsonNode)new JsonObject
        {
            ["uniquename"] = o.Key, ["name"] = $"{uniqueName}.{o.Key}", ["displayname"] = o.Key, ["description"] = o.Key,
            ["type"] = GenerationPlan.ParamTypeCodes[o.Value],
        }).ToArray()),
    };
}
