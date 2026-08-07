using FluentAssertions;
using Highway.Abstractions;
using Highway.Client.Engine;
using Highway.Client.Tests.TestFixtures;
using Highway.Client.Wire;
using NSubstitute;
using Xunit;

namespace Highway.Client.Tests.Engine;

/// <summary>
/// Feature 005 Task 5 — call correlation, timeout/cancellation mapping, and the
/// race rules that make late replies harmless.
/// </summary>
public class PendingCallRegistryTests
{
    private readonly IHighwayConnection _connection = Substitute.For<IHighwayConnection>();
    private readonly PendingCallRegistry _registry;

    public PendingCallRegistryTests()
    {
        _registry = new PendingCallRegistry(_connection);
    }

    private static byte[] ReplyEnvelope(string value)
        => HighwayJson.EncodeEnvelope("test-node", new TestResponse { Value = value });

    [Fact]
    public async Task TryCompleteFromSlot_WithReply_CompletesWithTypedResponse()
    {
        _connection.GetReplySlotAsync("r1", Arg.Any<CancellationToken>())
            .Returns(ReplyEnvelope("hello"));

        var task = _registry.Register("r1", typeof(TestResponse), TimeSpan.FromSeconds(30), CancellationToken.None);
        await _registry.TryCompleteFromSlotAsync("r1");

        var response = await task;
        response.Should().BeOfType<TestResponse>();
        ((TestResponse)response).Value.Should().Be("hello");
        response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task TryCompleteFromSlot_AfterCompleting_DeletesTheSlot()
    {
        _connection.GetReplySlotAsync("r1", Arg.Any<CancellationToken>()).Returns(ReplyEnvelope("x"));

        var task = _registry.Register("r1", typeof(TestResponse), TimeSpan.FromSeconds(30), CancellationToken.None);
        await _registry.TryCompleteFromSlotAsync("r1");
        await task;

        await _connection.Received(1).DeleteReplySlotAsync("r1", Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Regression: hw:door:rep is node-global — every engine on the server sees
    /// EVERY reply doorbell. A node must never read or delete a slot for a
    /// request it did not issue, or it destroys another caller's reply and hangs
    /// that call until its timeout.
    /// </summary>
    [Fact]
    public async Task TryCompleteFromSlot_ForForeignRequestId_TouchesNothing()
    {
        await _registry.TryCompleteFromSlotAsync("someone-elses-request");

        await _connection.DidNotReceive().GetReplySlotAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _connection.DidNotReceive().DeleteReplySlotAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TryCompleteFromSlot_SlotNotYetWritten_LeavesCallPendingForTheSweep()
    {
        _connection.GetReplySlotAsync("r1", Arg.Any<CancellationToken>()).Returns((byte[]?)null);

        var task = _registry.Register("r1", typeof(TestResponse), TimeSpan.FromSeconds(30), CancellationToken.None);
        await _registry.TryCompleteFromSlotAsync("r1");

        task.IsCompleted.Should().BeFalse("a doorbell that raced ahead of the write must leave the call to the sweep");
        _registry.PendingCount.Should().Be(1);
    }

    [Fact]
    public async Task Register_Timeout_CompletesWith504Data()
    {
        var task = _registry.Register("r1", typeof(TestResponse), TimeSpan.FromMilliseconds(60), CancellationToken.None);

        var response = await task;

        response.StatusCode.Should().Be(StatusCodes.Status504GatewayTimeout);
        response.Error!.Code.Should().Be("CALL_TIMEOUT");
        response.Should().BeOfType<TestResponse>("the caller's own response type must be constructed");
    }

    [Fact]
    public async Task Register_CallerCancellation_ThrowsOperationCanceled()
    {
        using var cts = new CancellationTokenSource();
        var task = _registry.Register("r1", typeof(TestResponse), TimeSpan.FromSeconds(30), cts.Token);

        await cts.CancelAsync();

        await FluentActions.Awaiting(() => task).Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task TryCompleteFromSlot_AfterTimeout_CleansTheSlotWithoutThrowing()
    {
        _connection.GetReplySlotAsync("r1", Arg.Any<CancellationToken>()).Returns(ReplyEnvelope("late"));

        var task = _registry.Register("r1", typeof(TestResponse), TimeSpan.FromMilliseconds(50), CancellationToken.None);
        (await task).StatusCode.Should().Be(StatusCodes.Status504GatewayTimeout);

        // The late reply arrives after the entry is gone: nothing is surfaced,
        // and because the id was ours the slot is still cleaned up.
        await _registry.Invoking(r => r.TryCompleteFromSlotAsync("r1")).Should().NotThrowAsync();
    }

    [Fact]
    public async Task TryCompleteFromSlot_MalformedEnvelope_CompletesWith502Data()
    {
        _connection.GetReplySlotAsync("r1", Arg.Any<CancellationToken>()).Returns("not json"u8.ToArray());

        var task = _registry.Register("r1", typeof(TestResponse), TimeSpan.FromSeconds(30), CancellationToken.None);
        await _registry.TryCompleteFromSlotAsync("r1");

        var response = await task;
        response.StatusCode.Should().Be(StatusCodes.Status502BadGateway);
        response.Error!.Code.Should().Be("BAD_REPLY");
    }

    [Fact]
    public async Task TryCompleteFromSlot_TransportFailure_LeavesCallPending()
    {
        _connection.GetReplySlotAsync("r1", Arg.Any<CancellationToken>())
            .Returns<byte[]?>(_ => throw new HighwayTransportException("boom"));

        var task = _registry.Register("r1", typeof(TestResponse), TimeSpan.FromSeconds(30), CancellationToken.None);
        await _registry.Invoking(r => r.TryCompleteFromSlotAsync("r1")).Should().NotThrowAsync();

        task.IsCompleted.Should().BeFalse("a transient read failure must not fail the call — the next sweep retries");
    }

    [Fact]
    public void Register_DuplicateRequestId_Throws()
    {
        _ = _registry.Register("dup", typeof(TestResponse), TimeSpan.FromSeconds(30), CancellationToken.None);

        Action act = () => _registry.Register("dup", typeof(TestResponse), TimeSpan.FromSeconds(30), CancellationToken.None);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task TryFail_CompletesWithSuppliedFailure()
    {
        var task = _registry.Register("r1", typeof(TestResponse), TimeSpan.FromSeconds(30), CancellationToken.None);

        _registry.TryFail("r1", PendingCallRegistry.BuildErrorResponse(
            typeof(TestResponse), StatusCodes.Status503ServiceUnavailable, "SERVER_UNAVAILABLE", "down"));

        (await task).StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        _registry.PendingCount.Should().Be(0);
    }

    [Fact]
    public void TryFail_ForUnknownRequestId_IsANoOp()
        => _registry.Invoking(r => r.TryFail("nope", new TestResponse())).Should().NotThrow();

    [Fact]
    public async Task Sweep_OnlyReadsSlotsOlderThanTheGraceWindow()
    {
        _connection.GetReplySlotAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((byte[]?)null);
        _ = _registry.Register("fresh", typeof(TestResponse), TimeSpan.FromSeconds(30), CancellationToken.None);

        await _registry.SweepAsync(TimeSpan.FromMinutes(1));

        await _connection.DidNotReceive().GetReplySlotAsync("fresh", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Sweep_AgedCall_ReadsTheSlot()
    {
        _connection.GetReplySlotAsync("aged", Arg.Any<CancellationToken>()).Returns((byte[]?)null);
        _ = _registry.Register("aged", typeof(TestResponse), TimeSpan.FromSeconds(30), CancellationToken.None);

        await Task.Delay(60);
        await _registry.SweepAsync(TimeSpan.FromMilliseconds(20));

        await _connection.Received().GetReplySlotAsync("aged", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Sweep_WithNoPendingCalls_PerformsNoNetworkIo()
    {
        await _registry.SweepAsync(TimeSpan.Zero);

        await _connection.DidNotReceive().GetReplySlotAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConcurrentCalls_EachCompleteWithTheirOwnResponse()
    {
        const int count = 50;
        for (var i = 0; i < count; i++)
        {
            var id = $"r{i}";
            _connection.GetReplySlotAsync(id, Arg.Any<CancellationToken>()).Returns(ReplyEnvelope($"v{i}"));
        }

        var tasks = Enumerable.Range(0, count)
            .Select(i => _registry.Register($"r{i}", typeof(TestResponse), TimeSpan.FromSeconds(30), CancellationToken.None))
            .ToList();

        await Task.WhenAll(Enumerable.Range(0, count)
            .Select(i => Task.Run(() => _registry.TryCompleteFromSlotAsync($"r{i}"))));

        var responses = await Task.WhenAll(tasks);
        for (var i = 0; i < count; i++)
            ((TestResponse)responses[i]).Value.Should().Be($"v{i}", "correlation must never cross calls");

        _registry.PendingCount.Should().Be(0);
    }
}
