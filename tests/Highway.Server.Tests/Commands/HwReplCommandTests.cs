using Highway.Server.Storage.Crystal;
using FluentAssertions;
using Highway.Server;
using Highway.Server.Commands.Ported;
using Highway.Server.Storage.Replication;
using StackExchange.Redis;
using Xunit;

namespace Highway.Server.Tests.Commands;

/// <summary>042 T1 — HW.REPL.HELLO (076: the data path is Crystal's), including a raw SE.Redis client against a live node.</summary>
public class HwReplCommandTests
{
    [Fact]
    public void Hello_OnEphemeralStore_Refuses()
    {
        using var h = new CommandHarness();
        var reply = CommandHarness.AsText(h.Run(new HwReplHelloCommand(), "replica-1", "0", "1", "FORMAT", "4"));
        reply.Should().StartWith("-ERR HW_INVALID_ARG");
        reply.Should().Contain("ephemeral");
    }

    [Fact]
    public void Hello_OnDurableStore_AnswersEpochFormatAndReplicationEndpoint()
    {
        var dir = Storage.TempDirs.NewTempDir();
        try
        {
            using var store = CrystalHighwayStore.Open(dir);
            using var h = new CommandHarness(store);

            using (var batch = store.NewBatch())
            {
                store.Set(batch, Highway.Server.Storage.Layout.HighwayKeyspace.Kv("k"), "v"u8.ToArray());   // a format-4 key (065)
                batch.Commit();
            }

            var hello = CommandHarness.AsText(h.Run(new HwReplHelloCommand(), "r1", "0", "1", "FORMAT", "4"));
            hello.Should().StartWith("*5\r\n:1\r\n", "epoch 1 leads the reply")
                .And.Contain(":4\r\n", "the primary echoes its storage format");
            store.Replication.Slots.Should().ContainKey("r1", "a hello opens the standby's slot");
        }
        finally
        {
            Storage.TempDirs.TryDelete(dir);
        }
    }

    // ---- 042-1a: the herd handshake and the roster ---------------------------

    /// <summary>The three handshake replies, golden-shaped at the command layer (A-T4).</summary>
    [Fact]
    public void ClientHandshake_Master_Standby_Willing_Shapes()
    {
        var primaryDir = Storage.TempDirs.NewTempDir();
        var replicaDir = Storage.TempDirs.NewBootstrappedDir();   // 076: a standby that has bootstrapped
        var clock = new Storage.ManualClock();
        try
        {
            using (var primary = CrystalHighwayStore.Open(primaryDir))
            using (var h = new CommandHarness(primary))
            {
                var reply = CommandHarness.AsText(h.Run(new HwReplHelloCommand(), "CLIENT", "client-1", "1"));
                reply.Should().Contain("master").And.Contain("$1\r\n1\r\n", "epoch 1");
            }

            using (var replica = CrystalHighwayStore.Open(replicaDir, replication: new HighwayReplicationOptions
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
            Storage.TempDirs.TryDelete(primaryDir);
            Storage.TempDirs.TryDelete(replicaDir);
        }
    }

    [Fact]
    public void Join_Admits_RefusesHeldPriority_AndStatusExposesTheRoster()
    {
        var dir = Storage.TempDirs.NewTempDir();
        try
        {
            using var store = CrystalHighwayStore.Open(dir);
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
            Storage.TempDirs.TryDelete(dir);
        }
    }

    [Fact]
    public void SeRedis_HelloOffALiveNode_NamesItsReplicationEndpoint()
    {
        var dir = Storage.TempDirs.NewTempDir();
        try
        {
            using var server = new HighwayTestServer(o =>
            {
                o.DataDir = dir;
                o.Ephemeral = false;
            });
            using var redis = ConnectionMultiplexer.Connect(server.ConnectionString);
            var db = redis.GetDatabase();

            db.Execute("HW.SEND", "q", "m1", "payload");

            // 065 T6: a replica declares its storage format; one that does not (a 2.x node) is refused.
            var legacy = () => db.Execute("HW.REPL.HELLO", "replica-old", "0", "1");
            legacy.Should().Throw<RedisServerException>().WithMessage("*HW_STORAGE_FORMAT*Highway 2.x*--wipe-data*");

            var hello = db.Execute("HW.REPL.HELLO", "replica-a", "0", "1", "FORMAT", "4");
            var rows = (RedisResult[])hello!;
            rows.Should().HaveCount(5);
            ((int)rows[4]).Should().Be(0, "this caller pulls nothing, so its data is not live (076)");
            ((int)rows[0]).Should().Be(1, "primary epoch");
            ((int)rows[2]).Should().Be(4, "the primary echoes its storage format");
            ((string)rows[3]!).Should().MatchRegex(@"^127\.0\.0\.1:\d+$", "the primary names where its data is followed (076)");

            // 076: the data path moved to the replication port; the RESP verbs that carried it are gone.
            var pull = () => db.Execute("HW.REPL.PULL", "0", "1000000");
            pull.Should().Throw<RedisServerException>();
        }
        finally
        {
            Storage.TempDirs.TryDelete(dir);
        }
    }
}
