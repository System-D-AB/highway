using FluentAssertions;
using Highway.Server;
using StackExchange.Redis;
using Xunit;

namespace Highway.Integration.Tests;

/// <summary>
/// Integration tests for the Pub/Sub command flow (protocol 5.0):
/// HW.SESSION (subscriptions) → HW.PUBLISH → HW.CLAIM Q → HW.ACK token (on derived queue {channel}@{group})
/// </summary>
public class PubSubFlowTests : IDisposable
{
    private readonly HighwayTestServer _server = new();
    private readonly ConnectionMultiplexer _redis;
    private readonly IDatabase _db;

    public PubSubFlowTests()
    {
        _redis = ConnectionMultiplexer.Connect(_server.ConnectionString);
        _db = _redis.GetDatabase();
    }

    public void Dispose()
    {
        _redis.Dispose();
        _server.Dispose();
    }

    [Fact]
    public void HwSession_WithSubscription_ReturnsOk()
    {
        var result = _db.Subscribe("orders.created", "audit-group");
        result.ToString().Should().Be("OK");
    }

    [Fact]
    public void HwPublish_WithSubscribers_ReturnsGroupCount()
    {
        _db.Subscribe("events.topic", "group-a");
        _db.Subscribe("events.topic", "group-b");

        var result = (int)_db.Execute("HW.PUBLISH", "events.topic", "{\"event\":\"test\"}");

        result.Should().Be(2);
    }

    [Fact]
    public void HwPublish_NoSubscribers_ReturnsZero()
    {
        var result = (int)_db.Execute("HW.PUBLISH", "lonely.channel", "{\"msg\":\"hello\"}");
        result.Should().Be(0);
    }

    [Fact]
    public void HwClaim_ReturnsPublishedMessages()
    {
        const string channel = "news.feed";
        const string group = "reader-group";
        var derivedQueue = $"{channel}@{group}";

        _db.Subscribe(channel, group);

        _db.Execute("HW.PUBLISH", channel, "message-1");
        _db.Execute("HW.PUBLISH", channel, "message-2");
        _db.Execute("HW.PUBLISH", channel, "message-3");

        // Claim messages one at a time via the derived queue
        var messages = new List<(string id, string payload)>();
        for (var i = 0; i < 3; i++)
        {
            var claim = _db.ClaimQ(derivedQueue, "node-1");
            claim.Should().NotBeNull();
            messages.Add((claim!.Value.Id, claim.Value.PayloadText));
        }

        messages.Should().HaveCount(3);
        messages[0].payload.Should().Be("message-1");
        messages[1].payload.Should().Be("message-2");
        messages[2].payload.Should().Be("message-3");

        // Message IDs should be unique and monotonically increasing
        var ids = messages.Select(m => long.Parse(m.id)).ToList();
        ids.Should().BeInAscendingOrder();
        ids.Distinct().Should().HaveCount(3);
    }

    [Fact]
    public void HwClaim_NothingDue_ReturnsEmptyArray()
    {
        _db.Subscribe("empty.ch", "g");

        var reply = _db.Execute("HW.CLAIM", "Q", "empty.ch@g", "node-1");

        reply.IsNull.Should().BeFalse("5.0 answers an empty array, not nil, when nothing is due");
        ((RedisResult[])reply!).Should().BeEmpty();
    }

    [Fact]
    public void HwAck_RemovesFromProcessing()
    {
        const string channel = "ack.test";
        const string group = "ack-group";
        var derivedQueue = $"{channel}@{group}";

        _db.Subscribe(channel, group);
        _db.Execute("HW.PUBLISH", channel, "payload-x");

        // Claim the message (moves to processing)
        var claimed = _db.ClaimQ(derivedQueue, "node-1");
        claimed.Should().NotBeNull();

        // ACK it by its token
        _db.Ack(claimed!.Value.Token).Should().Be(1);

        // Second claim finds nothing pending, and the token is no longer held
        _db.ClaimQ(derivedQueue, "node-1").Should().BeNull();
        _db.Ack(claimed.Value.Token).Should().Be(0);
    }


    [Fact]
    public void MultiGroupFanOut_IndependentDelivery()
    {
        const string channel = "fanout.test";

        _db.Subscribe(channel, "group-x");
        _db.Subscribe(channel, "group-y");

        _db.Execute("HW.PUBLISH", channel, "shared-message");

        // Each group gets the message independently via derived queues
        var resultX = _db.ClaimQ($"{channel}@group-x", "node-1");
        var resultY = _db.ClaimQ($"{channel}@group-y", "node-1");

        resultX.Should().NotBeNull();
        resultY.Should().NotBeNull();

        resultX!.Value.PayloadText.Should().Be("shared-message");
        resultY!.Value.PayloadText.Should().Be("shared-message");
        resultX.Value.Token.Should().NotBe(resultY.Value.Token, "each group's claim is its own lease");

        // ACK from group-x doesn't affect group-y
        _db.Ack(resultX.Value.Token).Should().Be(1);

        // group-y's message is still in its processing list (already claimed)
        _db.ClaimQ($"{channel}@group-y", "node-1").Should().BeNull(); // no new messages; original is in processing
        _db.Touch(resultY.Value.Token).Should().Be(1, "group-y's claim is still held");
    }

    [Fact]
    public void HwUnsubscribe_RemovesGroupState()
    {
        const string channel = "unsub.channel";
        const string group = "temp-group";
        var derivedQueue = $"{channel}@{group}";

        // Subscribe and publish
        _db.Subscribe(channel, group);
        _db.Execute("HW.PUBLISH", channel, "message-before-unsub");

        // Unsubscribe — clears group state
        var unsubResult = _db.Execute("HW.UNSUBSCRIBE", channel, group);
        unsubResult.ToString().Should().Be("OK");

        // Re-subscribe — should NOT see the old message (state was cleared)
        _db.Subscribe(channel, group);

        _db.ClaimQ(derivedQueue, "node-1").Should().BeNull();
    }
}
