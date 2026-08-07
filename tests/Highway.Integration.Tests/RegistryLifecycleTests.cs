using FluentAssertions;
using Highway.Abstractions;
using Highway.Server;
using StackExchange.Redis;
using Xunit;

namespace Highway.Integration.Tests;

/// <summary>
/// Feature 006 Task 13 — the registry through real engines: a node that starts
/// becomes discoverable with no application code, and one that stops cleanly
/// stops being discoverable straight away rather than after the expiry window.
/// </summary>
public class RegistryLifecycleTests : IDisposable
{
    private readonly HighwayTestServer _server = new();
    private readonly ConnectionMultiplexer _redis;
    private readonly List<EngineNode> _nodes = [];

    public RegistryLifecycleTests()
    {
        _redis = ConnectionMultiplexer.Connect(_server.ConnectionString);
    }

    public void Dispose()
    {
        foreach (var node in _nodes)
            node.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _redis.Dispose();
        _server.Dispose();
    }

    private async Task<EngineNode> StartNodeAsync(string name, Action<Highway.Client.HighwayOptions>? tune = null)
    {
        var node = await EngineNode.StartAsync(_server.ConnectionString, name, tune);
        _nodes.Add(node);
        return node;
    }

    private string[] Discover(string service)
    {
        var result = (RedisResult[])_redis.GetDatabase().Execute("HW.DISCOVER", service)!;
        return [.. result.Select(r => (string)((RedisResult[])r!)[0]!)];
    }

    private Dictionary<string, string> Stats(params string[] args)
    {
        var flat = (RedisResult[])_redis.GetDatabase().Execute("HW.STATS", args)!;
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i + 1 < flat.Length; i += 2)
            map[(string)flat[i]!] = (string)flat[i + 1]!;
        return map;
    }

    /// <summary>
    /// Registration happens at start, not on the first interval — otherwise the
    /// first call after a deployment would fast-fail spuriously.
    /// </summary>
    [Fact]
    public async Task StartedNode_IsDiscoverableImmediately_WithNoApplicationCode()
    {
        await StartNodeAsync("registry-node-1");

        Discover("it.echo").Should().Contain("registry-node-1");
    }

    [Fact]
    public async Task StartedNode_AppearsInServerStats()
    {
        await StartNodeAsync("registry-stats-node");

        var stats = Stats();
        stats["kind"].Should().Be("server");
        int.Parse(stats["nodes"]).Should().BeGreaterThanOrEqualTo(1);
        int.Parse(stats["services"]).Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task GracefullyStoppedNode_LeavesDiscoveryPromptly()
    {
        var node = await EngineNode.StartAsync(_server.ConnectionString, "departing-node");
        Discover("it.echo").Should().Contain("departing-node");

        await node.DisposeAsync();

        // The BYE form runs the full teardown, so the node is gone now rather
        // than after the 30s expiry window.
        Discover("it.echo").Should().NotContain("departing-node",
            "a clean shutdown must not leave a phantom host in the registry");
    }

    [Fact]
    public async Task TwoNodes_BothDiscoverable_AndOneLeavingDoesNotAffectTheOther()
    {
        var first = await EngineNode.StartAsync(_server.ConnectionString, "pair-node-1");
        await StartNodeAsync("pair-node-2");

        Discover("it.echo").Should().Contain(["pair-node-1", "pair-node-2"]);

        await first.DisposeAsync();

        Discover("it.echo").Should().NotContain("pair-node-1").And.Contain("pair-node-2");
    }

    [Fact]
    public async Task HeartbeatDisabled_NodeStaysOutOfTheRegistry_ButStillServes()
    {
        var caller = await StartNodeAsync("no-hb-caller", o => o.HeartbeatEnabled = false);
        await StartNodeAsync("no-hb-host", o => o.HeartbeatEnabled = false);

        Discover("it.echo").Should().BeEmpty("a node with heartbeat off never registers");

        // RPC is entirely unaffected: the registry is for discovery and
        // observability, never for routing.
        var response = await caller.Client.ExecuteAsync(new ItEchoRequest { Value = "still-works" });

        response.StatusCode.Should().Be(StatusCodes.Status200OK);
        response.Value.Should().Be("still-works");
    }

    [Fact]
    public async Task FastFail_UnhostedService_Returns404WithoutWaitingForTheCallTimeout()
    {
        // No host for it.echo is started, and the caller's own catalog does list
        // it (assembly-wide scanning), so only the registry can reveal that
        // nobody is serving it.
        var caller = await StartNodeAsync("fastfail-caller", o =>
        {
            o.FastFailEnabled = true;
            o.DiscoveryCacheTtl = TimeSpan.Zero;
            o.CallTimeout = TimeSpan.FromSeconds(30);
        });

        // Remove the caller's own registration so no node claims the service.
        _redis.GetDatabase().Execute("HW.HEARTBEAT", "fastfail-caller", "BYE");

        var started = DateTime.UtcNow;
        var response = await caller.Client.ExecuteAsync(new ItEchoRequest { Value = "x" });
        var elapsed = DateTime.UtcNow - started;

        response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        response.Error!.Code.Should().Be("SERVICE_NOT_FOUND");
        elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5),
            "fast-fail exists to surface a misconfiguration in milliseconds, not after CallTimeout");
    }

    [Fact]
    public async Task FastFail_WithALiveHost_CallSucceedsNormally()
    {
        var caller = await StartNodeAsync("fastfail-ok-caller", o =>
        {
            o.FastFailEnabled = true;
            o.DiscoveryCacheTtl = TimeSpan.Zero;
        });
        await StartNodeAsync("fastfail-ok-host");

        var response = await caller.Client.ExecuteAsync(new ItEchoRequest { Value = "hello" });

        response.StatusCode.Should().Be(StatusCodes.Status200OK);
        response.Value.Should().Be("hello");
    }

    [Fact]
    public async Task NodeSurvivesRegistryLoss_ByReRegisteringOnTheNextBeat()
    {
        await StartNodeAsync("resilient-node", o => o.HeartbeatInterval = TimeSpan.FromMilliseconds(200));
        Discover("it.echo").Should().Contain("resilient-node");

        // Simulate the registry being wiped underneath a live node — a
        // memory-only server restart, or an operator clearing state.
        _redis.GetDatabase().Execute("HW.HEARTBEAT", "resilient-node", "BYE");
        Discover("it.echo").Should().NotContain("resilient-node");

        // The next liveness beat gets REGISTER back and re-registers itself.
        var recovered = false;
        for (var i = 0; i < 40 && !recovered; i++)
        {
            await Task.Delay(100);
            recovered = Discover("it.echo").Contains("resilient-node");
        }

        recovered.Should().BeTrue(
            "a live node must recover its registration without operator action");
    }
}
