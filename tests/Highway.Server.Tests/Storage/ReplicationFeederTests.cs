using System.Text;
using FluentAssertions;
using Highway.Server;
using Highway.Server.Storage;
using Highway.Server.Storage.Rocks;
using Xunit;

namespace Highway.Server.Tests.Storage;

/// <summary>042 T1 — feeder paging, epoch stamp, slot floor. No RESP.</summary>
public class ReplicationFeederTests
{
    [Fact]
    public void Pull_AfterSyncPuts_ReturnsWalPagesInOrder()
    {
        var dir = ReplicationToolkitSurfaceTests.NewTempDir();
        try
        {
            using var store = RocksDbStore.Open(dir);
            Put(store, "a", "1");
            Put(store, "b", "2");

            var page = store.Replication.Pull(0, maxBytes: 1_000_000);
            page.Epoch.Should().Be(1);
            page.Batches.Should().NotBeEmpty();
            page.NextSeq.Should().BeNull("the iterator drained inside maxBytes");
        }
        finally
        {
            ReplicationToolkitSurfaceTests.TryDelete(dir);
        }
    }

    [Fact]
    public void Pull_MaxBytes_SplitsIntoPages_AlwaysMakesProgress()
    {
        var dir = ReplicationToolkitSurfaceTests.NewTempDir();
        try
        {
            using var store = RocksDbStore.Open(dir);
            for (var i = 0; i < 8; i++)
                Put(store, $"k{i}", new string('x', 64));

            var first = store.Replication.Pull(0, maxBytes: 1);
            first.Batches.Should().HaveCount(1, "a single oversized batch is still returned so the cursor advances");
            first.NextSeq.Should().NotBeNull("more WAL remains");

            var rest = store.Replication.Pull(first.NextSeq!.Value, maxBytes: 1_000_000);
            rest.Batches.Should().NotBeEmpty();
        }
        finally
        {
            ReplicationToolkitSurfaceTests.TryDelete(dir);
        }
    }

    [Fact]
    public void Hello_ThenAck_MinWatermarkIsTheFloor()
    {
        var dir = ReplicationToolkitSurfaceTests.NewTempDir();
        try
        {
            using var store = RocksDbStore.Open(dir);
            var feeder = store.Replication;

            feeder.Hello("r-slow", lastAppliedSeq: 1, replicaEpoch: 1);
            feeder.Hello("r-fast", lastAppliedSeq: 9, replicaEpoch: 1);
            feeder.MinAckedWatermark().Should().Be(1);

            feeder.Ack("r-slow", 4);
            feeder.MinAckedWatermark().Should().Be(4);

            feeder.Ack("unknown", 99).Should().BeFalse();
        }
        finally
        {
            ReplicationToolkitSurfaceTests.TryDelete(dir);
        }
    }

    [Fact]
    public void EnforceCap_DropsADeadReplicaWithANamedEvent()
    {
        var dir = ReplicationToolkitSurfaceTests.NewTempDir();
        try
        {
            using var store = RocksDbStore.Open(dir, replication: new HighwayReplicationOptions
            {
                SlotLagCapSequences = 2,
            });
            var feeder = store.Replication;
            feeder.Hello("dead", lastAppliedSeq: 0, replicaEpoch: 1);

            for (var i = 0; i < 8; i++)
                Put(store, $"k{i}", "v");

            feeder.EnforceCap();
            feeder.Slots["dead"].State.Should().Be(SlotState.Dropped);
            feeder.DropEvents.Should().Contain(e => e.Contains("slot-dropped") && e.Contains("dead"));
        }
        finally
        {
            ReplicationToolkitSurfaceTests.TryDelete(dir);
        }
    }

    [Fact]
    public void Promote_IncrementsEpoch_PriorityZeroRefuses()
    {
        var dir = ReplicationToolkitSurfaceTests.NewTempDir();
        try
        {
            using var store = RocksDbStore.Open(dir, replication: new HighwayReplicationOptions
            {
                StartAsReplica = true,
                Priority = 0,
            });
            store.Replication.TryPromote("admin", out var error).Should().BeFalse();
            error.Should().Contain("priority 0");
            store.Replication.Role.Should().Be(ReplicaRole.Replica);
        }
        finally
        {
            ReplicationToolkitSurfaceTests.TryDelete(dir);
        }
    }

    [Fact]
    public void Deadman_FakeClock_FencesThenUnfences_AndDemotesOnHigherEpoch()
    {
        var clock = new ManualClock();
        var dir = ReplicationToolkitSurfaceTests.NewTempDir();
        try
        {
            using var store = RocksDbStore.Open(dir, replication: new HighwayReplicationOptions
            {
                AutoFailover = true,
                FenceTimeout = TimeSpan.FromSeconds(5),
                PromoteTimeout = TimeSpan.FromSeconds(8),
                Margin = TimeSpan.FromSeconds(1),
                Clock = clock,
                Priority = 10,
            });
            var feeder = store.Replication;
            feeder.Hello("r1", 0, 1);
            feeder.Role.Should().Be(ReplicaRole.Primary);

            clock.Advance(TimeSpan.FromSeconds(6));
            feeder.TickDeadman();
            feeder.Role.Should().Be(ReplicaRole.Fenced);
            feeder.Transitions.Should().Contain(t => t.StartsWith("fence"));

            feeder.NotePeerContact();
            feeder.TickDeadman();
            feeder.Role.Should().Be(ReplicaRole.Primary);

            feeder.Demote(9, "test");
            File.Exists(feeder.LastReconciliationPath).Should().BeTrue();
            File.ReadAllText(feeder.LastReconciliationPath!).Should().Contain("lastReplicatedSeq=");
            File.ReadAllText(feeder.LastReconciliationPath!).Should().Contain("the broker never merges");
        }
        finally
        {
            ReplicationToolkitSurfaceTests.TryDelete(dir);
        }
    }

    /// <summary>
    /// 042-1a: the timer never promotes. A standby with a silent master becomes WILLING —
    /// promotion happens only when the herd actually arrives (042-1c wires that trigger).
    /// This replaces 042's Deadman_ReplicaPromotesAfterPromoteTimeout.
    /// </summary>
    [Fact]
    public void Deadman_SilentReplica_NeverSelfPromotes_ButBecomesWilling()
    {
        var clock = new ManualClock();
        var dir = ReplicationToolkitSurfaceTests.NewTempDir();
        try
        {
            using var store = RocksDbStore.Open(dir, replication: new HighwayReplicationOptions
            {
                StartAsReplica = true,
                AutoFailover = true,
                FenceTimeout = TimeSpan.FromSeconds(5),
                PromoteTimeout = TimeSpan.FromSeconds(8),
                Margin = TimeSpan.FromSeconds(1),
                WillingnessThreshold = TimeSpan.FromSeconds(3),
                Clock = clock,
                Priority = 10,
            });
            var feeder = store.Replication;

            feeder.IsWillingForHerd().Should().BeFalse("the master-link is fresh at start");

            clock.Advance(TimeSpan.FromSeconds(30));
            feeder.TickDeadman();
            feeder.Role.Should().Be(ReplicaRole.Replica,
                "no timer promotes anybody — mastership arrives with the herd, not a clock");
            feeder.Epoch.Should().Be(1);
            feeder.IsWillingForHerd().Should().BeTrue("its own master-link is dead past W");
        }
        finally
        {
            ReplicationToolkitSurfaceTests.TryDelete(dir);
        }
    }

    // ---- 042 gap-fix coverage (G1–G5) ---------------------------------------

    [Fact]
    public void Epoch_PersistsAcrossReopen_SoResurrectionFencingSurvivesARestart()
    {
        var dir = ReplicationToolkitSurfaceTests.NewTempDir();
        try
        {
            using (var store = RocksDbStore.Open(dir, replication: new HighwayReplicationOptions { StartAsReplica = true }))
            {
                store.Replication.Promote("test");
                store.Replication.Epoch.Should().Be(2);
            }

            // G2: the epoch is node-local durable state. A restarted promoted node must
            // come back at its promoted epoch, or the old primary is never demoted.
            using (var reopened = RocksDbStore.Open(dir))
            {
                reopened.Replication.Epoch.Should().Be(2,
                    "a promoted node that restarts at epoch 1 cannot fence its resurrected predecessor");
            }
        }
        finally
        {
            ReplicationToolkitSurfaceTests.TryDelete(dir);
        }
    }

    [Fact]
    public void HigherEpoch_OnAReplica_AdoptsWithoutDemoting()
    {
        var dir = ReplicationToolkitSurfaceTests.NewTempDir();
        try
        {
            using var store = RocksDbStore.Open(dir, replication: new HighwayReplicationOptions { StartAsReplica = true });
            var feeder = store.Replication;

            // G3 fix: a replica seeing its primary's higher epoch is normal operation —
            // it must stay a Replica (and keep deadman eligibility), never turn Demoted.
            feeder.ObserveHigherEpoch(5, "primary was promoted before we joined");
            feeder.Role.Should().Be(ReplicaRole.Replica);
            feeder.Epoch.Should().Be(5);
            feeder.LastReconciliationPath.Should().BeNull("nothing diverged; there is no tail to reconcile");
        }
        finally
        {
            ReplicationToolkitSurfaceTests.TryDelete(dir);
        }
    }

    [Fact]
    public void Pull_BehindTheRetainedWal_RefusesWithAGap_NeverAGappedStream()
    {
        var dir = ReplicationToolkitSurfaceTests.NewTempDir();
        try
        {
            var store = RocksDbStore.Open(dir);
            for (var i = 0; i < 4; i++)
                Put(store, $"k{i}", "v");
            store.Replication.Engine.GetLatestSequenceNumber().Should().BeGreaterThan(2);
            store.Dispose();

            // Reopen: recovery flushes the WAL's content into SSTs and retires the old
            // WAL into archive/ (the TTL keeps it there). Deleting the archive is then
            // exactly what WAL retention expiry does to a replica that stayed away too
            // long — the data lives on, the shippable log does not.
            using var reopened = RocksDbStore.Open(dir);
            reopened.Replication.Engine.GetLatestSequenceNumber().Should().BeGreaterThan(2,
                "recovery persisted the data and its sequence");
            var archive = Path.Combine(dir, "archive");
            if (Directory.Exists(archive))
            {
                foreach (var log in Directory.GetFiles(archive))
                    File.Delete(log);
            }

            // A pull from sequence 0 (a replica that missed everything) must be refused —
            // both the gapped-first-batch shape and the empty-iterator-while-behind shape.
            var act = () => reopened.Replication.Pull(0, 1_000_000);
            act.Should().Throw<ReplicationGapException>(
                "serving a stream that silently skips the recovered prefix is divergence, not replication");
        }
        finally
        {
            ReplicationToolkitSurfaceTests.TryDelete(dir);
        }
    }

    // ---- 042-1a contract coverage (A-T4) ------------------------------------

    /// <summary>
    /// The willingness truth table — the herd contract's no-split rule as a pure function.
    /// Threshold W = 3s, T_fence = 5s, stagger = 250ms/priority-unit, so the effective
    /// "lost master" window is <c>3s + priority×0.25s</c>.
    /// </summary>
    [Theory]
    [InlineData(0, 60, false, true, false)]    // priority 0: never, however dead the link
    [InlineData(2, 1, false, true, false)]     // healthy link: unwilling, whatever a client believes
    [InlineData(2, 4, false, true, true)]      // prio 2: effective 3.5s; 4s silence → willing
    [InlineData(2, 3, false, true, false)]     // prio 2: effective 3.5s; 3s silence → NOT yet
    [InlineData(2, 60, false, true, true)]     // link long dead: willing
    [InlineData(2, 0, true, true, true)]       // GOODBYE seen: immediately willing
    [InlineData(0, 0, true, true, false)]      // GOODBYE cannot override priority 0
    [InlineData(2, 4, false, false, false)]    // never saw the master: 'not found yet' ≠ 'lost' — outer bound applies
    [InlineData(2, 6, false, false, true)]     // never saw the master: willing on T_fence(5s)+0.5s = 5.5s
    [InlineData(2, 0, true, false, true)]      // GOODBYE trumps never-seen (the master itself said go)
    public void Willingness_TruthTable(int priority, int silenceSeconds, bool goodbye, bool seenMaster, bool expected)
        => Willingness.Decide(priority, TimeSpan.FromSeconds(silenceSeconds), goodbye,
                TimeSpan.FromSeconds(3), seenMaster, TimeSpan.FromSeconds(5))
            .Should().Be(expected);

    /// <summary>
    /// 042-1d: the priority stagger — the higher-priority (lower-number) successor turns
    /// willing FIRST, so a losing herd converges on ONE node, never splits.
    /// </summary>
    [Fact]
    public void Willingness_Stagger_HigherPrioritySuccessorTurnsWillingFirst()
    {
        TimeSpan W = TimeSpan.FromSeconds(3), Fence = TimeSpan.FromSeconds(30);
        // Stagger 400ms/unit: at 4.0s of silence, priority-2 (effective 3.8s) is willing;
        // priority-3 (4.2s) is not — the higher-priority successor leads.
        var at = TimeSpan.FromMilliseconds(4000);
        Willingness.Decide(2, at, false, W, true, Fence).Should().BeTrue("prio 2 effective 3.8s");
        Willingness.Decide(3, at, false, W, true, Fence).Should().BeFalse("prio 3 effective 4.2s — still waiting");
    }

    [Fact]
    public void Validation_RefusesWillingnessThresholdOutsideTheContractOrdering()
    {
        var bad = new HighwayReplicationOptions
        {
            FenceTimeout = TimeSpan.FromSeconds(5),
            WillingnessThreshold = TimeSpan.FromSeconds(5),   // W must be < T_fence
        };
        var act = bad.Validate;
        act.Should().Throw<InvalidOperationException>().WithMessage("*WillingnessThreshold*");
    }

    [Fact]
    public void RosterRecord_RoundTrips_AndEmptyDecodesFromNothing()
    {
        var roster = new RosterRecord(7,
        [
            new RosterMember("node-a", 1, "hosta:6500"),
            new RosterMember("node-b", 2, "hostb:6500"),
        ]);

        var decoded = RosterRecord.Decode(roster.Encode());
        decoded.Version.Should().Be(7);
        decoded.Members.Should().Equal(roster.Members);

        RosterRecord.Empty.Version.Should().Be(0);
        RosterRecord.Empty.Members.Should().BeEmpty();
    }

    [Fact]
    public void Feeder_IsWillingForHerd_ReadsRealState()
    {
        var clock = new ManualClock();
        var dir = ReplicationToolkitSurfaceTests.NewTempDir();
        try
        {
            using var store = RocksDbStore.Open(dir, replication: new HighwayReplicationOptions
            {
                StartAsReplica = true,
                WillingnessThreshold = TimeSpan.FromSeconds(3),
                Clock = clock,
                Priority = 1,   // effective lost-master window = 3s + 1×0.25s = 3.25s
            });
            var feeder = store.Replication;

            feeder.IsWillingForHerd().Should().BeFalse("master never seen AND link fresh");
            clock.Advance(TimeSpan.FromSeconds(4));
            feeder.IsWillingForHerd().Should().BeFalse(
                "a standby that never reached its master waits the outer T_fence bound (5.25s), not W");

            feeder.NotePeerContact();   // the pump succeeds: now the clock measures LOSS
            feeder.IsWillingForHerd().Should().BeFalse("a successful pull resets the clock");
            clock.Advance(TimeSpan.FromSeconds(4));
            feeder.IsWillingForHerd().Should().BeTrue("master-link silent past the effective 3.25s window");
            feeder.NotePeerContact();
            feeder.IsWillingForHerd().Should().BeFalse("contact resets it again");
            feeder.NoteGoodbye();
            feeder.IsWillingForHerd().Should().BeTrue("GOODBYE makes a standby immediately willing");

            // A promoted node is not 'willing' — it answers as master.
            feeder.Promote("test");
            feeder.IsWillingForHerd().Should().BeFalse();
        }
        finally
        {
            ReplicationToolkitSurfaceTests.TryDelete(dir);
        }
    }

    [Fact]
    public void RedirectEndpoint_NamesThePrimary_NeverTheRefusingNodeItself()
    {
        var dir = ReplicationToolkitSurfaceTests.NewTempDir();
        try
        {
            // Seed the directory first: a blank dir + PrimaryServer would try a real
            // snapshot bootstrap, and this test's primary is imaginary.
            RocksDbStore.Open(dir).Dispose();

            using var store = RocksDbStore.Open(dir, replication: new HighwayReplicationOptions
            {
                StartAsReplica = true,
                PrimaryServer = "primary-host:6500,password=secret",
                AdvertiseEndpoint = "replica-host:6500",
            });
            var feeder = store.Replication;

            // G5: a replica's -NOTPRIMARY must point at its primary, not at itself.
            feeder.RedirectEndpoint().Should().Be("primary-host:6500");

            // A higher-epoch HELLO carrying an endpoint updates the redirect — the
            // promoted node announcing itself (G3).
            feeder.Hello("new-primary", 0, 7, "promoted-host:6500");
            feeder.RedirectEndpoint().Should().Be("promoted-host:6500");

            // Promotion redirects to self.
            feeder.Promote("test");
            feeder.RedirectEndpoint().Should().Be("replica-host:6500");
        }
        finally
        {
            ReplicationToolkitSurfaceTests.TryDelete(dir);
        }
    }

    [Fact]
    public void ReconciliationReport_NamesTheEntitiesInTheUnreplicatedTail()
    {
        var dir = ReplicationToolkitSurfaceTests.NewTempDir();
        try
        {
            using var store = RocksDbStore.Open(dir);
            var feeder = store.Replication;

            // A replica acked nothing; every local write is unreplicated tail.
            feeder.Hello("r1", 0, 1);
            using (var batch = store.NewBatch())
            {
                store.ListRightPush(batch, Highway.Server.Storage.Layout.HighwayKeyspace.ListEntry("orders.create", 1), "payload"u8.ToArray());
                batch.Commit();
            }

            feeder.ObserveHigherEpoch(9, "seeded divergence");

            feeder.Role.Should().Be(ReplicaRole.Demoted);
            var report = File.ReadAllText(feeder.LastReconciliationPath!);
            report.Should().Contain("unreplicated tail", "the report must show WHAT diverged (G9/R4.2)");
            report.Should().Contain("orders.create", "the operator replays by queue; the report must name it");
            report.Should().Contain("the broker never merges");
        }
        finally
        {
            ReplicationToolkitSurfaceTests.TryDelete(dir);
        }
    }

    // ---- 042-1c: GOODBYE drain + herd count ----------------------------------

    [Fact]
    public void Goodbye_DrainsThenStandsDown_AtTheDeadline()
    {
        var clock = new ManualClock();
        var dir = ReplicationToolkitSurfaceTests.NewTempDir();
        try
        {
            using var store = RocksDbStore.Open(dir, replication: new HighwayReplicationOptions
            {
                GoodbyeDrainTimeout = TimeSpan.FromSeconds(5),
                Clock = clock,
            });
            var feeder = store.Replication;
            Put(store, "k", "v");                 // an unreplicated tail exists
            feeder.ClientSessionOpened();         // one stubborn client stays connected

            var narrated = new List<string>();
            feeder.Narrator = narrated.Add;

            feeder.BeginGoodbye("maintenance");
            feeder.IsDraining.Should().BeTrue();
            narrated.Should().ContainSingle(m => m.StartsWith("GOODBYE 1"),
                "GOODBYE is narrated with the epoch, naming no successor");

            clock.Advance(TimeSpan.FromSeconds(2));
            feeder.TickGoodbye();
            feeder.Role.Should().Be(ReplicaRole.Primary, "still draining; the client has not left");

            clock.Advance(TimeSpan.FromSeconds(4));
            feeder.TickGoodbye();
            feeder.Role.Should().Be(ReplicaRole.Demoted, "the drain is bounded — one stuck client cannot block a departure");
            feeder.IsDraining.Should().BeFalse();
            feeder.Transitions.Should().Contain(t => t.StartsWith("goodbye-complete") && t.Contains("drain-deadline"));
            File.Exists(feeder.LastReconciliationPath).Should().BeTrue("an unreplicated tail gets its report");
        }
        finally
        {
            ReplicationToolkitSurfaceTests.TryDelete(dir);
        }
    }

    [Fact]
    public void Goodbye_CompletesEarly_WhenTheHerdLeaves()
    {
        var clock = new ManualClock();
        var dir = ReplicationToolkitSurfaceTests.NewTempDir();
        try
        {
            using var store = RocksDbStore.Open(dir, replication: new HighwayReplicationOptions
            {
                GoodbyeDrainTimeout = TimeSpan.FromSeconds(30),
                Clock = clock,
            });
            var feeder = store.Replication;
            feeder.ClientSessionOpened();
            feeder.BeginGoodbye("restart");

            clock.Advance(TimeSpan.FromSeconds(1));
            feeder.TickGoodbye();
            feeder.IsDraining.Should().BeTrue();

            feeder.ClientSessionClosed();
            feeder.TickGoodbye();
            feeder.Role.Should().Be(ReplicaRole.Demoted,
                "an empty herd is a finished drain — no need to wait out the deadline");
            feeder.Transitions.Should().Contain(t => t.StartsWith("goodbye-complete") && t.Contains("herd-left"));
        }
        finally
        {
            ReplicationToolkitSurfaceTests.TryDelete(dir);
        }
    }

    [Fact]
    public void Goodbye_OnANonPrimary_IsANoOp()
    {
        var dir = ReplicationToolkitSurfaceTests.NewTempDir();
        try
        {
            using var store = RocksDbStore.Open(dir, replication: new HighwayReplicationOptions { StartAsReplica = true });
            store.Replication.BeginGoodbye("noop");
            store.Replication.IsDraining.Should().BeFalse();
            store.Replication.Role.Should().Be(ReplicaRole.Replica);
        }
        finally
        {
            ReplicationToolkitSurfaceTests.TryDelete(dir);
        }
    }

    private static void Put(RocksDbStore store, string key, string value)
    {
        using var batch = store.NewBatch();
        store.Set(batch, Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(value));
        batch.Commit();
    }
}

internal sealed class ManualClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}
