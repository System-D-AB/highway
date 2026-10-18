using System.Buffers.Binary;
using System.Collections.Concurrent;
using Highway.Server.Storage.Replication;
using Microsoft.Extensions.Logging;
using SystemD.Crystal;
using CrystalBox = SystemD.Crystal.Box;

namespace Highway.Server.Storage.Crystal;

/// <summary>
/// <see cref="IHighwayStore"/> on SystemD.Crystal (076 R2). Highway's keys are unchanged: a key is
/// <c>[box:4][rest]</c>, and Highway box <c>n</c> is the Crystal box <c>b/{n:x8}</c> in database
/// <see cref="DatabaseName"/>, holding <c>rest</c>. Highway's catalogue, box runtime and locks sit on top, exactly as
/// they did on RocksDB; Crystal supplies the boxes, the atomic multi-box batch and replication.
///
/// <para><b>One command, one batch (R2.5).</b> A <see cref="CrystalHighwayBatch"/> wraps one Crystal batch across every
/// box the command touches and commits it <see cref="DurabilityMode.Strict"/>. An empty batch commits nothing.</para>
///
/// <para><b>Retirement stays atomic (R2.4).</b> A range delete of a whole Highway box is a range delete of the whole
/// Crystal box inside the same batch. The emptied Crystal box is dropped at the next start (<see cref="SweepEmptyBoxes"/>):
/// dropping it at run time would race readers that still hold its handle, and an empty box costs one catalogue entry.</para>
///
/// <para><b>Standby.</b> A standby's database arrives by replication. Until its bootstrap lands, and while a re-bootstrap
/// replaces it, reads see an empty store; writes are refused before they reach here (the write gate).</para>
/// </summary>
public sealed class CrystalHighwayStore : IHighwayStore
{
    /// <summary>The replicated database that holds every Highway box (R3.1).</summary>
    internal const string DatabaseName = "highway";

    /// <summary>The node-local, not-replicated database that holds the cache (R3.1, R4).</summary>
    internal const string CacheDatabaseName = "highway.cache";

    private readonly CrystalStore _crystal;
    private readonly string _path;
    private readonly bool _ownsDirectory;
    private readonly Lock _viewGate = new();
    private DatabaseView? _view;

    internal HighwayReplication Replication { get; }

    /// <summary>The Crystal store under this one (replication, metrics, the cache database).</summary>
    internal CrystalStore Crystal => _crystal;

    /// <summary>The store's directory.</summary>
    internal string DataDir => _path;

    private CrystalHighwayStore(CrystalStore crystal, string path, bool ownsDirectory, HighwayReplicationOptions replication, ILogger? logger)
    {
        _crystal = crystal;
        _path = path;
        _ownsDirectory = ownsDirectory;
        Replication = new HighwayReplication(this, replication, logger);
        // A promotion announces its new epoch to every roster peer (042-1d), and the higher-peer check and the
        // equal-epoch tie-break read the priorities (069); the closures read through this fully-built store.
        Replication.RosterPeers = () => RosterStore.Read(this).Members.Select(m => m.Endpoint).ToArray();
        Replication.RosterMembers = () => RosterStore.Read(this).Members;
        // 050 T1 (F1): a node that promotes at runtime registers itself in the replicated roster, as one that starts
        // writable does, so the successor order survives a failover. Idempotent on the node id.
        Replication.RegisterSelfInRoster = () => RosterStore.TryUpsert(
            this,
            new RosterMember(Replication.Options.ReplicaId, Replication.Options.Priority, Replication.SelfEndpoint),
            out _, out _);
        // 061 A4: head hints describe what THIS process wrote as primary; any epoch change starts cold.
        Replication.OnEpochChanged += () => _headHints.Clear();
    }

    /// <summary>
    /// Opens (or creates) the store at <paramref name="path"/>. A primary creates database <see cref="DatabaseName"/> on
    /// first start; a standby receives it from its primary. A directory written by Highway's RocksDB engine (3.0
    /// pre-releases, or 2.x) is refused with the drain-and-wipe way forward (R3.2).
    /// </summary>
    public static CrystalHighwayStore Open(string path, bool ownsDirectory = false, HighwayReplicationOptions? replication = null,
        ILogger? logger = null, CancellationToken cancellationToken = default, ILoggerFactory? loggerFactory = null,
        System.Diagnostics.Metrics.IMeterFactory? meterFactory = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        replication ??= new HighwayReplicationOptions();
        replication.Validate();
        Directory.CreateDirectory(path);
        RefuseForeignDirectory(path);

        var crystal = CrystalStore.Open(path, new CrystalOptions
        {
            Durability = DurabilityMode.Strict,
            LoggerFactory = loggerFactory,
            MeterFactory = meterFactory,
            Replication = HighwayReplication.CrystalOptionsFor(replication),
        });

        CrystalHighwayStore? store = null;
        try
        {
            store = new CrystalHighwayStore(crystal, path, ownsDirectory, replication, logger);
            store.Replication.Start();
            if (store.Replication.IsWritable)
            {
                store.View(create: true);
                store.SweepEmptyBoxes();
            }
            return store;
        }
        catch
        {
            store?.Replication.Dispose();   // its timers and standby agent must not outlive a failed open (review L6)
            crystal.Dispose();
            throw;
        }
    }

    /// <summary>
    /// A directory with content but no Crystal stamp is not a store this build reads. Highway's own engine wrote one
    /// before 076 (a <c>CURRENT</c> file, or a <c>highway.format</c> stamp): name that case, with its way forward.
    /// </summary>
    private static void RefuseForeignDirectory(string path)
    {
        if (File.Exists(Path.Combine(path, "crystal.format"))) return;
        if (File.Exists(Path.Combine(path, "CURRENT")) || File.Exists(Path.Combine(path, "highway.format"))
            || Directory.Exists(Path.Combine(path, "checkpoints")) || Directory.Exists(Path.Combine(path, "log")))
            throw new InvalidOperationException(StorageLayoutMessage(path));
    }

    /// <summary>The operator-facing explanation for a data directory written by an earlier storage engine (no in-place migration).</summary>
    internal static string StorageLayoutMessage(string path)
        => $"Highway's data directory '{path}' holds data written by an earlier Highway storage engine{FoundStamp(path)} " +
           "(Highway 2.x, or a 3.0 pre-release on RocksDB). Highway 3.0 stores data in SystemD.Crystal (storage format " +
           $"{HighwayServerBuilder.StorageFormatVersion}) and does not " +
           "migrate in place. Drain the directory with the version that wrote it, then run 'highways --wipe-data' or " +
           "delete the directory, and start 3.0 on the empty directory. In a replica set, follow the 3.0 upgrade " +
           "runbook: the upgraded node starts empty and the other re-syncs from it.";

    /// <summary>What a pre-076 <c>highway.format</c> stamp says, named in the refusal: what was found, not only that it disagreed.</summary>
    private static string FoundStamp(string path)
    {
        try
        {
            var stamp = Path.Combine(path, "highway.format");
            return File.Exists(stamp) ? $" (stamped storage format '{File.ReadAllText(stamp).Trim()}')" : "";
        }
        catch (IOException) { return ""; }
    }

    // ---- the database, as this node currently has it --------------------------------------------------------------

    /// <summary>
    /// The current database and its box map, or null on a standby whose database has not arrived. A handle is never
    /// redirected to a database re-created under the same name (a re-bootstrap), so a stale one is replaced here.
    /// </summary>
    internal DatabaseView? View(bool create = false)
    {
        var view = Volatile.Read(ref _view);
        if (view is not null && !view.Stale) return view;
        lock (_viewGate)
        {
            if (_view is not null && !_view.Stale) return _view;
            CrystalDatabase? db;
            try { db = _crystal.OpenDatabase(DatabaseName); }
            catch (DatabaseNotFoundException) { db = null; }
            if (db is null && create)
                db = _crystal.CreateDatabase(DatabaseName, replicated: true);
            _view = db is null ? null : new DatabaseView(db);
            return _view;
        }
    }

    /// <summary>
    /// Runs <paramref name="use"/> on the current database, re-resolving it when the handle went stale under it: a
    /// standby's database is replaced when Crystal re-bootstraps it (a rejoin, a gap, a divergence), and the old handle
    /// then throws <see cref="DatabaseNotFoundException"/> (Crystal 1.0.0-rc.1). <see cref="ObjectDisposedException"/>
    /// now means only that the store itself closed: that is not staleness and is rethrown.
    /// </summary>
    internal T WithView<T>(bool create, Func<DatabaseView?, T> use)
    {
        for (var attempt = 0; ; attempt++)
        {
            var view = View(create);
            try
            {
                return use(view);
            }
            catch (Exception ex) when (attempt < 2 && view is not null && !_disposed
                                       && ex is DatabaseNotFoundException or BoxDroppedException)
            {
                ForgetView();
            }
        }
    }

    private volatile bool _disposed;

    /// <summary>Forgets the database handle and its box map (epoch change, re-bootstrap): the next use re-resolves both.</summary>
    internal void ForgetView()
    {
        lock (_viewGate)
        {
            if (_view is not null) _view.Stale = true;
            _view = null;
        }
    }

    /// <summary>
    /// Drops every empty Crystal box but the system box's (R2.4): what retirement emptied. Runs on a writable node at
    /// start, before any command, so no reader holds a handle to what it drops.
    /// </summary>
    internal int SweepEmptyBoxes() => WithView(false, view => view is null ? 0 : SweepEmptyBoxes(view));

    private static int SweepEmptyBoxes(DatabaseView view)
    {
        var dropped = 0;
        using var snapshot = view.Database.Snapshot();
        foreach (var info in view.Database.Boxes.List())
        {
            if (!CrystalBoxMap.TryParse(info.Name, out var id) || id == 0) continue;
            if (view.Database.Boxes.Find(info.Name) is not { } box) continue;   // a handle must come from the database
            if (snapshot.Scan(box, [], [], 1).Any()) continue;
            view.Database.Boxes.Drop(box);
            view.Boxes.Forget(id);
            dropped++;
        }
        return dropped;
    }

    // ---- IHighwayStore: snapshot and batch --------------------------------------------------------------------------

    /// <inheritdoc />
    public IStoreSnapshot Snapshot() => WithView(false, view => new CrystalHighwaySnapshot(view, view?.Database.Snapshot()));

    /// <inheritdoc />
    public IStoreBatch NewBatch() => new CrystalHighwayBatch(this);

    /// <inheritdoc />
    public void Dispose()
    {
        _disposed = true;
        Replication.Dispose();
        _crystal.Dispose();
        if (_ownsDirectory && Directory.Exists(_path))
        {
            try { Directory.Delete(_path, recursive: true); } catch { /* best-effort temp cleanup */ }
        }
    }

    // ---- ordered primitives (065) ----------------------------------------------------------------------------------

    /// <inheritdoc />
    public byte[]? Read(IStoreBatch batch, byte[] key) => Cast(batch).Get(key);

    /// <inheritdoc />
    public IReadOnlyList<(byte[] Key, byte[] Value)> Scan(IStoreBatch batch, byte[] prefix, byte[] from, int limit)
    {
        var entries = new List<(byte[], byte[])>();
        if (limit <= 0) return entries;
        foreach (var (key, value) in Cast(batch).Merged(prefix, from, withValues: true, listHint: false))
        {
            entries.Add((key, value!));
            if (entries.Count >= limit) break;
        }
        return entries;
    }

    // ---- reads against a snapshot -----------------------------------------------------------------------------------

    /// <inheritdoc />
    public byte[]? Get(IStoreSnapshot snapshot, byte[] key) => Cast(snapshot).Get(key);

    /// <inheritdoc />
    public byte[]? GetLive(IStoreSnapshot snapshot, byte[] key, long nowTicks)
        => ExpiryFraming.Unwrap(Cast(snapshot).Get(key), nowTicks);

    /// <inheritdoc />
    public long ListLength(IStoreSnapshot snapshot, byte[] key) => Cast(snapshot).Entries(key, key).LongCount();

    /// <inheritdoc />
    public long SortedSetLength(IStoreSnapshot snapshot, byte[] key) => Cast(snapshot).Entries(key, key).LongCount();

    /// <inheritdoc />
    public IReadOnlyList<byte[]> SortedSetRangeByScore(IStoreSnapshot snapshot, byte[] key, long minScore, long maxScore, int limit)
    {
        var results = new List<byte[]>();
        // Members are ordered by score: start at the lower score, stop past the upper one (061 A3).
        foreach (var (k, _) in Cast(snapshot).Entries(key, ComposeZKey(key, minScore, [])))
        {
            if (results.Count >= limit) break;
            if (k.Length < key.Length + 8) continue;
            var score = DecodeScoreAt(k, key.Length);
            if (score < minScore) continue;
            if (score > maxScore) break;
            results.Add(k[(key.Length + 8)..]);
        }
        return results;
    }

    /// <inheritdoc />
    public IReadOnlyList<byte[]> SetMembers(IStoreSnapshot snapshot, byte[] key)
        => [.. Cast(snapshot).Entries(key, key).Select(e => e.Key[key.Length..])];

    // ---- mutations (staged into a batch) ------------------------------------------------------------------------------

    /// <inheritdoc />
    public void Set(IStoreBatch batch, byte[] key, byte[] value) => Cast(batch).Put(key, value);

    /// <inheritdoc />
    public void SetEx(IStoreBatch batch, byte[] key, byte[] value, long expiresAtTicks)
        => Cast(batch).Put(key, ExpiryFraming.Wrap(value, expiresAtTicks));

    /// <inheritdoc />
    public void SweepExpired(IStoreBatch batch, long nowTicks)
    {
        var b = Cast(batch);
        foreach (var (k, framed) in b.Merged(ExpiryFraming.KvFamilyPrefix, ExpiryFraming.KvFamilyPrefix, withValues: true, listHint: false).ToList())
        {
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
        var current = b.Get(key) is { Length: >= 8 } bytes ? BinaryPrimitives.ReadInt64BigEndian(bytes) : 0L;
        var next = current + delta;
        var encoded = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(encoded, next);
        b.Put(key, encoded);
        return next;
    }

    /// <inheritdoc />
    public void ListRightPush(IStoreBatch batch, byte[] key, byte[] value)
    {
        LowerHeadHint(ListPrefixOf(key), key);   // 061 A4: before staging; lowering is always safe
        Cast(batch).Put(key, value);
    }

    /// <inheritdoc />
    public void ListLeftPush(IStoreBatch batch, byte[] key, byte[] value)
    {
        LowerHeadHint(ListPrefixOf(key), key);
        Cast(batch).Put(key, value);
    }

    /// <inheritdoc />
    public byte[]? ListLeftPop(IStoreBatch batch, byte[] key)
    {
        var b = Cast(batch);
        foreach (var (head, value) in b.Merged(key, key, withValues: true, listHint: true))
        {
            b.Delete(head);
            return value;
        }
        return null;
    }

    /// <inheritdoc />
    public IReadOnlyList<byte[]> ListDrain(IStoreBatch batch, byte[] key)
    {
        var b = Cast(batch);
        var entries = b.Merged(key, key, withValues: true, listHint: true).ToList();
        var values = new List<byte[]>(entries.Count);
        foreach (var (k, v) in entries)
        {
            if (v is not null) values.Add(v);
            b.Delete(k);
        }
        return values;
    }

    /// <inheritdoc />
    public IReadOnlyList<(byte[] Key, byte[] Value)> ListEntries(IStoreBatch batch, byte[] key)
        => [.. Cast(batch).Merged(key, key, withValues: true, listHint: true).Select(e => (e.Key, e.Value!))];

    /// <inheritdoc />
    public void SortedSetAdd(IStoreBatch batch, byte[] key, long score, byte[] member)
        => Cast(batch).Put(ComposeZKey(key, score, member), []);

    /// <inheritdoc />
    public void SortedSetRemove(IStoreBatch batch, byte[] key, byte[] member)
    {
        var b = Cast(batch);
        foreach (var (k, _) in b.Merged(key, key, withValues: false, listHint: false).ToList())
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
        var existed = b.Get(memberKey) is not null;
        b.Put(memberKey, []);
        return !existed;
    }

    /// <inheritdoc />
    public void SetRemove(IStoreBatch batch, byte[] key, byte[] member) => Cast(batch).Delete(Concat(key, member));

    /// <inheritdoc />
    public void DeleteRange(IStoreBatch batch, byte[] keyPrefix)
    {
        Cast(batch).DeleteRange(keyPrefix);
        ClearHeadHintsUnder(keyPrefix);   // 061 A4: an emptied list may restart its sequence below the old head
    }

    // ---- 061 A4: list head hints (ported from the RocksDB store; same invariant) ------------------------------------
    //
    // For each list, a key that is <= every live entry of that list, so a pop or list scan starts there instead of at
    // the list's start. Too LOW only costs a longer scan; too HIGH would skip live messages. So: lower at staging time
    // on every push; raise only after a successful commit, by compare-and-swap on the hint the scan started from; clear
    // under a range delete; clear all on any epoch change. Process-local, never persisted.

    private readonly ConcurrentDictionary<byte[], byte[]> _headHints = new(ByteArrayEqualityComparer.Instance);

    internal bool TryGetHeadHint(byte[] listPrefix, out byte[] hint) => _headHints.TryGetValue(listPrefix, out hint!);

    private static byte[] ListPrefixOf(byte[] entryKey) => entryKey[..^8];   // ListEntry = ListPrefix + seq:8

    private void LowerHeadHint(byte[] listPrefix, byte[] entryKey)
    {
        while (true)
        {
            if (_headHints.TryGetValue(listPrefix, out var current))
            {
                if (ByteArrayComparer.Instance.Compare(entryKey, current) >= 0) return;
                if (_headHints.TryUpdate(listPrefix, entryKey, current)) return;
            }
            else if (_headHints.TryAdd(listPrefix, listPrefix)) return;
        }
    }

    private void ClearHeadHintsUnder(byte[] rangePrefix)
    {
        foreach (var listPrefix in _headHints.Keys)
            if (StartsWith(listPrefix, rangePrefix)) _headHints.TryRemove(listPrefix, out _);
    }

    /// <summary>Applies a committed batch's hint effects (061 A4). Called only after the commit succeeded.</summary>
    internal void ApplyHeadHints(CrystalHighwayBatch batch)
    {
        foreach (var range in batch.RangeDeletes)
            ClearHeadHintsUnder(range);

        foreach (var (listPrefix, observed, firstLive) in batch.HintRaises)
        {
            if (batch.RangeDeletes.Exists(r => StartsWith(listPrefix, r))) continue;
            if (observed is null)
                _headHints.TryAdd(listPrefix, firstLive);
            else if (ByteArrayComparer.Instance.Compare(firstLive, observed) > 0)
                _headHints.TryUpdate(listPrefix, firstLive, observed);
        }
    }

    // ---- observability ----------------------------------------------------------------------------------------------

    private long _committedWrites;

    /// <summary>Commits that handed writes to Crystal (empty ones write nothing) — what "an idle poll writes nothing" is measured by.</summary>
    internal long CommittedWrites => Interlocked.Read(ref _committedWrites);

    internal void NoteCommittedWrite() => Interlocked.Increment(ref _committedWrites);

    /// <summary>
    /// A storage figure of database <c>highway</c> under the RocksDB property name the <c>highway.storage.*</c> gauges
    /// (061 T8) have always read, from Crystal's status of the database. Unknown names read 0.
    /// </summary>
    internal long StorageProperty(string property)
    {
        var status = _crystal.Status().Databases.FirstOrDefault(d => d.Name == DatabaseName);
        if (status is null) return 0;
        return property switch
        {
            "rocksdb.num-deletes-active-mem-table" => status.Storage.MemtableDeletes,
            "rocksdb.estimate-num-keys" => status.Storage.EstimatedKeys,
            "rocksdb.estimate-live-data-size" => status.Storage.LiveDataSize,
            _ => 0,
        };
    }

    // ---- test support -----------------------------------------------------------------------------------------------

    /// <summary>
    /// An ordered dump of every Highway key and value (038 T6), for byte-identical comparison between a reference run
    /// and a recovered crash run, and for <c>StoreInspection</c>.
    /// </summary>
    internal List<(byte[] Key, byte[] Value)> DumpData() => WithView(false, view => view is null ? [] : DumpData(view));

    private static List<(byte[] Key, byte[] Value)> DumpData(DatabaseView view)
    {
        var result = new List<(byte[], byte[])>();
        using var snapshot = view.Database.Snapshot();
        var boxes = new List<(uint Id, CrystalBox Box)>();
        foreach (var info in view.Database.Boxes.List())
            if (CrystalBoxMap.TryParse(info.Name, out var id) && view.Database.Boxes.Find(info.Name) is { } box)
                boxes.Add((id, box));
        foreach (var (id, box) in boxes.OrderBy(b => b.Id))
            foreach (var kv in snapshot.Scan(box, [], [], int.MaxValue))
                result.Add((CrystalBoxMap.Join(id, kv.Key), kv.Value));
        return result;
    }

    // ---- helpers ----------------------------------------------------------------------------------------------------

    private static CrystalHighwaySnapshot Cast(IStoreSnapshot s)
        => s as CrystalHighwaySnapshot ?? throw new ArgumentException("Snapshot from a different store.", nameof(s));

    private static CrystalHighwayBatch Cast(IStoreBatch b)
        => b as CrystalHighwayBatch ?? throw new ArgumentException("Batch from a different store.", nameof(b));

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
        BinaryPrimitives.WriteUInt64BigEndian(r.AsSpan(prefix.Length, 8), (ulong)score ^ 0x8000_0000_0000_0000UL);
        member.CopyTo(r, prefix.Length + 8);
        return r;
    }

    private static long DecodeScoreAt(byte[] key, int offset)
        => (long)(BinaryPrimitives.ReadUInt64BigEndian(key.AsSpan(offset, 8)) ^ 0x8000_0000_0000_0000UL);
}

/// <summary>One open handle of database <c>highway</c> and the Crystal boxes resolved through it.</summary>
internal sealed class DatabaseView(CrystalDatabase database)
{
    public CrystalDatabase Database { get; } = database;
    public CrystalBoxMap Boxes { get; } = new(database);
    public volatile bool Stale;
}
