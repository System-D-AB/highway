using FluentAssertions;
using Highway.Server;
using StackExchange.Redis;
using Xunit;

namespace Highway.Integration.Tests;

/// <summary>
/// Feature 004.1 Task 9 — Requirement 5: durability and restart survival.
/// These are the only tests permitted to touch disk (each uses a unique temp
/// data directory, cleaned up in Dispose). They prove 004 Requirement 13 and
/// Requirement 15 AC7, which had no coverage before 004.1.
/// </summary>
public class DurabilityTests : IDisposable
{
    private readonly string _dataDir =
        Path.Combine(Path.GetTempPath(), $"highway-dur-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dataDir))
                Directory.Delete(_dataDir, recursive: true);
        }
        catch
        {
            // Best-effort cleanup of temp AOF data
        }
    }

    private HighwayTestServer NewDurableServer() =>
        new(o => o.DataDir = _dataDir);

    [Fact]
    public void Restart_RecoversQueuedRequests_PublishedMessages_AndReplySlots()
    {
        using var server = NewDurableServer();
        var cs = server.ConnectionString;

        // Seed state: a queued RPC request, a published message to a group, a reply slot
        using (var redis = ConnectionMultiplexer.Connect(cs))
        {
            var db = redis.GetDatabase();
            db.Execute("HW.CALL", "dur.svc", "req-dur-1", "{\"n\":1}");
            db.Execute("HW.SUBSCRIBE", "dur.ch", "grp");
            db.Execute("HW.PUBLISH", "dur.ch", "msg-dur-1");
            db.Execute("HW.REPLY", "req-rep-1", "{\"ok\":true}");
        }

        server.Restart(); // dispose + rebuild on same port & data dir → AOF recovery

        using (var redis = ConnectionMultiplexer.Connect(cs))
        {
            var db = redis.GetDatabase();

            // Queued request survives, payload byte-identical
            var deq = db.Execute("HW.DEQUEUE", "dur.svc", "node-1");
            deq.IsNull.Should().BeFalse("queued RPC request must survive restart");
            var deqArr = (RedisResult[])deq!;
            ((string)deqArr[0]!).Should().Be("req-dur-1");
            ((string)deqArr[1]!).Should().Be("{\"n\":1}");

            // Published message survives for the pre-restart group
            var recv = db.Execute("HW.QCLAIM", "dur.ch@grp", "node-1");
            recv.IsNull.Should().BeFalse("published message must survive restart");
            ((string)((RedisResult[])recv!)[1]!).Should().Be("msg-dur-1");

            // Reply slot survives
            var reply = db.StringGet("hw:rep:req-rep-1");
            reply.HasValue.Should().BeTrue("reply slot must survive restart");
            ((string)reply!).Should().Be("{\"ok\":true}");
        }
    }

    [Fact]
    public void Restart_PreservesSubscriberGroups()
    {
        using var server = NewDurableServer();
        var cs = server.ConnectionString;

        using (var redis = ConnectionMultiplexer.Connect(cs))
        {
            redis.GetDatabase().Execute("HW.SUBSCRIBE", "grp.ch", "persistent-grp");
        }

        server.Restart();

        using (var redis = ConnectionMultiplexer.Connect(cs))
        {
            var db = redis.GetDatabase();
            // Publish AFTER restart without re-subscribing — the pre-restart group
            // must still be registered (004 Requirement 8 AC6).
            var count = (int)db.Execute("HW.PUBLISH", "grp.ch", "after-restart")!;
            count.Should().Be(1, "the pre-restart group must survive and receive");

            var recv = db.Execute("HW.QCLAIM", "grp.ch@persistent-grp", "node-1");
            recv.IsNull.Should().BeFalse();
            ((string)((RedisResult[])recv!)[1]!).Should().Be("after-restart");
        }
    }

    [Fact]
    public void Restart_MemoryOnly_LosesState_DocumentedExpectation()
    {
        using var server = new HighwayTestServer(); // no data dir → memory-only
        var cs = server.ConnectionString;

        using (var redis = ConnectionMultiplexer.Connect(cs))
        {
            redis.GetDatabase().Execute("HW.CALL", "mem.svc", "req-mem", "gone");
        }

        server.Restart();

        using (var redis = ConnectionMultiplexer.Connect(cs))
        {
            var result = redis.GetDatabase().Execute("HW.DEQUEUE", "mem.svc", "node-1");
            result.IsNull.Should().BeTrue(
                "memory-only state is intentionally lost across restart (004 Requirement 13 AC3)");
        }
    }

    [Fact]
    public void HighwayKeys_CoexistWithStockGarnetKeys()
    {
        using var server = NewDurableServer();
        var cs = server.ConnectionString;

        using (var redis = ConnectionMultiplexer.Connect(cs))
        {
            var db = redis.GetDatabase();

            // Stock Garnet key alongside Highway traffic
            db.StringSet("user:42", "plain-value");
            db.Execute("HW.CALL", "coexist.svc", "req-1", "hw-payload");

            // Neither disturbs the other
            ((string)db.StringGet("user:42")!).Should().Be("plain-value");
            var deq = db.Execute("HW.DEQUEUE", "coexist.svc", "node-1");
            deq.IsNull.Should().BeFalse();
            ((string)((RedisResult[])deq!)[1]!).Should().Be("hw-payload");

            // And the stock key is still intact after Highway work
            ((string)db.StringGet("user:42")!).Should().Be("plain-value");
        }
    }
}
