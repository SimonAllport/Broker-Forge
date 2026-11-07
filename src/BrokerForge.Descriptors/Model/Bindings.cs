namespace BrokerForge.Descriptors.Model;

/// <summary>Optional modifiers on a binding object (descriptor spec 3). Null means "use the default for the location".</summary>
public sealed record BindingModifiers(bool? Required, bool? OmitIfNull, string? Format)
{
    public static readonly BindingModifiers None = new(null, null, null);
}

/// <summary>One value source. Exactly one per path, query, header or body leaf.</summary>
public abstract record Binding(BindingModifiers Modifiers);

/// <summary>A YAML scalar. <see cref="Quoted"/> is false for plain scalars, so the body renderer can emit true/false/null/numbers as typed JSON.</summary>
public sealed record LiteralBinding(string Value, bool Quoted) : Binding(BindingModifiers.None);

public sealed record StaticBinding(string Value, bool Quoted, BindingModifiers M) : Binding(M);

public sealed record InstanceBinding(string Key, BindingModifiers M) : Binding(M);

/// <summary>Named secret on the service instance. Resolved only by the host; never allowed in path or body.</summary>
public sealed record SecretBinding(string Name, BindingModifiers M) : Binding(M);

public sealed record ContextBinding(string Name, BindingModifiers M) : Binding(M)
{
    public static readonly IReadOnlySet<string> Names = new HashSet<string> { "userObjectId", "correlationId", "environmentId", "async" };
}

/// <summary><see cref="OptionSet"/> names the option set map that reverse-maps a Dataverse option value on writes; null refuses option set cells.</summary>
public sealed record ColBinding(string Column, bool AlwaysSend, BindingModifiers M, string? OptionSet = null) : Binding(M);

/// <summary>A Custom API input parameter, by name.</summary>
public sealed record ParamBinding(string Name, BindingModifiers M) : Binding(M);

/// <summary>The decoded external key; <see cref="Part"/> names a composite key part, null for the whole key.</summary>
public sealed record IdBinding(string? Part, BindingModifiers M) : Binding(M);

public sealed record FilterBinding(string Column, IReadOnlyList<string> Ops, BindingModifiers M) : Binding(M);

/// <summary>Dataverse column to source sort field. <see cref="DescFormat"/> is a template with <c>{field}</c>; without it a descending sort is unsupported.</summary>
public sealed record SortBinding(IReadOnlyDictionary<string, string> Fields, string? DescFormat, BindingModifiers M) : Binding(M);

/// <summary>pageSize, token, offset or pageNumber, taken from the paging coordinator.</summary>
public sealed record PagingBinding(string Kind, int? Max, BindingModifiers M) : Binding(M)
{
    public static readonly IReadOnlySet<string> Kinds = new HashSet<string> { "pageSize", "token", "offset", "pageNumber" };
}

public sealed record ComputedBinding(string Kind, BindingModifiers M) : Binding(M)
{
    public static readonly IReadOnlySet<string> Kinds = new HashSet<string> { "uuid", "utcnow" };
}

public static class Formats
{
    public static readonly IReadOnlySet<string> All = new HashSet<string> { "iso8601", "date", "epochSeconds", "epochMillis", "lower", "upper", "csv", "json" };
}
