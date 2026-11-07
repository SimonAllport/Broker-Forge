using System.Text.Json;
using BrokerForge.Abstractions;
using BrokerForge.Auth.PerUser;

namespace BrokerForge.Host.Admin;

/// <summary>Redeems the role page's sign-in code for the administrator's own Dataverse token (HLD D-36).</summary>
public interface IAdminTokenExchange
{
    /// <summary>A delegated token for <c>{orgUrl}/user_impersonation</c>. Never stored or logged.</summary>
    Task<string> RedeemAsync(string orgUrl, string code, string verifier, string redirectUri, CancellationToken ct);
}

/// <summary>
/// The tenant's Entra token endpoint with the host's connect app as the client (its managed identity or fallback secret
/// authenticates it, as for the connect page). Only the access token is read from the reply.
/// </summary>
public sealed class EntraAdminTokenExchange(string tenantId, Func<IConnectAppClient?> connectApp, HttpClient http) : IAdminTokenExchange
{
    public async Task<string> RedeemAsync(string orgUrl, string code, string verifier, string redirectUri, CancellationToken ct)
    {
        var app = connectApp() ?? throw new ConfigurationException("Connect:ClientId is not set, so an administrator cannot sign in for the role step.");
        var form = new List<KeyValuePair<string, string>>
        {
            new("grant_type", "authorization_code"),
            new("client_id", app.ClientId),
            new("code", code),
            new("redirect_uri", redirectUri),
            new("code_verifier", verifier),
            new("scope", AdminEndpoints.RoleScope(orgUrl)),
        };
        form.AddRange(await app.ClientAuthAsync(ct));
        using var response = await http.PostAsync($"https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/token", new FormUrlEncodedContent(form), ct);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (!response.IsSuccessStatusCode || !body.RootElement.TryGetProperty("access_token", out var token) || token.GetString() is not { Length: > 0 } value)
        {
            var error = body.RootElement.TryGetProperty("error", out var e) ? e.GetString() : null;
            throw new ConfigurationException($"Entra did not issue a Dataverse token for the role step (HTTP {(int)response.StatusCode}{(error is { Length: <= 64 } ? ", " + error : "")}).");
        }
        return value;
    }
}
