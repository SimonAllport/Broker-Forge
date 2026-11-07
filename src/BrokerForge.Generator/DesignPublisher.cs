using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BrokerForge.Connections;

namespace BrokerForge.Generator;

/// <summary>
/// Writes a connection design's solution parts (spec 7, docs/connection-design-format.md): the web resource
/// <c>&lt;prefix&gt;_/brokerforge/&lt;Instance&gt;/design.xml</c> and the definitions of <c>bf_&lt;Instance&gt;_BaseUrl</c> and
/// <c>bf_&lt;Instance&gt;_Auth</c>, all in the source's solution. The two values are this environment's own: they are
/// written without the solution header, so they never travel and an imported connection says it needs its URL instead
/// of calling this environment's source (spec 4).
/// </summary>
public static partial class DesignPublisher
{
    /// <summary>The <c>bf_&lt;Instance&gt;_Auth</c> keys an environment may set (format doc, per-environment values).</summary>
    public static readonly IReadOnlySet<string> AuthOverrideKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "staticKind", "headerName", "queryParam", "username", "scope", "ssoKind",
    };

    private const int StringType = 100000000;
    private const int JsonType = 100000003;

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9]{0,63}$")]
    private static partial Regex InstanceName();

    public static string WebResourceName(string prefix, string instance) => $"{prefix}_/brokerforge/{instance}/design.xml";

    /// <summary>
    /// Publishes the design and returns the version written: one more than the published design, or 1 when there is none.
    /// A design equal to the published one apart from its version keeps that version and is not written again.
    /// A new version publishes <paramref name="table"/> with it: a read of the table's metadata returns unpublished names,
    /// so a rename whose publish was lost looks done, but that run never reached the design, which is written last
    /// (HLD 15).
    /// </summary>
    public static async Task<int> PublishAsync(DataverseClient client, ConnectionDesign design, string prefix, string solution, string? baseUrl, string? authJson, CancellationToken ct,
        string? table = null)
    {
        Check(design, authJson);

        var name = WebResourceName(prefix, design.Instance);
        var existing = (await client.ListAsync($"webresourceset?$select=webresourceid,content&$filter=name eq {DataverseClient.Literal(name)}", ct)).FirstOrDefault();
        var published = existing?["content"] is JsonValue c && c.GetValue<string>() is { Length: > 0 } text
            ? DesignSerializer.Parse(DesignCarrier.Unwrap(Encoding.UTF8.GetString(Convert.FromBase64String(text)), name), name)
            : null;

        int version;
        if (published != null && DesignSerializer.Serialize(published with { Version = 0 }) == DesignSerializer.Serialize(design with { Version = 0 }))
            version = published.Version;
        else
        {
            version = (published?.Version ?? 0) + 1;
            var content = Convert.ToBase64String(Encoding.UTF8.GetBytes(DesignCarrier.Wrap(DesignSerializer.Serialize(design with { Version = version }))));
            string? id;
            if (existing != null)
            {
                id = (string)existing["webresourceid"]!;
                await client.WriteAsync(HttpMethod.Patch, $"webresourceset({id})", new JsonObject { ["content"] = content }, null, ct);
            }
            else
            {
                var created = await client.WriteAsync(HttpMethod.Post, "webresourceset", new JsonObject
                {
                    ["name"] = name, ["displayname"] = $"BrokerForge design {design.Instance}", ["webresourcetype"] = 4, ["content"] = content,
                }, solution, ct);
                id = (string?)created?["webresourceid"];
            }
            // An update stays invisible to reads until it is published (portal carrier findings).
            if (id != null)
                await client.WriteAsync(HttpMethod.Post, "PublishXml", new JsonObject
                {
                    ["ParameterXml"] = $"<importexportxml>{(table == null ? "" : $"<entities><entity>{table}</entity></entities>")}<webresources><webresource>{{{id}}}</webresource></webresources></importexportxml>",
                }, null, ct);
        }

        var urlId = await EnsureDefinitionAsync(client, $"bf_{design.Instance}_BaseUrl", $"BrokerForge {design.Instance} base URL", StringType, solution, ct);
        var authId = await EnsureDefinitionAsync(client, $"bf_{design.Instance}_Auth", $"BrokerForge {design.Instance} auth overrides", JsonType, solution, ct);
        if (baseUrl != null)
            await SetValueAsync(client, urlId, $"bf_{design.Instance}_BaseUrl", baseUrl, ct);
        if (authJson != null)
            await SetValueAsync(client, authId, $"bf_{design.Instance}_Auth", authJson, ct);
        return version;
    }

    /// <summary>What <see cref="PublishAsync"/> refuses, checked before any write: the instance name, and the overrides.</summary>
    /// <exception cref="DesignFormatException">Naming the instance or the override key, never a value.</exception>
    public static void Check(ConnectionDesign design, string? authJson)
    {
        if (!InstanceName().IsMatch(design.Instance))
            throw new DesignFormatException($"instance name `{design.Instance}` must be a letter followed by up to 63 letters or digits");
        CheckOverrides(authJson);
    }

    /// <summary>An object of string values with documented keys only. The message names the key, never a value.</summary>
    private static void CheckOverrides(string? authJson)
    {
        if (authJson == null)
            return;
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(authJson);
        }
        catch (JsonException)
        {
            throw new DesignFormatException("the auth overrides are not valid JSON");
        }
        if (node is not JsonObject o)
            throw new DesignFormatException("the auth overrides must be a JSON object");
        foreach (var (key, value) in o)
        {
            if (!AuthOverrideKeys.Contains(key))
                throw new DesignFormatException($"auth override `{key}` is not one of {string.Join(", ", AuthOverrideKeys)}");
            if (value is not JsonValue v || v.GetValueKind() != JsonValueKind.String)
                throw new DesignFormatException($"auth override `{key}` must be a string");
        }
    }

    private static async Task<string?> EnsureDefinitionAsync(DataverseClient client, string schema, string display, int type, string solution, CancellationToken ct)
    {
        var found = (await client.ListAsync($"environmentvariabledefinitions?$select=environmentvariabledefinitionid&$filter=schemaname eq {DataverseClient.Literal(schema)}", ct)).FirstOrDefault();
        if (found != null)
            return (string)found["environmentvariabledefinitionid"]!;
        var created = await client.WriteAsync(HttpMethod.Post, "environmentvariabledefinitions",
            new JsonObject { ["schemaname"] = schema, ["displayname"] = display, ["type"] = type }, solution, ct);
        return (string?)created?["environmentvariabledefinitionid"];
    }

    private static async Task SetValueAsync(DataverseClient client, string? definitionId, string schema, string value, CancellationToken ct)
    {
        if (definitionId == null)
        {
            await client.WriteAsync(HttpMethod.Post, "environmentvariablevalues", null, null, ct);
            return;
        }
        var current = (await client.ListAsync($"environmentvariablevalues?$select=environmentvariablevalueid,value&$filter=_environmentvariabledefinitionid_value eq {definitionId}", ct)).FirstOrDefault();
        if (current == null)
            await client.WriteAsync(HttpMethod.Post, "environmentvariablevalues", new JsonObject
            {
                ["schemaname"] = schema, ["value"] = value, ["EnvironmentVariableDefinitionId@odata.bind"] = $"/environmentvariabledefinitions({definitionId})",
            }, null, ct);
        else if ((string?)current["value"] != value)
            await client.WriteAsync(HttpMethod.Patch, $"environmentvariablevalues({(string)current["environmentvariablevalueid"]!})", new JsonObject { ["value"] = value }, null, ct);
    }
}
