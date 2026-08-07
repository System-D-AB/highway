using FluentAssertions;
using Highway.Client.Engine;
using Highway.Client.Wire;
using NSubstitute;
using Xunit;

namespace Highway.Client.Tests.Engine;

/// <summary>
/// Feature 006 Task 13 — the discovery cache behind fast-fail.
///
/// <para>The safety rule under test: only a fresh, successful, empty lookup may
/// fast-fail. Every other path must return <see cref="DiscoveryOutcome.Proceed"/>,
/// so the cache can delay a fast-fail but can never drop a request that would
/// otherwise have been served.</para>
/// </summary>
public class ServiceDiscoveryCacheTests
{
    private readonly IHighwayConnection _connection = Substitute.For<IHighwayConnection>();

    private static IReadOnlyList<(string, TimeSpan)> Hosts(params string[] nodes)
        => [.. nodes.Select(n => (n, TimeSpan.Zero))];

    private ServiceDiscoveryCache Cache(TimeSpan? ttl = null)
        => new(_connection, ttl ?? TimeSpan.FromSeconds(1));

    [Fact]
    public async Task LiveHosts_Proceeds()
    {
        _connection.DiscoverAsync("svc", Arg.Any<CancellationToken>()).Returns(Hosts("node-1"));

        (await Cache().CheckAsync("svc")).Should().Be(DiscoveryOutcome.Proceed);
    }

    [Fact]
    public async Task FreshEmptyResult_IsTheOnlyFastFail()
    {
        _connection.DiscoverAsync("svc", Arg.Any<CancellationToken>()).Returns(Hosts());

        (await Cache().CheckAsync("svc")).Should().Be(DiscoveryOutcome.NoLiveHosts);
    }

    [Fact]
    public async Task DiscoveryFailure_Proceeds_NeverFastFails()
    {
        _connection.DiscoverAsync("svc", Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<(string, TimeSpan)>>(_ => throw new HighwayTransportException("down"));

        (await Cache().CheckAsync("svc")).Should().Be(DiscoveryOutcome.Proceed,
            "a failed lookup must never turn into a 404 for a service that may well be running");
    }

    [Fact]
    public async Task TransientFailure_Proceeds()
    {
        _connection.DiscoverAsync("svc", Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<(string, TimeSpan)>>(_ => throw new HighwayTransientException("ERR Transaction failed."));

        (await Cache().CheckAsync("svc")).Should().Be(DiscoveryOutcome.Proceed);
    }

    [Fact]
    public async Task WithinTtl_ResultIsReused()
    {
        _connection.DiscoverAsync("svc", Arg.Any<CancellationToken>()).Returns(Hosts("node-1"));
        var cache = Cache(TimeSpan.FromSeconds(30));

        for (var i = 0; i < 5; i++)
            await cache.CheckAsync("svc");

        await _connection.Received(1).DiscoverAsync("svc", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AfterTtl_ResultIsRefetched()
    {
        _connection.DiscoverAsync("svc", Arg.Any<CancellationToken>()).Returns(Hosts("node-1"));
        var cache = Cache(TimeSpan.FromMilliseconds(40));

        await cache.CheckAsync("svc");
        await Task.Delay(90);
        await cache.CheckAsync("svc");

        await _connection.Received(2).DiscoverAsync("svc", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ZeroTtl_DisablesCaching()
    {
        _connection.DiscoverAsync("svc", Arg.Any<CancellationToken>()).Returns(Hosts("node-1"));
        var cache = Cache(TimeSpan.Zero);

        await cache.CheckAsync("svc");
        await cache.CheckAsync("svc");

        await _connection.Received(2).DiscoverAsync("svc", Arg.Any<CancellationToken>());
        cache.Count.Should().Be(0);
    }

    [Fact]
    public async Task ServicesAreCachedIndependently()
    {
        _connection.DiscoverAsync("a", Arg.Any<CancellationToken>()).Returns(Hosts("node-1"));
        _connection.DiscoverAsync("b", Arg.Any<CancellationToken>()).Returns(Hosts());
        var cache = Cache(TimeSpan.FromSeconds(30));

        (await cache.CheckAsync("a")).Should().Be(DiscoveryOutcome.Proceed);
        (await cache.CheckAsync("b")).Should().Be(DiscoveryOutcome.NoLiveHosts);
        cache.Count.Should().Be(2);
    }

    [Fact]
    public async Task Cancellation_Propagates()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        _connection.DiscoverAsync("svc", Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<(string, TimeSpan)>>(_ => throw new OperationCanceledException());

        await Cache().Invoking(c => c.CheckAsync("svc", cts.Token))
            .Should().ThrowAsync<OperationCanceledException>();
    }
}
