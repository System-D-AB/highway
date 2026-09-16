using System.Collections.Concurrent;

namespace Highway.Server.Storage.Cache;

/// <summary>
/// In-memory cache store (044) — used when the broker is ephemeral (no data directory). A
/// concurrent map of key → (value, absolute-expiry), bounded by RAM. Same expiry and
/// epoch-wipe / size-cap behaviour as the durable store.
/// </summary>
internal sealed class InMemoryCacheStore : IHighwayCacheStore
{
    private readonly ConcurrentDictionary<string, Entry> _map = new(StringComparer.Ordinal);
    private long _sizeBytes;

    private readonly record struct Entry(byte[] Value, long ExpiresAtTicks);

    public byte[]? Get(string key, long nowTicks)
    {
        if (!_map.TryGetValue(key, out var e)) return null;
        if (nowTicks >= e.ExpiresAtTicks)
        {
            Remove(key);
            return null;
        }
        return e.Value;
    }

    public void Set(string key, byte[] value, long expiresAtTicks)
    {
        var entry = new Entry(value, expiresAtTicks);
        _map.AddOrUpdate(key,
            _ => { Interlocked.Add(ref _sizeBytes, value.Length); return entry; },
            (_, old) => { Interlocked.Add(ref _sizeBytes, value.Length - old.Value.Length); return entry; });
    }

    public void Remove(string key)
    {
        if (_map.TryRemove(key, out var e))
            Interlocked.Add(ref _sizeBytes, -e.Value.Length);
    }

    public long? GetExpiryTicks(string key, long nowTicks)
    {
        if (!_map.TryGetValue(key, out var e) || nowTicks >= e.ExpiresAtTicks) return null;
        return e.ExpiresAtTicks;
    }

    public void Clear()
    {
        _map.Clear();
        Interlocked.Exchange(ref _sizeBytes, 0);
    }

    public void SweepExpired(long nowTicks)
    {
        foreach (var (key, e) in _map)
            if (nowTicks >= e.ExpiresAtTicks)
                Remove(key);
    }

    public long ApproximateSizeBytes() => Interlocked.Read(ref _sizeBytes);

    public void Dispose() { }
}
