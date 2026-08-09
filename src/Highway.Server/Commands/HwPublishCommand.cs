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
/// HW.PUBLISH &lt;channel&gt; &lt;payload&gt; → :groupCount
///
/// Publishes a message to all subscriber groups atomically. **A publish with no
/// registered group is delivered to nobody** — Highway is not a store for messages
/// nobody has subscribed to; that is what <c>SendAsync</c> and a queue are for.
/// Each group's doorbell is rung in Finalize.
/// </summary>
internal sealed class HwPublishCommand : HighwayCommandBase
{
    private readonly HighwayServerOptions _opts;
    private readonly DoorbellBridge _doorbell;
    private readonly FlightRecorder _recorder;

    private string _channel = null!;
    private byte[] _payloadBytes = [];
    private string[] _groups = [];
    private long _messageId;

    public HwPublishCommand(HighwayServerOptions opts, DoorbellBridge doorbell, FlightRecorder recorder)
    {
        _opts     = opts;
        _doorbell = doorbell;
        _recorder = recorder;
    }

    /// <summary>
    /// Absolute delivery time in .NET UTC ticks, or 0 for immediate (feature 013).
    ///
    /// <para><b>Absolute, not a relative delay.</b> The client computes
    /// <c>UtcNow + delay</c> and the server stores what it was told, so a slow round trip
    /// cannot silently extend the delay — and, more importantly, AOF replay cannot
    /// re-delay from replay time. A stored relative delay would fabricate a new future on
    /// every recovery, the same way storing recorder events in the keyspace would have
    /// fabricated a new past (feature 002).</para>
    /// </summary>
    private long _deliverAtTicks;

    protected override void ResetState()
    {
        _groups = [];
        _messageId = 0;
        _payloadBytes = [];
        _deliverAtTicks = 0;
    }

    protected override bool PrepareCore<TGarnetReadApi>(TGarnetReadApi api, ref CustomProcedureInput procInput)
    {
        int idx = 0;
        if (!TryReadIdentifier(ref procInput, ref idx, "channel", _opts.MaxIdentifierBytes, out _channel))
            return true;
        if (!TryReadPayload(ref procInput, ref idx, _opts.MaxPayloadBytes, out _payloadBytes))
            return true;

        // Optional: AT <absolute delivery time in .NET UTC ticks>
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

            // A delivery time in the past is delivered immediately rather than rejected:
            // clock skew between a client and the broker is normal, and failing a publish
            // over a few milliseconds of it would be worse than delivering slightly early
            // relative to the client's clock.
            _deliverAtTicks = ticks;
        }

        // Read group membership from the main-store group list key.
        // We CANNOT use SetMembers here because GarnetWatchApi triggers a WATCH
        // on the key, and the subsequent exclusive lock on the same key causes
        // a Shared+Exclusive lock conflict that fails the transaction.
        var grpListKey = CreateArgSlice(HighwayKeys.ChannelGroupList(_channel));
        PinnedSpanByte grpListValue;
        api.GET(grpListKey, out grpListValue);
        _groups = grpListValue.Length > 0
            ? Encoding.UTF8.GetString(grpListValue.ReadOnlySpan).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            : [];

        // Lock sequence counter, group list (main store), backlog, and all group queues
        AddKey(CreateArgSlice(HighwayKeys.ChannelSeq(_channel)), LockType.Exclusive, StoreType.Main);
        AddKey(grpListKey, LockType.Exclusive, StoreType.Main);

        foreach (var group in _groups)
        {
            var derivedName = $"{_channel}@{group}";
            AddKey(CreateArgSlice(HighwayKeys.Queue(derivedName)), LockType.Exclusive, StoreType.Object);
            AddKey(CreateArgSlice(HighwayKeys.QueueDelayed(derivedName)), LockType.Exclusive, StoreType.Object);
        }

        return true;
    }

    public override void Main<TGarnetApi>(TGarnetApi api, ref CustomProcedureInput procInput, ref MemoryResult<byte> output)
    {
        if (TryWriteError(ref output)) return;

        try
        {
            // Increment sequence counter → unique monotonic message ID
            var seqKey = CreateArgSlice(HighwayKeys.ChannelSeq(_channel));
            api.Increment(seqKey, out _messageId);

            // The string form of the sequence counter is the message ID for the queue path.
            var messageIdStr = _messageId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var messageIdBytes = Encoding.UTF8.GetBytes(messageIdStr);

            if (_deliverAtTicks > DateTime.UtcNow.Ticks)
            {
                // Fan out into each group's derived queue delayed set.
                var rpcEntry = Envelope.EncodeRpcEntry(messageIdBytes, _payloadBytes);
                foreach (var group in _groups)
                {
                    var derivedName = $"{_channel}@{group}";
                    var derivedDelayedKey = CreateArgSlice(HighwayKeys.QueueDelayed(derivedName));
                    api.SortedSetAdd(
                        derivedDelayedKey,
                        CreateArgSlice(Encoding.ASCII.GetBytes(_deliverAtTicks.ToString())),
                        CreateArgSlice(rpcEntry),
                        out _);
                }

                // Zero groups notified now. The reply counts delivery, and nothing has
                // been delivered yet.
                WriteInt64(ref output, 0L);
            }
            else if (_groups.Length == 0)
            {
                // Nobody is subscribed, so the message is delivered to nobody. That is what
                // "publish" means (feature 014 follow-up).
                WriteInt64(ref output, 0L);
            }
            else
            {
                // Fan out to derived queue keys using RPC framing.
                var rpcEntry = Envelope.EncodeRpcEntry(messageIdBytes, _payloadBytes);
                foreach (var group in _groups)
                {
                    var derivedName = $"{_channel}@{group}";
                    var derivedQueueKey = CreateArgSlice(HighwayKeys.Queue(derivedName));
                    api.ListRightPush(derivedQueueKey, CreateArgSlice(rpcEntry), out _);
                }

                WriteInt64(ref output, _groups.Length);
            }
        }
        catch (Exception ex)
        {
            WriteError(ref output, HighwayErrors.InternalError(ex.Message));
        }
    }

    public override void Finalize<TGarnetApi>(TGarnetApi api, ref CustomProcedureInput procInput, ref MemoryResult<byte> output)
    {
        if (Failed) return; // a rejected command must never ring a doorbell
        _recorder.Record(
            HighwayEventType.Published, _channel ?? "?",
            messageId: _messageId == 0 ? null : _messageId,
            payload: _payloadBytes,
            errorCode: FailureCode,
            count: _groups.Length);

        if (Failed) return;

        // A delayed message is in nobody's queue yet, so ringing would wake every
        // consumer to find nothing. Promotion happens on the consumer's own backstop
        // poll instead — see HW.RECEIVE / HW.QCLAIM.
        if (_deliverAtTicks > DateTime.UtcNow.Ticks) return;

        var msgIdBytes = Encoding.UTF8.GetBytes(_messageId.ToString());

        // _channel is non-null past the guard: the only way it stays unset is
        // TryReadIdentifier failing, which calls Fail() and so returns above.
        foreach (var group in _groups)
        {
            var derivedName = $"{_channel}@{group}";
            _doorbell.Ring(HighwayKeys.QueueDoorbell(derivedName), msgIdBytes);
        }
    }


    /// <summary>Writes a RESP integer (:N\r\n) to the output.</summary>
    private static void WriteInt64(ref MemoryResult<byte> output, long value)
    {
        var digits = CountDigits(value);
        var sign   = value < 0 ? 1 : 0;
        var len    = 1 + sign + digits + 2;

        output.MemoryOwner?.Dispose();
        output.MemoryOwner = MemoryPool<byte>.Shared.Rent(len);
        output.Length = len;
        var span = output.MemoryOwner.Memory.Span;

        var pos = 0;
        span[pos++] = (byte)':';
        if (value < 0)
        {
            span[pos++] = (byte)'-';
            value = -value;
        }

        // Write digits
        var digitStart = pos;
        var tmp = value;
        for (var i = digits - 1; i >= 0; i--)
        {
            span[digitStart + i] = (byte)('0' + tmp % 10);
            tmp /= 10;
        }
        pos += digits;
        span[pos++] = (byte)'\r';
        span[pos]   = (byte)'\n';
    }

    private static int CountDigits(long value)
    {
        if (value == long.MinValue) return 19;
        if (value < 0) value = -value;
        if (value < 10L) return 1;
        if (value < 100L) return 2;
        if (value < 1_000L) return 3;
        if (value < 10_000L) return 4;
        if (value < 100_000L) return 5;
        if (value < 1_000_000L) return 6;
        if (value < 10_000_000L) return 7;
        if (value < 100_000_000L) return 8;
        if (value < 1_000_000_000L) return 9;
        if (value < 10_000_000_000L) return 10;
        if (value < 100_000_000_000L) return 11;
        if (value < 1_000_000_000_000L) return 12;
        if (value < 10_000_000_000_000L) return 13;
        if (value < 100_000_000_000_000L) return 14;
        if (value < 1_000_000_000_000_000L) return 15;
        if (value < 10_000_000_000_000_000L) return 16;
        if (value < 100_000_000_000_000_000L) return 17;
        if (value < 1_000_000_000_000_000_000L) return 18;
        return 19;
    }
}
