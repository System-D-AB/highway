using System.Text;
using Garnet.common;
using Garnet.server;
using Highway.Server.Internal;
using Highway.Server.Observability;
using Highway.Abstractions.Observability;
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
    private readonly FlightRecorder _recorder;

    // Captured in Prepare, used in Main and Finalize
    private string _service = null!;
    private byte[] _requestIdBytes = [];
    private byte[] _payloadBytes = [];

    public HwCallCommand(HighwayServerOptions opts, DoorbellBridge doorbell, FlightRecorder recorder)
    {
        _opts     = opts;
        _doorbell = doorbell;
        _recorder = recorder;
    }

    protected override void ResetState()
    {
        _requestIdBytes = [];
        _payloadBytes = [];
    }

    protected override bool PrepareCore<TGarnetReadApi>(TGarnetReadApi api, ref CustomProcedureInput procInput)
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
        // Record BEFORE the doorbell guard: a rejected command must not ring a
        // doorbell, but it must still be recorded — failures are the most
        // valuable thing in a flight recorder (feature 002).
        _recorder.Record(
            HighwayEventType.RpcEnqueued, _service ?? "?",
            requestId: _requestIdBytes.Length > 0 ? Encoding.UTF8.GetString(_requestIdBytes) : null,
            payload: _payloadBytes,
            errorCode: FailureCode);

        if (Failed) return; // a rejected command must never ring a doorbell
        _doorbell.Ring(HighwayKeys.ServiceDoorbell(_service), _requestIdBytes);
    }
}
