using System.Buffers;
using System.Text;
using Garnet.common;
using Garnet.server;
using Highway.Server.Internal;
using Highway.Server.Observability;
using Highway.Abstractions.Observability;
using Tsavorite.core;

namespace Highway.Server.Commands;

/// <summary>
/// HW.DEQUEUE &lt;service&gt; &lt;nodeId&gt; → [requestId, payload] | *-1\r\n (nil array)
///
/// Pops the head request from the service queue and claims it for the calling node.
/// Performs a lazy lease sweep first: entries in any node's processing list whose
/// claim timestamp has expired are returned to the queue tail for redelivery.
/// </summary>
internal sealed class HwDequeueCommand : HighwayCommandBase
{
    private readonly HighwayServerOptions _opts;
    private readonly FlightRecorder _recorder;

    private string _service = null!;
    private string _nodeId = null!;
    private string[] _knownNodes = [];
    private string? _claimedRequestId;

    /// <summary>
    /// Requests dead-lettered by this invocation's sweep, recorded in Finalize.
    /// A dead letter that nobody can see is the old infinite-retry bug with a quieter
    /// failure mode, so this is not optional bookkeeping.
    /// </summary>
    private readonly List<(string RequestId, ushort Attempts)> _deadLettered = [];


    public HwDequeueCommand(HighwayServerOptions opts, FlightRecorder recorder)
    {
        _opts = opts;
        _recorder = recorder;
    }

    /// <summary>
    /// Cleared because Garnet reuses one instance per session: a claimed request
    /// id left over from a previous successful dequeue would make the next NIL
    /// dequeue re-record a phantom claim.
    /// </summary>
    protected override void ResetState()
    {
        _claimedRequestId = null;
        _knownNodes = [];
        _deadLettered.Clear();
        ResetDeadLetterCounters();
    }

    protected override bool PrepareCore<TGarnetReadApi>(TGarnetReadApi api, ref CustomProcedureInput procInput)
    {
        int idx = 0;
        if (!TryReadIdentifier(ref procInput, ref idx, "service", _opts.MaxIdentifierBytes, out _service))
            return true;
        if (!TryReadIdentifier(ref procInput, ref idx, "nodeId", _opts.MaxIdentifierBytes, out _nodeId))
            return true;

        // Read current set of known nodes from the main-store node list key.
        // We CANNOT use SetMembers on the object-store set because GarnetWatchApi
        // triggers a WATCH, and the subsequent exclusive lock on the same key causes
        // a Shared+Exclusive lock conflict that fails the transaction.
        var nodeListKey = CreateArgSlice(HighwayKeys.ServiceNodeList(_service));
        PinnedSpanByte nodeListValue;
        api.GET(nodeListKey, out nodeListValue);
        _knownNodes = nodeListValue.Length > 0
            ? Encoding.UTF8.GetString(nodeListValue.ReadOnlySpan).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            : [];

        // Lock queue, node list (main store), nodes set, caller's proc list, and all known proc lists
        AddKey(CreateArgSlice(HighwayKeys.ServiceQueue(_service)), LockType.Exclusive, StoreType.Object);
        AddKey(nodeListKey, LockType.Exclusive, StoreType.Main);
        AddKey(CreateArgSlice(HighwayKeys.ServiceNodes(_service)), LockType.Exclusive, StoreType.Object);
        AddKey(CreateArgSlice(HighwayKeys.ServiceProcessing(_service, _nodeId)), LockType.Exclusive, StoreType.Object);

        // The dead-letter list is written by the lease sweep in Main (feature 013). Its
        // name derives from the service argument alone, so declaring it here costs no
        // read — which matters: reading object-store state in Prepare registers a watch
        // that the exclusive lock below would then fail (004.1).
        AddKey(CreateArgSlice(HighwayKeys.ServiceDeadLetter(_service)), LockType.Exclusive, StoreType.Object);

        foreach (var node in _knownNodes)
        {
            if (node == _nodeId) continue;
            AddKey(CreateArgSlice(HighwayKeys.ServiceProcessing(_service, node)), LockType.Exclusive, StoreType.Object);
        }

        // Registry keys for the dead-node sweep (feature 006). Locked, not read:
        // reading them here would only add watches for no benefit — Main reads
        // them under the lock.
        AddKey(CreateArgSlice(HighwayKeys.RegistrationNodeList), LockType.Exclusive, StoreType.Main);
        AddKey(CreateArgSlice(HighwayKeys.RegistrationService(_service)), LockType.Exclusive, StoreType.Main);
        foreach (var node in _knownNodes)
            AddKey(CreateArgSlice(HighwayKeys.RegistrationNode(node)), LockType.Exclusive, StoreType.Main);

        return true;
    }

    public override void Main<TGarnetApi>(TGarnetApi api, ref CustomProcedureInput procInput, ref MemoryResult<byte> output)
    {
        if (TryWriteError(ref output)) return;

        try
        {
            var queueKey      = CreateArgSlice(HighwayKeys.ServiceQueue(_service));
            var callerProcKey = CreateArgSlice(HighwayKeys.ServiceProcessing(_service, _nodeId));

            // Dead-node sweep (feature 006): a node whose registration has gone
            // stale gets its whole processing list returned to the queue at once,
            // and its lock/sweep cost removed from every future dequeue.
            var pruned = SweepDeadNodes(api);

            // Lazy lease sweep — skip when Lease == TimeSpan.Zero (disabled)
            if (_opts.Lease > TimeSpan.Zero)
            {
                var leaseExpiry = DateTime.UtcNow.Ticks - _opts.Lease.Ticks;
                var allNodes = _knownNodes.Contains(_nodeId)
                    ? _knownNodes
                    : [.. _knownNodes, _nodeId];

                foreach (var node in allNodes)
                {
                    if (pruned.Contains(node)) continue; // already emptied above
                    var procKey = CreateArgSlice(HighwayKeys.ServiceProcessing(_service, node));
                    var sweepStatus = api.ListLeftPop(procKey, int.MaxValue, out var entries);
                    if (sweepStatus != GarnetStatus.OK || entries is null) continue;

                    var keep = new List<byte[]>();
                    foreach (var entry in entries)
                    {
                        var span = entry.ReadOnlySpan;

                        // A pre-013 entry must be refused, never skipped and never
                        // misparsed: reading it as a current entry would reinterpret its
                        // leading bytes and hand a corrupt payload to an application.
                        if (Envelope.IsLegacyEntry(span))
                            throw new StorageFormatException(HighwayKeys.ServiceProcessing(_service, node));

                        Envelope.DecodeRpcProcessingEntry(
                            span, out var claimTicks, out var reqId, out var msgPayload, out var attempts);

                        if (claimTicks >= leaseExpiry)
                        {
                            keep.Add(span.ToArray());
                            continue;
                        }

                        // Expired. This is the redelivery path that used to be unbounded:
                        // the entry went back on the queue with nothing counting how often
                        // that had already happened, so a permanently failing request was
                        // retried for the life of the deployment — and, the queue being
                        // FIFO, retried ahead of everything behind it.
                        var next = Envelope.NextAttempt(attempts);

                        if (_opts.MaxDeliveryAttempts > 0 && next > _opts.MaxDeliveryAttempts)
                        {
                            var original = Envelope.EncodeRpcEntry(reqId, msgPayload, next);
                            var dead = DeadLetter.Encode(
                                DateTime.UtcNow.Ticks, next, DeadLetter.MaxAttempts, original);

                            var dlqKey = CreateArgSlice(HighwayKeys.ServiceDeadLetter(_service));
                            api.ListRightPush(dlqKey, CreateArgSlice(dead), out _);
                            TrimDeadLetters(api, dlqKey, _opts.MaxDeadLetterEntries);

                            // Removal from the processing list and the push above are the
                            // same transaction, so the entry is never in both lists and
                            // never in neither.
                            _deadLettered.Add((Encoding.UTF8.GetString(reqId), next));
                            continue;
                        }

                        // No backoff path for RPC by default. A caller waits against
                        // CallTimeout (30s) while Lease defaults to 5 minutes, so by the
                        // time a retry is possible the caller has already given up —
                        // delaying it further changes nothing except when the dead-letter
                        // happens. See HighwayServerOptions.RpcBackoffEnabled.
                        var rpcEntry = Envelope.EncodeRpcEntry(reqId, msgPayload, next);
                        api.ListRightPush(queueKey, CreateArgSlice(rpcEntry), out _);
                    }

                    // Restore non-expired entries
                    foreach (var e in keep)
                        api.ListRightPush(procKey, CreateArgSlice(e), out _);
                }
            }

            // Pop the head of the queue
            var popStatus = api.ListLeftPop(queueKey, out var popped);
            if (popStatus != GarnetStatus.OK || popped.Length == 0)
            {
                WriteNullArray(ref output);
                return;
            }

            // Wrap with claim timestamp and push to caller's proc list
            if (Envelope.IsLegacyEntry(popped.ReadOnlySpan))
                throw new StorageFormatException(HighwayKeys.ServiceQueue(_service));

            Envelope.DecodeRpcEntry(
                popped.ReadOnlySpan, out var requestId, out var deqPayload, out var claimedAttempts);

            // The attempt count travels with the claim. Resetting it here would make the
            // limit unreachable, because every redelivery starts with a fresh claim.
            var procEntry = Envelope.EncodeRpcProcessingEntry(
                DateTime.UtcNow.Ticks, requestId, deqPayload, claimedAttempts);
            api.ListRightPush(callerProcKey, CreateArgSlice(procEntry), out _);

            // Register this node in the nodes set and maintain the main-store node list
            var nodesKey    = CreateArgSlice(HighwayKeys.ServiceNodes(_service));
            var nodeIdSlice = CreateArgSlice(Encoding.UTF8.GetBytes(_nodeId));
            api.SetAdd(nodesKey, nodeIdSlice, out _);

            // Update main-store node list (for future Prepare reads)
            var nodeListKey = CreateArgSlice(HighwayKeys.ServiceNodeList(_service));
            if (!_knownNodes.Contains(_nodeId))
            {
                var newNodeList = _knownNodes.Length > 0
                    ? string.Join('\n', _knownNodes) + "\n" + _nodeId
                    : _nodeId;
                api.SET(nodeListKey, CreateArgSlice(newNodeList));
            }

            // Reply [requestId, payload]
            _claimedRequestId = Encoding.UTF8.GetString(requestId);
            WriteBulkStringArray(ref output, CreateArgSlice(requestId), CreateArgSlice(deqPayload));
        }
        catch (Exception ex)
        {
            WriteError(ref output, HighwayErrors.InternalError(ex.Message));
        }
    }

    /// <summary>
    /// Removes nodes whose registration has gone stale: their unacknowledged
    /// requests go back to the queue tail, and they are dropped from this
    /// service's worker set, its discovery index, and the registry. This is what
    /// stops <c>hw:svc:{service}:nodes</c> growing without bound and discharges
    /// the pruning deferral recorded in feature 004.1.
    ///
    /// <para><b>Only nodes that have a registration record are candidates.</b> A
    /// node with no record is not participating in the registry at all — the
    /// client can run with <c>HeartbeatEnabled = false</c> — and pruning it would
    /// requeue the in-flight work of a perfectly healthy worker on every dequeue,
    /// turning a configuration choice into a duplicate-execution storm. Those
    /// nodes are left to the per-entry lease sweep, exactly as before 006.</para>
    /// </summary>
    /// <returns>The node IDs pruned, so the lease sweep can skip them.</returns>
    private HashSet<string> SweepDeadNodes<TGarnetApi>(TGarnetApi api)
        where TGarnetApi : IGarnetApi
    {
        var pruned = new HashSet<string>(StringComparer.Ordinal);
        if (!_opts.PruningEnabled || _opts.NodeExpiry <= TimeSpan.Zero)
            return pruned;

        var now = DateTime.UtcNow.Ticks;

        foreach (var node in _knownNodes)
        {
            // The caller is demonstrably alive — it is issuing this command.
            if (node == _nodeId) continue;

            api.GET(CreateArgSlice(HighwayKeys.RegistrationNode(node)), out PinnedSpanByte record);
            if (record.Length < NodeRegistration.HeaderSize)
                continue; // not registered — see the note above

            if (!NodeRegistration.IsStale(record.ReadOnlySpan, now, _opts.NodeExpiry))
                continue;

            // Order matters: recover the work before dropping the ownership record.
            RequeueNodeWork(api, _service, node);
            RemoveNodeFromService(api, _service, node);
            RemoveFromServiceIndex(api, _service, node);
            RemoveRegistration(api, node);

            // NOTE: hw:ch:*:grp:{node}:* is deliberately NOT touched. A subscriber
            // group outlives its node's process so a restart resumes its pending
            // messages (005 Req 9 AC3). Deleting groups here would silently
            // downgrade durable pub/sub to fire-and-forget for any node that
            // outlives its expiry window.

            pruned.Add(node);
        }

        if (pruned.Count > 0)
            _knownNodes = [.. _knownNodes.Where(n => !pruned.Contains(n))];

        return pruned;
    }

    /// <summary>Writes a null array (*-1\r\n) to <paramref name="output"/>.</summary>
    private static void WriteNullArray(ref MemoryResult<byte> output)
    {
        const int len = 5;
        output.MemoryOwner?.Dispose();
        output.MemoryOwner = MemoryPool<byte>.Shared.Rent(len);
        output.Length = len;
        var span = output.MemoryOwner.Memory.Span;
        span[0] = (byte)'*';
        span[1] = (byte)'-';
        span[2] = (byte)'1';
        span[3] = (byte)'\r';
        span[4] = (byte)'\n';
    }

    /// <summary>Records the claim. A nil dequeue records nothing — an empty poll is not an event.</summary>
    public override void Finalize<TGarnetApi>(TGarnetApi api, ref CustomProcedureInput procInput, ref MemoryResult<byte> output)
    {
        // Dead letters are recorded even when this dequeue returned nil: the sweep that
        // produced them ran regardless of whether there was work to claim.
        foreach (var (requestId, attempts) in _deadLettered)
        {
            _recorder.Record(
                HighwayEventType.RpcDeadLettered, _service ?? "?",
                nodeId: _nodeId,
                requestId: requestId,
                count: attempts,
                errorCode: DeadLetter.MaxAttempts);
        }

        if (!Failed && _claimedRequestId is null) return;
        _recorder.Record(
            HighwayEventType.RpcClaimed, _service ?? "?",
            nodeId: _nodeId,
            requestId: _claimedRequestId,
            errorCode: FailureCode);
    }
}
