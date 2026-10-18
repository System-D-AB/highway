using FluentAssertions;
using Highway.Server.Storage.Crystal;
using Highway.Server.Storage.Replication;
using Xunit;

namespace Highway.Server.Tests.Storage;

/// <summary>
/// 076, the independent review of the port: each finding fixed on the branch is pinned here so it cannot come back.
/// </summary>
public class CrystalPolicyHardeningTests
{
    private static HighwayReplicationOptions Standby(TimeProvider? clock = null) => new()
    {
        StartAsReplica = true,
        Priority = 10,
        WillingnessThreshold = TimeSpan.FromSeconds(3),
        Clock = clock ?? TimeProvider.System,
    };

    [Fact]
    public void AStandbyHoldingNoneOfItsPrimarysData_IsNeverWilling()
    {
        // H1: a standby still bootstrapping serves RESP (on v3.0 it was still inside Open). With nothing replicated it
        // must not take the herd, however long its master has been silent.
        var clock = new ManualClock();
        var dir = TempDirs.NewTempDir();
        try
        {
            using var store = CrystalHighwayStore.Open(dir, replication: Standby(clock));
            clock.Advance(TimeSpan.FromMinutes(5));
            store.Replication.IsWillingForHerd().Should().BeFalse("it holds nothing to serve");
        }
        finally { TempDirs.TryDelete(dir); }
    }

    [Theory]
    [InlineData("0.0.0.0:6501", "192.0.2.1:6500,password=x", "192.0.2.1:6501")]
    [InlineData("[::]:6501", "192.0.2.1:6500", "192.0.2.1:6501")]
    [InlineData("192.0.2.7:6601", "192.0.2.1:6500", "192.0.2.7:6601")]
    [InlineData("", "192.0.2.1:6500", "")]
    public void AStandby_DialsAnUnspecifiedReplicationHostAtThePrimarysOwnHost(string named, string primaryServer, string expected)
        => StandbyAgent.Reachable(named, primaryServer).Should().Be(expected);   // H1

    [Fact]
    public void AnImplausiblyHighEpoch_IsIgnored_AndPromotionStaysOneStep()
    {
        // M2: a handshake carrying an epoch far ahead of any real history must not make the next promotion burn through
        // epochs (or never finish).
        var dir = TempDirs.NewBootstrappedDir();
        try
        {
            using var store = CrystalHighwayStore.Open(dir, replication: Standby());
            var feeder = store.Replication;
            var before = feeder.Epoch;

            feeder.ObserveHigherEpoch(ulong.MaxValue, "hostile handshake");
            feeder.Epoch.Should().Be(before, "an epoch more than MaxEpochLead ahead is not adopted");
            feeder.Transitions.Should().Contain(t => t.StartsWith("epoch-ignored"));

            feeder.TryPromote("herd", out var error).Should().BeTrue(error);
            feeder.Epoch.Should().Be(before + 1);
        }
        finally { TempDirs.TryDelete(dir); }
    }

    [Fact]
    public void AnAdoptedEpoch_SurvivesARestart_SoTheNextPromotionPassesIt()
    {
        // L2: v3.0 persisted the epoch a standby adopted from an announce; so does 076.
        var dir = TempDirs.NewBootstrappedDir();
        try
        {
            using (var store = CrystalHighwayStore.Open(dir, replication: Standby()))
                store.Replication.ObserveHigherEpoch(store.Replication.Epoch + 3, "announce");

            using var reopened = CrystalHighwayStore.Open(dir, replication: Standby());
            var heard = reopened.Replication.Epoch;
            reopened.Replication.TryPromote("herd", out var error).Should().BeTrue(error);
            reopened.Replication.Epoch.Should().BeGreaterThan(heard, "a promotion passes every epoch this node has heard of");
        }
        finally { TempDirs.TryDelete(dir); }
    }

    [Fact]
    public void AConfiguredPrimary_ThatStoodDown_ComesBackPrimaryOnRestart()
    {
        // M1: v3.0 restarted a configured primary as primary (a GOODBYE on a lone node, a config flipped from standby).
        var clock = new ManualClock();
        var dir = TempDirs.NewTempDir();
        try
        {
            using (var store = CrystalHighwayStore.Open(dir, replication: new HighwayReplicationOptions { Clock = clock }))
            {
                store.Replication.BeginGoodbye("maintenance");
                store.Replication.TickGoodbye();   // no clients: the drain completes at once
                store.Replication.Role.Should().Be(ReplicaRole.Demoted);
            }

            using var reopened = CrystalHighwayStore.Open(dir);
            reopened.Replication.Role.Should().Be(ReplicaRole.Primary);
            reopened.Replication.IsWritable.Should().BeTrue();
        }
        finally { TempDirs.TryDelete(dir); }
    }

    [Fact]
    public void ARejoinRecord_IsIgnoredAndCleared_WhenAutoRejoinIsOff()
    {
        // L4: the operator's config decides once auto-rejoin is off.
        var dir = TempDirs.NewTempDir();
        try
        {
            using (var store = CrystalHighwayStore.Open(dir))
                NodeRecord.SetRejoin(store.Crystal, "192.0.2.9:6500");

            using var reopened = CrystalHighwayStore.Open(dir, replication: new HighwayReplicationOptions { AutoRejoin = false });
            reopened.Replication.Role.Should().Be(ReplicaRole.Primary);
            NodeRecord.ReadRejoin(reopened.Crystal).Should().BeNull();
        }
        finally { TempDirs.TryDelete(dir); }
    }

    [Fact]
    public void TheCommandFastPath_TakesNoRoleLock_WhenNothingIsDraining()
    {
        // M3: every command ticks the policy; while a promotion holds the role lock (Crystal stops its follower), other
        // commands must not queue behind it.
        var dir = TempDirs.NewTempDir();
        try
        {
            using var store = CrystalHighwayStore.Open(dir);
            var feeder = store.Replication;
            var gate = typeof(HighwayReplication).GetField("_roleLock", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(feeder)!;
            using var held = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var holder = Task.Run(() => { using (((Lock)gate).EnterScope()) { held.Set(); release.Wait(); } });
            held.Wait();
            try
            {
                var tick = Task.Run(feeder.Tick);
                tick.Wait(TimeSpan.FromSeconds(2)).Should().BeTrue("Tick does not wait for the role lock");
            }
            finally { release.Set(); holder.Wait(); }
        }
        finally { TempDirs.TryDelete(dir); }
    }
}
