using System.Text;
using Highway.Server.Commands.Runtime;
using Highway.Server.Internal;
using Highway.Abstractions.Observability;
using Highway.Server.Storage.Layout;

namespace Highway.Server.Commands.Ported;

/// <summary>
/// HW.QACK &lt;queue&gt; &lt;nodeId&gt; &lt;messageId&gt; → :1 removed | :0 not found (feature 014).
///
/// Completes a claimed queue message: drain the node's processing list, drop the entry whose
/// message id matches (reply :1), or reply :0 if not found. Until this arrives the message is
/// still in the processing list and will be redelivered on lease expiry — that is what makes
/// delivery at-least-once. A retried ack of an already-removed id returns :0, not an error.
/// </summary>
internal sealed class HwQAckCommand : HighwayCommand
{
    private string _queue = null!;
    private string _nodeId = null!;
    private byte[] _messageIdBytes = [];

    protected override bool Parse(CommandContext ctx, CommandInput input)
    {
        var idx = 0;
        if (!TryReadDerivedIdentifier(input, ref idx, "queue", ctx.Options.MaxIdentifierBytes, out _queue)) return false;
        if (!TryReadIdentifier(input, ref idx, "nodeId", ctx.Options.MaxIdentifierBytes, out _nodeId)) return false;
        if (!TryReadIdentifier(input, ref idx, "messageId", ctx.Options.MaxIdentifierBytes, out _, out _messageIdBytes)) return false;
        return true;
    }

    protected override void Run(CommandContext ctx, RespWriter writer)
    {
        using var _lock = ctx.Locks.Lock(_queue);
        using var batch = ctx.Store.NewBatch();

        var found = false;
        ConsumerSupport.DrainAndRestore(ctx.Store, batch, HighwayNames.QueueProcessing(_queue, _nodeId), entry =>
        {
            if (!found && !Envelope.IsLegacyEntry(entry))
            {
                Envelope.DecodeRpcProcessingEntry(entry, out _, out var id, out _, out _);
                if (id.SequenceEqual(_messageIdBytes))
                {
                    found = true;
                    return null; // drop the acked entry
                }
            }
            return entry;
        });

        batch.Commit();
        writer.Integer(found ? 1 : 0);
    }

    protected override void AfterCommit(CommandContext ctx)
    {
        ctx.Recorder.Record(
            HighwayEventType.QueueAcknowledged, _queue ?? "?",
            nodeId: _nodeId,
            requestId: _messageIdBytes.Length > 0 ? Encoding.UTF8.GetString(_messageIdBytes) : null,
            errorCode: FailureCode);
        if (!Failed) ctx.Metrics?.RecordAcknowledged();
    }
}
