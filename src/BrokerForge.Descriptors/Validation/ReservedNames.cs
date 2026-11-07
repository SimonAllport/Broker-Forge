namespace BrokerForge.Descriptors.Validation;

/// <summary>
/// Names a descriptor may not declare because the instance's auth mode sets them: headers (always
/// Authorization) and query parameters (apiKeyQuery). Case-insensitive, so a source that ignores case
/// cannot receive the credential and a descriptor value under one name.
/// </summary>
public sealed record ReservedNames(IReadOnlySet<string> Headers, IReadOnlySet<string> QueryParams)
{
    public static ReservedNames For(IEnumerable<string> authModeHeaders, IEnumerable<string>? authModeQueryParams = null) =>
        new(new HashSet<string>(authModeHeaders.Append("Authorization"), StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(authModeQueryParams ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase));

    public bool Contains(string header) => Headers.Contains(header);

    public bool ContainsQueryParam(string name) => QueryParams.Contains(name);
}
