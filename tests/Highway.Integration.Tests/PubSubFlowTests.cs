using FluentAssertions;
using Highway.Server;
using StackExchange.Redis;
using Xunit;

namespace Highway.Integration.Tests;

/// <summary>
/// Integration tests for the Pub/Sub command flow:
/// HW.SUBSCRIBE → HW.PUBLISH → HW.QCLAIM → HW.QACK (on derived queue {channel}@{group})
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
    public void HwSubscribe_ReturnsOk()
    {
        var result = _db.Execute("HW.SUBSCRIBE", "orders.created", "audit-group");
        result.ToString().Should().Be("OK");
    }

    [Fact]
    public void HwPublish_WithSubscribers_ReturnsGroupCount()
    {
        _db.Execute("HW.SUBSCRIBE", "events.topic", "group-a");
        _db.Execute("HW.SUBSCRIBE", "events.topic", "group-b");

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
    public void HwQClaim_ReturnsPublishedMessages()
    {
        const string channel = "news.feed";
        const string group = "reader-group";
        var derivedQueue = $"{channel}@{group}";

        _db.Execute("HW.SUBSCRIBE", channel, group);

        _db.Execute("HW.PUBLISH", channel, "message-1");
        _db.Execute("HW.PUBLISH", channel, "message-2");
        _db.Execute("HW.PUBLISH", channel, "message-3");

        // Claim messages one at a time via the derived queue
        var messages = new List<(string id, string payload)>();
        for (var i = 0; i < 3; i++)
        {
            var result = _db.Execute("HW.QCLAIM", derivedQueue, "node-1");
            result.IsNull.Should().BeFalse();
            var arr = (RedisResult[])result!;
            messages.Add(((string)arr[0]!, (string)arr[1]!));
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
    public void HwQAck_RemovesFromProcessing()
    {
        const string channel = "ack.test";
        const string group = "ack-group";
        var derivedQueue = $"{channel}@{group}";

        _db.Execute("HW.SUBSCRIBE", channel, group);
        _db.Execute("HW.PUBLISH", channel, "payload-x");

        // Claim the message (moves to processing)
        var claimed = _db.Execute("HW.QCLAIM", derivedQueue, "node-1");
        claimed.IsNull.Should().BeFalse();
        var msgId = (string)((RedisResult[])claimed!)[0]!;

        // QACK it
        _db.Execute("HW.QACK", derivedQueue, "node-1", msgId);

        // Second claim should return nil (nothing pending)
        var secondClaim = _db.Execute("HW.QCLAIM", derivedQueue, "node-1");
        secondClaim.IsNull.Should().BeTrue();
    }


    [Fact]
    public void MultiGroupFanOut_IndependentDelivery()
    {
        const string channel = "fanout.test";

        _db.Execute("HW.SUBSCRIBE", channel, "group-x");
        _db.Execute("HW.SUBSCRIBE", channel, "group-y");

        _db.Execute("HW.PUBLISH", channel, "shared-message");

        // Each group gets the message independently via derived queues
        var resultX = _db.Execute("HW.QCLAIM", $"{channel}@group-x", "node-1");
        var resultY = _db.Execute("HW.QCLAIM", $"{channel}@group-y", "node-1");

        resultX.IsNull.Should().BeFalse();
        resultY.IsNull.Should().BeFalse();

        var payloadX = (string)((RedisResult[])resultX!)[1]!;
        var payloadY = (string)((RedisResult[])resultY!)[1]!;
        payloadX.Should().Be("shared-message");
        payloadY.Should().Be("shared-message");

        // QACK from group-x doesn't affect group-y
        var msgIdX = (string)((RedisResult[])resultX!)[0]!;
        _db.Execute("HW.QACK", $"{channel}@group-x", "node-1", msgIdX);

        // group-y's message is still in its processing list (already claimed)
        var resultYAgain = _db.Execute("HW.QCLAIM", $"{channel}@group-y", "node-1");
        resultYAgain.IsNull.Should().BeTrue(); // no new messages; original is in processing
    }

    [Fact]
    public void HwUnsubscribe_RemovesGroupState()
    {
        const string channel = "unsub.channel";
        const string group = "temp-group";
        var derivedQueue = $"{channel}@{group}";

        // Subscribe and publish
        _db.Execute("HW.SUBSCRIBE", channel, group);
        _db.Execute("HW.PUBLISH", channel, "message-before-unsub");

        // Unsubscribe — clears group state
        var unsubResult = _db.Execute("HW.UNSUBSCRIBE", channel, group);
        unsubResult.ToString().Should().Be("OK");

        // Re-subscribe — should NOT see the old message (state was cleared)
        _db.Execute("HW.SUBSCRIBE", channel, group);

        var result = _db.Execute("HW.QCLAIM", derivedQueue, "node-1");
        result.IsNull.Should().BeTrue();
    }

    // -------------------------------------------------------------------------
    // Feature 004.1 — Requirement 1: Idempotent re-subscribe
    // -------------------------------------------------------------------------



}
