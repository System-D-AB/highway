using System.Text;
using Highway.Abstractions.Observability;
using Highway.Server.Commands.Runtime;
using Highway.Server.Storage.Boxes;

namespace Highway.Server.Commands.Ported;

/// <summary>
/// HW.CANCEL &lt;service&gt; &lt;requestId&gt; → :1 withdrawn | :0 not waiting (074).
///
/// <para>A caller that gives up early (the user closed the page) withdraws its request so it stops taking a worker's
/// time. Only a request still waiting in the queue is removed; one already claimed is not interrupted — its worker
/// stops at the deadline, or finishes and its reply is discarded. Best effort by contract: the client sends it once
/// and never fails the caller's cancellation on it.</para>
/// </summary>
internal sealed class HwCancelCommand : HighwayCommand
{
    private string _service = null!;
    private string _requestId = null!;
    private bool _cancelled;

    protected override bool Parse(CommandContext ctx, CommandInput input)
    {
        var idx = 0;
        if (!TryReadIdentifier(input, ref idx, "service", ctx.Options.MaxIdentifierBytes, out _service))
            return false;
        return TryReadIdentifier(input, ref idx, "requestId", ctx.Options.MaxIdentifierBytes, out _requestId);
    }

    protected override void Run(CommandContext ctx, RespWriter writer)
    {
        if (BoxCommandSupport.Find(ctx, BoxKeys.Kind.Service, _service) is not { } box)
        {
            writer.Integer(0);
            return;
        }

        using (BoxCommandSupport.Lock(ctx, box))
        using (var wb = BoxCommandSupport.Open(ctx, box, BoxKeys.Kind.Service))
        {
            _cancelled = wb.CancelQueued(_requestId);
            wb.Commit();
            if (_cancelled) ctx.Calls?.Forget(_service, _requestId);
        }
        writer.Integer(_cancelled ? 1 : 0);
    }

    protected override void AfterCommit(CommandContext ctx)
    {
        if (!_cancelled) return;
        ctx.Recorder.Record(HighwayEventType.RpcCancelled, _service, requestId: _requestId);
        ctx.Metrics?.RecordRpcCancelled();
        ctx.RpcLog.Cancelled(_service);
    }
}
