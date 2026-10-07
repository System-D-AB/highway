using System.Text;
using Highway.Server.Commands.Runtime;
using Highway.Server.Internal;
using Highway.Abstractions.Observability;
using Highway.Server.Storage.Layout;

namespace Highway.Server.Commands.Ported;

/// <summary>
/// HW.REPLY &lt;requestId&gt; &lt;payload&gt; → +OK; HW.REPLY &lt;requestId&gt; &lt;payload&gt; TOKEN &lt;token&gt; → :1 | :0.
///
/// <para><b>074:</b> with the claim's <c>TOKEN</c>, the broker checks the request's deadline: a reply after it is
/// discarded — its caller has stopped waiting — answering <c>:0</c> and recording <c>RpcReplyLate</c>; in time it is
/// written and answers <c>:1</c>. Without a token (raw tools) it is written as before.</para>
///
/// Writes the reply payload to the reply slot with an absolute expiry and rings the reply
/// doorbell. Last-writer-wins. Ported unchanged in behaviour; the TTL is now an absolute
/// tick count computed from the single pre-read clock (037 R5.1) rather than Garnet's
/// relative <c>SETEX</c>.
/// </summary>
internal sealed class HwReplyCommand : HighwayCommand
{
    /// <summary>Fixed recorder bucket — HW.REPLY carries no service/channel name (see the Garnet original).</summary>
    internal const string RecorderName = "hw.replies";

    private string _requestId = null!;
    private byte[] _requestIdBytes = [];
    private byte[] _payloadBytes = [];
    private bool _tokenGiven;
    private uint _box;
    private ulong _seq;
    private bool _late;

    protected override bool Parse(CommandContext ctx, CommandInput input)
    {
        var idx = 0;
        if (!TryReadIdentifier(input, ref idx, "requestId", ctx.Options.MaxIdentifierBytes, out _requestId, out _requestIdBytes))
            return false;
        if (!TryReadPayload(input, ref idx, ctx.Options.MaxPayloadBytes, out _payloadBytes))
            return false;
        var keyword = input.Next(ref idx);
        if (keyword.Length == 0) return true;
        if (!Encoding.ASCII.GetString(keyword).Equals("TOKEN", StringComparison.OrdinalIgnoreCase))
            return Fail(HighwayErrors.InvalidArg, "expected TOKEN <token>");
        if (!BoxCommandSupport.TryParseToken(input.Next(ref idx), out _box, out _seq))
            return Fail(HighwayErrors.InvalidToken, "TOKEN is not a claim token");
        _tokenGiven = true;
        return true;
    }

    protected override void Run(CommandContext ctx, RespWriter writer)
    {
        if (_tokenGiven && IsLate(ctx))
        {
            _late = true;
            ctx.Metrics?.RecordRpcReplyLate();
            writer.Integer(0);
            return;
        }

        var slotKey = HighwayKeyspace.Kv(HighwayNames.ReplySlot(_requestId));
        // Absolute expiry = now + TTL, computed from the single clock read (was Garnet SETEX ttl).
        var expiresAt = ctx.NowTicks + ctx.Options.ReplySlotTtl.Ticks;

        using var _lock = ctx.Locks.Lock(_requestId);
        using var batch = ctx.Store.NewBatch();
        ExpirySupport.SetExpiring(ctx, batch, slotKey, _payloadBytes, expiresAt);   // 065 T5: indexed, so it is deleted when it expires
        var floor = ExpirySupport.Sweep(ctx, batch);
        batch.Commit();
        ExpirySupport.AfterCommit(ctx, floor);

        if (_tokenGiven) writer.Integer(1);
        else writer.SimpleString("OK");
    }

    /// <summary>
    /// The reply is not wanted any more (074): the claimed request's deadline has passed, or the claim is no longer
    /// held — swept as expired, or redelivered to another worker whose answer will be the one that counts.
    /// </summary>
    private bool IsLate(CommandContext ctx)
    {
        using var batch = ctx.Store.NewBatch();
        var raw = ctx.Store.Read(batch, Storage.Boxes.BoxKeys.Seq(_box, Storage.Boxes.BoxKeys.InFlight, _seq));
        if (raw is null) return true;
        return Envelope.TryGetDeadline(Storage.Boxes.InFlightRecord.Decode(raw).Entry, out var deadline)
            && ctx.NowTicks > deadline;
    }

    protected override void AfterCommit(CommandContext ctx)
    {
        if (_late)
        {
            ctx.Recorder.Record(HighwayEventType.RpcReplyLate, RecorderName, requestId: _requestId);
            return;   // discarded: nothing written, nobody woken
        }
        ctx.Recorder.Record(
            HighwayEventType.RpcReplied, RecorderName,
            requestId: _requestId,
            payload: _payloadBytes,
            errorCode: FailureCode);

        if (Failed) return;

        ctx.Doorbell.Ring(HighwayKeys.ReplyDoorbell, _requestIdBytes);
    }
}
