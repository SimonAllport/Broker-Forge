using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace BrokerForge.Host.Connect;

/// <summary>
/// The OAuth state parameter (HLD D-26): who started the connect, for which instance (and, for a connection design,
/// which environment), the PKCE verifier and a nonce that a cookie in the same browser must match. Protected with Data Protection, so it cannot be read or
/// forged; valid for <see cref="Lifetime"/> and once only (its nonce cookie is deleted on use).
/// </summary>
public sealed record ConnectState(string Instance, string UserObjectId, string Verifier, string Nonce, DateTimeOffset IssuedAt, string? EnvironmentId = null)
{
    public const string Purpose = "BrokerForge.ConnectState.v1";
    public const string NonceCookie = "bf_connect_nonce";
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    /// <summary>One cookie per sign-in, named from its nonce, so a second connect tab does not replace the first one's.</summary>
    public string NonceCookieName => NonceCookie + "_" + Challenge(Nonce)[..16];

    public string Protect(IDataProtectionProvider protection) => protection.CreateProtector(Purpose).Protect(JsonSerializer.Serialize(this));

    /// <summary>The state, or null when it was not issued by this host or has been altered.</summary>
    public static ConnectState? Unprotect(IDataProtectionProvider protection, string? value)
    {
        if (string.IsNullOrEmpty(value))
            return null;
        try
        {
            return JsonSerializer.Deserialize<ConnectState>(protection.CreateProtector(Purpose).Unprotect(value));
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or FormatException)
        {
            return null;
        }
    }

    public static string Random(int bytes) => Base64Url(RandomNumberGenerator.GetBytes(bytes));

    public static string Challenge(string verifier) => Base64Url(SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(verifier)));

    private static string Base64Url(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
