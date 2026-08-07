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
    public void LateSubscriber_ReceivesBacklog()
    {
        const string channel = "late.channel";
        const string group = "late-group";

        // Publish with no groups registered → goes to backlog
        _db.Execute("HW.PUBLISH", channel, "early-message");

        // Now subscribe (late)
        _db.Execute("HW.SUBSCRIBE", channel, group);

        // Receive should get the backlog message
        var result = (RedisResult[])_db.Execute("HW.RECEIVE", channel, group, "COUNT", "10")!;
        result.Should().HaveCount(1);

        var pair = (RedisResult[])result[0]!;
        ((string)pair[1]!).Should().Be("early-message");
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

    [Fact]
    public void Resubscribe_SameGroup_DoesNotDuplicateBacklog()
    {
        const string channel = "resub.channel";
        const string group = "resub-group";

        // Publish with no groups registered → backlog
        _db.Execute("HW.PUBLISH", channel, "msg-1");
        _db.Execute("HW.PUBLISH", channel, "msg-2");

        // First subscribe copies the backlog into the group queue
        _db.Execute("HW.SUBSCRIBE", channel, group);
        var first = (RedisResult[])_db.Execute("HW.RECEIVE", channel, group, "COUNT", "10")!;
        first.Should().HaveCount(2);

        // Feature 005's engine sends HW.SUBSCRIBE on every start with group = NodeName.
        // A second subscribe for the same group must be a no-op for the backlog.
        var resub = _db.Execute("HW.SUBSCRIBE", channel, group);
        resub.ToString().Should().Be("OK");

        var second = (RedisResult[])_db.Execute("HW.RECEIVE", channel, group, "COUNT", "10")!;
        second.Should().BeEmpty(
            "re-subscribing an already-registered group must not re-copy the backlog");
    }

    [Fact]
    public void Unsubscribe_ThenResubscribe_ReceivesBacklogAgain()
    {
        const string channel = "resub-after-unsub.channel";
        const string group = "resub-group-2";

        _db.Execute("HW.PUBLISH", channel, "old-message");   // → backlog (zero groups)
        _db.Execute("HW.SUBSCRIBE", channel, group);
        var first = (RedisResult[])_db.Execute("HW.RECEIVE", channel, group, "COUNT", "10")!;
        first.Should().HaveCount(1);

        // Unsubscribe deletes the group's queue; the backlog retains its entries
        _db.Execute("HW.UNSUBSCRIBE", channel, group);
        _db.Execute("HW.PUBLISH", channel, "new-message");   // zero groups again → backlog

        // Re-subscribe is treated as a NEW group: it receives the retained backlog
        _db.Execute("HW.SUBSCRIBE", channel, group);
        var second = (RedisResult[])_db.Execute("HW.RECEIVE", channel, group, "COUNT", "10")!;

        var payloads = second
            .Select(r => (string)((RedisResult[])r!)[1]!)
            .ToList();
        payloads.Should().BeEquivalentTo(["old-message", "new-message"],
            "a group that unsubscribed and re-subscribed is new and receives the backlog again");
    }

    [Fact]
    public void TwoGroups_EachReceiveBacklogOnce()
    {
        const string channel = "two-groups-backlog.channel";

        _db.Execute("HW.PUBLISH", channel, "backlog-msg");   // → backlog

        _db.Execute("HW.SUBSCRIBE", channel, "group-1");
        _db.Execute("HW.SUBSCRIBE", channel, "group-2");

        var r1 = (RedisResult[])_db.Execute("HW.RECEIVE", channel, "group-1", "COUNT", "10")!;
        var r2 = (RedisResult[])_db.Execute("HW.RECEIVE", channel, "group-2", "COUNT", "10")!;

        r1.Should().HaveCount(1, "each group receives the backlog exactly once");
        r2.Should().HaveCount(1, "each group receives the backlog exactly once");

        ((string)((RedisResult[])r1[0]!)[1]!).Should().Be("backlog-msg");
        ((string)((RedisResult[])r2[0]!)[1]!).Should().Be("backlog-msg");
    }
}
