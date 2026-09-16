using FluentAssertions;
using Highway.Abstractions;
using Highway.Client;
using Highway.Client.Engine;
using Highway.Server;
using Highway.Server.Storage.Rocks;
using StackExchange.Redis;
using Xunit;

namespace Highway.Integration.Tests;

[Queue("repl.notes")]
public sealed record ReplNote : ISend
{
    public string Tag { get; init; } = "";
}

/// <summary>
/// 042 T6/T7/T8 — two-node pair, client failover, partitions, witness. The fake-clock
/// partition semantics (witness gate, fence/unfence, stagger) are unit-proven in
/// <c>ReplicationFeederTests</c>; these tests prove the wiring end-to-end over real
/// Kestrel RESP + RocksDB.
/// </summary>
public class ReplicationPairTests : IDisposable
{
    private readonly List<string> _dirs = [];

    public void Dispose()
    {
        foreach (var dir in _dirs)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { /* best-effort */ }
        }
    }

    private string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hw-repl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _dirs.Add(dir);
        return dir;
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 10_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(50);
        }
        throw new TimeoutException("condition not met");
    }

    [Fact]
    public async Task TwoNode_PullApplies_Promote_ResurrectionDemotes_ZeroLoss()
    {
        const string password = "pair-shared";
        var primaryDir = NewDir();
        var replicaDir = NewDir();

        using var primary = new HighwayTestServer(o =>
        {
            o.DataDir = primaryDir;
            o.Ephemeral = false;
            o.Authentication.Password = password;
            o.Replication.ReplicaId = "primary";
        });

        using var replica = new HighwayTestServer(o =>
        {
            o.DataDir = replicaDir;
            o.Ephemeral = false;
            o.Authentication.Password = password;
            o.Replication.StartAsReplica = true;
            o.Replication.PrimaryServer = primary.ConnectionString;
            o.Replication.ReplicaId = "standby";
            o.Replication.Priority = 10;
        });

        using var redis = ConnectionMultiplexer.Connect(primary.ConnectionString);
        redis.GetDatabase().Execute("HW.QSEND", "repl.notes", "m-ack", "payload");

        await WaitForAsync(() =>
            replica.Replication is { } f && f.Engine.GetLatestSequenceNumber() > 0);

        replica.Replication!.IsWritable.Should().BeFalse();
        var qsendOnReplica = () =>
        {
            using var r = ConnectionMultiplexer.Connect(replica.ConnectionString);
            r.GetDatabase().Execute("HW.QSEND", "repl.notes", "x", "y");
        };
        // G5: the refusal must redirect to the PRIMARY, not to the refusing replica.
        qsendOnReplica.Should().Throw<RedisServerException>()
            .Which.Message.Should().StartWith("NOTPRIMARY").And.Contain($":{primary.Port}");

        var epoch = replica.Replication.Epoch;
        using (var rr = ConnectionMultiplexer.Connect(replica.ConnectionString))
            ((int)rr.GetDatabase().Execute("HW.REPL.PROMOTE", "harness")).Should().Be((int)epoch + 1);

        replica.Replication.IsWritable.Should().BeTrue();
        replica.Replication.Epoch.Should().Be(epoch + 1);

        // ZERO LOSS, asserted by reading the message back on the new primary — not by
        // trusting the sequence number (G10).
        using (var promoted = ConnectionMultiplexer.Connect(replica.ConnectionString))
        {
            var claimed = (RedisResult[])promoted.GetDatabase().Execute("HW.QCLAIM", "repl.notes", "worker-1")!;
            claimed.Should().NotBeNull();
            ((string)claimed[0]!).Should().Be("m-ack",
                "the message acked on the old primary and replicated must survive the failover");
            ((string)claimed[1]!).Should().Be("payload");
        }

        // Resurrection: the promoted node announced itself to the old primary (G3), so
        // the old primary demotes without any manual epoch injection — and its
        // reconciliation report names what stayed local (G9).
        await WaitForAsync(() => primary.Replication!.Role == ReplicaRole.Demoted);
        File.Exists(primary.Replication!.LastReconciliationPath).Should().BeTrue();
        var report = File.ReadAllText(primary.Replication.LastReconciliationPath!);
        report.Should().Contain("the broker never merges");

        // And the demoted primary redirects clients to the node that announced (G5).
        var qsendOnDemoted = () =>
        {
            using var p = ConnectionMultiplexer.Connect(primary.ConnectionString);
            p.GetDatabase().Execute("HW.QSEND", "repl.notes", "late", "y");
        };
        qsendOnDemoted.Should().Throw<RedisServerException>()
            .Which.Message.Should().StartWith("NOTPRIMARY").And.Contain($"127.0.0.1:{replica.Port}");
    }

    [Fact]
    public async Task Client_OnNotPrimary_FollowsTheRedirect_WithNoManualEndpointPatching()
    {
        const string password = "pair-fail";
        var primaryDir = NewDir();
        var replicaDir = NewDir();

        using var primary = new HighwayTestServer(o =>
        {
            o.DataDir = primaryDir;
            o.Ephemeral = false;
            o.Authentication.Password = password;
        });
        using var replica = new HighwayTestServer(o =>
        {
            o.DataDir = replicaDir;
            o.Ephemeral = false;
            o.Authentication.Password = password;
            o.Replication.StartAsReplica = true;
            o.Replication.PrimaryServer = primary.ConnectionString;
            o.Replication.ReplicaId = "standby";
            o.Replication.Priority = 10;
        });

        await using var node = await EngineNode.StartAsync(primary.ConnectionString, "repl-client");

        var id1 = await node.Client.SendAsync(new ReplNote { Tag = "before" });
        id1.Should().NotBeNullOrWhiteSpace();

        // Promote the replica; its announcement demotes the old primary, whose
        // -NOTPRIMARY then carries the new primary's endpoint (G3+G5) — nothing here
        // patches AdvertiseEndpoint by hand.
        using (var rr = ConnectionMultiplexer.Connect(replica.ConnectionString))
            rr.GetDatabase().Execute("HW.REPL.PROMOTE", "client-failover");

        await WaitForAsync(() => primary.Replication!.Role == ReplicaRole.Demoted);

        var id2 = await node.Client.SendAsync(new ReplNote { Tag = "after" });
        id2.Should().NotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// 042-1b B-T6 — the headline: kill the master (a real dispose; no redirect can be
    /// spoken, and NOBODY calls promote). The client's walk finds the willing standby;
    /// its re-driven verb is the herd arriving, which IS the promotion (042-1c).
    /// </summary>
    [Fact]
    public async Task Client_SurvivesAKilledPrimary_ViaTheHerdWalk()
    {
        const string password = "pair-kill";
        var primaryDir = NewDir();
        var replicaDir = NewDir();

        var primary = new HighwayTestServer(o =>
        {
            o.DataDir = primaryDir;
            o.Ephemeral = false;
            o.Authentication.Password = password;
        });
        using var replica = new HighwayTestServer(o =>
        {
            o.DataDir = replicaDir;
            o.Ephemeral = false;
            o.Authentication.Password = password;
            o.Replication.StartAsReplica = true;
            o.Replication.PrimaryServer = primary.ConnectionString;
            o.Replication.ReplicaId = "standby";
            o.Replication.Priority = 10;
            o.Replication.WillingnessThreshold = TimeSpan.FromMilliseconds(400);
            o.Replication.FenceTimeout = TimeSpan.FromMilliseconds(900);
        });

        // R6.1: the client bootstraps with BOTH endpoints.
        var multi = $"localhost:{primary.Port},localhost:{replica.Port},password={password}";
        await using var node = await EngineNode.StartAsync(multi, "repl-kill-client");

        (await node.Client.SendAsync(new ReplNote { Tag = "before-kill" }))
            .Should().NotBeNullOrWhiteSpace();

        await WaitForAsync(() => replica.Replication!.HasSeenMaster);
        primary.Dispose();

        // The send sees connection loss, walks to the standby, gets "willing" once the
        // standby's own master-link is dead past W, and the verb itself promotes it.
        var id = await node.Client.SendAsync(new ReplNote { Tag = "after-kill" });
        id.Should().NotBeNullOrWhiteSpace();
        replica.Replication!.IsWritable.Should().BeTrue("the herd arriving is the promotion");
        replica.Replication.Epoch.Should().Be(2);
        replica.Replication.Transitions.Should().Contain(t => t.Contains("reason=herd-arrival"));
    }

    /// <summary>
    /// 042-1b B-T3b + B-R2 — GOODBYE moves a real client, and dynamic membership works:
    /// the client bootstrapped with ONLY the primary's endpoint; it reaches the standby
    /// through the roster the master narrated (a node absent from its connection string).
    /// </summary>
    [Fact]
    public async Task Client_FollowsGoodbye_ToARosterNodeAbsentFromItsBootstrapString()
    {
        const string password = "pair-follow";
        var primaryDir = NewDir();
        var replicaDir = NewDir();

        using var primary = new HighwayTestServer(o =>
        {
            o.DataDir = primaryDir;
            o.Ephemeral = false;
            o.Authentication.Password = password;
            o.Replication.ReplicaId = "leader";
            o.Replication.Priority = 1;
            o.Replication.GoodbyeDrainTimeout = TimeSpan.FromMilliseconds(400);
        });
        using var replica = new HighwayTestServer(o =>
        {
            o.DataDir = replicaDir;
            o.Ephemeral = false;
            o.Authentication.Password = password;
            o.Replication.StartAsReplica = true;
            o.Replication.PrimaryServer = primary.ConnectionString;
            o.Replication.ReplicaId = "standby";
            o.Replication.Priority = 10;
        });

        // Single-endpoint bootstrap — the standby is NOT in the client's string.
        await using var node = await EngineNode.StartAsync(primary.ConnectionString, "repl-follow-client");

        (await node.Client.SendAsync(new ReplNote { Tag = "before-bye" }))
            .Should().NotBeNullOrWhiteSpace();

        // Let the standby join the roster and the client learn it.
        await WaitForAsync(() => replica.Replication!.HasSeenMaster);
        await Task.Delay(400);

        using (var p = ConnectionMultiplexer.Connect(primary.ConnectionString))
            p.GetDatabase().Execute("HW.REPL.GOODBYE", "maintenance");

        // The client hears GOODBYE (or hits -NOTPRIMARY at stand-down), walks the
        // roster, lands on the standby, and its verb promotes it — sends keep working.
        string? after = null;
        await WaitForAsync(() =>
        {
            try
            {
                after = node.Client.SendAsync(new ReplNote { Tag = "after-bye" }).GetAwaiter().GetResult();
                return replica.Replication!.IsWritable;
            }
            catch (Exception)
            {
                return false;   // still converging — the drain window is deliberately small
            }
        }, timeoutMs: 20_000);

        after.Should().NotBeNullOrWhiteSpace();
        replica.Replication!.IsWritable.Should().BeTrue();
        primary.Replication!.Role.Should().Be(ReplicaRole.Demoted, "the departed node stood down cleanly");
    }

    /// <summary>
    /// 042-1a: the herd handshake over the wire. The master answers "master"; a standby
    /// with a healthy master-link answers "standby" with the redirect (the no-split rule:
    /// one confused client cannot move mastership); the same standby answers "willing"
    /// once its own link to the master has been dead past W.
    /// </summary>
    [Fact]
    public async Task ClientHandshake_Master_Standby_ThenWillingAfterMasterDies()
    {
        const string password = "pair-hs";
        var primaryDir = NewDir();
        var replicaDir = NewDir();

        var primary = new HighwayTestServer(o =>
        {
            o.DataDir = primaryDir;
            o.Ephemeral = false;
            o.Authentication.Password = password;
        });
        using var replica = new HighwayTestServer(o =>
        {
            o.DataDir = replicaDir;
            o.Ephemeral = false;
            o.Authentication.Password = password;
            o.Replication.StartAsReplica = true;
            o.Replication.PrimaryServer = primary.ConnectionString;
            o.Replication.ReplicaId = "standby";
            o.Replication.Priority = 10;
            o.Replication.WillingnessThreshold = TimeSpan.FromMilliseconds(400);
            o.Replication.FenceTimeout = TimeSpan.FromMilliseconds(900);
        });

        using (var p = ConnectionMultiplexer.Connect(primary.ConnectionString))
        {
            var reply = (RedisResult[])p.GetDatabase().Execute("HW.REPL.HELLO", "CLIENT", "client-1", "1")!;
            ((string)reply[0]!).Should().Be("master");
        }

        using var r = ConnectionMultiplexer.Connect(replica.ConnectionString);

        await WaitForAsync(() =>
        {
            var reply = (RedisResult[])r.GetDatabase().Execute("HW.REPL.HELLO", "CLIENT", "client-1", "1")!;
            return (string)reply[0]! == "standby";
        });
        var standby = (RedisResult[])r.GetDatabase().Execute("HW.REPL.HELLO", "CLIENT", "client-1", "1")!;
        ((string)standby[0]!).Should().Be("standby",
            "a standby with a healthy master-link is unwilling, whatever a client believes");
        ((string)standby[1]!).Should().Contain($":{primary.Port}", "the unwilling reply redirects to the master it still sees");

        primary.Dispose();

        await WaitForAsync(() =>
        {
            var reply = (RedisResult[])r.GetDatabase().Execute("HW.REPL.HELLO", "CLIENT", "client-1", "1")!;
            return (string)reply[0]! == "willing";
        });
        replica.Replication!.Role.Should().Be(ReplicaRole.Replica,
            "answering willing never promotes — the herd's first accepted verb does (042-1c)");
    }

    [Fact]
    public async Task IsolatedPrimary_FenceBackstopStillFences()
    {
        var dir = NewDir();
        using var primary = new HighwayTestServer(o =>
        {
            o.DataDir = dir;
            o.Ephemeral = false;
            o.Replication.AutoFailover = true;
            o.Replication.FenceTimeout = TimeSpan.FromMilliseconds(250);
            o.Replication.PromoteTimeout = TimeSpan.FromMilliseconds(600);
            o.Replication.Margin = TimeSpan.FromMilliseconds(100);
            o.Replication.WillingnessThreshold = TimeSpan.FromMilliseconds(100);
        });

        await Task.Delay(800);
        primary.Replication!.Role.Should().Be(ReplicaRole.Fenced);
    }

    /// <summary>
    /// 042-1c C-T4 — the herd IS the promotion trigger: kill the master, and the willing
    /// standby becomes master on the first accepted client verb, not on any timer.
    /// </summary>
    [Fact]
    public async Task HerdArrival_FirstClientVerb_PromotesTheWillingStandby()
    {
        const string password = "pair-herd";
        var primaryDir = NewDir();
        var replicaDir = NewDir();

        var primary = new HighwayTestServer(o =>
        {
            o.DataDir = primaryDir;
            o.Ephemeral = false;
            o.Authentication.Password = password;
        });
        using var replica = new HighwayTestServer(o =>
        {
            o.DataDir = replicaDir;
            o.Ephemeral = false;
            o.Authentication.Password = password;
            o.Replication.StartAsReplica = true;
            o.Replication.PrimaryServer = primary.ConnectionString;
            o.Replication.ReplicaId = "standby";
            o.Replication.Priority = 10;
            o.Replication.WillingnessThreshold = TimeSpan.FromMilliseconds(400);
            o.Replication.FenceTimeout = TimeSpan.FromMilliseconds(900);
        });

        using var r = ConnectionMultiplexer.Connect(replica.ConnectionString);
        var db = r.GetDatabase();

        // Wait for the pump's first real contact — from here the willingness clock
        // measures LOSS, and the standby is deterministically unwilling while it pulls.
        await WaitForAsync(() => replica.Replication!.HasSeenMaster);

        // While the master lives, a client verb on the standby is refused — no promotion.
        var early = () => db.Execute("HW.QSEND", "repl.notes", "too-early", "x");
        early.Should().Throw<RedisServerException>().Which.Message.Should().StartWith("NOTPRIMARY");
        replica.Replication!.Epoch.Should().Be(1);

        primary.Dispose();
        await WaitForAsync(() => replica.Replication.IsWillingForHerd());

        // The first accepted client verb IS the promotion.
        db.Execute("HW.QSEND", "repl.notes", "after-arrival", "payload");
        replica.Replication.IsWritable.Should().BeTrue();
        replica.Replication.Epoch.Should().Be(2);
        replica.Replication.Transitions.Should().Contain(t => t.Contains("reason=herd-arrival"));
    }

    /// <summary>042-1c C-T5 — GOODBYE over the wire: narrated, drained, stood down; the standby hears it and turns willing.</summary>
    [Fact]
    public async Task Goodbye_NarratesDrains_StandsDown_AndTheStandbyTurnsWilling()
    {
        const string password = "pair-bye";
        var primaryDir = NewDir();
        var replicaDir = NewDir();

        using var primary = new HighwayTestServer(o =>
        {
            o.DataDir = primaryDir;
            o.Ephemeral = false;
            o.Authentication.Password = password;
            o.Replication.ReplicaId = "leader";
            o.Replication.GoodbyeDrainTimeout = TimeSpan.FromMilliseconds(400);
        });
        using var replica = new HighwayTestServer(o =>
        {
            o.DataDir = replicaDir;
            o.Ephemeral = false;
            o.Authentication.Password = password;
            o.Replication.StartAsReplica = true;
            o.Replication.PrimaryServer = primary.ConnectionString;
            o.Replication.ReplicaId = "standby";
            o.Replication.Priority = 10;
        });

        using var client = ConnectionMultiplexer.Connect(primary.ConnectionString);
        var db = client.GetDatabase();
        db.Execute("HW.QSEND", "repl.notes", "pre-bye", "x");   // classifies the session as a herd client

        var goodbyeHeard = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await client.GetSubscriber().SubscribeAsync(
            RedisChannel.Literal("hw:door:topology"),
            (_, m) => { if (((string?)m)?.StartsWith("GOODBYE") == true) goodbyeHeard.TrySetResult((string)m!); });

        primary.Replication!.ConnectedClients.Should().BeGreaterThan(0, "the herd count sees the client");

        db.Execute("HW.REPL.GOODBYE", "maintenance").ToString().Should().Be("OK");

        (await Task.WhenAny(goodbyeHeard.Task, Task.Delay(5_000))).Should().Be(goodbyeHeard.Task,
            "GOODBYE is narrated to connected clients");
        goodbyeHeard.Task.Result.Should().StartWith("GOODBYE 1");

        // New work refused during/after the drain; the node stands down at the deadline.
        await WaitForAsync(() => primary.Replication.Role == ReplicaRole.Demoted);
        var late = () => db.Execute("HW.QSEND", "repl.notes", "post-bye", "x");
        late.Should().Throw<RedisServerException>().Which.Message.Should().StartWith("NOTPRIMARY");

        // The standby heard the narration on its own subscription: immediately willing.
        await WaitForAsync(() => replica.Replication!.GoodbyeSeen);
        replica.Replication!.IsWillingForHerd().Should().BeTrue(
            "GOODBYE makes the standby willing without waiting out W");
    }

    /// <summary>042-1c C-T2 — the roster: master self-registers, the standby's JOIN announce lands, STATUS serves both.</summary>
    [Fact]
    public async Task JoinAnnounce_PopulatesTheRoster_AndStatusServesIt()
    {
        const string password = "pair-roster";
        var primaryDir = NewDir();
        var replicaDir = NewDir();

        using var primary = new HighwayTestServer(o =>
        {
            o.DataDir = primaryDir;
            o.Ephemeral = false;
            o.Authentication.Password = password;
            o.Replication.ReplicaId = "leader";
            o.Replication.Priority = 1;
        });
        using var replica = new HighwayTestServer(o =>
        {
            o.DataDir = replicaDir;
            o.Ephemeral = false;
            o.Authentication.Password = password;
            o.Replication.StartAsReplica = true;
            o.Replication.PrimaryServer = primary.ConnectionString;
            o.Replication.ReplicaId = "standby";
            o.Replication.Priority = 10;
        });

        using var p = ConnectionMultiplexer.Connect(primary.ConnectionString);

        await WaitForAsync(() =>
        {
            var fields = Fields((RedisResult[])p.GetDatabase().Execute("HW.REPL.STATUS")!);
            return fields.Values.Contains("standby") && fields.Values.Contains("leader");
        });

        var status = Fields((RedisResult[])p.GetDatabase().Execute("HW.REPL.STATUS")!);
        status["roster.0.id"].Should().Be("leader", "the master self-registered at priority 1 — the roster is sorted by priority");
        status["roster.1.id"].Should().Be("standby", "the standby's JOIN announce landed");
        ulong.Parse(status["roster.version"]).Should().BeGreaterThanOrEqualTo(2);

        // The roster WAL-ships: the standby already holds it in its own store (parent R13.6).
        using var r = ConnectionMultiplexer.Connect(replica.ConnectionString);
        await WaitForAsync(() =>
        {
            var fields = Fields((RedisResult[])r.GetDatabase().Execute("HW.REPL.STATUS")!);
            return fields.Values.Contains("standby") && fields.Values.Contains("leader");
        });
    }

    private static Dictionary<string, string> Fields(RedisResult[] flat)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i + 1 < flat.Length; i += 2)
            map[flat[i].ToString()!] = flat[i + 1].ToString()!;
        return map;
    }

    [Fact]
    public void RawWireWrites_AreRefusedOnAReplica()
    {
        const string password = "pair-raw";
        var primaryDir = NewDir();
        var replicaDir = NewDir();

        using var primary = new HighwayTestServer(o =>
        {
            o.DataDir = primaryDir;
            o.Ephemeral = false;
            o.Authentication.Password = password;
        });
        using var replica = new HighwayTestServer(o =>
        {
            o.DataDir = replicaDir;
            o.Ephemeral = false;
            o.Authentication.Password = password;
            o.Replication.StartAsReplica = true;
            o.Replication.PrimaryServer = primary.ConnectionString;
            o.Replication.ReplicaId = "standby";
        });

        using var r = ConnectionMultiplexer.Connect(replica.ConnectionString);
        var db = r.GetDatabase();

        // G7: the raw wire surface obeys the same writability rule as HW.* verbs.
        var set = () => db.Execute("SET", "hw:idem:svc:req-1", "marker", "PX", "60000", "NX");
        set.Should().Throw<RedisServerException>().Which.Message.Should().StartWith("NOTPRIMARY");

        var del = () => db.Execute("DEL", "hw:rep:req-1");
        del.Should().Throw<RedisServerException>().Which.Message.Should().StartWith("NOTPRIMARY");

        // Reads stay served on a replica.
        db.StringGet("hw:idem:svc:req-1").IsNull.Should().BeTrue();
    }
}
