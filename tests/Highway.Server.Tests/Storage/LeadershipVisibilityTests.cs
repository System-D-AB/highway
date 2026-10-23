using Highway.Server.Storage.Crystal;
using FluentAssertions;
using Highway.Server;
using Highway.Server.Storage.Replication;
using Xunit;

namespace Highway.Server.Tests.Storage;

/// <summary>
/// Feature 050 T5 (F6/R7) — a leadership change is loud on the replication status: the redundancy
/// state (the two degraded cases vs. an intentional single node), when the current leadership epoch
/// began, and a short event timeline. These are the fields the dashboard banner, clients and later
/// the metrics/health pillars read.
/// </summary>
public class LeadershipVisibilityTests
{
    private static Dictionary<string, string> Stats(HighwayReplication feeder)
        => feeder.StatsFields().ToDictionary(f => f.Name, f => f.Value, StringComparer.Ordinal);

    [Fact]
    public void Redundancy_Single_ForALonePrimaryWithNoRoster()
    {
        var dir = TempDirs.NewTempDir();
        try
        {
            using var store = CrystalHighwayStore.Open(dir);   // default: primary, empty roster
            Stats(store.Replication)["repl.redundancy"].Should().Be("single", "a lone node expects no standby");
        }
        finally { TempDirs.TryDelete(dir); }
    }

    [Fact]
    public void Redundancy_NoStandby_WhenRosterExpectsAStandbyButNoneIsAttached()
    {
        var dir = TempDirs.NewTempDir();
        try
        {
            using var store = CrystalHighwayStore.Open(dir);   // primary
            RosterStore.TryUpsert(store, new RosterMember("node-a", 50, "192.0.2.1:6500"), out _, out _);
            RosterStore.TryUpsert(store, new RosterMember("node-b", 100, "192.0.2.2:6500"), out _, out _);

            Stats(store.Replication)["repl.redundancy"].Should().Be("no-standby", "roster expects a standby, none attached");
        }
        finally { TempDirs.TryDelete(dir); }
    }

    [Fact]
    public void Redundancy_IsNotHealthy_WhileAStandbySaysHelloButReceivesNothing()
    {
        // 076 (review H1): a hello proves the standby reaches RESP, not that it replicates. Until the replication layer
        // shows it pulling, its slot is not Active and the set has no standby. The healthy case — a standby that follows —
        // is proven end to end by ReplicationPairTests and RollingUpgradeTests.
        var dir = TempDirs.NewTempDir();
        try
        {
            using var store = CrystalHighwayStore.Open(dir);
            RosterStore.TryUpsert(store, new RosterMember("node-a", 50, "192.0.2.1:6500"), out _, out _);
            RosterStore.TryUpsert(store, new RosterMember("node-b", 100, "192.0.2.2:6500"), out _, out _);
            store.Replication.Hello("node-b", 0, store.Replication.Epoch, crystalNodeId: Guid.NewGuid().ToString());

            Stats(store.Replication)["repl.redundancy"].Should().Be("no-standby");
            store.Replication.Slots["node-b"].State.Should().Be(SlotState.Lagging);
            store.Replication.IsDataLive(null).Should().BeFalse();
        }
        finally { TempDirs.TryDelete(dir); }
    }

    [Fact]
    public void Redundancy_Demoted_AfterBeingSuperseded()
    {
        var dir = TempDirs.NewTempDir();
        try
        {
            using var store = CrystalHighwayStore.Open(dir);
            store.Replication.ObserveHigherEpoch(store.Replication.Epoch + 1, "superseded", "192.0.2.9:6500");

            Stats(store.Replication)["repl.redundancy"].Should().Be("demoted");
        }
        finally { TempDirs.TryDelete(dir); }
    }

    [Fact]
    public void LeadershipSince_And_EventTimeline_SetOnPromote()
    {
        var dir = TempDirs.NewTempDir();
        try
        {
            using var store = CrystalHighwayStore.Open(dir, replication: new HighwayReplicationOptions
            {
                StartAsReplica = true,
                Priority = 100,
            });
            var feeder = store.Replication;

            feeder.TryPromote("herd-arrival", out var error).Should().BeTrue(error);

            var stats = Stats(feeder);
            stats["repl.leadershipSince"].Should().NotBeNullOrEmpty("promotion establishes a leadership epoch");
            stats.Should().ContainKey("repl.event.0", "recent role changes are surfaced as a timeline");
            stats.Values.Should().Contain(v => v.StartsWith("promote", StringComparison.Ordinal));
        }
        finally { TempDirs.TryDelete(dir); }
    }
}
