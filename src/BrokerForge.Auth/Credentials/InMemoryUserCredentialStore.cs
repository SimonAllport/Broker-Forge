namespace BrokerForge.Auth.Credentials;

/// <summary>For tests and local runs: unencrypted, lost on restart.</summary>
public sealed class InMemoryUserCredentialStore : IUserCredentialStore
{
    private readonly Dictionary<(string, string), VersionedCredential> _rows = new();

    private static string NewVersion() => Guid.NewGuid().ToString("N");

    public Task<UserCredential?> GetAsync(string instance, string userObjectId, CancellationToken ct) =>
        Task.FromResult(Read(instance, userObjectId)?.Credential);

    public Task<VersionedCredential?> GetVersionedAsync(string instance, string userObjectId, CancellationToken ct) =>
        Task.FromResult(Read(instance, userObjectId));

    private VersionedCredential? Read(string instance, string userObjectId)
    {
        var key = CredentialKeys.Check(instance, userObjectId);
        lock (_rows)
            return _rows.GetValueOrDefault(key);
    }

    public Task SaveAsync(string instance, string userObjectId, UserCredential credential, CancellationToken ct)
    {
        var key = CredentialKeys.Check(instance, userObjectId);
        lock (_rows)
            _rows[key] = new VersionedCredential(credential, NewVersion());
        return Task.CompletedTask;
    }

    public Task<bool> ReplaceIfUnchangedAsync(string instance, string userObjectId, UserCredential credential, string version, CancellationToken ct) =>
        Task.FromResult(Swap(instance, userObjectId, version, _ => credential));

    public Task<bool> SetStatusIfUnchangedAsync(string instance, string userObjectId, CredentialStatus status, string version, CancellationToken ct) =>
        Task.FromResult(Swap(instance, userObjectId, version, row => row with { Status = status }));

    private bool Swap(string instance, string userObjectId, string version, Func<UserCredential, UserCredential> change)
    {
        var key = CredentialKeys.Check(instance, userObjectId);
        lock (_rows)
        {
            if (!_rows.TryGetValue(key, out var row) || row.Version != version)
                return false;
            _rows[key] = new VersionedCredential(change(row.Credential), NewVersion());
            return true;
        }
    }

    public Task DeleteAsync(string instance, string userObjectId, CancellationToken ct)
    {
        var key = CredentialKeys.Check(instance, userObjectId);
        lock (_rows)
            _rows.Remove(key);
        return Task.CompletedTask;
    }
}
