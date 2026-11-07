namespace BrokerForge.Schema;

/// <summary>
/// One source's table schema file, <c>schemas/&lt;ServiceInstance&gt;.yaml</c> (descriptor spec 14, HLD D-17).
/// <see cref="Table"/> is null for a file that only declares Custom APIs. Dictionaries keep file order.
/// </summary>
public sealed record SourceSchema(
    string? Table,
    string? DisplayName,
    string? DisplayCollectionName,
    string? ExternalName,
    string? PrimaryName,
    IReadOnlyDictionary<string, ColumnSchema> Columns,
    IReadOnlyDictionary<string, CustomApiSchema> CustomApis)
{
    /// <summary>The implicit primary key column, <c>&lt;table&gt;id</c>.</summary>
    public string? PrimaryKey => Table == null ? null : Table + "id";
}

/// <summary><see cref="Options"/> maps the source value (the option label) to the Dataverse option value.</summary>
public sealed record ColumnSchema(string Type, string DisplayName, int? MaxLength, int? Precision, IReadOnlyDictionary<string, int>? Options)
{
    public static readonly IReadOnlySet<string> Types = new HashSet<string> { "string", "memo", "int", "decimal", "bool", "datetime", "date", "optionset" };

    // Value equality over Options: records compare dictionaries by reference otherwise.
    public bool Equals(ColumnSchema? other) =>
        other is not null && Type == other.Type && DisplayName == other.DisplayName && MaxLength == other.MaxLength && Precision == other.Precision &&
        (Options == null ? other.Options == null : other.Options != null && Options.Count == other.Options.Count && Options.All(o => other.Options.TryGetValue(o.Key, out var v) && v == o.Value));

    public override int GetHashCode() => HashCode.Combine(Type, DisplayName, MaxLength, Precision);
}

public sealed record CustomApiSchema(string DisplayName, IReadOnlyDictionary<string, ParamSchema> Inputs, IReadOnlyDictionary<string, string> Outputs)
{
    public static readonly IReadOnlySet<string> ParamTypes = new HashSet<string> { "string", "int", "decimal", "bool", "datetime", "guid" };
}

public sealed record ParamSchema(string Type, bool Required);
