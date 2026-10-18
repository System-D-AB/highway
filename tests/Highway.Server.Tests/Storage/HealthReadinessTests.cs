using Highway.Server.Storage.Crystal;
using FluentAssertions;
using Highway.Server.Storage.Replication;
using Xunit;

namespace Highway.Server.Tests.Storage;

/// <summary>
/// Feature 052 R2 — the readiness decision the <c>/ready</c> endpoint serves is computed by the
/// feeder (server-side, fresh per read) and rides <c>StatsFields</c>: a writable, un-fenced,
/// non-draining, fully-bootstrapped primary is ready; every other role/state names why it is not.
/// </summary>
public class HealthReadinessTests
{
    private static HighwayReplicationOptions Opts(ManualClock clock, bool replica) => new()
    {
        StartAsReplica = replica,
        Priority = 100,
        ReplicaId = "node-a",
        Clock = clock,
    };

    private static void With(bool replica, Action<HighwayReplication> act)
    {
        var dir = TempDirs.NewTempDir();
        try
        {
            using var store = CrystalHighwayStore.Open(dir, replication: Opts(new ManualClock(), replica));
            act(store.Replication);
        }
        finally { TempDirs.TryDelete(dir); }
    }

    [Fact]
    public void APrimary_IsReady_AndSaysSoOnStatsFields()
        => With(replica: false, feeder =>
        {
            feeder.Readiness().Should().Be((true, "primary"));
            var stats = feeder.StatsFields().ToDictionary(f => f.Name, f => f.Value, StringComparer.Ordinal);
            stats["repl.ready"].Should().Be("True");
            stats["repl.readyReason"].Should().Be("primary");
        });

    [Fact]
    public void AReplica_IsNotReady_Reason_Replica()
    {
        var dir = TempDirs.NewBootstrappedDir();   // a standby with its data on disk: a standby from start
        try
        {
            using var store = CrystalHighwayStore.Open(dir, replication: Opts(new ManualClock(), replica: true));
            store.Replication.Readiness().Should().Be((false, "replica"));
        }
        finally { TempDirs.TryDelete(dir); }
    }

    [Fact]
    public void ABlankReplica_IsSyncing_Reason_Bootstrapping()
        => With(replica: true, feeder => feeder.Readiness().Should().Be((false, "bootstrapping"),
            "a node with none of its primary's data is syncing, not a standby"));

    [Fact]
    public void AFencedNode_IsNotReady_Reason_Fenced()
        => With(replica: false, feeder =>
        {
            feeder.Fence("test");
            feeder.Readiness().Should().Be((false, "fenced"));
        });

    [Fact]
    public void ADemotedNode_IsNotReady_Reason_Demoted()
        => With(replica: false, feeder =>
        {
            feeder.ObserveHigherEpoch(feeder.Epoch + 1, "a higher epoch was observed");
            feeder.Readiness().Should().Be((false, "demoted"));
        });

    [Fact]
    public void ABootstrappingNode_IsNotReady_Reason_Bootstrapping()
        => With(replica: false, feeder =>
        {
            feeder.NoteResyncRequired(watermark: 5, "WAL gap");
            feeder.Readiness().Should().Be((false, "bootstrapping"));
        });

    [Fact]
    public void ADrainingPrimary_IsNotReady_Reason_Draining()
        => With(replica: false, feeder =>
        {
            feeder.ClientSessionOpened();      // a client is present, so the drain does not complete instantly
            feeder.BeginGoodbye("upgrade");
            feeder.IsDraining.Should().BeTrue();
            feeder.Readiness().Should().Be((false, "draining"));
        });
}
