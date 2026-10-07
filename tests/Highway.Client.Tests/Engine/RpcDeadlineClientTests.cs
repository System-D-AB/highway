using FluentAssertions;
using Highway.Abstractions;
using Highway.Client.Engine;
using Highway.Client.Scanning;
using Highway.Client.Tests.TestFixtures;
using Highway.Client.Wire;
using NSubstitute;
using Xunit;

namespace Highway.Client.Tests.Engine;

[Service("deadline.long", Timeout = "00:05:00")]
public sealed class LongRequest : IReturn<TestResponse>;

[Service("deadline.bad", Timeout = "five minutes")]
public sealed class BadTimeoutRequest : IReturn<TestResponse>;

/// <summary>
/// 074 Part A, client side (T7, T8 registry, T10): every call tells the broker how long its caller waits; a call made
/// inside a handler never outlives that handler's caller; a service nobody serves fails at once; a caller that gives
/// up withdraws its request; a re-send after a failover carries only the time left.
/// </summary>
public class RpcDeadlineClientTests
{
    private readonly IHighwayConnection _connection = Substitute.For<IHighwayConnection>();
    private readonly IHighwayEngine _engine = Substitute.For<IHighwayEngine>();
    private readonly IHighwayEngineInternals _internals = Substitute.For<IHighwayEngineInternals>();
    private readonly ICatalog _catalog = Substitute.For<ICatalog>();
    private readonly PendingCallRegistry _registry;
    private readonly HighwayOptions _options = new()
    {
        NodeName = "test-node",
        Server = "localhost:6500",
        CallTimeout = TimeSpan.FromSeconds(30),
    };
    private readonly List<TimeSpan?> _sentTimeouts = [];

    public RpcDeadlineClientTests()
    {
        _registry = new PendingCallRegistry(_connection);
        _engine.State.Returns(EngineState.Running);
        _internals.Connection.Returns(_connection);
        _internals.PendingCalls.Returns(_registry);
        _catalog.GetServiceNameForRequestType(typeof(TestRequest)).Returns("test.hello");
        _catalog.GetServiceNameForRequestType(typeof(LongRequest)).Returns("deadline.long");
        _connection.CallAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(ci => { lock (_sentTimeouts) _sentTimeouts.Add(ci.ArgAt<TimeSpan?>(3)); return Task.CompletedTask; });
        // No reply is waiting (NSubstitute's auto-value would be an empty array — a "reply").
        _connection.TakeReplyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<byte[]?>(null));
    }

    private HighwayClient Client() => new(_catalog, _options, _engine, _internals);

    /// <summary>Starts a call that will never be answered and returns the timeout it sent once it has been sent.</summary>
    private async Task<TimeSpan?> SentTimeout(Func<CancellationToken, Task> call)
    {
        using var cts = new CancellationTokenSource();
        var task = call(cts.Token);
        for (var i = 0; i < 200 && _sentTimeouts.Count == 0; i++) await Task.Delay(5);
        await cts.CancelAsync();
        try { await task; } catch (OperationCanceledException) { }
        return _sentTimeouts.Single();
    }

    [Fact]
    public async Task ACallWithoutAnyTimeout_SendsNone_SoTheBrokersDefaultApplies()
    {
        // D1: the timeout is optional on the wire; the broker's DefaultRpcTimeout (30 s, = CallTimeout) applies.
        (await SentTimeout(ct => Client().ExecuteAsync(new TestRequest(), ct))).Should().BeNull();
    }

    [Fact]
    public async Task AContractTimeout_IsUsed_AndAPerCallTimeoutOverridesIt()
    {
        (await SentTimeout(ct => Client().ExecuteAsync(new LongRequest(), ct))).Should().Be(TimeSpan.FromMinutes(5));
        _sentTimeouts.Clear();
        (await SentTimeout(ct => Client().ExecuteAsync(new LongRequest(), TimeSpan.FromSeconds(2), ct))).Should().Be(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task ACallInsideAHandler_NeverOutlivesThatHandlersCaller()
    {
        using (AmbientDeadline.Enter(TimeSpan.FromSeconds(3)))
        {
            var sent = await SentTimeout(ct => Client().ExecuteAsync(new LongRequest(), ct));
            sent.Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(3), "the inner call inherits the outer call's remaining time")
                .And.BeGreaterThan(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task ACallInsideAHandlerWhoseDeadlineHasPassed_IsNotMade()
    {
        using var scope = AmbientDeadline.Enter(TimeSpan.FromMilliseconds(1));
        await Task.Delay(20);

        var response = await Client().ExecuteAsync(new TestRequest());

        response.StatusCode.Should().Be(StatusCodes.Status504GatewayTimeout);
        response.Error!.Code.Should().Be("DEADLINE_PASSED");
        _sentTimeouts.Should().BeEmpty();
    }

    [Fact]
    public async Task AServiceNobodyHasEverServed_Answers503AtOnce()
    {
        _connection.CallAsync("test.hello", Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new HighwayTransportException("ERR HW_NO_WORKERS no node has ever served 'test.hello'"));
        var started = DateTime.UtcNow;

        var response = await Client().ExecuteAsync(new TestRequest());

        response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        response.Error!.Code.Should().Be("NO_WORKERS");
        (DateTime.UtcNow - started).Should().BeLessThan(TimeSpan.FromSeconds(5), "not after waiting out the 30 s timeout");
        _registry.PendingCount.Should().Be(0);
    }

    [Fact]
    public async Task ACallerThatGivesUp_WithdrawsItsRequest()
    {
        _connection.CancelCallAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);

        await SentTimeout(ct => Client().ExecuteAsync(new TestRequest(), ct));

        for (var i = 0; i < 100 && _connection.ReceivedCalls().All(c => c.GetMethodInfo().Name != nameof(IHighwayConnection.CancelCallAsync)); i++)
            await Task.Delay(5);
        await _connection.Received(1).CancelCallAsync("test.hello", Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ACallThatTimesOut_IsNotWithdrawn_TheBrokerAlreadyEndsIt()
    {
        var response = await Client().ExecuteAsync(new TestRequest(), TimeSpan.FromMilliseconds(50));

        response.StatusCode.Should().Be(StatusCodes.Status504GatewayTimeout);
        await _connection.DidNotReceiveWithAnyArgs().CancelCallAsync(default!, default!, default);
    }

    [Fact]
    public async Task AReSendAfterAFailover_CarriesOnlyTheTimeLeft()
    {
        var call = _registry.Register("r-1", typeof(TestResponse), TimeSpan.FromSeconds(10), CancellationToken.None, "test.hello", [1]);
        await Task.Delay(300);

        await _registry.ReplayPendingAsync();

        var resent = _sentTimeouts.Should().ContainSingle().Subject!.Value;
        resent.Should().BeLessThan(TimeSpan.FromSeconds(10)).And.BeGreaterThan(TimeSpan.FromSeconds(9));
        call.IsCompleted.Should().BeFalse();
    }

    [Fact]
    public async Task ACallWithNoTimeLeftAtAFailover_IsAnsweredLocally_NotReSent()
    {
        // The race this guards: the failover re-send runs after the call's deadline but before its own timer has
        // fired. The clock is moved past the deadline so the timer (10 s) has certainly not fired yet.
        var call = _registry.Register("r-1", typeof(TestResponse), TimeSpan.FromSeconds(10), CancellationToken.None, "test.hello", [1]);
        var real = _registry.Timestamp;
        _registry.Timestamp = () => real() + 11 * System.Diagnostics.Stopwatch.Frequency;

        await _registry.ReplayPendingAsync();

        _sentTimeouts.Should().BeEmpty("a caller with no time left is owed an answer, not a worker running for nobody");
        call.IsCompleted.Should().BeTrue("answered here, at once — not left to its own timer");
        (await call).StatusCode.Should().Be(StatusCodes.Status504GatewayTimeout);
    }

    [Fact]
    public void AnUnparseableContractTimeout_IsAStartupError()
    {
        var act = () => new DefaultTypeScanner().ScanTypes([typeof(BadTimeoutRequest)]);

        act.Should().Throw<InvalidServiceTimeoutException>().WithMessage("*five minutes*");
    }

    [Fact]
    public async Task WorkAHandlerStartsThatOutlivesIt_DoesNotInheritAnEndedDeadline()
    {
        // A timer or fire-and-forget task created inside a handler captures the async flow. Once the handler ends,
        // its deadline must not follow that work: every call from it would be refused as DEADLINE_PASSED forever.
        Task<TimeSpan?> later;
        using (AmbientDeadline.Enter(TimeSpan.FromMilliseconds(50)))
        {
            later = Task.Run(async () => { await Task.Delay(200); return AmbientDeadline.Remaining; });
        }

        (await later).Should().BeNull("the handler that owned the deadline has ended");
    }

    [Fact]
    public async Task ACallsTimeout_NeverExceedsTheBrokersMaximum()
    {
        _options.LearnedMaxRpcTimeout = TimeSpan.FromMinutes(10);

        (await SentTimeout(ct => Client().ExecuteAsync(new TestRequest(), TimeSpan.FromHours(2), ct)))
            .Should().Be(TimeSpan.FromMinutes(10), "the broker would drop the request at its maximum; waiting longer is waiting for nothing");
    }

    [Theory]
    [InlineData("30")]          // thirty DAYS to TimeSpan.Parse — never what was meant
    [InlineData("1")]           // one DAY — within the cap, so only the required colon refuses it
    [InlineData("2.00:00:00")]  // above a day
    [InlineData("00:00:00")]
    public void AnUnreasonableContractTimeout_IsRefused(string text)
    {
        ServiceTimeouts.TryParse(text, out _).Should().BeFalse();
    }

    [Fact]
    public async Task AWithdrawal_WaitsForTheCallItWithdraws()
    {
        var order = new List<string>();
        var release = new TaskCompletionSource();
        _connection.CallAsync("test.hello", Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(async _ => { await release.Task; lock (order) order.Add("CALL"); });
        _connection.CancelCallAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => { lock (order) order.Add("CANCEL"); return Task.FromResult(true); });
        using var cts = new CancellationTokenSource();

        var call = Client().ExecuteAsync(new TestRequest(), cts.Token);
        await cts.CancelAsync();          // the caller gives up while its HW.CALL is still on the wire
        await Task.Delay(100);
        lock (order) order.Should().BeEmpty("the cancel must not overtake the call");
        release.SetResult();

        try { await call; } catch (OperationCanceledException) { }
        for (var i = 0; i < 100 && order.Count < 2; i++) await Task.Delay(10);
        order.Should().Equal("CALL", "CANCEL");
    }
}
