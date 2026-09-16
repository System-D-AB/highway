using System.Diagnostics;
using FluentAssertions;
using Highway.Abstractions;
using Highway.Client.Engine;
using Highway.Server;
using Highway.Server.Storage.Rocks;
using StackExchange.Redis;
using Xunit;
using Xunit.Abstractions;

namespace Highway.Integration.Tests;

[Queue("herd.notes")]
public sealed record HerdNote : ISend
{
    public string Tag { get; init; } = "";
}

/// <summary>
/// 042-1d — the gate (parent T9/R11): herd cohesion at multi-client, multi-node scale.
/// The pair-level mechanics live in <c>ReplicationPairTests</c>; these tests prove the
/// CENTRAL invariant — the herd converges on the SAME node, never splits, and
/// acked-and-replicated work survives with duplicates counted, not silently doubled.
///
/// <para><b>Partition simulation notes</b> (design rule: declared, not smuggled): peer
/// isolation is simulated by disposing the peer (its socket genuinely dies); client-side
/// partition uses the declared <c>EndpointReachableOverride</c> seam on the connection
/// source. Real packet-level fault injection belongs to the assurance rig (D-T6).</para>
/// </summary>
public class HerdCohesionTests : IDisposable
{
    private const string Password = "herd-pass";
    private readonly List<string> _dirs = [];
    private readonly ITestOutputHelper _output;

    public HerdCohesionTests(ITestOutputHelper output) => _output = output;

    public void Dispose()
    {
        foreach (var dir in _dirs)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { /* best-effort */ }
        }
    }

    private string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hw-herd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _dirs.Add(dir);
        return dir;
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 20_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(50);
        }
        throw new TimeoutException("condition not met");
    }

    private HighwayTestServer StartNode(string id, int priority, HighwayTestServer? pullFrom, Action<HighwayServerOptions>? tune = null)
        => new(o =>
        {
            o.DataDir = NewDir();
            o.Ephemeral = false;
            o.Authentication.Password = Password;
            o.Replication.ReplicaId = id;
            o.Replication.Priority = priority;
            o.Replication.WillingnessThreshold = TimeSpan.FromMilliseconds(400);
            o.Replication.FenceTimeout = TimeSpan.FromMilliseconds(900);
            if (pullFrom is not null)
            {
                o.Replication.StartAsReplica = true;
                o.Replication.PrimaryServer = pullFrom.ConnectionString;
            }
            tune?.Invoke(o);
        });

    private static string Multi(params HighwayTestServer[] nodes)
        => string.Join(',', nodes.Select(n => $"localhost:{n.Port}")) + $",password={Password}";

    /// <summary>The client's active node, normalized to its PORT — the roster advertises
    /// 127.0.0.1 while bootstrap strings say localhost; the port is the identity.</summary>
    private static int PortOfClient(EngineNode node)
    {
        var host = HighwayConnectionSource.HostOf(node.Source.ActiveServer);
        return int.Parse(host[(host.LastIndexOf(':') + 1)..]);
    }

    /// <summary>Wire-level claim-drain of a queue: every entry's messageId, duplicates included.</summary>
    private static List<string> DrainClaims(HighwayTestServer server, string queue)
    {
        using var mux = ConnectionMultiplexer.Connect(server.ConnectionString);
        var db = mux.GetDatabase();
        var ids = new List<string>();
        while (true)
        {
            var claimed = db.Execute("HW.QCLAIM", queue, "drain-worker");
            if (claimed.IsNull) break;
            ids.Add((string)((RedisResult[])claimed!)[0]!);
        }
        return ids;
    }

    private static async Task WaitForReplicationCaughtUpAsync(HighwayTestServer primary, params HighwayTestServer[] standbys)
    {
        await WaitForAsync(() =>
        {
            var latest = primary.Replication!.Engine.GetLatestSequenceNumber();
            return standbys.All(s => s.Replication!.Engine.GetLatestSequenceNumber() == latest);
        });
    }

    // ------------------------------------------------------------------ D-T2

    /// <summary>
    /// The headline (parent R11.1): kill the master mid-traffic. Every client lands on
    /// the SAME successor (no split), zero acked-and-replicated loss proven by wire
    /// read-back, duplicates counted, convergence latency measured against x.
    /// </summary>
    [Fact]
    public async Task HardKill_HerdConverges_NoSplit_ZeroLoss_DuplicatesCounted()
    {
        var node1 = StartNode("node1", 1, null);
        using var node2 = StartNode("node2", 2, node1);
        using var node3 = StartNode("node3", 3, node1);
        var multi = Multi(node1, node2, node3);

        await using var clientA = await EngineNode.StartAsync(multi, "herd-a");
        await using var clientB = await EngineNode.StartAsync(multi, "herd-b");
        await using var clientC = await EngineNode.StartAsync(multi, "herd-c");
        var clients = new[] { clientA, clientB, clientC };

        // Pre-kill traffic: every ack recorded in the ledger.
        var acked = new List<string>();
        foreach (var c in clients)
            for (var i = 0; i < 10; i++)
                acked.Add(await c.Client.SendAsync(new HerdNote { Tag = $"{c.NodeName}-{i}" }));

        // Let replication catch up so zero-loss is strict (the RPO window is measured
        // separately by C9.1; this test pins the replicated guarantee).
        await WaitForReplicationCaughtUpAsync(node1, node2, node3);

        node1.Dispose();
        var killedAt = Stopwatch.StartNew();

        // Each client re-drives until its send lands; the verb arriving IS the promotion.
        var postKill = new List<string>();
        foreach (var c in clients)
        {
            string? id = null;
            await WaitForAsync(() =>
            {
                try { id = c.Client.SendAsync(new HerdNote { Tag = "post-kill" }).GetAwaiter().GetResult(); return true; }
                catch { return false; }
            });
            postKill.Add(id!);
            _output.WriteLine($"{c.NodeName} converged after {killedAt.ElapsedMilliseconds} ms");
        }

        // NO SPLIT — the herd settles on ONE node. Convergence is asynchronous (a client
        // may momentarily be "walking"), so wait for the steady state, then assert the
        // mechanical invariant: every client's active endpoint is the same node, and it is
        // the highest-priority successor. A permanent second master-with-clients would
        // never let this settle — that is the split the harness exists to catch.
        await WaitForAsync(() => clients.Select(PortOfClient).Distinct().Count() == 1
                              && PortOfClient(clientA) == node2.Port);
        clients.Select(PortOfClient).Distinct().Should().Equal(new[] { node2.Port },
            "the herd is a herd — all clients on the highest-priority successor");
        node2.Replication!.IsWritable.Should().BeTrue();
        node2.Replication.Epoch.Should().Be(2);
        node3.Replication!.IsWritable.Should().BeFalse("exactly one node is master — no split");

        // Observability is load-bearing (D-R4): the wire says what the test believes.
        using (var mux = ConnectionMultiplexer.Connect(node2.ConnectionString))
        {
            var status = Fields((RedisResult[])mux.GetDatabase().Execute("HW.REPL.STATUS")!);
            status["repl.role"].Should().Be("Primary");
            status["repl.epoch"].Should().Be("2");
            int.Parse(status["repl.clients"]).Should().BeGreaterThanOrEqualTo(3);
        }

        // ZERO LOSS + COUNTED DUPLICATES, by read-back on the new master.
        var claimed = DrainClaims(node2, "herd.notes");
        var distinct = claimed.Distinct(StringComparer.Ordinal).ToHashSet();
        distinct.Should().Contain(acked, "every acked-and-replicated message survives the failover");
        distinct.Should().Contain(postKill);
        var sent = acked.Concat(postKill).ToHashSet(StringComparer.Ordinal);
        claimed.Should().OnlyContain(id => sent.Contains(id), "nothing appears that was never sent");
        var duplicates = claimed.Count - distinct.Count;
        _output.WriteLine($"claimed={claimed.Count} distinct={distinct.Count} duplicates={duplicates} (allowed, counted)");
    }

    // ------------------------------------------------------------------ D-T4: GOODBYE at herd scale

    [Fact]
    public async Task Goodbye_MovesTheWholeHerd_ZeroLoss()
    {
        var node1 = StartNode("g-node1", 1, null, o => o.Replication.GoodbyeDrainTimeout = TimeSpan.FromMilliseconds(500));
        using var node2 = StartNode("g-node2", 2, node1);
        var multi = Multi(node1, node2);

        await using var clientA = await EngineNode.StartAsync(multi, "g-a");
        await using var clientB = await EngineNode.StartAsync(multi, "g-b");
        var clients = new[] { clientA, clientB };

        var acked = new List<string>();
        foreach (var c in clients)
            for (var i = 0; i < 5; i++)
                acked.Add(await c.Client.SendAsync(new HerdNote { Tag = "pre-bye" }));

        await WaitForReplicationCaughtUpAsync(node1, node2);

        using (var p = ConnectionMultiplexer.Connect(node1.ConnectionString))
            p.GetDatabase().Execute("HW.REPL.GOODBYE", "herd-scale");

        var post = new List<string>();
        foreach (var c in clients)
        {
            string? id = null;
            await WaitForAsync(() =>
            {
                try { id = c.Client.SendAsync(new HerdNote { Tag = "post-bye" }).GetAwaiter().GetResult(); return node2.Replication!.IsWritable; }
                catch { return false; }
            });
            post.Add(id!);
        }

        clients.Select(PortOfClient).Distinct().Should().HaveCount(1, "narrated departures keep the herd whole too");
        await WaitForAsync(() => node1.Replication!.Role == ReplicaRole.Demoted);

        var distinct = DrainClaims(node2, "herd.notes").Distinct().ToHashSet();
        distinct.Should().Contain(acked, "a planned departure loses nothing");
        distinct.Should().Contain(post);
        node1.Dispose();
    }

    // ------------------------------------------------------------------ D-T4: rejoin without preemption

    [Fact]
    public async Task Rejoin_HigherPriorityNode_WaitsAsStandby_UntilTheNextTransition()
    {
        var node2 = StartNode("r-node2", 2, null, o => o.Replication.GoodbyeDrainTimeout = TimeSpan.FromMilliseconds(400));
        var multi2 = $"localhost:{node2.Port},password={Password}";
        await using var client = await EngineNode.StartAsync(multi2, "r-client");

        (await client.Client.SendAsync(new HerdNote { Tag = "on-2" })).Should().NotBeNullOrWhiteSpace();

        // The higher-priority node returns — as a STANDBY. The herd must not move.
        using var node1 = StartNode("r-node1", 1, node2);
        await WaitForAsync(() => node1.Replication!.HasSeenMaster);
        await Task.Delay(1_500);   // well past W and T_fence — ample time to preempt if a path existed

        node2.Replication!.IsWritable.Should().BeTrue("no preemption: priority is 'next', not 'now'");
        node1.Replication!.Role.Should().Be(ReplicaRole.Replica);
        PortOfClient(client).Should().Be(node2.Port, "the herd stayed put");

        // Deliberate failback is GOODBYE (parent R13.5): now the herd converges on the returner.
        using (var p = ConnectionMultiplexer.Connect(node2.ConnectionString))
            p.GetDatabase().Execute("HW.REPL.GOODBYE", "failback");

        await WaitForAsync(() =>
        {
            try
            {
                client.Client.SendAsync(new HerdNote { Tag = "on-1" }).GetAwaiter().GetResult();
                return node1.Replication!.IsWritable;
            }
            catch { return false; }
        });

        node1.Replication!.IsWritable.Should().BeTrue("at the next real transition, priority is honoured");
        PortOfClient(client).Should().Be(node1.Port);
        node2.Dispose();
    }

    // ------------------------------------------------------------------ D-T4: priority collision

    [Fact]
    public async Task PriorityCollision_SecondAnnouncer_IsRefused_AndStaysOutOfSuccession()
    {
        using var master = StartNode("c-master", 1, null);
        using var standbyA = StartNode("c-standby-a", 5, master);
        using var standbyB = StartNode("c-standby-b", 5, master);   // the misconfigured twin

        await WaitForAsync(() => standbyA.Replication!.HasSeenMaster && standbyB.Replication!.HasSeenMaster);
        await WaitForAsync(() =>
            standbyA.Replication!.Transitions.Any(t => t.StartsWith("join-refused"))
            || standbyB.Replication!.Transitions.Any(t => t.StartsWith("join-refused")));

        using var mux = ConnectionMultiplexer.Connect(master.ConnectionString);
        var status = Fields((RedisResult[])mux.GetDatabase().Execute("HW.REPL.STATUS")!);
        var rosterIds = status
            .Where(kv => kv.Key.StartsWith("roster.", StringComparison.Ordinal) && kv.Key.EndsWith(".id"))
            .Select(kv => kv.Value).ToList();

        rosterIds.Should().HaveCount(2, "master + exactly one of the twins — first announcer wins");
        rosterIds.Should().Contain("c-master");
        var admitted = rosterIds.Single(id => id != "c-master");
        var refused = admitted == "c-standby-a" ? standbyB : standbyA;
        refused.Replication!.Transitions.Should().Contain(t => t.StartsWith("join-refused"),
            "the loser is told loudly, names included");
        refused.Replication.HasSeenMaster.Should().BeTrue("refused from succession, but still pulling — warm, not dead");
    }

    // ------------------------------------------------------------------ D-T3: partition matrix

    /// <summary>Parent R11.2(a): peers gone, clients present — the master keeps its herd (the herd IS mastership).</summary>
    [Fact]
    public async Task MasterIsolatedFromPeers_WithClients_KeepsServing()
    {
        using var master = StartNode("p-master", 1, null, o => o.Replication.AutoFailover = true);
        var standby = StartNode("p-standby", 2, master);
        var multi = $"localhost:{master.Port},password={Password}";
        await using var client = await EngineNode.StartAsync(multi, "p-client");

        (await client.Client.SendAsync(new HerdNote { Tag = "before" })).Should().NotBeNullOrWhiteSpace();

        standby.Dispose();   // total peer loss
        await Task.Delay(1_500);   // well past T_fence (900ms)

        master.Replication!.Role.Should().Be(ReplicaRole.Primary,
            "a master with clients does not fence on peer loss — mastership is where the herd is");
        (await client.Client.SendAsync(new HerdNote { Tag = "after" })).Should().NotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// Parent R11.2(c): a client cut off from the master (but not the standby) gets
    /// "no master for me yet" — NEVER a second master. The standby's healthy link vetoes.
    /// </summary>
    [Fact]
    public async Task DoublyConfusedClient_CannotMintASecondMaster()
    {
        using var master = StartNode("d-master", 1, null);
        using var standby = StartNode("d-standby", 2, master);
        await WaitForAsync(() => standby.Replication!.HasSeenMaster);

        var source = new HighwayConnectionSource(new Highway.Client.HighwayOptions
        {
            Server = Multi(master, standby),
            NodeName = "d-client",
        });
        // The simulated partition: this client cannot reach the master.
        source.EndpointReachableOverride = host => !host.Contains($":{master.Port}");

        var moved = await source.TryFailoverAsync();
        moved.Should().BeFalse(
            "the standby's healthy master-link answers 'standby'; its redirect points at a host this client cannot reach — transient, not a coup");
        standby.Replication!.Role.Should().Be(ReplicaRole.Replica);
        standby.Replication.Epoch.Should().Be(1);
        master.Replication!.IsWritable.Should().BeTrue("the real master never noticed");
        await source.DisposeAsync();
    }

    // ------------------------------------------------------------------ D-T4: RPC across failover (parent R11.3)

    /// <summary>
    /// The named R4 scenario: a call is acked and replicated, the master dies before any
    /// host answers it, the caller converges and re-drives the SAME request id, a host
    /// arriving on the new master answers it. The call was never lost — it lived in the
    /// caller across the whole failover.
    /// </summary>
    [Fact]
    public async Task RpcAcrossFailover_CallerRedrivesSameId_AndGetsItsReply()
    {
        var node1 = StartNode("rpc-node1", 1, null);
        using var node2 = StartNode("rpc-node2", 2, node1);
        var multi = Multi(node1, node2);

        // Only the caller is connected at send time — no host to answer, so the call
        // stays IN FLIGHT (the R4 scenario's whole point). It lives in the caller's
        // pending registry, not yet answered anywhere.
        await using var caller = await EngineNode.StartAsync(multi, "rpc-caller");
        var callTask = caller.Client.ExecuteAsync(new ItEchoRequest { Value = "herd-rpc" });
        await WaitForAsync(() => node1.Replication!.Engine.GetLatestSequenceNumber() > 0);

        // The master dies with the call unanswered. The caller's registry is the sole
        // surviving copy; its Converged replay re-drives the SAME id to the successor.
        node1.Dispose();

        // The caller walks to node2 and its Converged replay promotes node2 (the
        // re-driven verb is the herd arriving) — mastership moved with the client.
        await WaitForAsync(() => node2.Replication!.IsWritable, 30_000);
        node2.Replication!.Epoch.Should().Be(2);

        // A host now answers the re-driven call on the new master. (It connects to node2's
        // own endpoint — the multi-endpoint COLD connect against a dead bootstrap entry is
        // a separate concern, exercised by DoublyConfusedClient/HardKill; here the point
        // is that the caller's same-id replay is claimable and answerable post-failover.)
        await using var host = await EngineNode.StartAsync(node2.ConnectionString, "rpc-host");

        var reply = await callTask;
        reply.StatusCode.Should().Be(200, $"the reply must arrive (got {reply.StatusCode}: {reply.Error?.Message})");
        reply.Value.Should().Be("herd-rpc", "the echo proves the same request crossed the failover");
    }

    private static Dictionary<string, string> Fields(RedisResult[] flat)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i + 1 < flat.Length; i += 2)
            map[flat[i].ToString()!] = flat[i + 1].ToString()!;
        return map;
    }
}
