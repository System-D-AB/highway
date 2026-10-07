using System.Diagnostics.Metrics;

namespace Highway.Client.Observability;

/// <summary>
/// Client-side operational metrics (feature 051 R2). A <see cref="Meter"/> named
/// <c>Highway.Client</c> exposing the caller's view: RPC latency/throughput/outcome, and
/// <c>highway.client.failovers</c> — how many times the connection followed a master change
/// (a <c>-NOTPRIMARY</c> redirect or a dead-primary walk), the caller-side signal of a cluster
/// event.
///
/// <para><b>Same posture as <see cref="HighwayActivity"/>: no OpenTelemetry dependency, zero cost
/// when unobserved.</b> Static, like the client's <see cref="System.Diagnostics.ActivitySource"/> —
/// the process-wide meter aggregates across every <c>HighwayClient</c> in the app, and the hosting
/// application subscribes with <c>.AddMeter("Highway.Client")</c>.</para>
/// </summary>
internal static class HighwayClientMetrics
{
    /// <summary>The meter name applications subscribe to. Part of the documented metric surface.</summary>
    public const string MeterName = "Highway.Client";

    private static readonly Meter Meter = new(MeterName);

    private static readonly Counter<long> Requests =
        Meter.CreateCounter<long>("highway.client.rpc.requests", "{request}", "RPC calls made, tagged by result.");

    private static readonly Histogram<double> Latency =
        Meter.CreateHistogram<double>("highway.client.rpc.latency", "s", "End-to-end RPC round-trip latency from the caller.");

    private static readonly Counter<long> Failovers =
        Meter.CreateCounter<long>("highway.client.failovers", "{failover}", "Master changes the connection followed (-NOTPRIMARY redirect or dead-primary walk).");

    /// <summary>Records a completed RPC round trip; <paramref name="ok"/> false means a 4xx/5xx outcome.</summary>
    public static void RecordRpc(bool ok, double seconds)
    {
        var result = ok ? "ok" : "error";
        Requests.Add(1, new KeyValuePair<string, object?>("result", result));
        Latency.Record(seconds, new KeyValuePair<string, object?>("result", result));
    }

    /// <summary>The connection adopted a new master — a failover the caller followed.</summary>
    public static void RecordFailover() => Failovers.Add(1);

    private static readonly Counter<long> HandlersCancelled =
        Meter.CreateCounter<long>("highway.handlers.cancelled", "{handler}", "Handlers cancelled because processing exceeded MaxProcessingTime, tagged by kind.");

    private static readonly Counter<long> HandlersAbandoned =
        Meter.CreateCounter<long>("highway.handlers.abandoned", "{handler}", "Hung handlers abandoned after MaxProcessingTime + AbandonGrace, tagged by kind.");

    /// <summary>A handler was cancelled because processing exceeded MaxProcessingTime (074 Part B).</summary>
    public static void RecordHandlerCancelled(string kind)
        => HandlersCancelled.Add(1, new KeyValuePair<string, object?>("kind", kind));

    /// <summary>A hung handler was abandoned after MaxProcessingTime + AbandonGrace (074 Part B).</summary>
    public static void RecordHandlerAbandoned(string kind)
        => HandlersAbandoned.Add(1, new KeyValuePair<string, object?>("kind", kind));

    private static readonly Counter<long> RpcDeadlines =
        Meter.CreateCounter<long>("highway.client.rpc.deadline_exceeded", "{request}", "RPC handlers stopped at their caller's deadline (074).");

    private static readonly Counter<long> RpcCancels =
        Meter.CreateCounter<long>("highway.client.rpc.cancels", "{request}", "HW.CANCEL sent for calls their caller gave up on, tagged by result (withdrawn, not_waiting, failed) (074).");

    private static readonly Counter<long> RpcLateReplies =
        Meter.CreateCounter<long>("highway.client.rpc.replies.late", "{reply}", "Replies the broker discarded because they came after the request's deadline (074).");

    /// <summary>An RPC handler was stopped at its caller's deadline (074).</summary>
    public static void RecordRpcDeadlineExceeded() => RpcDeadlines.Add(1);

    /// <summary>The result of an HW.CANCEL for a call its caller gave up on (074).</summary>
    public static void RecordRpcCancel(string result) => RpcCancels.Add(1, new KeyValuePair<string, object?>("result", result));

    /// <summary>A reply arrived after its request's deadline and was discarded (074).</summary>
    public static void RecordRpcLateReply() => RpcLateReplies.Add(1);
}
