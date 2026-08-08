using System.Buffers;
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
/// HW.RECEIVE &lt;channel&gt; &lt;group&gt; [COUNT n] → [[messageId, payload], ...]
///
/// Pops up to COUNT messages from the group queue and moves each to the
/// processing list with a receive timestamp. Performs a lazy lease sweep first.
/// Returns an empty array when nothing is available.
/// </summary>
internal sealed class HwReceiveCommand : HighwayCommandBase
{
    private readonly HighwayServerOptions _opts;
    private readonly FlightRecorder _recorder;

    private string _channel = null!;
    private string _group = null!;
    private int _count;
    private int _receivedCount;

    /// <summary>
    /// Messages dead-lettered by this invocation's sweep, recorded in Finalize.
    /// A dead letter nobody can see is the old infinite-redelivery bug with a quieter
    /// failure mode.
    /// </summary>
    private readonly List<(long MessageId, ushort Attempts)> _deadLettered = [];

    public HwReceiveCommand(HighwayServerOptions opts, FlightRecorder recorder)
    {
        _opts = opts;
        _recorder = recorder;
    }

    protected override void ResetState()
    {
        _receivedCount = 0;
        _count = 0;
        _deadLettered.Clear();
        ResetDeadLetterCounters();
        _allGroups = [];
        _promoted = 0;
    }

    protected override bool PrepareCore<TGarnetReadApi>(TGarnetReadApi api, ref CustomProcedureInput procInput)
    {
        int idx = 0;
        if (!TryReadIdentifier(ref procInput, ref idx, "channel", _opts.MaxIdentifierBytes, out _channel))
            return true;
        if (!TryReadIdentifier(ref procInput, ref idx, "group", _opts.MaxIdentifierBytes, out _group))
            return true;

        _count = _opts.ReceiveDefaultCount;

        // Optional COUNT argument — may be "COUNT n" or just "n"
        var arg3 = GetNextArg(ref procInput, ref idx);
        if (arg3.Length > 0)
        {
            var span3 = arg3.ReadOnlySpan;
            if (IsCountKeyword(span3))
            {
                // "COUNT" keyword — next arg is the value
                var countValueArg = GetNextArg(ref procInput, ref idx);
                if (!TryReadCountValue(countValueArg.ReadOnlySpan))
                    return true;
            }
            else if (!TryReadCountValue(span3))
            {
                return true;
            }
        }

        if (_count > _opts.ReceiveMaxCount)
        {
            Fail(HighwayErrors.InvalidCount, $"COUNT {_count} exceeds maximum {_opts.ReceiveMaxCount}");
            return true;
        }

        AddKey(CreateArgSlice(HighwayKeys.GroupQueue(_channel, _group)), LockType.Exclusive, StoreType.Object);
        AddKey(CreateArgSlice(HighwayKeys.GroupProcessing(_channel, _group)), LockType.Exclusive, StoreType.Object);

        // Written by the lease sweep in Main (feature 013). Name-only derivation, so no
        // Prepare-phase read and therefore no watch conflict (004.1).
        AddKey(CreateArgSlice(HighwayKeys.GroupDeadLetter(_channel, _group)), LockType.Exclusive, StoreType.Object);

        // Delayed delivery (feature 013). Promotion fans a due message out to *every*
        // registered group, not just this one, so the group list and every group queue
        // must be locked here.
        //
        // The group list is read from the main-store mirror, never from the object-store
        // set: an object-store read in Prepare registers a watch that the exclusive lock
        // below would then fail (004.1). The delayed key itself is derived from the
        // channel name alone and is never read here — only in Main, under the lock.
        var grpListKey = CreateArgSlice(HighwayKeys.ChannelGroupList(_channel));
        api.GET(grpListKey, out PinnedSpanByte grpListValue);
        _allGroups = SplitList(grpListValue);

        AddKey(grpListKey, LockType.Exclusive, StoreType.Main);
        AddKey(CreateArgSlice(HighwayKeys.ChannelDelayed(_channel)), LockType.Exclusive, StoreType.Object);
        AddKey(CreateArgSlice(HighwayKeys.GroupRetry(_channel, _group)), LockType.Exclusive, StoreType.Object);
        foreach (var other in _allGroups)
        {
            if (other == _group) continue;   // already locked above
            AddKey(CreateArgSlice(HighwayKeys.GroupQueue(_channel, other)), LockType.Exclusive, StoreType.Object);
        }

        return true;
    }

    /// <summary>
    /// Every group registered on this channel, read from the main-store mirror in
    /// Prepare. Promotion delivers to all of them, so all of them are locked.
    /// </summary>
    private string[] _allGroups = [];

    /// <summary>
    /// Most due messages promoted in one receive.
    ///
    /// <para>Bounded so a channel that accumulated a large delayed batch cannot turn a
    /// single consumer poll into an unboundedly long transaction holding locks on every
    /// group queue. The remainder promotes on the next poll, which is at most one backstop
    /// interval away.</para>
    /// </summary>
    private const int MaxPromotionBatch = 256;

    /// <summary>
    /// Moves messages whose delivery time has passed out of the delayed set and into
    /// every registered group queue.
    ///
    /// <para><b>Driven by consumer activity, not a timer.</b> Highway already recovers
    /// abandoned work lazily, in exactly this shape, and a background timer that writes to
    /// the keyspace would be a new class of thing: its own transaction, its own failure
    /// handling, its own interaction with AOF replay, running whether or not anyone is
    /// listening. The cost is that a delay is a <b>"not before"</b> rather than an alarm
    /// clock — resolution is bounded by the consumer's backstop interval, and a channel
    /// whose groups have no running consumer promotes nothing until one starts.</para>
    /// </summary>
    private void PromoteDueMessages<TGarnetApi>(TGarnetApi api)
        where TGarnetApi : IGarnetApi
    {
        // Nothing registered means nothing to promote *to*. Skipping leaves the messages
        // in the delayed set for whenever a group appears — promoting them into no queue
        // at all would silently destroy them.
        if (_allGroups.Length == 0) return;

        var delayedKey = CreateArgSlice(HighwayKeys.ChannelDelayed(_channel));
        var now = DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture);

        // Range-read then remove, rather than pop-and-restore-the-undue. Popping first
        // means every message not yet due has to be written back, and anything that goes
        // wrong between the two loses it — which is exactly what happened in development:
        // a due message was popped, pushed nowhere, and vanished from both the set and
        // the queue.
        //
        // Reading by score also removes any need to parse a score back. Garnet stores
        // scores as doubles and formats them with the *current culture*, so a tick count
        // comes back as "6,392172787835095E+17" on a European machine — which no
        // long.TryParse and no invariant double.TryParse will accept.
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
            foreach (var group in _allGroups)
                api.ListRightPush(CreateArgSlice(HighwayKeys.GroupQueue(_channel, group)), CreateArgSlice(entry), out _);

            // Removed only after it is in every queue, and in the same transaction, so
            // there is no state in which it exists in neither.
            api.SortedSetRemove(delayedKey, CreateArgSlice(entry), out _);
            _promoted++;
        }
    }

    /// <summary>
    /// Returns messages whose retry backoff has elapsed to this group's own queue.
    ///
    /// <para>Deliberately separate from <see cref="PromoteDueMessages"/>: that one fans a
    /// delayed <i>publish</i> out to every registered group, which is correct for a
    /// publish and would be a duplicate for a retry. A retry belongs to exactly the group
    /// that failed it.</para>
    /// </summary>
    private void PromoteDueRetries<TGarnetApi>(TGarnetApi api)
        where TGarnetApi : IGarnetApi
    {
        var retryKey = CreateArgSlice(HighwayKeys.GroupRetry(_channel, _group));
        var now = DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture);

        var status = api.SortedSetRange(
            retryKey,
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

        var groupQueueKey = CreateArgSlice(HighwayKeys.GroupQueue(_channel, _group));
        foreach (var member in due)
        {
            var entry = member.ReadOnlySpan.ToArray();

            // Head, not tail: a retry is older than anything queued behind it, and the
            // non-backoff path pushes to the head for the same reason.
            api.ListLeftPush(groupQueueKey, CreateArgSlice(entry), out _);
            api.SortedSetRemove(retryKey, CreateArgSlice(entry), out _);
        }
    }

    /// <summary>Messages promoted by this invocation, recorded in Finalize.</summary>
    private int _promoted;

    /// <summary>
    /// Parses the COUNT value, capturing a distinct <c>HW_INVALID_COUNT</c>
    /// detail for every failure class: missing, negative, non-numeric,
    /// overflow, and zero. (The above-max check runs in Prepare after parsing.)
    /// Overflow-safe: accumulates in <see cref="long"/> and rejects before wrap.
    /// </summary>
    private bool TryReadCountValue(ReadOnlySpan<byte> span)
    {
        if (span.IsEmpty)
            return Fail(HighwayErrors.InvalidCount, "COUNT value is missing");

        if (span[0] == (byte)'-')
            return Fail(HighwayErrors.InvalidCount, "COUNT must not be negative");

        long value = 0;
        foreach (var b in span)
        {
            if (b < '0' || b > '9')
                return Fail(HighwayErrors.InvalidCount,
                    $"COUNT value '{Encoding.UTF8.GetString(span)}' is not numeric");

            // Reject before the multiplication/addition can wrap
            if (value > (long.MaxValue - (b - '0')) / 10)
                return Fail(HighwayErrors.InvalidCount, "COUNT overflows a 32-bit integer");

            value = value * 10 + (b - '0');
        }

        if (value == 0)
            return Fail(HighwayErrors.InvalidCount, "COUNT must be at least 1");

        if (value > int.MaxValue)
            return Fail(HighwayErrors.InvalidCount, "COUNT overflows a 32-bit integer");

        _count = (int)value;
        return true;
    }

    public override void Main<TGarnetApi>(TGarnetApi api, ref CustomProcedureInput procInput, ref MemoryResult<byte> output)
    {
        if (TryWriteError(ref output)) return;

        try
        {
            var groupQueueKey = CreateArgSlice(HighwayKeys.GroupQueue(_channel, _group));
            var groupProcKey  = CreateArgSlice(HighwayKeys.GroupProcessing(_channel, _group));

            // Delayed messages whose time has come join the queue before it is served, so
            // a consumer polling at the right moment sees them on this call rather than
            // the next (feature 013).
            PromoteDueMessages(api);
            PromoteDueRetries(api);

            // Lazy lease sweep: expired processing entries → re-queue at HEAD
            if (_opts.Lease > TimeSpan.Zero)
            {
                var leaseExpiry = DateTime.UtcNow.Ticks - _opts.Lease.Ticks;
                var sweepStatus = api.ListLeftPop(groupProcKey, int.MaxValue, out var procEntries);
                if (sweepStatus == GarnetStatus.OK && procEntries is { Length: > 0 })
                {
                    var expired = new List<byte[]>();
                    var keep    = new List<byte[]>();

                    foreach (var entry in procEntries)
                    {
                        var span = entry.ReadOnlySpan;

                        // Refuse a pre-013 entry rather than misparsing it into a
                        // corrupt payload (feature 013).
                        if (Envelope.IsLegacyEntry(span))
                            throw new StorageFormatException(HighwayKeys.GroupProcessing(_channel, _group));

                        Envelope.DecodeGroupProcessingEntry(span, out var receiveTicks, out _, out _, out _);
                        if (receiveTicks < leaseExpiry)
                            expired.Add(span.ToArray());
                        else
                            keep.Add(span.ToArray());
                    }

                    // Restore non-expired to proc list
                    foreach (var e in keep)
                        api.ListRightPush(groupProcKey, CreateArgSlice(e), out _);

                    // Re-queue expired at HEAD (reversed to preserve order), unless the
                    // message has exhausted its attempts — the same unbounded-redelivery
                    // bug lived here as in HW.DEQUEUE's lease sweep.
                    for (var i = expired.Count - 1; i >= 0; i--)
                    {
                        Envelope.DecodeGroupProcessingEntry(
                            expired[i], out _, out var msgId, out var msgPayload, out var attempts);

                        var next = Envelope.NextAttempt(attempts);

                        if (_opts.MaxDeliveryAttempts > 0 && next > _opts.MaxDeliveryAttempts)
                        {
                            var original = Envelope.EncodeChannelEntry(msgId, msgPayload, next);
                            var dead = DeadLetter.Encode(
                                DateTime.UtcNow.Ticks, next, DeadLetter.MaxAttempts, original);

                            var dlqKey = CreateArgSlice(HighwayKeys.GroupDeadLetter(_channel, _group));
                            api.ListRightPush(dlqKey, CreateArgSlice(dead), out _);
                            TrimDeadLetters(api, dlqKey, _opts.MaxDeadLetterEntries);
                            _deadLettered.Add((msgId, next));
                            continue;
                        }

                        var channelEntry = Envelope.EncodeChannelEntry(msgId, msgPayload, next);

                        if (_opts.PubSubBackoffEnabled)
                        {
                            // Backoff reuses delayed delivery rather than inventing a second
                            // mechanism: the entry goes into the delayed set with a future
                            // score and is promoted back by a later HW.RECEIVE.
                            //
                            // A subscriber that failed once usually fails again immediately,
                            // and a tight redelivery loop wastes the consumer it is trying
                            // to reach.
                            var readyAt = DateTime.UtcNow + RetryBackoff.For(next, _opts.MaxBackoff);
                            api.SortedSetAdd(
                                CreateArgSlice(HighwayKeys.GroupRetry(_channel, _group)),
                                CreateArgSlice(Encoding.ASCII.GetBytes(
                                    readyAt.Ticks.ToString(CultureInfo.InvariantCulture))),
                                CreateArgSlice(channelEntry),
                                out _);
                            continue;
                        }

                        api.ListLeftPush(groupQueueKey, CreateArgSlice(channelEntry), out _);
                    }
                }
            }

            // Pop up to _count messages from the group queue
            var results = new List<PinnedSpanByte[]>();

            for (var i = 0; i < _count; i++)
            {
                var popStatus = api.ListLeftPop(groupQueueKey, out var popped);
                if (popStatus != GarnetStatus.OK || popped.Length == 0) break;

                if (Envelope.IsLegacyEntry(popped.ReadOnlySpan))
                    throw new StorageFormatException(HighwayKeys.GroupQueue(_channel, _group));

                Envelope.DecodeChannelEntry(
                    popped.ReadOnlySpan, out var messageId, out var msgPayload, out var msgAttempts);

                // The attempt count travels with the receive; resetting it here would make
                // the limit unreachable, because every redelivery starts a fresh receive.
                var procEntry = Envelope.CarryFailureBlock(
                    popped.ReadOnlySpan,
                    Envelope.EncodeGroupProcessingEntry(
                        DateTime.UtcNow.Ticks, messageId, msgPayload, msgAttempts));
                api.ListRightPush(groupProcKey, CreateArgSlice(procEntry), out _);

                var msgIdSlice = CreateArgSlice(Encoding.UTF8.GetBytes(messageId.ToString()));
                var paySlice   = CreateArgSlice(msgPayload);
                results.Add([msgIdSlice, paySlice]);
            }

            _receivedCount = results.Count;
            WriteMessageArray(ref output, results);
        }
        catch (Exception ex)
        {
            WriteError(ref output, HighwayErrors.InternalError(ex.Message));
        }
    }

    /// <summary>Writes a RESP array of 2-element bulk-string sub-arrays.</summary>
    private static unsafe void WriteMessageArray(ref MemoryResult<byte> output, List<PinnedSpanByte[]> pairs)
    {
        var totalLen = ArrayHeaderLen(pairs.Count);
        foreach (var pair in pairs)
        {
            totalLen += ArrayHeaderLen(2);
            totalLen += BulkStringLen(pair[0].Length);
            totalLen += BulkStringLen(pair[1].Length);
        }

        output.MemoryOwner?.Dispose();
        output.MemoryOwner = MemoryPool<byte>.Shared.Rent(totalLen);
        output.Length = totalLen;

        fixed (byte* ptr = output.MemoryOwner.Memory.Span)
        {
            var curr = ptr;
            var end  = ptr + totalLen;
            RespWriteUtils.TryWriteArrayLength(pairs.Count, ref curr, end);
            foreach (var pair in pairs)
            {
                RespWriteUtils.TryWriteArrayLength(2, ref curr, end);
                RespWriteUtils.TryWriteBulkString(pair[0].Span, ref curr, end);
                RespWriteUtils.TryWriteBulkString(pair[1].Span, ref curr, end);
            }
        }
    }

    private static int ArrayHeaderLen(int count) => 1 + CountDigits(count) + 2;
    private static int BulkStringLen(int len)    => 1 + CountDigits(len) + 2 + len + 2;

    private static int CountDigits(int value)
    {
        if (value < 0) value = -value;
        if (value < 10) return 1;
        if (value < 100) return 2;
        if (value < 1_000) return 3;
        if (value < 10_000) return 4;
        if (value < 100_000) return 5;
        if (value < 1_000_000) return 6;
        if (value < 10_000_000) return 7;
        if (value < 100_000_000) return 8;
        if (value < 1_000_000_000) return 9;
        return 10;
    }

    private static bool IsCountKeyword(ReadOnlySpan<byte> span)
        => span.Length == 5
           && (span[0] == 'C' || span[0] == 'c')
           && (span[1] == 'O' || span[1] == 'o')
           && (span[2] == 'U' || span[2] == 'u')
           && (span[3] == 'N' || span[3] == 'n')
           && (span[4] == 'T' || span[4] == 't');

    /// <summary>One event per batch, not per message: a batch of 500 is one operation.</summary>
    public override void Finalize<TGarnetApi>(TGarnetApi api, ref CustomProcedureInput procInput, ref MemoryResult<byte> output)
    {
        // Recorded even when the receive returned nothing: the sweep that produced them
        // ran regardless of whether there were messages to serve.
        foreach (var (messageId, attempts) in _deadLettered)
        {
            _recorder.Record(
                HighwayEventType.MessageDeadLettered, _channel ?? "?",
                messageId: messageId,
                count: attempts,
                errorCode: DeadLetter.MaxAttempts);
        }

        if (!Failed && _receivedCount == 0) return;
        _recorder.Record(
            HighwayEventType.MessagesReceived, _channel ?? "?",
            nodeId: _group,
            errorCode: FailureCode,
            count: _receivedCount);
    }
}
