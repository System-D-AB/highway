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
///
/// <para><b>074 (from 2.5.6):</b> a repeated call for a request id this broker enqueued within
/// <c>RpcDuplicateWindow</c> answers <c>+OK</c> and is not queued again — the request id is one call, and a
/// client re-sends pending calls after a connection blip. Checked under the service box lock; the id is
/// remembered only after the commit.</para>
/// </summary>
internal sealed class HwCallCommand : HighwayCommand
{
    private string _service = null!;
    private byte[] _requestIdBytes = [];
    private byte[] _payloadBytes = [];
    private bool _duplicate;

    /// <summary>True when this call repeated one already queued here and was suppressed (074).</summary>
    internal bool IsDuplicate => _duplicate;

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

        // 065: the service's own box — resolved (created on first use) before its lock is taken.
        var box = BoxCommandSupport.Ensure(ctx, Storage.Boxes.BoxKeys.Kind.Service, _service);
        using var _lock = BoxCommandSupport.Lock(ctx, box);
        var requestId = Encoding.UTF8.GetString(_requestIdBytes);
        if (ctx.Calls is { } calls && calls.TryGet(_service, requestId, ctx.NowTicks, out _))
        {
            _duplicate = true;           // already queued (or done) here: one call, one entry
            calls.RecordDuplicate();
            ctx.Metrics?.RecordMessageDuplicate("call");
            writer.SimpleString("OK");
            return;
        }

        using var wb = BoxCommandSupport.Open(ctx, box, Storage.Boxes.BoxKeys.Kind.Service);
        wb.Enqueue(entry);
        wb.Commit();
        ctx.Calls?.Admit(_service, requestId, ctx.NowTicks, default);

        writer.SimpleString("OK");
    }

    protected override void AfterCommit(CommandContext ctx)
    {
        if (_duplicate) return;   // nothing was enqueued: no event, no doorbell

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
