using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BrokerForge.Auth.Credentials;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace BrokerForge.Contract.Tests;

/// <summary>
/// The phase 3 per-user source in the mock (tools/mock-api/tickets.js): its dummy client secret and user
/// passwords, a throwaway RSA key standing in for Entra, and helpers that do the browser half of a sign-in.
/// None of these values is a real secret.
/// </summary>
public static class Tickets
{
    public const string ClientId = "bf-tickets";
    public const string ClientSecret = "bf-test-tk-client-secret";
    public const string AlicePassword = "bf-test-alice-pw";
    public const string BobPassword = "bf-test-bob-pw";
    public const string ConnectBase = "https://bf-host.test";
    public const string Redirect = ConnectBase + "/connect/callback";
    public const string EntraAudience = "api://bf-test-tickets";
    public const string AliceOid = "a11ce000-0000-0000-0000-000000000001";
    public const string BobOid = "b0b00000-0000-0000-0000-000000000002";
    public static readonly string[] AliceTickets = Enumerable.Range(1, 7).Select(i => $"TK-{i}").ToArray();
    public static readonly string[] BobTickets = Enumerable.Range(8, 5).Select(i => $"TK-{i}").ToArray();

    private static readonly RsaSecurityKey EntraKey = new(RSA.Create(2048)) { KeyId = "bf-test-entra" };
    private static readonly Lazy<string> JwksFile = new(() =>
    {
        var p = EntraKey.Rsa.ExportParameters(false);
        var jwks = new JsonObject
        {
            ["keys"] = new JsonArray(new JsonObject
            {
                ["kty"] = "RSA", ["kid"] = EntraKey.KeyId, ["use"] = "sig",
                ["n"] = Base64UrlEncoder.Encode(p.Modulus), ["e"] = Base64UrlEncoder.Encode(p.Exponent),
            }),
        };
        var file = Path.Combine(Path.GetTempPath(), $"bf-test-jwks-{Guid.NewGuid():N}.json");
        File.WriteAllText(file, jwks.ToJsonString());
        return file;
    });

    public static IEnumerable<(string, string)> MockEnvironment() => new[]
    {
        ("MOCK_TK_CLIENT_SECRET", ClientSecret), ("MOCK_TK_ALICE_PASSWORD", AlicePassword), ("MOCK_TK_BOB_PASSWORD", BobPassword),
        ("MOCK_TK_REDIRECT_URIS", Redirect), ("MOCK_ENTRA_AUDIENCE", EntraAudience), ("MOCK_ENTRA_JWKS_FILE", JwksFile.Value),
        ("MOCK_ENTRA_USERS", $"{AliceOid}:alice,{BobOid}:bob"),
    };

    /// <summary>An Entra-style delegated token for the Tickets API, signed with the test key.</summary>
    public static string EntraToken(string oid, TimeSpan? lifetime = null) =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Audience = EntraAudience,
            Expires = DateTime.UtcNow + (lifetime ?? TimeSpan.FromHours(1)),
            Claims = new Dictionary<string, object> { ["oid"] = oid, ["scp"] = "user_impersonation" },
            SigningCredentials = new SigningCredentials(EntraKey, SecurityAlgorithms.RsaSha256),
        });

    public static string Password(string user) => user == "alice" ? AlicePassword : BobPassword;

    /// <summary>Signs in on the mock's authorize page and redeems the code, as a browser and the connect page would.</summary>
    public static async Task<(string Access, string Refresh, int ExpiresIn)> SignInAsync(string mockBase, string user)
    {
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        var verifier = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        var form = new Dictionary<string, string>
        {
            ["response_type"] = "code", ["client_id"] = ClientId, ["redirect_uri"] = Redirect, ["state"] = "s",
            ["code_challenge"] = Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))), ["code_challenge_method"] = "S256",
            ["scope"] = "tickets.read", ["user"] = user, ["password"] = Password(user),
        };
        using var signedIn = await http.PostAsync(mockBase + "/tk/oauth/authorize", new FormUrlEncodedContent(form));
        if (signedIn.StatusCode != HttpStatusCode.Redirect)
            throw new InvalidOperationException($"mock sign-in returned {(int)signedIn.StatusCode}");
        var code = System.Web.HttpUtility.ParseQueryString(signedIn.Headers.Location!.Query)["code"]!;
        using var tokens = await http.PostAsync(mockBase + "/tk/oauth/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = Redirect, ["code_verifier"] = verifier,
            ["client_id"] = ClientId, ["client_secret"] = ClientSecret,
        }));
        tokens.EnsureSuccessStatusCode();
        var json = JsonDocument.Parse(await tokens.Content.ReadAsStringAsync()).RootElement;
        return (json.GetProperty("access_token").GetString()!, json.GetProperty("refresh_token").GetString()!, json.GetProperty("expires_in").GetInt32());
    }

    public static UserCredential OAuthCredential((string Access, string Refresh, int ExpiresIn) t, DateTimeOffset? expires = null) =>
        new(CredentialKind.OAuth, CredentialStatus.Connected, t.Access, t.Refresh, expires ?? DateTimeOffset.UtcNow.AddSeconds(t.ExpiresIn), null, null, DateTimeOffset.UtcNow);

    public static async Task<int> DataRequestsAsync(string mockBase)
    {
        using var http = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, mockBase + "/__tk/stats");
        request.Headers.Add("X-Api-Key", MockApiFixture.ApiKey);
        using var response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("dataRequests").GetInt32();
    }

    /// <summary>Host settings for TicketsAPI (OAuth) and ext_ticket over the mock; edits apply last (null removes).</summary>
    public static Dictionary<string, string?> HostSettings(string mockBase, params (string Key, string? Value)[] edits)
    {
        var s = new Dictionary<string, string?>
        {
            ["Connect:PublicBaseUrl"] = ConnectBase,
            ["Connect:ClientId"] = "connect-test-client",
            ["Connect:ClientSecretRef"] = "env:BF_TEST_CONNECT_SECRET",
            ["Credentials:InMemory"] = "true",
            ["Instances:TicketsAPI:BrokerType"] = "rest",
            ["Instances:TicketsAPI:BaseUrl"] = mockBase,
            ["Instances:TicketsAPI:Auth:Mode"] = "OAuth",
            ["Instances:TicketsAPI:Auth:OAuthResource"] = "Tickets",
            ["OAuthResources:Tickets:AuthorizeUrl"] = mockBase + "/tk/oauth/authorize",
            ["OAuthResources:Tickets:TokenUrl"] = mockBase + "/tk/oauth/token",
            ["OAuthResources:Tickets:Scopes:0"] = "tickets.read",
            ["OAuthResources:Tickets:ClientId"] = ClientId,
            ["OAuthResources:Tickets:ClientSecretRef"] = "env:BF_TEST_TK_CLIENT_SECRET",
            ["ObjectMaps:ext_ticket:ServiceInstance"] = "TicketsAPI",
            ["ObjectMaps:ext_ticket:KeyStrategy"] = "reversible",
            ["ObjectMaps:ext_ticket:KeyKind"] = "String",
            ["ObjectMaps:ext_ticket:Operations:RetrieveMultiple"] = "listTickets",
            ["ObjectMaps:ext_ticket:Operations:Retrieve"] = "getTicket",
            ["ObjectMaps:ext_ticket:Operations:Create"] = "createTicket",
        };
        Environment.SetEnvironmentVariable("BF_TEST_TK_CLIENT_SECRET", ClientSecret);
        Environment.SetEnvironmentVariable("BF_TEST_CONNECT_SECRET", "bf-test-connect-secret");
        foreach (var (k, v) in edits)
            if (v == null) s.Remove(k); else s[k] = v;
        return s;
    }
}
