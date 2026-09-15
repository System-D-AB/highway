using System.Text;
using Highway.Server.Commands.Runtime;
using Highway.Server.Internal;
using Highway.Abstractions.Observability;
using Highway.Server.Storage.Layout;

namespace Highway.Server.Commands.Ported;

/// <summary>
/// HW.ACK &lt;service&gt; &lt;nodeId&gt; &lt;requestId&gt; → +OK.
///
/// Completes a claimed RPC request: drain the node's processing list, drop the entry whose
/// request id matches, restore the rest in order. Acknowledging an unknown id is not an
/// error (+OK) — a retried ack is doing the right thing. Ported from Garnet unchanged.
/// </summary>
internal sealed class HwAckCommand : HighwayCommand
{
    private string _service = null!;
    private string _nodeId = null!;
    private byte[] _requestIdBytes = [];

    protected override bool Parse(CommandContext ctx, CommandInput input)
    {
        var idx = 0;
        if (!TryReadIdentifier(input, ref idx, "service", ctx.Options.MaxIdentifierBytes, out _service)) return false;
        if (!TryReadIdentifier(input, ref idx, "nodeId", ctx.Options.MaxIdentifierBytes, out _nodeId)) return false;
        if (!TryReadIdentifier(input, ref idx, "requestId", ctx.Options.MaxIdentifierBytes, out _, out _requestIdBytes)) return false;
        return true;
    }

    protected override void Run(CommandContext ctx, RespWriter writer)
    {
        using var _lock = ctx.Locks.Lock(_service);
        using var batch = ctx.Store.NewBatch();

        var found = false;
        ConsumerSupport.DrainAndRestore(ctx.Store, batch, HighwayNames.ServiceProcessing(_service, _nodeId), entry =>
        {
            if (!found && !Envelope.IsLegacyEntry(entry))
            {
                Envelope.DecodeRpcProcessingEntry(entry, out _, out var reqId, out _, out _);
                if (reqId.SequenceEqual(_requestIdBytes))
                {
                    found = true;
                    return null; // drop the acked entry
                }
            }
            return entry;
        });

        batch.Commit();
        writer.SimpleString("OK");
    }

    protected override void AfterCommit(CommandContext ctx)
        => ctx.Recorder.Record(
            HighwayEventType.RpcAcknowledged, _service ?? "?",
            nodeId: _nodeId,
            requestId: _requestIdBytes.Length > 0 ? Encoding.UTF8.GetString(_requestIdBytes) : null,
            errorCode: FailureCode);
}
