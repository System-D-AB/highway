using FluentAssertions;
using Highway.Abstractions.Observability;
using Highway.Server.Observability;
using Xunit;

namespace Highway.Server.Tests;

/// <summary>
/// Feature 002 — one name's bounded history. The eviction paths matter most:
/// they only run under pressure, which is exactly when nobody is watching.
/// </summary>
public class NameBufferTests
{
    private static NameBuffer New(int capacity = 4, TimeSpan? retention = null,
        PayloadCapture capture = PayloadCapture.Full)
        => new("svc", capacity, retention ?? TimeSpan.FromHours(1), capture);

    private static HighwayEvent Event(DateTimeOffset at, string? node = null, byte[]? payload = null)
        => new()
        {
            Timestamp = at,
            EventType = HighwayEventType.RpcEnqueued,
            Name = "svc",
            NodeId = node,
            Payload = payload,
            PayloadSize = payload?.Length ?? 0,
        };

    private static readonly DateTimeOffset T0 = new(2026, 8, 7, 12, 0, 0, TimeSpan.Zero);

    private IReadOnlyList<HighwayEvent> ReadAll(NameBuffer b, DateTimeOffset now)
        => b.Read(DateTimeOffset.MinValue, DateTimeOffset.MaxValue, null, 1000, now);

    [Fact]
    public void Read_ReturnsEventsInChronologicalOrder()
    {
        var buffer = New();
        for (var i = 0; i < 3; i++)
            buffer.Append(Event(T0.AddSeconds(i)));

        var events = ReadAll(buffer, T0.AddMinutes(1));

        events.Select(e => e.Timestamp).Should().BeInAscendingOrder();
        events.Should().HaveCount(3);
    }

    [Fact]
    public void Append_BeyondCapacity_DropsOldestAndCounts()
    {
        var buffer = New(capacity: 3);
        for (var i = 0; i < 5; i++)
            buffer.Append(Event(T0.AddSeconds(i)));

        var events = ReadAll(buffer, T0.AddMinutes(1));

        events.Should().HaveCount(3, "capacity bounds the buffer");
        events[0].Timestamp.Should().Be(T0.AddSeconds(2), "the two oldest were dropped");
        buffer.DroppedCapacity.Should().Be(2);
    }

    [Fact]
    public void Read_ExcludesEventsPastRetention_EvenBeforeTheSweepRuns()
    {
        // Retention is correctness at READ. A stale event must never surface
        // merely because the sweeper has not got to it yet.
        var buffer = New(capacity: 10, retention: TimeSpan.FromMinutes(5));
        buffer.Append(Event(T0));                    // old
        buffer.Append(Event(T0.AddMinutes(9)));      // recent

        var events = ReadAll(buffer, now: T0.AddMinutes(10));

        events.Should().ContainSingle().Which.Timestamp.Should().Be(T0.AddMinutes(9));
    }

    [Fact]
    public void Read_FiltersByTimeWindow()
    {
        var buffer = New(capacity: 10);
        for (var i = 0; i < 5; i++)
            buffer.Append(Event(T0.AddSeconds(i)));

        var events = buffer.Read(T0.AddSeconds(1), T0.AddSeconds(3), null, 100, T0.AddMinutes(1));

        events.Should().HaveCount(3);
    }

    [Fact]
    public void Read_FiltersByNode()
    {
        var buffer = New(capacity: 10);
        buffer.Append(Event(T0, node: "a"));
        buffer.Append(Event(T0.AddSeconds(1), node: "b"));

        var events = buffer.Read(DateTimeOffset.MinValue, DateTimeOffset.MaxValue, "b", 100, T0.AddMinutes(1));

        events.Should().ContainSingle().Which.NodeId.Should().Be("b");
    }

    [Fact]
    public void Read_HonoursLimit()
    {
        var buffer = New(capacity: 10);
        for (var i = 0; i < 8; i++)
            buffer.Append(Event(T0.AddSeconds(i)));

        buffer.Read(DateTimeOffset.MinValue, DateTimeOffset.MaxValue, null, 3, T0.AddMinutes(1))
            .Should().HaveCount(3);
    }

    [Fact]
    public void Bytes_TrackPayloadSize()
    {
        var buffer = New(capacity: 10);
        var before = buffer.Bytes;

        buffer.Append(Event(T0, payload: new byte[1000]));

        buffer.Bytes.Should().BeGreaterThan(before + 900, "the payload dominates the retained size");
    }

    [Fact]
    public void SweepExpired_ReclaimsAndReportsBytes()
    {
        var buffer = New(capacity: 10, retention: TimeSpan.FromMinutes(1));
        buffer.Append(Event(T0, payload: new byte[500]));

        var reclaimed = buffer.SweepExpired(T0.AddMinutes(5));

        reclaimed.Should().BeGreaterThan(0);
        buffer.Count.Should().Be(0);
    }

    [Fact]
    public void TrimTo_ReducesToTheTarget()
    {
        var buffer = New(capacity: 10);
        for (var i = 0; i < 5; i++)
            buffer.Append(Event(T0.AddSeconds(i), payload: new byte[1000]));

        var before = buffer.Bytes;
        buffer.TrimTo(before / 2);

        buffer.Bytes.Should().BeLessThanOrEqualTo(before / 2);
    }

    [Fact]
    public void Append_IsSafeUnderConcurrency()
    {
        var buffer = New(capacity: 500);

        Parallel.For(0, 2000, i => buffer.Append(Event(T0.AddMilliseconds(i))));

        buffer.Count.Should().Be(500, "the ring stays bounded no matter how many writers");
        buffer.DroppedCapacity.Should().Be(1500);
    }
}
