using BrokerForge.Abstractions;

namespace BrokerForge.Host.Execution;

/// <summary>
/// The HMAC key for page tokens, read once at startup from the secret reference in Host:PageTokenKeyRef.
/// Every replica must share it, so it lives in Key Vault, not in process memory.
/// </summary>
public sealed record PageTokenKey(byte[] Value)
{
    public const int MinimumLength = 32;

    public static async Task<PageTokenKey> LoadAsync(IConfiguration configuration, ISecretResolver secrets)
    {
        var reference = configuration["Host:PageTokenKeyRef"];
        if (string.IsNullOrWhiteSpace(reference))
            throw new ConfigurationException("Host configuration is invalid:\n - Host:PageTokenKeyRef is required (kv:<secret uri> or env:<VARIABLE>)");
        var value = await secrets.GetAsync(SecretRef.Parse(reference), CancellationToken.None);
        if (value.Length < MinimumLength)
            throw new ConfigurationException($"Host configuration is invalid:\n - the secret named by Host:PageTokenKeyRef must be at least {MinimumLength} characters");
        return new PageTokenKey(System.Text.Encoding.UTF8.GetBytes(value));
    }
}
