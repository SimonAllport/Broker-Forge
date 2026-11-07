namespace BrokerForge.SwaggerForge;

/// <summary>
/// <see cref="Prefix"/> is the publisher prefix (ext). <see cref="ReservedNames"/> are the header and query names the
/// connection's authentication sends; a definition parameter with one of them is left to the authentication.
/// </summary>
public sealed record DraftOptions(string ServiceInstance, string Prefix, IReadOnlyList<string>? ReservedNames = null);

/// <summary>
/// The drafted schema file and descriptors (operation name to YAML), what was skipped and why, and
/// <see cref="Notes"/>: what was not drafted and why.
/// </summary>
public sealed record DraftResult(
    string SchemaYaml,
    IReadOnlyDictionary<string, string> Descriptors,
    IReadOnlyList<SkippedOperation> Skipped,
    IReadOnlyList<string> Notes);

public sealed record SkippedOperation(string Method, string Path, string Reason);

/// <summary>An OpenAPI shape SwaggerForge will not draft. Caught per operation and reported as skipped.</summary>
internal sealed class UnsupportedShapeException(string reason) : Exception(reason);
