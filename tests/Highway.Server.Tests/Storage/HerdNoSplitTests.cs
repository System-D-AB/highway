using FluentAssertions;
using Highway.Server.Storage.Rocks;
using Xunit;

namespace Highway.Server.Tests.Storage;

/// <summary>
/// Feature 069 — no split brain when failover runs under load. R1: a standby that has lost its master
/// answers willing only when no higher-priority roster peer could take the herd. R2: two primaries at one
/// epoch settle by priority. The scenario that split before 069 is replayed deterministically: the master
/// dies, the higher-priority successor is alive and willing, and a client whose probe of it was slow
/// arrives at the lower-priority standby.
/// </summary>
public class HerdNoSplitTests
{
    private const string Master = "node1.test:6500";
    private const string Successor = "node2.test:6500";
    private const string Lower = "node3.test:6500";

    private static readonly RosterMember[] Roster =
    [
        new("n1", 1, Master),
        new("n2", 2, Successor),
        new("n3", 3, Lower),
    ];

    private static HighwayReplicationOptions Options(ManualClock clock, bool replica, string id, int priority, string endpoint) => new()
    {
        StartAsReplica = replica,
        ReplicaId = id,
        Priority = priority,
        AdvertiseEndpoint = endpoint,
        Clock = clock,
        AutoRejoin = true,
    };

    /// <summary>A probe that answers from a table the test edits as the scenario moves.</summary>
    private static Func<string, CancellationToken, Task<PeerObservation>> Probe(
        ManualClock clock, Dictionary<string, (PeerAnswer Answer, string? Redirect)> answers)
        => (endpoint, _) =>
        {
            var (answer, redirect) = answers.TryGetValue(endpoint, out var a) ? a : (PeerAnswer.Unreachable, null);
            return Task.FromResult(new PeerObservation(clock.GetUtcNow(), answer, redirect));
        };

    [Fact]
    public async Task ALowerStandby_DefersToALiveWillingSuccessor_InsteadOfPromoting()
    {
        var clock = new ManualClock();
        var dir = ReplicationToolkitSurfaceTests.NewTempDir();
        try
        {
            using var store = RocksDbStore.Open(dir, replication: Options(clock, replica: true, "n3", 3, Lower));
            var feeder = store.Replication;
            var answers = new Dictionary<string, (PeerAnswer, string?)>
            {
                [Master] = (PeerAnswer.Unreachable, null),       // the master died
                [Successor] = (PeerAnswer.Willing, Successor),   // n2 is alive and has turned willing
            };
            feeder.RosterMembers = () => Roster;
            feeder.PeerProbe = Probe(clock, answers);
            feeder.NotePeerContact();   // n3 had been following the master

            clock.Advance(TimeSpan.FromMinutes(1));   // far past n3's own staggered threshold
            feeder.IsWillingForHerd().Should().BeFalse("with no probe answers yet, the successor's state is unknown — wait, don't guess");

            await feeder.ProbeHigherPeersAsync();
            feeder.IsWillingForHerd().Should().BeFalse("the higher-priority successor is alive and willing: the herd belongs there");
            feeder.RedirectEndpoint().Should().Be(Successor, "a client that reached n3 is sent to n2");

            answers[Successor] = (PeerAnswer.Unreachable, null);   // now n2 dies too
            await feeder.ProbeHigherPeersAsync();
            feeder.IsWillingForHerd().Should().BeTrue("every higher-priority peer is gone, so n3 is the successor");
        }
        finally { ReplicationToolkitSurfaceTests.TryDelete(dir); }
    }

    [Fact]
    public async Task AStandby_NeverTurnsWilling_WhileItsMasterStillAnswers()
    {
        // The standby's own link stalled (load), but the master is alive: that is not a lost master.
        var clock = new ManualClock();
        var dir = ReplicationToolkitSurfaceTests.NewTempDir();
        try
        {
            using var store = RocksDbStore.Open(dir, replication: Options(clock, replica: true, "n2", 2, Successor));
            var feeder = store.Replication;
            feeder.RosterMembers = () => Roster;
            feeder.PeerProbe = Probe(clock, new() { [Master] = (PeerAnswer.Master, Master) });
            feeder.NotePeerContact();

            clock.Advance(TimeSpan.FromMinutes(1));
            await feeder.ProbeHigherPeersAsync();
            feeder.IsWillingForHerd().Should().BeFalse();
            feeder.RedirectEndpoint().Should().Be(Master);
        }
        finally { ReplicationToolkitSurfaceTests.TryDelete(dir); }
    }

    [Fact]
    public async Task ADrainingMaster_DoesNotHoldBackItsSuccessor()
    {
        // GOODBYE: the leaving master answers standby and redirects to itself, the signal to walk on.
        var clock = new ManualClock();
        var dir = ReplicationToolkitSurfaceTests.NewTempDir();
        try
        {
            using var store = RocksDbStore.Open(dir, replication: Options(clock, replica: true, "n2", 2, Successor));
            var feeder = store.Replication;
            feeder.RosterMembers = () => Roster;
            feeder.PeerProbe = Probe(clock, new() { [Master] = (PeerAnswer.Standby, Master) });
            feeder.NotePeerContact();
            feeder.NoteGoodbye();

            await feeder.ProbeHigherPeersAsync();
            feeder.IsWillingForHerd().Should().BeTrue("GOODBYE hands the herd over at once");
        }
        finally { ReplicationToolkitSurfaceTests.TryDelete(dir); }
    }

    [Fact]
    public async Task AStaleAnswer_IsNoEvidence()
    {
        var clock = new ManualClock();
        var dir = ReplicationToolkitSurfaceTests.NewTempDir();
        try
        {
            using var store = RocksDbStore.Open(dir, replication: Options(clock, replica: true, "n2", 2, Successor));
            var feeder = store.Replication;
            feeder.RosterMembers = () => Roster;
            feeder.PeerProbe = Probe(clock, new() { [Master] = (PeerAnswer.Unreachable, null) });
            feeder.NotePeerContact();

            clock.Advance(TimeSpan.FromMinutes(1));
            await feeder.ProbeHigherPeersAsync();
            feeder.IsWillingForHerd().Should().BeTrue();

            clock.Advance(HigherPeerWatch.Freshness + TimeSpan.FromSeconds(1));   // probes stalled
            feeder.IsWillingForHerd().Should().BeFalse("without a recent answer the master may be back");
        }
        finally { ReplicationToolkitSurfaceTests.TryDelete(dir); }
    }

    [Fact]
    public void Verdict_TruthTable()
    {
        var now = DateTimeOffset.UnixEpoch.AddDays(1);
        var higher = new[] { new RosterMember("n1", 1, Master) };
        bool Outranked(PeerAnswer answer, string? redirect, out string? to)
            => HigherPeerWatch.Outranked(now, higher,
                new Dictionary<string, PeerObservation> { [Master] = new(now, answer, redirect) }, Lower, out to);

        Outranked(PeerAnswer.Master, Master, out var to).Should().BeTrue(); to.Should().Be(Master);
        Outranked(PeerAnswer.Willing, Master, out to).Should().BeTrue(); to.Should().Be(Master);
        Outranked(PeerAnswer.Standby, Successor, out to).Should().BeTrue("a standby still following someone outranks");
        Outranked(PeerAnswer.Standby, Master, out _).Should().BeFalse("a self-redirect is a draining master");
        Outranked(PeerAnswer.Standby, Lower, out _).Should().BeFalse("a peer that defers to this node");
        Outranked(PeerAnswer.Unreachable, null, out _).Should().BeFalse("a refused connection: nothing listens there");

        // Slow is not dead (the 069 bug, one level down): a timed-out probe still outranks until the peer
        // has been silent for SilentPeerDeadAfter.
        bool SlowFor(TimeSpan silent, out string? to)
            => HigherPeerWatch.Outranked(now, higher,
                new Dictionary<string, PeerObservation> { [Master] = new(now, PeerAnswer.Slow, null, now - silent) }, Lower, out to);
        SlowFor(TimeSpan.FromSeconds(1), out to).Should().BeTrue("slow may be alive"); to.Should().BeNull();
        SlowFor(HigherPeerWatch.SilentPeerDeadAfter, out _).Should().BeFalse("silent long enough to be gone");

        var first = new PeerObservation(now, PeerAnswer.Slow, null).After(null);
        var second = new PeerObservation(now.AddSeconds(2), PeerAnswer.Slow, null).After(first);
        second.SilentSince.Should().Be(now, "silence is measured from the first unanswered probe");
        new PeerObservation(now.AddSeconds(3), PeerAnswer.Willing, Master).After(second).SilentSince.Should().BeNull();

        HigherPeerWatch.Outranked(now, higher, new Dictionary<string, PeerObservation>(), Lower, out to)
            .Should().BeTrue("unknown is not dead");
        to.Should().BeNull();
        HigherPeerWatch.Outranked(now, [], new Dictionary<string, PeerObservation>(), Lower, out _)
            .Should().BeFalse("the highest-priority node defers to no one");
    }

    [Fact]
    public void TwoPrimariesAtOneEpoch_TheLowerPriorityYields_AndSchedulesItsRejoin()
    {
        var clock = new ManualClock();
        var lowDir = ReplicationToolkitSurfaceTests.NewTempDir();
        var highDir = ReplicationToolkitSurfaceTests.NewTempDir();
        try
        {
            using var low = RocksDbStore.Open(lowDir, replication: Options(clock, replica: false, "n3", 3, Lower));
            using var high = RocksDbStore.Open(highDir, replication: Options(clock, replica: false, "n2", 2, Successor));
            low.Replication.RosterMembers = () => Roster;
            high.Replication.RosterMembers = () => Roster;
            low.Replication.Epoch.Should().Be(high.Replication.Epoch);
            var epoch = low.Replication.Epoch;

            // Each announces itself to the other (HELLO with the caller's endpoint, at the shared epoch).
            high.Replication.Hello("n3", 0, epoch, Lower);
            low.Replication.Hello("n2", 0, epoch, Successor);

            high.Replication.Role.Should().Be(ReplicaRole.Primary, "the higher-priority primary keeps the herd");
            low.Replication.Role.Should().Be(ReplicaRole.Demoted, "the lower-priority one yields");
            low.Replication.RedirectEndpoint().Should().Be(Successor);
            low.Replication.Transitions.Should().Contain(t => t.Contains("equal-epoch-tie-break"));
            ReplicaPuller.ReadRejoinMarker(low.DataDir)!.Value.Endpoint.Should().Be(Successor,
                "it rejoins the survivor as its replica, like any demoted ex-primary");
        }
        finally
        {
            ReplicationToolkitSurfaceTests.TryDelete(lowDir);
            ReplicationToolkitSurfaceTests.TryDelete(highDir);
        }
    }
}
