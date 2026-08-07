using FluentAssertions;
using Highway.Server;
using StackExchange.Redis;
using Xunit;

namespace Highway.Integration.Tests;

/// <summary>
/// Feature 006 Task 12 — what happens when a node stops beating.
///
/// <para>Two of these carry guarantees that only break in production if left
/// untested: a dead node's unacknowledged RPC work must be recovered, and its
/// subscriber group must survive. The second is the one a future change is most
/// likely to break, because deleting a departed node's state looks like tidy
/// housekeeping right up until it silently downgrades durable pub/sub to
/// fire-and-forget.</para>
/// </summary>
public class NodeExpiryTests
{
    private const string Catalog =
        """{"services":[{"name":"orders.create","requestType":"R","responseType":"S"}],"channels":[{"name":"orders.placed","subscriberCount":1}]}""";

    /// <summary>Expiry short enough to observe, long enough not to race the test itself.</summary>
    private static readonly TimeSpan Expiry = TimeSpan.FromMilliseconds(400);

    private static HighwayTestServer NewServer(bool pruning = true)
        => new(o =>
        {
            o.NodeExpiry = Expiry;
            o.PruningEnabled = pruning;
            o.Lease = TimeSpan.FromMinutes(5); // long, so any recovery seen is the node sweep
        });

    private static async Task WaitForExpiryAsync()
        => await Task.Delay(Expiry + TimeSpan.FromMilliseconds(250));

    private static string[] Discover(IDatabase db, string service)
    {
        var result = (RedisResult[])db.Execute("HW.DISCOVER", service)!;
        return [.. result.Select(r => (string)((RedisResult[])r!)[0]!)];
    }

    [Fact]
    public async Task StaleNode_DisappearsFromDiscovery()
    {
        using var server = NewServer();
        using var redis = ConnectionMultiplexer.Connect(server.ConnectionString);
        var db = redis.GetDatabase();

        db.Execute("HW.HEARTBEAT", "node-1", Catalog);
        Discover(db, "orders.create").Should().Equal("node-1");

        await WaitForExpiryAsync();

        Discover(db, "orders.create").Should().BeEmpty(
            "a node that stops beating must stop being routable");
    }

    [Fact]
    public async Task StaleNode_BeatsAgain_ReappearsAfterReRegistering()
    {
        using var server = NewServer();
        using var redis = ConnectionMultiplexer.Connect(server.ConnectionString);
        var db = redis.GetDatabase();

        db.Execute("HW.HEARTBEAT", "node-1", Catalog);
        await WaitForExpiryAsync();
        Discover(db, "orders.create").Should().BeEmpty();

        // The record still exists (nothing pruned it yet), so a liveness beat
        // is enough to bring it back.
        ((string)db.Execute("HW.HEARTBEAT", "node-1")!).Should().Be("OK");

        Discover(db, "orders.create").Should().Equal("node-1");
    }

    /// <summary>
    /// Requirement 4 AC4 — non-skippable. At-least-once has to survive a node
    /// dying, not only a lease expiring.
    /// </summary>
    [Fact]
    public async Task DeadNode_UnacknowledgedRequests_AreRequeuedNotLost()
    {
        using var server = NewServer();
        using var redis = ConnectionMultiplexer.Connect(server.ConnectionString);
        var db = redis.GetDatabase();

        db.Execute("HW.HEARTBEAT", "worker-1", Catalog);
        db.Execute("HW.CALL", "orders.create", "req-1", "payload-1");

        // worker-1 claims the request and then dies without acking.
        var claimed = (RedisResult[])db.Execute("HW.DEQUEUE", "orders.create", "worker-1")!;
        ((string)claimed[0]!).Should().Be("req-1");

        await WaitForExpiryAsync();

        // A different worker dequeues: the dead node's claim is swept and its
        // work returned to the queue. The lease is 5 minutes, so this can only
        // be the node sweep.
        db.Execute("HW.HEARTBEAT", "worker-2", Catalog);
        var recovered = db.Execute("HW.DEQUEUE", "orders.create", "worker-2")!;

        recovered.IsNull.Should().BeFalse("a dead node's in-flight work must come back");
        var pair = (RedisResult[])recovered!;
        ((string)pair[0]!).Should().Be("req-1");
        ((string)pair[1]!).Should().Be("payload-1", "the payload must survive the round trip intact");
    }

    /// <summary>
    /// Requirement 4 AC5 — non-skippable, and the invariant most at risk from a
    /// well-meaning future cleanup.
    /// </summary>
    [Fact]
    public async Task DeadNode_SubscriberGroupAndPendingMessages_Survive()
    {
        using var server = NewServer();
        using var redis = ConnectionMultiplexer.Connect(server.ConnectionString);
        var db = redis.GetDatabase();

        db.Execute("HW.HEARTBEAT", "sub-1", Catalog);
        db.Execute("HW.SUBSCRIBE", "orders.placed", "sub-1");

        // Published while the node is still alive but not consuming.
        db.Execute("HW.PUBLISH", "orders.placed", "msg-before");

        await WaitForExpiryAsync();

        // Drive a dequeue so the node sweep definitely runs and prunes sub-1.
        db.Execute("HW.HEARTBEAT", "worker-2", Catalog);
        db.Execute("HW.DEQUEUE", "orders.create", "worker-2");
        Discover(db, "orders.create").Should().NotContain("sub-1", "sub-1 should have been pruned");

        // Published while the node is gone — its group still exists, so this queues.
        db.Execute("HW.PUBLISH", "orders.placed", "msg-after");

        // The node comes back and drains everything addressed to it.
        var received = (RedisResult[])db.Execute("HW.RECEIVE", "orders.placed", "sub-1", "COUNT", "10")!;
        var payloads = received.Select(r => (string)((RedisResult[])r!)[1]!).ToArray();

        payloads.Should().Equal(["msg-before", "msg-after"],
            "pruning a dead node must never delete its subscriber group — pub/sub messages are " +
            "addressed to a group and outlive the process, so a restart resumes its pending messages");
    }

    [Fact]
    public async Task DeadNode_IsRemovedFromTheServiceWorkerSet()
    {
        using var server = NewServer();
        using var redis = ConnectionMultiplexer.Connect(server.ConnectionString);
        var db = redis.GetDatabase();

        db.Execute("HW.HEARTBEAT", "worker-1", Catalog);
        db.Execute("HW.CALL", "orders.create", "req-1", "p");
        db.Execute("HW.DEQUEUE", "orders.create", "worker-1");

        await WaitForExpiryAsync();

        db.Execute("HW.HEARTBEAT", "worker-2", Catalog);
        db.Execute("HW.DEQUEUE", "orders.create", "worker-2");

        // The dead node is gone from the worker set, so future dequeues stop
        // locking and sweeping its list — the 004.1 unbounded-growth deferral.
        var nodeList = (string?)db.StringGet("hw:svc:orders.create:nodelist");
        nodeList.Should().NotBeNull();
        nodeList!.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Should().NotContain("worker-1").And.Contain("worker-2");
    }

    [Fact]
    public async Task PruningDisabled_LeavesDeadNodeStateAlone()
    {
        using var server = NewServer(pruning: false);
        using var redis = ConnectionMultiplexer.Connect(server.ConnectionString);
        var db = redis.GetDatabase();

        db.Execute("HW.HEARTBEAT", "worker-1", Catalog);
        db.Execute("HW.CALL", "orders.create", "req-1", "p");
        db.Execute("HW.DEQUEUE", "orders.create", "worker-1");

        await WaitForExpiryAsync();

        db.Execute("HW.HEARTBEAT", "worker-2", Catalog);
        db.Execute("HW.DEQUEUE", "orders.create", "worker-2").IsNull.Should().BeTrue(
            "with pruning off, recovery falls back to the slower per-entry lease sweep");

        var nodeList = (string?)db.StringGet("hw:svc:orders.create:nodelist");
        nodeList!.Split('\n', StringSplitOptions.RemoveEmptyEntries).Should().Contain("worker-1");
    }

    /// <summary>
    /// A node running with <c>HeartbeatEnabled = false</c> holds no registration
    /// at all. Pruning it on that basis would requeue a healthy worker's
    /// in-flight work on every dequeue — a configuration choice turned into a
    /// duplicate-execution storm.
    /// </summary>
    [Fact]
    public async Task UnregisteredNode_IsNeverPruned()
    {
        using var server = NewServer();
        using var redis = ConnectionMultiplexer.Connect(server.ConnectionString);
        var db = redis.GetDatabase();

        // worker-1 never heartbeats; it just works.
        db.Execute("HW.CALL", "orders.create", "req-1", "p");
        db.Execute("HW.DEQUEUE", "orders.create", "worker-1");

        await WaitForExpiryAsync();

        db.Execute("HW.HEARTBEAT", "worker-2", Catalog);
        db.Execute("HW.DEQUEUE", "orders.create", "worker-2").IsNull.Should().BeTrue(
            "a node with no registration is not participating in the registry and must be left to the lease sweep");
    }
}
