using FluentAssertions;
using Highway.Server;
using Highway.Server.Storage.Rocks;
using Xunit;

namespace Highway.Server.Tests.Storage;

/// <summary>
/// Feature 050 T4 (F3) — stale slots age out on role change and on silence. A demoted node holds no
/// replicas (its slots are cleared at once), and a slot on a live primary that stops making contact
/// is dropped after <c>SlotStaleAfter</c> even when it is under the lag cap — so the dashboard never
/// shows a phantom "Active" replica that is not actually streaming.
/// </summary>
public class SlotAgingTests
{
    private static HighwayReplicationOptions Primary(ManualClock clock, TimeSpan? staleAfter = null) => new()
    {
        StartAsReplica = false,
        Priority = 50,
        ReplicaId = "node-a",
        AdvertiseEndpoint = "10.0.0.1:6500",
        Clock = clock,
        SlotStaleAfter = staleAfter ?? TimeSpan.FromSeconds(30),
    };

    [Fact]
    public void Demotion_ClearsSlots()
    {
        var dir = ReplicationToolkitSurfaceTests.NewTempDir();
        try
        {
            using var store = RocksDbStore.Open(dir, replication: Primary(new ManualClock()));
            var feeder = store.Replication;
            feeder.Hello("replica-1", 0, feeder.Epoch);
            feeder.Slots.Should().ContainKey("replica-1");

            feeder.ObserveHigherEpoch(feeder.Epoch + 1, "successor promoted", "10.0.0.9:6500");

            feeder.Role.Should().Be(ReplicaRole.Demoted);
            feeder.Slots.Should().BeEmpty("a demoted node holds no replicas — the phantom slot is dropped at once");
        }
        finally { ReplicationToolkitSurfaceTests.TryDelete(dir); }
    }

    [Fact]
    public void SilentSlot_AgesToDropped_PastTheBound()
    {
        var dir = ReplicationToolkitSurfaceTests.NewTempDir();
        try
        {
            var clock = new ManualClock();
            using var store = RocksDbStore.Open(dir, replication: Primary(clock, staleAfter: TimeSpan.FromSeconds(30)));
            var feeder = store.Replication;
            feeder.Hello("replica-1", 0, feeder.Epoch);
            feeder.Slots["replica-1"].State.Should().Be(SlotState.Active);

            clock.Advance(TimeSpan.FromSeconds(31));   // no contact past the bound
            feeder.EnforceCap();

            feeder.Slots["replica-1"].State.Should().Be(SlotState.Dropped, "a silent replica must not read as Active");
        }
        finally { ReplicationToolkitSurfaceTests.TryDelete(dir); }
    }

    [Fact]
    public void RecentlyContactedSlot_StaysActive()
    {
        var dir = ReplicationToolkitSurfaceTests.NewTempDir();
        try
        {
            var clock = new ManualClock();
            using var store = RocksDbStore.Open(dir, replication: Primary(clock, staleAfter: TimeSpan.FromSeconds(30)));
            var feeder = store.Replication;
            feeder.Hello("replica-1", 0, feeder.Epoch);

            clock.Advance(TimeSpan.FromSeconds(10));   // still within the bound
            feeder.EnforceCap();

            feeder.Slots["replica-1"].State.Should().Be(SlotState.Active);
        }
        finally { ReplicationToolkitSurfaceTests.TryDelete(dir); }
    }

    [Fact]
    public void StatusRead_RefreshesStaleness_SoNoPhantomIsReported()
    {
        var dir = ReplicationToolkitSurfaceTests.NewTempDir();
        try
        {
            var clock = new ManualClock();
            using var store = RocksDbStore.Open(dir, replication: Primary(clock, staleAfter: TimeSpan.FromSeconds(30)));
            var feeder = store.Replication;
            feeder.Hello("replica-1", 0, feeder.Epoch);

            clock.Advance(TimeSpan.FromSeconds(31));
            _ = feeder.StatsFields();   // the dashboard / HW.REPL.STATUS read path

            feeder.Slots["replica-1"].State.Should().Be(SlotState.Dropped, "a status read refreshes slot staleness");
        }
        finally { ReplicationToolkitSurfaceTests.TryDelete(dir); }
    }

    [Fact]
    public void SlotStaleAfterZero_DisablesTheTimeRule()
    {
        var dir = ReplicationToolkitSurfaceTests.NewTempDir();
        try
        {
            var clock = new ManualClock();
            using var store = RocksDbStore.Open(dir, replication: Primary(clock, staleAfter: TimeSpan.Zero));
            var feeder = store.Replication;
            feeder.Hello("replica-1", 0, feeder.Epoch);

            clock.Advance(TimeSpan.FromHours(1));
            feeder.EnforceCap();

            feeder.Slots["replica-1"].State.Should().Be(SlotState.Active, "time-based staleness is disabled at zero");
        }
        finally { ReplicationToolkitSurfaceTests.TryDelete(dir); }
    }
}
