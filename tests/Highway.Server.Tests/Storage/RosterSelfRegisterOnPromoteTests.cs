using FluentAssertions;
using Highway.Server;
using Highway.Server.Storage.Rocks;
using Xunit;

namespace Highway.Server.Tests.Storage;

/// <summary>
/// Feature 050 T1 (F1) — a node that <b>promotes</b> at runtime self-registers in the replicated
/// roster, the same record a node that <b>starts</b> primary already writes. Before this fix only a
/// start-writable node registered, so after a herd-arrival failover the roster went empty exactly
/// when the cluster needed its own membership map: a blank succession view and a lost successor
/// priority order (the F1 gap 050 closes).
/// </summary>
public class RosterSelfRegisterOnPromoteTests
{
    private static HighwayReplicationOptions Replica(string id, int priority, string endpoint) => new()
    {
        StartAsReplica = true,
        Priority = priority,
        ReplicaId = id,
        AdvertiseEndpoint = endpoint,
    };

    [Fact]
    public void Promote_RegistersSelfInTheRoster()
    {
        var dir = ReplicationToolkitSurfaceTests.NewTempDir();
        try
        {
            using var store = RocksDbStore.Open(dir, replication: Replica("node-b", 100, "10.0.0.2:6500"));
            var feeder = store.Replication;

            RosterStore.Read(store).Members.Should()
                .BeEmpty("a replica that never promoted is not in the roster");

            feeder.TryPromote("herd-arrival", out var error).Should().BeTrue(error);

            var roster = RosterStore.Read(store);
            roster.Members.Should().ContainSingle("the promoted node registers itself")
                .Which.Should().BeEquivalentTo(new RosterMember("node-b", 100, "10.0.0.2:6500"));
        }
        finally { ReplicationToolkitSurfaceTests.TryDelete(dir); }
    }

    [Fact]
    public void Promote_IsIdempotent_NeverDuplicatesTheEntry()
    {
        var dir = ReplicationToolkitSurfaceTests.NewTempDir();
        try
        {
            using var store = RocksDbStore.Open(dir, replication: Replica("node-b", 100, "10.0.0.2:6500"));
            var feeder = store.Replication;

            feeder.TryPromote("first", out _).Should().BeTrue();
            // Re-invoking the registration hook (re-announce / re-promote) must upsert on the node
            // id, never append a second row or corrupt the record.
            feeder.RegisterSelfInRoster!.Invoke();

            var roster = RosterStore.Read(store);
            roster.Members.Should().ContainSingle("re-registration upserts on the node id, never appends");
            roster.Members[0].NodeId.Should().Be("node-b");
        }
        finally { ReplicationToolkitSurfaceTests.TryDelete(dir); }
    }

    [Fact]
    public void PromotedNode_JoinsAnExistingRoster_WithoutDroppingPeers()
    {
        var dir = ReplicationToolkitSurfaceTests.NewTempDir();
        try
        {
            using var store = RocksDbStore.Open(dir, replication: Replica("node-b", 100, "10.0.0.2:6500"));

            // A peer (the former primary) is already in the replicated roster.
            RosterStore.TryUpsert(store, new RosterMember("node-a", 50, "10.0.0.1:6500"), out _, out var holder)
                .Should().BeTrue(holder);

            store.Replication.TryPromote("herd-arrival", out var error).Should().BeTrue(error);

            var roster = RosterStore.Read(store);
            roster.Members.Select(m => m.NodeId).Should()
                .BeEquivalentTo(["node-a", "node-b"], "promotion adds self without dropping known peers");
        }
        finally { ReplicationToolkitSurfaceTests.TryDelete(dir); }
    }
}
