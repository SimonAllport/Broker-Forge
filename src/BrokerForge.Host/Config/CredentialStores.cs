using Azure.Core;
using Azure.Data.Tables;
using BrokerForge.Abstractions;
using BrokerForge.Auth.Credentials;
using Microsoft.AspNetCore.DataProtection;

namespace BrokerForge.Host.Config;

/// <summary>
/// The per-user credential store (HLD D-25) and the instance secret store (D-34): the Azure table at Credentials:TableEndpoint. A host with a per-user
/// instance, or with connection designs (any of which may be per-user), and no table refuses to start; the in-memory store is only for tests and local runs that ask for it
/// (Credentials:InMemory), because it is lost on restart and not shared between replicas.
/// </summary>
public static class CredentialStores
{
    public const string DefaultTable = "bfusercredentials";

    /// <summary>The credential table, or null when no endpoint is configured.</summary>
    private static TableClient? Table(IConfiguration configuration, TokenCredential credential)
    {
        var endpoint = configuration["Credentials:TableEndpoint"];
        if (string.IsNullOrWhiteSpace(endpoint))
            return null;
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != "https")
            throw new ConfigurationException("Credentials:TableEndpoint must be the storage account's https table endpoint.");
        if (string.IsNullOrWhiteSpace(configuration["DataProtection:BlobUri"]) || string.IsNullOrWhiteSpace(configuration["DataProtection:KeyId"]))
            throw new ConfigurationException("DataProtection:BlobUri and DataProtection:KeyId are required with Credentials:TableEndpoint: without persisted keys, stored credentials cannot be read after a restart.");
        return new TableClient(uri, configuration["Credentials:TableName"] ?? DefaultTable, credential);
    }

    public static IUserCredentialStore Create(IConfiguration configuration, HostConfigStore files, bool designs, TokenCredential credential, IDataProtectionProvider protection)
    {
        if (Table(configuration, credential) is { } table)
            return new TableUserCredentialStore(table, protection);
        if (configuration.GetValue("Credentials:InMemory", false))
            return new InMemoryUserCredentialStore();
        var perUser = files.PerUserInstances;
        if (perUser.Count > 0)
            throw new ConfigurationException(
                $"Credentials:TableEndpoint is required: {string.Join(", ", perUser)} use per-user modes, and their credentials need a durable store.");
        if (designs)
            throw new ConfigurationException("Credentials:TableEndpoint is required: connection designs can use per-user modes, and their credentials need a durable store.");
        return new InMemoryUserCredentialStore();
    }

    /// <summary>
    /// The instance secret store (HLD D-34), in the same table and under the same rules: connection designs keep their
    /// keys here, so a host with designs and no table refuses to start unless the in-memory store is asked for.
    /// </summary>
    public static IInstanceSecretStore CreateInstanceSecrets(IConfiguration configuration, bool designs, TokenCredential credential, IDataProtectionProvider protection)
    {
        if (Table(configuration, credential) is { } table)
            return new TableInstanceSecretStore(table, protection);
        if (configuration.GetValue("Credentials:InMemory", false))
            return new InMemoryInstanceSecretStore();
        if (designs)
            throw new ConfigurationException("Credentials:TableEndpoint is required: connection designs keep their keys in the host's store, which needs a durable table.");
        return new InMemoryInstanceSecretStore();
    }
}
