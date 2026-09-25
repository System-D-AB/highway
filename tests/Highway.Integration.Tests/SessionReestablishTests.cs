using FluentAssertions;
using Highway.Server;
using StackExchange.Redis;
using Xunit;

namespace Highway.Integration.Tests;

/// <summary>
/// Feature 067 T4 (constraint C2.6) — a node's session follows it to a broker that never saw it. A
/// memory-only restart is exactly that broker: same address, nothing registered. Before 3.0 the client
/// registered its subscriptions once, at start, so publishes to its groups then reached nobody until the
/// process restarted. Now the broker's <c>+SESSION</c> answer to a heartbeat makes the client re-declare.
/// </summary>
[Collection(SubscriberRecorderCollection.Name)]
public class SessionReestablishTests : IAsyncLifetime
{
    private readonly HighwayTestServer _server = new();
    private EngineNode? _node;

    public async Task InitializeAsync()
    {
        SubscriberRecorder.Reset();
        _node = await EngineNode.StartAsync(_server.ConnectionString, "session-node",
            o => o.HeartbeatInterval = TimeSpan.FromMilliseconds(300));
    }

    public async Task DisposeAsync()
    {
        if (_node is not null) await _node.DisposeAsync();
        _server.Dispose();
    }

    [Fact]
    public async Task AWipedBroker_GetsTheRunningNodesSubscriptionsBack()
    {
        await _node!.Client.PublishAsync(new ItEvent { Data = "before" });
        (await SubscriberRecorder.WaitForAsync(() => SubscriberRecorder.CountEntries("A:before") >= 1))
            .Should().BeTrue("sanity: the subscription works before the wipe");

        _server.Restart();   // memory-only: the broker comes back empty on the same port

        // Nothing registered: until the client re-declares, a publish reaches no group.
        using var mux = await ConnectionMultiplexer.ConnectAsync(_server.ConnectionString);
        var db = mux.GetDatabase();
        var deadline = DateTime.UtcNow.AddSeconds(20);
        long groups = 0;
        while (DateTime.UtcNow < deadline)
        {
            groups = (long)(await db.ExecuteAsync("HW.PUBLISH", "it.events", "{\"Data\":\"probe\"}"u8.ToArray()));
            if (groups > 0) break;
            await Task.Delay(200);
        }
        groups.Should().BeGreaterThan(0, "the running node re-declared its session on the empty broker");

        await _node.Client.PublishAsync(new ItEvent { Data = "after" });
        (await SubscriberRecorder.WaitForAsync(() => SubscriberRecorder.CountEntries("A:after") >= 1))
            .Should().BeTrue("a publish after the wipe reaches the node's subscribers again");
    }
}
