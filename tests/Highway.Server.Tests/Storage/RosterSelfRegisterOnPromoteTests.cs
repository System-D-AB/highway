using Highway.Server.Storage.Crystal;
using FluentAssertions;
using Highway.Server;
using Highway.Server.Storage.Replication;
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
        var dir = TempDirs.NewTempDir();
        try
        {
            using var store = CrystalHighwayStore.Open(dir, replication: Replica("node-b", 100, "192.0.2.2:6500"));
            var feeder = store.Replication;

            RosterStore.Read(store).Members.Should()
                .BeEmpty("a replica that never promoted is not in the roster");

            feeder.TryPromote("herd-arrival", out var error).Should().BeTrue(error);

            var roster = RosterStore.Read(store);
            roster.Members.Should().ContainSingle("the promoted node registers itself")
                .Which.Should().BeEquivalentTo(new RosterMember("node-b", 100, "192.0.2.2:6500"));
        }
        finally { TempDirs.TryDelete(dir); }
    }

    [Fact]
    public void Promote_IsIdempotent_NeverDuplicatesTheEntry()
    {
        var dir = TempDirs.NewTempDir();
        try
        {
            using var store = CrystalHighwayStore.Open(dir, replication: Replica("node-b", 100, "192.0.2.2:6500"));
            var feeder = store.Replication;

            feeder.TryPromote("first", out _).Should().BeTrue();
            // Re-invoking the registration hook (re-announce / re-promote) must upsert on the node
            // id, never append a second row or corrupt the record.
            feeder.RegisterSelfInRoster!.Invoke();

            var roster = RosterStore.Read(store);
            roster.Members.Should().ContainSingle("re-registration upserts on the node id, never appends");
            roster.Members[0].NodeId.Should().Be("node-b");
        }
        finally { TempDirs.TryDelete(dir); }
    }

    [Fact]
    public void PromotedNode_JoinsAnExistingRoster_WithoutDroppingPeers()
    {
        var dir = TempDirs.NewTempDir();
        try
        {
            // A peer (the former primary) is already in the replicated roster. 076: a standby's store refuses local
            // writes (its data is the primary's), so the roster is written while the node is writable, as replication
            // would have delivered it, and the node then restarts as a standby.
            using (var seed = CrystalHighwayStore.Open(dir))
                RosterStore.TryUpsert(seed, new RosterMember("node-a", 50, "192.0.2.1:6500"), out _, out var holder)
                    .Should().BeTrue(holder);

            using var store = CrystalHighwayStore.Open(dir, replication: Replica("node-b", 100, "192.0.2.2:6500"));

            store.Replication.TryPromote("herd-arrival", out var error).Should().BeTrue(error);

            var roster = RosterStore.Read(store);
            roster.Members.Select(m => m.NodeId).Should()
                .BeEquivalentTo(["node-a", "node-b"], "promotion adds self without dropping known peers");
        }
        finally { TempDirs.TryDelete(dir); }
    }
}
