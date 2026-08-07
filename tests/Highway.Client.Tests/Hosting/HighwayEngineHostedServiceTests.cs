using FluentAssertions;
using Highway.Client.Engine;
using Highway.Client.Hosting;
using NSubstitute;
using Xunit;

namespace Highway.Client.Tests.Hosting;

/// <summary>
/// Feature 005 Task 11 — the host bridge. Engine lifecycle ordering itself is
/// asserted end-to-end in the integration suite (it needs a live server); what
/// is unit-testable here is that the host's signals reach the engine unchanged.
/// </summary>
public class HighwayEngineHostedServiceTests
{
    private readonly IHighwayEngine _engine = Substitute.For<IHighwayEngine>();

    [Fact]
    public async Task StartAsync_DelegatesToTheEngine_PassingTheHostToken()
    {
        using var cts = new CancellationTokenSource();
        var hosted = new HighwayEngineHostedService(_engine);

        await hosted.StartAsync(cts.Token);

        await _engine.Received(1).StartAsync(cts.Token);
    }

    [Fact]
    public async Task StopAsync_DelegatesToTheEngine_PassingTheHostToken()
    {
        using var cts = new CancellationTokenSource();
        var hosted = new HighwayEngineHostedService(_engine);

        await hosted.StopAsync(cts.Token);

        await _engine.Received(1).StopAsync(cts.Token);
    }

    [Fact]
    public async Task StartAsync_EngineFailure_PropagatesSoTheHostFailsFast()
    {
        _engine.StartAsync(Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new HighwayServerUnreachableException("localhost:6500", new Exception("no")));

        var hosted = new HighwayEngineHostedService(_engine);

        await hosted.Invoking(h => h.StartAsync(CancellationToken.None))
            .Should().ThrowAsync<HighwayServerUnreachableException>(
                "a broker that cannot be reached must fail host startup, not degrade silently");
    }

    [Fact]
    public async Task StopAsync_CalledTwice_IsSafe()
    {
        var hosted = new HighwayEngineHostedService(_engine);

        await hosted.StopAsync(CancellationToken.None);
        await hosted.Invoking(h => h.StopAsync(CancellationToken.None)).Should().NotThrowAsync();

        await _engine.Received(2).StopAsync(Arg.Any<CancellationToken>());
    }
}
