using System.Buffers.Binary;

namespace Highway.Server.Storage.Rocks;

/// <summary>
/// The primary's most recently committed WAL batches, in memory, so a caught-up replica's pull never
/// re-reads the WAL (feature 062).
///
/// <para><b>Why.</b> <c>GetUpdatesSince(seq)</c> finds the WAL file containing <c>seq</c> and reads it
/// from the file's start. The live file only rolls on a memtable flush — hours on a lightly written
/// broker — so every pull re-read a file that kept growing: 1.5 ms at 2k writes, 54 ms at 120k, and a
/// caught-up replica pulls every 50 ms. Here a pull is a binary search and a short copy.</para>
///
/// <para><b>Same bytes as the WAL.</b> After <c>RocksDb.Write</c> returns, a <c>WriteBatch</c>'s
/// representation carries the sequence RocksDB assigned, and it is byte-identical to the WAL record
/// <c>GetUpdatesSince</c> yields (verified against the binding). One difference: RocksDB's group commit
/// merges concurrent committers into one WAL record, while this holds them one by one — the same
/// sequences and operations, finer boundaries. So a replica fed from here can stand inside a WAL record;
/// the WAL path trims such a record (<see cref="WriteBatchTrim"/>).</para>
///
/// <para><b>Never a gap.</b> Committers add after their write, so adds can arrive out of order and a
/// batch can be written but not yet added. Batches are kept sorted by sequence; a read returns only a
/// contiguous run starting at a batch that begins at exactly <c>fromSeq + 1</c>, and stops at the first hole.
/// Anything this cannot answer — the first batch missing (evicted, never captured, older history) —
/// is a miss, and the caller falls back to the WAL path with its gap checks unchanged.</para>
///
/// <para>Bounded by <see cref="MaxBytes"/> and <see cref="MaxBatches"/>, oldest evicted first. Cleared on
/// any epoch or role change. Capture is off until the first pull, so a broker with no replica pays
/// nothing.</para>
/// </summary>
internal sealed class ReplicationTail(long maxBytes = ReplicationTail.DefaultMaxBytes, int maxBatches = ReplicationTail.DefaultMaxBatches)
{
    internal const long DefaultMaxBytes = 16L * 1024 * 1024;
    internal const int DefaultMaxBatches = 131_072;

    private readonly record struct Entry(ulong Seq, ulong LastSeq, byte[] Data);

    private readonly Lock _gate = new();
    private Entry[] _ring = new Entry[1024];   // circular, sorted by Seq from _head
    private int _head;
    private int _count;
    private long _bytes;
    private volatile bool _capturing;

    public long MaxBytes { get; } = maxBytes;
    public int MaxBatches { get; } = maxBatches;

    /// <summary>Whether commits are being captured (set by the first pull).</summary>
    public bool Capturing => _capturing;

    internal int Count { get { lock (_gate) return _count; } }
    internal long Bytes { get { lock (_gate) return _bytes; } }

    /// <summary>Start capturing. Idempotent; called on every pull.</summary>
    public void Enable() => _capturing = true;

    /// <summary>Drop everything (epoch or role change). Capture stays as it was.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            Array.Clear(_ring);
            _head = 0;
            _count = 0;
            _bytes = 0;
        }
    }

    /// <summary>
    /// Record a batch just written. <paramref name="data"/> is the written <c>WriteBatch</c>'s
    /// representation: <c>[8B LE sequence][4B LE count]...</c>. Ownership passes to the tail.
    /// </summary>
    public void Add(byte[] data)
    {
        if (!_capturing || data.Length < 12) return;
        var seq = BinaryPrimitives.ReadUInt64LittleEndian(data);
        var count = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8, 4));
        if (seq == 0) return;                       // not a written batch
        var entry = new Entry(seq, seq + Math.Max(count, 1u) - 1, data);

        lock (_gate)
        {
            if (_count == _ring.Length) Grow();
            // Append, then move back past any later-sequenced entry (a concurrent committer that
            // added first). Out-of-order distance is the number of racing committers: a few steps.
            var i = _count;
            while (i > 0 && At(i - 1).Seq > seq)
            {
                At(i) = At(i - 1);
                i--;
            }
            At(i) = entry;
            _count++;
            _bytes += data.Length;

            while (_count > 1 && (_bytes > MaxBytes || _count > MaxBatches))
            {
                _bytes -= At(0).Data.Length;
                At(0) = default;
                _head = (_head + 1) % _ring.Length;
                _count--;
            }
        }
    }

    /// <summary>
    /// The contiguous run of batches following <paramref name="fromSeq"/>, up to
    /// <paramref name="maxBytes"/> (the first batch always fits). False unless a held batch starts at
    /// exactly <c>fromSeq + 1</c> — the caller must then use the WAL. <paramref name="more"/> is the
    /// first sequence left out by the byte cap, or null.
    /// </summary>
    public bool TryRead(ulong fromSeq, int maxBytes, List<(ulong Seq, byte[] Data)> into, out ulong? more)
    {
        more = null;
        var want = fromSeq + 1;
        lock (_gate)
        {
            // The run must start exactly at the cursor. A batch that merely contains it would be skipped
            // whole by the replica (first sequence <= its watermark); the WAL path trims such a batch.
            var i = Floor(want);
            if (i < 0 || At(i).Seq != want) return false;

            ulong pageBytes = 0;
            var expected = At(i).Seq;
            for (; i < _count; i++)
            {
                var e = At(i);
                if (e.Seq != expected) break;       // a hole: written, not yet added — pull again
                if (into.Count > 0 && pageBytes + (ulong)e.Data.Length > (ulong)maxBytes)
                {
                    more = e.Seq;
                    break;
                }
                into.Add((e.Seq, e.Data));
                pageBytes += (ulong)e.Data.Length;
                expected = e.LastSeq + 1;
            }
            return true;
        }
    }

    /// <summary>Index of the last entry with Seq &lt;= <paramref name="seq"/>, or -1.</summary>
    private int Floor(ulong seq)
    {
        int lo = 0, hi = _count - 1, found = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >>> 1;
            if (At(mid).Seq <= seq) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return found;
    }

    private ref Entry At(int logical) => ref _ring[(_head + logical) % _ring.Length];

    private void Grow()
    {
        var next = new Entry[_ring.Length * 2];
        for (var i = 0; i < _count; i++) next[i] = At(i);
        _ring = next;
        _head = 0;
    }
}
