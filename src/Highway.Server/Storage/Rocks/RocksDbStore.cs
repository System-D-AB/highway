using System.Buffers.Binary;
using Highway.Server.Storage.Layout;
using Microsoft.Extensions.Logging;
using RocksDbSharp;

namespace Highway.Server.Storage.Rocks;

/// <summary>
/// The RocksDB implementation of <see cref="IHighwayStore"/> (037 R3.3, 038 T3). Opens
/// the database with the column families in their asserted order, and wires the four
/// key families + counters through the existing <see cref="HighwayKeyspace"/> layout.
///
/// <para><b>Read-view (038 R0.4).</b> The design named <c>WriteBatchWithIndex</c> as the
/// primary read-your-own-writes mechanism, with a <b>managed overlay</b> as the sanctioned
/// fallback. This store uses the managed overlay: a batch stages puts/deletes into an
/// ordered overlay and reads through overlay-first-then-DB, exactly as the proven
/// <c>InMemoryBatch</c> does. It is chosen over WBWI because the overlay's correctness is
/// verified by the same contract suite that already passes in-memory, whereas WBWI's
/// merged-iterator behaviour under CF-scoped snapshots is a binding detail this feature
/// cannot measure. WBWI remains the documented optimisation if the overlay's per-batch
/// allocation is ever measured to matter (OD1). Recorded as a deviation from the design's
/// stated primary in 038's design.</para>
///
/// <para><b>Durability (038 R0.3).</b> <see cref="RocksDbBatch.Commit"/> maps to
/// <c>db.Write</c> with <c>WriteOptions.SetSync(true)</c> — an acknowledged write is on
/// disk. Recovery mode tolerates a torn WAL tail (the reference discipline).</para>
///
/// <para><b>Column families.</b> All families (<c>q</c>/<c>z</c>/<c>s</c>/<c>k</c>/<c>n</c>)
/// live in <c>data</c>; dead-letter lists live in <c>dlq</c>. The family tag is inside the
/// key, so a key names its family regardless of CF; the store routes DLQ keys to the
/// <c>dlq</c> CF by inspecting the tag+name, everything else to <c>data</c>. For 038 the
/// contract suite exercises <c>data</c>; the DLQ routing is validated when 039 ports the
/// commands. To keep the seam engine-free, CF routing is entirely internal here.</para>
/// </summary>
public sealed class RocksDbStore : IHighwayStore
{
    private readonly RocksDb _db;
    private readonly ColumnFamilyHandle _data;
    private readonly WriteOptions _writeOptions;
    private readonly string _path;
    private readonly bool _ownsDirectory;

    /// <summary>
    /// WAL retention defaults (042 T1/T4, configurable via
    /// <see cref="HighwayReplicationOptions"/>). 24h TTL and 1 GiB cap: a dead replica
    /// cannot fill the disk. A live replica whose watermark falls behind the retained
    /// WAL is refused with <c>HW_REPL_GAP</c> and re-bootstraps via snapshot (G4) —
    /// never served a gapped stream.
    /// </summary>
    internal const long DefaultWalTtlSeconds = 86_400;
    internal const long DefaultMaxTotalWalSizeBytes = 1024L * 1024 * 1024;

    /// <summary>The WAL feeder. Null only if construction failed before Open completed.</summary>
    internal ReplicationFeeder Replication { get; }

    /// <summary>The store's directory — where replication keeps its node-local files (epoch, resync marker).</summary>
    internal string DataDir => _path;

    private RocksDbStore(RocksDb db, string path, bool ownsDirectory, HighwayReplicationOptions? replication)
    {
        _db = db;
        _path = path;
        _ownsDirectory = ownsDirectory;
        _data = db.GetColumnFamily(HighwayColumnFamilies.OrderedNames[HighwayColumnFamilies.Data]);
        _writeOptions = new WriteOptions().SetSync(true);
        Replication = new ReplicationFeeder(db, path, replication ?? new HighwayReplicationOptions());
        // A promotion announces its new epoch to every roster peer (042-1d); the closure
        // reads through this fully-built store, invoked only at promote time.
        Replication.RosterPeers = () => RosterStore.Read(this).Members.Select(m => m.Endpoint).ToArray();
        // 050 T1 (F1): a node that promotes at runtime self-registers into the roster — the same
        // record the startup path writes for a node that starts writable — so the successor order
        // (and the dashboard succession view) survives a failover. Idempotent on the node id, and
        // written through this store so it WAL-ships to every standby.
        Replication.RegisterSelfInRoster = () => RosterStore.TryUpsert(
            this,
            new RosterMember(Replication.Options.ReplicaId, Replication.Options.Priority, Replication.SelfEndpoint),
            out _, out _);
    }

    /// <summary>
    /// Opens (or creates) a store at <paramref name="path"/>. The column families are
    /// created if absent and their order is asserted (physical-layout.md §6).
    /// </summary>
    public static RocksDbStore Open(string path, bool ownsDirectory = false, HighwayReplicationOptions? replication = null, ILogger? logger = null)
    {
        replication?.Validate();
        Directory.CreateDirectory(path);

        // 050 T2/T3 (F2): a demoted ex-primary wrote a rejoin marker naming the new primary it
        // learned at runtime. Honour it by re-syncing as that primary's replica — reusing the same
        // wipe → snapshot → puller path a configured replica uses. The node has no static
        // PrimaryServer, so the marker supplies the endpoint; any auth/TLS tail on the node's own
        // (rare) PrimaryServer config is carried over. WipeForRejoin preserves the marker itself, so
        // a failed snapshot download leaves it for the next restart to retry rather than silently
        // coming back up as a stale primary; a successful Open consumes it below.
        if (replication is { AutoRejoin: true } && ReplicaPuller.ReadRejoinMarker(path) is { } rejoin)
        {
            // The rejoin endpoint was learned at runtime and carries no credentials. Use the tail
            // from this node's own (rare) PrimaryServer config if it has one, else the shared-secret
            // tail the server derived from its authentication (050 T3) — a replica set shares it.
            var priorTail = replication.PrimaryServer is { } ps && ps.Contains(',')
                ? ps[ps.IndexOf(',')..]
                : (replication.AuthTail ?? "");
            replication.StartAsReplica = true;
            replication.PrimaryServer = rejoin.Endpoint + priorTail;
            logger?.LogWarning(
                "[replication] rejoin marker present; wiping and re-syncing as a replica of {Primary} (epoch {Epoch})",
                ReplicationFeeder.HostOf(rejoin.Endpoint) ?? rejoin.Endpoint, rejoin.Epoch);
            WipeForRejoin(path);
        }

        // G4: a replica that hit a WAL gap wrote the resync marker and was restarted.
        // Wipe the stale database (keeping node-local replication files — the epoch must
        // survive a re-sync, or a stale old primary could feed the fresh replica) and
        // fall through to the ordinary blank-directory bootstrap: one code path (R2.2).
        if (replication is { StartAsReplica: true } &&
            !string.IsNullOrWhiteSpace(replication.PrimaryServer) &&
            File.Exists(Path.Combine(path, ReplicaPuller.ResyncMarkerFileName)))
        {
            logger?.LogWarning("[replication] resync marker present; wiping the local database to re-bootstrap from the primary");
            WipeForResync(path);
        }

        if (replication is { StartAsReplica: true } &&
            !string.IsNullOrWhiteSpace(replication.PrimaryServer) &&
            !File.Exists(Path.Combine(path, "CURRENT")))
        {
            var primaryHost = ReplicationFeeder.HostOf(replication.PrimaryServer) ?? "(primary)";
            logger?.LogInformation("[replication] replica data directory is blank; bootstrapping snapshot from {Primary}", primaryHost);
            var elapsed = ReplicaPuller.DownloadSnapshot(replication.PrimaryServer, path);
            logger?.LogInformation("[replication] snapshot bootstrap complete in {Ms} ms", (long)elapsed.TotalMilliseconds);
        }
        var options = new DbOptions()
            .SetCreateIfMissing(true)
            .SetCreateMissingColumnFamilies(true)
            .SetWalTtlSeconds((ulong)(replication?.WalTtlSeconds ?? DefaultWalTtlSeconds))
            .SetMaxTotalWalSize((ulong)(replication?.MaxTotalWalSizeBytes ?? DefaultMaxTotalWalSizeBytes));

        var families = new ColumnFamilies();
        for (var i = 1; i < HighwayColumnFamilies.OrderedNames.Count; i++)
            families.Add(HighwayColumnFamilies.OrderedNames[i], new ColumnFamilyOptions());

        var db = RocksDb.Open(options, path, families);

        // 050 T2/T3: a rejoin bootstrap succeeded (the snapshot applied and the DB opened) — consume
        // the marker so a clean restart does not wipe again. A failed download would have thrown
        // above, leaving the marker in place for the next attempt.
        var rejoinMarker = Path.Combine(path, ReplicaPuller.RejoinMarkerFileName);
        if (File.Exists(rejoinMarker)) File.Delete(rejoinMarker);

        AssertColumnFamilyOrder(db);
        return new RocksDbStore(db, path, ownsDirectory, replication);
    }

    /// <summary>
    /// Deletes the database files ahead of a snapshot re-bootstrap (G4), preserving the
    /// node-local replication files: the persisted epoch and the bootstrap log. The
    /// resync marker itself is removed — the wipe is the marker's fulfilment.
    /// </summary>
    private static void WipeForResync(string path)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
        {
            var name = Path.GetFileName(entry);
            if (name is "repl-epoch.txt" or "snapshot-bootstrap.log")
                continue;
            if (Directory.Exists(entry)) Directory.Delete(entry, recursive: true);
            else File.Delete(entry);
        }
    }

    /// <summary>
    /// Wipes the database ahead of a rejoin re-bootstrap (050 T2), preserving the persisted epoch,
    /// the bootstrap log, AND the rejoin marker — the marker must survive a failed snapshot download
    /// so the next restart retries the rejoin instead of coming up as a stale primary. The marker is
    /// consumed only after <see cref="Open"/> succeeds.
    /// </summary>
    private static void WipeForRejoin(string path)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
        {
            var name = Path.GetFileName(entry);
            if (name is "repl-epoch.txt" or "snapshot-bootstrap.log" or ReplicaPuller.RejoinMarkerFileName)
                continue;
            if (Directory.Exists(entry)) Directory.Delete(entry, recursive: true);
            else File.Delete(entry);
        }
    }

    private static void AssertColumnFamilyOrder(RocksDb db)
    {
        // A batch addresses a CF by handle; a missing/reordered family writes to the wrong
        // place silently. Assert every expected family resolves (stow ColumnFamilies.AssertOrder).
        for (var i = 1; i < HighwayColumnFamilies.OrderedNames.Count; i++)
        {
            var name = HighwayColumnFamilies.OrderedNames[i];
            if (!db.TryGetColumnFamily(name, out _))
                throw new InvalidOperationException(
                    $"Column family '{name}' (index {i}) not found — the database layout does not match {nameof(HighwayColumnFamilies)}.");
        }
    }

    /// <inheritdoc />
    public IStoreSnapshot Snapshot()
    {
        var snapshot = _db.CreateSnapshot();
        var readOptions = new ReadOptions().SetSnapshot(snapshot);
        return new RocksDbSnapshot(snapshot, readOptions);
    }

    /// <inheritdoc />
    public IStoreBatch NewBatch() => new RocksDbBatch(this);

    /// <inheritdoc />
    public void Dispose()
    {
        Replication.Dispose();
        _db.Dispose();
        if (_ownsDirectory && Directory.Exists(_path))
        {
            try { Directory.Delete(_path, recursive: true); } catch { /* best-effort temp cleanup */ }
        }
    }

    // ---- reads (against a snapshot) ------------------------------------------

    /// <inheritdoc />
    public byte[]? Get(IStoreSnapshot snapshot, byte[] key)
        => _db.Get(key, _data, Cast(snapshot).ReadOptions);

    /// <inheritdoc />
    public long ListLength(IStoreSnapshot snapshot, byte[] key)
        => CountPrefix(Cast(snapshot).ReadOptions, key);

    /// <inheritdoc />
    public long SortedSetLength(IStoreSnapshot snapshot, byte[] key)
        => CountPrefix(Cast(snapshot).ReadOptions, key);

    /// <inheritdoc />
    public IReadOnlyList<byte[]> SortedSetRangeByScore(
        IStoreSnapshot snapshot, byte[] key, long minScore, long maxScore, int limit)
    {
        var results = new List<byte[]>();
        using var iter = _db.NewIterator(_data, Cast(snapshot).ReadOptions);
        for (iter.Seek(key); iter.Valid() && results.Count < limit; iter.Next())
        {
            var k = iter.Key();
            if (!StartsWith(k, key)) break;
            if (k.Length < key.Length + 8) continue;

            var score = DecodeScoreAt(k, key.Length);
            if (score < minScore) continue;
            if (score > maxScore) continue;

            results.Add(k[(key.Length + 8)..]); // member = suffix after prefix+score
        }
        return results;
    }

    /// <inheritdoc />
    public IReadOnlyList<byte[]> SetMembers(IStoreSnapshot snapshot, byte[] key)
    {
        var results = new List<byte[]>();
        using var iter = _db.NewIterator(_data, Cast(snapshot).ReadOptions);
        for (iter.Seek(key); iter.Valid(); iter.Next())
        {
            var k = iter.Key();
            if (!StartsWith(k, key)) break;
            results.Add(k[key.Length..]); // member = suffix after set prefix
        }
        return results;
    }

    // ---- mutations (staged into a batch) -------------------------------------

    /// <inheritdoc />
    public void Set(IStoreBatch batch, byte[] key, byte[] value) => Cast(batch).Put(key, value);

    /// <inheritdoc />
    public void SetEx(IStoreBatch batch, byte[] key, byte[] value, long expiresAtTicks)
        => Cast(batch).Put(key, ExpiryFraming.Wrap(value, expiresAtTicks));

    /// <inheritdoc />
    public byte[]? GetLive(IStoreSnapshot snapshot, byte[] key, long nowTicks)
        => ExpiryFraming.Unwrap(_db.Get(key, _data, Cast(snapshot).ReadOptions), nowTicks);

    /// <inheritdoc />
    public void SweepExpired(IStoreBatch batch, long nowTicks)
    {
        var b = Cast(batch);
        foreach (var k in b.AllUnderPrefix(ExpiryFraming.KvFamilyPrefix))
        {
            var framed = b.ReadThrough(k);
            if (framed is not null && ExpiryFraming.IsExpired(framed, nowTicks))
                b.Delete(k);
        }
    }

    /// <inheritdoc />
    public void Delete(IStoreBatch batch, byte[] key) => Cast(batch).Delete(key);

    /// <inheritdoc />
    public long Increment(IStoreBatch batch, byte[] key, long delta)
    {
        var b = Cast(batch);
        var current = b.ReadThrough(key) is { Length: >= 8 } bytes
            ? BinaryPrimitives.ReadInt64BigEndian(bytes)
            : 0L;
        var next = current + delta;
        var encoded = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(encoded, next);
        b.Put(key, encoded);
        return next;
    }

    /// <inheritdoc />
    public void ListRightPush(IStoreBatch batch, byte[] key, byte[] value) => Cast(batch).Put(key, value);

    /// <inheritdoc />
    public void ListLeftPush(IStoreBatch batch, byte[] key, byte[] value) => Cast(batch).Put(key, value);

    /// <inheritdoc />
    public byte[]? ListLeftPop(IStoreBatch batch, byte[] key)
    {
        var b = Cast(batch);
        var head = b.FirstUnderPrefix(key);
        if (head is null) return null;
        var value = b.ReadThrough(head);
        b.Delete(head);
        return value;
    }

    /// <inheritdoc />
    public IReadOnlyList<byte[]> ListDrain(IStoreBatch batch, byte[] key)
    {
        var b = Cast(batch);
        var keys = b.AllUnderPrefix(key);
        var values = new List<byte[]>(keys.Count);
        foreach (var k in keys)
        {
            var v = b.ReadThrough(k);
            if (v is not null) values.Add(v);
            b.Delete(k);
        }
        return values;
    }

    /// <inheritdoc />
    public void SortedSetAdd(IStoreBatch batch, byte[] key, long score, byte[] member)
        => Cast(batch).Put(ComposeZKey(key, score, member), []);

    /// <inheritdoc />
    public void SortedSetRemove(IStoreBatch batch, byte[] key, byte[] member)
    {
        var b = Cast(batch);
        foreach (var k in b.AllUnderPrefix(key))
        {
            if (k.Length < key.Length + 8) continue;
            if (k.AsSpan(key.Length + 8).SequenceEqual(member))
            {
                b.Delete(k);
                return;
            }
        }
    }

    /// <inheritdoc />
    public bool SetAdd(IStoreBatch batch, byte[] key, byte[] member)
    {
        var b = Cast(batch);
        var memberKey = Concat(key, member);
        var existed = b.ReadThrough(memberKey) is not null;
        b.Put(memberKey, []);
        return !existed;
    }

    /// <inheritdoc />
    public void SetRemove(IStoreBatch batch, byte[] key, byte[] member)
        => Cast(batch).Delete(Concat(key, member));

    /// <inheritdoc />
    public void DeleteRange(IStoreBatch batch, byte[] keyPrefix)
    {
        // Staged as explicit per-key deletes over the merged view so read-your-own-writes
        // stays correct within the batch. A native WriteBatch.DeleteRange is the physical
        // optimisation, applied at commit for keys already on disk (below).
        var b = Cast(batch);
        foreach (var k in b.AllUnderPrefix(keyPrefix))
            b.Delete(k);
        b.MarkRangeDeleted(keyPrefix);
    }

    // ---- commit (the single write path) --------------------------------------

    internal void Commit(RocksDbBatch batch)
    {
        using var wb = new WriteBatch();

        // Physical range tombstones first (cheap bulk purge of already-committed keys).
        foreach (var prefix in batch.RangeDeletes)
            wb.DeleteRange(prefix, (ulong)prefix.Length, PrefixUpperBound(prefix), (ulong)prefix.Length, _data);

        foreach (var (key, value) in batch.Overlay)
        {
            if (value is null) wb.Delete(key, _data);
            else wb.Put(key, value, _data);
        }

        _db.Write(wb, _writeOptions);
    }

    /// <summary>Committed read for a batch's read-through fallback.</summary>
    internal byte[]? CommittedGet(byte[] key) => _db.Get(key, _data);

    /// <summary>
    /// Test support (038 T6): an ordered dump of every key/value in the <c>data</c> CF, for
    /// byte-identical comparison between a reference run and a recovered crash run.
    /// </summary>
    internal List<(byte[] Key, byte[] Value)> DumpData()
    {
        var result = new List<(byte[], byte[])>();
        using var iter = _db.NewIterator(_data);
        for (iter.SeekToFirst(); iter.Valid(); iter.Next())
            result.Add((iter.Key(), iter.Value()));
        return result;
    }

    /// <summary>Committed keys under a prefix, ordered — for a batch's merged view.</summary>
    internal List<byte[]> CommittedKeysUnderPrefix(byte[] prefix)
    {
        var result = new List<byte[]>();
        using var iter = _db.NewIterator(_data);
        for (iter.Seek(prefix); iter.Valid(); iter.Next())
        {
            var k = iter.Key();
            if (!StartsWith(k, prefix)) break;
            result.Add(k);
        }
        return result;
    }

    // ---- helpers ------------------------------------------------------------

    private long CountPrefix(ReadOptions readOptions, byte[] prefix)
    {
        long n = 0;
        using var iter = _db.NewIterator(_data, readOptions);
        for (iter.Seek(prefix); iter.Valid(); iter.Next())
        {
            if (!StartsWith(iter.Key(), prefix)) break;
            n++;
        }
        return n;
    }

    private static RocksDbSnapshot Cast(IStoreSnapshot s)
        => s as RocksDbSnapshot ?? throw new ArgumentException("Snapshot from a different store.", nameof(s));

    private static RocksDbBatch Cast(IStoreBatch b)
        => b as RocksDbBatch ?? throw new ArgumentException("Batch from a different store.", nameof(b));

    internal static bool StartsWith(byte[] whole, byte[] prefix)
        => whole.Length >= prefix.Length && whole.AsSpan(0, prefix.Length).SequenceEqual(prefix);

    private static byte[] Concat(byte[] a, byte[] b)
    {
        var r = new byte[a.Length + b.Length];
        a.CopyTo(r, 0);
        b.CopyTo(r, a.Length);
        return r;
    }

    private static byte[] ComposeZKey(byte[] prefix, long score, byte[] member)
    {
        var r = new byte[prefix.Length + 8 + member.Length];
        prefix.CopyTo(r, 0);
        var encoded = (ulong)score ^ 0x8000_0000_0000_0000UL;
        BinaryPrimitives.WriteUInt64BigEndian(r.AsSpan(prefix.Length, 8), encoded);
        member.CopyTo(r, prefix.Length + 8);
        return r;
    }

    private static long DecodeScoreAt(byte[] key, int offset)
    {
        var raw = BinaryPrimitives.ReadUInt64BigEndian(key.AsSpan(offset, 8));
        return (long)(raw ^ 0x8000_0000_0000_0000UL);
    }

    /// <summary>The exclusive upper bound of a prefix range: the prefix with its last byte incremented,
    /// or an all-0xFF-extended bound when the prefix is all 0xFF.</summary>
    private static byte[] PrefixUpperBound(byte[] prefix)
    {
        var upper = (byte[])prefix.Clone();
        for (var i = upper.Length - 1; i >= 0; i--)
        {
            if (upper[i] != 0xFF)
            {
                upper[i]++;
                return upper[..(i + 1)];
            }
        }
        // All 0xFF: no finite successor within the same length; append 0xFF to extend the range end.
        var extended = new byte[prefix.Length + 1];
        prefix.CopyTo(extended, 0);
        extended[^1] = 0xFF;
        return extended;
    }
}
