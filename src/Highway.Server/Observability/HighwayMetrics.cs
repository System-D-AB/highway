using System.Diagnostics.Metrics;
using Highway.Server.Storage.Rocks;

namespace Highway.Server.Observability;

/// <summary>
/// Server-side operational metrics (feature 051). Owns a <see cref="Meter"/> named
/// <c>Highway.Server</c> and every instrument the broker exposes — replication role/epoch/lag,
/// queue depth/bytes/dead-letters, RPC and delivery counters, connections, recorder health.
///
/// <para><b>Same posture as <see cref="HighwayServerActivity"/>: no OpenTelemetry dependency.</b>
/// Highway defines the instruments through the in-box <see cref="System.Diagnostics.Metrics"/> API;
/// the hosting application wires whatever exporter it runs (OpenTelemetry → Prometheus, OTLP, …) and
/// subscribes with <c>.AddMeter("Highway.Server")</c>. The instrument names, units and label keys are
/// the documented contract (see <c>docs/HIGHWAY-PROTOCOL.md</c> § "Metric emission").</para>
///
/// <para><b>Zero cost when unobserved.</b> Counters do a cheap <c>Add</c> whether or not anything is
/// listening; the observable gauges sample the live feeder / store / recorder <b>only</b> when a
/// listener collects, so an idle broker with no exporter attached pays nothing.</para>
///
/// <para><b>Bounded cardinality.</b> Every label is drawn from a bounded name set — queues,
/// replicas, the four roles, an <c>ok|error</c> result — never a per-request id, the same rule the
/// flight recorder enforces.</para>
/// </summary>
internal sealed class HighwayMetrics : IDisposable
{
    /// <summary>The meter name applications subscribe to. Part of the documented metric surface.</summary>
    public const string MeterName = "Highway.Server";

    private readonly Meter _meter;

    // Event counters — incremented at the command / transition sites.
    private readonly Counter<long> _rpcRequests;
    private readonly Histogram<double> _rpcLatency;
    private readonly Counter<long> _published;
    private readonly Counter<long> _delivered;
    private readonly Counter<long> _acknowledged;
    private readonly Counter<long> _failed;
    private readonly Counter<long> _refused;
    private readonly Counter<long> _deadLettered;
    private readonly Counter<long> _promotions;
    private readonly Counter<long> _demotions;
    private readonly Counter<long> _fences;

    /// <param name="replication">The WAL feeder, or null on an ephemeral (in-memory) broker — then
    /// the replication gauges simply report nothing.</param>
    /// <param name="sampleQueues">Reads current queue state (depth/bytes/dead-letters) on demand;
    /// invoked only when a listener collects the queue gauges.</param>
    /// <param name="recorder">The flight recorder, for its drop gauge.</param>
    /// <param name="meterName">Overrides the meter name; production always uses the default
    /// <see cref="MeterName"/>. A test passes a unique name so its <c>MeterListener</c> never
    /// collects a parallel embedded server's identically-named instruments.</param>
    public HighwayMetrics(
        ReplicationFeeder? replication,
        Func<IReadOnlyList<QueueStateDto>> sampleQueues,
        FlightRecorder recorder,
        string? meterName = null)
    {
        _meter = new Meter(meterName ?? MeterName);

        _rpcRequests = _meter.CreateCounter<long>("highway.rpc.requests", "{request}", "RPC requests enqueued (HW.CALL), tagged by result.");
        _rpcLatency = _meter.CreateHistogram<double>("highway.rpc.latency", "s", "Server-side handling latency of an RPC request.");
        _published = _meter.CreateCounter<long>("highway.messages.published", "{message}", "Queue/pub-sub messages accepted (HW.QSEND).");
        _delivered = _meter.CreateCounter<long>("highway.messages.delivered", "{message}", "Messages claimed by a worker (HW.QCLAIM).");
        _acknowledged = _meter.CreateCounter<long>("highway.messages.acknowledged", "{message}", "Messages acknowledged (HW.QACK).");
        _failed = _meter.CreateCounter<long>("highway.messages.failed", "{message}", "Deliveries reported failed (HW.FAIL).");
        _refused = _meter.CreateCounter<long>("highway.messages.refused", "{message}", "Sends refused for a full queue (HW.QSEND, QUEUE_FULL).");
        _deadLettered = _meter.CreateCounter<long>("highway.messages.deadlettered", "{message}", "Messages moved to the dead-letter list on exhausting their attempts.");
        _promotions = _meter.CreateCounter<long>("highway.replication.promotions", "{event}", "Times this node promoted to primary.");
        _demotions = _meter.CreateCounter<long>("highway.replication.demotions", "{event}", "Times this node demoted on a higher observed epoch.");
        _fences = _meter.CreateCounter<long>("highway.replication.fences", "{event}", "Times this node fenced itself (deadman backstop).");

        // ---- observable gauges: sampled only when a listener collects --------
        if (replication is not null)
        {
            _meter.CreateObservableGauge("highway.replication.role", () => RoleMeasurements(replication), "{role}",
                "1 for the node's current role, 0 for the others (labelled role=primary|replica|fenced|demoted).");
            _meter.CreateObservableGauge("highway.replication.epoch", () => (long)replication.Epoch, "{epoch}",
                "The node's current replication epoch.");
            _meter.CreateObservableGauge("highway.replication.slots", () => (long)replication.Slots.Count, "{slot}",
                "Replica slots the primary is tracking.");
            _meter.CreateObservableGauge("highway.replication.lag_sequences", () => LagMeasurements(replication), "{sequence}",
                "Per-replica WAL lag in sequence numbers (labelled replica).");
            _meter.CreateObservableGauge("highway.replication.rpo_seconds", () => RpoSeconds(replication), "s",
                "Estimated RPO: seconds since the least-recently-heard active replica last made contact.");
            _meter.CreateObservableGauge("highway.connections", () => (long)replication.ConnectedClients, "{connection}",
                "Authenticated client sessions currently connected (the herd).");
        }

        _meter.CreateObservableGauge("highway.queue.depth", () => QueueMeasurements(sampleQueues, q => q.Depth), "{message}",
            "Live queue depth (labelled queue).");
        _meter.CreateObservableGauge("highway.queue.bytes", () => QueueMeasurements(sampleQueues, q => q.Bytes), "By",
            "Live queue size in bytes (labelled queue).");
        _meter.CreateObservableGauge("highway.deadletters", () => QueueMeasurements(sampleQueues, q => q.DeadLettered), "{message}",
            "Dead-lettered messages awaiting an operator (labelled queue).");
        _meter.CreateObservableGauge("highway.recorder.dropped", () => recorder.DroppedBudget, "{event}",
            "Flight-recorder events dropped rather than blocking a delivery.");
    }

    // ---- counter recording (called from the command / transition sites) ------

    /// <summary>An RPC request was enqueued; <paramref name="ok"/> false means it was rejected.</summary>
    public void RecordRpcRequest(bool ok, double seconds)
    {
        var result = ok ? "ok" : "error";
        _rpcRequests.Add(1, new KeyValuePair<string, object?>("result", result));
        _rpcLatency.Record(seconds, new KeyValuePair<string, object?>("result", result));
    }

    public void RecordPublished() => _published.Add(1);
    public void RecordDelivered() => _delivered.Add(1);
    public void RecordAcknowledged() => _acknowledged.Add(1);
    public void RecordFailed() => _failed.Add(1);
    public void RecordRefused() => _refused.Add(1);
    public void RecordDeadLettered(long count) { if (count > 0) _deadLettered.Add(count); }
    public void RecordPromotion() => _promotions.Add(1);
    public void RecordDemotion() => _demotions.Add(1);
    public void RecordFence() => _fences.Add(1);

    // ---- gauge sampling helpers ----------------------------------------------

    private static IEnumerable<Measurement<long>> RoleMeasurements(ReplicationFeeder feeder)
    {
        var role = feeder.Role;
        yield return new(role == ReplicaRole.Primary ? 1 : 0, new KeyValuePair<string, object?>("role", "primary"));
        yield return new(role == ReplicaRole.Replica ? 1 : 0, new KeyValuePair<string, object?>("role", "replica"));
        yield return new(role == ReplicaRole.Fenced ? 1 : 0, new KeyValuePair<string, object?>("role", "fenced"));
        yield return new(role == ReplicaRole.Demoted ? 1 : 0, new KeyValuePair<string, object?>("role", "demoted"));
    }

    private static IEnumerable<Measurement<long>> LagMeasurements(ReplicationFeeder feeder)
    {
        var latest = feeder.Engine.GetLatestSequenceNumber();
        foreach (var slot in feeder.Slots.Values)
        {
            var lag = latest > slot.AckedSeq ? (long)(latest - slot.AckedSeq) : 0;
            yield return new(lag, new KeyValuePair<string, object?>("replica", slot.ReplicaId));
        }
    }

    private static double RpoSeconds(ReplicationFeeder feeder)
    {
        var now = feeder.Options.Clock.GetUtcNow();
        double worst = 0;
        foreach (var slot in feeder.Slots.Values)
        {
            if (slot.State == SlotState.Dropped || slot.LastContact == default) continue;
            var age = (now - slot.LastContact).TotalSeconds;
            if (age > worst) worst = age;
        }
        return worst;
    }

    private static IEnumerable<Measurement<long>> QueueMeasurements(
        Func<IReadOnlyList<QueueStateDto>> sampleQueues, Func<QueueStateDto, long> select)
    {
        foreach (var q in sampleQueues())
            yield return new(select(q), new KeyValuePair<string, object?>("queue", q.Name));
    }

    public void Dispose() => _meter.Dispose();
}
