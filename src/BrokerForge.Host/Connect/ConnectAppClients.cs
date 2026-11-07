using Azure.Core;
using BrokerForge.Abstractions;
using BrokerForge.Auth.PerUser;

namespace BrokerForge.Host.Connect;

/// <summary>
/// The BrokerForge Connect app authenticating with the host's managed identity as a federated credential
/// (HLD D-26): no client secret exists anywhere.
/// </summary>
public sealed class ManagedIdentityConnectAppClient(string clientId, TokenCredential managedIdentity) : IConnectAppClient
{
    private const string ExchangeScope = "api://AzureADTokenExchange/.default";

    public string ClientId => clientId;

    public async Task<IReadOnlyList<KeyValuePair<string, string>>> ClientAuthAsync(CancellationToken ct)
    {
        var assertion = await managedIdentity.GetTokenAsync(new TokenRequestContext(new[] { ExchangeScope }), ct);
        return new[]
        {
            new KeyValuePair<string, string>("client_assertion_type", "urn:ietf:params:oauth:client-assertion-type:jwt-bearer"),
            new KeyValuePair<string, string>("client_assertion", assertion.Token),
        };
    }
}

/// <summary>The fallback when a federated credential is not available: a client secret named by a secret reference.</summary>
public sealed class SecretConnectAppClient(string clientId, SecretRef secret, ISecretResolver secrets) : IConnectAppClient
{
    public string ClientId => clientId;

    public async Task<IReadOnlyList<KeyValuePair<string, string>>> ClientAuthAsync(CancellationToken ct) =>
        new[] { new KeyValuePair<string, string>("client_secret", await secrets.GetAsync(secret, ct)) };
}
