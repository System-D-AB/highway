using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using SystemD.Crystal;
using SystemD.Crystal.Encoding;
using CrystalBatch = SystemD.Crystal.Batch;
using CrystalBox = SystemD.Crystal.Box;
using CrystalSnapshot = SystemD.Crystal.Snapshot;

namespace Highway.Server.Storage.Crystal;

/// <summary>
/// Highway box id ↔ Crystal box (076 R2.2): Highway box <c>n</c> is the Crystal box named <c>b/{n:x8}</c>. Resolved
/// boxes are cached per database handle. A box is created the first time its id is written (R2.3) — Crystal's own
/// synced catalogue commit, made before the write is staged. Every box is <see cref="BoxProfile.DeleteHeavy"/>, the
/// system box too: it holds reply slots, idempotency markers and the expiry index, and v3.0 tuned all data for deletes.
/// </summary>
internal sealed class CrystalBoxMap(CrystalDatabase database)
{
    private readonly ConcurrentDictionary<uint, CrystalBox> _boxes = new();
    private readonly Lock _create = new();

    public static string Name(uint id) => "b/" + id.ToString("x8", CultureInfo.InvariantCulture);

    public static bool TryParse(string name, out uint id)
    {
        id = 0;
        return name.Length == 10 && name.StartsWith("b/", StringComparison.Ordinal)
            && uint.TryParse(name.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out id);
    }

    /// <summary>The Highway box id a key or prefix names (its first four bytes).</summary>
    public static uint IdOf(byte[] key)
    {
        if (key.Length < 4)
            throw new ArgumentException("A Highway key starts with its 4-byte box id (065).", nameof(key));
        return BinaryPrimitives.ReadUInt32BigEndian(key);
    }

    /// <summary>The Highway key for <paramref name="rest"/> in box <paramref name="id"/>.</summary>
    public static byte[] Join(uint id, ReadOnlySpan<byte> rest)
    {
        var key = new byte[4 + rest.Length];
        BinaryPrimitives.WriteUInt32BigEndian(key, id);
        rest.CopyTo(key.AsSpan(4));
        return key;
    }

    /// <summary>The live Crystal box for <paramref name="id"/>, or null if it was never written.</summary>
    public CrystalBox? Find(uint id)
    {
        if (_boxes.TryGetValue(id, out var box)) return box;
        if (database.Boxes.Find(Name(id)) is not { } found) return null;
        _boxes[id] = found;
        return found;
    }

    /// <summary>The Crystal box for <paramref name="id"/>, created if it does not exist (R2.3).</summary>
    public CrystalBox Ensure(uint id)
    {
        if (Find(id) is { } box) return box;
        lock (_create)
        {
            if (Find(id) is { } raced) return raced;
            CrystalBox created;
            try
            {
                created = database.Boxes.Create(Name(id), BoxProfile.DeleteHeavy);
            }
            catch (BoxNameTakenException)
            {
                created = database.Boxes.Find(Name(id)) ?? throw new InvalidOperationException($"Box {Name(id)} vanished while it was created.");
            }
            _boxes[id] = created;
            return created;
        }
    }

    public void Forget(uint id) => _boxes.TryRemove(id, out _);
}

/// <summary>
/// One command's writes (076 R2.5): one Crystal batch across every box the command touches, committed
/// <see cref="DurabilityMode.Strict"/>. Reads through it see its own staged writes (Crystal's batch reads its own
/// writes). Disposing without committing discards everything.
/// </summary>
internal sealed class CrystalHighwayBatch : IStoreBatch
{
    private readonly CrystalHighwayStore _store;
    private DatabaseView? _view;
    private CrystalBatch? _batch;
    private bool _committed;
    private bool _staged;

    /// <summary>Prefixes range-deleted in this batch (for the head hints).</summary>
    internal List<byte[]> RangeDeletes { get; } = [];

    /// <summary>061 A4: list head hints to raise once this batch commits — (list prefix, hint started from, first live key).</summary>
    internal List<(byte[] Prefix, byte[]? Observed, byte[] FirstLive)> HintRaises { get; } = [];

    internal CrystalHighwayBatch(CrystalHighwayStore store)
    {
        _store = store;
        (_view, _batch) = store.WithView(false, view => (view, view?.Database.NewBatch()));
    }

    /// <summary>The database to write: created on a primary's first write; refused by Crystal on a standby.</summary>
    private (DatabaseView View, CrystalBatch Batch) Writable()
    {
        if (_batch is null)
            (_view, _batch) = _store.WithView(true, view => (view, view!.Database.NewBatch()));
        return (_view!, _batch);
    }

    internal byte[]? Get(byte[] key)
    {
        if (_batch is null) return null;
        var id = CrystalBoxMap.IdOf(key);
        try
        {
            return _view!.Boxes.Find(id) is { } box ? _batch.Get(box, key.AsSpan(4)) : null;
        }
        catch (BoxDroppedException)
        {
            _view!.Boxes.Forget(id);   // dropped since it was cached (review L5): look it up again
            return _view.Boxes.Find(id) is { } box ? _batch.Get(box, key.AsSpan(4)) : null;
        }
    }

    internal void Put(byte[] key, byte[] value)
    {
        var id = CrystalBoxMap.IdOf(key);
        var (view, batch) = Writable();
        try
        {
            batch.Put(view.Boxes.Ensure(id), key.AsSpan(4), value);
        }
        catch (BoxDroppedException)
        {
            view.Boxes.Forget(id);
            batch.Put(view.Boxes.Ensure(id), key.AsSpan(4), value);
        }
        _staged = true;
    }

    internal void Delete(byte[] key)
    {
        var id = CrystalBoxMap.IdOf(key);
        if (_batch is null || _view!.Boxes.Find(id) is not { } box) return;   // never written: nothing to delete
        _batch.Delete(box, key.AsSpan(4));
        _staged = true;
    }

    /// <summary>
    /// Stages the deletion of every key starting with <paramref name="prefix"/> — a whole Highway box when the prefix is
    /// its four id bytes (R2.4: retirement, atomic with the rest of the batch).
    /// </summary>
    internal void DeleteRange(byte[] prefix)
    {
        var id = CrystalBoxMap.IdOf(prefix);
        RangeDeletes.Add(prefix);
        if (_batch is null || _view!.Boxes.Find(id) is not { } box) return;
        var rest = prefix.AsSpan(4);
        if (rest.IsEmpty) _batch.DeleteRange(box, [], []);
        else _batch.DeleteRange(box, rest, KeyRanges.PrefixEnd(rest));
        _staged = true;
    }

    /// <summary>
    /// Live entries under <paramref name="prefix"/> from <paramref name="from"/> on (or from the list's head hint), in key
    /// order, staged writes merged over committed ones. Lazy: stop enumerating and nothing more is read.
    /// </summary>
    internal IEnumerable<(byte[] Key, byte[]? Value)> Merged(byte[] prefix, byte[] from, bool withValues, bool listHint)
    {
        var id = CrystalBoxMap.IdOf(prefix);
        byte[]? observed = null;
        var seekFrom = ByteArrayComparer.Instance.Compare(from, prefix) > 0 ? from : prefix;
        if (listHint && _store.TryGetHeadHint(prefix, out var hint))
        {
            observed = hint;
            seekFrom = hint;
        }

        if (_batch is null || _view!.Boxes.Find(id) is not { } box) yield break;

        var to = KeyRanges.PrefixEnd(prefix.AsSpan(4));
        var restFrom = seekFrom.AsSpan(4).ToArray();
        if (to.Length > 0 && ByteArrayComparer.Instance.Compare(restFrom, to) >= 0) yield break;

        var first = true;
        foreach (var kv in _batch.Scan(box, restFrom, to, int.MaxValue))
        {
            var key = CrystalBoxMap.Join(id, kv.Key);
            if (first)
            {
                first = false;
                if (listHint) HintRaises.Add((prefix, observed, key));
            }
            yield return (key, withValues ? kv.Value : null);
        }
    }

    public void Commit()
    {
        if (_committed) throw new InvalidOperationException("Batch already committed.");
        _committed = true;
        if (_staged)
        {
            _batch!.Commit(DurabilityMode.Strict);
        }
        // else: an empty batch writes and syncs nothing (Highway 059; Crystal R6.3)
        _store.ApplyHeadHints(this);             // only after the commit succeeded (061 A4)
    }

    public void Dispose() => _batch?.Dispose();
}

/// <summary>A point-in-time read view (037 R4) over one Crystal snapshot of database <c>highway</c>; empty when none exists yet.</summary>
internal sealed class CrystalHighwaySnapshot(DatabaseView? view, CrystalSnapshot? snapshot) : IStoreSnapshot
{
    internal byte[]? Get(byte[] key)
    {
        if (snapshot is null) return null;
        var id = CrystalBoxMap.IdOf(key);
        return view!.Boxes.Find(id) is { } box ? snapshot.Get(box, key.AsSpan(4)) : null;
    }

    /// <summary>Entries under <paramref name="prefix"/> from <paramref name="from"/> on, in key order.</summary>
    internal IEnumerable<(byte[] Key, byte[] Value)> Entries(byte[] prefix, byte[] from)
    {
        if (snapshot is null) yield break;
        var id = CrystalBoxMap.IdOf(prefix);
        if (view!.Boxes.Find(id) is not { } box) yield break;
        var seekFrom = ByteArrayComparer.Instance.Compare(from, prefix) > 0 ? from : prefix;
        var to = KeyRanges.PrefixEnd(prefix.AsSpan(4));
        var restFrom = seekFrom.AsSpan(4).ToArray();
        if (to.Length > 0 && ByteArrayComparer.Instance.Compare(restFrom, to) >= 0) yield break;
        foreach (var kv in snapshot.Scan(box, restFrom, to, int.MaxValue))
            yield return (CrystalBoxMap.Join(id, kv.Key), kv.Value);
    }

    public void Dispose() => snapshot?.Dispose();
}
