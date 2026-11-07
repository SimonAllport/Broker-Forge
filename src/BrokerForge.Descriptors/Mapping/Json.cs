using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Path;

namespace BrokerForge.Descriptors.Mapping;

/// <summary>JSONPath (RFC 9535) helpers. A JSON null is one match whose value is null; a missing path is no match.</summary>
internal static class Json
{
    private static readonly ConcurrentDictionary<string, JsonPath> Paths = new();

    public static JsonNode? Parse(byte[] body, Func<string, Exception> fail)
    {
        try
        {
            return body.Length == 0 ? throw fail("the response body is empty") : JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            throw fail("the response body is not JSON");
        }
    }

    public static IReadOnlyList<JsonNode?> Select(JsonNode? root, string path) =>
        Paths.GetOrAdd(path, p => JsonPath.Parse(p)).Evaluate(root).Matches.Select(m => m.Value).ToList();

    /// <summary>The scalar's text: strings unquoted, numbers and booleans as written.</summary>
    public static string? ScalarText(JsonNode? node) => node switch
    {
        null => null,
        JsonValue v when v.GetValueKind() == JsonValueKind.String => v.GetValue<string>(),
        JsonValue v => v.ToJsonString(),
        _ => null,
    };
}
