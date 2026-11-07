using System.Security.Cryptography;
using System.Text.Json;
using Azure;
using Azure.Data.Tables;
using Microsoft.AspNetCore.DataProtection;

namespace BrokerForge.Auth.Credentials;

/// <summary>
/// Azure Table Storage store (HLD D-25): PartitionKey = service instance, RowKey = user object ID. Kind, status
/// and times are plain columns; tokens and the SSO credential are one Data Protection payload.
/// </summary>
public sealed class TableUserCredentialStore(TableClient table, IDataProtectionProvider protection) : IUserCredentialStore
{
    public const string Purpose = "BrokerForge.UserCredential.v1";
    private readonly IDataProtector _protector = protection.CreateProtector(Purpose);

    private sealed record Secrets(string? AccessToken, string? RefreshToken, string? Username, string? Secret);

    public async Task<UserCredential?> GetAsync(string instance, string userObjectId, CancellationToken ct) =>
        (await GetVersionedAsync(instance, userObjectId, ct))?.Credential;

    public async Task<VersionedCredential?> GetVersionedAsync(string instance, string userObjectId, CancellationToken ct)
    {
        var (pk, rk) = CredentialKeys.Check(instance, userObjectId);
        var row = await table.GetEntityIfExistsAsync<TableEntity>(pk, rk, cancellationToken: ct);
        if (!row.HasValue)
            return null;
        var e = row.Value!;
        try
        {
            var secrets = JsonSerializer.Deserialize<Secrets>(_protector.Unprotect(e.GetString("Payload")))!;
            return new VersionedCredential(new UserCredential(
                Enum.Parse<CredentialKind>(e.GetString("Kind")),
                Enum.Parse<CredentialStatus>(e.GetString("Status")),
                secrets.AccessToken, secrets.RefreshToken, e.GetDateTimeOffset("ExpiresAt"), secrets.Username, secrets.Secret,
                e.GetDateTimeOffset("ConnectedAt")!.Value), e.ETag.ToString());
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or ArgumentException or InvalidOperationException or NullReferenceException)
        {
            // The key ring that protected the payload is gone, or the row is damaged. No detail: it could hold ciphertext.
            throw new CredentialUnreadableException($"The stored credential for {pk} user {rk} could not be read ({ex.GetType().Name}); the user must connect again.");
        }
    }

    public async Task SaveAsync(string instance, string userObjectId, UserCredential credential, CancellationToken ct)
    {
        var (pk, rk) = CredentialKeys.Check(instance, userObjectId);
        await table.UpsertEntityAsync(Entity(pk, rk, credential), TableUpdateMode.Replace, ct);
    }

    public async Task<bool> ReplaceIfUnchangedAsync(string instance, string userObjectId, UserCredential credential, string version, CancellationToken ct)
    {
        var (pk, rk) = CredentialKeys.Check(instance, userObjectId);
        return await IfUnchanged(() => table.UpdateEntityAsync(Entity(pk, rk, credential), new ETag(version), TableUpdateMode.Replace, ct));
    }

    public async Task<bool> SetStatusIfUnchangedAsync(string instance, string userObjectId, CredentialStatus status, string version, CancellationToken ct)
    {
        var (pk, rk) = CredentialKeys.Check(instance, userObjectId);
        return await IfUnchanged(() => table.UpdateEntityAsync(new TableEntity(pk, rk) { ["Status"] = status.ToString() }, new ETag(version), TableUpdateMode.Merge, ct));
    }

    /// <summary>An update with If-Match: 404 (deleted) and 412 (changed since it was read) mean nothing was written.</summary>
    private static async Task<bool> IfUnchanged(Func<Task<Response>> update)
    {
        try
        {
            await update();
            return true;
        }
        catch (RequestFailedException ex) when (ex.Status is 404 or 412)
        {
            return false;
        }
    }

    private TableEntity Entity(string pk, string rk, UserCredential credential) => new(pk, rk)
    {
        ["Kind"] = credential.Kind.ToString(),
        ["Status"] = credential.Status.ToString(),
        ["ExpiresAt"] = credential.ExpiresAt,
        ["ConnectedAt"] = credential.ConnectedAt,
        ["Payload"] = _protector.Protect(JsonSerializer.Serialize(new Secrets(credential.AccessToken, credential.RefreshToken, credential.Username, credential.Secret))),
    };

    public async Task DeleteAsync(string instance, string userObjectId, CancellationToken ct)
    {
        var (pk, rk) = CredentialKeys.Check(instance, userObjectId);
        await table.DeleteEntityAsync(pk, rk, ETag.All, ct);
    }
}
