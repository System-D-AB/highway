using FluentAssertions;
using Highway.Client.Engine;
using Xunit;

namespace Highway.Client.Tests.Engine;

/// <summary>074 T8: pending calls are re-sent only after a real master change, and once per change.</summary>
public class ReplayGateTests
{
    [Fact]
    public void AConvergenceOnTheSameMaster_ReSendsNothing()
    {
        var gate = new ReplayGate();

        gate.ShouldReplay(0).Should().BeFalse("no master change has happened: a blip that recovered in place");
        gate.ShouldReplay(0).Should().BeFalse();
    }

    [Fact]
    public void EachRealChange_ReSendsExactlyOnce()
    {
        var gate = new ReplayGate();

        gate.ShouldReplay(1).Should().BeTrue();
        gate.ShouldReplay(1).Should().BeFalse("a second convergence for the same change re-sends nothing");
        gate.ShouldReplay(2).Should().BeTrue("a further failover is a new change");
    }

    [Fact]
    public void ConcurrentConvergences_ForOneChange_ReSendOnce()
    {
        var gate = new ReplayGate();
        var wins = 0;

        Parallel.For(0, 64, _ => { if (gate.ShouldReplay(1)) Interlocked.Increment(ref wins); });

        wins.Should().Be(1, "every in-flight command fails at once on a failover; only one re-send pass runs");
    }
}
