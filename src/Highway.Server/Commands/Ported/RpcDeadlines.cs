using Highway.Abstractions.Observability;
using Highway.Server.Commands.Runtime;
using Highway.Server.Storage.Boxes;

namespace Highway.Server.Commands.Ported;

/// <summary>The broker's RPC deadline settings and reporting (074).</summary>
internal static class RpcDeadlines
{
    public static WorkBox.DeadlinePolicy Policy(CommandContext ctx) => new(
        ctx.Options.Lease.Ticks,
        ctx.Options.RpcLeaseGrace.Ticks,
        ctx.Options.MinRemainingToStart.Ticks,
        Math.Max(1, ctx.Options.MaxExpiredPerClaim));

    /// <summary>Milliseconds left before <paramref name="deadlineTicks"/>, never negative — what a worker is told.</summary>
    public static long RemainingMs(long deadlineTicks, long nowTicks)
        => Math.Max(0, (deadlineTicks - nowTicks) / TimeSpan.TicksPerMillisecond);

    /// <summary>Forgets the ids of requests recovery removed. Call under the service box lock, after the commit.</summary>
    public static void Forget(CommandContext ctx, string service, IEnumerable<WorkBox.RpcEnded> ended)
    {
        if (ctx.Calls is not { } calls) return;
        foreach (var e in ended) calls.Forget(service, e.Id);
    }

    /// <summary>Records, counts and logs requests recovery removed (074). Call after the commit.</summary>
    public static void Report(CommandContext ctx, string service, string? node, IReadOnlyCollection<WorkBox.RpcEnded> ended)
    {
        if (ended.Count == 0) return;
        long expired = 0;
        foreach (var e in ended)
        {
            if (e.Expired)
            {
                expired++;
                ctx.Recorder.Record(HighwayEventType.RpcExpired, service, nodeId: node, requestId: e.Id, count: e.Attempts,
                    errorCode: "redelivery");
            }
            else
            {
                ctx.Recorder.Record(HighwayEventType.RpcFailedFinal, service, nodeId: node, requestId: e.Id, count: e.Attempts,
                    errorCode: e.Failure ?? "no failure reported");
                ctx.Metrics?.RecordRpcFailedFinal();
                ctx.RpcLog.FailedFinal(ctx.Recorder.Logger, service, e.Failure, ctx.NowTicks, ctx.Options.RpcLogInterval);
            }
        }
        if (expired > 0)
        {
            ctx.Metrics?.RecordRpcExpired("redelivery", expired);
            ctx.RpcLog.Expired(ctx.Recorder.Logger, service, "redelivery", expired, ctx.NowTicks, ctx.Options.RpcLogInterval);
        }
    }
}
