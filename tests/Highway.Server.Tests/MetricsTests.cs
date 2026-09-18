using System.Diagnostics.Metrics;
using FluentAssertions;
using Highway.Server.Internal;
using Highway.Server.Observability;
using Xunit;

namespace Highway.Server.Tests;

/// <summary>
/// Feature 051 T1/T2 — the broker's <c>Highway.Server</c> meter exposes the operational instruments,
/// the delivery/RPC/transition counters move when driven, the queue gauges report live depth, and an
/// idle broker with no listener never samples (zero-cost-when-unobserved).
/// </summary>
public class MetricsTests
{
    private static QueueStateDto Queue(string name, long depth, long bytes, long dead)
        => new(name, depth, bytes, MaxBytes: 0, InFlight: 0, DeadLettered: dead, Delayed: 0, IsSubscriberGroup: false);

    private static FlightRecorder Recorder() => new(new HighwayServerOptions().Observability);

    /// <summary>Collects measurements from one metrics instance in isolation (unique meter name).</summary>
    private sealed class Probe : IDisposable
    {
        private readonly MeterListener _listener;
        public readonly List<string> Instruments = [];
        public readonly List<(string Name, long Value, IReadOnlyList<KeyValuePair<string, object?>> Tags)> Longs = [];
        public readonly List<(string Name, double Value)> Doubles = [];

        public Probe(string meterName)
        {
            _listener = new MeterListener
            {
                InstrumentPublished = (inst, l) =>
                {
                    if (inst.Meter.Name != meterName) return;
                    Instruments.Add(inst.Name);
                    l.EnableMeasurementEvents(inst);
                },
            };
            _listener.SetMeasurementEventCallback<long>((inst, m, tags, _) =>
                Longs.Add((inst.Name, m, tags.ToArray())));
            _listener.SetMeasurementEventCallback<double>((inst, m, _, _) =>
                Doubles.Add((inst.Name, m)));
            _listener.Start();
        }

        public void Collect() => _listener.RecordObservableInstruments();
        public long Sum(string name) => Longs.Where(x => x.Name == name).Sum(x => x.Value);
        public void Dispose() => _listener.Dispose();
    }

    [Fact]
    public void Meter_ExposesTheDocumentedInstruments()
    {
        var name = "Highway.Server.Test." + Guid.NewGuid().ToString("N");
        using var probe = new Probe(name);
        using var metrics = new HighwayMetrics(replication: null, () => [], Recorder(), name);
        probe.Collect();

        probe.Instruments.Should().Contain(new[]
        {
            "highway.rpc.requests", "highway.rpc.latency",
            "highway.messages.published", "highway.messages.delivered",
            "highway.messages.acknowledged", "highway.messages.failed",
            "highway.messages.refused", "highway.messages.deadlettered",
            "highway.replication.promotions", "highway.replication.demotions", "highway.replication.fences",
            "highway.queue.depth", "highway.queue.bytes", "highway.deadletters",
            "highway.recorder.dropped",
        });
    }

    [Fact]
    public void DeliveryCounters_MoveWhenDriven()
    {
        var name = "Highway.Server.Test." + Guid.NewGuid().ToString("N");
        using var probe = new Probe(name);
        using var metrics = new HighwayMetrics(replication: null, () => [], Recorder(), name);

        metrics.RecordPublished();
        metrics.RecordPublished();
        metrics.RecordDelivered();
        metrics.RecordAcknowledged();
        metrics.RecordFailed();
        metrics.RecordRefused();
        metrics.RecordDeadLettered(3);
        metrics.RecordPromotion();
        metrics.RecordRpcRequest(ok: true, seconds: 0.002);

        probe.Sum("highway.messages.published").Should().Be(2);
        probe.Sum("highway.messages.delivered").Should().Be(1);
        probe.Sum("highway.messages.acknowledged").Should().Be(1);
        probe.Sum("highway.messages.failed").Should().Be(1);
        probe.Sum("highway.messages.refused").Should().Be(1);
        probe.Sum("highway.messages.deadlettered").Should().Be(3);
        probe.Sum("highway.replication.promotions").Should().Be(1);
        probe.Sum("highway.rpc.requests").Should().Be(1);
        probe.Doubles.Should().Contain(x => x.Name == "highway.rpc.latency");
    }

    [Fact]
    public void QueueGauges_ReportLiveDepthBytesAndDeadLetters_PerQueue()
    {
        var name = "Highway.Server.Test." + Guid.NewGuid().ToString("N");
        using var probe = new Probe(name);
        var queues = new[] { Queue("orders", depth: 7, bytes: 4096, dead: 2) };
        using var metrics = new HighwayMetrics(replication: null, () => queues, Recorder(), name);

        probe.Collect();

        Tagged(probe, "highway.queue.depth", "orders").Should().Be(7);
        Tagged(probe, "highway.queue.bytes", "orders").Should().Be(4096);
        Tagged(probe, "highway.deadletters", "orders").Should().Be(2);
    }

    [Fact]
    public void Unobserved_NeverSamplesTheLiveSources()
    {
        var name = "Highway.Server.Test." + Guid.NewGuid().ToString("N");
        var sampled = 0;
        using var metrics = new HighwayMetrics(
            replication: null,
            () => { sampled++; return []; },
            Recorder(),
            name);

        // No listener attached and no collection: the queue sampler must never run.
        sampled.Should().Be(0, "an unobserved broker pays nothing for its gauges");
    }

    private static long Tagged(Probe probe, string instrument, string queue)
        => probe.Longs
            .Where(x => x.Name == instrument && x.Tags.Any(t => t.Key == "queue" && (string?)t.Value == queue))
            .Select(x => x.Value)
            .Single();
}
