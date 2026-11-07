using System.Text.RegularExpressions;
using BrokerForge.Abstractions;

namespace BrokerForge.Auth.Credentials;

/// <summary>A stored credential and the row version it was read at.</summary>
public sealed record VersionedCredential(UserCredential Credential, string Version);

/// <summary>
/// Per-user credentials keyed by service instance and the user's Entra object ID (HLD D-25, D-27).
/// <see cref="SaveAsync"/> and <see cref="DeleteAsync"/> are the user's own actions on the connect page and always
/// win. A token refresh writes only through the conditional methods, so a refresh that races a disconnect, a
/// reconnect or another replica's refresh never overwrites what happened meanwhile.
/// </summary>
public interface IUserCredentialStore
{
    Task<UserCredential?> GetAsync(string instance, string userObjectId, CancellationToken ct);
    Task<VersionedCredential?> GetVersionedAsync(string instance, string userObjectId, CancellationToken ct);
    Task SaveAsync(string instance, string userObjectId, UserCredential credential, CancellationToken ct);

    /// <summary>Replaces the row only if it still has <paramref name="version"/>; false if it changed or is gone.</summary>
    Task<bool> ReplaceIfUnchangedAsync(string instance, string userObjectId, UserCredential credential, string version, CancellationToken ct);

    /// <summary>Changes the status, keeping the payload, only if the row still has <paramref name="version"/>; false if it changed or is gone.</summary>
    Task<bool> SetStatusIfUnchangedAsync(string instance, string userObjectId, CredentialStatus status, string version, CancellationToken ct);
    Task DeleteAsync(string instance, string userObjectId, CancellationToken ct);
}

/// <summary>
/// The instance part of a credential key. A design instance's name is unique only within its environment, so its
/// credentials are kept under <c>&lt;environment ID, N form&gt;_&lt;instance&gt;</c>; a host-settings instance keeps
/// its bare name, so rows stored before designs existed still match.
/// </summary>
public static class CredentialPartition
{
    public static string For(ServiceInstance instance) => instance.EnvironmentId switch
    {
        null => instance.Name,
        var env when Guid.TryParse(env, out var id) => id.ToString("N") + "_" + instance.Name,
        var env => throw new ArgumentException($"Service instance {instance.Name} has environment ID `{env}`, which is not a GUID.", nameof(instance)),
    };
}

internal static partial class CredentialKeys
{
    [GeneratedRegex("^([0-9a-f]{32}_)?[A-Za-z0-9]+$")]
    private static partial Regex InstanceName();

    /// <summary>The validated (instance, object ID) pair; the object ID in lower-case "D" form so one user has one row.</summary>
    public static (string Instance, string User) Check(string instance, string userObjectId)
    {
        if (!InstanceName().IsMatch(instance))
            throw new ArgumentException("A service instance key is letters and digits, optionally after an environment ID (N form) and an underscore.", nameof(instance));
        if (!Guid.TryParse(userObjectId, out var oid))
            throw new ArgumentException("A user object ID is a GUID.", nameof(userObjectId));
        return (instance, oid.ToString("D"));
    }
}
