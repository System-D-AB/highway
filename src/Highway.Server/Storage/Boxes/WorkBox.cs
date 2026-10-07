using System.Buffers.Binary;
using System.Text;
using Highway.Abstractions.Scheduling;
using Highway.Server.Internal;

namespace Highway.Server.Storage.Boxes;

/// <summary>
/// One service, queue or group queue in its own box (065) — every per-message operation is a point read,
/// a point write, or a seek from a known live position; every count is a stored counter.
///
/// <para>Spaces (see <see cref="BoxKeys"/>): ready <c>[seq]</c>, in-flight <c>[seq]</c> (the claim token),
/// lease index <c>[claimTicks][seq]</c>, delayed <c>[due][seq]</c>, dead <c>[seq]</c>, counters, workers,
/// schedules + schedule index, node index <c>[node][seq]</c>. One allocator (<c>nextSeq</c>) numbers every
/// entry in the box, so a sequence — and therefore a token — names exactly one claim, forever.</para>
///
/// <para>Entries keep the pre-065 framings: ready and delayed hold the queue entry
/// (<see cref="Envelope.EncodeRpcEntry"/>, attempts and failure block included); dead holds a
/// <see cref="DeadLetter"/>; in-flight wraps the queue entry with who claimed it and when. So a message
/// crosses ready → in-flight → ready/dead without re-encoding its payload or losing its failure block.</para>
///
/// <para>Callers hold this box's command lock (<see cref="LockName"/>) for the whole operation, which is
/// what makes the counters' read-modify-write and the process-local floors safe.</para>
/// </summary>
internal sealed class WorkBox : IDisposable
{
    private const byte CHead = 1, CNextSeq = 2, CReady = 3, CInFlight = 4, CDelayed = 5, CDead = 6, CBytes = 7, CDeadHead = 8;
    private const byte CDrainingSince = 9;   // 070: a group queue whose last member left (ticks; absent = not draining)
    private const byte CFeedOn = 10;         // 073: >= 1 node is declaring a feed for this target (count; absent = 0)
    private const byte CMaxRequeues = 11;    // 073: retry policy max requeues (default 3)
    private const byte CMaxAttempts = 12;    // 073: retry policy max attempts (absent = server option)
    private const byte CBackoffKind = 13;    // 073: 0=none, 1=fixed, 2=exp
    private const byte CBackoffMin = 14;     // 073: min backoff ticks
    private const byte CBackoffMax = 15;     // 073: max backoff ticks
    private const byte CJitter = 16;         // 073: 1=deterministic jitter, 0=none
    private const int SweepPage = 256;

    private readonly IHighwayStore _store;
    private readonly BoxRuntime _rt;
    private readonly bool _ownsBatch;
    private readonly List<(byte Space, long Ticks)> _raises = [];

    public uint Id { get; }

    /// <summary>Queues and group queues count bytes against <c>MaxQueueBytes</c>; services do not.</summary>
    public bool TracksBytes { get; }

    /// <summary>
    /// Entries whose payload runs to the very end, with no trailers (073: dead-letter feeds). Their payload is a
    /// dead-letter record that ends with the original entry's failure block and lineage, so reading trailers
    /// here would strip — or, on a re-encode, duplicate — the dead letter's own history.
    /// </summary>
    public bool RawEntries { get; }

    public IStoreBatch Batch { get; }

    public WorkBox(IHighwayStore store, BoxRuntime rt, uint id, bool tracksBytes, IStoreBatch? sharedBatch = null,
        bool rawEntries = false)
    {
        _store = store;
        _rt = rt;
        Id = id;
        TracksBytes = tracksBytes;
        RawEntries = rawEntries;
        _ownsBatch = sharedBatch is null;
        Batch = sharedBatch ?? store.NewBatch();
    }

    /// <summary>The command lock that serializes every operation on a box.</summary>
    public static string LockName(uint box) => "box:" + box.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Commits this box's own batch, then raises its floors (never before the write succeeded).</summary>
    public void Commit()
    {
        if (!_ownsBatch) throw new InvalidOperationException("A shared-batch box is committed by its owner; call AfterCommit.");
        Batch.Commit();
        AfterCommit();
    }

    /// <summary>For a box sharing another batch: apply floor raises once that batch has committed.</summary>
    public void AfterCommit()
    {
        foreach (var (space, ticks) in _raises) _rt.Raise(Id, space, ticks);
        _raises.Clear();
    }

    public void Dispose()
    {
        if (_ownsBatch) Batch.Dispose();
    }

    // =====================================================================================
    // Counters
    // =====================================================================================

    public long Ready => Get(CReady);
    public long InFlight => Get(CInFlight);
    public long Delayed => Get(CDelayed);
    public long Dead => Get(CDead);
    public long Bytes => Get(CBytes);

    /// <summary>When this group queue started draining (070), or 0 when it is not draining.</summary>
    public long DrainingSince => Get(CDrainingSince);

    /// <summary>Marks the group queue draining from <paramref name="ticks"/>, or clears the mark with 0.</summary>
    public void SetDrainingSince(long ticks)
    {
        var key = BoxKeys.Counter(Id, CDrainingSince);
        if (ticks <= 0) _store.Delete(Batch, key);
        else _store.Set(Batch, key, BoxKeys.I64(ticks));
    }

    /// <summary>Number of nodes currently declaring a feed for this target (073), or 0 when no feed is declared.</summary>
    public long FeedOn => Get(CFeedOn);

    /// <summary>Sets the number of active feed members for this target (073).</summary>
    public void SetFeedOn(long count)
    {
        var key = BoxKeys.Counter(Id, CFeedOn);
        if (count <= 0) _store.Delete(Batch, key);
        else _store.Set(Batch, key, BoxKeys.I64(count));
    }

    /// <summary>
    /// Maximum number of requeues permitted for this target by policy (073); 3 when no policy sets it. A policy
    /// may set 0 ("never requeue") — absent and 0 are different answers.
    /// </summary>
    public int MaxRequeues => StoredMaxRequeues();

    private int StoredMaxRequeues()
        => _store.Read(Batch, BoxKeys.Counter(Id, CMaxRequeues)) is { } raw ? (int)BoxKeys.ReadI64(raw) : 3;

    /// <summary>Sets the max requeues policy for this target (073).</summary>
    public void SetMaxRequeues(int max) => SetCounter(CMaxRequeues, Math.Max(0, max));   // 0 is stored: "never"

    /// <summary>Returns the configured retry policy for this target, or server defaults (073).</summary>
    public Highway.Abstractions.RetryPolicy GetRetryPolicy()
    {
        var maxAttempts = (int)Get(CMaxAttempts);
        var kind = (Highway.Abstractions.BackoffKind)Get(CBackoffKind);
        var min = TimeSpan.FromTicks(Get(CBackoffMin));
        var max = TimeSpan.FromTicks(Get(CBackoffMax));
        var jitter = Get(CJitter) != 0;
        return new Highway.Abstractions.RetryPolicy(maxAttempts, kind, min, max, jitter, StoredMaxRequeues());
    }

    /// <summary>
    /// Sets the retry policy for this target (073). Writing the default policy (<see cref="Highway.Abstractions.RetryPolicy.None"/>,
    /// what a contract without <c>[Retry]</c> declares) leaves the target reading as the broker's defaults — so removing
    /// <c>[Retry]</c> from a contract takes effect on its next session.
    /// </summary>
    public void SetRetryPolicy(Highway.Abstractions.RetryPolicy policy)
    {
        SetCounter(CMaxAttempts, policy.MaxAttempts);
        SetCounter(CBackoffKind, (long)policy.BackoffKind);
        SetCounter(CBackoffMin, policy.MinBackoff.Ticks);
        SetCounter(CBackoffMax, policy.MaxBackoff.Ticks);
        SetCounter(CJitter, policy.Jitter ? 1 : 0);
        SetCounter(CMaxRequeues, policy.MaxRequeues);
    }

    private void SetCounter(byte counter, long value)
    {
        var key = BoxKeys.Counter(Id, counter);
        if (value == 0 && counter is not CMaxRequeues) _store.Delete(Batch, key);
        else _store.Set(Batch, key, BoxKeys.I64(value));
    }

    private long Get(byte counter) => BoxKeys.ReadI64(_store.Read(Batch, BoxKeys.Counter(Id, counter)));

    private void Add(byte counter, long delta)
    {
        if (delta == 0) return;
        var value = Get(counter) + delta;
        var key = BoxKeys.Counter(Id, counter);
        if (value == 0 && counter is not (CHead or CNextSeq or CDeadHead)) _store.Delete(Batch, key);
        else _store.Set(Batch, key, BoxKeys.I64(value));
    }

    private ulong NextSeq()
    {
        var seq = Get(CNextSeq);
        _store.Set(Batch, BoxKeys.Counter(Id, CNextSeq), BoxKeys.I64(seq + 1));
        return (ulong)seq;
    }

    // =====================================================================================
    // Enqueue
    // =====================================================================================

    /// <summary>Appends a queue entry to ready. Returns its sequence.</summary>
    public ulong Enqueue(byte[] entry)
    {
        var seq = NextSeq();
        _store.Set(Batch, BoxKeys.Seq(Id, BoxKeys.Ready, seq), entry);
        Add(CReady, 1);
        if (TracksBytes) Add(CBytes, entry.Length);
        return seq;
    }

    /// <summary>Holds a queue entry until <paramref name="dueTicks"/> (promoted by <see cref="PromoteDue"/>).</summary>
    public void EnqueueDelayed(byte[] entry, long dueTicks)
    {
        var seq = NextSeq();
        _store.Set(Batch, BoxKeys.TicksSeq(Id, BoxKeys.Delayed, dueTicks, seq), entry);
        _rt.Lower(Id, BoxKeys.Delayed, dueTicks);
        Add(CDelayed, 1);
        if (TracksBytes) Add(CBytes, entry.Length);
    }

    /// <summary>Moves delayed entries due at or before <paramref name="nowTicks"/> to ready. Returns how many.</summary>
    public int PromoteDue(long nowTicks, int limit = 256)
    {
        var floor = _rt.Floor(Id, BoxKeys.Delayed);
        var due = _store.Scan(Batch, BoxKeys.Space(Id, BoxKeys.Delayed), BoxKeys.TicksFloor(Id, BoxKeys.Delayed, floor), limit);
        var promoted = 0;
        var exhausted = due.Count < limit;
        foreach (var (key, entry) in due)
        {
            if (BoxKeys.TicksAt(key) > nowTicks) { exhausted = true; break; }
            _store.Delete(Batch, key);
            Add(CDelayed, -1);
            var seq = NextSeq();
            _store.Set(Batch, BoxKeys.Seq(Id, BoxKeys.Ready, seq), entry);   // bytes already counted
            Add(CReady, 1);
            promoted++;
        }
        if (exhausted) _raises.Add((BoxKeys.Delayed, nowTicks + 1));
        else if (promoted > 0) _raises.Add((BoxKeys.Delayed, BoxKeys.TicksAt(due[promoted - 1].Key)));
        return promoted;
    }

    // =====================================================================================
    // Claim / settle
    // =====================================================================================

    internal readonly record struct Claimed(ulong Token, byte[] Id, byte[] Payload, ushort Attempts);

    /// <summary>Moves up to <paramref name="max"/> ready entries in-flight for <paramref name="node"/>.</summary>
    public List<Claimed> Claim(string node, long nowTicks, int max, string storageName)
    {
        var claimed = new List<Claimed>(Math.Min(max, 16));
        if (max <= 0) return claimed;

        var head = (ulong)Get(CHead);
        var ready = _store.Scan(Batch, BoxKeys.Space(Id, BoxKeys.Ready), BoxKeys.Seq(Id, BoxKeys.Ready, head), max);
        foreach (var (key, entry) in ready)
        {
            if (Envelope.IsLegacyEntry(entry)) throw new StorageFormatException(storageName);
            var seq = BoxKeys.TrailingSeq(key);
            _store.Delete(Batch, key);
            head = seq + 1;
            Add(CReady, -1);
            if (TracksBytes) Add(CBytes, -entry.Length);

            _store.Set(Batch, BoxKeys.Seq(Id, BoxKeys.InFlight, seq), InFlightRecord.Encode(nowTicks, node, entry));
            _store.Set(Batch, BoxKeys.TicksSeq(Id, BoxKeys.Lease, nowTicks, seq), []);
            _store.Set(Batch, BoxKeys.NameSeq(Id, BoxKeys.NodeIndex, node, seq), []);
            _rt.Lower(Id, BoxKeys.Lease, nowTicks);
            Add(CInFlight, 1);

            Decode(entry, out var id, out var payload, out var attempts);
            claimed.Add(new Claimed(seq, id.ToArray(), payload.ToArray(), attempts));
        }

        if (claimed.Count > 0)
        {
            _store.Set(Batch, BoxKeys.Counter(Id, CHead), BoxKeys.I64((long)head));
            _store.Set(Batch, BoxKeys.Name(Id, BoxKeys.Workers, node), BoxKeys.I64(nowTicks));
        }
        return claimed;
    }

    /// <summary>Settles a claim for good. False when <paramref name="token"/> is not held (acked, swept, never claimed).</summary>
    public bool Ack(ulong token)
    {
        if (ReadInFlight(token) is not { } rec) return false;
        RemoveInFlight(token, rec);
        return true;
    }

    /// <summary>"Still working": the claim's lease restarts now. False when not held.</summary>
    public bool Touch(ulong token, long nowTicks)
    {
        if (ReadInFlight(token) is not { } rec) return false;
        _store.Delete(Batch, BoxKeys.TicksSeq(Id, BoxKeys.Lease, rec.ClaimTicks, token));
        _store.Set(Batch, BoxKeys.TicksSeq(Id, BoxKeys.Lease, nowTicks, token), []);
        _rt.Lower(Id, BoxKeys.Lease, nowTicks);
        _store.Set(Batch, BoxKeys.Seq(Id, BoxKeys.InFlight, token), InFlightRecord.Encode(nowTicks, rec.Node, rec.Entry));
        return true;
    }

    /// <summary>Rewrites the held entry's failure block (<see cref="Envelope.WithFailureBlock"/>). False when not held.</summary>
    public bool Fail(ulong token, Func<byte[], byte[]> buildBlock)
    {
        if (RawEntries) return false;   // a feed entry carries no failure of its own (073)
        if (ReadInFlight(token) is not { } rec) return false;
        var updated = Envelope.WithFailureBlock(rec.Entry, buildBlock(rec.Entry));
        _store.Set(Batch, BoxKeys.Seq(Id, BoxKeys.InFlight, token), InFlightRecord.Encode(rec.ClaimTicks, rec.Node, updated));
        return true;
    }

    /// <summary>The held claim for <paramref name="token"/>, decoded — id and node, for recording.</summary>
    public InFlightRecord? ReadInFlight(ulong token)
    {
        var raw = _store.Read(Batch, BoxKeys.Seq(Id, BoxKeys.InFlight, token));
        return raw is null ? null : InFlightRecord.Decode(raw);
    }

    private void RemoveInFlight(ulong seq, InFlightRecord rec)
    {
        _store.Delete(Batch, BoxKeys.Seq(Id, BoxKeys.InFlight, seq));
        _store.Delete(Batch, BoxKeys.TicksSeq(Id, BoxKeys.Lease, rec.ClaimTicks, seq));
        _store.Delete(Batch, BoxKeys.NameSeq(Id, BoxKeys.NodeIndex, rec.Node, seq));
        Add(CInFlight, -1);
    }

    // =====================================================================================
    // Recovery
    // =====================================================================================

    internal readonly record struct DeadLettered(string Id, ushort Attempts, ulong Seq, byte[] Record);

    /// <summary>
    /// Recovers claims whose lease started before <paramref name="leaseExpiry"/>: each returns to ready
    /// with its attempt count incremented, or is dead-lettered past <paramref name="maxAttempts"/>. Reads
    /// only the lease index from its floor up to the expiry — never a live claim.
    /// </summary>
    public List<DeadLettered> SweepLeases(long leaseExpiry, long nowTicks, int maxAttempts, int maxDeadEntries)
    {
        var policy = GetRetryPolicy();
        var outcomes = new List<DeadLettered>();
        var floor = _rt.Floor(Id, BoxKeys.Lease);
        var page = _store.Scan(Batch, BoxKeys.Space(Id, BoxKeys.Lease), BoxKeys.TicksFloor(Id, BoxKeys.Lease, floor), SweepPage);
        var exhausted = page.Count < SweepPage;
        var processed = 0;
        foreach (var (key, _) in page)
        {
            var claimTicks = BoxKeys.TicksAt(key);
            if (claimTicks >= leaseExpiry) { exhausted = true; break; }
            processed++;
            var seq = BoxKeys.TrailingSeq(key);
            _store.Delete(Batch, key);
            if (ReadInFlight(seq) is not { } rec) continue;   // index entry without a claim: just drop it
            RemoveInFlight(seq, rec);
            Redeliver(rec.Entry, nowTicks, maxAttempts, maxDeadEntries, outcomes, policy);
        }
        if (exhausted) _raises.Add((BoxKeys.Lease, leaseExpiry));
        else if (processed > 0) _raises.Add((BoxKeys.Lease, BoxKeys.TicksAt(page[processed - 1].Key)));
        return outcomes;
    }

    /// <summary>
    /// Returns every claim held by <paramref name="node"/> to ready with its attempt count incremented (a
    /// delivery was made and lost with the node), never dead-lettering — dead-node recovery and departure,
    /// exactly as <c>RequeueNodeWork</c> did. Returns how many.
    /// </summary>
    public int RequeueNode(string node)
    {
        var prefix = BoxKeys.Name(Id, BoxKeys.NodeIndex, node);
        var count = 0;
        while (true)
        {
            var page = _store.Scan(Batch, prefix, prefix, SweepPage);
            foreach (var (key, _) in page)
            {
                var seq = BoxKeys.TrailingSeq(key);
                if (ReadInFlight(seq) is not { } rec) { _store.Delete(Batch, key); continue; }
                RemoveInFlight(seq, rec);
                Decode(rec.Entry, out var id, out var payload, out var attempts);
                Enqueue(Carry(rec.Entry,
                    Envelope.EncodeRpcEntry(id.ToArray(), payload.ToArray(), Envelope.NextAttempt(attempts))));
                count++;
            }
            if (page.Count < SweepPage) break;
        }
        _store.Delete(Batch, BoxKeys.Name(Id, BoxKeys.Workers, node));
        return count;
    }

    private void Redeliver(byte[] entry, long nowTicks, int maxAttempts, int maxDeadEntries, List<DeadLettered> outcomes, Highway.Abstractions.RetryPolicy policy)
    {
        Decode(entry, out var id, out var payload, out var attempts);
        var next = Envelope.NextAttempt(attempts);
        var rebuilt = Carry(entry, Envelope.EncodeRpcEntry(id.ToArray(), payload.ToArray(), next));
        var effectiveMax = policy.MaxAttempts > 0 ? policy.MaxAttempts : maxAttempts;
        if (effectiveMax > 0 && next > effectiveMax)
        {
            var record = DeadLetter.Encode(nowTicks, next, DeadLetter.MaxAttempts, rebuilt);
            var deadSeq = PushDead(record, maxDeadEntries);
            outcomes.Add(new DeadLettered(Encoding.UTF8.GetString(id), next, deadSeq, record));
            return;
        }

        if (policy.BackoffKind != Highway.Abstractions.BackoffKind.None)
        {
            var messageId = Encoding.UTF8.GetString(id);
            var delayTicks = policy.CalculateBackoffTicks(messageId, next);
            if (delayTicks > 0)
            {
                EnqueueDelayed(rebuilt, nowTicks + delayTicks);
                return;
            }
        }

        Enqueue(rebuilt);
    }

    private void Decode(ReadOnlySpan<byte> entry, out ReadOnlySpan<byte> id, out ReadOnlySpan<byte> payload, out ushort attempts)
    {
        if (RawEntries) Envelope.DecodeRpcEntryRaw(entry, out id, out payload, out attempts);
        else Envelope.DecodeRpcEntry(entry, out id, out payload, out attempts);
    }

    private byte[] Carry(byte[] source, byte[] rebuilt) => RawEntries ? rebuilt : Envelope.CarryFailureBlock(source, rebuilt);

    private byte[] Reencode(byte[] entry, bool keepAttempts)
    {
        Decode(entry, out var id, out var payload, out var attempts);
        return Envelope.EncodeRpcEntry(id.ToArray(), payload.ToArray(), keepAttempts ? attempts : (ushort)0);
    }

    /// <summary>Distinct nodes with a worker record — the candidates dead-node recovery checks.</summary>
    public List<string> Workers()
    {
        var prefix = BoxKeys.Space(Id, BoxKeys.Workers);
        var nodes = new List<string>();
        foreach (var (key, _) in _store.Scan(Batch, prefix, prefix, 4096))
            nodes.Add(Layout.KeyEncoding.ReadString(key.AsSpan(5), out _));
        return nodes;
    }

    // =====================================================================================
    // Inspection (operators, tests) — bounded reads, never on a hot path
    // =====================================================================================

    /// <summary>Ready entries in delivery order, up to <paramref name="limit"/>.</summary>
    public IReadOnlyList<byte[]> ReadyEntries(int limit = 4096)
        => [.. _store.Scan(Batch, BoxKeys.Space(Id, BoxKeys.Ready), BoxKeys.Seq(Id, BoxKeys.Ready, (ulong)Get(CHead)), limit).Select(e => e.Value)];

    /// <summary>The claims <paramref name="node"/> holds, oldest first.</summary>
    public IReadOnlyList<InFlightRecord> InFlightFor(string node, int limit = 4096)
    {
        var prefix = BoxKeys.Name(Id, BoxKeys.NodeIndex, node);
        var held = new List<InFlightRecord>();
        foreach (var (key, _) in _store.Scan(Batch, prefix, prefix, limit))
            if (ReadInFlight(BoxKeys.TrailingSeq(key)) is { } rec) held.Add(rec);
        return held;
    }

    // =====================================================================================
    // Dead letters
    // =====================================================================================

    internal ulong PushDead(byte[] record, int maxEntries)
    {
        var seq = NextSeq();
        _store.Set(Batch, BoxKeys.Seq(Id, BoxKeys.Dead, seq), record);
        Add(CDead, 1);
        if (maxEntries > 0)
            while (Get(CDead) > maxEntries)
                if (TakeDead(1, _ => { }) == 0) break;
        return seq;
    }

    /// <summary>
    /// Drops the oldest ready entries until the ready count is at or below <paramref name="maxEntries"/>.
    /// Returns how many were dropped (073).
    /// </summary>
    public int TrimReady(int maxEntries)
    {
        if (maxEntries <= 0) return 0;
        var dropped = 0;
        while (Get(CReady) > maxEntries)
        {
            var head = (ulong)Get(CHead);
            var scan = _store.Scan(Batch, BoxKeys.Space(Id, BoxKeys.Ready), BoxKeys.Seq(Id, BoxKeys.Ready, head), 1);
            if (scan.Count == 0) break;
            var (key, entry) = scan[0];
            _store.Delete(Batch, key);
            var seq = BoxKeys.TrailingSeq(key);
            _store.Set(Batch, BoxKeys.Counter(Id, CHead), BoxKeys.I64((long)(seq + 1)));
            Add(CReady, -1);
            if (TracksBytes) Add(CBytes, -entry.Length);
            dropped++;
        }
        return dropped;
    }

    /// <summary>The oldest dead letters, without removing them.</summary>
    public IReadOnlyList<(byte[] Key, byte[] Value)> PeekDead(int count)
        => count <= 0 ? [] : _store.Scan(Batch, BoxKeys.Space(Id, BoxKeys.Dead), BoxKeys.Seq(Id, BoxKeys.Dead, (ulong)Get(CDeadHead)), count);

    /// <summary>Reads a specific dead letter record by sequence, or null when absent or already settled.</summary>
    public byte[]? ReadDead(ulong seq)
        => _store.Read(Batch, BoxKeys.Seq(Id, BoxKeys.Dead, seq));

    /// <summary>
    /// Returns the oldest dead letters to ready with attempts reset to zero, failure block dropped,
    /// and lineage incremented (as <c>HW.DLQ REQUEUE</c> always did).
    /// </summary>
    public int RequeueDead(int count)
        => TakeDead(count, record =>
        {
            DeadLetter.Decode(record, out _, out _, out _, out var original);
            Envelope.TryGetLineage(original, out var requeues, out var withoutLineage);
            var nextRequeues = requeues == ushort.MaxValue ? requeues : (ushort)(requeues + 1);   // saturates: 0 would strip it
            var bare = Envelope.StripFailureBlock(withoutLineage);
            var clean = Reencode(bare.ToArray(), keepAttempts: false);
            Enqueue(Envelope.WithLineage(clean, nextRequeues));
        });

    /// <summary>Deletes the oldest dead letters.</summary>
    public int PurgeDead(int count) => TakeDead(count, _ => { });

    /// <summary>Deletes a specific dead letter by sequence. False when absent (already purged, requeued, or evicted).</summary>
    public bool PurgeDead(ulong seq)
    {
        var key = BoxKeys.Seq(Id, BoxKeys.Dead, seq);
        if (_store.Read(Batch, key) is null) return false;
        _store.Delete(Batch, key);
        Add(CDead, -1);
        if (seq == (ulong)Get(CDeadHead))
            _store.Set(Batch, BoxKeys.Counter(Id, CDeadHead), BoxKeys.I64((long)(seq + 1)));
        return true;
    }

    /// <summary>
    /// Requeues a specific dead letter by sequence with attempts reset to zero and lineage incremented.
    /// False when absent.
    /// </summary>
    public bool RequeueDead(ulong seq, TimeSpan? after = null, long nowTicks = 0)
    {
        var key = BoxKeys.Seq(Id, BoxKeys.Dead, seq);
        var record = _store.Read(Batch, key);
        if (record is null) return false;

        _store.Delete(Batch, key);
        Add(CDead, -1);
        if (seq == (ulong)Get(CDeadHead))
            _store.Set(Batch, BoxKeys.Counter(Id, CDeadHead), BoxKeys.I64((long)(seq + 1)));

        DeadLetter.Decode(record, out _, out _, out _, out var original);
        Envelope.TryGetLineage(original, out var requeues, out var withoutLineage);
        var nextRequeues = requeues == ushort.MaxValue ? requeues : (ushort)(requeues + 1);   // saturates: 0 would strip it
        var bare = Envelope.StripFailureBlock(withoutLineage);
        var clean = Reencode(bare.ToArray(), keepAttempts: false);
        var rebuilt = Envelope.WithLineage(clean, nextRequeues);

        if (after is { } delay && delay > TimeSpan.Zero)
            EnqueueDelayed(rebuilt, nowTicks + delay.Ticks);
        else
            Enqueue(rebuilt);

        return true;
    }

    private int TakeDead(int count, Action<byte[]> each)
    {
        var taken = 0;
        var head = (ulong)Get(CDeadHead);
        foreach (var (key, record) in PeekDead(count))
        {
            _store.Delete(Batch, key);
            head = BoxKeys.TrailingSeq(key) + 1;
            Add(CDead, -1);
            each(record);
            taken++;
        }
        if (taken > 0) _store.Set(Batch, BoxKeys.Counter(Id, CDeadHead), BoxKeys.I64((long)(head)));
        return taken;
    }

    // =====================================================================================
    // Recurring jobs (queues only)
    // =====================================================================================

    internal readonly record struct Schedule(string Job, string Expression, long LastFire, long NextFire, byte[] Template);

    /// <summary>
    /// Registers or replaces a schedule, preserving <c>lastFire</c> (last registration wins). Returns the
    /// previous expression, or null when the job is new.
    /// </summary>
    public string? UpsertSchedule(string job, string expression, JobExpression parsed, byte[] template, long nowTicks)
    {
        long lastFire = 0;
        string? previous = null;
        if (ReadSchedule(job) is { } existing)
        {
            lastFire = existing.LastFire;
            previous = existing.Expression;
            _store.Delete(Batch, BoxKeys.TicksName(Id, BoxKeys.ScheduleIndex, existing.NextFire, job));
        }
        var next = parsed.NextOccurrence(new DateTime(nowTicks, DateTimeKind.Utc)).Ticks;
        WriteSchedule(job, expression, lastFire, next, template);
        return previous;
    }

    public bool RemoveSchedule(string job)
    {
        if (ReadSchedule(job) is not { } existing) return false;
        _store.Delete(Batch, BoxKeys.Name(Id, BoxKeys.Schedules, job));
        _store.Delete(Batch, BoxKeys.TicksName(Id, BoxKeys.ScheduleIndex, existing.NextFire, job));
        return true;
    }

    public List<Schedule> Schedules()
    {
        var prefix = BoxKeys.Space(Id, BoxKeys.Schedules);
        var list = new List<Schedule>();
        foreach (var (_, record) in _store.Scan(Batch, prefix, prefix, 4096))
            if (TryDecode(record, out var s)) list.Add(s);
        return list;
    }

    internal readonly record struct JobFire(string Job, string MessageId, bool Refused);

    /// <summary>
    /// Fires every schedule due at <paramref name="nowTicks"/>: one occurrence each, re-armed from now
    /// (catch-up-one). A fire the byte budget refuses leaves <c>nextFire</c> unchanged, so a later poll
    /// retries it. An unreadable schedule is removed, loudly.
    /// </summary>
    public List<JobFire> FireDueJobs(long nowTicks, long maxQueueBytes, int limit = 16)
    {
        var fires = new List<JobFire>();
        var floor = _rt.Floor(Id, BoxKeys.ScheduleIndex);
        var due = _store.Scan(Batch, BoxKeys.Space(Id, BoxKeys.ScheduleIndex), BoxKeys.TicksFloor(Id, BoxKeys.ScheduleIndex, floor), limit);
        var exhausted = due.Count < limit;
        long lowestRefused = long.MaxValue;
        foreach (var (key, _) in due)
        {
            if (BoxKeys.TicksAt(key) > nowTicks) { exhausted = true; break; }
            var job = BoxKeys.NameAfterTicks(key);
            if (ReadSchedule(job) is not { } s || !TryParse(s.Expression, out var parsed))
            {
                _store.Delete(Batch, key);
                _store.Delete(Batch, BoxKeys.Name(Id, BoxKeys.Schedules, job));
                fires.Add(new JobFire("<unreadable schedule removed>", "", Refused: true));
                continue;
            }

            var messageId = $"job:{job}:{nowTicks.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
            var entry = Envelope.EncodeRpcEntry(Encoding.UTF8.GetBytes(messageId), s.Template);
            if (maxQueueBytes > 0 && Bytes + entry.Length > maxQueueBytes)
            {
                fires.Add(new JobFire(job, messageId, Refused: true));
                lowestRefused = Math.Min(lowestRefused, s.NextFire);
                continue;   // nextFire unchanged — retried on a later poll
            }
            Enqueue(entry);
            _store.Delete(Batch, key);
            WriteSchedule(job, s.Expression, nowTicks, parsed.NextOccurrence(new DateTime(nowTicks, DateTimeKind.Utc)).Ticks, s.Template);
            fires.Add(new JobFire(job, messageId, Refused: false));
        }
        if (exhausted) _raises.Add((BoxKeys.ScheduleIndex, Math.Min(nowTicks + 1, lowestRefused)));
        return fires;
    }

    private Schedule? ReadSchedule(string job)
    {
        var raw = _store.Read(Batch, BoxKeys.Name(Id, BoxKeys.Schedules, job));
        return raw is not null && TryDecode(raw, out var s) ? s : null;
    }

    private void WriteSchedule(string job, string expression, long lastFire, long nextFire, byte[] template)
    {
        _store.Set(Batch, BoxKeys.Name(Id, BoxKeys.Schedules, job), JobScheduleRecord.Encode(job, expression, lastFire, nextFire, template));
        _store.Set(Batch, BoxKeys.TicksName(Id, BoxKeys.ScheduleIndex, nextFire, job), []);
        _rt.Lower(Id, BoxKeys.ScheduleIndex, nextFire);
    }

    private static bool TryDecode(byte[] record, out Schedule schedule)
    {
        try
        {
            JobScheduleRecord.Decode(record, out var job, out var expr, out var last, out var next, out var template);
            schedule = new Schedule(job, expr, last, next, template.ToArray());
            return true;
        }
        catch (Exception)
        {
            schedule = default;
            return false;
        }
    }

    private static bool TryParse(string expression, out JobExpression parsed)
    {
        try { parsed = JobExpression.Parse(expression); return true; }
        catch (Exception) { parsed = null!; return false; }
    }
}

/// <summary>A claimed entry: when and by whom, wrapping the queue entry unchanged (065).</summary>
internal sealed record InFlightRecord(long ClaimTicks, string Node, byte[] Entry)
{
    private const byte Version = 1;

    public static byte[] Encode(long claimTicks, string node, byte[] entry)
    {
        var nodeBytes = Encoding.UTF8.GetBytes(node);
        var buf = new byte[1 + 8 + 2 + nodeBytes.Length + entry.Length];
        buf[0] = Version;
        BinaryPrimitives.WriteInt64BigEndian(buf.AsSpan(1), claimTicks);
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(9), (ushort)nodeBytes.Length);
        nodeBytes.CopyTo(buf.AsSpan(11));
        entry.CopyTo(buf.AsSpan(11 + nodeBytes.Length));
        return buf;
    }

    public static InFlightRecord Decode(byte[] raw)
    {
        if (raw.Length < 11 || raw[0] != Version)
            throw new InvalidDataException($"In-flight record version {(raw.Length > 0 ? raw[0] : -1)} is not readable by this build.");
        var claim = BinaryPrimitives.ReadInt64BigEndian(raw.AsSpan(1));
        var nodeLen = BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(9));
        var node = Encoding.UTF8.GetString(raw, 11, nodeLen);
        return new InFlightRecord(claim, node, raw[(11 + nodeLen)..]);
    }
}
