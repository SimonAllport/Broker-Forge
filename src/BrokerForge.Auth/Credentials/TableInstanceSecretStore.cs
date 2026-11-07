using System.Security.Cryptography;
using Azure;
using Azure.Data.Tables;
using Microsoft.AspNetCore.DataProtection;

namespace BrokerForge.Auth.Credentials;

/// <summary>
/// Instance secrets in the credential table (HLD D-34): PartitionKey = <c>instancesecret_&lt;environment ID, N form&gt;</c>,
/// RowKey = <c>&lt;instance&gt;_&lt;name&gt;</c> (a name has no underscore, so the key is unambiguous). The value is one
/// Data Protection payload; the row holds nothing readable. The partition prefix keeps these rows apart from user
/// credentials, whose partitions are instance keys.
/// </summary>
public sealed class TableInstanceSecretStore(TableClient table, IDataProtectionProvider protection) : IInstanceSecretStore
{
    public const string Purpose = "BrokerForge.InstanceSecret.v1";
    private readonly IDataProtector _protector = protection.CreateProtector(Purpose);

    private static (string Pk, string Rk) Keys(string environmentId, string instance, string name)
    {
        var (env, i, n) = InstanceSecretKeys.Check(environmentId, instance, name);
        return ($"instancesecret_{env:N}", $"{i}_{n}");
    }

    public async Task<string?> GetAsync(string environmentId, string instance, string name, CancellationToken ct)
    {
        var (pk, rk) = Keys(environmentId, instance, name);
        var row = await table.GetEntityIfExistsAsync<TableEntity>(pk, rk, cancellationToken: ct);
        if (!row.HasValue)
            return null;
        try
        {
            return _protector.Unprotect(row.Value!.GetString("Payload"));
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException or InvalidOperationException)
        {
            // The key ring that protected the value is gone, or the row is damaged. No detail: it could hold ciphertext.
            throw new CredentialUnreadableException($"The stored secret `{name}` of {instance} could not be read ({ex.GetType().Name}); an administrator must set it again.");
        }
    }

    public async Task SetAsync(string environmentId, string instance, string name, string value, CancellationToken ct)
    {
        var (pk, rk) = Keys(environmentId, instance, name);
        await table.UpsertEntityAsync(new TableEntity(pk, rk) { ["Payload"] = _protector.Protect(value) }, TableUpdateMode.Replace, ct);
    }

    public async Task<bool> ExistsAsync(string environmentId, string instance, string name, CancellationToken ct)
    {
        var (pk, rk) = Keys(environmentId, instance, name);
        return (await table.GetEntityIfExistsAsync<TableEntity>(pk, rk, select: new[] { "RowKey" }, cancellationToken: ct)).HasValue;
    }

    public async Task DeleteAsync(string environmentId, string instance, string name, CancellationToken ct)
    {
        var (pk, rk) = Keys(environmentId, instance, name);
        await table.DeleteEntityAsync(pk, rk, ETag.All, ct);
    }
}
