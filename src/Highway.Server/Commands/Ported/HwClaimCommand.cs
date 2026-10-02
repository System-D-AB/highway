using System.Globalization;
using System.Text;
using Highway.Server.Commands.Runtime;
using Highway.Server.Internal;
using Highway.Abstractions.Observability;
using Highway.Server.Storage.Boxes;
using Highway.Server.Storage.Layout;

namespace Highway.Server.Commands.Ported;

/// <summary>
/// HW.CLAIM SVC|Q &lt;name&gt; &lt;node&gt; [COUNT &lt;n&gt;] → [[token, id, payload], …] (5.0; replaces HW.DEQUEUE and
/// HW.QCLAIM). An empty array when nothing is due.
///
/// <para>One path for services, queues and group queues, over the target's box (065). Before popping it
/// does the recovery work the old claims did, in the same batch: for a queue, fire due recurring jobs and
/// promote delayed messages; for both, return claims whose lease expired (attempts + 1, or dead-lettered
/// past <c>MaxDeliveryAttempts</c>); for a service, requeue the work of nodes whose registration went stale.
/// The lease sweep reads only the lease index from its floor up to now, so it costs nothing when nothing
/// expired and runs on every claim — the 061 throttle is no longer needed.</para>
///
/// <para>The token is the claim's handle for HW.ACK / HW.TOUCH / HW.FAIL: one point lookup, and a stale
/// token (the lease expired and the message was redelivered) answers <c>:0</c> because the redelivery has a
/// new sequence.</para>
/// </summary>
internal sealed class HwClaimCommand : HighwayCommand
{
    private BoxKeys.Kind _kind;
    private string _name = null!;
    private string _node = null!;
    private int _count = 1;
    private bool _isDead;

    private readonly List<WorkBox.DeadLettered> _deadLettered = [];
    private readonly List<WorkBox.JobFire> _jobs = [];
    private readonly List<string> _claimedIds = [];
    private int _notificationsEnqueued;
    private int _feedOverflowCount;
    private string? _feedTarget;

    protected override bool Parse(CommandContext ctx, CommandInput input)
    {
        var idx = 0;
        var first = input.Next(ref idx);
        if (first.Length == 0) return Fail(HighwayErrors.InvalidArg, "HW.CLAIM requires a target: SVC <service> or Q <queue>, or DEAD SVC|Q <name>");

        if (Encoding.ASCII.GetString(first).Equals("DEAD", StringComparison.OrdinalIgnoreCase))
        {
            _isDead = true;
            if (!BoxCommandSupport.TryParseTarget(input.Next(ref idx), out _kind))
                return Fail(HighwayErrors.InvalidArg, "target must be SVC or Q");
        }
        else
        {
            if (!BoxCommandSupport.TryParseTarget(first, out _kind))
                return Fail(HighwayErrors.InvalidArg, "target must be SVC or Q");
        }

        var nameOk = _kind == BoxKeys.Kind.Queue
            ? TryReadDerivedIdentifier(input, ref idx, "queue", ctx.Options.MaxIdentifierBytes, out _name)
            : TryReadIdentifier(input, ref idx, "service", ctx.Options.MaxIdentifierBytes, out _name);
        if (!nameOk) return false;
        if (!TryReadIdentifier(input, ref idx, "node", ctx.Options.MaxIdentifierBytes, out _node))
            return false;

        var keyword = input.Next(ref idx);
        if (keyword.Length == 0) return true;
        if (!Encoding.ASCII.GetString(keyword).Equals("COUNT", StringComparison.OrdinalIgnoreCase))
            return Fail(HighwayErrors.InvalidArg, "expected COUNT <n>");
        var value = Encoding.ASCII.GetString(input.Next(ref idx));
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out _count)
            || _count < 1 || _count > ctx.Options.ReceiveMaxCount)
            return Fail(HighwayErrors.InvalidCount, $"COUNT must be 1..{ctx.Options.ReceiveMaxCount}");
        return true;
    }

    protected override void Run(CommandContext ctx, RespWriter writer)
    {
        if (_isDead)
        {
            var targetString = BoxCommandSupport.FeedTargetName(_kind, _name);
            var feedLock = BoxCommandSupport.LockLive(ctx, BoxKeys.Kind.DeadFeed, targetString, create: false, out var resolved);
            if (resolved is not { } feedBox)
            {
                feedLock.Dispose();
                writer.EmptyArray();
                return;
            }

            using (feedLock)
            using (var wbFeed = BoxCommandSupport.Open(ctx, feedBox, BoxKeys.Kind.DeadFeed))
            {
                if (ctx.Options.Lease > TimeSpan.Zero)
                {
                    wbFeed.SweepLeases(
                        ctx.NowTicks - ctx.Options.Lease.Ticks, ctx.NowTicks,
                        ctx.Options.MaxDeliveryAttempts, ctx.Options.MaxDeadLetterEntries);
                }

                var claimed = wbFeed.Claim(_node, ctx.NowTicks, _count, targetString);
                wbFeed.Commit();

                if (claimed.Count == 0)
                {
                    writer.EmptyArray();
                    return;
                }

                var rows = new List<IReadOnlyList<byte[]>>(claimed.Count);
                foreach (var c in claimed)
                {
                    rows.Add([Encoding.ASCII.GetBytes(BoxCommandSupport.Token(feedBox, c.Token)), c.Id, c.Payload]);
                    _claimedIds.Add(Encoding.UTF8.GetString(c.Id));
                }
                writer.ArrayOfArrays(rows);
            }
            return;
        }

        for (var attempt = 1; ; attempt++)
        {
            var targetLock = BoxCommandSupport.LockLive(ctx, _kind, _name, create: true, out var resolved);
            var box = resolved!.Value;
            var wb = BoxCommandSupport.Open(ctx, box, _kind);

            if (_kind == BoxKeys.Kind.DeadFeed || wb.FeedOn == 0)
            {
                using (targetLock)
                using (wb)
                {
                    ExecuteClaim(ctx, writer, box, wb, wbFeed: null, targetString: null);
                    return;
                }
            }

            // wb.FeedOn > 0: need both target and feed locks in canonical sorted order.
            wb.Dispose();
            targetLock.Dispose();

            var targetString = BoxCommandSupport.FeedTargetName(_kind, _name);
            var feedBox = BoxCommandSupport.Ensure(ctx, BoxKeys.Kind.DeadFeed, targetString);

            var locks = new[] { WorkBox.LockName(box), WorkBox.LockName(feedBox) };
            using (ctx.Locks.LockMany(locks))
            {
                if (BoxCommandSupport.CanRetire(_kind, _name) && BoxCommandSupport.LiveBox(ctx, _kind, _name) != box)
                {
                    ctx.Boxes.Forget(_kind, _name, box);
                    continue;
                }
                if (BoxCommandSupport.LiveBox(ctx, BoxKeys.Kind.DeadFeed, targetString) != feedBox)
                {
                    ctx.Boxes.Forget(BoxKeys.Kind.DeadFeed, targetString, feedBox);
                    continue;
                }

                using var wbTarget = BoxCommandSupport.Open(ctx, box, _kind);
                if (wbTarget.FeedOn > 0)
                {
                    using var wbFeed = BoxCommandSupport.Open(ctx, feedBox, BoxKeys.Kind.DeadFeed, sharedBatch: wbTarget.Batch);
                    ExecuteClaim(ctx, writer, box, wbTarget, wbFeed, targetString);
                }
                else
                {
                    ExecuteClaim(ctx, writer, box, wbTarget, wbFeed: null, targetString: null);
                }
                return;
            }
        }
    }

    private void ExecuteClaim(
        CommandContext ctx, RespWriter writer, uint box, WorkBox wb, WorkBox? wbFeed, string? targetString)
    {
        if (_kind == BoxKeys.Kind.Queue)
        {
            _jobs.AddRange(wb.FireDueJobs(ctx.NowTicks, ctx.Options.MaxQueueBytes));
        }
        wb.PromoteDue(ctx.NowTicks);

        if (ctx.Options.Lease > TimeSpan.Zero)
        {
            var dead = wb.SweepLeases(
                ctx.NowTicks - ctx.Options.Lease.Ticks, ctx.NowTicks,
                ctx.Options.MaxDeliveryAttempts, ctx.Options.MaxDeadLetterEntries);
            _deadLettered.AddRange(dead);

            if (wbFeed is not null && dead.Count > 0)
            {
                _feedTarget = targetString;
                foreach (var d in dead)
                {
                    var deadId = DeadLetter.FormatDeadId(box, d.Seq);
                    var feedEntry = Envelope.EncodeRpcEntry(Encoding.UTF8.GetBytes(deadId), d.Record, 0);
                    wbFeed.Enqueue(feedEntry);
                    _notificationsEnqueued++;
                }

                if (ctx.Options.MaxDeadLetterEntries > 0)
                {
                    var dropped = wbFeed.TrimReady(ctx.Options.MaxDeadLetterEntries);
                    _feedOverflowCount += dropped;
                }
            }
        }

        if (_kind == BoxKeys.Kind.Service)
            RecoverDeadNodes(ctx, wb);

        var claimed = wb.Claim(_node, ctx.NowTicks, _count, BoxCommandSupport.StorageName(_kind, _name));
        wb.Commit();
        wbFeed?.AfterCommit();

        if (claimed.Count == 0)
        {
            writer.EmptyArray();
            return;
        }
        var rows = new List<IReadOnlyList<byte[]>>(claimed.Count);
        foreach (var c in claimed)
        {
            rows.Add([Encoding.ASCII.GetBytes(BoxCommandSupport.Token(box, c.Token)), c.Id, c.Payload]);
            _claimedIds.Add(Encoding.UTF8.GetString(c.Id));
        }
        writer.ArrayOfArrays(rows);
    }

    /// <summary>
    /// A node whose registration went stale loses its claims to the queue (attempts + 1) and leaves the
    /// registry — as HW.DEQUEUE's pruning did. A node with no registration record is not participating in
    /// the registry (heartbeat disabled) and is left to the lease sweep.
    /// </summary>
    private void RecoverDeadNodes(CommandContext ctx, WorkBox wb)
    {
        if (!ctx.Options.PruningEnabled || ctx.Options.NodeExpiry <= TimeSpan.Zero) return;
        using var snap = ctx.Store.Snapshot();
        foreach (var node in wb.Workers())
        {
            if (node == _node) continue;   // the caller is demonstrably alive
            var record = ctx.Store.Get(snap, HighwayKeyspace.Kv(HighwayNames.RegistrationNode(node)));
            if (record is null || record.Length < NodeRegistration.HeaderSize) continue;
            if (!NodeRegistration.IsStale(RegistrySupport.SeenTicks(ctx.Store, snap, node, record), ctx.NowTicks, ctx.Options.NodeExpiry))
                continue;
            wb.RequeueNode(node);
            RegistrySupport.RemoveFromServiceIndex(ctx.Store, wb.Batch, _name, node);
            RegistrySupport.RemoveRegistration(ctx.Store, wb.Batch, node);
        }
    }

    protected override void AfterCommit(CommandContext ctx)
    {
        if (_isDead) return;
        var service = _kind == BoxKeys.Kind.Service;
        foreach (var d in _deadLettered)
            ctx.Recorder.Record(
                service ? HighwayEventType.RpcDeadLettered : HighwayEventType.QueueDeadLettered, _name,
                nodeId: _node, requestId: d.Id, count: d.Attempts, errorCode: DeadLetter.MaxAttempts);
        ctx.Metrics?.RecordDeadLettered(_deadLettered.Count);

        foreach (var job in _jobs)
            ctx.Recorder.Record(
                job.Refused ? HighwayEventType.JobFireRefused : HighwayEventType.JobFired, _name,
                requestId: job.Refused ? null : job.MessageId, errorCode: job.Job);

        if (Failed)
        {
            ctx.Recorder.Record(service ? HighwayEventType.RpcClaimed : HighwayEventType.QueueClaimed,
                _name ?? "?", nodeId: _node, errorCode: FailureCode);
            return;
        }
        foreach (var id in _claimedIds)
        {
            ctx.Recorder.Record(service ? HighwayEventType.RpcClaimed : HighwayEventType.QueueClaimed,
                _name, nodeId: _node, requestId: id);
            if (!service) ctx.Metrics?.RecordDelivered();
        }

        if (_feedTarget is not null)
        {
            if (_notificationsEnqueued > 0)
                ctx.Doorbell.Ring(HighwayKeys.DeadLetterDoorbell(_feedTarget), Encoding.UTF8.GetBytes(_feedTarget));
            if (_feedOverflowCount > 0)
                ctx.Recorder.Record(HighwayEventType.DeadLetterFeedOverflow, _feedTarget, count: _feedOverflowCount,
                    errorCode: $"dropped {_feedOverflowCount} oldest notification(s) due to bound {ctx.Options.MaxDeadLetterEntries}");
        }
    }
}
