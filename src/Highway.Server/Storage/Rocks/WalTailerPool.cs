using RocksDbSharp;

namespace Highway.Server.Storage.Rocks;

/// <summary>
/// The WAL read path for a replica that is behind the in-memory tail (feature 066).
///
/// <para><b>Why.</b> <c>GetUpdatesSince</c> re-reads the live WAL file from its start on every call, so a
/// replica catching up page by page paid O(file position) per page — O(pages²) for the whole catch-up
/// (measured: 300 000 batches, 35 pages, 1.5 s; the last page 86 ms). A <see cref="WalTailer"/> opened
/// once continues from where it stopped: the same catch-up is 0.2 s, and no page after the first costs
/// more than a chunk read.</para>
///
/// <para><b>How.</b> <c>HW.REPL.PULL</c> carries no replica id, so tailers are held by the sequence they
/// will serve next: a pull from <c>fromSeq</c> continues the tailer whose next sequence is
/// <c>fromSeq + 1</c>, and otherwise opens a fresh one (one O(file position) read, as before). A tailer is
/// taken out of the pool while it is read, so concurrent pulls never share one. The pool is small and
/// cleared on any epoch or role change, like the tail. An idle tailer is closed after
/// <see cref="IdleTimeout"/>; a replica that is catching up pulls continuously, so it never idles out.</para>
///
/// <para><b>Never a tailer over a missing WAL</b> (probed on 11.8.1). A tailer opened on a range the WAL no
/// longer holds — a replica away past retention, a pull just after a primary restart — crashes the
/// process in native code on its first read, where <c>GetUpdatesSince</c> simply returns nothing. So a
/// fresh tailer is opened only after the safe reader has confirmed the WAL still holds its first
/// sequence (otherwise the caller refuses the pull as a gap, as before), and while any tailer exists WAL
/// file deletion is paused (<c>DisableFileDeletions</c>, counted, as snapshots use it), so a held tailer
/// can never step onto a file that retention removed. Retention resumes when the last tailer closes —
/// and a hold never lasts longer than <see cref="MaxHold"/>: past it a tailer is closed after its read
/// instead of being kept, so a replica that pulls forever without catching up cannot pause retention
/// forever (constraint C9.4 — the WAL caps still bound the disk). It then reopens once per hold, and a
/// range retention removed in between is refused as a gap, as before.</para>
///
/// <para><b>What a chunk is</b> (probed on 11.8.1): one serialized write batch — header at the first
/// sequence, count the sum — made of whole WAL records, so it starts at the record that contains the
/// requested sequence. The caller trims it to the cursor exactly as 062 trims a single record.</para>
/// </summary>
internal sealed class WalTailerPool : IDisposable
{
    private const int Capacity = 4;
    private const int MinChunkBytes = 16;
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxHold = TimeSpan.FromSeconds(30);

    private readonly RocksDb _db;
    private readonly ReplicationSource _source;
    private readonly Lock _lock = new();
    private readonly List<(WalTailer Tailer, int MaxBytes, long IdleSince)> _idle = [];
    private readonly Timer _sweep;
    private int _live;   // tailers open (idle or being read); > 0 holds WAL file deletion off
    private long _heldSince;   // when the current hold began (Environment.TickCount64)

    public WalTailerPool(RocksDb db, ReplicationSource source)
    {
        _db = db;
        _source = source;
        _sweep = new Timer(_ => DisposeExpired(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    /// <summary>Tailers opened so far — each one read the WAL file from its start once (066 tests).</summary>
    internal int Opened { get; private set; }

    /// <summary>True while a tailer holds WAL file deletion off (066 tests).</summary>
    internal bool HoldingWal { get { lock (_lock) return _live > 0; } }

    /// <summary>
    /// Reads the next chunk at or after <paramref name="fromSeq"/> + 1: a copy of its bytes and its first
    /// and next sequence, or null when the WAL does not hold that sequence (the caller decides whether
    /// that is "caught up" or a gap).
    /// </summary>
    public (ulong First, ulong Next, byte[] Data)? Read(ulong fromSeq, int maxBytes)
    {
        // The binding refuses a chunk limit below a small floor (2 is refused, 16 accepted; probed on
        // 11.8.1). A record larger than the limit is still served alone, so the floor changes nothing a
        // replica sees; it only keeps a tiny request from building an invalid tailer.
        maxBytes = Math.Max(maxBytes, MinChunkBytes);
        var want = fromSeq + 1;
        DisposeExpired();

        WalTailer? tailer = null;
        lock (_lock)
        {
            var i = _idle.FindIndex(t => t.MaxBytes == maxBytes && t.Tailer.NextSequenceNumber == want);
            if (i >= 0)
            {
                tailer = _idle[i].Tailer;
                _idle.RemoveAt(i);
            }
        }

        tailer ??= Open(want, maxBytes);
        if (tailer is null)
            return null;

        try
        {
            if (!tailer.TryReadChunk(out var chunk))
            {
                Close(tailer);
                return null;
            }

            var result = (chunk.FirstSequenceNumber, chunk.NextSequenceNumber, chunk.Data.ToArray());
            Return(tailer, maxBytes);
            return result;
        }
        catch
        {
            Close(tailer);
            throw;
        }
    }

    /// <summary>A fresh tailer at <paramref name="want"/>, or null when the WAL does not hold it.</summary>
    private WalTailer? Open(ulong want, int maxBytes)
    {
        Hold();   // before the check, so nothing the check saw can be deleted before the tailer opens
        try
        {
            if (!WalHolds(want))
            {
                Release();
                return null;
            }

            var tailer = _source.TailWal(want, maxBytes);
            lock (_lock) Opened++;
            return tailer;
        }
        catch
        {
            Release();
            throw;
        }
    }

    /// <summary>Whether the live WAL holds <paramref name="seq"/>: its first record starts at or before it.</summary>
    private bool WalHolds(ulong seq)
    {
        using var updates = _source.GetWalUpdates(seq).GetEnumerator();
        return updates.MoveNext() && updates.Current.SequenceNumber <= seq;
    }

    private void Hold()
    {
        lock (_lock)
        {
            if (_live++ == 0)
            {
                _db.DisableFileDeletions();
                _heldSince = Environment.TickCount64;
            }
        }
    }

    private void Release()
    {
        lock (_lock)
        {
            if (--_live == 0)
                _db.EnableFileDeletions();
        }
    }

    private void Close(WalTailer tailer)
    {
        tailer.Dispose();
        Release();
    }

    private void Return(WalTailer tailer, int maxBytes)
    {
        var close = new List<WalTailer>();
        lock (_lock)
        {
            if (Environment.TickCount64 - _heldSince > (long)MaxHold.TotalMilliseconds)
            {
                // Held long enough: close everything so retention runs before the next pull.
                close.Add(tailer);
                close.AddRange(_idle.Select(t => t.Tailer));
                _idle.Clear();
            }
            else
            {
                _idle.Add((tailer, maxBytes, Environment.TickCount64));
                if (_idle.Count > Capacity)
                {
                    close.Add(_idle[0].Tailer);   // the least recently returned
                    _idle.RemoveAt(0);
                }
            }
        }
        close.ForEach(Close);
    }

    /// <summary>Closes tailers idle past <see cref="IdleTimeout"/>, releasing the WAL they hold.</summary>
    internal void DisposeExpired()
    {
        var cutoff = Environment.TickCount64 - (long)IdleTimeout.TotalMilliseconds;
        List<WalTailer>? drop = null;
        lock (_lock)
        {
            for (var i = _idle.Count - 1; i >= 0; i--)
            {
                if (_idle[i].IdleSince > cutoff) continue;
                (drop ??= []).Add(_idle[i].Tailer);
                _idle.RemoveAt(i);
            }
        }
        drop?.ForEach(Close);
    }

    /// <summary>Drops every held tailer (epoch or role change: a new history, 062's rule for the tail).</summary>
    public void Clear()
    {
        List<(WalTailer Tailer, int MaxBytes, long IdleSince)> drop;
        lock (_lock)
        {
            drop = [.. _idle];
            _idle.Clear();
        }
        foreach (var (t, _, _) in drop)
            Close(t);
    }

    public void Dispose()
    {
        _sweep.Dispose();
        Clear();
    }
}
