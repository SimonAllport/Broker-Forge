using System.Text.RegularExpressions;

namespace BrokerForge.Auth.Credentials;

/// <summary>
/// Instance secrets (spec section 8): the keys a connection design names in <c>secretNames</c>, set by an administrator
/// per environment and read through <c>store:&lt;name&gt;</c> references. Keyed by environment, instance and name,
/// because one instance name can exist in several environments.
/// </summary>
public interface IInstanceSecretStore
{
    Task<string?> GetAsync(string environmentId, string instance, string name, CancellationToken ct);
    Task SetAsync(string environmentId, string instance, string name, string value, CancellationToken ct);
    Task<bool> ExistsAsync(string environmentId, string instance, string name, CancellationToken ct);
    Task DeleteAsync(string environmentId, string instance, string name, CancellationToken ct);
}

/// <summary>For tests and local runs: unencrypted, lost on restart.</summary>
public sealed class InMemoryInstanceSecretStore : IInstanceSecretStore
{
    private readonly Dictionary<(string, string, string), string> _values = new();

    private static (string, string, string) Key(string environmentId, string instance, string name)
    {
        var (env, i, n) = InstanceSecretKeys.Check(environmentId, instance, name);
        return (env.ToString("D"), i, n);
    }

    public Task<string?> GetAsync(string environmentId, string instance, string name, CancellationToken ct)
    {
        lock (_values)
            return Task.FromResult(_values.GetValueOrDefault(Key(environmentId, instance, name)));
    }

    public Task SetAsync(string environmentId, string instance, string name, string value, CancellationToken ct)
    {
        lock (_values)
            _values[Key(environmentId, instance, name)] = value;
        return Task.CompletedTask;
    }

    public Task<bool> ExistsAsync(string environmentId, string instance, string name, CancellationToken ct)
    {
        lock (_values)
            return Task.FromResult(_values.ContainsKey(Key(environmentId, instance, name)));
    }

    public Task DeleteAsync(string environmentId, string instance, string name, CancellationToken ct)
    {
        lock (_values)
            _values.Remove(Key(environmentId, instance, name));
        return Task.CompletedTask;
    }
}

internal static partial class InstanceSecretKeys
{
    [GeneratedRegex("^[A-Za-z0-9]+$")]
    private static partial Regex Plain();

    /// <summary>The validated key: the environment ID as a GUID, then instance and secret name (letters and digits, as <c>store:</c> names are).</summary>
    public static (Guid Environment, string Instance, string Name) Check(string environmentId, string instance, string name)
    {
        if (!Guid.TryParse(environmentId, out var env))
            throw new ArgumentException("An environment ID is a GUID.", nameof(environmentId));
        if (!Plain().IsMatch(instance))
            throw new ArgumentException("A service instance name is letters and digits.", nameof(instance));
        if (!Plain().IsMatch(name))
            throw new ArgumentException("A secret name is letters and digits.", nameof(name));
        return (env, instance, name);
    }
}
