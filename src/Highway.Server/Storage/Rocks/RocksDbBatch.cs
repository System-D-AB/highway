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
/// </summary>
internal sealed class RocksDbBatch : IStoreBatch
{
    private readonly RocksDbStore _store;

    // Ordered overlay so merged prefix scans interleave with committed keys correctly.
    internal SortedDictionary<byte[], byte[]?> Overlay { get; } = new(ByteArrayComparer.Instance);

    // Prefixes range-deleted in this batch, applied as native DeleteRange at commit.
    internal List<byte[]> RangeDeletes { get; } = [];

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

    internal byte[]? FirstUnderPrefix(byte[] prefix)
    {
        foreach (var k in MergedKeysUnderPrefix(prefix))
            return k; // yielded in byte order
        return null;
    }

    internal List<byte[]> AllUnderPrefix(byte[] prefix) => [.. MergedKeysUnderPrefix(prefix)];

    private IEnumerable<byte[]> MergedKeysUnderPrefix(byte[] prefix)
    {
        var seen = new SortedDictionary<byte[], bool>(ByteArrayComparer.Instance);

        foreach (var k in _store.CommittedKeysUnderPrefix(prefix))
            seen[k] = true;

        foreach (var kv in Overlay)
        {
            if (!RocksDbStore.StartsWith(kv.Key, prefix)) continue;
            if (kv.Value is null) seen.Remove(kv.Key);
            else seen[kv.Key] = true;
        }

        return seen.Keys;
    }

    // ---- commit ----

    public void Commit()
    {
        if (_committed) throw new InvalidOperationException("Batch already committed.");
        _committed = true;
        _store.Commit(this);
    }

    public void Dispose() { /* uncommitted overlay discarded */ }
}
