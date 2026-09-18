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
}
