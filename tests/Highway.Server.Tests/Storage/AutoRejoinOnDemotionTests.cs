using FluentAssertions;
using Highway.Server;
using Highway.Server.Storage.Rocks;
using Xunit;

namespace Highway.Server.Tests.Storage;

/// <summary>
/// Feature 050 T2/T3 (F2) — auto-rejoin of a demoted ex-primary. When a node that believed itself
/// primary observes a higher epoch it demotes (fencing, unchanged) and — if it knows where the new
/// primary is and auto-rejoin is on — writes a <c>rejoin-required</c> marker so that, on the next
/// restart, <see cref="RocksDbStore.Open"/> wipes and re-syncs it as that primary's replica.
/// These tests cover the marker's write conditions and round-trip; the wipe→snapshot→attach path is
/// exercised end to end by the herd rig (T8).
/// </summary>
public class AutoRejoinOnDemotionTests
{
    private static HighwayReplicationOptions Primary(string id, int priority, string endpoint, bool autoRejoin = true) => new()
    {
        StartAsReplica = false,
        Priority = priority,
        ReplicaId = id,
        AdvertiseEndpoint = endpoint,
        AutoRejoin = autoRejoin,
    };

    private static HighwayReplicationOptions Replica(string id, int priority, string endpoint) => new()
    {
        StartAsReplica = true,
        Priority = priority,
        ReplicaId = id,
        AdvertiseEndpoint = endpoint,
    };

    [Fact]
    public void MarkerRoundTrips()
    {
        var dir = ReplicationToolkitSurfaceTests.NewTempDir();
        try
        {
            ReplicaPuller.ReadRejoinMarker(dir).Should().BeNull("no marker yet");

            ReplicaPuller.WriteRejoinMarker(dir, "10.0.0.9:6500", 42);

            var marker = ReplicaPuller.ReadRejoinMarker(dir);
            marker.Should().NotBeNull();
            marker!.Value.Endpoint.Should().Be("10.0.0.9:6500");
            marker.Value.Epoch.Should().Be(42UL);
        }
        finally { ReplicationToolkitSurfaceTests.TryDelete(dir); }
    }

    [Fact]
    public void Demotion_WithKnownPrimary_WritesRejoinMarker_AndFiresHook()
    {
        var dir = ReplicationToolkitSurfaceTests.NewTempDir();
        try
        {
            using var store = RocksDbStore.Open(dir, replication: Primary("node-a", 50, "10.0.0.1:6500"));
            var feeder = store.Replication;
            var fired = 0;
            feeder.RejoinRequested = () => Interlocked.Increment(ref fired);
            feeder.Role.Should().Be(ReplicaRole.Primary);

            // The successor announced its promotion (HELLO carries its endpoint as the 4th arg).
            feeder.ObserveHigherEpoch(feeder.Epoch + 1, "successor promoted", "10.0.0.9:6500");

            feeder.Role.Should().Be(ReplicaRole.Demoted, "a higher epoch fences the old primary");
            var marker = ReplicaPuller.ReadRejoinMarker(store.DataDir);
            marker.Should().NotBeNull("a demoted ex-primary that knows the new primary schedules a rejoin");
            marker!.Value.Endpoint.Should().Be("10.0.0.9:6500");
            fired.Should().Be(1, "the host is asked once to restart into the rejoin");
        }
        finally { ReplicationToolkitSurfaceTests.TryDelete(dir); }
    }

    [Fact]
    public void Demotion_WithoutEndpoint_SchedulesRejoinWhenTheEndpointIsLearnedLater()
    {
        var dir = ReplicationToolkitSurfaceTests.NewTempDir();
        try
        {
            using var store = RocksDbStore.Open(dir, replication: Primary("node-a", 50, "10.0.0.1:6500"));
            var feeder = store.Replication;

            // A bare-epoch observation (no endpoint) demotes but cannot yet name the primary.
            feeder.ObserveHigherEpoch(feeder.Epoch + 1, "higher epoch, no endpoint");
            feeder.Role.Should().Be(ReplicaRole.Demoted);
            ReplicaPuller.ReadRejoinMarker(store.DataDir).Should().BeNull("no endpoint known yet → nothing to rejoin");

            var fired = 0;
            feeder.RejoinRequested = () => Interlocked.Increment(ref fired);

            // The promotion announce arrives afterwards, at the same epoch, carrying the endpoint.
            feeder.ObserveHigherEpoch(feeder.Epoch, "announce carries the endpoint", "10.0.0.9:6500");

            ReplicaPuller.ReadRejoinMarker(store.DataDir).Should()
                .NotBeNull("learning the endpoint after demotion still schedules the rejoin");
            fired.Should().Be(1);
        }
        finally { ReplicationToolkitSurfaceTests.TryDelete(dir); }
    }

    [Fact]
    public void AutoRejoinDisabled_DemotesButWritesNoMarker()
    {
        var dir = ReplicationToolkitSurfaceTests.NewTempDir();
        try
        {
            using var store = RocksDbStore.Open(dir, replication: Primary("node-a", 50, "10.0.0.1:6500", autoRejoin: false));
            var feeder = store.Replication;

            feeder.ObserveHigherEpoch(feeder.Epoch + 1, "successor promoted", "10.0.0.9:6500");

            feeder.Role.Should().Be(ReplicaRole.Demoted, "fencing is unchanged; only the rejoin is opt-out");
            ReplicaPuller.ReadRejoinMarker(store.DataDir).Should()
                .BeNull("auto-rejoin off → the node holds in Demoted for manual recovery");
        }
        finally { ReplicationToolkitSurfaceTests.TryDelete(dir); }
    }

    [Fact]
    public void ReplicaAdoptingAHigherEpoch_WritesNoMarker()
    {
        var dir = ReplicationToolkitSurfaceTests.NewTempDir();
        try
        {
            using var store = RocksDbStore.Open(dir, replication: Replica("node-b", 100, "10.0.0.2:6500"));
            var feeder = store.Replication;
            feeder.Role.Should().Be(ReplicaRole.Replica);

            feeder.ObserveHigherEpoch(feeder.Epoch + 2, "primary promoted", "10.0.0.9:6500");

            feeder.Role.Should().Be(ReplicaRole.Replica, "a replica adopts a higher epoch, it does not demote");
            ReplicaPuller.ReadRejoinMarker(store.DataDir).Should()
                .BeNull("only a demoting ex-primary schedules a rejoin, never a replica");
        }
        finally { ReplicationToolkitSurfaceTests.TryDelete(dir); }
    }
}
