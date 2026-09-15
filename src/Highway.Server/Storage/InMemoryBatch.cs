namespace Highway.Server.Storage;

/// <summary>
/// Stages mutations for the in-memory store and applies them atomically on
/// <see cref="Commit"/> — the single commit point (037 R4.1). Reads issued through the
/// batch (<c>ListLeftPop</c>, <c>Increment</c>, <c>SetAdd</c>'s existence check) see the
/// batch's own staged writes first, then fall through to the committed store: the
/// read-your-own-writes contract that RocksDB's <c>WriteBatchWithIndex</c> provides
/// (038 R0.4). Without it, a pop-after-push or an in-batch seq reservation in the same
/// command would not see what the command just staged.
///
/// <para>An overlay entry with a <c>null</c> value is a staged delete (a tombstone),
/// distinct from an absent key — a read-through must treat a staged delete as "gone",
/// not "fall through to committed".</para>
///
/// <para>Disposing without committing discards every staged mutation (037 R4.2): the
/// store is never touched until <see cref="Commit"/> runs.</para>
/// </summary>
internal sealed class InMemoryBatch : IStoreBatch
{
    private readonly InMemoryStore _store;

    // Ordered so prefix scans over staged-and-committed keys interleave correctly.
    // Value null == staged delete (tombstone).
    private readonly SortedDictionary<byte[], byte[]?> _overlay = new(ByteArrayComparer.Instance);

    private bool _committed;

    internal InMemoryBatch(InMemoryStore store) => _store = store;

    // ---- staging ----

    internal void Put(byte[] key, byte[] value) => _overlay[key] = value;

    internal void Delete(byte[] key) => _overlay[key] = null; // tombstone

    // ---- read-your-own-writes: overlay first, then committed ----

    /// <summary>Reads a key as this batch currently sees it — staged write, staged delete, or committed.</summary>
    internal byte[]? ReadThrough(byte[] key)
    {
        if (_overlay.TryGetValue(key, out var staged))
            return staged; // null here means staged-deleted → "gone"
        return _store.CommittedGet(key);
    }

    /// <summary>The first live key under a prefix (staged + committed merged), or null.</summary>
    internal byte[]? FirstUnderPrefix(byte[] prefix)
    {
        byte[]? best = null;
        foreach (var k in MergedKeysUnderPrefix(prefix))
        {
            best = k;
            break; // MergedKeysUnderPrefix yields in byte order
        }
        return best;
    }

    /// <summary>Every live key under a prefix (staged + committed merged), in byte order.</summary>
    internal List<byte[]> AllUnderPrefix(byte[] prefix) => [.. MergedKeysUnderPrefix(prefix)];

    private IEnumerable<byte[]> MergedKeysUnderPrefix(byte[] prefix)
    {
        // Committed keys minus staged tombstones, plus staged puts — all under the prefix,
        // yielded in byte order. Correctness over speed (this is the semantics reference).
        var seen = new SortedDictionary<byte[], bool>(ByteArrayComparer.Instance);

        foreach (var k in _store.CommittedKeysUnderPrefix(prefix))
            seen[k] = true;

        foreach (var kv in _overlay)
        {
            if (!InMemoryStore.StartsWith(kv.Key, prefix)) continue;
            if (kv.Value is null) seen.Remove(kv.Key); // staged delete
            else seen[kv.Key] = true;                  // staged put
        }

        return seen.Keys;
    }

    // ---- commit ----

    public void Commit()
    {
        if (_committed) throw new InvalidOperationException("Batch already committed.");
        _committed = true;

        var ops = new List<(byte[] Key, byte[]? Value)>(_overlay.Count);
        foreach (var kv in _overlay)
            ops.Add((kv.Key, kv.Value));

        _store.Apply(ops);
    }

    public void Dispose() { /* uncommitted overlay is simply discarded */ }
}
