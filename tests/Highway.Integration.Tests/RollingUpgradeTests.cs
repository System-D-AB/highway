using FluentAssertions;
using Highway.Client.Engine;
using Highway.Server;
using Highway.Server.Storage.Rocks;
using StackExchange.Redis;
using Xunit;
using Xunit.Abstractions;

namespace Highway.Integration.Tests;

/// <summary>
/// Feature 050 T8 (R4.4) — the assurance that a rolling upgrade of a two-node set is zero-loss and
/// ends with a healthy attached pair. It exercises the real code end to end: replica-first restart,
/// a GOODBYE hand-off, and the ex-primary <b>auto-rejoining</b> (T2/T3) as a replica of the successor
/// on its next start. "Restart" here is <see cref="HighwayTestServer.Restart"/> — the store reopens at
/// the same port and data directory, running <c>RocksDbStore.Open</c> exactly as a binary swap would,
/// so the rejoin marker is honoured for real.
///
/// <para>Runs with authentication on, which is what makes the auth-tail plumbing (050 T3) load-bearing:
/// the rejoin's snapshot pull targets an endpoint learned at runtime that carries no credentials, so
/// the node authenticates it with its own shared secret.</para>
/// </summary>
public class RollingUpgradeTests : IDisposable
{
    private const string Password = "upgrade-pass";
    private readonly List<string> _dirs = [];
    private readonly ITestOutputHelper _output;

    public RollingUpgradeTests(ITestOutputHelper output) => _output = output;

    public void Dispose()
    {
        foreach (var dir in _dirs)
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { /* best-effort */ }
    }

    private string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hw-upgrade-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _dirs.Add(dir);
        return dir;
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 30_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(50);
        }
        throw new TimeoutException("condition not met");
    }

    private (HighwayTestServer Server, string Dir) StartNode(string id, int priority, HighwayTestServer? pullFrom)
    {
        var dir = NewDir();
        var server = new HighwayTestServer(o =>
        {
            o.DataDir = dir;
            o.Ephemeral = false;
            o.Authentication.Password = Password;
            o.Replication.ReplicaId = id;
            o.Replication.Priority = priority;
            o.Replication.WillingnessThreshold = TimeSpan.FromMilliseconds(400);
            o.Replication.FenceTimeout = TimeSpan.FromMilliseconds(900);
            o.Replication.GoodbyeDrainTimeout = TimeSpan.FromMilliseconds(500);
            if (pullFrom is not null)
            {
                o.Replication.StartAsReplica = true;
                o.Replication.PrimaryServer = pullFrom.ConnectionString;
            }
        });
        return (server, dir);
    }

    private static string Multi(params HighwayTestServer[] nodes)
        => string.Join(',', nodes.Select(n => $"localhost:{n.Port}")) + $",password={Password}";

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

    private static async Task WaitCaughtUpAsync(HighwayTestServer primary, params HighwayTestServer[] standbys)
        => await WaitForAsync(() =>
        {
            var latest = primary.Replication!.Engine.GetLatestSequenceNumber();
            return standbys.All(s => s.Replication!.Engine.GetLatestSequenceNumber() == latest);
        });

    private static bool HasActiveSlot(HighwayTestServer primary, string replicaId)
        => primary.Replication!.Slots.Values.Any(s => s.ReplicaId == replicaId && s.State == SlotState.Active);

    [Fact]
    public async Task RollingUpgrade_ReplicaFirst_GoodbyeHandoff_ExPrimaryAutoRejoins_ZeroLoss()
    {
        var (a, aDir) = StartNode("up-a", 1, pullFrom: null);   // primary
        var (b, _) = StartNode("up-b", 2, pullFrom: a);          // replica of A

        var multi = Multi(a, b);
        await using var c1 = await EngineNode.StartAsync(multi, "up-c1");
        await using var c2 = await EngineNode.StartAsync(multi, "up-c2");
        var clients = new[] { c1, c2 };

        var acked = new List<string>();
        async Task SendRoundAsync(string tag)
        {
            foreach (var c in clients)
                acked.Add(await c.Client.SendAsync(new HerdNote { Tag = tag }));
        }

        await SendRoundAsync("pre");
        await WaitForAsync(() => HasActiveSlot(a, "up-b"));
        await WaitCaughtUpAsync(a, b);

        // ---- Step 1: upgrade the REPLICA first (swap binaries → restart). It rejoins A. ----
        b.Restart();
        await WaitForAsync(() => HasActiveSlot(a, "up-b"), 40_000);
        await SendRoundAsync("after-replica-upgrade");
        await WaitCaughtUpAsync(a, b);

        // ---- Step 2: hand the PRIMARY off with GOODBYE, then swap it. ----
        using (var mux = ConnectionMultiplexer.Connect(a.ConnectionString))
            mux.GetDatabase().Execute("HW.REPL.GOODBYE", "rolling-upgrade");

        // The herd's first accepted verb on B is the promotion; drive it until B is writable.
        await WaitForAsync(() =>
        {
            try
            {
                foreach (var c in clients)
                    acked.Add(c.Client.SendAsync(new HerdNote { Tag = "handoff" }).GetAwaiter().GetResult());
                return b.Replication!.IsWritable;
            }
            catch { return false; }
        });

        await WaitForAsync(() => a.Replication!.Role == ReplicaRole.Demoted);
        // B announced its promotion to A (a roster peer); A learns the new primary and schedules its
        // rejoin — the marker on disk is what the next start honours.
        await WaitForAsync(() => File.Exists(Path.Combine(aDir, ReplicaPuller.RejoinMarkerFileName)));

        // ---- Swap the ex-primary: restart → auto-rejoin as B's replica, no operator step. ----
        a.Restart();
        await WaitForAsync(() => a.Replication!.Role == ReplicaRole.Replica, 40_000);
        await WaitForAsync(() => HasActiveSlot(b, "up-a"), 40_000);
        await SendRoundAsync("post-rejoin");
        await WaitCaughtUpAsync(b, a);

        // ---- A healthy attached pair, roles swapped. ----
        b.Replication!.IsWritable.Should().BeTrue("the successor is the primary");
        a.Replication!.Role.Should().Be(ReplicaRole.Replica, "the ex-primary rejoined as a standby, no failback");
        HasActiveSlot(b, "up-a").Should().BeTrue("the ex-primary is a live attached replica again");

        // ---- Zero client loss: every acked message is present on the final primary. ----
        var claimed = DrainClaims(b, "herd.notes");
        var distinct = claimed.Distinct(StringComparer.Ordinal).ToHashSet();
        distinct.Should().Contain(acked, "a rolling upgrade loses no acked work");
        _output.WriteLine($"acked={acked.Count} claimed={claimed.Count} distinct={distinct.Count} " +
                          $"duplicates={claimed.Count - distinct.Count} (allowed, counted)");

        a.Dispose();
        b.Dispose();
    }
}
