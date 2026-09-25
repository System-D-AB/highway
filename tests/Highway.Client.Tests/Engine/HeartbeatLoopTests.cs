using System.Text;
using System.Text.Json;
using FluentAssertions;
using Highway.Abstractions;
using Highway.Client.Engine;
using Highway.Client.Wire;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Highway.Client.Tests.Engine;

/// <summary>
/// Features 006 / 067 / 068 — the heartbeat loop and the session it keeps.
///
/// <para>The behaviours worth pinning: the session (catalog, subscriptions, jobs) is serialized once and
/// never rides a normal beat; a <c>+SESSION</c> reply re-declares the whole session at once rather than
/// waiting; and with heartbeats off the session carries no catalog, so the node never looks registered and
/// then dead.</para>
/// </summary>
public class HeartbeatLoopTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(40);

    private readonly IHighwayConnection _connection = Substitute.For<IHighwayConnection>();

    private static readonly CatalogInfo Catalog = new()
    {
        Services = [new CatalogServiceEntry { Name = "orders.create", RequestTypeName = "R", ResponseTypeName = "S" }],
        Channels = [],
    };

    private SessionKeeper Session(CatalogInfo? catalog = null) => new(
        _connection, "node-1", catalog ?? Catalog,
        [new SessionKeeper.Subscription("orders.shipped", "billing")],
        [new SessionKeeper.Job("ticks", "minutely", "every:60", "{}"u8.ToArray())],
        NullLogger.Instance);

    private HeartbeatLoop CreateLoop(SessionKeeper? session = null, TimeSpan? interval = null)
        => new(_connection, session ?? Session(), "node-1", interval ?? Interval, NullLogger.Instance);

    private static async Task RunForAsync(HeartbeatLoop loop, int ms)
    {
        using var cts = new CancellationTokenSource();
        var run = loop.RunAsync(cts.Token);
        await Task.Delay(ms);
        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task Establish_DeclaresCatalog_Subscriptions_AndJobs_InOneCommand()
    {
        byte[]? sent = null;
        _connection.EstablishSessionAsync("node-1", Arg.Do<byte[]>(b => sent = b), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await Session().EstablishAsync(CancellationToken.None);

        using var doc = JsonDocument.Parse(sent!);
        doc.RootElement.GetProperty("catalog").GetProperty("services")[0].GetProperty("name").GetString().Should().Be("orders.create");
        doc.RootElement.GetProperty("subscriptions")[0].GetProperty("group").GetString().Should().Be("billing");
        var job = doc.RootElement.GetProperty("jobs")[0];
        job.GetProperty("expression").GetString().Should().Be("every:60");
        Encoding.UTF8.GetString(job.GetProperty("template").GetBytesFromBase64()).Should().Be("{}");
    }

    [Fact]
    public async Task WithoutHeartbeats_TheSessionCarriesNoCatalog()
    {
        byte[]? sent = null;
        _connection.EstablishSessionAsync("node-1", Arg.Do<byte[]>(b => sent = b), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        var session = new SessionKeeper(_connection, "node-1", catalog: null,
            [new SessionKeeper.Subscription("orders.shipped", "billing")], [], NullLogger.Instance);

        await session.EstablishAsync(CancellationToken.None);

        using var doc = JsonDocument.Parse(sent!);
        doc.RootElement.TryGetProperty("catalog", out _).Should().BeFalse(
            "a node that never beats must not look registered — it would look dead and lose its work");
        session.CatalogBytes.Should().Be(0);
    }

    [Fact]
    public async Task SteadyState_SendsLivenessOnly_NeverTheSession()
    {
        _connection.HeartbeatAsync("node-1", Arg.Any<CancellationToken>()).Returns(HeartbeatReply.Ok);

        await RunForAsync(CreateLoop(), 220);

        await _connection.Received().HeartbeatAsync("node-1", Arg.Any<CancellationToken>());
        await _connection.DidNotReceive().EstablishSessionAsync(Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SessionReply_ReDeclaresTheWholeSession_AtOnce()
    {
        var replies = 0;
        _connection.HeartbeatAsync("node-1", Arg.Any<CancellationToken>())
            .Returns(_ => ++replies == 1 ? HeartbeatReply.SessionRequired : HeartbeatReply.Ok);

        await RunForAsync(CreateLoop(), 220);

        await _connection.Received(1).EstablishSessionAsync("node-1", Arg.Any<byte[]>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Reestablishment_IsSingleFlight_AndSurvivesFailure()
    {
        var gate = new TaskCompletionSource();
        var calls = 0;
        _connection.EstablishSessionAsync("node-1", Arg.Any<byte[]>(), Arg.Any<CancellationToken>())
            .Returns(_ => { Interlocked.Increment(ref calls); return gate.Task; });
        var session = Session();

        var first = session.ReestablishAsync("failover");
        var second = session.ReestablishAsync("+SESSION");   // one already under way covers it
        await second;
        calls.Should().Be(1);

        gate.SetException(new HighwayTransportException("broker gone"));
        await FluentActions.Awaiting(() => first).Should().NotThrowAsync("a re-declaration failure waits for the next trigger");
    }

    [Fact]
    public void Session_IsSerializedOnce_AtConstruction()
        => CreateLoop().CatalogBytes.Should().BeGreaterThan(0);

    [Fact]
    public async Task TransientFailure_DoesNotKillTheLoop()
    {
        var calls = 0;
        _connection.HeartbeatAsync("node-1", Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                calls++;
                if (calls == 1) throw new HighwayTransientException("transient");
                return HeartbeatReply.Ok;
            });

        await RunForAsync(CreateLoop(), 220);

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
    public async Task Depart_AnnouncesDeparture()
    {
        await CreateLoop().DepartAsync();
        await _connection.Received(1).DepartAsync("node-1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Depart_Failing_IsSwallowed()
    {
        _connection.DepartAsync("node-1", Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new HighwayTransportException("already gone"));

        await CreateLoop().Invoking(l => l.DepartAsync()).Should().NotThrowAsync(
            "shutdown must never fail on a courtesy message");
    }

    [Fact]
    public async Task Cancellation_StopsPromptly()
    {
        _connection.HeartbeatAsync("node-1", Arg.Any<CancellationToken>()).Returns(HeartbeatReply.Ok);
        var loop = CreateLoop(interval: TimeSpan.FromSeconds(30));

        using var cts = new CancellationTokenSource();
        var run = loop.RunAsync(cts.Token);
        await cts.CancelAsync();

        await run.Invoking(r => r).Should().CompleteWithinAsync(TimeSpan.FromSeconds(2));
    }
}
