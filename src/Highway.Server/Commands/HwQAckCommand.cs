using System.Buffers;
using Garnet.common;
using Garnet.server;
using Highway.Server.Internal;
using Highway.Server.Observability;
using Highway.Abstractions.Observability;
using Tsavorite.core;

namespace Highway.Server.Commands;

/// <summary>
/// HW.QACK &lt;queue&gt; &lt;nodeId&gt; &lt;messageId&gt; → :1 removed | :0 not found (feature 014).
///
/// <para>Completes a claimed message. Until this arrives the message is still in the
/// worker's processing list and will be redelivered once its lease expires — that is what
/// makes delivery at-least-once.</para>
///
/// <para>Acknowledging an unknown message returns <c>:0</c> rather than an error. A worker
/// retrying an acknowledgement it already sent is doing the right thing, and failing it
/// would turn a healthy retry into an error to investigate.</para>
/// </summary>
internal sealed class HwQAckCommand : HighwayCommandBase
{
    private readonly HighwayServerOptions _opts;
    private readonly FlightRecorder _recorder;

    private string _queue = null!;
    private string _nodeId = null!;
    private string _messageId = null!;
    private byte[] _messageIdBytes = [];

    public HwQAckCommand(HighwayServerOptions opts, FlightRecorder recorder)
    {
        _opts = opts;
        _recorder = recorder;
    }

    protected override void ResetState() => _messageIdBytes = [];

    protected override bool PrepareCore<TGarnetReadApi>(TGarnetReadApi api, ref CustomProcedureInput procInput)
    {
        var idx = 0;
        if (!TryReadDerivedIdentifier(ref procInput, ref idx, "queue", _opts.MaxIdentifierBytes, out _queue))
            return true;
        if (!TryReadIdentifier(ref procInput, ref idx, "nodeId", _opts.MaxIdentifierBytes, out _nodeId))
            return true;
        if (!TryReadIdentifier(ref procInput, ref idx, "messageId", _opts.MaxIdentifierBytes, out _messageId, out _messageIdBytes))
            return true;

        AddKey(CreateArgSlice(HighwayKeys.QueueProcessing(_queue, _nodeId)), LockType.Exclusive, StoreType.Object);
        return true;
    }

    public override void Main<TGarnetApi>(TGarnetApi api, ref CustomProcedureInput procInput, ref MemoryResult<byte> output)
    {
        if (TryWriteError(ref output)) return;

        try
        {
            var procKey = CreateArgSlice(HighwayKeys.QueueProcessing(_queue, _nodeId));
            var status = api.ListLeftPop(procKey, int.MaxValue, out var entries);

            if (status != GarnetStatus.OK || entries is null || entries.Length == 0)
            {
                WriteInteger(ref output, 0);
                return;
            }

            var found = false;
            foreach (var entry in entries)
            {
                var span = entry.ReadOnlySpan;

                if (!found && !Envelope.IsLegacyEntry(span))
                {
                    Envelope.DecodeRpcProcessingEntry(span, out _, out var id, out _, out _);
                    if (id.SequenceEqual(_messageIdBytes))
                    {
                        found = true;
                        continue; // remove this entry
                    }
                }

                api.ListRightPush(procKey, CreateArgSlice(span.ToArray()), out _);
            }

            WriteInteger(ref output, found ? 1 : 0);
        }
        catch (Exception ex)
        {
            WriteError(ref output, HighwayErrors.InternalError(ex.Message));
        }
    }


    public override void Finalize<TGarnetApi>(TGarnetApi api, ref CustomProcedureInput procInput, ref MemoryResult<byte> output)
        => _recorder.Record(
            HighwayEventType.QueueAcknowledged, _queue ?? "?",
            nodeId: _nodeId,
            requestId: _messageId,
            errorCode: FailureCode);
}
