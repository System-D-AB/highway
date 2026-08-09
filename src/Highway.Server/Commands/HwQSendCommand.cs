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
/// HW.QSEND &lt;queue&gt; &lt;messageId&gt; &lt;payload&gt; [AT &lt;ticks&gt;] → +OK (feature 014).
///
/// <para>Enqueues work for exactly one processor. This is <c>HW.CALL</c> without a reply
/// slot: the sender is not waiting for an answer, so nothing is allocated for one.</para>
///
/// <para><b>Sending never requires a running processor.</b> The message waits in
/// <c>hw:q:{queue}:q</c> until a worker claims it. That is the capability whose absence
/// made people misuse <c>HW.PUBLISH</c> as a queue.</para>
///
/// <para>The optional <c>AT</c> argument defers delivery, reusing feature 013's delayed
/// mechanism. It is an <b>absolute</b> tick count so AOF replay cannot re-delay from
/// replay time.</para>
/// </summary>
internal sealed class HwQSendCommand : HighwayCommandBase
{
    private readonly HighwayServerOptions _opts;
    private readonly DoorbellBridge _doorbell;
    private readonly FlightRecorder _recorder;

    private string _queue = null!;
    private string _messageId = null!;
    private byte[] _messageIdBytes = [];
    private byte[] _payloadBytes = [];
    private long _deliverAtTicks;

    public HwQSendCommand(HighwayServerOptions opts, DoorbellBridge doorbell, FlightRecorder recorder)
    {
        _opts = opts;
        _doorbell = doorbell;
        _recorder = recorder;
    }

    protected override void ResetState()
    {
        _messageIdBytes = [];
        _payloadBytes = [];

        // Must reset with the rest: a stale value would count a refusal that did not happen on
        // the next invocation of this pooled instance -- exactly the leak feature 004.1's
        // sealed Prepare was written to make structurally impossible.
        _refusedReason = null;
        _deliverAtTicks = 0;
    }

    protected override bool PrepareCore<TGarnetReadApi>(TGarnetReadApi api, ref CustomProcedureInput procInput)
    {
        var idx = 0;
        if (!TryReadIdentifier(ref procInput, ref idx, "queue", _opts.MaxIdentifierBytes, out _queue))
            return true;
        if (!TryReadIdentifier(ref procInput, ref idx, "messageId", _opts.MaxIdentifierBytes, out _messageId, out _messageIdBytes))
            return true;
        if (!TryReadPayload(ref procInput, ref idx, _opts.MaxPayloadBytes, out _payloadBytes))
            return true;

        var keyword = GetNextArg(ref procInput, ref idx);
        if (keyword.Length > 0)
        {
            var word = Encoding.ASCII.GetString(keyword.ReadOnlySpan).ToUpperInvariant();
            if (word != "AT")
            {
                Fail(HighwayErrors.InvalidArg, $"unknown argument '{word}'; expected AT");
                return true;
            }

            var value = GetNextArg(ref procInput, ref idx);
            if (value.Length == 0
                || !long.TryParse(Encoding.ASCII.GetString(value.ReadOnlySpan), out var ticks)
                || ticks < 0)
            {
                Fail(HighwayErrors.InvalidArg, "AT requires a non-negative .NET UTC tick count");
                return true;
            }

            _deliverAtTicks = ticks;
        }

        // Both derived from the queue name alone, so no Prepare-phase read and therefore
        // no watch conflict (004.1).
        AddKey(CreateArgSlice(HighwayKeys.Queue(_queue)), LockType.Exclusive, StoreType.Object);
        AddKey(CreateArgSlice(HighwayKeys.QueueDelayed(_queue)), LockType.Exclusive, StoreType.Object);

        // Byte accounting (016). Derived from the queue name alone, so it is declarable here —
        // which is the whole reason the counter lives in the main store beside the queue
        // rather than being computed from the structure in Main.
        AddKey(CreateArgSlice(HighwayKeys.QueueBytes(_queue)), LockType.Exclusive, StoreType.Main);
        return true;
    }

    public override void Main<TGarnetApi>(TGarnetApi api, ref CustomProcedureInput procInput, ref MemoryResult<byte> output)
    {
        if (TryWriteError(ref output)) return;

        try
        {
            var entry = Envelope.EncodeRpcEntry(_messageIdBytes, _payloadBytes);
            var counterKey = HighwayKeys.QueueBytes(_queue);

            // Refuse rather than drop (016 R4.1). Under C1.2 a queued message is one nobody has
            // ever processed, so discarding the oldest to make room loses exactly the data the
            // queue exists to protect. The producer is told; the stored messages are untouched.
            if (_opts.MaxQueueBytes > 0)
            {
                var current = ReadByteCounter(api, counterKey);
                if (current + entry.Length > _opts.MaxQueueBytes)
                {
                    _refusedReason = $"queue '{_queue}' at {current}/{_opts.MaxQueueBytes} bytes";
                    WriteError(ref output, HighwayErrors.Format(
                        HighwayErrors.QueueFull,
                        $"queue '{_queue}' is at its limit ({current} of {_opts.MaxQueueBytes} bytes); " +
                        "the message was not stored"));
                    return;
                }
            }

            AdjustByteCounter(api, counterKey, entry.Length);

            if (_deliverAtTicks > DateTime.UtcNow.Ticks)
            {
                api.SortedSetAdd(
                    CreateArgSlice(HighwayKeys.QueueDelayed(_queue)),
                    CreateArgSlice(Encoding.ASCII.GetBytes(_deliverAtTicks.ToString(CultureInfo.InvariantCulture))),
                    CreateArgSlice(entry),
                    out _);
            }
            else
            {
                api.ListRightPush(CreateArgSlice(HighwayKeys.Queue(_queue)), CreateArgSlice(entry), out _);
            }

            WriteSimpleString(ref output, "OK");
        }
        catch (Exception ex)
        {
            WriteError(ref output, HighwayErrors.InternalError(ex.Message));
        }
    }

    /// <summary>Set when the byte limit refused this send, so Finalize can count it.</summary>
    private string? _refusedReason;

    public override void Finalize<TGarnetApi>(TGarnetApi api, ref CustomProcedureInput procInput, ref MemoryResult<byte> output)
    {
        if (_refusedReason is not null)
        {
            _recorder.Record(
                HighwayEventType.SendRefused, _queue ?? "?",
                requestId: _messageId,
                errorCode: _refusedReason);
        }

        _recorder.Record(
            HighwayEventType.QueueSent, _queue ?? "?",
            requestId: _messageId,
            payload: _payloadBytes,
            errorCode: FailureCode);

        if (Failed) return;

        // A deferred message is in nobody's queue yet; ringing would wake every worker to
        // find nothing. It is promoted by a worker's own poll instead.
        if (_deliverAtTicks > DateTime.UtcNow.Ticks) return;

        _doorbell.Ring(HighwayKeys.QueueDoorbell(_queue!), _messageIdBytes);
    }
}
