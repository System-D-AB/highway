using FluentAssertions;
using Highway.Server;
using Highway.Server.Storage.Rocks;
using Xunit;

namespace Highway.Server.Tests.Storage;

/// <summary>
/// Feature 044 T4 — the feeder's <c>OnEpochChanged</c> hook, the sole coupling between the herd
/// and the cache. It must fire exactly once whenever mastership moves (a promote bumps the epoch,
/// or a higher epoch is adopted) and never when the epoch is unchanged (a herd-holding master
/// through a peer-only partition). The cache subscribes to it and wipes; nothing else depends on it.
/// </summary>
public class CacheEpochHookTests
{
    [Fact]
    public void Promote_RaisesOnEpochChanged_Once_AndBumpsTheEpoch()
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
            var before = feeder.Epoch;
            var fired = 0;
            feeder.OnEpochChanged += () => Interlocked.Increment(ref fired);

            feeder.TryPromote("test", out var error).Should().BeTrue(error);

            feeder.Epoch.Should().Be(before + 1);
            fired.Should().Be(1, "a promotion moved mastership → wipe exactly once");
        }
        finally { ReplicationToolkitSurfaceTests.TryDelete(dir); }
    }

    [Fact]
    public void AdoptingAHigherEpoch_RaisesOnEpochChanged_Once()
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
            var fired = 0;
            feeder.OnEpochChanged += () => Interlocked.Increment(ref fired);

            feeder.ObserveHigherEpoch(feeder.Epoch + 3, "primary promoted elsewhere");

            fired.Should().Be(1, "adopting a higher epoch means another node may have mutated data → wipe");
        }
        finally { ReplicationToolkitSurfaceTests.TryDelete(dir); }
    }

    [Fact]
    public void NoEpochChange_RaisesNothing()
    {
        var dir = ReplicationToolkitSurfaceTests.NewTempDir();
        try
        {
            // A default (primary) broker holding its herd — no mastership move.
            using var store = RocksDbStore.Open(dir);
            var feeder = store.Replication;
            var fired = 0;
            feeder.OnEpochChanged += () => Interlocked.Increment(ref fired);

            // Observing its own (equal) epoch — the peer-only-partition case — must not wipe.
            feeder.ObserveHigherEpoch(feeder.Epoch, "peer contact, same epoch");
            // And a lower epoch is stale gossip, ignored.
            if (feeder.Epoch > 0)
                feeder.ObserveHigherEpoch(feeder.Epoch - 1, "stale gossip");

            fired.Should().Be(0, "a master that keeps its herd without an epoch change keeps its cache");
        }
        finally { ReplicationToolkitSurfaceTests.TryDelete(dir); }
    }
}
