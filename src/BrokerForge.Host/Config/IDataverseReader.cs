using System.Text.Json;

namespace BrokerForge.Host.Config;

/// <summary>
/// Reads from an environment's Dataverse Web API as the host's own identity. <c>relativeUrl</c> is relative to
/// <c>&lt;orgUrl&gt;/api/data/v9.2/</c>. A collection read returns every page as one <c>{"value":[...]}</c> document.
/// </summary>
public interface IDataverseReader
{
    /// <exception cref="DataverseUnavailableException">Dataverse could not be read: no token, unreachable, timed out or a non-success status.</exception>
    Task<JsonDocument> GetAsync(string orgUrl, string relativeUrl, CancellationToken ct);
}

/// <summary>
/// Dataverse could not be read. The message is the reason only (for example <c>HTTP 403 (Forbidden)</c>); it never holds a
/// token. <see cref="Transient"/> is true only for what may pass by itself: no token, network errors, timeouts, 408, 429
/// and 5xx. Anything else (another status, an answer of the wrong shape) means the request or the setup is wrong.
/// </summary>
public sealed class DataverseUnavailableException(string reason, bool transient) : Exception(reason)
{
    public bool Transient { get; } = transient;
}
