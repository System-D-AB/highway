using FluentAssertions;
using Highway.Abstractions.Observability;
using Highway.Server.Observability;
using Xunit;

namespace Highway.Server.Tests;

/// <summary>
/// Feature 011, T1 — <see cref="FlightRecorder.Names"/> enumeration.
/// </summary>
public class FlightRecorderNamesTests
{
    private static ObservabilityOptions Opts(Action<ObservabilityOptions>? tune = null)
    {
        var o = new ObservabilityOptions { SweepInterval = TimeSpan.FromHours(1) };
        tune?.Invoke(o);
        return o;
    }

    [Fact]
    public void Names_Empty_ReturnsEmptyList()
    {
        using var recorder = new FlightRecorder(Opts());

        var names = recorder.Names();

        names.Should().BeEmpty();
    }

    [Fact]
    public void Names_PopulatedBuffers_ReturnsCorrectEntries()
    {
        using var recorder = new FlightRecorder(Opts());

        recorder.Record(HighwayEventType.RpcEnqueued, "alpha", payload: new byte[100]);
        recorder.Record(HighwayEventType.RpcEnqueued, "alpha", payload: new byte[200]);
        recorder.Record(HighwayEventType.Published, "beta", payload: new byte[50]);

        var names = recorder.Names();

        names.Should().HaveCount(2);

        var alpha = names.Should().ContainSingle(n => n.Name == "alpha").Subject;
        alpha.Count.Should().Be(2);
        alpha.Bytes.Should().BeGreaterThan(0);
        alpha.Capture.Should().Be(PayloadCapture.Full);
        alpha.DroppedCapacity.Should().Be(0);

        var beta = names.Should().ContainSingle(n => n.Name == "beta").Subject;
        beta.Count.Should().Be(1);
        beta.Bytes.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Names_DisabledName_IsAbsent()
    {
        using var recorder = new FlightRecorder(Opts(o =>
            o.Overrides["disabled"] = new NameRecorderOptions { Capture = PayloadCapture.Off }));

        recorder.Record(HighwayEventType.RpcEnqueued, "disabled");
        recorder.Record(HighwayEventType.RpcEnqueued, "enabled");

        var names = recorder.Names();

        names.Should().ContainSingle().Which.Name.Should().Be("enabled");
    }

    [Fact]
    public void Names_DroppedCapacity_Tracked()
    {
        using var recorder = new FlightRecorder(Opts(o => o.DefaultCapacity = 2));

        recorder.Record(HighwayEventType.RpcEnqueued, "svc");
        recorder.Record(HighwayEventType.RpcEnqueued, "svc");
        recorder.Record(HighwayEventType.RpcEnqueued, "svc"); // overwrites oldest

        var names = recorder.Names();
        var entry = names.Should().ContainSingle().Subject;
        entry.DroppedCapacity.Should().Be(1, "one event was displaced when buffer was full");
    }

    [Fact]
    public void Names_CaptureMode_ReportedCorrectly()
    {
        using var recorder = new FlightRecorder(Opts(o =>
            o.Overrides["headers"] = new NameRecorderOptions { Capture = PayloadCapture.HeadersOnly }));

        recorder.Record(HighwayEventType.RpcEnqueued, "headers", payload: new byte[100]);

        var names = recorder.Names();
        names.Should().ContainSingle().Which.Capture.Should().Be(PayloadCapture.HeadersOnly);
    }
}
