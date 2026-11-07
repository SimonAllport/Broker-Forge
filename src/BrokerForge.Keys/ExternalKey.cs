using System.Globalization;
using System.Text.Json;

namespace BrokerForge.Keys;

public enum KeyKind { Int32, Int64, String, Guid }

/// <summary>A source key. <see cref="Value"/> is invariant text: integers in "D" form, GUIDs in "D" form.</summary>
public sealed record ExternalKey(KeyKind Kind, string Value)
{
    public static ExternalKey FromInt32(int value) => new(KeyKind.Int32, value.ToString(CultureInfo.InvariantCulture));
    public static ExternalKey FromInt64(long value) => new(KeyKind.Int64, value.ToString(CultureInfo.InvariantCulture));
    public static ExternalKey FromString(string value) => new(KeyKind.String, value ?? throw new ArgumentNullException(nameof(value)));
    public static ExternalKey FromGuid(Guid value) => new(KeyKind.Guid, value.ToString("D"));

    /// <summary>Reads a key from a source JSON value. A number for a string key is taken as its raw text.</summary>
    public static ExternalKey FromJson(JsonElement element, KeyKind kind) => kind switch
    {
        KeyKind.Int32 when element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var i) => FromInt32(i),
        KeyKind.Int32 when element.ValueKind == JsonValueKind.String && int.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) => FromInt32(i),
        KeyKind.Int64 when element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var l) => FromInt64(l),
        KeyKind.Int64 when element.ValueKind == JsonValueKind.String && long.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) => FromInt64(l),
        KeyKind.String when element.ValueKind == JsonValueKind.String => FromString(element.GetString()!),
        KeyKind.String when element.ValueKind == JsonValueKind.Number => FromString(element.GetRawText()),
        KeyKind.Guid when element.ValueKind == JsonValueKind.String && Guid.TryParse(element.GetString(), out var g) => FromGuid(g),
        _ => throw new FormatException($"A JSON {element.ValueKind} is not a {kind} key."),
    };

    public int AsInt32() => int.Parse(Value, NumberStyles.Integer, CultureInfo.InvariantCulture);
    public long AsInt64() => long.Parse(Value, NumberStyles.Integer, CultureInfo.InvariantCulture);
    public Guid AsGuid() => Guid.Parse(Value);
}
