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

    private readonly List<WorkBox.DeadLettered> _deadLettered = [];
    private readonly List<WorkBox.JobFire> _jobs = [];
    private readonly List<string> _claimedIds = [];

    protected override bool Parse(CommandContext ctx, CommandInput input)
    {
        var idx = 0;
        if (!BoxCommandSupport.TryParseTarget(input.Next(ref idx), out _kind))
            return Fail(HighwayErrors.InvalidArg, "target must be SVC or Q");
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
        using var _lock = BoxCommandSupport.LockLive(ctx, _kind, _name, create: true, out var resolved);
        var box = resolved!.Value;
        using var wb = BoxCommandSupport.Open(ctx, box, _kind);

        if (_kind == BoxKeys.Kind.Queue)
        {
            _jobs.AddRange(wb.FireDueJobs(ctx.NowTicks, ctx.Options.MaxQueueBytes));
            wb.PromoteDue(ctx.NowTicks);
        }

        if (ctx.Options.Lease > TimeSpan.Zero)
            _deadLettered.AddRange(wb.SweepLeases(
                ctx.NowTicks - ctx.Options.Lease.Ticks, ctx.NowTicks,
                ctx.Options.MaxDeliveryAttempts, ctx.Options.MaxDeadLetterEntries));

        if (_kind == BoxKeys.Kind.Service)
            RecoverDeadNodes(ctx, wb);

        var claimed = wb.Claim(_node, ctx.NowTicks, _count, BoxCommandSupport.StorageName(_kind, _name));
        wb.Commit();

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
    }
}
