using System.Text;
using Highway.Server.Commands.Runtime;
using Highway.Server.Internal;
using Highway.Abstractions.Observability;
using Highway.Server.Storage.Boxes;

namespace Highway.Server.Commands.Ported;

/// <summary>
/// HW.SEND &lt;queue&gt; &lt;messageId&gt; &lt;payload&gt; [AT &lt;ticks&gt;] → +OK (5.0; was HW.QSEND, feature 014).
///
/// <para>Enqueues onto the queue's box (065): immediately into ready, or — with <c>AT</c> in the future —
/// into the delayed space, promoted by the next claim after it falls due. Refuses rather than drops at
/// <c>MaxQueueBytes</c> (016 R4.1): the byte counter covers ready and delayed messages, so a queue full of
/// deferred work refuses too. A send never requires a running worker.</para>
/// </summary>
internal sealed class HwSendCommand : HighwayCommand
{
    private string _queue = null!;
    private string _messageId = null!;
    private byte[] _messageIdBytes = [];
    private byte[] _payloadBytes = [];
    private long _deliverAtTicks;
    private string? _refusedReason;

    protected override bool Parse(CommandContext ctx, CommandInput input)
    {
        var idx = 0;
        if (!TryReadIdentifier(input, ref idx, "queue", ctx.Options.MaxIdentifierBytes, out _queue))
            return false;
        if (!TryReadIdentifier(input, ref idx, "messageId", ctx.Options.MaxIdentifierBytes, out _messageId, out _messageIdBytes))
            return false;
        if (!TryReadPayload(input, ref idx, ctx.Options.MaxPayloadBytes, out _payloadBytes))
            return false;
        return TryReadDeliverAt(input, ref idx, out _deliverAtTicks);
    }

    /// <summary>The optional <c>AT &lt;absolute UTC ticks&gt;</c> suffix shared by HW.SEND and HW.PUBLISH.</summary>
    internal bool TryReadDeliverAt(CommandInput input, ref int idx, out long ticks)
    {
        ticks = 0;
        var keyword = input.Next(ref idx);
        if (keyword.Length == 0) return true;

        var word = Encoding.ASCII.GetString(keyword).ToUpperInvariant();
        if (word != "AT")
            return Fail(HighwayErrors.InvalidArg, $"unknown argument '{word}'; expected AT");

        var value = input.Next(ref idx);
        if (value.Length == 0 || !long.TryParse(Encoding.ASCII.GetString(value), out ticks) || ticks < 0)
            return Fail(HighwayErrors.InvalidArg, "AT requires a non-negative .NET UTC tick count");
        return true;
    }

    protected override void Run(CommandContext ctx, RespWriter writer)
    {
        var entry = Envelope.EncodeRpcEntry(_messageIdBytes, _payloadBytes);
        var box = BoxCommandSupport.Ensure(ctx, BoxKeys.Kind.Queue, _queue);
        using var _lock = BoxCommandSupport.Lock(ctx, box);
        using var wb = BoxCommandSupport.Open(ctx, box, BoxKeys.Kind.Queue);

        if (ctx.Options.MaxQueueBytes > 0)
        {
            var current = wb.Bytes;
            if (current + entry.Length > ctx.Options.MaxQueueBytes)
            {
                _refusedReason = $"queue '{_queue}' at {current}/{ctx.Options.MaxQueueBytes} bytes";
                writer.Error(HighwayErrors.Format(
                    HighwayErrors.QueueFull,
                    $"queue '{_queue}' is at its limit ({current} of {ctx.Options.MaxQueueBytes} bytes); " +
                    "the message was not stored"));
                return;
            }
        }

        if (_deliverAtTicks > ctx.NowTicks) wb.EnqueueDelayed(entry, _deliverAtTicks);
        else wb.Enqueue(entry);
        wb.Commit();
        writer.SimpleString("OK");
    }

    protected override void AfterCommit(CommandContext ctx)
    {
        if (_refusedReason is not null)
        {
            ctx.Recorder.Record(HighwayEventType.SendRefused, _queue, requestId: _messageId, errorCode: _refusedReason);
            ctx.Metrics?.RecordRefused();
            return;   // a refused send wrote nothing — no QueueSent record, no doorbell
        }

        ctx.Recorder.Record(HighwayEventType.QueueSent, _queue, requestId: _messageId, payload: _payloadBytes, errorCode: FailureCode);
        if (Failed) return;
        ctx.Metrics?.RecordPublished();

        // A deferred message is in nobody's reach yet; its promotion happens on a consumer's poll.
        if (_deliverAtTicks > ctx.NowTicks) return;
        ctx.Doorbell.Ring(HighwayKeys.QueueDoorbell(_queue), _messageIdBytes);
    }
}
