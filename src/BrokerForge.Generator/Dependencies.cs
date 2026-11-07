namespace BrokerForge.Generator;

/// <summary>What else uses a component the generator would delete (generator spike section 3).</summary>
public static class Dependencies
{
    public const int Attribute = 2;
    public const int CustomApi = 10038;

    /// <summary>A dependent the platform deletes with the component, so it never stops the delete.</summary>
    private const int SolutionInternal = 1;

    private static readonly IReadOnlyDictionary<int, string> BuiltIn = new Dictionary<int, string>
    {
        [1] = "table", [2] = "column", [9] = "choice", [10] = "relationship", [20] = "role", [59] = "chart", [61] = "web resource",
        [62] = "site map", [90] = "plug-in type", [92] = "plug-in step", [300] = "canvas app", [380] = "environment variable",
    };

    /// <summary>
    /// The components that would stop a delete, named for the admin, for example <c>view "Open orders" (id)</c>.
    /// <paramref name="generated"/> holds the IDs of BrokerForge's own parts, which the generator updates first.
    /// </summary>
    public static async Task<IReadOnlyList<string>> ForDeleteAsync(DataverseClient client, int componentType, string objectId, IReadOnlySet<string> generated, CancellationToken ct)
    {
        var rows = await client.ListAsync($"RetrieveDependenciesForDelete(ObjectId={objectId},ComponentType={componentType})", ct);
        var names = new List<string>();
        foreach (var row in rows.Where(r => (int?)r["dependencytype"] != SolutionInternal))
        {
            var id = (string?)row["dependentcomponentobjectid"] ?? "(unknown)";
            if (generated.Contains(id, StringComparer.OrdinalIgnoreCase))
                continue;
            names.Add(await NameAsync(client, (int?)row["dependentcomponenttype"] ?? -1, id, ct));
        }
        return names;
    }

    private static async Task<string> NameAsync(DataverseClient client, int type, string id, CancellationToken ct)
    {
        var (set, field, what) = type switch
        {
            60 => ("systemforms", "name", "form"),
            26 => ("savedqueries", "name", "view"),
            29 => ("workflows", "name", "process or flow"),
            80 => ("appmodules", "name", "app"),
            _ => (null, null, BuiltIn.GetValueOrDefault(type) ?? await DefinitionAsync(client, type, ct)),
        };
        var name = set == null ? null : (string?)(await client.GetAsync($"{set}({id})?$select={field}", ct))?[field!];
        return name == null ? $"{what} ({id})" : $"{what} \"{name}\" ({id})";
    }

    /// <summary>Types from 10000 (Custom API, most app components) are named in solutioncomponentdefinitions.</summary>
    private static async Task<string> DefinitionAsync(DataverseClient client, int type, CancellationToken ct) =>
        (string?)(await client.ListAsync($"solutioncomponentdefinitions?$select=name&$filter=solutioncomponenttype eq {type}", ct)).FirstOrDefault()?["name"]
        ?? $"component of type {type}";
}
