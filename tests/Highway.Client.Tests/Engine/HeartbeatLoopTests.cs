using FluentAssertions;
using Highway.Abstractions;
using Highway.Client.Engine;
using Highway.Client.Scanning;
using Highway.Client.Wire;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Highway.Client.Tests.Engine;

/// <summary>
/// Feature 006 Task 13 — the register-once-then-ping loop.
///
/// <para>The behaviours worth pinning: the catalog is serialized once and never
/// rides a normal beat, and a <c>REGISTER</c> reply triggers immediate
/// re-registration rather than waiting a full interval.</para>
/// </summary>
public class HeartbeatLoopTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(40);

    private readonly IHighwayConnection _connection = Substitute.For<IHighwayConnection>();
    private readonly ICatalog _catalog = Substitute.For<ICatalog>();

    public HeartbeatLoopTests()
    {
        _catalog.ToCatalogInfo().Returns(new CatalogInfo
        {
            Services = [new CatalogServiceEntry
            {
                Name = "orders.create",
                RequestTypeName = "R",
                ResponseTypeName = "S",
            }],
            Channels = [],
        });
    }

    private HeartbeatLoop CreateLoop(string node = "node-1")
        => new(_connection, _catalog, node, Interval, NullLogger.Instance);

    private static async Task RunForAsync(HeartbeatLoop loop, int ms)
    {
        using var cts = new CancellationTokenSource();
        var run = loop.RunAsync(cts.Token);
        await Task.Delay(ms);
        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task RegisterAsync_SendsTheCatalog()
    {
        // The engine awaits this during startup so the node is discoverable by
        // the time it reports Running — a node registered only from inside the
        // loop could miss the first request after a deployment.
        await CreateLoop().RegisterAsync();

        await _connection.Received(1).RegisterAsync("node-1", Arg.Any<byte[]>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Run_DoesNotReRegisterWhenTheServerIsHappy()
    {
        _connection.HeartbeatAsync("node-1", Arg.Any<CancellationToken>()).Returns(HeartbeatReply.Ok);

        await RunForAsync(CreateLoop(), 220);

        await _connection.DidNotReceive().RegisterAsync(
            Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SteadyState_SendsLivenessOnly_NeverTheCatalog()
    {
        _connection.HeartbeatAsync("node-1", Arg.Any<CancellationToken>()).Returns(HeartbeatReply.Ok);
        var loop = CreateLoop();

        await RunForAsync(loop, 220);

        await _connection.Received().HeartbeatAsync("node-1", Arg.Any<CancellationToken>());
        await _connection.DidNotReceive().RegisterAsync(
            Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Catalog_IsSerializedOnce_AtConstruction()
    {
        var loop = CreateLoop();

        loop.CatalogBytes.Should().BeGreaterThan(0);
        _catalog.Received(1).ToCatalogInfo();
    }

    [Fact]
    public async Task Catalog_IsNotRebuiltPerBeat()
    {
        _connection.HeartbeatAsync("node-1", Arg.Any<CancellationToken>()).Returns(HeartbeatReply.Ok);
        var loop = CreateLoop();

        await RunForAsync(loop, 220);

        _catalog.Received(1).ToCatalogInfo(
            /* built at construction; a beat must not touch it */);
    }

    [Fact]
    public async Task RegisterReply_TriggersImmediateReRegistration()
    {
        var replies = 0;
        _connection.HeartbeatAsync("node-1", Arg.Any<CancellationToken>())
            .Returns(_ => ++replies == 1 ? HeartbeatReply.ReRegisterRequired : HeartbeatReply.Ok);

        var loop = CreateLoop();
        await RunForAsync(loop, 220);

        await _connection.Received(1).RegisterAsync("node-1", Arg.Any<byte[]>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TransientFailure_DoesNotKillTheLoop()
    {
        var calls = 0;
        _connection.HeartbeatAsync("node-1", Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                calls++;
                if (calls == 1) throw new HighwayTransientException("ERR Transaction failed.");
                return HeartbeatReply.Ok;
            });

        var loop = CreateLoop();
        await RunForAsync(loop, 220);

        calls.Should().BeGreaterThan(1, "a lost beat must be retried on the next tick");
    }

    [Fact]
    public async Task PermanentFailure_DoesNotKillTheLoop()
    {
        var calls = 0;
        _connection.HeartbeatAsync("node-1", Arg.Any<CancellationToken>())
            .Returns<HeartbeatReply>(_ =>
            {
                calls++;
                throw new HighwayTransportException("server gone");
            });

        var loop = CreateLoop();
        await FluentActions.Awaiting(() => RunForAsync(loop, 220)).Should().NotThrowAsync();

        calls.Should().BeGreaterThan(1,
            "a node that cannot beat is invisible, not broken — RPC and pub/sub keep working");
    }

    [Fact]
    public async Task RegistrationFailure_IsSurvivable()
    {
        _connection.RegisterAsync("node-1", Arg.Any<byte[]>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new HighwayTransportException("nope"));
        _connection.HeartbeatAsync("node-1", Arg.Any<CancellationToken>()).Returns(HeartbeatReply.Ok);

        var loop = CreateLoop();

        await FluentActions.Awaiting(() => loop.RegisterAsync()).Should().NotThrowAsync();
        await FluentActions.Awaiting(() => RunForAsync(loop, 150)).Should().NotThrowAsync();
    }

    [Fact]
    public async Task Depart_AnnouncesDeparture()
    {
        var loop = CreateLoop();

        await loop.DepartAsync();

        await _connection.Received(1).DepartAsync("node-1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Depart_Failing_IsSwallowed()
    {
        _connection.DepartAsync("node-1", Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new HighwayTransportException("already gone"));

        var loop = CreateLoop();

        await loop.Invoking(l => l.DepartAsync()).Should().NotThrowAsync(
            "shutdown must never fail on a courtesy message");
    }

    [Fact]
    public async Task Cancellation_StopsPromptly()
    {
        _connection.HeartbeatAsync("node-1", Arg.Any<CancellationToken>()).Returns(HeartbeatReply.Ok);
        var loop = new HeartbeatLoop(
            _connection, _catalog, "node-1", TimeSpan.FromSeconds(30), NullLogger.Instance);

        using var cts = new CancellationTokenSource();
        var run = loop.RunAsync(cts.Token);
        await cts.CancelAsync();

        await run.Invoking(r => r).Should().CompleteWithinAsync(TimeSpan.FromSeconds(2));
    }
}
