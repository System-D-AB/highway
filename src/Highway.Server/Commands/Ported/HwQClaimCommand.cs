using System.Text;
using Highway.Abstractions.Scheduling;
using Highway.Server.Commands.Runtime;
using Highway.Server.Internal;
using Highway.Abstractions.Observability;
using Highway.Server.Storage;
using Highway.Server.Storage.Layout;

namespace Highway.Server.Commands.Ported;

/// <summary>
/// HW.QCLAIM &lt;queue&gt; &lt;nodeId&gt; → [messageId, payload] | *-1 (feature 014).
///
/// The most entangled command (037 §2, 039 R6). On every claim it: promotes matured delayed
/// messages, sweeps expired leases (requeue or dead-letter), pops the head, stamps a claim
/// timestamp, moves it to the caller's processing list, registers the node, and replies the
/// two-element array (or a null array when empty).
///
/// <para>The claim is a <b>move committed in one batch</b> — pop from live, push to
/// processing — so the message is durable in some list at every instant (at-least-once). The
/// per-queue lock serializes claimants, so two workers on one queue get disjoint messages
/// (R6.1). <b>Job firing</b> (028, completed in T7): due schedules in <c>job:{queue}:schedules</c>
/// fire their templated occurrence onto the live queue and re-arm atomically; the
/// claim/promote/sweep core is behavior-identical.</para>
/// </summary>
internal sealed class HwQClaimCommand : HighwayCommand
{
    private string _queue = null!;
    private string _nodeId = null!;
    private readonly List<(string Id, ushort Attempts)> _deadLettered = [];
    private readonly List<(string Job, string MessageId)> _firedJobs = [];
    private readonly List<string> _refusedJobs = [];
    private string? _claimedId;

    protected override bool Parse(CommandContext ctx, CommandInput input)
    {
        var idx = 0;
        if (!TryReadDerivedIdentifier(input, ref idx, "queue", ctx.Options.MaxIdentifierBytes, out _queue))
            return false;
        if (!TryReadIdentifier(input, ref idx, "nodeId", ctx.Options.MaxIdentifierBytes, out _nodeId))
            return false;
        return true;
    }

    protected override void Run(CommandContext ctx, RespWriter writer)
    {
        var store = ctx.Store;

        using var _lock = ctx.Locks.Lock(_queue);
        using var snap = store.Snapshot();
        using var batch = store.NewBatch();

        // 1. Promote matured delayed messages into the live queue.
        ConsumerSupport.PromoteDueMessages(store, snap, batch, _queue, ctx.NowTicks);

        // 1b. Fire due recurring jobs into the live queue and re-arm them (028). Only touches
        // the store when this queue has schedules, so a queue with none pays one set read.
        FireDueJobs(ctx, store, snap, batch);

        // 2. Lease sweep across this queue's processing lists (this node + any known workers).
        if (ctx.Options.Lease > TimeSpan.Zero)
        {
            var leaseExpiry = ctx.NowTicks - ctx.Options.Lease.Ticks;
            foreach (var node in KnownWorkerNodes(store, snap, _queue, _nodeId))
            {
                var dead = ConsumerSupport.SweepExpiredEntries(
                    store, batch,
                    procListName: HighwayNames.QueueProcessing(_queue, node),
                    liveQueueName: HighwayNames.Queue(_queue),
                    dlqName: HighwayNames.QueueDeadLetter(_queue),
                    leaseExpiry: leaseExpiry,
                    nowTicks: ctx.NowTicks,
                    opts: ctx.Options,
                    decode: DecodeProcessing,
                    encodeQueueEntry: static (id, payload, attempts) => Envelope.EncodeRpcEntry(id, payload, attempts),
                    idToString: static id => Encoding.UTF8.GetString(id),
                    bytesCounterName: HighwayNames.QueueBytes(_queue));

                foreach (var d in dead) _deadLettered.Add((d.Id, d.Attempts));
            }
        }

        // 3. Pop the head of the live queue.
        var popped = store.ListLeftPop(batch, HighwayKeyspace.ListPrefix(HighwayNames.Queue(_queue)));
        if (popped is null)
        {
            batch.Commit(); // promotion + sweep still commit
            writer.NullArray();
            return;
        }

        if (Envelope.IsLegacyEntry(popped))
            throw new StorageFormatException(HighwayNames.Queue(_queue));

        Envelope.DecodeRpcEntry(popped, out var messageId, out var payload, out var attempts);

        // Byte accounting: the message left the live queue.
        store.AdjustByteCounter(batch, HighwayNames.QueueBytes(_queue), -popped.Length);

        // 4. Wrap with the claim timestamp and push to the caller's processing list.
        var procEntry = Envelope.CarryFailureBlock(
            popped,
            Envelope.EncodeRpcProcessingEntry(ctx.NowTicks, messageId.ToArray(), payload.ToArray(), attempts));
        HwQSendCommand.PushTail(store, batch, HighwayNames.QueueProcessing(_queue, _nodeId), procEntry);

        // 5. Register this worker so future claims sweep its list too.
        store.SetAdd(batch, HighwayKeyspace.SetPrefix(HighwayNames.QueueNodes(_queue)), Encoding.UTF8.GetBytes(_nodeId));

        batch.Commit();

        _claimedId = Encoding.UTF8.GetString(messageId);
        writer.BulkStringArray(messageId.ToArray(), payload.ToArray());
    }

    protected override void AfterCommit(CommandContext ctx)
    {
        foreach (var (id, attempts) in _deadLettered)
        {
            ctx.Recorder.Record(
                HighwayEventType.QueueDeadLettered, _queue,
                nodeId: _nodeId, requestId: id, count: attempts, errorCode: DeadLetter.MaxAttempts);
        }
        ctx.Metrics?.RecordDeadLettered(_deadLettered.Count);

        foreach (var (job, messageId) in _firedJobs)
            ctx.Recorder.Record(HighwayEventType.JobFired, _queue, requestId: messageId, errorCode: job);

        foreach (var job in _refusedJobs)
            ctx.Recorder.Record(HighwayEventType.JobFireRefused, _queue, errorCode: job);

        // The Garnet Finalize records QueueClaimed for a successful claim and for a rejected
        // run (with its FailureCode); only the empty poll is silent — an empty poll is not an
        // event. (Found by the 040 fixture swap: the port had dropped the record entirely.)
        if (!Failed && _claimedId is null) return;

        ctx.Recorder.Record(
            HighwayEventType.QueueClaimed, _queue ?? "?",
            nodeId: _nodeId,
            requestId: _claimedId,
            errorCode: FailureCode);
        if (_claimedId is not null) ctx.Metrics?.RecordDelivered();
    }

    /// <summary>
    /// Fires every schedule whose next occurrence is at or before <see cref="CommandContext.NowTicks"/>:
    /// pushes the templated occurrence message onto the live queue and re-arms the schedule at
    /// its next occurrence — remove-then-add of the member in the same batch (atomic re-arm).
    ///
    /// <para><b>Catch-up-one (OD3):</b> the next occurrence is computed from <i>now</i>, so a
    /// schedule due five times while nothing polled fires once and realigns. <b>Backpressure
    /// (016):</b> a full queue refuses the fire — nextFire left unchanged, retried on a later
    /// poll, recorded <c>JobFireRefused</c>. An unreadable/unparseable record is removed loudly
    /// rather than retried forever (013's rule).</para>
    /// </summary>
    private void FireDueJobs(CommandContext ctx, IHighwayStore store, IStoreSnapshot snap, IStoreBatch batch)
    {
        var schedName = HighwayNames.JobSchedules(_queue);
        var schedKey = HighwayKeyspace.SortedSetPrefix(schedName);

        var due = store.SortedSetRangeByScore(snap, schedKey, long.MinValue, ctx.NowTicks, 16);
        if (due.Count == 0)
            return;

        var now = new DateTime(ctx.NowTicks, DateTimeKind.Utc);

        foreach (var member in due)
        {
            string job, expression;
            byte[] template;
            JobExpression parsed;

            try
            {
                JobScheduleRecord.Decode(member, out job, out expression, out _, out _, out var templateSpan);
                template = templateSpan.ToArray();
                parsed = JobExpression.Parse(expression);
            }
            catch (Exception)
            {
                // A record this build cannot read or re-arm would otherwise be retried on every
                // poll forever. Removing it is destruction, so it is loud (JobFireRefused).
                store.SortedSetRemove(batch, schedKey, member);
                _refusedJobs.Add("<unreadable schedule removed>");
                continue;
            }

            var messageId = $"job:{job}:{ctx.NowTicks.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
            var entry = Envelope.EncodeRpcEntry(Encoding.UTF8.GetBytes(messageId), template);

            // The same refusal HW.QSEND makes: scheduled fan-in does not get to overfill a
            // queue that hand-sent work would be refused on.
            if (ctx.Options.MaxQueueBytes > 0
                && store.ReadByteCounter(snap, HighwayNames.QueueBytes(_queue)) + entry.Length > ctx.Options.MaxQueueBytes)
            {
                _refusedJobs.Add(job);
                continue; // nextFire unchanged — retried on a later poll
            }

            HwQSendCommand.PushTail(store, batch, HighwayNames.Queue(_queue), entry);
            store.AdjustByteCounter(batch, HighwayNames.QueueBytes(_queue), entry.Length);

            var nextFire = parsed.NextOccurrence(now).Ticks;
            store.SortedSetRemove(batch, schedKey, member);
            store.SortedSetAdd(batch, schedKey, nextFire, JobScheduleRecord.Encode(job, expression, ctx.NowTicks, nextFire, template));

            _firedJobs.Add((job, messageId));
        }
    }

    /// <summary>The worker nodes to sweep: those registered on the queue, plus this caller.</summary>
    private static IEnumerable<string> KnownWorkerNodes(IHighwayStore store, IStoreSnapshot snap, string queue, string self)
    {
        var members = store.SetMembers(snap, HighwayKeyspace.SetPrefix(HighwayNames.QueueNodes(queue)));
        var nodes = new HashSet<string>(members.Select(m => Encoding.UTF8.GetString(m))) { self };
        return nodes;
    }

    private static void DecodeProcessing(ReadOnlySpan<byte> data, out long claimTicks, out byte[] id, out byte[] payload, out ushort attempts)
    {
        Envelope.DecodeRpcProcessingEntry(data, out claimTicks, out var idSpan, out var payloadSpan, out attempts);
        id = idSpan.ToArray();
        payload = payloadSpan.ToArray();
    }
}
