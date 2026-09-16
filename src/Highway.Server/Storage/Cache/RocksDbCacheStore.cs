using System.Text;
using RocksDbSharp;

namespace Highway.Server.Storage.Cache;

/// <summary>
/// The durable broker-local cache store (044) — its <b>own</b> RocksDB database at
/// <c>dataDir/cache</c>, entirely separate from the replicated broker store.
///
/// <para><b>Why a separate database (044 R2).</b> RocksDB keeps one WAL per database, and
/// replication ships that whole WAL. A cache column family in the broker DB would still
/// ship; a separate database is the only structure whose writes <i>cannot</i> reach the
/// replicated WAL. The replication feeder is built over the broker DB only and never learns
/// this database exists.</para>
///
/// <para>Writes bypass this DB's own WAL (<c>disableWAL</c>) — a cache need not be
/// crash-durable, and it keeps cache churn out of the log. Values are framed with
/// <see cref="ExpiryFraming"/>; expiry is filter-on-read, so a lapsed entry reads as a miss
/// even before the sweeper reaches it.</para>
/// </summary>
internal sealed class RocksDbCacheStore : IHighwayCacheStore
{
    private readonly RocksDb _db;
    private readonly string _path;
    private readonly WriteOptions _writeOptions;

    private RocksDbCacheStore(RocksDb db, string path)
    {
        _db = db;
        _path = path;
        _writeOptions = new WriteOptions().SetSync(false).DisableWal(1);
    }

    /// <summary>Opens (or creates) the cache database at <paramref name="path"/> (by convention <c>dataDir/cache</c>).</summary>
    public static RocksDbCacheStore Open(string path)
    {
        Directory.CreateDirectory(path);
        var options = new DbOptions().SetCreateIfMissing(true);
        var db = RocksDb.Open(options, path);
        return new RocksDbCacheStore(db, path);
    }

    private static byte[] K(string key) => Encoding.UTF8.GetBytes(key);

    public byte[]? Get(string key, long nowTicks)
        => ExpiryFraming.Unwrap(_db.Get(K(key)), nowTicks);

    public void Set(string key, byte[] value, long expiresAtTicks)
        => _db.Put(K(key), ExpiryFraming.Wrap(value, expiresAtTicks), cf: null, writeOptions: _writeOptions);

    public void Remove(string key)
        => _db.Remove(K(key), cf: null, writeOptions: _writeOptions);

    public long? GetExpiryTicks(string key, long nowTicks)
    {
        var framed = _db.Get(K(key));
        if (framed is null || ExpiryFraming.Unwrap(framed, nowTicks) is null) return null;
        return ExpiryFraming.TryReadExpiry(framed);
    }

    public void Clear()
    {
        // Drop every key. Cache clears are infrequent (epoch change / over-cap), so an
        // iterate-and-batch-delete is fine and needs no range-bound gymnastics.
        using var batch = new WriteBatch();
        using var iter = _db.NewIterator();
        for (iter.SeekToFirst(); iter.Valid(); iter.Next())
            batch.Delete(iter.Key());
        _db.Write(batch, _writeOptions);
    }

    public void SweepExpired(long nowTicks)
    {
        // Correctness is already covered by filter-on-read; this reclaims space for the
        // entries that have lapsed. Bounded by the size cap, which triggers a full Clear.
        using var batch = new WriteBatch();
        using var iter = _db.NewIterator();
        var any = false;
        for (iter.SeekToFirst(); iter.Valid(); iter.Next())
        {
            if (ExpiryFraming.IsExpired(iter.Value(), nowTicks))
            {
                batch.Delete(iter.Key());
                any = true;
            }
        }
        if (any) _db.Write(batch, _writeOptions);
    }

    public long ApproximateSizeBytes()
    {
        var raw = _db.GetProperty("rocksdb.estimate-live-data-size");
        return long.TryParse(raw, out var bytes) ? bytes : 0;
    }

    public void Dispose() => _db.Dispose();
}
