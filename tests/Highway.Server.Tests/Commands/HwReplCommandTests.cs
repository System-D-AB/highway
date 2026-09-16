using FluentAssertions;
using Highway.Server;
using Highway.Server.Commands.Ported;
using Highway.Server.Storage.Rocks;
using StackExchange.Redis;
using Xunit;

namespace Highway.Server.Tests.Commands;

/// <summary>042 T1 — HW.REPL.HELLO / PULL / ACK, including a raw SE.Redis client against a live node.</summary>
public class HwReplCommandTests
{
    [Fact]
    public void Hello_OnEphemeralStore_Refuses()
    {
        using var h = new CommandHarness();
        var reply = CommandHarness.AsText(h.Run(new HwReplHelloCommand(), "replica-1", "0", "1"));
        reply.Should().StartWith("-ERR HW_INVALID_ARG");
        reply.Should().Contain("ephemeral");
    }

    [Fact]
    public void HelloPullAck_OnRocksStore_RoundTrips()
    {
        var dir = Storage.ReplicationToolkitSurfaceTests.NewTempDir();
        try
        {
            using var store = RocksDbStore.Open(dir);
            using var h = new CommandHarness(store);

            using (var batch = store.NewBatch())
            {
                store.Set(batch, "k"u8.ToArray(), "v"u8.ToArray());
                batch.Commit();
            }

            var hello = CommandHarness.AsText(h.Run(new HwReplHelloCommand(), "r1", "0", "1"));
            hello.Should().StartWith("*2\r\n");

            var pull = CommandHarness.AsText(h.Run(new HwReplPullCommand(), "0", "1000000"));
            pull.Should().StartWith("*3\r\n");
            pull.Should().Contain(":1\r\n", "epoch 1 is stamped on the page");

            var ack = CommandHarness.AsText(h.Run(new HwReplAckCommand(), "r1", "1"));
            ack.Should().Be("+OK\r\n");
        }
        finally
        {
            Storage.ReplicationToolkitSurfaceTests.TryDelete(dir);
        }
    }

    // ---- 042-1a: the herd handshake and the roster ---------------------------

    /// <summary>The three handshake replies, golden-shaped at the command layer (A-T4).</summary>
    [Fact]
    public void ClientHandshake_Master_Standby_Willing_Shapes()
    {
        var primaryDir = Storage.ReplicationToolkitSurfaceTests.NewTempDir();
        var replicaDir = Storage.ReplicationToolkitSurfaceTests.NewTempDir();
        var clock = new Storage.ManualClock();
        try
        {
            using (var primary = RocksDbStore.Open(primaryDir))
            using (var h = new CommandHarness(primary))
            {
                var reply = CommandHarness.AsText(h.Run(new HwReplHelloCommand(), "CLIENT", "client-1", "1"));
                reply.Should().Contain("master").And.Contain("$1\r\n1\r\n", "epoch 1");
            }

            using (var replica = RocksDbStore.Open(replicaDir, replication: new HighwayReplicationOptions
            {
                StartAsReplica = true,
                PrimaryServer = null,           // no bootstrap needed — role alone matters here
                Priority = 1,                   // effective never-seen window = T_fence(5s) + 1×0.25s = 5.25s
                WillingnessThreshold = TimeSpan.FromSeconds(3),
                Clock = clock,
                AdvertiseEndpoint = "standby-host:6500",
            }))
            using (var h = new CommandHarness(replica))
            {
                var standby = CommandHarness.AsText(h.Run(new HwReplHelloCommand(), "CLIENT", "client-1", "1"));
                standby.Should().Contain("standby",
                    "a fresh master-link means unwilling — one client cannot move mastership");

                // This standby never reached a master (none exists in the harness), so
                // willingness waits the outer T_fence bound (contract amendment 2026-09-16).
                clock.Advance(TimeSpan.FromSeconds(4));
                CommandHarness.AsText(h.Run(new HwReplHelloCommand(), "CLIENT", "client-1", "1"))
                    .Should().Contain("standby", "'never found the master' is not 'lost the master'");

                clock.Advance(TimeSpan.FromSeconds(2));
                var willing = CommandHarness.AsText(h.Run(new HwReplHelloCommand(), "CLIENT", "client-1", "1"));
                willing.Should().Contain("willing");
                replica.Replication.Role.Should().Be(ReplicaRole.Replica, "answering willing never promotes");
            }
        }
        finally
        {
            Storage.ReplicationToolkitSurfaceTests.TryDelete(primaryDir);
            Storage.ReplicationToolkitSurfaceTests.TryDelete(replicaDir);
        }
    }

    [Fact]
    public void Join_Admits_RefusesHeldPriority_AndStatusExposesTheRoster()
    {
        var dir = Storage.ReplicationToolkitSurfaceTests.NewTempDir();
        try
        {
            using var store = RocksDbStore.Open(dir);
            using var h = new CommandHarness(store);

            CommandHarness.AsText(h.Run(new HwReplJoinCommand(), "node-a", "1", "hosta:6500"))
                .Should().Be(":1\r\n", "first admission is roster version 1");

            var collision = CommandHarness.AsText(h.Run(new HwReplJoinCommand(), "node-b", "1", "hostb:6500"));
            collision.Should().StartWith("-ERR HW_PRIORITY_TAKEN").And.Contain("node-a",
                "first announcer wins; the refusal names the holder");

            CommandHarness.AsText(h.Run(new HwReplJoinCommand(), "node-b", "2", "hostb:6500"))
                .Should().Be(":2\r\n");

            // Re-announce by the same node updates it (new endpoint), not a collision.
            CommandHarness.AsText(h.Run(new HwReplJoinCommand(), "node-a", "1", "hosta:6501"))
                .Should().Be(":3\r\n");

            var status = CommandHarness.AsText(h.Run(new HwReplStatusCommand()));
            status.Should().Contain("roster.version").And.Contain("node-a").And.Contain("hosta:6501")
                .And.Contain("node-b").And.Contain("roster.0.priority");
        }
        finally
        {
            Storage.ReplicationToolkitSurfaceTests.TryDelete(dir);
        }
    }

    [Fact]
    public void SeRedis_PullsPagesOffALiveNode()
    {
        var dir = Storage.ReplicationToolkitSurfaceTests.NewTempDir();
        try
        {
            using var server = new HighwayTestServer(o =>
            {
                o.DataDir = dir;
                o.Ephemeral = false;
            });
            using var redis = ConnectionMultiplexer.Connect(server.ConnectionString);
            var db = redis.GetDatabase();

            db.Execute("HW.QSEND", "q", "m1", "payload");

            var hello = db.Execute("HW.REPL.HELLO", "replica-a", "0", "1");
            ((RedisResult[])hello!).Should().HaveCount(2);

            var pull = db.Execute("HW.REPL.PULL", "0", "1000000");
            var pullRows = (RedisResult[])pull!;
            pullRows.Should().HaveCount(3);
            ((int)pullRows[0]).Should().Be(1, "primary epoch");

            db.Execute("HW.REPL.ACK", "replica-a", "1");
        }
        finally
        {
            Storage.ReplicationToolkitSurfaceTests.TryDelete(dir);
        }
    }
}
