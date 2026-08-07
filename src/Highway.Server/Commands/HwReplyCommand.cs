using Garnet.common;
using Garnet.server;
using Highway.Server.Internal;
using Highway.Server.Observability;
using Highway.Abstractions.Observability;
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
    /// <summary>
    /// The flight-recorder bucket <c>RpcReplied</c> events are recorded under.
    ///
    /// <para><b>Why a fixed name.</b> The recorder buckets by service or channel
    /// name, and <c>HW.REPLY</c> carries neither — its arguments are a request ID
    /// and a payload, so the service that produced the reply is not on the wire.
    /// Recording under the request ID instead, as this command originally did,
    /// created a new bucket per RPC: the recorder's name dictionary grew without
    /// bound for the life of the process, and replies never appeared alongside the
    /// rest of their service's lifecycle.</para>
    ///
    /// <para>Reserved: an application that happens to name a service or channel
    /// <c>hw.replies</c> will find its events interleaved with these. Harmless, and
    /// the <c>hw.</c> prefix matches the namespace Highway reserves everywhere else.</para>
    /// </summary>
    internal const string RecorderName = "hw.replies";

    private readonly HighwayServerOptions _opts;
    private readonly DoorbellBridge _doorbell;
    private readonly FlightRecorder _recorder;

    private string _requestId = null!;
    private byte[] _requestIdBytes = [];
    private byte[] _payloadBytes = [];

    public HwReplyCommand(HighwayServerOptions opts, DoorbellBridge doorbell, FlightRecorder recorder)
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
        // Fixed bucket, never the request ID — see RecorderName.
        _recorder.Record(
            HighwayEventType.RpcReplied, RecorderName,
            requestId: _requestId,
            payload: _payloadBytes,
            errorCode: FailureCode);

        if (Failed) return; // a rejected command must never ring a doorbell
        _doorbell.Ring(HighwayKeys.ReplyDoorbell, _requestIdBytes);
    }
}
