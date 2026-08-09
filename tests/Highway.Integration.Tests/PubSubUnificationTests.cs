using FluentAssertions;
using Highway.Server;
using StackExchange.Redis;
using Xunit;

namespace Highway.Integration.Tests;

/// <summary>
/// Feature 018, Phase 4 — tests that did not exist before the unification.
///
/// <para>These verify the new semantics, the startup check, and the
/// collision guard from both directions.</para>
/// </summary>
public class PubSubUnificationTests : IDisposable
{
    private readonly HighwayTestServer _server = new();
    private readonly ConnectionMultiplexer _redis;
    private readonly IDatabase _db;

    public PubSubUnificationTests()
    {
        _redis = ConnectionMultiplexer.Connect(_server.ConnectionString);
        _db = _redis.GetDatabase();
    }

    public void Dispose()
    {
        _redis.Dispose();
        _server.Dispose();
    }

    // -------------------------------------------------------------------------
    // T15.1 — PublishReachesEveryRegisteredGroup_OrNone
    // -------------------------------------------------------------------------

    [Fact]
    public void PublishReachesEveryRegisteredGroup_OrNone()
    {
        _db.Execute("HW.SUBSCRIBE", "orders.placed", "billing");
        _db.Execute("HW.SUBSCRIBE", "orders.placed", "shipping");

        var groups = (int)_db.Execute("HW.PUBLISH", "orders.placed", "payload-abc"u8.ToArray());
        groups.Should().Be(2, "two groups are registered");

        // Claim from both derived queues
        var r1 = _db.Execute("HW.QCLAIM", "orders.placed@billing", "node-1");
        var r2 = _db.Execute("HW.QCLAIM", "orders.placed@shipping", "node-1");

        r1.IsNull.Should().BeFalse("billing group should receive the message");
        r2.IsNull.Should().BeFalse("shipping group should receive the message");

        var payload1 = (string)((RedisResult[])r1!)[1]!;
        var payload2 = (string)((RedisResult[])r2!)[1]!;
        payload1.Should().Be(payload2, "both groups receive identical payloads");
    }

    // -------------------------------------------------------------------------
    // T15.2 — GroupRegisteredDuringDelay_DoesNotReceive (R5.3)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GroupRegisteredDuringDelay_DoesNotReceive()
    {
        _db.Execute("HW.SUBSCRIBE", "deferred.ch", "early-group");

        var deliveryTime = (DateTime.UtcNow + TimeSpan.FromMilliseconds(500)).Ticks;
        _db.Execute("HW.PUBLISH", "deferred.ch", "deferred-msg"u8.ToArray(),
            "AT", deliveryTime.ToString());

        // Subscribe a group DURING the delay
        _db.Execute("HW.SUBSCRIBE", "deferred.ch", "late-group");

        // Wait for delivery time to pass
        await Task.Delay(700);

        // Early group should get it after promotion
        var early = _db.Execute("HW.QCLAIM", "deferred.ch@early-group", "node-1");
        early.IsNull.Should().BeFalse("group registered before publish receives the delayed message");

        // Late group should NOT get it — R5.3 semantic change
        var late = _db.Execute("HW.QCLAIM", "deferred.ch@late-group", "node-1");
        late.IsNull.Should().BeTrue(
            "a group registered during the delay does NOT receive the message (018 R5.3)");
    }

    // -------------------------------------------------------------------------
    // T15.3 — SubscriberGroupProcessesInOrder_ByDefault (R5.2)
    // -------------------------------------------------------------------------

    [Fact]
    public void SubscriberGroupProcessesInOrder_ByDefault()
    {
        const string channel = "ordering.ch";
        const string group = "ordered-grp";
        var derivedQueue = $"{channel}@{group}";

        _db.Execute("HW.SUBSCRIBE", channel, group);

        // Publish 5 messages
        for (var i = 1; i <= 5; i++)
            _db.Execute("HW.PUBLISH", channel, $"msg-{i}");

        // Claim them in order from the derived queue
        var payloads = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            var result = _db.Execute("HW.QCLAIM", derivedQueue, "node-1");
            if (result.IsNull) break;
            var arr = (RedisResult[])result!;
            payloads.Add((string)arr[1]!);
            var msgId = (string)arr[0]!;
            _db.Execute("HW.QACK", derivedQueue, "node-1", msgId);
        }

        payloads.Should().Equal(["msg-1", "msg-2", "msg-3", "msg-4", "msg-5"],
            "messages are claimed in FIFO order — subscriber ordering is preserved by default");
    }

    // -------------------------------------------------------------------------
    // T15.4 — QueueNamedLikeAGroup_IsRejected
    // -------------------------------------------------------------------------

    [Fact]
    public void QueueNamedLikeAGroup_IsRejected()
    {
        // HW.QSEND with @ in the name is rejected
        var act = () => _db.Execute("HW.QSEND", "orders@billing", "m-1", "body"u8.ToArray());
        act.Should().Throw<RedisServerException>()
            .WithMessage("*HW_INVALID_ARG*@*");
    }

    // -------------------------------------------------------------------------
    // T15.5 — PreUnificationChannelData_RefusesStartup (T10)
    // -------------------------------------------------------------------------

    [Fact]
    public void PreUnificationChannelData_RefusesStartup()
    {
        // Write an old-pattern key to the running test server
        _db.Execute("SET", "hw:ch:legacy.channel:grp:old-group:q", "fake-data");

        // Now start a NEW server against the same data: it should refuse.
        // Since HighwayTestServer is memory-only and we can't share state, we test
        // by writing the key directly then verifying the scan finds it.
        // The actual startup check lives in HighwayServer.Start() — we verify it
        // by checking that the key pattern exists.
        var server = _redis.GetServers()[0];
        var found = false;
        foreach (var key in server.Keys(pattern: "hw:ch:*:grp:*", pageSize: 100))
        {
            found = true;
            break;
        }

        found.Should().BeTrue("we just wrote a pre-018 key pattern");

        // Now verify the startup check mechanism: start a second server sharing data
        // (use a temp data dir). Write the old key, then attempt to start a server
        // against that data.
        var dataDir = Path.Combine(Path.GetTempPath(), $"hw-018-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDir);

        try
        {
            // First: start a server with the data dir and write the old key
            using var setup = new HighwayTestServer(o => o.DataDir = dataDir);
            var setupRedis = ConnectionMultiplexer.Connect(setup.ConnectionString);
            var setupDb = setupRedis.GetDatabase();
            setupDb.Execute("SET", "hw:ch:old.events:grp:dead-group:q", "stale-data");
            setupRedis.Dispose();

            // Restart (simulates starting against pre-018 data)
            var ex = Assert.Throws<InvalidOperationException>(() => setup.Restart());
            ex.Message.Should().Contain("hw:ch:*:grp:*");
            ex.Message.Should().Contain("018");
        }
        finally
        {
            try { Directory.Delete(dataDir, recursive: true); } catch { }
        }
    }

    // -------------------------------------------------------------------------
    // T15.6 — SubscriberFailure_DeadLettersWithContext
    //
    // Moved to SubscriberFailureTests, and rewritten. The version that lived here drove
    // HW.FAIL directly over RESP and never invoked a handler, so it passed while the
    // executor was still swallowing every subscriber exception. It tested the server
    // plumbing feature 015 already covered, under a name that claimed otherwise.
    // -------------------------------------------------------------------------

    // -------------------------------------------------------------------------
    // T15 supplemental — PublishStillReportsGroupCount
    // -------------------------------------------------------------------------

    [Fact]
    public void PublishStillReportsGroupCount()
    {
        _db.Execute("HW.SUBSCRIBE", "count.ch", "g1");
        _db.Execute("HW.SUBSCRIBE", "count.ch", "g2");
        _db.Execute("HW.SUBSCRIBE", "count.ch", "g3");

        var count = (int)_db.Execute("HW.PUBLISH", "count.ch", "counted"u8.ToArray());
        count.Should().Be(3, "PublishAsync still reports how many groups received the message");
    }
}
