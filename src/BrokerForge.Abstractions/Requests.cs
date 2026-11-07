namespace BrokerForge.Abstractions;

/// <summary>
/// A source request after binding. <see cref="PathAndQuery"/> is relative to <see cref="BaseUrl"/>.
/// <see cref="SecretQueryParams"/> names query parameters that carry secrets and
/// <see cref="SecretValues"/> holds every resolved secret value, so links, logs and error messages can
/// strip them.
/// </summary>
public sealed record RenderedRequest(
    string BaseUrl,
    string Method,
    string PathAndQuery,
    IReadOnlyList<KeyValuePair<string, string>> Headers,
    byte[]? Body,
    string? ContentType,
    IReadOnlySet<string> SecretQueryParams,
    IReadOnlyCollection<string> SecretValues);

/// <summary>The raw source reply. Header names are case-insensitive.</summary>
public sealed record BrokerResult(int Status, IReadOnlyDictionary<string, string[]> Headers, byte[] Body)
{
    public string? Header(string name) => Headers.TryGetValue(name, out var values) && values.Length > 0 ? values[0] : null;
}
