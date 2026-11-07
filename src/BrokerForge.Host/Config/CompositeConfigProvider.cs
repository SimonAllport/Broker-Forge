using System.Collections.Concurrent;
using BrokerForge.Abstractions;
using BrokerForge.Connections;

namespace BrokerForge.Host.Config;

/// <summary>
/// Builds one store per environment from the files and that environment's designs, on first use and again after
/// <c>ttl</c> or <see cref="Invalidate"/>. Without a design source every environment gets <see cref="Files"/>.
/// <list type="bullet">
/// <item>A design that cannot be read or validated, or that clashes with a file instance or another design, is left
/// out with a failure text (<see cref="HostConfigStore.DesignFailures"/>); the files and the other designs still serve,
/// and a request only a missing design could resolve gets a <see cref="ConfigurationException"/> with the texts
/// (final review I1). A file instance is never the one left out.</item>
/// <item>When the source cannot be read at all, the store is the files alone with the read failure as its text. For a
/// transient reason (<see cref="DesignSourceUnavailableException"/>) the last good store is served instead, with a
/// warning on every request, until one more <c>ttl</c> has passed after it expired; any other read failure (such as
/// 400 or 403) is never covered by an older store.</item>
/// <item>A store with failures is kept for <see cref="FailureCacheTime"/> (or <c>ttl</c> if shorter), so a broken or
/// unreachable environment is not read on every request and a fixed design is picked up soon after.</item>
/// </list>
/// </summary>
/// <param name="designTrust">What the host allows each environment's designs (<see cref="DesignTrust"/>); nothing when null.</param>
public sealed class CompositeConfigProvider(HostConfigStore files, IDesignSource? designs, TimeProvider time, TimeSpan ttl, ILogger? log = null,
    Func<string, DesignTrust>? designTrust = null) : IHostConfigProvider
{
    /// <summary>How long a store with failures is reused before the source is read again.</summary>
    public static readonly TimeSpan FailureCacheTime = TimeSpan.FromSeconds(30);

    /// <param name="Unavailable">The transient read failure, when the source could not be read; the last good store may cover it.</param>
    private sealed record Built(HostConfigStore Store, string? Unavailable);

    private sealed record Entry(Task<Built> Build, DateTimeOffset Started);

    private sealed record Good(HostConfigStore Store, DateTimeOffset Expires);

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Good> _lastGood = new(StringComparer.Ordinal);
    private readonly TimeSpan _failureTtl = ttl < FailureCacheTime ? ttl : FailureCacheTime;

    public HostConfigStore Files => files;

    public async Task<HostConfigStore> ForEnvironmentAsync(string environmentId, CancellationToken ct)
    {
        if (designs == null)
            return files;
        var key = Key(environmentId);
        Entry entry;
        while (true)
        {
            var now = time.GetUtcNow();
            if (_entries.TryGetValue(key, out var cached) && Current(cached, now))
            {
                entry = cached;
                break;
            }
            // Concurrent first calls share one build; the loser of the swap waits for the winner's.
            var fresh = new Entry(Build(key, now), now);
            if (cached == null ? _entries.TryAdd(key, fresh) : _entries.TryUpdate(key, fresh, cached))
            {
                entry = fresh;
                break;
            }
        }
        var built = await entry.Build.WaitAsync(ct);
        if (built.Unavailable != null && _lastGood.TryGetValue(key, out var good) && time.GetUtcNow() < good.Expires + ttl)
        {
            log?.LogWarning("{Reason} Serving the connection designs read before {Expired:O}, at most until {Until:O}.", built.Unavailable, good.Expires, good.Expires + ttl);
            return good.Store;
        }
        return built.Store;
    }

    public void Invalidate(string environmentId) => _entries.TryRemove(Key(environmentId), out _);

    /// <summary>A running build is always shared; a clean one lasts <c>ttl</c>, one with failures (or that threw) the failure time.</summary>
    private bool Current(Entry entry, DateTimeOffset now) =>
        !entry.Build.IsCompleted
        || entry.Started + (entry.Build.IsCompletedSuccessfully && entry.Build.Result.Store.DesignFailures.Count == 0 ? ttl : _failureTtl) > now;

    private async Task<Built> Build(string environmentId, DateTimeOffset started)
    {
        DesignLoad loaded;
        try
        {
            // Not the caller's token: the build is shared, and one caller giving up must not fail the others.
            loaded = await designs!.LoadAsync(environmentId, CancellationToken.None);
        }
        catch (DesignSourceUnavailableException ex)
        {
            return new Built(Assemble(environmentId, new DesignLoad(Array.Empty<(ConnectionDesign, EnvironmentSettings)>(), new[] { ex.Message })), ex.Message);
        }
        catch (ConfigurationException ex)
        {
            return new Built(Assemble(environmentId, new DesignLoad(Array.Empty<(ConnectionDesign, EnvironmentSettings)>(), new[] { ex.Message })), null);
        }
        var store = Assemble(environmentId, loaded);
        _lastGood[environmentId] = new Good(store, started + ttl);
        return new Built(store, null);
    }

    /// <summary>
    /// The files plus every design that is valid beside them. Designs are added one at a time in name order, and one
    /// whose addition makes the store invalid is left out with that store's problems; two designs that map one table
    /// or share a name are both left out, so neither is picked.
    /// </summary>
    private HostConfigStore Assemble(string environmentId, DesignLoad loaded)
    {
        var scopes = designTrust?.Invoke(environmentId) ?? DesignTrust.None;
        var failures = new List<string>(loaded.Failures);
        var candidates = loaded.Designs.OrderBy(d => d.Design.Instance, StringComparer.Ordinal).ToList();
        foreach (var twice in candidates.GroupBy(d => d.Design.Instance, StringComparer.Ordinal).Where(g => g.Count() > 1))
            failures.Add($"Instance {twice.Key} has two connection designs in environment {environmentId}");
        candidates.RemoveAll(d => candidates.Count(o => o.Design.Instance == d.Design.Instance) > 1);
        foreach (var table in candidates.Where(d => d.Design.ObjectMap != null).GroupBy(d => d.Design.ObjectMap!.Table, StringComparer.Ordinal).Where(g => g.Count() > 1))
            failures.Add($"Table {table.Key} is mapped by both connection designs {string.Join(" and ", table.Select(d => d.Design.Instance))} in environment {environmentId}");
        candidates.RemoveAll(d => d.Design.ObjectMap is { } m && candidates.Count(o => o.Design.ObjectMap?.Table == m.Table) > 1);

        var accepted = new List<(ConnectionDesign Design, EnvironmentSettings Settings)>();
        var store = new HostConfigStore(DesignConfigInput.Merge(files.Input, environmentId, accepted, scopes));
        foreach (var candidate in candidates)
        {
            var trial = HostConfigStore.TryCreate(DesignConfigInput.Merge(files.Input, environmentId, accepted.Append(candidate), scopes), out var problems);
            if (trial == null)
            {
                failures.Add($"Connection design {candidate.Design.Instance} (v{candidate.Design.Version}) was left out: {string.Join("; ", problems)}");
                continue;
            }
            accepted.Add(candidate);
            store = trial;
        }
        // Logged by each request that needs a missing design, as its ConfigurationError.
        store.Input.DesignFailures.AddRange(failures);
        return store;
    }

    private static string Key(string environmentId) =>
        Guid.TryParse(environmentId, out var id)
            ? id.ToString("D")
            : throw new ConfigurationException($"Environment ID `{environmentId}` is not a GUID.");
}
