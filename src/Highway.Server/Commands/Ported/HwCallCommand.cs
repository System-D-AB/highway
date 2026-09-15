using System.Text;
using Highway.Server.Commands.Runtime;
using Highway.Server.Internal;
using Highway.Abstractions.Observability;
using Highway.Server.Storage.Layout;

namespace Highway.Server.Commands.Ported;

/// <summary>
/// HW.CALL &lt;service&gt; &lt;requestId&gt; &lt;payload&gt; → +OK.
///
/// Enqueues an RPC request onto the service queue (tail-push) and rings the service
/// doorbell. Ported from the Garnet version unchanged in behaviour.
/// </summary>
internal sealed class HwCallCommand : HighwayCommand
{
    private string _service = null!;
    private byte[] _requestIdBytes = [];
    private byte[] _payloadBytes = [];

    protected override bool Parse(CommandContext ctx, CommandInput input)
    {
        var idx = 0;
        if (!TryReadIdentifier(input, ref idx, "service", ctx.Options.MaxIdentifierBytes, out _service))
            return false;
        if (!TryReadIdentifier(input, ref idx, "requestId", ctx.Options.MaxIdentifierBytes, out _, out _requestIdBytes))
            return false;
        if (!TryReadPayload(input, ref idx, ctx.Options.MaxPayloadBytes, out _payloadBytes))
            return false;
        return true;
    }

    protected override void Run(CommandContext ctx, RespWriter writer)
    {
        var entry = Envelope.EncodeRpcEntry(_requestIdBytes, _payloadBytes);

        using var _lock = ctx.Locks.Lock(_service);
        using var batch = ctx.Store.NewBatch();
        HwQSendCommand.PushTail(ctx.Store, batch, HighwayNames.ServiceQueue(_service), entry);
        batch.Commit();

        writer.SimpleString("OK");
    }

    protected override void AfterCommit(CommandContext ctx)
    {
        // Record even on the failure path — failures are the most valuable recorder events.
        ctx.Recorder.Record(
            HighwayEventType.RpcEnqueued, _service ?? "?",
            requestId: _requestIdBytes.Length > 0 ? Encoding.UTF8.GetString(_requestIdBytes) : null,
            payload: _payloadBytes,
            errorCode: FailureCode);

        if (Failed) return;

        // Non-null past the Failed guard: _service is only unset when Parse failed.
        ctx.Doorbell.Ring(HighwayKeys.ServiceDoorbell(_service!), _requestIdBytes);
    }
}
