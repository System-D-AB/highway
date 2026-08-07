using Garnet.common;
using Garnet.server;
using Highway.Server.Internal;
using Tsavorite.core;

namespace Highway.Server.Commands;

/// <summary>
/// HW.REPLY &lt;requestId&gt; &lt;payload&gt; → +OK
///
/// Writes the reply payload to the reply slot with a TTL and rings the
/// reply doorbell. Last-writer-wins semantics.
/// </summary>
internal sealed class HwReplyCommand : HighwayCommandBase
{
    private readonly HighwayServerOptions _opts;
    private readonly DoorbellBridge _doorbell;

    private string _requestId = null!;
    private byte[] _requestIdBytes = [];
    private byte[] _payloadBytes = [];

    public HwReplyCommand(HighwayServerOptions opts, DoorbellBridge doorbell)
    {
        _opts     = opts;
        _doorbell = doorbell;
    }

    public override bool Prepare<TGarnetReadApi>(TGarnetReadApi api, ref CustomProcedureInput procInput)
    {
        int idx = 0;
        if (!TryReadIdentifier(ref procInput, ref idx, "requestId", _opts.MaxIdentifierBytes, out _requestId, out _requestIdBytes))
            return true;
        if (!TryReadPayload(ref procInput, ref idx, _opts.MaxPayloadBytes, out _payloadBytes))
            return true;

        AddKey(CreateArgSlice(HighwayKeys.ReplySlot(_requestId)), LockType.Exclusive, StoreType.Main);
        return true;
    }

    public override void Main<TGarnetApi>(TGarnetApi api, ref CustomProcedureInput procInput, ref MemoryResult<byte> output)
    {
        if (TryWriteError(ref output)) return;

        try
        {
            var slotKey    = CreateArgSlice(HighwayKeys.ReplySlot(_requestId));
            var valueSlice = CreateArgSlice(_payloadBytes);
            api.SETEX(slotKey, valueSlice, _opts.ReplySlotTtl);
            WriteSimpleString(ref output, "OK");
        }
        catch (Exception ex)
        {
            WriteError(ref output, HighwayErrors.InternalError(ex.Message));
        }
    }

    public override void Finalize<TGarnetApi>(TGarnetApi api, ref CustomProcedureInput procInput, ref MemoryResult<byte> output)
    {
        if (Failed) return; // a rejected command must never ring a doorbell
        _doorbell.Ring(HighwayKeys.ReplyDoorbell, _requestIdBytes);
    }
}
