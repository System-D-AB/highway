using System.Buffers;
using System.Text;
using Garnet.common;
using Garnet.server;
using Highway.Server.Internal;
using Tsavorite.core;

namespace Highway.Server.Commands;

/// <summary>
/// HW.DLQ &lt;action&gt; &lt;target...&gt; [COUNT n] — inspect and manage dead letters (feature 013).
///
/// <code>
/// HW.DLQ PEEK    SVC &lt;service&gt;            [COUNT n]  → array of entries, non-destructive
/// HW.DLQ PEEK    Q   &lt;queue&gt;              [COUNT n]
/// HW.DLQ REQUEUE SVC &lt;service&gt;            [COUNT n]  → integer moved back
/// HW.DLQ PURGE   SVC &lt;service&gt;            [COUNT n]  → integer removed
/// </code>
///
/// <para><b>PEEK is listed first deliberately.</b> The supported workflow is look, then
/// decide — an operator who can only drain has to destroy the evidence to see it.</para>
///
/// <para><b>REQUEUE resets the attempt count.</b> An operator requeues <i>after fixing
/// something</i>; a message that immediately re-dead-letters has wasted the round trip and
/// taught nobody anything.</para>
///
/// <para><b>Requeue is operator-initiated, never automatic.</b> A queue that re-feeds its
/// own failures is a loop with extra steps — which is the defect this whole feature
/// exists to remove.</para>
///
/// <para>An unknown service or channel returns an empty array or zero, never an error,
/// matching <c>HW.DISCOVER</c> and <c>HW.STATS</c>: an operator querying a name that has
/// seen no traffic deserves an answer, not a failure.</para>
/// </summary>
internal sealed class HwDlqCommand : HighwayCommandBase
{
    private const string ActionPeek = "PEEK";
    private const string ActionRequeue = "REQUEUE";
    private const string ActionPurge = "PURGE";
    private const string TargetService = "SVC";
    private const string TargetQueue = "Q";

    private readonly HighwayServerOptions _opts;

    private string _action = null!;
    private string _dlqKey = null!;
    private string _liveKey = null!;
    private int _count;

    public HwDlqCommand(HighwayServerOptions opts) => _opts = opts;

    protected override void ResetState()
    {
        _count = 0;
    }

    protected override bool PrepareCore<TGarnetReadApi>(TGarnetReadApi api, ref CustomProcedureInput procInput)
    {
        var idx = 0;

        var actionArg = GetNextArg(ref procInput, ref idx);
        if (actionArg.Length == 0)
        {
            Fail(HighwayErrors.InvalidArg, "HW.DLQ requires an action: PEEK, REQUEUE or PURGE");
            return true;
        }

        _action = Encoding.ASCII.GetString(actionArg.ReadOnlySpan).ToUpperInvariant();
        if (_action is not (ActionPeek or ActionRequeue or ActionPurge))
        {
            Fail(HighwayErrors.InvalidArg,
                $"unknown action '{_action}'; expected PEEK, REQUEUE or PURGE");
            return true;
        }

        var kindArg = GetNextArg(ref procInput, ref idx);
        if (kindArg.Length == 0)
        {
            Fail(HighwayErrors.InvalidArg, "HW.DLQ requires a target: SVC <service> or Q <queue>");
            return true;
        }

        var kind = Encoding.ASCII.GetString(kindArg.ReadOnlySpan).ToUpperInvariant();
        switch (kind)
        {
            case TargetService:
            {
                if (!TryReadIdentifier(ref procInput, ref idx, "service", _opts.MaxIdentifierBytes, out var service))
                    return true;
                _dlqKey  = HighwayKeys.ServiceDeadLetter(service);
                _liveKey = HighwayKeys.ServiceQueue(service);
                break;
            }

            case TargetQueue:
            {
                if (!TryReadDerivedIdentifier(ref procInput, ref idx, "queue", _opts.MaxIdentifierBytes, out var queue))
                    return true;
                _dlqKey  = HighwayKeys.QueueDeadLetter(queue);
                _liveKey = HighwayKeys.Queue(queue);
                break;
            }

            default:
                Fail(HighwayErrors.InvalidArg, $"unknown target '{kind}'; accepted forms are SVC <service> or Q <queue>");
                return true;
        }

        _count = _opts.ReceiveDefaultCount;

        var keyword = GetNextArg(ref procInput, ref idx);
        if (keyword.Length > 0)
        {
            var word = Encoding.ASCII.GetString(keyword.ReadOnlySpan).ToUpperInvariant();
            if (word != "COUNT")
            {
                Fail(HighwayErrors.InvalidArg, $"unknown argument '{word}'; expected COUNT");
                return true;
            }

            var value = GetNextArg(ref procInput, ref idx);
            if (value.Length == 0
                || !int.TryParse(Encoding.ASCII.GetString(value.ReadOnlySpan), out var count)
                || count < 1)
            {
                Fail(HighwayErrors.InvalidCount, "COUNT must be a positive integer");
                return true;
            }

            if (count > _opts.ReceiveMaxCount)
            {
                Fail(HighwayErrors.InvalidCount, $"COUNT {count} exceeds maximum {_opts.ReceiveMaxCount}");
                return true;
            }

            _count = count;
        }

        // Both keys are derived from the arguments alone, so declaring them costs no
        // Prepare-phase read — which matters, because reading object-store state here
        // registers a watch that the exclusive lock would then fail (004.1).
        // Exclusive even for PEEK: Garnet's list surface has no range read here, so PEEK
        // pops and restores in the same transaction, which is a mutation from the store's
        // point of view even though the list is unchanged when the command returns.
        AddKey(CreateArgSlice(_dlqKey), LockType.Exclusive, StoreType.Object);
        if (_action == ActionRequeue)
            AddKey(CreateArgSlice(_liveKey), LockType.Exclusive, StoreType.Object);

        return true;
    }

    public override void Main<TGarnetApi>(TGarnetApi api, ref CustomProcedureInput procInput, ref MemoryResult<byte> output)
    {
        if (TryWriteError(ref output)) return;

        try
        {
            switch (_action)
            {
                case ActionPeek:    Peek(api, ref output); break;
                case ActionRequeue: Requeue(api, ref output); break;
                default:            Purge(api, ref output); break;
            }
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
    /// Non-destructive read. Pops up to <c>_count</c> entries and pushes them straight
    /// back in the same transaction, so the list is unchanged when the command returns.
    ///
    /// <para>Garnet's list API has no range read on this surface, so a pop-and-restore is
    /// the available shape. It is safe because the whole command is one transaction under
    /// an exclusive-equivalent lock: no other command observes the intermediate state.</para>
    /// </summary>
    private void Peek<TGarnetApi>(TGarnetApi api, ref MemoryResult<byte> output)
        where TGarnetApi : IGarnetApi
    {
        var dlqKey = CreateArgSlice(_dlqKey);
        var status = api.ListLeftPop(dlqKey, _count, out var entries);

        if (status != GarnetStatus.OK || entries is null || entries.Length == 0)
        {
            WriteEmptyArray(ref output);
            return;
        }

        var snapshot = new List<byte[]>(entries.Length);
        foreach (var entry in entries)
            snapshot.Add(entry.ReadOnlySpan.ToArray());

        // Restore in order: they came off the head, so they go back on the head reversed.
        for (var i = snapshot.Count - 1; i >= 0; i--)
            api.ListLeftPush(dlqKey, CreateArgSlice(snapshot[i]), out _);

        WriteDeadLetters(ref output, snapshot);
    }

    /// <summary>
    /// Moves entries back to the live queue with the attempt count reset, so a fixed
    /// bug can be replayed against the traffic that exposed it.
    /// </summary>
    private void Requeue<TGarnetApi>(TGarnetApi api, ref MemoryResult<byte> output)
        where TGarnetApi : IGarnetApi
    {
        var dlqKey  = CreateArgSlice(_dlqKey);
        var liveKey = CreateArgSlice(_liveKey);

        var status = api.ListLeftPop(dlqKey, _count, out var entries);
        if (status != GarnetStatus.OK || entries is null || entries.Length == 0)
        {
            WriteInteger(ref output, 0);
            return;
        }

        var moved = 0;
        foreach (var entry in entries)
        {
            DeadLetter.Decode(entry.ReadOnlySpan, out _, out _, out _, out var original);

            // Reset attempts: the operator is retrying after changing something, and a
            // message that immediately re-dead-letters has wasted the round trip.
            Envelope.DecodeRpcEntry(original, out var requestId, out var payload, out _);
            var revived = Envelope.EncodeRpcEntry(requestId, payload, attempts: 0);

            api.ListRightPush(liveKey, CreateArgSlice(revived), out _);
            moved++;
        }

        WriteInteger(ref output, moved);
    }

    private void Purge<TGarnetApi>(TGarnetApi api, ref MemoryResult<byte> output)
        where TGarnetApi : IGarnetApi
    {
        var dlqKey = CreateArgSlice(_dlqKey);
        var status = api.ListLeftPop(dlqKey, _count, out var entries);

        var removed = status == GarnetStatus.OK && entries is not null ? entries.Length : 0;
        WriteInteger(ref output, removed);
    }

    /// <summary>
    /// Writes one flat field/value array per dead letter — the same self-describing shape
    /// <c>HW.STATS</c> and <c>HW.REPLAY</c> use, so fields can be appended later without
    /// breaking readers.
    /// </summary>
    private unsafe void WriteDeadLetters(ref MemoryResult<byte> output, List<byte[]> entries)
    {
        var encoded = new List<List<byte[]>>(entries.Count);

        foreach (var raw in entries)
        {
            DeadLetter.Decode(raw, out var ticks, out var attempts, out var reason, out var original);

            var fields = new List<byte[]>(12);

            void Add(string name, string value)
            {
                fields.Add(Encoding.UTF8.GetBytes(name));
                fields.Add(Encoding.UTF8.GetBytes(value));
            }

            Add("deadLetteredAt", new DateTime(ticks, DateTimeKind.Utc).ToString("O"));
            Add("attempts", attempts.ToString());
            Add("reason", Encoding.UTF8.GetString(reason));

            Envelope.DecodeRpcEntry(original, out var requestId, out var payload, out _);
            Add("requestId", Encoding.UTF8.GetString(requestId));
            fields.Add("payload"u8.ToArray());
            fields.Add(payload.ToArray());

            // Why it died (015). Without these a dead letter says only that something failed
            // n times, and an operator has to correlate logs across every worker to learn what
            // threw — which is the problem feature 015 exists to remove.
            if (Envelope.TryGetFailureBlock(original, out var block, out _))
            {
                Envelope.DecodeFailureBlock(block, out var type, out var firstType, out var detail);

                Add("failureType", Encoding.UTF8.GetString(type));
                if (firstType.Length > 0)
                    Add("failureFirstType", Encoding.UTF8.GetString(firstType));

                fields.Add("failureDetail"u8.ToArray());
                fields.Add(detail.ToArray());
            }
            else
            {
                // Said explicitly rather than left as blank fields. A worker that crashed
                // before it could report is a different situation from one that reported
                // nothing useful, and an operator should not have to guess which they have.
                Add("failure", "not reported - the worker did not report a failure before this " +
                               "message exhausted its attempts");
            }

            encoded.Add(fields);
        }

        var totalLen = ArrayHeaderLen(encoded.Count);
        foreach (var fields in encoded)
        {
            totalLen += ArrayHeaderLen(fields.Count);
            foreach (var f in fields) totalLen += BulkStringLen(f.Length);
        }

        output.MemoryOwner?.Dispose();
        output.MemoryOwner = MemoryPool<byte>.Shared.Rent(totalLen);
        output.Length = totalLen;

        fixed (byte* ptr = output.MemoryOwner.Memory.Span)
        {
            var curr = ptr;
            var end = ptr + totalLen;
            RespWriteUtils.TryWriteArrayLength(encoded.Count, ref curr, end);
            foreach (var fields in encoded)
            {
                RespWriteUtils.TryWriteArrayLength(fields.Count, ref curr, end);
                foreach (var f in fields)
                    RespWriteUtils.TryWriteBulkString(f, ref curr, end);
            }
        }
    }

    /// <summary>
    /// Writes a RESP integer. Garnet's procedure base offers simple-string, bulk-string
    /// and array writers but no integer one, so this is the local equivalent.
    /// </summary>

    private static unsafe void WriteEmptyArray(ref MemoryResult<byte> output)
    {
        const int len = 4; // *0\r\n
        output.MemoryOwner?.Dispose();
        output.MemoryOwner = MemoryPool<byte>.Shared.Rent(len);
        output.Length = len;
        fixed (byte* ptr = output.MemoryOwner.Memory.Span)
        {
            var curr = ptr;
            RespWriteUtils.TryWriteArrayLength(0, ref curr, ptr + len);
        }
    }

    private static int ArrayHeaderLen(int count) => 1 + CountDigits(count) + 2;
    private static int BulkStringLen(int len) => 1 + CountDigits(len) + 2 + len + 2;

    private static int CountDigits(int value)
    {
        if (value < 0) value = -value;
        var digits = 1;
        while (value >= 10) { value /= 10; digits++; }
        return digits;
    }
}
