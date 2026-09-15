using Highway.Server.Commands.Runtime;
using Highway.Server.Internal;
using Highway.Abstractions.Observability;
using Highway.Server.Storage.Layout;

namespace Highway.Server.Commands.Ported;

/// <summary>
/// HW.REPLY &lt;requestId&gt; &lt;payload&gt; → +OK.
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

    protected override bool Parse(CommandContext ctx, CommandInput input)
    {
        var idx = 0;
        if (!TryReadIdentifier(input, ref idx, "requestId", ctx.Options.MaxIdentifierBytes, out _requestId, out _requestIdBytes))
            return false;
        if (!TryReadPayload(input, ref idx, ctx.Options.MaxPayloadBytes, out _payloadBytes))
            return false;
        return true;
    }

    protected override void Run(CommandContext ctx, RespWriter writer)
    {
        var slotKey = HighwayKeyspace.Kv(HighwayNames.ReplySlot(_requestId));
        // Absolute expiry = now + TTL, computed from the single clock read (was Garnet SETEX ttl).
        var expiresAt = ctx.NowTicks + ctx.Options.ReplySlotTtl.Ticks;

        using var _lock = ctx.Locks.Lock(_requestId);
        using var batch = ctx.Store.NewBatch();
        ctx.Store.SetEx(batch, slotKey, _payloadBytes, expiresAt);
        batch.Commit();

        writer.SimpleString("OK");
    }

    protected override void AfterCommit(CommandContext ctx)
    {
        ctx.Recorder.Record(
            HighwayEventType.RpcReplied, RecorderName,
            requestId: _requestId,
            payload: _payloadBytes,
            errorCode: FailureCode);

        if (Failed) return;

        ctx.Doorbell.Ring(HighwayKeys.ReplyDoorbell, _requestIdBytes);
    }
}
