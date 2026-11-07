using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace BrokerForge.SwaggerForge;

/// <summary>An ordered YAML tree written in block style, so drafts keep the field order of the spec examples.</summary>
internal sealed partial class YamlMap
{
    private readonly List<(string Key, object Value)> _entries = new();

    public int Count => _entries.Count;

    /// <summary>Value is a string, int, bool, <see cref="YamlMap"/> or a list of strings.</summary>
    public YamlMap Add(string key, object value)
    {
        _entries.Add((key, value));
        return this;
    }

    public YamlMap Map(string key)
    {
        var child = new YamlMap();
        Add(key, child);
        return child;
    }

    public override string ToString()
    {
        var sb = new StringBuilder();
        Write(sb, 0);
        return sb.ToString();
    }

    private void Write(StringBuilder sb, int indent)
    {
        var pad = new string(' ', indent);
        foreach (var (key, value) in _entries)
        {
            sb.Append(pad).Append(Scalar(key)).Append(':');
            switch (value)
            {
                case YamlMap { Count: 0 }:
                    sb.Append(" {}\n");
                    break;
                case YamlMap map:
                    sb.Append('\n');
                    map.Write(sb, indent + 2);
                    break;
                case IReadOnlyList<string> { Count: 0 }:
                    sb.Append(" []\n");
                    break;
                case IReadOnlyList<string> list:
                    sb.Append(" [").Append(string.Join(", ", list.Select(Scalar))).Append("]\n");
                    break;
                case bool b:
                    sb.Append(' ').Append(b ? "true" : "false").Append('\n');
                    break;
                case int i:
                    sb.Append(' ').Append(i.ToString(CultureInfo.InvariantCulture)).Append('\n');
                    break;
                default:
                    sb.Append(' ').Append(Scalar((string)value)).Append('\n');
                    break;
            }
        }
    }

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_ ()./-]*$")]
    private static partial Regex PlainSafe();

    /// <summary>Plain when it cannot be read as anything but this string; otherwise double-quoted.</summary>
    private static string Scalar(string value)
    {
        var reserved = value is "true" or "false" or "null" or "yes" or "no" or "on" or "off" or "~";
        if (PlainSafe().IsMatch(value) && !reserved && value.Trim() == value)
            return value;
        return Quoted(value);
    }

    /// <summary>A YAML double-quoted scalar. Control characters, NEL, the line and paragraph separators and BOM are escaped, because YAML folds or refuses them raw.</summary>
    private static string Quoted(string value)
    {
        var sb = new StringBuilder("\"");
        foreach (var c in value)
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case < ' ' or (>= '\u007f' and <= '\u009f') or '\u2028' or '\u2029' or '\ufeff':
                    sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    break;
                default: sb.Append(c); break;
            }
        return sb.Append('"').ToString();
    }
}
