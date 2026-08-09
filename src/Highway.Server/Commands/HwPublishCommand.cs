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

    /// <summary>Groups whose owning node has been absent past the retirement threshold (017).</summary>
    private string[] _deadGroups = [];

    // What this publish retired, so Finalize can log it loudly. Retirement is the largest
    // single loss Highway can inflict, and C4.3 — a loss is never silent — applies most here.
    private int _retiredGroups;
    private long _retiredMessages;
    private long _retiredBytes;

    /// <summary>Groups past HALF the threshold: not retired, but heading that way (017).</summary>
    private string[] _suspectGroups = [];
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
        _deadGroups = [];
        _suspectGroups = [];
        _retiredGroups = 0;
        _retiredMessages = 0;
        _retiredBytes = 0;
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
            AddKey(CreateArgSlice(HighwayKeys.QueueBytes(derivedName)), LockType.Exclusive, StoreType.Main);
            AddKey(CreateArgSlice(HighwayKeys.QueueDeadLetter(derivedName)), LockType.Exclusive, StoreType.Object);
            AddKey(CreateArgSlice(HighwayKeys.QueueNodes(derivedName)), LockType.Exclusive, StoreType.Object);
            AddKey(CreateArgSlice(HighwayKeys.QueueNodeList(derivedName)), LockType.Exclusive, StoreType.Main);
            AddKey(CreateArgSlice(HighwayKeys.QueueProcessing(derivedName, group)), LockType.Exclusive, StoreType.Object);
            AddKey(CreateArgSlice(HighwayKeys.NodeChannels(group)), LockType.Exclusive, StoreType.Main);
        }

        // Automatic retirement (017) rides here rather than on a timer. A publish already reads
        // this channel's group list and already locks every group's queue, so the check costs one
        // main-store GET per group on a path that is about to do N pushes anyway — and the
        // publish that WOULD be blocked by a dead subscriber is the one that clears it.
        //
        // Liveness evidence, not a consumption gap: a group nobody has consumed from is not
        // dead, but a group whose node has stopped heartbeating is. RabbitMQ's x-expires and
        // Azure's AutoDeleteOnIdle cannot tell those apart; Highway can, because a group IS a
        // node (018).
        if (_opts.SubscriberRetirementThreshold > TimeSpan.Zero && _groups.Length > 0)
        {
            // Retirement unregisters the group, which means touching the object-store SET that
            // a publish has never needed before. Undeclared keys are rejected in Main, not here
            // — the wall 013, 014, 015 and now 017 have each met.
            AddKey(CreateArgSlice(HighwayKeys.ChannelGroups(_channel)), LockType.Exclusive, StoreType.Object);

            var now = DateTime.UtcNow.Ticks;
            var dead = new List<string>();
            var suspect = new List<string>();
            var halfThreshold = _opts.SubscriberRetirementThreshold / 2;

            foreach (var group in _groups)
            {
                var regKey = CreateArgSlice(HighwayKeys.RegistrationNode(group));
                AddKey(regKey, LockType.Exclusive, StoreType.Main);

                api.GET(regKey, out PinnedSpanByte record);

                // No registration at all is NOT evidence of death: a subscriber that never
                // registered a catalog still has a group. Only a record that exists and has
                // gone stale proves the node was here and stopped.
                if (record.Length >= NodeRegistration.HeaderSize
                    && NodeRegistration.IsStale(record.ReadOnlySpan, now, _opts.SubscriberRetirementThreshold))
                {
                    dead.Add(group);
                }
                else if (record.Length >= NodeRegistration.HeaderSize
                         && NodeRegistration.IsStale(record.ReadOnlySpan, now, halfThreshold))
                {
                    // Past half the threshold: still alive as far as this feature is concerned,
                    // but worth seeing in a replay before its backlog disappears.
                    suspect.Add(group);
                }
            }

            _deadGroups = [.. dead];
            _suspectGroups = [.. suspect];
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

                // Retire first, so a dead subscriber cannot fail the limit check below for the
                // living ones. This is the self-healing step: the publish that would have been
                // refused clears the group that would have refused it.
                foreach (var group in _deadGroups)
                {
                    var destroyed = RetireGroup(api, _channel, group);
                    _retiredGroups++;
                    _retiredMessages += destroyed.Messages;
                    _retiredBytes += destroyed.Bytes;
                }

                if (_deadGroups.Length > 0)
                    _groups = [.. _groups.Where(g => !_deadGroups.Contains(g))];

                // Check EVERY group before writing ANY of them (016 T10). Fan-out is atomic —
                // 018 guarantees a publish reaches every registered group or none — so a full
                // group queue has to fail the whole publish rather than deliver a partial one.
                //
                // The accepted cost is that one stuck subscriber blocks the channel for the
                // healthy ones. The mitigation is not to hide it but to make it attributable:
                // the error names the offending group, so an operator fixes a subscriber
                // instead of debugging a channel.
                if (_opts.MaxQueueBytes > 0)
                {
                    foreach (var group in _groups)
                    {
                        var name = $"{_channel}@{group}";
                        var used = ReadByteCounter(api, HighwayKeys.QueueBytes(name));

                        if (used + rpcEntry.Length > _opts.MaxQueueBytes)
                        {
                            WriteError(ref output, HighwayErrors.Format(
                                HighwayErrors.QueueFull,
                                $"channel '{_channel}' refused: group '{group}' is at its limit " +
                                $"({used} of {_opts.MaxQueueBytes} bytes). No group received this " +
                                "message - a publish reaches every registered group or none."));
                            return;
                        }
                    }
                }

                foreach (var group in _groups)
                {
                    var derivedName = $"{_channel}@{group}";
                    var derivedQueueKey = CreateArgSlice(HighwayKeys.Queue(derivedName));
                    api.ListRightPush(derivedQueueKey, CreateArgSlice(rpcEntry), out _);
                    AdjustByteCounter(api, HighwayKeys.QueueBytes(derivedName), rpcEntry.Length);
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

        // Loud, and in the replay (017 T7). An operator who later asks "where did my
        // subscriber's backlog go?" must be able to answer it without guessing.
        foreach (var group in _suspectGroups)
        {
            _recorder.Record(
                HighwayEventType.NodeSuspect, _channel ?? "?",
                nodeId: group,
                errorCode: $"node '{group}' has been absent past half the retirement threshold; " +
                           "its subscriber queue will be destroyed if it does not return");
        }

        if (_retiredGroups > 0)
        {
            _recorder.Record(
                HighwayEventType.GroupRetired, _channel ?? "?",
                count: (int)_retiredMessages,
                errorCode: $"retired {_retiredGroups} group(s), discarded {_retiredMessages} message(s) / {_retiredBytes} byte(s)");
        }

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
