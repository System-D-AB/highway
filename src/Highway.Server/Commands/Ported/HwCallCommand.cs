using System.Globalization;
using System.Text;
using Highway.Server.Commands.Runtime;
using Highway.Server.Internal;
using Highway.Abstractions.Observability;

namespace Highway.Server.Commands.Ported;

/// <summary>
/// HW.CALL &lt;service&gt; &lt;requestId&gt; &lt;payload&gt; [TIMEOUT &lt;ms&gt;] → +OK | ERR HW_NO_WORKERS.
///
/// Enqueues an RPC request onto the service queue (tail-push) and rings the service doorbell.
///
/// <para><b>Deadline (074):</b> the caller says how long it will wait (<c>TIMEOUT</c>, relative; absent →
/// <c>DefaultRpcTimeout</c>, longer than <c>MaxRpcTimeout</c> → clamped). The broker fixes the absolute deadline
/// with its own clock and stores it on the entry, so claims, redeliveries and replies can tell when the answer is
/// no longer wanted. No clock but the broker's is ever compared.</para>
///
/// <para><b>Fail fast (074):</b> a service no node has ever declared answers <c>HW_NO_WORKERS</c> at once. A
/// service that has been served is always queued, whether or not a worker is live now.</para>
///
/// <para><b>Duplicates (074):</b> a repeated call for a request id this broker enqueued, before that request's
/// deadline, answers <c>+OK</c> and is not queued again — a client re-sends pending calls after a failover.
/// Checked under the service box lock; the id is remembered only after the commit.</para>
/// </summary>
internal sealed class HwCallCommand : HighwayCommand
{
    private string _service = null!;
    private byte[] _requestIdBytes = [];
    private byte[] _payloadBytes = [];
    private TimeSpan? _timeout;
    private bool _duplicate;
    private bool _noWorkers;

    /// <summary>True when this call repeated one already queued here and was suppressed (074).</summary>
    internal bool IsDuplicate => _duplicate;

    /// <summary>True when this call was refused because no node has ever served the service (074).</summary>
    internal bool IsFailFast => _noWorkers;

    /// <summary>The timeout the broker applied, after the default and the clamp (074; for tests and the log).</summary>
    internal TimeSpan AppliedTimeout { get; private set; }

    protected override bool Parse(CommandContext ctx, CommandInput input)
    {
        var idx = 0;
        if (!TryReadIdentifier(input, ref idx, "service", ctx.Options.MaxIdentifierBytes, out _service))
            return false;
        if (!TryReadIdentifier(input, ref idx, "requestId", ctx.Options.MaxIdentifierBytes, out _, out _requestIdBytes))
            return false;
        if (!TryReadPayload(input, ref idx, ctx.Options.MaxPayloadBytes, out _payloadBytes))
            return false;

        var keyword = input.Next(ref idx);
        if (keyword.Length == 0) return true;
        if (!Encoding.ASCII.GetString(keyword).Equals("TIMEOUT", StringComparison.OrdinalIgnoreCase))
            return Fail(HighwayErrors.InvalidArg, $"unknown argument '{Encoding.UTF8.GetString(keyword)}'; expected TIMEOUT <ms>");
        if (!long.TryParse(Encoding.ASCII.GetString(input.Next(ref idx)), NumberStyles.None, CultureInfo.InvariantCulture, out var ms) || ms <= 0)
            return Fail(HighwayErrors.InvalidArg, "TIMEOUT must be a positive number of milliseconds");
        if (input.Next(ref idx).Length != 0)
            return Fail(HighwayErrors.InvalidArg, "unexpected argument after TIMEOUT <ms>");
        _timeout = TimeSpan.FromMilliseconds(Math.Min(ms, (long)TimeSpan.MaxValue.TotalMilliseconds / 2));
        return true;
    }

    protected override void Run(CommandContext ctx, RespWriter writer)
    {
        if (ctx.Options.RpcFailFast && !RegistrySupport.HasBeenServed(ctx.Store, _service))
        {
            _noWorkers = true;
            ctx.Metrics?.RecordRpcFailFast();
            writer.Error(HighwayErrors.NoWorkers(_service));
            return;
        }

        var timeout = _timeout ?? ctx.Options.DefaultRpcTimeout;
        if (timeout > ctx.Options.MaxRpcTimeout)
        {
            ctx.RpcLog.Clamped(ctx.Recorder.Logger, _service, timeout, ctx.Options.MaxRpcTimeout);
            timeout = ctx.Options.MaxRpcTimeout;
        }
        AppliedTimeout = timeout;
        var deadline = ctx.NowTicks + timeout.Ticks;
        var entry = Envelope.WithDeadline(Envelope.EncodeRpcEntry(_requestIdBytes, _payloadBytes), ctx.NowTicks, deadline);

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
        ctx.Calls?.Admit(_service, requestId, ctx.NowTicks, default, deadline);   // until its own deadline

        writer.SimpleString("OK");
    }

    protected override void AfterCommit(CommandContext ctx)
    {
        if (_duplicate) return;   // nothing was enqueued: no event, no doorbell
        if (_noWorkers)
        {
            ctx.Recorder.Record(HighwayEventType.RpcFailFast, _service,
                requestId: Encoding.UTF8.GetString(_requestIdBytes), errorCode: HighwayErrors.NoWorkersCode);
            return;
        }

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
