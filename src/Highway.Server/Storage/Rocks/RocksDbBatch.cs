using Highway.Server.Storage.Layout;

namespace Highway.Server.Storage.Rocks;

/// <summary>
/// Stages mutations for <see cref="RocksDbStore"/> and applies them in a single native
/// <c>WriteBatch</c> on <see cref="Commit"/> — the one commit point (037 R4.1). Reads
/// issued through the batch see staged writes first, then the committed DB: the
/// read-your-own-writes contract (038 R0.4), implemented as a managed overlay rather
/// than <c>WriteBatchWithIndex</c> (see <see cref="RocksDbStore"/> remarks).
///
/// <para>An overlay entry with a <c>null</c> value is a staged delete (tombstone),
/// distinct from an absent key. Disposing without committing discards everything
/// (037 R4.2) — the DB is untouched until <see cref="Commit"/>.</para>
///
/// <para><b>Scans stream (061).</b> A merged prefix scan walks the committed iterator and the
/// overlay together, lazily, so <see cref="FirstUnderPrefix"/> stops at the first live key instead
/// of materialising every key in the list — a pop used to cost O(list length). List scans may start
/// from the store's head hint (061 A4) and record, for after the commit, the first live key they
/// saw, so the next scan of that list starts there instead of re-walking deleted history.</para>
/// </summary>
internal sealed class RocksDbBatch : IStoreBatch
{
    private readonly RocksDbStore _store;

    // Ordered overlay so merged prefix scans interleave with committed keys correctly.
    internal SortedDictionary<byte[], byte[]?> Overlay { get; } = new(ByteArrayComparer.Instance);

    // Prefixes range-deleted in this batch, applied as native DeleteRange at commit.
    internal List<byte[]> RangeDeletes { get; } = [];

    // 061 A4: list head hints to raise once this batch has committed — (list prefix, the hint the
    // scan started from or null, the first live key it found). Raised with compare-and-swap.
    internal List<(byte[] Prefix, byte[]? Observed, byte[] FirstLive)> HintRaises { get; } = [];

    private bool _committed;

    internal RocksDbBatch(RocksDbStore store) => _store = store;

    // ---- staging ----

    internal void Put(byte[] key, byte[] value) => Overlay[key] = value;

    internal void Delete(byte[] key) => Overlay[key] = null;

    internal void MarkRangeDeleted(byte[] prefix) => RangeDeletes.Add(prefix);

    // ---- read-your-own-writes ----

    internal byte[]? ReadThrough(byte[] key)
    {
        if (Overlay.TryGetValue(key, out var staged))
            return staged; // null == staged-deleted → "gone"
        return _store.CommittedGet(key);
    }

    /// <summary>The first live key under <paramref name="prefix"/>; stops there (061).</summary>
    internal byte[]? FirstUnderPrefix(byte[] prefix, bool listHint = false)
    {
        foreach (var (key, _) in Merged(prefix, withValues: false, listHint))
            return key;
        return null;
    }

    internal List<byte[]> AllUnderPrefix(byte[] prefix, bool listHint = false)
    {
        var keys = new List<byte[]>();
        foreach (var (key, _) in Merged(prefix, withValues: false, listHint))
            keys.Add(key);
        return keys;
    }

    /// <summary>Live entries under <paramref name="prefix"/> from <paramref name="from"/> on, at most <paramref name="limit"/> (065).</summary>
    internal List<(byte[] Key, byte[] Value)> EntriesFrom(byte[] prefix, byte[] from, int limit)
    {
        var entries = new List<(byte[], byte[])>();
        if (limit <= 0) return entries;
        foreach (var (key, value) in Merged(prefix, withValues: true, listHint: false, from))
        {
            entries.Add((key, value!));
            if (entries.Count >= limit) break;
        }
        return entries;
    }

    /// <summary>Every live entry under <paramref name="prefix"/>, in key order, with its value (061 A1).</summary>
    internal List<(byte[] Key, byte[] Value)> EntriesUnderPrefix(byte[] prefix, bool listHint = false)
    {
        var entries = new List<(byte[], byte[])>();
        foreach (var (key, value) in Merged(prefix, withValues: true, listHint))
            entries.Add((key, value!));
        return entries;
    }

    /// <summary>
    /// Merges the committed range (bounded, optionally from the head hint) with this batch's staged
    /// writes under <paramref name="prefix"/>, in key order; a staged write wins over the committed
    /// value and a staged delete hides the key. Lazy: abandoning the enumeration disposes the native
    /// iterator.
    /// </summary>
    private IEnumerable<(byte[] Key, byte[]? Value)> Merged(byte[] prefix, bool withValues, bool listHint, byte[]? from = null)
    {
        byte[]? observed = null;
        var seekFrom = from is not null && ByteArrayComparer.Instance.Compare(from, prefix) > 0 ? from : prefix;
        if (listHint && _store.TryGetHeadHint(prefix, out var hint))
        {
            observed = hint;
            seekFrom = hint;
        }

        // The overlay is small (one command's writes); take its slice under the prefix.
        var staged = new List<KeyValuePair<byte[], byte[]?>>();
        foreach (var kv in Overlay)
            if (RocksDbStore.StartsWith(kv.Key, prefix) && ByteArrayComparer.Instance.Compare(kv.Key, seekFrom) >= 0)
                staged.Add(kv);

        using var committed = _store.CommittedEntries(prefix, seekFrom, withValues).GetEnumerator();
        var hasCommitted = committed.MoveNext();
        var s = 0;
        var first = true;

        while (hasCommitted || s < staged.Count)
        {
            int order;
            if (!hasCommitted) order = 1;
            else if (s >= staged.Count) order = -1;
            else order = ByteArrayComparer.Instance.Compare(committed.Current.Key, staged[s].Key);

            byte[] key;
            byte[]? value;
            if (order < 0)
            {
                (key, value) = committed.Current;
                hasCommitted = committed.MoveNext();
            }
            else
            {
                key = staged[s].Key;
                value = staged[s].Value;
                s++;
                if (order == 0) hasCommitted = committed.MoveNext();   // the staged write shadows the committed one
                if (value is null) continue;                            // staged delete
            }

            if (first)
            {
                first = false;
                if (listHint) HintRaises.Add((prefix, observed, key));
            }
            yield return (key, withValues ? value : null);
        }
    }

    // ---- commit ----

    public void Commit()
    {
        if (_committed) throw new InvalidOperationException("Batch already committed.");
        _committed = true;
        _store.Commit(this);
        _store.ApplyHeadHints(this);   // only after the write succeeded (061 A4)
    }

    public void Dispose() { /* uncommitted overlay discarded */ }
}
