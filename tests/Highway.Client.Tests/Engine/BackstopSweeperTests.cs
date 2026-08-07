using FluentAssertions;
using Highway.Client.Engine;
using Highway.Client.Tests.TestFixtures;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Highway.Client.Tests.Engine;

/// <summary>
/// Feature 005 Task 9 — the sweep that demotes doorbells to a pure latency
/// optimization. It must be cheap when idle and must never die.
/// </summary>
public class BackstopSweeperTests
{
    private readonly IHighwayConnection _connection = Substitute.For<IHighwayConnection>();

    private static async Task RunForAsync(BackstopSweeper sweeper, int ms)
    {
        using var cts = new CancellationTokenSource();
        var run = sweeper.RunAsync(cts.Token);
        await Task.Delay(ms);
        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task Sweep_WhenIdle_PerformsNoNetworkIo()
    {
        var registry = new PendingCallRegistry(_connection);
        var sweeper = new BackstopSweeper(
            registry, TimeSpan.FromMilliseconds(20), [], NullLogger<BackstopSweeper>.Instance);

        await RunForAsync(sweeper, 150);

        await _connection.DidNotReceive().GetReplySlotAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Sweep_SignalsEveryRegisteredLoopWake()
    {
        var registry = new PendingCallRegistry(_connection);
        var wakeA = new LoopWake();
        var wakeB = new LoopWake();
        var sweeper = new BackstopSweeper(
            registry, TimeSpan.FromMilliseconds(20), [wakeA, wakeB], NullLogger<BackstopSweeper>.Instance);

        await RunForAsync(sweeper, 150);

        // A signalled wake completes its wait immediately.
        await wakeA.Invoking(w => w.WaitAsync(TimeSpan.FromMilliseconds(50), CancellationToken.None))
            .Should().CompleteWithinAsync(TimeSpan.FromMilliseconds(200));
        await wakeB.Invoking(w => w.WaitAsync(TimeSpan.FromMilliseconds(50), CancellationToken.None))
            .Should().CompleteWithinAsync(TimeSpan.FromMilliseconds(200));
    }

    [Fact]
    public async Task Sweep_AgedPendingCall_ReadsTheReplySlot()
    {
        _connection.GetReplySlotAsync("aged", Arg.Any<CancellationToken>()).Returns((byte[]?)null);
        var registry = new PendingCallRegistry(_connection);
        _ = registry.Register("aged", typeof(TestResponse), TimeSpan.FromSeconds(30), CancellationToken.None);

        var sweeper = new BackstopSweeper(
            registry, TimeSpan.FromMilliseconds(20), [], NullLogger<BackstopSweeper>.Instance);
        await RunForAsync(sweeper, 200);

        await _connection.Received().GetReplySlotAsync("aged", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Sweep_WhenAnIterationThrows_KeepsSweeping()
    {
        var calls = 0;
        _connection.GetReplySlotAsync("boom", Arg.Any<CancellationToken>())
            .Returns<byte[]?>(_ =>
            {
                calls++;
                throw new InvalidOperationException("unexpected internal failure");
            });

        var registry = new PendingCallRegistry(_connection);
        _ = registry.Register("boom", typeof(TestResponse), TimeSpan.FromSeconds(30), CancellationToken.None);

        var sweeper = new BackstopSweeper(
            registry, TimeSpan.FromMilliseconds(20), [], NullLogger<BackstopSweeper>.Instance);

        await FluentActions.Awaiting(() => RunForAsync(sweeper, 250)).Should().NotThrowAsync();
        calls.Should().BeGreaterThan(1, "the sweeper is the engine's heartbeat and must never die");
    }

    [Fact]
    public async Task Sweep_StopsPromptlyOnCancellation()
    {
        var registry = new PendingCallRegistry(_connection);
        var sweeper = new BackstopSweeper(
            registry, TimeSpan.FromSeconds(30), [], NullLogger<BackstopSweeper>.Instance);

        using var cts = new CancellationTokenSource();
        var run = sweeper.RunAsync(cts.Token);
        await cts.CancelAsync();

        await run.Invoking(r => r).Should().CompleteWithinAsync(TimeSpan.FromSeconds(2));
    }
}
