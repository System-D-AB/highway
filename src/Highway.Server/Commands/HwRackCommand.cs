using Garnet.common;
using Garnet.server;
using Highway.Server.Internal;
using Highway.Server.Observability;
using Highway.Abstractions.Observability;
using Tsavorite.core;

namespace Highway.Server.Commands;

/// <summary>
/// HW.RACK &lt;channel&gt; &lt;group&gt; &lt;messageId&gt; → +OK
///
/// Removes the entry with the given messageId from the group's processing list.
/// Idempotent. One group's ack never touches another group's copy.
/// </summary>
internal sealed class HwRackCommand : HighwayCommandBase
{
    private readonly HighwayServerOptions _opts;
    private readonly FlightRecorder _recorder;

    private string _channel = null!;
    private string _group = null!;
    private long _messageId;

    public HwRackCommand(HighwayServerOptions opts, FlightRecorder recorder)
    {
        _opts = opts;
        _recorder = recorder;
    }

    protected override bool PrepareCore<TGarnetReadApi>(TGarnetReadApi api, ref CustomProcedureInput procInput)
    {
        int idx = 0;
        if (!TryReadIdentifier(ref procInput, ref idx, "channel", _opts.MaxIdentifierBytes, out _channel))
            return true;
        if (!TryReadIdentifier(ref procInput, ref idx, "group", _opts.MaxIdentifierBytes, out _group))
            return true;
        if (!TryReadIdentifier(ref procInput, ref idx, "messageId", _opts.MaxIdentifierBytes, out var messageIdText))
            return true;
        if (!TryParseMessageId(messageIdText))
            return true;

        AddKey(CreateArgSlice(HighwayKeys.GroupProcessing(_channel, _group)), LockType.Exclusive, StoreType.Object);
        return true;
    }

    /// <summary>
    /// Parses the message ID as a positive <see cref="long"/>, rejecting
    /// negatives, non-numeric values, and overflow (the previous parser wrapped).
    /// Message IDs are assigned by the server's per-channel sequence counter and
    /// are always positive.
    /// </summary>
    private bool TryParseMessageId(string text)
    {
        var span = System.Text.Encoding.UTF8.GetBytes(text).AsSpan();

        if (span.Length > 0 && span[0] == (byte)'-')
            return Fail(HighwayErrors.InvalidArg, "messageId must be a positive integer");

        long value = 0;
        foreach (var b in span)
        {
            if (b < '0' || b > '9')
                return Fail(HighwayErrors.InvalidArg, $"messageId '{text}' is not a valid message ID");

            if (value > (long.MaxValue - (b - '0')) / 10)
                return Fail(HighwayErrors.InvalidArg, $"messageId '{text}' overflows a 64-bit integer");

            value = value * 10 + (b - '0');
        }

        if (value == 0)
            return Fail(HighwayErrors.InvalidArg, "messageId must be a positive integer");

        _messageId = value;
        return true;
    }

    public override void Main<TGarnetApi>(TGarnetApi api, ref CustomProcedureInput procInput, ref MemoryResult<byte> output)
    {
        if (TryWriteError(ref output)) return;

        try
        {
            var groupProcKey = CreateArgSlice(HighwayKeys.GroupProcessing(_channel, _group));

            var status = api.ListLeftPop(groupProcKey, int.MaxValue, out var entries);
            if (status != GarnetStatus.OK || entries is null || entries.Length == 0)
            {
                WriteSimpleString(ref output, "OK");
                return;
            }

            bool found = false;
            foreach (var entry in entries)
            {
                var span = entry.ReadOnlySpan;
                if (!found && !Envelope.IsLegacyEntry(span))
                {
                    Envelope.DecodeGroupProcessingEntry(span, out _, out var msgId, out _, out _);
                    if (msgId == _messageId)
                    {
                        found = true;
                        continue; // remove this entry
                    }
                }
                api.ListRightPush(groupProcKey, CreateArgSlice(span.ToArray()), out _);
            }

            WriteSimpleString(ref output, "OK");
        }
        catch (Exception ex)
        {
            WriteError(ref output, HighwayErrors.InternalError(ex.Message));
        }
    }

    public override void Finalize<TGarnetApi>(TGarnetApi api, ref CustomProcedureInput procInput, ref MemoryResult<byte> output)
        => _recorder.Record(
            HighwayEventType.MessageAcknowledged, _channel ?? "?",
            nodeId: _group,
            messageId: _messageId,
            errorCode: FailureCode);
}
