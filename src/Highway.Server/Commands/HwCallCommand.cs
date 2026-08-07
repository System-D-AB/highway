using Garnet.common;
using Garnet.server;
using Highway.Server.Internal;
using Tsavorite.core;

namespace Highway.Server.Commands;

/// <summary>
/// HW.CALL &lt;service&gt; &lt;requestId&gt; &lt;payload&gt; → +OK
///
/// Enqueues an RPC request onto the service queue and rings the service
/// doorbell so waiting workers are notified immediately.
/// </summary>
internal sealed class HwCallCommand : HighwayCommandBase
{
    private readonly HighwayServerOptions _opts;
    private readonly DoorbellBridge _doorbell;

    // Captured in Prepare, used in Main and Finalize
    private string _service = null!;
    private byte[] _requestIdBytes = [];
    private byte[] _payloadBytes = [];

    public HwCallCommand(HighwayServerOptions opts, DoorbellBridge doorbell)
    {
        _opts     = opts;
        _doorbell = doorbell;
    }

    public override bool Prepare<TGarnetReadApi>(TGarnetReadApi api, ref CustomProcedureInput procInput)
    {
        int idx = 0;
        if (!TryReadIdentifier(ref procInput, ref idx, "service", _opts.MaxIdentifierBytes, out _service))
            return true;
        if (!TryReadIdentifier(ref procInput, ref idx, "requestId", _opts.MaxIdentifierBytes, out _, out _requestIdBytes))
            return true;
        if (!TryReadPayload(ref procInput, ref idx, _opts.MaxPayloadBytes, out _payloadBytes))
            return true;

        AddKey(CreateArgSlice(HighwayKeys.ServiceQueue(_service)), LockType.Exclusive, StoreType.Object);
        return true;
    }

    public override void Main<TGarnetApi>(TGarnetApi api, ref CustomProcedureInput procInput, ref MemoryResult<byte> output)
    {
        if (TryWriteError(ref output)) return;

        try
        {
            var entry    = Envelope.EncodeRpcEntry(_requestIdBytes, _payloadBytes);
            var queueKey = CreateArgSlice(HighwayKeys.ServiceQueue(_service));
            api.ListRightPush(queueKey, CreateArgSlice(entry), out _);
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
        _doorbell.Ring(HighwayKeys.ServiceDoorbell(_service), _requestIdBytes);
    }
}
