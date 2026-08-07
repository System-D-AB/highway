using FluentAssertions;
using Highway.Abstractions.Observability;
using Highway.Server.Observability;
using Xunit;

namespace Highway.Server.Tests;

/// <summary>
/// Feature 002 — the recorder's guarantees. Two of these protect properties that
/// only break under conditions ordinary use never reaches.
/// </summary>
public class FlightRecorderTests
{
    private static ObservabilityOptions Opts(Action<ObservabilityOptions>? tune = null)
    {
        var o = new ObservabilityOptions { SweepInterval = TimeSpan.FromHours(1) };
        tune?.Invoke(o);
        return o;
    }

    private static IReadOnlyList<HighwayEvent> All(FlightRecorder r, string name)
        => r.Read(name, DateTimeOffset.MinValue, DateTimeOffset.MaxValue, null, 10_000);

    [Fact]
    public void Record_ThenRead_RoundTrips()
    {
        using var recorder = new FlightRecorder(Opts());

        recorder.Record(HighwayEventType.RpcEnqueued, "svc", requestId: "r1", payload: [1, 2, 3]);

        var evt = All(recorder, "svc").Should().ContainSingle().Subject;
        evt.RequestId.Should().Be("r1");
        evt.PayloadSize.Should().Be(3);
        evt.Payload.Should().NotBeNull();
    }

    /// <summary>
    /// The reason buffers are per-name. A chatty health check must not be able to
    /// evict the history of the service you are actually debugging.
    /// </summary>
    [Fact]
    public void OneNameFlooding_DoesNotEvictAnother()
    {
        using var recorder = new FlightRecorder(Opts(o => o.DefaultCapacity = 10));

        recorder.Record(HighwayEventType.RpcEnqueued, "quiet.svc", requestId: "important");
        for (var i = 0; i < 5_000; i++)
            recorder.Record(HighwayEventType.RpcEnqueued, "noisy.svc", requestId: $"n{i}");

        All(recorder, "quiet.svc").Should().ContainSingle()
            .Which.RequestId.Should().Be("important",
                "per-name buffers isolate one name's pressure from another's history");
        All(recorder, "noisy.svc").Should().HaveCount(10);
    }

    [Fact]
    public void CaptureHeadersOnly_KeepsSizeButNotContent()
    {
        using var recorder = new FlightRecorder(Opts(o =>
            o.Overrides["svc"] = new NameRecorderOptions { Capture = PayloadCapture.HeadersOnly }));

        recorder.Record(HighwayEventType.RpcEnqueued, "svc", payload: new byte[512]);

        var evt = All(recorder, "svc").Should().ContainSingle().Subject;
        evt.Payload.Should().BeNull("content must not be retained");
        evt.PayloadSize.Should().Be(512, "size stays visible so throughput is still observable");
    }

    [Fact]
    public void CaptureOff_RecordsNothing_AndAllocatesNoBuffer()
    {
        using var recorder = new FlightRecorder(Opts(o =>
            o.Overrides["svc"] = new NameRecorderOptions { Capture = PayloadCapture.Off }));

        recorder.Record(HighwayEventType.RpcEnqueued, "svc", payload: [1, 2, 3]);

        All(recorder, "svc").Should().BeEmpty();
        recorder.Snapshot().Names.Should().Be(0, "a disabled name never gets a buffer");
    }

    [Fact]
    public void ZeroCapacity_DisablesTheName()
    {
        using var recorder = new FlightRecorder(Opts(o =>
            o.Overrides["svc"] = new NameRecorderOptions { Capacity = 0 }));

        recorder.Record(HighwayEventType.RpcEnqueued, "svc");

        All(recorder, "svc").Should().BeEmpty();
    }

    [Fact]
    public void Disabled_RecordsNothing_ButStillAnswers()
    {
        using var recorder = new FlightRecorder(Opts(o => o.RecorderEnabled = false));

        recorder.Record(HighwayEventType.RpcEnqueued, "svc");

        All(recorder, "svc").Should().BeEmpty();
        recorder.Snapshot().Enabled.Should().BeFalse("metrics report the disabled state rather than erroring");
    }

    /// <summary>
    /// The guarantee that matters most: a flight recorder that can break the
    /// system it observes is worse than none.
    /// </summary>
    [Fact]
    public void Record_WhenSomethingThrows_IsSwallowedAndCounted()
    {
        using var recorder = new FlightRecorder(Opts());

        // A null name forces an exception inside Record's own path.
        var act = () => recorder.Record(HighwayEventType.RpcEnqueued, null!);

        act.Should().NotThrow("recording must never fail the operation being recorded");
        recorder.Failures.Should().BeGreaterThan(0, "the failure is counted, not silently ignored");
    }

    [Fact]
    public void Read_UnknownName_ReturnsEmpty()
        => new FlightRecorder(Opts()).Read("never.seen", DateTimeOffset.MinValue, DateTimeOffset.MaxValue, null, 10)
            .Should().BeEmpty();

    [Fact]
    public void Snapshot_CountsNamesEventsAndBytes()
    {
        using var recorder = new FlightRecorder(Opts());
        recorder.Record(HighwayEventType.RpcEnqueued, "a", payload: new byte[100]);
        recorder.Record(HighwayEventType.RpcEnqueued, "b", payload: new byte[100]);

        var snapshot = recorder.Snapshot();

        snapshot.Names.Should().Be(2);
        snapshot.Events.Should().Be(2);
        snapshot.Bytes.Should().BeGreaterThan(200);
    }

    [Fact]
    public void Sweep_EnforcesTheGlobalBudget()
    {
        using var recorder = new FlightRecorder(Opts(o =>
        {
            o.DefaultCapacity = 1000;
            o.MaxBytes = 50_000;
        }));

        for (var i = 0; i < 200; i++)
            recorder.Record(HighwayEventType.RpcEnqueued, "svc", payload: new byte[1000]);

        recorder.Snapshot().Bytes.Should().BeGreaterThan(50_000, "over budget before the sweep");

        recorder.Sweep();

        recorder.Snapshot().Bytes.Should().BeLessThan(200_000, "the sweep reclaimed toward the budget");
        recorder.DroppedBudget.Should().BeGreaterThan(0, "budget reclamation is distinguishable from capacity drops");
    }

    [Fact]
    public void Options_Validate_RejectsBadValuesNamingThem()
    {
        var act = () => new ObservabilityOptions { MaxBytes = 0 }.Validate();

        act.Should().Throw<InvalidOperationException>().WithMessage("*MaxBytes*");
    }
}
