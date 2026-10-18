using System.Text;
using SystemD.Crystal;
using CrystalBox = SystemD.Crystal.Box;

namespace Highway.Server.Storage.Cache;

/// <summary>
/// The durable broker-local cache store (044, on Crystal since 076 R4): database <c>highway.cache</c> in the broker's
/// Crystal store, created <b>not replicated</b>, so no cache write can reach a standby — the property 044 R2 needed a
/// separate RocksDB database for. Writes commit <see cref="DurabilityMode.Buffered"/>: a cache need not be crash-durable.
/// Values are framed with <see cref="ExpiryFraming"/>; expiry is filter-on-read, so a lapsed entry reads as a miss even
/// before the sweeper reaches it.
/// </summary>
internal sealed class CrystalCacheStore : IHighwayCacheStore
{
    private const string EntriesBox = "entries";

    private readonly CrystalDatabase _db;
    private readonly CrystalBox _entries;

    private CrystalCacheStore(CrystalDatabase db, CrystalBox entries)
    {
        _db = db;
        _entries = entries;
    }

    /// <summary>Opens (or creates) the cache database in <paramref name="store"/>.</summary>
    public static CrystalCacheStore Open(CrystalStore store, string name)
    {
        CrystalDatabase db;
        try { db = store.OpenDatabase(name); }
        catch (DatabaseNotFoundException) { db = store.CreateDatabase(name, replicated: false); }
        var entries = db.Boxes.Find(EntriesBox) ?? db.Boxes.Create(EntriesBox, BoxProfile.DeleteHeavy);
        return new CrystalCacheStore(db, entries);
    }

    private static byte[] K(string key) => Encoding.UTF8.GetBytes(key);

    private byte[]? Raw(string key)
    {
        using var snapshot = _db.Snapshot();
        return snapshot.Get(_entries, K(key));
    }

    public byte[]? Get(string key, long nowTicks) => ExpiryFraming.Unwrap(Raw(key), nowTicks);

    public void Set(string key, byte[] value, long expiresAtTicks)
    {
        using var batch = _db.NewBatch();
        batch.Put(_entries, K(key), ExpiryFraming.Wrap(value, expiresAtTicks));
        batch.Commit(DurabilityMode.Buffered);
    }

    public void Remove(string key)
    {
        using var batch = _db.NewBatch();
        batch.Delete(_entries, K(key));
        batch.Commit(DurabilityMode.Buffered);
    }

    public long? GetExpiryTicks(string key, long nowTicks)
    {
        var framed = Raw(key);
        if (framed is null || ExpiryFraming.Unwrap(framed, nowTicks) is null) return null;
        return ExpiryFraming.TryReadExpiry(framed);
    }

    public void Clear()
    {
        using var batch = _db.NewBatch();
        batch.DeleteRange(_entries, [], []);
        batch.Commit(DurabilityMode.Buffered);
    }

    public void SweepExpired(long nowTicks)
    {
        // Correctness is already covered by filter-on-read; this reclaims the space of lapsed entries.
        using var batch = _db.NewBatch();
        using (var snapshot = _db.Snapshot())
        {
            foreach (var kv in snapshot.Scan(_entries, [], [], int.MaxValue))
                if (ExpiryFraming.IsExpired(kv.Value, nowTicks))
                    batch.Delete(_entries, kv.Key);
        }
        batch.Commit(DurabilityMode.Buffered);   // nothing staged → nothing written
    }

    /// <summary>RocksDB's estimate for the entries box (memtables included), as the RocksDB store read it: no scan.</summary>
    public long ApproximateSizeBytes() => (long)_db.ApproximateBytes(_entries, KeyRange.All);

    /// <summary>The database belongs to the broker's Crystal store, which closes it.</summary>
    public void Dispose() { }
}
