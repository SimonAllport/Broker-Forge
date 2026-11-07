using System.Text.Json;
using System.Text.RegularExpressions;
using BrokerForge.Abstractions;

namespace BrokerForge.Auth.PerUser;

public sealed record TokenSet(string AccessToken, string? RefreshToken, DateTimeOffset ExpiresAt);

/// <summary>The authorisation server refused a refresh token (invalid_grant): the user must connect again.</summary>
public sealed class RefreshRejectedException(string message) : Exception(message);

/// <summary>
/// Calls an OAuth resource's token endpoint (HLD D-28) for the code redemption and refreshes. Failure messages
/// name the token URL, the status and the OAuth error code only: the body can echo codes and tokens.
/// </summary>
public sealed partial class TokenEndpointClient(HttpClient http, ISecretResolver secrets, IConnectAppClient? connectApp, TimeProvider time)
{
    /// <summary>When a token response has no expires_in, the token is treated as good for this long.</summary>
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(5);

    [GeneratedRegex("^[a-z_]{1,64}$")]
    private static partial Regex ErrorCode();

    public Task<TokenSet> RedeemCodeAsync(OAuthResource resource, ServiceInstance instance, string code, string verifier, string redirectUri, CancellationToken ct) =>
        PostAsync(resource, instance, new[]
        {
            Pair("grant_type", "authorization_code"), Pair("code", code), Pair("code_verifier", verifier), Pair("redirect_uri", redirectUri),
        }, ct);

    public Task<TokenSet> RefreshAsync(OAuthResource resource, ServiceInstance instance, string refreshToken, CancellationToken ct) =>
        PostAsync(resource, instance, new[] { Pair("grant_type", "refresh_token"), Pair("refresh_token", refreshToken) }, ct);

    private static KeyValuePair<string, string> Pair(string k, string v) => new(k, v);

    private async Task<TokenSet> PostAsync(OAuthResource resource, ServiceInstance instance, KeyValuePair<string, string>[] grant, CancellationToken ct)
    {
        var form = new List<KeyValuePair<string, string>>(grant) { Pair("scope", string.Join(' ', resource.Scopes)) };
        if (resource.UsesConnectApp)
        {
            // The connect app's credential (and the user's code) goes to Entra and nowhere else, whatever configured the URL.
            if (!(Uri.TryCreate(resource.TokenUrl, UriKind.Absolute, out var url) && url.Scheme == Uri.UriSchemeHttps
                  && url.IsDefaultPort && url.UserInfo.Length == 0 && url.Host == "login.microsoftonline.com"))
                throw new ConfigurationException($"OAuthResources:{resource.Name} uses the connect app, whose credential is sent only to https://login.microsoftonline.com/; its TokenUrl is {resource.TokenUrl}.");
            if (connectApp == null)
                throw new ConfigurationException($"OAuthResources:{resource.Name} uses the connect app, but the connect app is not configured (Connect settings).");
            form.Add(Pair("client_id", connectApp.ClientId));
            form.AddRange(await connectApp.ClientAuthAsync(ct));
        }
        else
        {
            form.Add(Pair("client_id", resource.ClientId!));
            if (resource.ClientSecretRef != null)
                form.Add(Pair("client_secret", await secrets.GetAsync(SecretRef.Parse(resource.ClientSecretRef), instance, ct)));
        }

        HttpResponseMessage response;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, resource.TokenUrl) { Content = new FormUrlEncodedContent(form) };
            // A design instance's token URL goes through the design egress rule (DesignEgress).
            DesignEgress.Mark(request, instance);
            response = await http.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new CredentialUnavailableException($"The token endpoint {resource.TokenUrl} could not be reached ({ex.HttpRequestError}).");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new CredentialUnavailableException($"The token endpoint {resource.TokenUrl} did not answer within {http.Timeout.TotalSeconds:0} s.");
        }
        using (response)
        {
            JsonElement body = default;
            try
            {
                body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)).RootElement;
            }
            catch (JsonException)
            {
            }
            var error = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String
                && ErrorCode().IsMatch(e.GetString()!) ? e.GetString() : null;
            if (!response.IsSuccessStatusCode)
            {
                if (error == "invalid_grant")
                    throw new RefreshRejectedException($"The token endpoint {resource.TokenUrl} refused the grant (invalid_grant).");
                throw new CredentialUnavailableException($"The token endpoint {resource.TokenUrl} returned {(int)response.StatusCode}{(error == null ? "" : " " + error)}.");
            }
            if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("access_token", out var at) || at.ValueKind != JsonValueKind.String)
                throw new CredentialUnavailableException($"The token endpoint {resource.TokenUrl} returned {(int)response.StatusCode} without an access_token.");
            var refresh = body.TryGetProperty("refresh_token", out var rt) && rt.ValueKind == JsonValueKind.String ? rt.GetString() : null;
            return new TokenSet(at.GetString()!, refresh, time.GetUtcNow() + Lifetime(body, resource));
        }
    }

    /// <summary>
    /// <c>expires_in</c> in seconds: a JSON number, or a string of digits as some servers send it. Absent means
    /// <see cref="DefaultLifetime"/>; anything else is refused rather than guessed.
    /// </summary>
    private static TimeSpan Lifetime(JsonElement body, OAuthResource resource)
    {
        if (!body.TryGetProperty("expires_in", out var ei))
            return DefaultLifetime;
        var seconds = ei.ValueKind switch
        {
            JsonValueKind.Number when ei.TryGetInt32(out var n) => n,
            JsonValueKind.String when int.TryParse(ei.GetString(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) => n,
            _ => 0,
        };
        return seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : throw new CredentialUnavailableException($"The token endpoint {resource.TokenUrl} returned an expires_in that is not a positive number of seconds.");
    }
}
