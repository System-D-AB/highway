using FluentAssertions;
using Highway.Client.Engine;
using NSubstitute;
using Xunit;

namespace Highway.Client.Tests.Engine;

/// <summary>
/// Feature 005 Task 6 — doorbell subscription set and wake routing. Doorbells
/// are a latency optimization only; disabling them must subscribe nothing.
/// </summary>
public class DoorbellWatcherTests
{
    private readonly IHighwayConnection _connection = Substitute.For<IHighwayConnection>();
    private readonly Dictionary<string, Action<string>> _handlers = [];

    public DoorbellWatcherTests()
    {
        _connection
            .SubscribeDoorbellAsync(Arg.Any<string>(), Arg.Any<Action<string>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                _handlers[ci.ArgAt<string>(0)] = ci.ArgAt<Action<string>>(1);
                return Task.CompletedTask;
            });
    }

    private DoorbellWatcher CreateWatcher(bool enabled = true)
        => new(_connection, new PendingCallRegistry(_connection), enabled);

    [Fact]
    public async Task Start_SubscribesTheReplyDoorbellOncePerNode()
    {
        var watcher = CreateWatcher();
        watcher.RegisterServiceWake("svc.a", new LoopWake());
        watcher.RegisterServiceWake("svc.b", new LoopWake());

        await watcher.StartAsync();

        _handlers.Should().ContainKey("hw:door:rep");
        await _connection.Received(1).SubscribeDoorbellAsync(
            "hw:door:rep", Arg.Any<Action<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Start_SubscribesEveryServiceAndGroupDoorbell()
    {
        var watcher = CreateWatcher();
        watcher.RegisterServiceWake("svc.a", new LoopWake());
        watcher.RegisterGroupWake("ch.x", "node-1", new LoopWake());

        await watcher.StartAsync();

        _handlers.Keys.Should().BeEquivalentTo(
            ["hw:door:rep", "hw:door:svc:svc.a", "hw:door:ch:ch.x:grp:node-1"]);
    }

    [Fact]
    public async Task ServiceDoorbell_SignalsOnlyThatServicesLoop()
    {
        var watcher = CreateWatcher();
        var wakeA = new LoopWake();
        var wakeB = new LoopWake();
        watcher.RegisterServiceWake("svc.a", wakeA);
        watcher.RegisterServiceWake("svc.b", wakeB);
        await watcher.StartAsync();

        _handlers["hw:door:svc:svc.a"]("req-1");

        await wakeA.Invoking(w => w.WaitAsync(TimeSpan.FromMilliseconds(50), CancellationToken.None))
            .Should().CompleteWithinAsync(TimeSpan.FromMilliseconds(300));

        // wakeB was never signalled: its wait runs the full timeout instead of returning at once.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await wakeB.WaitAsync(TimeSpan.FromMilliseconds(120), CancellationToken.None);
        sw.ElapsedMilliseconds.Should().BeGreaterThan(80, "an unrelated service doorbell must not wake this loop");
    }

    [Fact]
    public async Task GroupDoorbell_SignalsThatChannelsConsumerLoop()
    {
        var watcher = CreateWatcher();
        var wake = new LoopWake();
        watcher.RegisterGroupWake("ch.x", "node-1", wake);
        await watcher.StartAsync();

        _handlers["hw:door:ch:ch.x:grp:node-1"]("42");

        await wake.Invoking(w => w.WaitAsync(TimeSpan.FromMilliseconds(50), CancellationToken.None))
            .Should().CompleteWithinAsync(TimeSpan.FromMilliseconds(300));
    }

    [Fact]
    public async Task Start_WhenDoorbellsDisabled_SubscribesNothing()
    {
        var watcher = CreateWatcher(enabled: false);
        watcher.RegisterServiceWake("svc.a", new LoopWake());
        watcher.RegisterGroupWake("ch.x", "node-1", new LoopWake());

        await watcher.StartAsync();

        _handlers.Should().BeEmpty("with doorbells off, correctness rides entirely on the backstop sweep");
        await _connection.DidNotReceive().SubscribeDoorbellAsync(
            Arg.Any<string>(), Arg.Any<Action<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReplyDoorbell_ForForeignRequestId_DoesNotTouchTheSlot()
    {
        var watcher = CreateWatcher();
        await watcher.StartAsync();

        _handlers["hw:door:rep"]("not-my-request");
        await Task.Delay(50);

        // hw:door:rep is node-global — a node must ignore replies it did not request.
        await _connection.DidNotReceive().GetReplySlotAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _connection.DidNotReceive().DeleteReplySlotAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
