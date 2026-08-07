using FluentAssertions;
using Highway.Server;
using StackExchange.Redis;
using Xunit;

namespace Highway.Integration.Tests;

/// <summary>
/// Integration tests for the Pub/Sub command flow:
/// HW.SUBSCRIBE → HW.PUBLISH → HW.RECEIVE → HW.RACK
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
    public void HwReceive_ReturnsPublishedMessages()
    {
        const string channel = "news.feed";
        const string group = "reader-group";

        _db.Execute("HW.SUBSCRIBE", channel, group);

        _db.Execute("HW.PUBLISH", channel, "message-1");
        _db.Execute("HW.PUBLISH", channel, "message-2");
        _db.Execute("HW.PUBLISH", channel, "message-3");

        var result = (RedisResult[])_db.Execute("HW.RECEIVE", channel, group, "COUNT", "10")!;

        result.Should().HaveCount(3);

        var messages = result.Select(r =>
        {
            var pair = (RedisResult[])r!;
            return (id: (string)pair[0]!, payload: (string)pair[1]!);
        }).ToList();

        messages[0].payload.Should().Be("message-1");
        messages[1].payload.Should().Be("message-2");
        messages[2].payload.Should().Be("message-3");

        // Message IDs should be unique and monotonically increasing
        var ids = messages.Select(m => long.Parse(m.id)).ToList();
        ids.Should().BeInAscendingOrder();
        ids.Distinct().Should().HaveCount(3);
    }

    [Fact]
    public void HwRack_RemovesFromProcessing()
    {
        const string channel = "ack.test";
        const string group = "ack-group";

        _db.Execute("HW.SUBSCRIBE", channel, group);
        _db.Execute("HW.PUBLISH", channel, "payload-x");

        // Receive the message (moves to processing)
        var received = (RedisResult[])_db.Execute("HW.RECEIVE", channel, group, "COUNT", "10")!;
        received.Should().HaveCount(1);
        var msgId = (string)((RedisResult[])received[0]!)[0]!;

        // RACK it
        var rackResult = _db.Execute("HW.RACK", channel, group, msgId);
        rackResult.ToString().Should().Be("OK");

        // Second receive should return empty (nothing pending)
        var secondReceive = (RedisResult[])_db.Execute("HW.RECEIVE", channel, group, "COUNT", "10")!;
        secondReceive.Should().BeEmpty();
    }


    [Fact]
    public void MultiGroupFanOut_IndependentDelivery()
    {
        const string channel = "fanout.test";

        _db.Execute("HW.SUBSCRIBE", channel, "group-x");
        _db.Execute("HW.SUBSCRIBE", channel, "group-y");

        _db.Execute("HW.PUBLISH", channel, "shared-message");

        // Each group gets the message independently
        var resultX = (RedisResult[])_db.Execute("HW.RECEIVE", channel, "group-x", "COUNT", "10")!;
        var resultY = (RedisResult[])_db.Execute("HW.RECEIVE", channel, "group-y", "COUNT", "10")!;

        resultX.Should().HaveCount(1);
        resultY.Should().HaveCount(1);

        var payloadX = (string)((RedisResult[])resultX[0]!)[1]!;
        var payloadY = (string)((RedisResult[])resultY[0]!)[1]!;
        payloadX.Should().Be("shared-message");
        payloadY.Should().Be("shared-message");

        // RACK from group-x doesn't affect group-y
        var msgIdX = (string)((RedisResult[])resultX[0]!)[0]!;
        _db.Execute("HW.RACK", channel, "group-x", msgIdX);

        // group-y still has nothing new to receive (already received), but processing list has the entry
        var resultYAgain = (RedisResult[])_db.Execute("HW.RECEIVE", channel, "group-y", "COUNT", "10")!;
        resultYAgain.Should().BeEmpty(); // no new messages, original is in processing
    }

    [Fact]
    public void HwUnsubscribe_RemovesGroupState()
    {
        const string channel = "unsub.channel";
        const string group = "temp-group";

        // Subscribe and publish
        _db.Execute("HW.SUBSCRIBE", channel, group);
        _db.Execute("HW.PUBLISH", channel, "message-before-unsub");

        // Unsubscribe — clears group state
        var unsubResult = _db.Execute("HW.UNSUBSCRIBE", channel, group);
        unsubResult.ToString().Should().Be("OK");

        // Re-subscribe — should NOT see the old message (state was cleared)
        _db.Execute("HW.SUBSCRIBE", channel, group);

        var result = (RedisResult[])_db.Execute("HW.RECEIVE", channel, group, "COUNT", "10")!;
        result.Should().BeEmpty();
    }

    // -------------------------------------------------------------------------
    // Feature 004.1 — Requirement 1: Idempotent re-subscribe
    // -------------------------------------------------------------------------



}
