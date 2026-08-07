using FluentAssertions;
using Highway.Client.Engine;
using Highway.Client.Wire;
using Highway.Server;
using StackExchange.Redis;
using Xunit;

namespace Highway.Integration.Tests;

/// <summary>
/// Feature 005 Task 13 — Pub/Sub end-to-end through real engines. Every node
/// hosts the it.* channels (assembly-wide scanning), so each published message
/// fans out to every engine's group and every local subscriber records it.
/// </summary>
[Collection(SubscriberRecorderCollection.Name)]
public class PubSubIntegrationTests : IDisposable
{
    private readonly HighwayTestServer _server = new();
    private readonly List<EngineNode> _nodes = [];

    public PubSubIntegrationTests()
    {
        SubscriberRecorder.Reset();
    }

    public void Dispose()
    {
        foreach (var node in _nodes)
            node.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _server.Dispose();
    }

    private async Task<EngineNode> StartNodeAsync(string name, Action<Highway.Client.HighwayOptions>? tune = null)
    {
        var node = await EngineNode.StartAsync(_server.ConnectionString, name, tune);
        _nodes.Add(node);
        return node;
    }

    [Fact]
    public async Task Publish_DeliveredToEveryNode_AndEveryLocalSubscriber()
    {
        var nodeA = await StartNodeAsync("pub-node-a");
        await StartNodeAsync("pub-node-b");

        await nodeA.Client.PublishAsync(new ItEvent { Data = "fanout-1" });

        // Two nodes x two local subscribers = one record per subscriber per node.
        var delivered = await SubscriberRecorder.WaitForAsync(() =>
            SubscriberRecorder.CountEntries("A:fanout-1") >= 2 &&
            SubscriberRecorder.CountEntries("B:fanout-1") >= 2);

        delivered.Should().BeTrue(
            "each of the two nodes runs both local subscribers on its own copy of the message");
        SubscriberRecorder.CountEntries("A:fanout-1").Should().Be(2);
        SubscriberRecorder.CountEntries("B:fanout-1").Should().Be(2);
    }

    /// <summary>
    /// <b>Behaviour change (feature 014 follow-up).</b> This test previously asserted that a
    /// message published before any subscriber existed was delivered when one appeared —
    /// the channel backlog. That backlog has been removed: a publish with no registered
    /// group is delivered to nobody, which is what "publish" means.
    ///
    /// <para>The capability has not been lost, it has moved. "Hold this until someone can
    /// handle it" is <c>SendAsync</c> and a queue, which is durable by design and has no
    /// surprising dependence on when the first subscriber happened to start.</para>
    /// </summary>
    [Fact]
    public async Task Publish_BeforeAnySubscriberExists_ReachesNobody()
    {
        var db = (await ConnectionMultiplexer.ConnectAsync(_server.ConnectionString)).GetDatabase();

        var groups = (long)(await db.ExecuteAsync("HW.PUBLISH", "orphan.ch", "nobody"u8.ToArray()));
        groups.Should().Be(0, "no group is registered");

        await db.ExecuteAsync("HW.SUBSCRIBE", "orphan.ch", "arrives-later");

        var received = await db.ExecuteAsync("HW.RECEIVE", "orphan.ch", "arrives-later");
        (received.IsNull || ((RedisResult[])received!).Length == 0)
            .Should().BeTrue("a new group starts empty — use SendAsync when the work must wait");
    }

    /// <summary>
    /// Task 13 restart-resume: the client never sends HW.UNSUBSCRIBE, so a node's
    /// group outlives its process. Messages published while the node is down sit
    /// in that group's queue and drain when it comes back under the same NodeName.
    /// This also guards the 004.1 Requirement 1 fix from the client side — the
    /// engine re-sends HW.SUBSCRIBE on every start, and that must not redeliver
    /// anything the node already consumed.
    /// </summary>
    [Fact]
    public async Task Subscriber_StopsAndRestartsWithSameNodeName_DrainsMessagesPublishedWhileDown()
    {
        const string nodeName = "resume-subscriber";

        // A publisher that stays up for the whole test.
        var publisher = await StartNodeAsync("resume-publisher");

        // The subscriber node starts (registering its group), receives one
        // message, then shuts down gracefully.
        var subscriber = await EngineNode.StartAsync(_server.ConnectionString, nodeName);
        await publisher.Client.PublishAsync(new ItEvent { Data = "before-restart" });
        (await SubscriberRecorder.WaitForAsync(() =>
            SubscriberRecorder.CountEntries("A:before-restart") >= 2)).Should().BeTrue();

        await subscriber.DisposeAsync();

        // Published while the subscriber node is down: its group still exists
        // server-side, so the message accumulates rather than being dropped.
        await publisher.Client.PublishAsync(new ItEvent { Data = "while-down" });

        // Same NodeName → same group → the pending message drains on restart.
        var restarted = await StartNodeAsync(nodeName);
        restarted.Engine.State.Should().Be(EngineState.Running);

        var drained = await SubscriberRecorder.WaitForAsync(() =>
            SubscriberRecorder.CountEntries("A:while-down") >= 2);

        drained.Should().BeTrue(
            "a group is never unsubscribed, so messages published while the node was down survive its restart");

        // The re-subscribe must not replay what was already consumed (004.1 Req 1).
        SubscriberRecorder.CountEntries("A:before-restart").Should().Be(2,
            "re-subscribing an existing group must not redeliver already-consumed messages");
    }

    [Fact]
    public async Task SubscriberFailure_DoesNotBlockSiblings()
    {
        var node = await StartNodeAsync("failover-node");

        await node.Client.PublishAsync(new ItFailEvent { Data = "boom-test" });

        var survivorDelivered = await SubscriberRecorder.WaitForAsync(() =>
            SubscriberRecorder.CountEntries("S:boom-test") >= 1);

        survivorDelivered.Should().BeTrue(
            "the failing subscriber must not abort its sibling or block the channel");
    }
}
