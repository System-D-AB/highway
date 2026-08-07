using FluentAssertions;
using Highway.Abstractions;
using Highway.Client.Engine;
using Highway.Client.Scanning;
using Highway.Client.Tests.TestFixtures;
using Highway.Client.Wire;
using NSubstitute;
using Xunit;

namespace Highway.Client.Tests.Engine;

/// <summary>
/// Feature 005 Task 10 — the caller-facing error-mapping table. ExecuteAsync
/// never throws for service-level outcomes (failures are data); PublishAsync has
/// no response object, so its failures are the documented exceptions.
/// </summary>
public class HighwayClientTests
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
        CallTimeout = TimeSpan.FromMilliseconds(200),
    };

    public HighwayClientTests()
    {
        _registry = new PendingCallRegistry(_connection);
        _engine.State.Returns(EngineState.Running);
        _internals.Connection.Returns(_connection);
        _internals.PendingCalls.Returns(_registry);
        _catalog.GetServiceNameForRequestType(typeof(TestRequest)).Returns("test.hello");
        _catalog.GetChannelNameForMessageType(typeof(TestEvent)).Returns("test.events");
    }

    private HighwayClient CreateClient() => new(_catalog, _options, _engine, _internals);

    // ---------------------------------------------------------------- ExecuteAsync

    [Fact]
    public async Task ExecuteAsync_UnknownRequestType_Returns404Data_WithoutNetwork()
    {
        _catalog.GetServiceNameForRequestType(typeof(CancellableRequest)).Returns((string?)null);

        var response = await CreateClient().ExecuteAsync(new CancellableRequest());

        response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        response.Error!.Code.Should().Be("SERVICE_NOT_FOUND");
        await _connection.DidNotReceive().CallAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_EngineNotRunning_Returns503Data()
    {
        _engine.State.Returns(EngineState.NotStarted);

        var response = await CreateClient().ExecuteAsync(new TestRequest { Input = "x" });

        response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        response.Error!.Code.Should().Be("SERVER_UNAVAILABLE");
    }

    [Fact]
    public async Task ExecuteAsync_TransportFailureOnSend_Returns503Data_WithoutWaitingForTimeout()
    {
        _connection
            .CallAsync("test.hello", Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new HighwayTransportException("server rejected"));

        var response = await CreateClient().ExecuteAsync(new TestRequest { Input = "x" });

        response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        response.Error!.Code.Should().Be("SERVER_UNAVAILABLE");
    }

    [Fact]
    public async Task ExecuteAsync_NoReply_Returns504Data()
    {
        _connection.CallAsync("test.hello", Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _connection.GetReplySlotAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((byte[]?)null);

        var response = await CreateClient().ExecuteAsync(new TestRequest { Input = "x" });

        response.StatusCode.Should().Be(StatusCodes.Status504GatewayTimeout);
        response.Error!.Code.Should().Be("CALL_TIMEOUT");
    }

    [Fact]
    public async Task ExecuteAsync_SendsHwCallWithTheCatalogServiceName()
    {
        _connection.CallAsync("test.hello", Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _connection.GetReplySlotAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((byte[]?)null);

        await CreateClient().ExecuteAsync(new TestRequest { Input = "x" });

        await _connection.Received(1).CallAsync(
            "test.hello", Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_ConcurrentCalls_UseDistinctRequestIds()
    {
        var ids = new System.Collections.Concurrent.ConcurrentBag<string>();
        _connection.CallAsync("test.hello", Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<CancellationToken>())
            .Returns(ci => { ids.Add(ci.ArgAt<string>(1)); return Task.CompletedTask; });
        _connection.GetReplySlotAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((byte[]?)null);

        var client = CreateClient();
        await Task.WhenAll(Enumerable.Range(0, 25)
            .Select(_ => client.ExecuteAsync(new TestRequest { Input = "x" })));

        ids.Should().HaveCount(25);
        ids.Distinct().Should().HaveCount(25, "a repeated request id would cross-correlate two callers");
    }

    // ---------------------------------------------------------------- PublishAsync

    [Fact]
    public async Task PublishAsync_UnknownMessageType_ThrowsChannelNotRegistered()
    {
        _catalog.GetChannelNameForMessageType(typeof(UnregisteredEvent)).Returns((string?)null);

        await CreateClient().Invoking(c => c.PublishAsync(new UnregisteredEvent()))
            .Should().ThrowAsync<ChannelNotRegisteredException>();
    }

    [Fact]
    public async Task PublishAsync_EngineNotRunning_Throws()
    {
        _engine.State.Returns(EngineState.NotStarted);

        await CreateClient().Invoking(c => c.PublishAsync(new TestEvent { Data = "x" }))
            .Should().ThrowAsync<HighwayTransportException>();
    }

    [Fact]
    public async Task PublishAsync_SendsHwPublishWithTheCatalogChannelName()
    {
        _connection.PublishCommandAsync(
            "test.events", Arg.Any<byte[]>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>()).Returns(2L);

        await CreateClient().PublishAsync(new TestEvent { Data = "x" });

        await _connection.Received(1).PublishCommandAsync(
            "test.events", Arg.Any<byte[]>(), null, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Feature 013. The delay is converted to an <b>absolute</b> delivery time on the
    /// client, so a slow round trip cannot silently extend it and AOF replay cannot
    /// re-delay from replay time.
    /// </summary>
    [Fact]
    public async Task PublishAsync_WithDelay_SendsAnAbsoluteDeliveryTime()
    {
        _connection.PublishCommandAsync(
            "test.events", Arg.Any<byte[]>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>()).Returns(0L);

        var before = DateTimeOffset.UtcNow;
        await CreateClient().PublishAsync(new TestEvent { Data = "x" }, TimeSpan.FromMinutes(5));

        await _connection.Received(1).PublishCommandAsync(
            "test.events",
            Arg.Any<byte[]>(),
            Arg.Is<DateTimeOffset?>(at => at != null
                                          && at.Value >= before.AddMinutes(5)
                                          && at.Value <= before.AddMinutes(6)),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A non-positive delay publishes immediately rather than failing: it falls out of
    /// ordinary arithmetic on a caller's schedule, and refusing it would make every caller
    /// write the guard Highway can write once.
    /// </summary>
    [Fact]
    public async Task PublishAsync_WithNonPositiveDelay_PublishesImmediately()
    {
        _connection.PublishCommandAsync(
            "test.events", Arg.Any<byte[]>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>()).Returns(1L);

        await CreateClient().PublishAsync(new TestEvent { Data = "x" }, TimeSpan.Zero);
        await CreateClient().PublishAsync(new TestEvent { Data = "x" }, TimeSpan.FromSeconds(-30));

        await _connection.Received(2).PublishCommandAsync(
            "test.events", Arg.Any<byte[]>(), null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PublishAsync_TransportFailure_Propagates()
    {
        _connection.PublishCommandAsync(
                "test.events", Arg.Any<byte[]>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>())
            .Returns<long>(_ => throw new HighwayTransportException("down"));

        await CreateClient().Invoking(c => c.PublishAsync(new TestEvent { Data = "x" }))
            .Should().ThrowAsync<HighwayTransportException>();
    }

    // ---------------------------------------------------------------- fast-fail (006)

    private void EnableFastFail(params string[] hosts)
    {
        _options.FastFailEnabled = true;
        _connection.DiscoverAsync("test.hello", Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<(string, TimeSpan)>)[.. hosts.Select(h => (h, TimeSpan.Zero))]);
        _internals.Discovery.Returns(new ServiceDiscoveryCache(_connection, TimeSpan.Zero));
    }

    [Fact]
    public async Task ExecuteAsync_FastFail_NoLiveHosts_Returns404WithoutEnqueuing()
    {
        EnableFastFail();

        var response = await CreateClient().ExecuteAsync(new TestRequest { Input = "x" });

        response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        response.Error!.Code.Should().Be("SERVICE_NOT_FOUND");
        await _connection.DidNotReceive().CallAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_FastFail_WithLiveHost_EnqueuesNormally()
    {
        EnableFastFail("node-1");
        _connection.GetReplySlotAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((byte[]?)null);

        await CreateClient().ExecuteAsync(new TestRequest { Input = "x" });

        await _connection.Received(1).CallAsync(
            "test.hello", Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_FastFail_DiscoveryFails_StillEnqueues()
    {
        _options.FastFailEnabled = true;
        _connection.DiscoverAsync("test.hello", Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<(string, TimeSpan)>>(_ => throw new HighwayTransportException("down"));
        _internals.Discovery.Returns(new ServiceDiscoveryCache(_connection, TimeSpan.Zero));
        _connection.GetReplySlotAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((byte[]?)null);

        await CreateClient().ExecuteAsync(new TestRequest { Input = "x" });

        await _connection.Received(1).CallAsync(
            "test.hello", Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_FastFailDisabled_NeverConsultsDiscovery()
    {
        _options.FastFailEnabled = false;
        _internals.Discovery.Returns(new ServiceDiscoveryCache(_connection, TimeSpan.Zero));
        _connection.GetReplySlotAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((byte[]?)null);

        await CreateClient().ExecuteAsync(new TestRequest { Input = "x" });

        await _connection.DidNotReceive().DiscoverAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_UnknownRequestType_ShortCircuitsBeforeFastFail()
    {
        EnableFastFail("node-1");
        _catalog.GetServiceNameForRequestType(typeof(CancellableRequest)).Returns((string?)null);

        var response = await CreateClient().ExecuteAsync(new CancellableRequest());

        response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        await _connection.DidNotReceive().DiscoverAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
