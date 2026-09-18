using System.Diagnostics.Metrics;
using FluentAssertions;
using Highway.Client.Observability;
using Xunit;

namespace Highway.Client.Tests;

/// <summary>
/// Feature 051 T3 — the client's <c>Highway.Client</c> meter records RPC outcome/latency and a
/// <c>failovers</c> counter (the caller-side view of a master change).
/// </summary>
public class ClientMetricsTests
{
    private sealed class Listener : IDisposable
    {
        private readonly MeterListener _listener;
        public readonly List<(string Name, long Value, IReadOnlyList<KeyValuePair<string, object?>> Tags)> Longs = [];
        public readonly List<string> Doubles = [];

        public Listener()
        {
            _listener = new MeterListener
            {
                InstrumentPublished = (inst, l) => { if (inst.Meter.Name == HighwayClientMetrics.MeterName) l.EnableMeasurementEvents(inst); },
            };
            _listener.SetMeasurementEventCallback<long>((inst, m, tags, _) => Longs.Add((inst.Name, m, tags.ToArray())));
            _listener.SetMeasurementEventCallback<double>((inst, _, _, _) => Doubles.Add(inst.Name));
            _listener.Start();
        }

        public void Dispose() => _listener.Dispose();
    }

    [Fact]
    public void RecordFailover_IncrementsTheFailoverCounter()
    {
        using var listener = new Listener();

        HighwayClientMetrics.RecordFailover();
        HighwayClientMetrics.RecordFailover();

        listener.Longs.Where(x => x.Name == "highway.client.failovers").Sum(x => x.Value)
            .Should().BeGreaterThanOrEqualTo(2, "each followed master change is counted");
    }

    [Fact]
    public void RecordRpc_RecordsRequestWithResultTag_AndLatency()
    {
        using var listener = new Listener();

        HighwayClientMetrics.RecordRpc(ok: true, seconds: 0.01);

        listener.Longs.Should().Contain(x =>
            x.Name == "highway.client.rpc.requests"
            && x.Tags.Any(t => t.Key == "result" && (string?)t.Value == "ok"));
        listener.Doubles.Should().Contain("highway.client.rpc.latency");
    }
}
