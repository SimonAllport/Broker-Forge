namespace BrokerForge.Auth.PerUser;

/// <summary>
/// One refresh at a time per (instance, user). Sources that rotate refresh tokens accept each one once, so two
/// parallel refreshes would make one of them fail and wrongly mark the user expired. An entry lives only while
/// someone holds or waits for it, so the table does not grow with every user a replica has seen.
/// </summary>
internal sealed class RefreshGate
{
    private sealed class Entry
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public int Users;
    }

    private readonly Dictionary<(string, string), Entry> _gates = new();

    internal int Count
    {
        get
        {
            lock (_gates)
                return _gates.Count;
        }
    }

    public async Task<IDisposable> EnterAsync(string instance, string user, CancellationToken ct)
    {
        var key = (instance, user);
        Entry entry;
        lock (_gates)
        {
            if (!_gates.TryGetValue(key, out entry!))
                _gates[key] = entry = new Entry();
            entry.Users++;
        }
        try
        {
            await entry.Gate.WaitAsync(ct);
        }
        catch
        {
            Leave(key, entry);
            throw;
        }
        return new Release(this, key, entry);
    }

    private void Leave((string, string) key, Entry entry)
    {
        lock (_gates)
        {
            if (--entry.Users == 0)
                _gates.Remove(key);
        }
    }

    private sealed class Release(RefreshGate owner, (string, string) key, Entry entry) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 1)
                return;
            entry.Gate.Release();
            owner.Leave(key, entry);
        }
    }
}
