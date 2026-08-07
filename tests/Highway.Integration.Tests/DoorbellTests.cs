using FluentAssertions;
using Highway.Server;
using StackExchange.Redis;
using Xunit;

namespace Highway.Integration.Tests;

/// <summary>
/// Feature 004.1 Task 11 — Requirement 7 AC5–AC7: doorbell regression tests.
/// The doorbell mechanism is what feature 005's entire latency design rests on;
/// these tests promote review-time probes to permanent coverage so it cannot
/// regress silently. Also covers the HW.RECEIVE reply shape 005 parses against.
/// </summary>
public class DoorbellTests
{
    private sealed class RingCollector : IDisposable
    {
        private readonly ConnectionMultiplexer _redis;
        public List<string> Rings { get; } = [];
        private readonly SemaphoreSlim _gate = new(0);

        public RingCollector(string connectionString)
        {
            _redis = ConnectionMultiplexer.Connect(connectionString);
        }

        public async Task SubscribeAsync(string channel)
        {
            await _redis.GetSubscriber().SubscribeAsync(RedisChannel.Literal(channel), (_, value) =>
            {
                lock (Rings) Rings.Add((string)value!);
                _gate.Release();
            });
        }

        public async Task<bool> WaitForRingAsync(TimeSpan timeout)
            => await _gate.WaitAsync(timeout);

        public int Count
        {
            get { lock (Rings) return Rings.Count; }
        }

        public string Last
        {
            get { lock (Rings) return Rings[^1]; }
        }

        public void Dispose() => _redis.Dispose();
    }

    [Fact]
    public async Task HwCall_RingsServiceDoorbell_WithRequestId()
    {
        using var server = new HighwayTestServer();
        using var collector = new RingCollector(server.ConnectionString);
        await collector.SubscribeAsync("hw:door:svc:ring.svc");

        using var redis = ConnectionMultiplexer.Connect(server.ConnectionString);
        redis.GetDatabase().Execute("HW.CALL", "ring.svc", "req-ring-1", "payload");

        (await collector.WaitForRingAsync(TimeSpan.FromSeconds(5)))
            .Should().BeTrue("HW.CALL must ring the service doorbell");
        collector.Last.Should().Be("req-ring-1");
    }

    [Fact]
    public async Task HwReply_RingsReplyDoorbell_WithRequestId()
    {
        using var server = new HighwayTestServer();
        using var collector = new RingCollector(server.ConnectionString);
        await collector.SubscribeAsync("hw:door:rep");

        using var redis = ConnectionMultiplexer.Connect(server.ConnectionString);
        redis.GetDatabase().Execute("HW.REPLY", "req-ring-2", "response");

        (await collector.WaitForRingAsync(TimeSpan.FromSeconds(5)))
            .Should().BeTrue("HW.REPLY must ring the reply doorbell");
        collector.Last.Should().Be("req-ring-2");
    }

    [Fact]
    public async Task HwPublish_RingsGroupDoorbell_OncePerGroup()
    {
        using var server = new HighwayTestServer();
        using var collectorX = new RingCollector(server.ConnectionString);
        using var collectorY = new RingCollector(server.ConnectionString);
        await collectorX.SubscribeAsync("hw:door:ch:ring.ch:grp:grpX");
        await collectorY.SubscribeAsync("hw:door:ch:ring.ch:grp:grpY");

        using var redis = ConnectionMultiplexer.Connect(server.ConnectionString);
        var db = redis.GetDatabase();
        db.Execute("HW.SUBSCRIBE", "ring.ch", "grpX");
        db.Execute("HW.SUBSCRIBE", "ring.ch", "grpY");
        db.Execute("HW.PUBLISH", "ring.ch", "fan-out-msg");

        (await collectorX.WaitForRingAsync(TimeSpan.FromSeconds(5)))
            .Should().BeTrue("every group's doorbell must ring");
        (await collectorY.WaitForRingAsync(TimeSpan.FromSeconds(5)))
            .Should().BeTrue("every group's doorbell must ring");

        // Payload is the messageId assigned by the server (decimal long)
        long.Parse(collectorX.Last).Should().BePositive();
        collectorX.Last.Should().Be(collectorY.Last, "both groups are notified about the same message");
    }

    [Fact]
    public async Task RejectedCommand_RingsNothing()
    {
        using var server = new HighwayTestServer();
        using var collector = new RingCollector(server.ConnectionString);
        await collector.SubscribeAsync("hw:door:svc:guard.svc");

        using var redis = ConnectionMultiplexer.Connect(server.ConnectionString);
        var act = () => redis.GetDatabase().Execute("HW.CALL", "guard.svc", "", "payload");
        act.Should().Throw<RedisServerException>();

        // Give any (incorrect) ring time to arrive
        var rang = await collector.WaitForRingAsync(TimeSpan.FromMilliseconds(500));
        rang.Should().BeFalse("the Finalize Failed-guard must prevent doorbells for rejected commands");
    }

    [Fact]
    public async Task Restart_NoExtraDoorbells_FromAofReplay()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), $"highway-door-{Guid.NewGuid():N}");
        try
        {
            using var server = new HighwayTestServer(o => o.DataDir = dataDir);

            // SE.Redis auto-reconnects and resubscribes across the restart.
            using var collector = new RingCollector(server.ConnectionString);
            await collector.SubscribeAsync("hw:door:svc:replay.svc");

            using (var redis = ConnectionMultiplexer.Connect(server.ConnectionString))
            {
                redis.GetDatabase().Execute("HW.CALL", "replay.svc", "req-live", "payload");
            }

            (await collector.WaitForRingAsync(TimeSpan.FromSeconds(5)))
                .Should().BeTrue("the live HW.CALL rings once");
            collector.Count.Should().Be(1);

            server.Restart(); // AOF replay re-executes HW.CALL — Finalize is skipped

            // Wait generously; if replay re-rang the doorbell and the subscriber
            // reconnected in time, the count would grow. (Inherently limited: a
            // replay ring fired before resubscription is unobservable from outside —
            // the code-level guarantee is Finalize being skipped during replay.)
            await Task.Delay(TimeSpan.FromSeconds(2));
            var extra = await collector.WaitForRingAsync(TimeSpan.FromSeconds(1));
            extra.Should().BeFalse("no doorbell may arrive after the single live ring");
            collector.Count.Should().Be(1, "AOF replay must not re-ring doorbells");
        }
        finally
        {
            try { Directory.Delete(dataDir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void HwReceive_ReplyShape_ArrayOfTwoElementPairs()
    {
        using var server = new HighwayTestServer();
        using var redis = ConnectionMultiplexer.Connect(server.ConnectionString);
        var db = redis.GetDatabase();

        db.Execute("HW.SUBSCRIBE", "shape.ch", "grp");
        db.Execute("HW.PUBLISH", "shape.ch", "payload-a");
        db.Execute("HW.PUBLISH", "shape.ch", "payload-b");

        var result = db.Execute("HW.RECEIVE", "shape.ch", "grp", "COUNT", "10");

        // Contract 005 parses against: *N of *2 [messageId, payload]
        var outer = (RedisResult[])result!;
        outer.Should().HaveCount(2);

        foreach (var item in outer)
        {
            var pair = (RedisResult[])item!;
            pair.Should().HaveCount(2);
            long.Parse((string)pair[0]!).Should().BePositive("messageId is a decimal long");
            ((string)pair[1]!).Should().StartWith("payload-");
        }
    }
}
