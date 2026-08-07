using FluentAssertions;
using Highway.Abstractions.Observability;
using Highway.Server.Observability;
using Xunit;
using static Highway.Server.Observability.FlightRecorder;

namespace Highway.Server.Tests;

/// <summary>
/// Feature 011, T2 — Observer subscribe/unsubscribe/notify/isolate.
/// </summary>
public class FlightRecorderObserverTests
{
    private static ObservabilityOptions Opts(Action<ObservabilityOptions>? tune = null)
    {
        var o = new ObservabilityOptions { SweepInterval = TimeSpan.FromHours(1) };
        tune?.Invoke(o);
        return o;
    }

    private sealed class CollectingObserver : IRecorderObserver
    {
        public List<HighwayEvent> Events { get; } = [];

        public void OnRecorded(in HighwayEvent evt)
        {
            Events.Add(evt);
        }
    }

    private sealed class ThrowingObserver : IRecorderObserver
    {
        public int CallCount;

        public void OnRecorded(in HighwayEvent evt)
        {
            Interlocked.Increment(ref CallCount);
            throw new InvalidOperationException("boom");
        }
    }

    [Fact]
    public void Observer_ReceivesRecordedEvents()
    {
        using var recorder = new FlightRecorder(Opts());
        var observer = new CollectingObserver();
        recorder.Subscribe(observer);

        recorder.Record(HighwayEventType.RpcEnqueued, "svc", requestId: "r1");
        recorder.Record(HighwayEventType.Published, "ch", messageId: 42);

        observer.Events.Should().HaveCount(2);
        observer.Events[0].RequestId.Should().Be("r1");
        observer.Events[1].MessageId.Should().Be(42);
    }

    [Fact]
    public void ThrowingObserver_IsCounted_AndDoesNotBlockOthers()
    {
        using var recorder = new FlightRecorder(Opts());
        var bad = new ThrowingObserver();
        var good = new CollectingObserver();

        recorder.Subscribe(bad);
        recorder.Subscribe(good);

        recorder.Record(HighwayEventType.RpcEnqueued, "svc");

        bad.CallCount.Should().Be(1, "bad observer was called");
        good.Events.Should().HaveCount(1, "good observer is not blocked by a throwing one");
        recorder.ObserverFailures.Should().Be(1);
        recorder.Snapshot().ObserverFailures.Should().Be(1);
    }

    [Fact]
    public void Unsubscribe_StopsDelivery()
    {
        using var recorder = new FlightRecorder(Opts());
        var observer = new CollectingObserver();
        recorder.Subscribe(observer);

        recorder.Record(HighwayEventType.RpcEnqueued, "svc");
        observer.Events.Should().HaveCount(1);

        recorder.Unsubscribe(observer);

        recorder.Record(HighwayEventType.RpcEnqueued, "svc");
        observer.Events.Should().HaveCount(1, "no more events after unsubscribe");
    }

    [Fact]
    public void MultipleObservers_AllNotified()
    {
        using var recorder = new FlightRecorder(Opts());
        var a = new CollectingObserver();
        var b = new CollectingObserver();
        recorder.Subscribe(a);
        recorder.Subscribe(b);

        recorder.Record(HighwayEventType.RpcEnqueued, "svc");

        a.Events.Should().HaveCount(1);
        b.Events.Should().HaveCount(1);
    }

    [Fact]
    public void ThrowingObserver_DoesNotPreventLaterObservers()
    {
        using var recorder = new FlightRecorder(Opts());
        var bad = new ThrowingObserver();
        var after = new CollectingObserver();

        recorder.Subscribe(bad);
        recorder.Subscribe(after);

        recorder.Record(HighwayEventType.RpcEnqueued, "svc");
        recorder.Record(HighwayEventType.RpcEnqueued, "svc");

        after.Events.Should().HaveCount(2,
            "an observer that throws must not prevent later observers in the array from being notified");
        recorder.ObserverFailures.Should().Be(2);
    }

    [Fact]
    public void NoObservers_RecordDoesNoExtraAllocation()
    {
        // This test is structural: with no observers, the array read is a volatile
        // read and the length check short-circuits. We verify by ensuring Record
        // still works correctly with zero observers (no exceptions, no side effects).
        using var recorder = new FlightRecorder(Opts());

        recorder.Record(HighwayEventType.RpcEnqueued, "svc", requestId: "r1");

        var events = recorder.Read("svc", DateTimeOffset.MinValue, DateTimeOffset.MaxValue, null, 100);
        events.Should().HaveCount(1);
        recorder.ObserverFailures.Should().Be(0);
    }
}
