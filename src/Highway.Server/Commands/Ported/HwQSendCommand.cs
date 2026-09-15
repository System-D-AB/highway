using System.Text;
using Highway.Server.Commands.Runtime;
using Highway.Server.Internal;
using Highway.Abstractions.Observability;
using Highway.Server.Storage.Layout;

namespace Highway.Server.Commands.Ported;

/// <summary>
/// HW.QSEND &lt;queue&gt; &lt;messageId&gt; &lt;payload&gt; [AT &lt;ticks&gt;] → +OK (feature 014).
///
/// <para><b>The ported template (039 T2).</b> This is the shape every other command follows:
/// <c>Parse</c> reads and validates the arguments (no key pre-declaration — there is no
/// <c>Prepare</c>/lock-set to declare); <c>Run</c> takes the per-queue lock, opens one batch,
/// decides everything against the single pre-read clock, commits once; <c>AfterCommit</c> does
/// the recorder write and the doorbell ring under the same guards <c>Finalize</c> used.</para>
///
/// <para>Behaviour is identical to the Garnet version: same validation, same QUEUE_FULL
/// refusal, same delayed-vs-immediate routing, same +OK reply, same recorder events, same
/// doorbell. What changed is the model underneath — one <c>IHighwayStore</c> batch instead of
/// Garnet's Prepare/Main, and the clock read once (037 R5) instead of inside the transaction.</para>
/// </summary>
internal sealed class HwQSendCommand : HighwayCommand
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

        // Optional: AT <absolute delivery ticks>.
        var keyword = input.Next(ref idx);
        if (keyword.Length > 0)
        {
            var word = Encoding.ASCII.GetString(keyword).ToUpperInvariant();
            if (word != "AT")
                return Fail(HighwayErrors.InvalidArg, $"unknown argument '{word}'; expected AT");

            var value = input.Next(ref idx);
            if (value.Length == 0
                || !long.TryParse(Encoding.ASCII.GetString(value), out var ticks)
                || ticks < 0)
                return Fail(HighwayErrors.InvalidArg, "AT requires a non-negative .NET UTC tick count");

            _deliverAtTicks = ticks;
        }

        return true;
    }

    protected override void Run(CommandContext ctx, RespWriter writer)
    {
        var store = ctx.Store;
        var entry = Envelope.EncodeRpcEntry(_messageIdBytes, _payloadBytes);
        var counterName = HighwayNames.QueueBytes(_queue);

        // The per-queue lock: read-then-write atomicity a single batch cannot give
        // (the byte-limit check reads the counter, then the push writes it).
        using var _lock = ctx.Locks.Lock(_queue);
        using var snap = store.Snapshot();

        // Refuse rather than drop (016 R4.1): a queued message is one nobody has processed,
        // so discarding to make room loses exactly what the queue protects. Producer is told;
        // stored messages untouched.
        if (ctx.Options.MaxQueueBytes > 0)
        {
            var current = store.ReadByteCounter(snap, counterName);
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

        using var batch = store.NewBatch();
        store.AdjustByteCounter(batch, counterName, entry.Length);

        if (_deliverAtTicks > ctx.NowTicks)
        {
            // Deferred: into the delayed ordered set, score = absolute delivery ticks.
            store.SortedSetAdd(batch, HighwayKeyspace.SortedSetPrefix(HighwayNames.QueueDelayed(_queue)), _deliverAtTicks, entry);
        }
        else
        {
            // Immediate: tail-push onto the live queue (seq allocated in the same batch).
            PushTail(store, batch, HighwayNames.Queue(_queue), entry);
        }

        batch.Commit();
        writer.SimpleString("OK");
    }

    protected override void AfterCommit(CommandContext ctx)
    {
        if (_refusedReason is not null)
        {
            ctx.Recorder.Record(HighwayEventType.SendRefused, _queue, requestId: _messageId, errorCode: _refusedReason);
            // A refused send wrote nothing — no QueueSent record, no doorbell.
            return;
        }

        ctx.Recorder.Record(HighwayEventType.QueueSent, _queue, requestId: _messageId, payload: _payloadBytes, errorCode: FailureCode);

        if (Failed) return;

        // A deferred message is in nobody's live queue yet; ringing would wake workers to
        // find nothing. Its own promotion happens on a consumer's poll.
        if (_deliverAtTicks > ctx.NowTicks) return;

        ctx.Doorbell.Ring(HighwayKeys.QueueDoorbell(_queue), _messageIdBytes);
    }

    /// <summary>
    /// Tail-push onto a list: reserve the next high-water seq from the list's counter
    /// (in the same batch — the B1-safe allocation under the lock we hold) and write the
    /// entry at that seq (physical-layout §5, scheme 1).
    /// </summary>
    internal static void PushTail(Storage.IHighwayStore store, Storage.IStoreBatch batch, string listName, byte[] value)
    {
        var counter = HighwayKeyspace.Counter(HighwayNames.ListSequence(listName));
        var seq = store.Increment(batch, counter, 1) - 1; // 0-based tail seq
        store.ListRightPush(batch, HighwayKeyspace.ListEntry(listName, seq), value);
    }
}
