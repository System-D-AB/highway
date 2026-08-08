using System.Globalization;
using System.Text;
using Garnet.common;
using Garnet.server;
using Highway.Server.Internal;
using Highway.Server.Observability;
using Highway.Abstractions.Observability;
using Tsavorite.core;

namespace Highway.Server.Commands;

/// <summary>
/// HW.QCLAIM &lt;queue&gt; &lt;nodeId&gt; → [messageId, payload] | nil (feature 014).
///
/// <para>Claims the next message for one worker. <b>Competing consumers by default</b>:
/// every instance calling this shares the work, with no group name and no coupling to node
/// identity — the property Pub/Sub cannot express.</para>
///
/// <para>Promotes due deferred messages, then sweeps expired leases through the shared
/// <c>SweepExpiredEntries</c> — the same attempt counting and dead-lettering
/// <c>HW.DEQUEUE</c> uses, not a second copy of it.</para>
///
/// <para>Unlike <c>HW.DEQUEUE</c> there is no dead-node prune: a queue has no service
/// registry, so an abandoned claim is recovered by the lease sweep alone.</para>
/// </summary>
internal sealed class HwQClaimCommand : HighwayCommandBase
{
    /// <summary>Most deferred messages promoted in one claim; the rest follow on later polls.</summary>
    private const int MaxPromotionBatch = 256;

    private readonly HighwayServerOptions _opts;
    private readonly FlightRecorder _recorder;

    private string _queue = null!;
    private string _nodeId = null!;
    private string[] _knownNodes = [];
    private string? _claimedId;
    private readonly List<(string Id, ushort Attempts)> _deadLettered = [];

    public HwQClaimCommand(HighwayServerOptions opts, FlightRecorder recorder)
    {
        _opts = opts;
        _recorder = recorder;
    }

    protected override void ResetState()
    {
        _claimedId = null;
        _knownNodes = [];
        _deadLettered.Clear();
        ResetDeadLetterCounters();
    }

    protected override bool PrepareCore<TGarnetReadApi>(TGarnetReadApi api, ref CustomProcedureInput procInput)
    {
        var idx = 0;
        if (!TryReadIdentifier(ref procInput, ref idx, "queue", _opts.MaxIdentifierBytes, out _queue))
            return true;
        if (!TryReadIdentifier(ref procInput, ref idx, "nodeId", _opts.MaxIdentifierBytes, out _nodeId))
            return true;

        // Read the worker list from the main-store mirror, never the object-store set: an
        // object-store read here registers a watch that the exclusive locks below would
        // then fail (004.1).
        var nodeListKey = CreateArgSlice(HighwayKeys.QueueNodeList(_queue));
        api.GET(nodeListKey, out PinnedSpanByte nodeListValue);
        _knownNodes = SplitList(nodeListValue);

        AddKey(CreateArgSlice(HighwayKeys.Queue(_queue)), LockType.Exclusive, StoreType.Object);
        AddKey(CreateArgSlice(HighwayKeys.QueueDelayed(_queue)), LockType.Exclusive, StoreType.Object);
        AddKey(CreateArgSlice(HighwayKeys.QueueDeadLetter(_queue)), LockType.Exclusive, StoreType.Object);
        AddKey(CreateArgSlice(HighwayKeys.QueueNodes(_queue)), LockType.Exclusive, StoreType.Object);
        AddKey(nodeListKey, LockType.Exclusive, StoreType.Main);
        AddKey(CreateArgSlice(HighwayKeys.QueueProcessing(_queue, _nodeId)), LockType.Exclusive, StoreType.Object);

        foreach (var node in _knownNodes)
        {
            if (node == _nodeId) continue;
            AddKey(CreateArgSlice(HighwayKeys.QueueProcessing(_queue, node)), LockType.Exclusive, StoreType.Object);
        }

        return true;
    }

    public override void Main<TGarnetApi>(TGarnetApi api, ref CustomProcedureInput procInput, ref MemoryResult<byte> output)
    {
        if (TryWriteError(ref output)) return;

        try
        {
            var queueKey = CreateArgSlice(HighwayKeys.Queue(_queue));
            var procKey = CreateArgSlice(HighwayKeys.QueueProcessing(_queue, _nodeId));

            PromoteDueMessages(api, queueKey);

            if (_opts.Lease > TimeSpan.Zero)
            {
                var leaseExpiry = DateTime.UtcNow.Ticks - _opts.Lease.Ticks;
                var allNodes = _knownNodes.Contains(_nodeId) ? _knownNodes : [.. _knownNodes, _nodeId];

                foreach (var node in allNodes)
                {
                    var dead = SweepExpiredEntries(
                        api,
                        procKey: CreateArgSlice(HighwayKeys.QueueProcessing(_queue, node)),
                        queueKey: queueKey,
                        dlqKey: CreateArgSlice(HighwayKeys.QueueDeadLetter(_queue)),
                        procKeyName: HighwayKeys.QueueProcessing(_queue, node),
                        leaseExpiry: leaseExpiry,
                        opts: _opts,
                        decode: DecodeProcessing,
                        encodeQueueEntry: static (id, payload, attempts) =>
                            Envelope.EncodeRpcEntry(id, payload, attempts),
                        idToString: static id => Encoding.UTF8.GetString(id));

                    foreach (var d in dead)
                        _deadLettered.Add((d.Id, d.Attempts));
                }
            }

            var popStatus = api.ListLeftPop(queueKey, out var popped);
            if (popStatus != GarnetStatus.OK || popped.Length == 0)
            {
                WriteNullArray(ref output);
                return;
            }

            if (Envelope.IsLegacyEntry(popped.ReadOnlySpan))
                throw new StorageFormatException(HighwayKeys.Queue(_queue));

            Envelope.DecodeRpcEntry(popped.ReadOnlySpan, out var messageId, out var payload, out var attempts);

            // The attempt count travels with the claim; resetting it here would make the
            // limit unreachable, because every redelivery starts a fresh claim.
            var procEntry = Envelope.CarryFailureBlock(
                popped.ReadOnlySpan,
                Envelope.EncodeRpcProcessingEntry(DateTime.UtcNow.Ticks, messageId, payload, attempts));
            api.ListRightPush(procKey, CreateArgSlice(procEntry), out _);

            // Register this worker so future claims sweep its list too.
            api.SetAdd(
                CreateArgSlice(HighwayKeys.QueueNodes(_queue)),
                CreateArgSlice(Encoding.UTF8.GetBytes(_nodeId)),
                out _);

            if (!_knownNodes.Contains(_nodeId))
            {
                var updated = _knownNodes.Length > 0
                    ? string.Join('\n', _knownNodes) + "\n" + _nodeId
                    : _nodeId;
                api.SET(CreateArgSlice(HighwayKeys.QueueNodeList(_queue)), CreateArgSlice(updated));
            }

            _claimedId = Encoding.UTF8.GetString(messageId);
            WriteBulkStringArray(ref output, CreateArgSlice(messageId), CreateArgSlice(payload));
        }
        catch (StorageFormatException ex)
        {
            WriteError(ref output, HighwayErrors.StorageFormatError(ex.Message));
        }
        catch (Exception ex)
        {
            WriteError(ref output, HighwayErrors.InternalError(ex.Message));
        }
    }

    /// <summary>
    /// Moves messages whose delivery time has passed into the live queue.
    ///
    /// <para>Range-read then remove, never pop-and-restore: popping first means anything
    /// not yet due must be written back, and a gap between the two loses it. Reading by
    /// score also avoids parsing a score back — Garnet stores them as doubles and formats
    /// them with the current culture, so a tick count returns as <c>6,39E+17</c> on a
    /// European machine.</para>
    /// </summary>
    private void PromoteDueMessages<TGarnetApi>(TGarnetApi api, PinnedSpanByte queueKey)
        where TGarnetApi : IGarnetApi
    {
        var delayedKey = CreateArgSlice(HighwayKeys.QueueDelayed(_queue));
        var now = DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture);

        var status = api.SortedSetRange(
            delayedKey,
            CreateArgSlice("-inf"),
            CreateArgSlice(now),
            SortedSetOrderOperation.ByScore,
            out var due,
            out _,
            withScores: false,
            reverse: false,
            limit: ("0", MaxPromotionBatch));

        if (status != GarnetStatus.OK || due is null || due.Length == 0)
            return;

        foreach (var member in due)
        {
            var entry = member.ReadOnlySpan.ToArray();
            api.ListRightPush(queueKey, CreateArgSlice(entry), out _);
            api.SortedSetRemove(delayedKey, CreateArgSlice(entry), out _);
        }
    }

    private static void DecodeProcessing(
        ReadOnlySpan<byte> data, out long claimTicks, out byte[] id, out byte[] payload, out ushort attempts)
    {
        Envelope.DecodeRpcProcessingEntry(data, out claimTicks, out var idSpan, out var payloadSpan, out attempts);
        id = idSpan.ToArray();
        payload = payloadSpan.ToArray();
    }

    public override void Finalize<TGarnetApi>(TGarnetApi api, ref CustomProcedureInput procInput, ref MemoryResult<byte> output)
    {
        foreach (var (id, attempts) in _deadLettered)
        {
            _recorder.Record(
                HighwayEventType.QueueDeadLettered, _queue ?? "?",
                nodeId: _nodeId,
                requestId: id,
                count: attempts,
                errorCode: DeadLetter.MaxAttempts);
        }

        if (!Failed && _claimedId is null) return;

        _recorder.Record(
            HighwayEventType.QueueClaimed, _queue ?? "?",
            nodeId: _nodeId,
            requestId: _claimedId,
            errorCode: FailureCode);
    }
}
