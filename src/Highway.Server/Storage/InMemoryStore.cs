using System.Buffers.Binary;

namespace Highway.Server.Storage;

/// <summary>
/// The in-memory implementation of <see cref="IHighwayStore"/> (037 R3.3). Written
/// <b>first</b> (038 T2), so the contract is defined by what the commands need rather
/// than by what RocksDB happens to do. It exists to define semantics and to make 039's
/// command tests fast — simplicity over speed.
///
/// <para>Shape (038 design): a per-store lock + a <see cref="SortedDictionary{TKey,TValue}"/>
/// keyed by a bytewise comparer, so <b>ordering behaviour is identical to RocksDB's by
/// construction</b> — which is exactly what the contract suite exercises. A batch stages
/// into an overlay and applies on commit; a snapshot is a cheap copy of the map.</para>
///
/// <para>This is not durable and has no WAL — the crash/replay guarantees (037 R5) are a
/// RocksDB property, proven against <c>RocksDbStore</c>. What <c>InMemoryStore</c> proves
/// is the <i>semantics</i>: FIFO order, in-batch read-your-writes, atomic commit, range
/// bounds, self-delimiting prefixes.</para>
/// </summary>
public sealed class InMemoryStore : IHighwayStore
{
    private readonly Lock _gate = new();

    // The whole keyspace, byte-ordered — mirrors RocksDB's single ordered keyspace.
    // Empty value (Array.Empty) is a present key with no payload (set members, zset members).
    private readonly SortedDictionary<byte[], byte[]> _map = new(ByteArrayComparer.Instance);

    /// <inheritdoc />
    public IStoreSnapshot Snapshot()
    {
        lock (_gate)
        {
            // A snapshot is a point-in-time copy. Cheap enough for a semantics reference.
            var copy = new SortedDictionary<byte[], byte[]>(_map, ByteArrayComparer.Instance);
            return new InMemorySnapshot(copy);
        }
    }

    /// <inheritdoc />
    public IStoreBatch NewBatch() => new InMemoryBatch(this);

    /// <inheritdoc />
    public void Dispose() { }

    // ---- reads (against a snapshot) ------------------------------------------

    /// <inheritdoc />
    public byte[]? Get(IStoreSnapshot snapshot, byte[] key)
    {
        var snap = Cast(snapshot);
        return snap.Map.TryGetValue(key, out var value) ? value : null;
    }

    /// <inheritdoc />
    public long ListLength(IStoreSnapshot snapshot, byte[] key)
        => CountPrefix(Cast(snapshot).Map, key);

    /// <inheritdoc />
    public long SortedSetLength(IStoreSnapshot snapshot, byte[] key)
        => CountPrefix(Cast(snapshot).Map, key);

    /// <inheritdoc />
    public IReadOnlyList<byte[]> SortedSetRangeByScore(
        IStoreSnapshot snapshot, byte[] key, long minScore, long maxScore, int limit)
    {
        var map = Cast(snapshot).Map;
        var results = new List<byte[]>();

        // Keys under this zset are: <key><score:8 order-preserving><member>. The score
        // sits immediately after the prefix; decode it and filter by [min,max].
        foreach (var kv in map)
        {
            if (results.Count >= limit) break;
            if (!StartsWith(kv.Key, key)) continue;
            if (kv.Key.Length < key.Length + 8) continue;

            var score = DecodeScoreAt(kv.Key, key.Length);
            if (score < minScore || score > maxScore) continue;

            // Return the member (the bytes after prefix+score), matching the seam contract
            // that ranges return members without scores.
            var member = kv.Key[(key.Length + 8)..];
            results.Add(member);
        }

        return results;
    }

    /// <inheritdoc />
    public IReadOnlyList<byte[]> SetMembers(IStoreSnapshot snapshot, byte[] key)
    {
        var map = Cast(snapshot).Map;
        var results = new List<byte[]>();
        foreach (var kv in map)
        {
            if (!StartsWith(kv.Key, key)) continue;
            results.Add(kv.Key[key.Length..]); // the member is the suffix after the set prefix
        }
        return results;
    }

    // ---- mutations (staged into a batch) -------------------------------------
    // The seam's mutators take an IStoreBatch; the batch records intents and, on Commit,
    // applies them under the store lock. Reads issued *by the batch* (ListLeftPop, Increment)
    // see earlier staged writes — the read-your-own-writes contract (R2, WBWI in RocksDB).

    /// <inheritdoc />
    public void Set(IStoreBatch batch, byte[] key, byte[] value) => Cast(batch).Put(key, value);

    /// <inheritdoc />
    public void SetEx(IStoreBatch batch, byte[] key, byte[] value, long expiresAtTicks)
        => Cast(batch).Put(key, ExpiryFraming.Wrap(value, expiresAtTicks));

    /// <inheritdoc />
    public byte[]? GetLive(IStoreSnapshot snapshot, byte[] key, long nowTicks)
    {
        var framed = Cast(snapshot).Map.TryGetValue(key, out var v) ? v : null;
        return ExpiryFraming.Unwrap(framed, nowTicks);
    }

    /// <inheritdoc />
    public void SweepExpired(IStoreBatch batch, long nowTicks)
    {
        var b = Cast(batch);
        // Scan the KV family prefix; delete any framed key whose expiry has passed.
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
        var current = b.ReadThrough(key) is { } bytes && bytes.Length >= 8
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
        // Score is unknown to the caller; find the member under the prefix and delete it.
        var b = Cast(batch);
        foreach (var k in b.AllUnderPrefix(key))
        {
            if (k.Length < key.Length + 8) continue;
            var m = k[(key.Length + 8)..];
            if (m.AsSpan().SequenceEqual(member))
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
        var b = Cast(batch);
        foreach (var k in b.AllUnderPrefix(keyPrefix))
            b.Delete(k);
    }

    // ---- commit -------------------------------------------------------------

    internal void Apply(IReadOnlyList<(byte[] Key, byte[]? Value)> ops)
    {
        lock (_gate)
        {
            foreach (var (key, value) in ops)
            {
                if (value is null) _map.Remove(key);
                else _map[key] = value;
            }
        }
    }

    /// <summary>A read against the committed store, for a batch's read-through fallback.</summary>
    internal byte[]? CommittedGet(byte[] key)
    {
        lock (_gate)
            return _map.TryGetValue(key, out var v) ? v : null;
    }

    /// <summary>Committed keys under a prefix, ordered — for a batch's read-through fallback.</summary>
    internal List<byte[]> CommittedKeysUnderPrefix(byte[] prefix)
    {
        lock (_gate)
        {
            var result = new List<byte[]>();
            foreach (var k in _map.Keys)
                if (StartsWith(k, prefix)) result.Add(k);
            return result;
        }
    }

    /// <summary>
    /// The whole committed keyspace, byte-ordered — the same shape as
    /// <c>RocksDbStore.DumpData</c>, so a command's effect on this store can be compared
    /// byte-for-byte against a second run (039 T8 / R5.3, the command-layer determinism proof).
    /// </summary>
    internal List<(byte[] Key, byte[] Value)> DumpData()
    {
        lock (_gate)
            return [.. _map.Select(kv => ((byte[])kv.Key.Clone(), (byte[])kv.Value.Clone()))];
    }

    // ---- helpers ------------------------------------------------------------

    private static InMemorySnapshot Cast(IStoreSnapshot s)
        => s as InMemorySnapshot ?? throw new ArgumentException("Snapshot from a different store.", nameof(s));

    private static InMemoryBatch Cast(IStoreBatch b)
        => b as InMemoryBatch ?? throw new ArgumentException("Batch from a different store.", nameof(b));

    private static long CountPrefix(SortedDictionary<byte[], byte[]> map, byte[] prefix)
    {
        long n = 0;
        foreach (var k in map.Keys)
            if (StartsWith(k, prefix)) n++;
        return n;
    }

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
        // Order-preserving score encoding, matching KeyEncoding.WriteScore.
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

}

/// <summary>Bytewise ordinal comparer — the single ordered keyspace RocksDB gives for free.</summary>
internal sealed class ByteArrayComparer : IComparer<byte[]>
{
    public static readonly ByteArrayComparer Instance = new();
    public int Compare(byte[]? x, byte[]? y) => x!.AsSpan().SequenceCompareTo(y!);
}
