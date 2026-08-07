using System.Diagnostics;
using FluentAssertions;
using Highway.Abstractions.Observability;
using Highway.Server.Observability;
using Xunit;

namespace Highway.Server.Tests;

/// <summary>
/// Feature 011, T3 — Prove the notification path costs nothing when unused.
///
/// This is a measurement assertion, not a micro-benchmark. The bound is loose
/// enough for CI (1000 records in under 50 ms) but catches a lock or allocation
/// bug introduced in the observer path.
/// </summary>
public class FlightRecorderPerformanceTests
{
    private static ObservabilityOptions Opts(Action<ObservabilityOptions>? tune = null)
    {
        var o = new ObservabilityOptions { SweepInterval = TimeSpan.FromHours(1) };
        tune?.Invoke(o);
        return o;
    }

    [Fact]
    public void Record_WithZeroObservers_CompletesWithinTimeBound()
    {
        using var recorder = new FlightRecorder(Opts(o => o.DefaultCapacity = 2000));
        const int iterations = 1000;

        // Warm-up: ensure buffer resolution is cached
        recorder.Record(HighwayEventType.RpcEnqueued, "perf.svc", payload: new byte[64]);

        var sw = Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++)
        {
            recorder.Record(HighwayEventType.RpcEnqueued, "perf.svc", requestId: $"r{i}", payload: new byte[64]);
        }
        sw.Stop();

        sw.ElapsedMilliseconds.Should().BeLessThan(50,
            "1000 records with zero observers should complete well within 50 ms; " +
            "exceeding this suggests a lock or allocation was introduced on the observer path");
    }
}
