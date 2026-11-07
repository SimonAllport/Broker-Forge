using System.Net.Http.Headers;
using BrokerForge.Abstractions;
using BrokerForge.Contracts;

namespace BrokerForge.Adapters.Rest;

/// <summary>
/// REST adapter (HLD 7.2): sends a rendered request over the authenticated channel and returns the raw
/// reply. It never reads secrets or descriptors. Network failures are <see cref="SourceException"/> with
/// no status; cancellation propagates so the executor can tell a deadline from a caller abort.
/// </summary>
public sealed class RestBroker : IBroker
{
    public string BrokerType => "rest";

    public BrokerCapabilities Capabilities { get; } = new(new HashSet<string> { "cursor", "offset", "pageNumber", "link", "none" });

    public Task<DiscoveryResult> DiscoverAsync(ServiceInstance instance, CancellationToken ct) =>
        throw new NotSupportedException("REST discovery arrives with SwaggerForge in phase 2.");

    public async Task<BrokerResult> ExecuteAsync(RenderedRequest request, IAuthenticatedChannel channel, string correlationId, CancellationToken ct)
    {
        using var message = new HttpRequestMessage(new HttpMethod(request.Method), new Uri(request.BaseUrl.TrimEnd('/') + request.PathAndQuery, UriKind.Absolute));
        foreach (var (name, value) in request.Headers)
            message.Headers.TryAddWithoutValidation(name, value);
        if (request.Body != null)
        {
            message.Content = new ByteArrayContent(request.Body);
            message.Content.Headers.ContentType = new MediaTypeHeaderValue(request.ContentType ?? "application/json") { CharSet = "utf-8" };
        }

        HttpResponseMessage response;
        try
        {
            response = await channel.SendAsync(message, ct);
        }
        catch (HttpRequestException ex)
        {
            // The URL is left out: its query can carry a secret.
            throw new SourceException($"Source request failed (network) for {request.Method} {PathOnly(request.PathAndQuery)}: {ex.HttpRequestError}.", correlationId, null, ex);
        }

        using (response)
        {
            var body = await response.Content.ReadAsByteArrayAsync(ct);
            var headers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, values) in response.Headers.Concat(response.Content.Headers))
                headers[name] = values.ToArray();
            return new BrokerResult((int)response.StatusCode, headers, body);
        }
    }

    private static string PathOnly(string pathAndQuery) =>
        pathAndQuery.IndexOf('?') is var q and >= 0 ? pathAndQuery[..q] : pathAndQuery;
}
