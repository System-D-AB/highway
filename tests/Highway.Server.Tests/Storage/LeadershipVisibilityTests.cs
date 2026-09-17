using FluentAssertions;
using Highway.Server;
using Highway.Server.Storage.Rocks;
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
    private static Dictionary<string, string> Stats(ReplicationFeeder feeder)
        => feeder.StatsFields().ToDictionary(f => f.Name, f => f.Value, StringComparer.Ordinal);

    [Fact]
    public void Redundancy_Single_ForALonePrimaryWithNoRoster()
    {
        var dir = ReplicationToolkitSurfaceTests.NewTempDir();
        try
        {
            using var store = RocksDbStore.Open(dir);   // default: primary, empty roster
            Stats(store.Replication)["repl.redundancy"].Should().Be("single", "a lone node expects no standby");
        }
        finally { ReplicationToolkitSurfaceTests.TryDelete(dir); }
    }

    [Fact]
    public void Redundancy_NoStandby_WhenRosterExpectsAStandbyButNoneIsAttached()
    {
        var dir = ReplicationToolkitSurfaceTests.NewTempDir();
        try
        {
            using var store = RocksDbStore.Open(dir);   // primary
            RosterStore.TryUpsert(store, new RosterMember("node-a", 50, "10.0.0.1:6500"), out _, out _);
            RosterStore.TryUpsert(store, new RosterMember("node-b", 100, "10.0.0.2:6500"), out _, out _);

            Stats(store.Replication)["repl.redundancy"].Should().Be("no-standby", "roster expects a standby, none attached");
        }
        finally { ReplicationToolkitSurfaceTests.TryDelete(dir); }
    }

    [Fact]
    public void Redundancy_Healthy_WithAnActiveStandbyAttached()
    {
        var dir = ReplicationToolkitSurfaceTests.NewTempDir();
        try
        {
            using var store = RocksDbStore.Open(dir);
            RosterStore.TryUpsert(store, new RosterMember("node-a", 50, "10.0.0.1:6500"), out _, out _);
            RosterStore.TryUpsert(store, new RosterMember("node-b", 100, "10.0.0.2:6500"), out _, out _);
            store.Replication.Hello("node-b", 0, store.Replication.Epoch);   // a standby attaches

            Stats(store.Replication)["repl.redundancy"].Should().Be("healthy");
        }
        finally { ReplicationToolkitSurfaceTests.TryDelete(dir); }
    }

    [Fact]
    public void Redundancy_Demoted_AfterBeingSuperseded()
    {
        var dir = ReplicationToolkitSurfaceTests.NewTempDir();
        try
        {
            using var store = RocksDbStore.Open(dir);
            store.Replication.ObserveHigherEpoch(store.Replication.Epoch + 1, "superseded", "10.0.0.9:6500");

            Stats(store.Replication)["repl.redundancy"].Should().Be("demoted");
        }
        finally { ReplicationToolkitSurfaceTests.TryDelete(dir); }
    }

    [Fact]
    public void LeadershipSince_And_EventTimeline_SetOnPromote()
    {
        var dir = ReplicationToolkitSurfaceTests.NewTempDir();
        try
        {
            using var store = RocksDbStore.Open(dir, replication: new HighwayReplicationOptions
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
        finally { ReplicationToolkitSurfaceTests.TryDelete(dir); }
    }
}
