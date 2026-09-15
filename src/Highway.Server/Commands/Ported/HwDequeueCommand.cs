using System.Text;
using Highway.Server.Commands.Runtime;
using Highway.Server.Internal;
using Highway.Abstractions.Observability;
using Highway.Server.Storage;
using Highway.Server.Storage.Layout;

namespace Highway.Server.Commands.Ported;

/// <summary>
/// HW.DEQUEUE &lt;service&gt; &lt;nodeId&gt; → [requestId, payload] | *-1 (RPC consumer).
///
/// The RPC counterpart of HW.QCLAIM: sweep expired leases across the service's processing
/// lists, pop the head of the service queue, stamp a claim timestamp, move it to the caller's
/// processing list, register the node, reply the two-element array (or a null array when
/// empty). Sweep returns survivors to the tail (RPC has no head-preservation requirement).
///
/// <para><b>Dead-node sweep</b> (006, completed in T7): a node whose registration has gone
/// stale has its whole processing list requeued and is dropped from the worker set, index and
/// registry via <see cref="RegistrySupport"/>. The registry reads use the collapsed sets
/// (T3.3). The claim + lease-sweep core is behavior-identical.</para>
/// </summary>
internal sealed class HwDequeueCommand : HighwayCommand
{
    private string _service = null!;
    private string _nodeId = null!;
    private string? _claimedRequestId;
    private readonly List<(string Id, ushort Attempts)> _deadLettered = [];

    protected override bool Parse(CommandContext ctx, CommandInput input)
    {
        var idx = 0;
        if (!TryReadIdentifier(input, ref idx, "service", ctx.Options.MaxIdentifierBytes, out _service))
            return false;
        if (!TryReadIdentifier(input, ref idx, "nodeId", ctx.Options.MaxIdentifierBytes, out _nodeId))
            return false;
        return true;
    }

    protected override void Run(CommandContext ctx, RespWriter writer)
    {
        var store = ctx.Store;

        using var _lock = ctx.Locks.Lock(_service);
        using var snap = store.Snapshot();
        using var batch = store.NewBatch();

        // Dead-node sweep (006): a node whose registration has gone stale gets its whole
        // processing list requeued at once and is dropped from the worker set, index and
        // registry — so it stops being locked and swept on every future dequeue.
        var pruned = SweepDeadNodes(ctx, store, snap, batch);

        // Lease sweep across the service's processing lists (skip nodes just pruned).
        if (ctx.Options.Lease > TimeSpan.Zero)
        {
            var leaseExpiry = ctx.NowTicks - ctx.Options.Lease.Ticks;
            foreach (var node in KnownWorkerNodes(store, snap, _service, _nodeId))
            {
                if (pruned.Contains(node)) continue; // already emptied above

                var dead = ConsumerSupport.SweepExpiredEntries(
                    store, batch,
                    procListName: HighwayNames.ServiceProcessing(_service, node),
                    liveQueueName: HighwayNames.ServiceQueue(_service),
                    dlqName: HighwayNames.ServiceDeadLetter(_service),
                    leaseExpiry: leaseExpiry,
                    nowTicks: ctx.NowTicks,
                    opts: ctx.Options,
                    decode: DecodeProcessing,
                    encodeQueueEntry: static (id, payload, attempts) => Envelope.EncodeRpcEntry(id, payload, attempts),
                    idToString: static id => Encoding.UTF8.GetString(id));

                foreach (var d in dead) _deadLettered.Add((d.Id, d.Attempts));
            }
        }

        var popped = store.ListLeftPop(batch, HighwayKeyspace.ListPrefix(HighwayNames.ServiceQueue(_service)));
        if (popped is null)
        {
            batch.Commit();
            writer.NullArray();
            return;
        }

        if (Envelope.IsLegacyEntry(popped))
            throw new StorageFormatException(HighwayNames.ServiceQueue(_service));

        Envelope.DecodeRpcEntry(popped, out var requestId, out var payload, out var attempts);

        var procEntry = Envelope.CarryFailureBlock(
            popped,
            Envelope.EncodeRpcProcessingEntry(ctx.NowTicks, requestId.ToArray(), payload.ToArray(), attempts));
        HwQSendCommand.PushTail(store, batch, HighwayNames.ServiceProcessing(_service, _nodeId), procEntry);

        store.SetAdd(batch, HighwayKeyspace.SetPrefix(HighwayNames.ServiceNodes(_service)), Encoding.UTF8.GetBytes(_nodeId));

        batch.Commit();

        _claimedRequestId = Encoding.UTF8.GetString(requestId);
        writer.BulkStringArray(requestId.ToArray(), payload.ToArray());
    }

    protected override void AfterCommit(CommandContext ctx)
    {
        foreach (var (id, attempts) in _deadLettered)
        {
            ctx.Recorder.Record(
                HighwayEventType.RpcDeadLettered, _service,
                nodeId: _nodeId, requestId: id, count: attempts, errorCode: DeadLetter.MaxAttempts);
        }

        // The claim itself (parity with the Garnet Finalize — found by the 040 fixture swap:
        // RpcEnqueued/RpcClaimed/RpcAcknowledged is the recorded round-trip shape the replay
        // and session-isolation tests pin). A nil dequeue records nothing — not an event.
        if (_claimedRequestId is not null)
        {
            ctx.Recorder.Record(
                HighwayEventType.RpcClaimed, _service,
                nodeId: _nodeId, requestId: _claimedRequestId);
        }
    }

    private static IEnumerable<string> KnownWorkerNodes(IHighwayStore store, IStoreSnapshot snap, string service, string self)
    {
        var members = store.SetMembers(snap, HighwayKeyspace.SetPrefix(HighwayNames.ServiceNodes(service)));
        var nodes = new HashSet<string>(members.Select(m => Encoding.UTF8.GetString(m))) { self };
        return nodes;
    }

    /// <summary>
    /// Removes nodes whose registration has gone stale (006): their unacknowledged requests go
    /// back to the queue tail, and they are dropped from this service's worker set, discovery
    /// index and the registry. This is what stops the worker set growing without bound.
    ///
    /// <para><b>Only nodes with a registration record are candidates.</b> A node with no record
    /// is not participating in the registry (a client may run with heartbeat disabled), and
    /// pruning it would requeue a healthy worker's in-flight work on every dequeue. Those are
    /// left to the per-entry lease sweep. Subscriber group state is never touched here (pub/sub
    /// outlives the process).</para>
    /// </summary>
    /// <returns>The node ids pruned, so the lease sweep can skip them.</returns>
    private HashSet<string> SweepDeadNodes(CommandContext ctx, IHighwayStore store, IStoreSnapshot snap, IStoreBatch batch)
    {
        var pruned = new HashSet<string>(StringComparer.Ordinal);
        if (!ctx.Options.PruningEnabled || ctx.Options.NodeExpiry <= TimeSpan.Zero)
            return pruned;

        foreach (var node in RegistrySupport.ReadSet(store, snap, HighwayNames.ServiceNodes(_service)))
        {
            if (node == _nodeId) continue; // the caller is demonstrably alive

            var record = store.Get(snap, HighwayKeyspace.Kv(HighwayNames.RegistrationNode(node)));
            if (record is null || record.Length < NodeRegistration.HeaderSize)
                continue; // not registered — leave it to the lease sweep

            if (!NodeRegistration.IsStale(record, ctx.NowTicks, ctx.Options.NodeExpiry))
                continue;

            // Order matters: recover the work before dropping the ownership record.
            RegistrySupport.RequeueNodeWork(store, batch, _service, node);
            RegistrySupport.RemoveNodeFromService(store, batch, _service, node);
            RegistrySupport.RemoveFromServiceIndex(store, batch, _service, node);
            RegistrySupport.RemoveRegistration(store, batch, node);

            pruned.Add(node);
        }

        return pruned;
    }

    private static void DecodeProcessing(ReadOnlySpan<byte> data, out long claimTicks, out byte[] id, out byte[] payload, out ushort attempts)
    {
        Envelope.DecodeRpcProcessingEntry(data, out claimTicks, out var idSpan, out var payloadSpan, out attempts);
        id = idSpan.ToArray();
        payload = payloadSpan.ToArray();
    }
}
