using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BrokerForge.Connections;

public static class DesignSerializer
{
    private static readonly string[] SecretKeys = ["secret", "password", "clientSecret", "apiKeyValue", "value"];

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Relaxed escaping keeps YAML and OpenAPI text readable in the saved document.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Camelcase, indented, keys in declaration order, dictionaries ordinally sorted, no CDATA-ending sequence.</summary>
    public static string Serialize(ConnectionDesign d)
    {
        var sorted = d with
        {
            Answers = Sort(d.Answers),
            Descriptors = Sort(d.Descriptors),
            ObjectMap = d.ObjectMap is null ? null : d.ObjectMap with { Operations = Sort(d.ObjectMap.Operations) },
        };
        var json = JsonSerializer.Serialize(sorted, Options).Replace("\r\n", "\n");
        // "]]>" can only occur inside a JSON string; > decodes to the same text and keeps the CDATA wrap safe.
        return json.Replace("]]>", "]]\\u003E");
    }

    private static SortedDictionary<string, string> Sort(IReadOnlyDictionary<string, string> d) =>
        new(d.ToDictionary(p => p.Key, p => p.Value), StringComparer.Ordinal);

    public static ConnectionDesign Parse(string json, string source)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException e) { throw new DesignFormatException($"{source}: $: not valid JSON ({e.Message})"); }
        using (doc)
        {
            var r = new Reader(source);
            var root = r.Object(doc.RootElement, "$");
            r.Keys(root, "$", ["formatVersion", "instance", "version", "brokerType", "openApi", "answers", "auth", "objectMap", "schema", "descriptors"]);

            // Secret scan runs before anything else so no later error can carry a value.
            if (root.TryGetValue("auth", out var authEl) && authEl.ValueKind == JsonValueKind.Object)
                foreach (var prop in authEl.EnumerateObject())
                    if (SecretKeys.Contains(prop.Name, StringComparer.OrdinalIgnoreCase))
                        throw r.Fail($"$.auth.{prop.Name}", "looks like a secret value; a design holds secret names only");

            var formatVersion = r.Int(root, "$", "formatVersion");
            if (formatVersion != 1)
                throw r.Fail("$.formatVersion", $"unsupported format version {formatVersion}, expected 1");

            return new ConnectionDesign(
                formatVersion,
                r.Str(root, "$", "instance"),
                r.Int(root, "$", "version"),
                r.Str(root, "$", "brokerType"),
                r.Str(root, "$", "openApi"),
                r.Map(root, "$", "answers"),
                ParseAuth(r, r.Req(root, "$", "auth")),
                root.TryGetValue("objectMap", out var om) ? ParseObjectMap(r, om) : null,
                r.Str(root, "$", "schema"),
                r.Map(root, "$", "descriptors"));
        }
    }

    private static DesignAuth ParseAuth(Reader r, JsonElement el)
    {
        const string p = "$.auth";
        var o = r.Object(el, p);
        // Secret check first so the message never carries the value.
        foreach (var k in o.Keys)
            if (SecretKeys.Contains(k, StringComparer.OrdinalIgnoreCase))
                throw r.Fail($"{p}.{k}", "looks like a secret value; a design holds secret names only");
        r.Keys(o, p, ["mode", "staticKind", "headerName", "queryParam", "username", "scope", "ssoKind", "asyncContext", "oauth", "secretNames"]);
        return new DesignAuth(
            r.Str(o, p, "mode"),
            r.OptStr(o, p, "staticKind"), r.OptStr(o, p, "headerName"), r.OptStr(o, p, "queryParam"),
            r.OptStr(o, p, "username"), r.OptStr(o, p, "scope"), r.OptStr(o, p, "ssoKind"), r.OptStr(o, p, "asyncContext"),
            o.TryGetValue("oauth", out var oa) ? ParseOAuth(r, oa) : null,
            r.List(o, p, "secretNames"));
    }

    private static DesignOAuth ParseOAuth(Reader r, JsonElement el)
    {
        const string p = "$.auth.oauth";
        var o = r.Object(el, p);
        r.Keys(o, p, ["authorizeUrl", "tokenUrl", "scopes", "clientId", "usesConnectApp"]);
        return new DesignOAuth(
            r.OptStr(o, p, "authorizeUrl"), r.OptStr(o, p, "tokenUrl"), r.List(o, p, "scopes"),
            r.OptStr(o, p, "clientId"), r.Bool(o, p, "usesConnectApp"));
    }

    private static DesignObjectMap ParseObjectMap(Reader r, JsonElement el)
    {
        const string p = "$.objectMap";
        var o = r.Object(el, p);
        r.Keys(o, p, ["table", "keyStrategy", "keyKind", "operations"]);
        return new DesignObjectMap(r.Str(o, p, "table"), r.Str(o, p, "keyStrategy"), r.Str(o, p, "keyKind"), r.Map(o, p, "operations"));
    }

    private sealed class Reader(string source)
    {
        public DesignFormatException Fail(string path, string message) => new($"{source}: {path}: {message}");

        public Dictionary<string, JsonElement> Object(JsonElement el, string path)
        {
            if (el.ValueKind != JsonValueKind.Object) throw Fail(path, "expected an object");
            var d = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var p in el.EnumerateObject())
                if (!d.TryAdd(p.Name, p.Value)) throw Fail($"{path}.{p.Name}", "duplicate key");
            return d;
        }

        public void Keys(Dictionary<string, JsonElement> o, string path, string[] known)
        {
            foreach (var k in o.Keys)
                if (!known.Contains(k, StringComparer.Ordinal)) throw Fail($"{path}.{k}", "unknown key");
        }

        public JsonElement Req(Dictionary<string, JsonElement> o, string path, string key) =>
            o.TryGetValue(key, out var v) ? v : throw Fail($"{path}.{key}", "required key is missing");

        public string Str(Dictionary<string, JsonElement> o, string path, string key)
        {
            var v = Req(o, path, key);
            return v.ValueKind == JsonValueKind.String ? v.GetString()! : throw Fail($"{path}.{key}", "expected a string");
        }

        public string? OptStr(Dictionary<string, JsonElement> o, string path, string key)
        {
            if (!o.TryGetValue(key, out var v) || v.ValueKind == JsonValueKind.Null) return null;
            return v.ValueKind == JsonValueKind.String ? v.GetString() : throw Fail($"{path}.{key}", "expected a string");
        }

        public int Int(Dictionary<string, JsonElement> o, string path, string key)
        {
            var v = Req(o, path, key);
            return v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : throw Fail($"{path}.{key}", "expected an integer");
        }

        public bool Bool(Dictionary<string, JsonElement> o, string path, string key)
        {
            var v = Req(o, path, key);
            return v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : throw Fail($"{path}.{key}", "expected true or false");
        }

        public IReadOnlyList<string> List(Dictionary<string, JsonElement> o, string path, string key)
        {
            var v = Req(o, path, key);
            if (v.ValueKind != JsonValueKind.Array) throw Fail($"{path}.{key}", "expected an array");
            var list = new List<string>();
            foreach (var item in v.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String) throw Fail($"{path}.{key}[{list.Count}]", "expected a string");
                list.Add(item.GetString()!);
            }
            return list;
        }

        public IReadOnlyDictionary<string, string> Map(Dictionary<string, JsonElement> o, string path, string key)
        {
            var v = Req(o, path, key);
            if (v.ValueKind != JsonValueKind.Object) throw Fail($"{path}.{key}", "expected an object");
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var p in v.EnumerateObject())
            {
                var value = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : throw Fail($"{path}.{key}.{p.Name}", "expected a string");
                if (!map.TryAdd(p.Name, value)) throw Fail($"{path}.{key}.{p.Name}", "duplicate key");
            }
            return map;
        }
    }
}
