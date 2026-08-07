using FluentAssertions;
using Highway.Server;
using StackExchange.Redis;
using Xunit;
using Xunit.Abstractions;

namespace Highway.Integration.Tests;

/// <summary>
/// Feature 005 Task 1 — promoted spike (outcome: positive). SE.Redis 2.8.24
/// auto-reconnects after a same-port server restart AND automatically
/// re-establishes pub/sub subscriptions, so DoorbellWatcher needs no
/// ConnectionRestored re-issue logic. See 005/design.md § Spikes.
/// </summary>
public class ClientReconnectTests : IDisposable
{
    private readonly HighwayTestServer _server = new();
    private readonly ITestOutputHelper _output;

    public ClientReconnectTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public void Dispose() => _server.Dispose();

    [Fact]
    public async Task Subscriber_SurvivesServerRestart_OnSamePort()
    {
        var cs = _server.ConnectionString;

        var config = ConfigurationOptions.Parse(cs);
        config.ConnectTimeout = 5000;
        config.SyncTimeout = 5000;

        await using var redis = await ConnectionMultiplexer.ConnectAsync(config);

        var restored = new SemaphoreSlim(0);
        redis.ConnectionRestored += (_, ep) =>
        {
            _output.WriteLine($"ConnectionRestored: {ep}");
            restored.Release();
        };

        var rings = 0;
        var gate = new SemaphoreSlim(0);
        await redis.GetSubscriber().SubscribeAsync(RedisChannel.Literal("hw:door:rep"), (_, _) =>
        {
            Interlocked.Increment(ref rings);
            gate.Release();
        });

        // Doorbell 1 — before restart
        using (var writer = ConnectionMultiplexer.Connect(cs))
            writer.GetDatabase().Execute("HW.REPLY", "spike-r1", "p1");
        (await gate.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue("pre-restart doorbell");

        _server.Restart();

        // Wait for SE.Redis to notice and reconnect
        var reconnected = await restored.WaitAsync(TimeSpan.FromSeconds(15));
        _output.WriteLine($"Reconnected within 15s: {reconnected}; IsConnected={redis.IsConnected}");
        reconnected.Should().BeTrue("SE.Redis must auto-reconnect to the restarted server");

        // Doorbell 2 — after restart, same subscription
        using (var writer = ConnectionMultiplexer.Connect(cs))
            writer.GetDatabase().Execute("HW.REPLY", "spike-r2", "p2");

        var rang = await gate.WaitAsync(TimeSpan.FromSeconds(10));
        _output.WriteLine($"Post-restart doorbell received: {rang}; total rings={Volatile.Read(ref rings)}");
        rang.Should().BeTrue("the subscription must be re-established automatically after reconnect");
        Volatile.Read(ref rings).Should().Be(2);
    }
}
