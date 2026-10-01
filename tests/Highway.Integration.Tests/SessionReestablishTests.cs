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

/// <summary>
/// Feature 070 end to end: a node redeployed <b>without</b> a subscriber stops backing that group. Its
/// group drains — publishes skip it (so a full, abandoned group can never refuse the channel) and its
/// backlog is kept — and when the subscriber comes back, the backlog is delivered.
/// </summary>
[Collection(SubscriberRecorderCollection.Name)]
public class RedeployWithoutSubscriberTests : IAsyncLifetime
{
    private const string Node = "redeploy-node";
    private readonly HighwayTestServer _server = new();
    private EngineNode? _publisher;

    public async Task InitializeAsync()
    {
        SubscriberRecorder.Reset();
        // Publishes through a real client, hosting nothing itself.
        _publisher = await EngineNode.StartAsync(_server.ConnectionString, "redeploy-publisher",
            o => o.HostingMode = Highway.Client.HostingMode.ExplicitOnly);
    }

    public async Task DisposeAsync()
    {
        if (_publisher is not null) await _publisher.DisposeAsync();
        _server.Dispose();
    }

    private async Task<Dictionary<string, string>> StatsAsync(string name)
    {
        using var mux = await ConnectionMultiplexer.ConnectAsync(_server.ConnectionString);
        var flat = (RedisResult[])(await mux.GetDatabase().ExecuteAsync("HW.STATS", name))!;
        var fields = new Dictionary<string, string>();
        for (var i = 0; i + 1 < flat.Length; i += 2) fields[(string)flat[i]!] = (string)flat[i + 1]!;
        return fields;
    }

    [Fact]
    public async Task ARedeployWithoutTheSubscriber_DrainsTheGroup_AndTheBacklogReturnsWithIt()
    {
        var group = $"it.events@{Node}";

        // v1 of the node hosts the it.events subscribers.
        await using (var v1 = await EngineNode.StartAsync(_server.ConnectionString, Node))
        {
            await _publisher!.Client.PublishAsync(new ItEvent { Data = "live" });
            (await SubscriberRecorder.WaitForAsync(() => SubscriberRecorder.CountEntries("A:live") >= 1)).Should().BeTrue();
        }

        // Down between deploys: the group still has its member, so this message waits in its queue.
        await _publisher!.Client.PublishAsync(new ItEvent { Data = "while-down" });

        // v2 of the node no longer hosts the subscriber: its session declares no subscriptions.
        await using (var v2 = await EngineNode.StartAsync(_server.ConnectionString, Node,
                         o => o.HostingMode = Highway.Client.HostingMode.ExplicitOnly))
        {
            (await StatsAsync("Q:" + group))["draining"].Should().NotBe("0", "its last member stopped declaring it");
            (await StatsAsync("Q:" + group))["depth"].Should().Be("1", "the backlog is kept while it drains");

            await _publisher.Client.PublishAsync(new ItEvent { Data = "skipped" });   // must not be refused
            (await StatsAsync("Q:" + group))["depth"].Should().Be("1", "a draining group receives nothing new");
        }

        // v3 hosts the subscriber again: the group resumes and its backlog is delivered.
        await using var v3 = await EngineNode.StartAsync(_server.ConnectionString, Node);
        (await SubscriberRecorder.WaitForAsync(() => SubscriberRecorder.CountEntries("A:while-down") >= 1))
            .Should().BeTrue("re-declaring the group before the threshold keeps its backlog");
        (await StatsAsync("Q:" + group))["draining"].Should().Be("0");
        SubscriberRecorder.CountEntries("A:skipped").Should().Be(0, "published while the group was draining");
    }
}

