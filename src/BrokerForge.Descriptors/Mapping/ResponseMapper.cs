using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BrokerForge.Abstractions;
using BrokerForge.Contracts;
using BrokerForge.Descriptors.Model;
using BrokerForge.Keys;

namespace BrokerForge.Descriptors.Mapping;

/// <summary>
/// Maps a successful source response to rows (descriptor spec 6). A shape that does not match the map
/// is a <see cref="MappingException"/> naming the column and path; nothing is guessed or skipped.
/// </summary>
public static class ResponseMapper
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

    // `json` cells are read by people in a memo column, so keep them compact and unescaped.
    private static readonly JsonSerializerOptions JsonCell = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static IReadOnlyList<MappedRow> MapRows(OperationDescriptor d, BrokerResult r, KeyKind keyKind, string correlationId)
    {
        MappingException Fail(string message) => new($"{message} (operation {d.Operation}).", correlationId);
        var response = d.Response ?? throw Fail("the descriptor has no response block");
        var rowsPath = response.Rows ?? throw Fail("the descriptor has no response.rows");
        var root = Json.Parse(r.Body, Fail);

        List<JsonNode?> rows;
        if (rowsPath.EndsWith("[*]", StringComparison.Ordinal))
        {
            // `$.data[*]` matches nothing both for [] and for a missing `data`; only the first is an empty page.
            var container = Json.Select(root, rowsPath[..^3]);
            if (container.Count != 1 || container[0] is not JsonArray)
                throw Fail($"response path {rowsPath} does not point at a list in the source response");
            rows = Json.Select(root, rowsPath).ToList();
        }
        else
        {
            var matches = Json.Select(root, rowsPath);
            if (matches.Count != 1 || matches[0] == null)
                throw Fail($"response path {rowsPath} matched {matches.Count} values; expected one");
            rows = matches[0] is JsonArray array ? array.ToList() : new List<JsonNode?> { matches[0] };
        }

        if (d.Binds == Messages.Retrieve && rows.Count != 1)
            throw Fail($"Retrieve expected one row at {rowsPath}; got {rows.Count}");

        return rows.Select((row, i) =>
        {
            if (row is not JsonObject)
                throw Fail($"row {i} at {rowsPath} is not an object");
            return new MappedRow(ReadKey(response.Key, row, keyKind, Fail), MapCells(d, response, row, Fail));
        }).ToList();
    }

    /// <summary>A Custom API's output cells (descriptor spec 6), in declaration order. A missing output is a <see cref="MappingException"/> unless optional.</summary>
    public static List<Cell> MapOutputs(OperationDescriptor d, BrokerResult r, string correlationId)
    {
        MappingException Fail(string message) => new($"{message} (operation {d.Operation}).", correlationId);
        var outputs = d.Response?.Outputs ?? new Dictionary<string, MapEntry>();
        if (outputs.Count == 0)
            return new List<Cell>();
        var root = Json.Parse(r.Body, Fail);
        return outputs.Select(o => Entry(d, $"output {o.Key}", o.Key, o.Value, root, "the source response", Fail)).ToList();
    }

    /// <summary>The scalar at a JSONPath in a response body (paging next-cursor and totals), or null when missing or JSON null.</summary>
    public static string? SelectScalar(byte[] body, string path, Func<string, Exception> fail)
    {
        var matches = Json.Select(Json.Parse(body, fail), path);
        return matches.Count == 1 ? Json.ScalarText(matches[0]) : null;
    }

    public static ExternalKey MapCreatedKey(OperationDescriptor d, BrokerResult r, KeyKind keyKind, string correlationId)
    {
        MappingException Fail(string message) => new($"{message} (operation {d.Operation}).", correlationId);
        switch (d.Response?.Key)
        {
            case HeaderKey h:
                var header = r.Header(h.Header) ?? throw Fail($"the source returned no {h.Header} header for the new record's key");
                // The header comes from the source and the pattern from the descriptor: bound the match so a
                // backtracking pattern cannot hold the request.
                Match match;
                try { match = Regex.Match(header, h.Pattern, RegexOptions.None, MatchTimeout); }
                catch (RegexMatchTimeoutException) { throw Fail($"matching the {h.Header} header against the key pattern {h.Pattern} ran out of time"); }
                if (!match.Success || !match.Groups["id"].Success)
                    throw Fail($"the {h.Header} header does not match the key pattern {h.Pattern}");
                return KeyFromText(match.Groups["id"].Value, keyKind, $"header {h.Header}", Fail);
            case PathKey or CompositeKey:
                return ReadKey(d.Response.Key, Json.Parse(r.Body, Fail), keyKind, Fail);
            default:
                throw Fail("the descriptor has no response.key for the new record");
        }
    }

    private static ExternalKey ReadKey(KeySpec? spec, JsonNode? row, KeyKind keyKind, Func<string, MappingException> fail)
    {
        switch (spec)
        {
            case PathKey p:
                var matches = Json.Select(row, p.Path);
                if (matches.Count != 1 || matches[0] is not JsonValue value)
                    throw fail($"key path {p.Path} matched no single value in the source row");
                try
                {
                    return ExternalKey.FromJson(JsonSerializer.SerializeToElement(value), keyKind);
                }
                catch (FormatException ex)
                {
                    throw fail($"key path {p.Path}: {ex.Message}");
                }
            case CompositeKey:
                throw fail("composite keys need the key map, which is not supported until phase 5");
            default:
                throw fail("the descriptor's response.key is not a JSONPath");
        }
    }

    private static ExternalKey KeyFromText(string text, KeyKind kind, string where, Func<string, MappingException> fail)
    {
        try
        {
            return ExternalKey.FromJson(JsonSerializer.SerializeToElement(text), kind);
        }
        catch (FormatException ex)
        {
            throw fail($"key from {where}: {ex.Message}");
        }
    }

    private static List<Cell> MapCells(OperationDescriptor d, ResponseSpec response, JsonNode row, Func<string, MappingException> fail)
    {
        return response.Map.Select(m => Entry(d, $"column {m.Key}", m.Key, m.Value, row, "the source row", fail)).ToList();
    }

    /// <summary>One map or outputs entry. <paramref name="what"/> names it in errors ("column ext_x", "output X").</summary>
    private static Cell Entry(OperationDescriptor d, string what, string name, MapEntry entry, JsonNode? root, string where, Func<string, MappingException> fail)
    {
        var matches = Json.Select(root, entry.Path);
        if (matches.Count == 0 && !entry.Optional)
            throw fail($"{what}: path {entry.Path} is missing from {where} (mark it optional: true if the source omits it)");
        if (matches.Count > 1)
            throw fail($"{what}: path {entry.Path} matched {matches.Count} values; expected one");
        return Coerce(d, what, name, entry, matches.Count == 0 ? null : matches[0], fail);
    }

    private static Cell Coerce(OperationDescriptor d, string what, string col, MapEntry entry, JsonNode? node, Func<string, MappingException> fail)
    {
        var setName = entry.OptionSetName;
        if (entry.Type == MapEntry.JsonType)
            return new Cell { Col = col, Type = CellTypes.String, Value = node?.ToJsonString(JsonCell) };
        var cellType = setName != null ? CellTypes.OptionSet : entry.Type ?? CellTypes.String;
        if (node == null)
            return new Cell { Col = col, Type = cellType, Value = null };
        MappingException Bad(string expected) => fail($"{what}: value at {entry.Path} is not {expected}");

        if (node is not JsonValue)
            throw Bad("a scalar");
        var text = Json.ScalarText(node)!;
        var kind = node.GetValueKind();
        var inv = CultureInfo.InvariantCulture;

        string value = cellType switch
        {
            CellTypes.String => text,
            CellTypes.Int => int.TryParse(text, NumberStyles.AllowLeadingSign, inv, out var i) ? i.ToString(inv) : throw Bad("an integer"),
            CellTypes.Decimal => decimal.TryParse(text, NumberStyles.Number | NumberStyles.AllowExponent, inv, out var m) ? m.ToString(inv) : throw Bad("a decimal"),
            CellTypes.Bool => kind is JsonValueKind.True or JsonValueKind.False || text is "true" or "false" ? text : throw Bad("a boolean"),
            CellTypes.DateTime => kind == JsonValueKind.String && DateTimeOffset.TryParse(text, inv, DateTimeStyles.AssumeUniversal, out var at)
                ? at.UtcDateTime.ToString("o", inv)
                : throw Bad("a date and time"),
            CellTypes.Date => kind == JsonValueKind.String && DateTime.TryParse(text, inv, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date)
                ? date.ToString("yyyy-MM-dd", inv)
                : throw Bad("a date"),
            CellTypes.Guid => Guid.TryParse(text, out var g) ? g.ToString("D") : throw Bad("a GUID"),
            CellTypes.OptionSet => OptionValue(d, setName!, text, col, entry, fail),
            _ => throw fail($"{what}: unknown type {entry.Type}"),
        };
        return new Cell { Col = col, Type = cellType, Value = value };
    }

    private static string OptionValue(OperationDescriptor d, string setName, string sourceValue, string col, MapEntry entry, Func<string, MappingException> fail)
    {
        var set = d.Optionsets[setName];
        if (set.Values.TryGetValue(sourceValue, out var option))
            return option.ToString(CultureInfo.InvariantCulture);
        if (set.UnknownNull)
            return null!;
        throw fail($"column {col}: source value `{sourceValue}` at {entry.Path} has no entry in option set {setName}");
    }
}
