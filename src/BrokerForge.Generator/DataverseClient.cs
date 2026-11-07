using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace BrokerForge.Generator;

/// <summary>A Dataverse Web API call failed. The message carries the method, path, status and the platform's error text.</summary>
public sealed class DataverseException(string message) : Exception(message);

/// <summary>
/// Thin Web API client for the generator. Reads always run; with <see cref="DryRun"/> writes are recorded in
/// <see cref="Writes"/> and not sent. The bearer token goes only in the Authorization header.
/// </summary>
/// <param name="wait">How a 429 waits before the request is sent again; Task.Delay unless a test passes its own.</param>
public sealed class DataverseClient(HttpClient http, Func<CancellationToken, Task<string>> token, bool dryRun,
    Func<TimeSpan, CancellationToken, Task>? wait = null)
{
    // A 429 means Dataverse did not process the request (a service protection limit, or another customisation
    // publishing), so any request may be sent again after Retry-After: up to three times, waiting at most a minute each.
    private const int BusyRetries = 3;
    private static readonly TimeSpan BusyDefault = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan BusyLongest = TimeSpan.FromMinutes(1);

    private readonly List<string> _writes = new();

    public bool DryRun => dryRun;

    /// <summary>An OData string literal for a $filter: quotes doubled, then percent-encoded so &amp;, # and + survive the query string.</summary>
    public static string Literal(string value) => "'" + Uri.EscapeDataString(value.Replace("'", "''")) + "'";

    /// <summary>"METHOD path" for each write that succeeded (or is planned, in a dry run).</summary>
    public IReadOnlyList<string> Writes => _writes;

    /// <summary>The JSON body, or null when the resource does not exist (404).</summary>
    public async Task<JsonObject?> GetAsync(string path, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, path, null, null, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        return await ReadAsync(response, "GET", path, ct) ?? throw new DataverseException($"GET {path} returned no body.");
    }

    /// <summary>The <c>value</c> rows of a collection query.</summary>
    public async Task<IReadOnlyList<JsonObject>> ListAsync(string path, CancellationToken ct)
    {
        var body = await GetAsync(path, ct) ?? throw new DataverseException($"GET {path} returned 404.");
        return body["value"]?.AsArray().Select(v => v!.AsObject()).ToList() ?? throw new DataverseException($"GET {path} returned no value list.");
    }

    /// <summary>POST, PATCH or PUT. Returns the representation when the platform sends one; null in a dry run.</summary>
    public async Task<JsonObject?> WriteAsync(HttpMethod method, string path, JsonObject? body, string? solution, CancellationToken ct, bool mergeLabels = false)
    {
        if (dryRun)
        {
            _writes.Add($"{method.Method} {path}");
            return null;
        }
        using var response = await SendAsync(method, path, body, solution, ct, mergeLabels);
        var result = await ReadAsync(response, method.Method, path, ct);
        _writes.Add($"{method.Method} {path}");
        return result;
    }

    /// <summary>DELETE. A resource already gone (404) counts as deleted, so a re-run after a failure converges.</summary>
    public async Task DeleteAsync(string path, CancellationToken ct)
    {
        if (!dryRun)
        {
            using var response = await SendAsync(HttpMethod.Delete, path, null, null, ct);
            if (response.StatusCode != HttpStatusCode.NotFound)
                await ReadAsync(response, "DELETE", path, ct);
        }
        _writes.Add($"DELETE {path}");
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, JsonObject? body, string? solution, CancellationToken ct, bool mergeLabels = false)
    {
        for (var attempt = 0; ; attempt++)
        {
            var response = await SendOnceAsync(method, path, body, solution, ct, mergeLabels);
            if ((int)response.StatusCode != 429 || attempt == BusyRetries)
                return response;
            var after = response.Headers.RetryAfter?.Delta ?? BusyDefault;
            response.Dispose();
            await (wait ?? Task.Delay)(after > BusyLongest ? BusyLongest : after, ct);
        }
    }

    private async Task<HttpResponseMessage> SendOnceAsync(HttpMethod method, string path, JsonObject? body, string? solution, CancellationToken ct, bool mergeLabels)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await token(ct));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Add("OData-Version", "4.0");
        request.Headers.Add("OData-MaxVersion", "4.0");
        request.Headers.Add("Prefer", "return=representation");
        if (solution != null)
            request.Headers.Add("MSCRM.SolutionUniqueName", solution);
        if (mergeLabels)
            request.Headers.Add("MSCRM.MergeLabels", "true");
        if (body != null)
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        return await http.SendAsync(request, ct);
    }

    private static async Task<JsonObject?> ReadAsync(HttpResponseMessage response, string method, string path, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new DataverseException($"{method} {path} returned HTTP {(int)response.StatusCode}: {(text.Length > 1500 ? text[..1500] : text)}");
        return text.Length == 0 ? null : JsonNode.Parse(text)?.AsObject();
    }
}
