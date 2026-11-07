using BrokerForge.Contracts;

namespace BrokerForge.Descriptors.Model;

/// <summary>One parsed operation descriptor. Property names follow the spec keys.</summary>
public sealed record OperationDescriptor(
    string Operation,
    int Version,
    string Binds,
    string? Table,
    RequestSpec Request,
    ResponseSpec? Response,
    PagingSpec? Paging,
    ErrorsSpec? Errors,
    RetrySpec? Retry,
    IReadOnlyDictionary<string, OptionSetMap> Optionsets,
    FiltersSpec Filters,
    bool? SortStable,
    string Source)
{
    public const string CustomApiPrefix = "customapi:";

    public bool IsRead => Binds is Messages.Retrieve or Messages.RetrieveMultiple;

    /// <summary>The Custom API unique name for <c>customapi:&lt;UniqueName&gt;</c> binds, otherwise null.</summary>
    public string? CustomApi => Binds.StartsWith(CustomApiPrefix, StringComparison.Ordinal) ? Binds[CustomApiPrefix.Length..] : null;
}

public sealed record RequestSpec(
    string Method,
    string Path,
    IReadOnlyDictionary<string, Binding> PathParams,
    IReadOnlyList<KeyValuePair<string, Binding>> Query,
    IReadOnlyList<KeyValuePair<string, Binding>> Headers,
    BodySpec? Body);

/// <summary>Either <see cref="Template"/> (structured, mirrored into JSON or form fields) or <see cref="Raw"/> with <c>{{ source.name }}</c> placeholders.</summary>
public sealed record BodySpec(string ContentType, BodyNode? Template, string? Raw, IReadOnlyList<RawPlaceholder> RawPlaceholders)
{
    public const string Json = "application/json";
    public const string Form = "application/x-www-form-urlencoded";
    public const string Xml = "text/xml";
    public static readonly IReadOnlySet<string> ContentTypes = new HashSet<string> { Json, Form, Xml };
}

/// <summary><see cref="Token"/> is the exact placeholder text, including the braces.</summary>
public sealed record RawPlaceholder(string Token, Binding Binding);

public abstract record BodyNode;
public sealed record BodyObject(IReadOnlyList<KeyValuePair<string, BodyNode>> Members) : BodyNode;
public sealed record BodyArray(IReadOnlyList<BodyNode> Items) : BodyNode;
public sealed record BodyLeaf(Binding Binding) : BodyNode;

/// <summary><see cref="Outputs"/> is Custom API output parameter to entry, in file order; empty for virtual table messages.</summary>
public sealed record ResponseSpec(string? Rows, KeySpec? Key, IReadOnlyDictionary<string, MapEntry> Map, IReadOnlyList<string> Unmapped, IReadOnlyDictionary<string, MapEntry> Outputs);

public abstract record KeySpec;
public sealed record PathKey(string Path) : KeySpec;
public sealed record HeaderKey(string Header, string Pattern) : KeySpec;
public sealed record CompositeKey(IReadOnlyDictionary<string, string> Parts) : KeySpec;

/// <summary>
/// <see cref="Type"/> is null (string) or one of string, int, decimal, bool, datetime, date, guid, json, optionset:&lt;map&gt;.
/// <c>json</c> serialises the matched JSON value to a string cell.
/// </summary>
public sealed record MapEntry(string Path, string? Type, bool Optional)
{
    public const string JsonType = "json";
    public static readonly IReadOnlySet<string> Types = new HashSet<string> { "string", "int", "decimal", "bool", "datetime", "date", "guid", JsonType };
    /// <summary>Types an <c>outputs</c> entry may take: the Custom API parameter types (descriptor spec 6).</summary>
    public static readonly IReadOnlySet<string> OutputTypes = new HashSet<string> { "string", "int", "decimal", "bool", "datetime", "guid" };
    public string? OptionSetName => Type != null && Type.StartsWith("optionset:", StringComparison.Ordinal) ? Type["optionset:".Length..] : null;
}

/// <summary>Source value to Dataverse option value. <see cref="UnknownNull"/> is <c>_unknown: null</c>.</summary>
public sealed record OptionSetMap(IReadOnlyDictionary<string, int> Values, bool UnknownNull);

public sealed record PagingSpec(
    string Strategy,
    PageSizeSpec? PageSize,
    TokenSpec? Token,
    OffsetSpec? Offset,
    PageNumberSpec? Page,
    int MaxCallsPerPage)
{
    public const int DefaultMaxCallsPerPage = 5;
    public static readonly IReadOnlySet<string> Strategies = new HashSet<string> { "cursor", "offset", "pageNumber", "link", "none" };
}

public sealed record PageSizeSpec(string Param, int Max);
public sealed record TokenSpec(string Param, string Next);
/// <summary><see cref="Total"/> is an optional JSONPath to the total row count.</summary>
public sealed record OffsetSpec(string Param, string? Total);
public sealed record PageNumberSpec(string Param, int Base);

public sealed record ErrorsSpec(IReadOnlyDictionary<int, ErrorMapping> Statuses, string? Default, SuccessBodyCheck? SuccessBodyCheck);

/// <summary><see cref="Exception"/> is NotFound, Conflict, Forbidden, Validation or Fail.</summary>
public sealed record ErrorMapping(string Exception, string? MessagePath)
{
    public static readonly IReadOnlySet<string> Kinds = new HashSet<string> { "NotFound", "Conflict", "Forbidden", "Validation", "Fail" };
}

/// <summary>A 2xx is a success only when the value at <see cref="Path"/> equals <see cref="EqualsValue"/> (compared as JSON text).</summary>
public sealed record SuccessBodyCheck(string Path, string EqualsValue);

/// <summary><see cref="On"/> holds status codes as text and/or "network".</summary>
public sealed record RetrySpec(bool Idempotent, int Attempts, IReadOnlyList<string> On);

public sealed record FiltersSpec(bool AllowOr, string? OrStyle)
{
    public static readonly FiltersSpec Default = new(false, null);
}
