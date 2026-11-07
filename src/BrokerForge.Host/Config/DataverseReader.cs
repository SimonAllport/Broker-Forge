using System.Net.Http.Headers;
using System.Text.Json;
using Azure.Core;

namespace BrokerForge.Host.Config;

/// <summary>
/// The real <see cref="IDataverseReader"/>: a token for <c>&lt;orgUrl&gt;/.default</c> from the host's
/// <see cref="TokenCredential"/>, sent on the named <c>dataverse</c> client, following <c>@odata.nextLink</c> until the
/// last page. A next link must stay under the org URL, so the token is never sent anywhere else.
/// </summary>
public sealed class DataverseReader(TokenCredential credential, HttpClient http) : IDataverseReader
{
    private const int MaxPages = 100;

    public async Task<JsonDocument> GetAsync(string orgUrl, string relativeUrl, CancellationToken ct)
    {
        var org = orgUrl.TrimEnd('/');
        var rows = new List<string>();
        var url = $"{org}/api/data/v9.2/{relativeUrl}";
        for (var page = 1; ; page++)
        {
            using var doc = await Page(org, url, ct);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Array)
                throw new DataverseUnavailableException($"the answer to {relativeUrl} has no value array", transient: false);
            rows.AddRange(value.EnumerateArray().Select(e => e.GetRawText()));
            if (!doc.RootElement.TryGetProperty("@odata.nextLink", out var next) || next.ValueKind != JsonValueKind.String)
                break;
            url = next.GetString()!;
            if (!url.StartsWith(org + "/", StringComparison.OrdinalIgnoreCase))
                throw new DataverseUnavailableException($"the next page link is not under {org}", transient: false);
            if (page == MaxPages)
                throw new DataverseUnavailableException($"{relativeUrl} has more than {MaxPages} pages", transient: false);
        }
        return JsonDocument.Parse("{\"value\":[" + string.Join(",", rows) + "]}");
    }

    private async Task<JsonDocument> Page(string org, string url, CancellationToken ct)
    {
        AccessToken token;
        try
        {
            token = await credential.GetTokenAsync(new TokenRequestContext(new[] { org + "/.default" }), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            throw new DataverseUnavailableException($"no token for {org}/.default ({FirstLine(ex.Message)})", transient: true);
        }
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Add("OData-MaxVersion", "4.0");
        request.Headers.Add("OData-Version", "4.0");
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new DataverseUnavailableException($"timed out after {http.Timeout.TotalSeconds:0} s", transient: true);
        }
        catch (HttpRequestException ex)
        {
            throw new DataverseUnavailableException($"unreachable ({FirstLine(ex.Message)})", transient: true);
        }
        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new DataverseUnavailableException($"HTTP {(int)response.StatusCode} ({response.StatusCode})" + ErrorMessage(body),
                    transient: (int)response.StatusCode is 408 or 429 or >= 500);
            try
            {
                return JsonDocument.Parse(body);
            }
            catch (JsonException)
            {
                throw new DataverseUnavailableException($"HTTP {(int)response.StatusCode} with a body that is not JSON", transient: false);
            }
        }
    }

    /// <summary>Dataverse's own error text, which names the missing privilege or the bad query; capped so a log line stays short.</summary>
    private static string ErrorMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var error) && error.TryGetProperty("message", out var m) && m.GetString() is { Length: > 0 } text)
                return ": " + FirstLine(text.Length > 300 ? text[..300] + "..." : text);
        }
        catch (JsonException) { }
        return "";
    }

    private static string FirstLine(string text)
    {
        var end = text.IndexOfAny(['\r', '\n']);
        return end < 0 ? text : text[..end];
    }
}
