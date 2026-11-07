using System.Collections.Concurrent;
using Azure;
using Azure.Core;
using Azure.Security.KeyVault.Secrets;
using BrokerForge.Abstractions;
using BrokerForge.Auth.Credentials;

namespace BrokerForge.Auth;

/// <summary>
/// Routes <c>kv:</c> to Key Vault, <c>env:</c> to environment variables and <c>store:</c> to the instance secret store.
/// <c>env:</c> works only when Secrets:AllowEnvironment is true (tests, local runs). <c>store:</c> needs the instance,
/// because its value is kept per environment and instance.
/// </summary>
public sealed class SecretResolverRouter(ISecretResolver keyVault, ISecretResolver environment, bool allowEnvironment, IInstanceSecretStore? instanceSecrets = null)
    : ISecretResolver
{
    public Task<string> GetAsync(SecretRef reference, CancellationToken ct) => reference.Scheme switch
    {
        "kv" => keyVault.GetAsync(reference, ct),
        "env" when allowEnvironment => environment.GetAsync(reference, ct),
        "env" => throw new CredentialUnavailableException($"Secret reference {reference} uses env:, which is disabled (Secrets:AllowEnvironment is false)."),
        "store" => throw new ConfigurationException($"Secret reference {reference} is an instance secret; it can only be read for its service instance."),
        _ => throw new CredentialUnavailableException($"Secret reference scheme `{reference.Scheme}` is not supported."),
    };

    public async Task<string> GetAsync(SecretRef reference, ServiceInstance instance, CancellationToken ct)
    {
        if (reference.Scheme != "store")
            return await GetAsync(reference, ct);
        if (instance.EnvironmentId is not { } environmentId)
            throw new ConfigurationException($"Service instance {instance.Name} names secret {reference}, but store: secrets belong to connection designs and it is configured in host settings.");
        var store = instanceSecrets ?? throw new ConfigurationException($"Service instance {instance.Name} names secret {reference}, but this host has no instance secret store.");
        return await store.GetAsync(environmentId, instance.Name, reference.Location, ct)
            ?? throw new ConfigurationException($"Service instance {instance.Name} needs its key `{reference.Location}` in this environment: an administrator sets it on the BrokerForge secret page.");
    }
}

public sealed class EnvironmentSecretResolver : ISecretResolver
{
    public Task<string> GetAsync(SecretRef reference, CancellationToken ct) =>
        Environment.GetEnvironmentVariable(reference.Location) is { Length: > 0 } value
            ? Task.FromResult(value)
            : throw new CredentialUnavailableException($"Environment variable {reference.Location} named by {reference} is not set.");
}

/// <summary>Reads <c>kv:https://&lt;vault&gt;.vault.azure.net/secrets/&lt;name&gt;[/&lt;version&gt;]</c> with the host identity. Values are cached for five minutes.</summary>
public sealed class KeyVaultSecretResolver(TokenCredential credential) : ISecretResolver
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<Uri, SecretClient> _clients = new();
    private readonly ConcurrentDictionary<string, (string Value, DateTimeOffset Until)> _cache = new();

    public async Task<string> GetAsync(SecretRef reference, CancellationToken ct)
    {
        if (_cache.TryGetValue(reference.Location, out var hit) && hit.Until > DateTimeOffset.UtcNow)
            return hit.Value;

        KeyVaultSecretIdentifier id;
        try
        {
            id = new KeyVaultSecretIdentifier(new Uri(reference.Location));
        }
        catch (Exception ex) when (ex is ArgumentException or UriFormatException)
        {
            throw new CredentialUnavailableException($"Secret reference {reference} is not a Key Vault secret URI.", ex);
        }

        var client = _clients.GetOrAdd(id.VaultUri, uri => new SecretClient(uri, credential));
        try
        {
            var secret = await client.GetSecretAsync(id.Name, id.Version, ct);
            _cache[reference.Location] = (secret.Value.Value, DateTimeOffset.UtcNow + Lifetime);
            return secret.Value.Value;
        }
        catch (RequestFailedException ex)
        {
            throw new CredentialUnavailableException($"Key Vault returned {ex.Status} for {reference}.", ex);
        }
    }
}
