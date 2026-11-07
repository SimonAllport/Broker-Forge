using System.Text;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;

namespace BrokerForge.SwaggerForge;

/// <summary>Naming and type rules. Each is a fixed mapping from the OpenAPI document, never a guess about the source.</summary>
internal static class Heuristics
{
    public const int FirstOptionValue = 100000000;

    /// <summary>placedAt → "Placed at"; on_hand → "On hand".</summary>
    public static string Humanise(string name)
    {
        var words = Words(name);
        var text = string.Join(" ", words).ToLowerInvariant();
        return text.Length == 0 ? name : char.ToUpperInvariant(text[0]) + text[1..];
    }

    /// <summary>widgetId → WidgetId; adjust_stock → AdjustStock.</summary>
    public static string Pascal(string name) =>
        string.Concat(Words(name).Select(w => char.ToUpperInvariant(w[0]) + w[1..]));

    /// <summary>The column logical name: prefix, underscore, then the ASCII letters and digits of the source name, lower case.</summary>
    public static string Column(string prefix, string name) =>
        prefix + "_" + new string(name.Where(char.IsAsciiLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    /// <summary>A name Dataverse can carry: ASCII letters, digits and separators only, with at least one letter.</summary>
    public static bool Representable(string name) =>
        name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or ' ' or '.') && name.Any(char.IsAsciiLetter);

    /// <summary>A JSONPath member step: <c>.name</c> for identifiers, otherwise <c>['name']</c> (RFC 9535 escaping).</summary>
    public static string Step(string name) =>
        name.Length > 0 && (char.IsAsciiLetter(name[0]) || name[0] == '_') && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')
            ? "." + name
            : "['" + name.Replace("\\", "\\\\").Replace("'", "\\'") + "']";

    /// <summary>A Custom API parameter name, or an unsupported-shape refusal when the source name cannot become one.</summary>
    public static string ParamName(string name, string where)
    {
        var pascal = Pascal(name);
        return pascal.Length > 0 && char.IsAsciiLetter(pascal[0]) && pascal.All(char.IsAsciiLetterOrDigit)
            ? pascal
            : throw new UnsupportedShapeException($"{where} `{name}` cannot become a Custom API parameter name");
    }

    private static List<string> Words(string name)
    {
        var words = new List<string>();
        var current = new StringBuilder();
        foreach (var c in name)
        {
            if (!char.IsLetterOrDigit(c))
            {
                Flush();
                continue;
            }
            if (char.IsUpper(c) && current.Length > 0 && !char.IsUpper(current[^1]))
                Flush();
            current.Append(c);
        }
        Flush();
        return words;

        void Flush()
        {
            if (current.Length > 0)
                words.Add(current.ToString());
            current.Clear();
        }
    }

    public static bool IsScalar(OpenApiSchema s) => s.Type is "string" or "integer" or "number" or "boolean";

    /// <summary>The values of a string enum; null for no enum, or one whose values are not all strings (an integer enum is a plain int).</summary>
    public static IReadOnlyList<string>? EnumValues(OpenApiSchema s) =>
        s.Type == "string" && s.Enum is { Count: > 0 } values && values.All(v => v is OpenApiString)
            ? values.Select(v => ((OpenApiString)v).Value).ToList()
            : null;

    /// <summary>Decimal places stated by <c>multipleOf</c> 1, 0.1 … 0.0000000001; null when it states none.</summary>
    public static int? PrecisionOf(OpenApiSchema s)
    {
        if (s.MultipleOf is not { } step)
            return null;
        for (var places = 0; places <= 10; places++)
            if (step == (decimal)Math.Pow(10, -places))
                return places;
        return null;
    }

    /// <summary>Column type for a scalar property (descriptor spec 14).</summary>
    public static string ColumnType(OpenApiSchema s) => s.Type switch
    {
        "string" when EnumValues(s) != null => "optionset",
        "string" when s.Format == "date-time" => "datetime",
        "string" when s.Format == "date" => "date",
        "string" => "string",
        "integer" => "int",
        "number" => "decimal",
        "boolean" => "bool",
        _ => throw new UnsupportedShapeException($"type {s.Type ?? "(none)"} has no column type"),
    };

    /// <summary>Custom API parameter type for a scalar (descriptor spec 14). Enums travel as their string value.</summary>
    public static string ParamType(OpenApiSchema s) => s.Type switch
    {
        "string" when s.Format == "date-time" => "datetime",
        "string" when s.Format == "uuid" => "guid",
        "string" => "string",
        "integer" => "int",
        "number" => "decimal",
        "boolean" => "bool",
        _ => throw new UnsupportedShapeException($"type {s.Type ?? "(none)"} has no Custom API parameter type"),
    };

    /// <summary>The map entry type for a column type; null means a plain path (string).</summary>
    public static string? MapType(string columnType, string? optionSet) => columnType switch
    {
        "string" => null,
        "memo" => "json",
        "optionset" => "optionset:" + optionSet,
        var t => t,
    };

    /// <summary>
    /// Refuses shapes the drafter cannot represent: composition (oneOf, anyOf, allOf) and a schema that
    /// contains itself. <paramref name="ancestors"/> holds the schemas on the current path (reference equality);
    /// <paramref name="verified"/> holds schemas already checked, so a schema reached by many routes is walked once.
    /// </summary>
    public static void Check(OpenApiSchema? s, string where, HashSet<OpenApiSchema>? ancestors = null, HashSet<OpenApiSchema>? verified = null)
    {
        if (s == null || verified?.Contains(s) == true)
            return;
        ancestors ??= new HashSet<OpenApiSchema>(ReferenceEqualityComparer.Instance);
        if (!ancestors.Add(s))
            throw new UnsupportedShapeException($"schema {s.Reference?.Id ?? where} refers to itself");
        foreach (var (kind, list) in new[] { ("oneOf", s.OneOf), ("anyOf", s.AnyOf), ("allOf", s.AllOf) })
            if (list is { Count: > 0 })
                throw new UnsupportedShapeException($"{where} uses {kind}, which has no single column layout");
        foreach (var (name, child) in s.Properties ?? new Dictionary<string, OpenApiSchema>())
            Check(child, $"{where}.{name}", ancestors, verified);
        Check(s.Items, $"{where}[]", ancestors, verified);
        if (s.AdditionalProperties != null)
            Check(s.AdditionalProperties, $"{where}.*", ancestors, verified);
        ancestors.Remove(s);
        verified?.Add(s);
    }
}
