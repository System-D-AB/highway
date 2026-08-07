using System.Text;
using Garnet.common;
using Garnet.server;
using Highway.Server.Internal;
using Highway.Server.Observability;
using Highway.Abstractions.Observability;
using Tsavorite.core;

namespace Highway.Server.Commands;

/// <summary>
/// HW.ACK &lt;service&gt; &lt;nodeId&gt; &lt;requestId&gt; → +OK
///
/// Removes the entry with the given requestId from the node's processing list.
/// Idempotent: if the requestId is not found, +OK is still returned.
/// </summary>
internal sealed class HwAckCommand : HighwayCommandBase
{
    private readonly HighwayServerOptions _opts;
    private readonly FlightRecorder _recorder;

    private string _service = null!;
    private string _nodeId = null!;
    private byte[] _requestIdBytes = [];

    public HwAckCommand(HighwayServerOptions opts, FlightRecorder recorder)
    {
        _opts = opts;
        _recorder = recorder;
    }

    protected override void ResetState() => _requestIdBytes = [];

    protected override bool PrepareCore<TGarnetReadApi>(TGarnetReadApi api, ref CustomProcedureInput procInput)
    {
        int idx = 0;
        if (!TryReadIdentifier(ref procInput, ref idx, "service", _opts.MaxIdentifierBytes, out _service))
            return true;
        if (!TryReadIdentifier(ref procInput, ref idx, "nodeId", _opts.MaxIdentifierBytes, out _nodeId))
            return true;
        if (!TryReadIdentifier(ref procInput, ref idx, "requestId", _opts.MaxIdentifierBytes, out _, out _requestIdBytes))
            return true;

        AddKey(CreateArgSlice(HighwayKeys.ServiceProcessing(_service, _nodeId)), LockType.Exclusive, StoreType.Object);
        return true;
    }

    public override void Main<TGarnetApi>(TGarnetApi api, ref CustomProcedureInput procInput, ref MemoryResult<byte> output)
    {
        if (TryWriteError(ref output)) return;

        try
        {
            var procKey = CreateArgSlice(HighwayKeys.ServiceProcessing(_service, _nodeId));

            var status = api.ListLeftPop(procKey, int.MaxValue, out var entries);
            if (status != GarnetStatus.OK || entries is null || entries.Length == 0)
            {
                WriteSimpleString(ref output, "OK");
                return;
            }

            bool found = false;
            foreach (var entry in entries)
            {
                var span = entry.ReadOnlySpan;
                if (!found && span.Length >= 10)
                {
                    Envelope.DecodeRpcProcessingEntry(span, out _, out var reqId, out _);
                    if (reqId.SequenceEqual(_requestIdBytes))
                    {
                        found = true;
                        continue; // remove this entry
                    }
                }
                api.ListRightPush(procKey, CreateArgSlice(span.ToArray()), out _);
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
            HighwayEventType.RpcAcknowledged, _service ?? "?",
            nodeId: _nodeId,
            requestId: _requestIdBytes.Length > 0 ? Encoding.UTF8.GetString(_requestIdBytes) : null,
            errorCode: FailureCode);
}
