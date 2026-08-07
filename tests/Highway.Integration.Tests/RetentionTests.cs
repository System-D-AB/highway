using FluentAssertions;
using Highway.Server;
using StackExchange.Redis;
using Xunit;

namespace Highway.Integration.Tests;

/// <summary>
/// Feature 004.1 Task 12 — Requirement 7 AC1–AC4: reply-slot TTL,
/// last-writer-wins replies, backlog retention window, and backlog entry cap.
/// Covers 004 Requirements 4 AC4/AC5 and 10 AC4 for the first time.
/// </summary>
public class RetentionTests
{
    [Fact]
    public void ReplySlot_ExpiresAfterTtl()
    {
        using var server = new HighwayTestServer(o => o.ReplySlotTtl = TimeSpan.FromMilliseconds(500));
        using var redis = ConnectionMultiplexer.Connect(server.ConnectionString);
        var db = redis.GetDatabase();

        db.Execute("HW.REPLY", "ttl-req", "transient-response");
        ((string)db.StringGet("hw:rep:ttl-req")!).Should().Be("transient-response");

        Thread.Sleep(TimeSpan.FromMilliseconds(1200));

        db.StringGet("hw:rep:ttl-req").HasValue.Should().BeFalse(
            "an unretrieved reply slot must be garbage-collected after its TTL (004 Req 4 AC5)");
    }

    [Fact]
    public void DoubleReply_LastWriterWins()
    {
        using var server = new HighwayTestServer();
        using var redis = ConnectionMultiplexer.Connect(server.ConnectionString);
        var db = redis.GetDatabase();

        db.Execute("HW.REPLY", "lww-req", "first");
        db.Execute("HW.REPLY", "lww-req", "second");

        ((string)db.StringGet("hw:rep:lww-req")!).Should().Be("second",
            "the documented rule is last-writer-wins (004 Req 4 AC4)");
    }

    [Fact]
    public void Backlog_ExpiredEntries_NotDeliveredToLateSubscriber()
    {
        using var server = new HighwayTestServer(o => o.BacklogRetention = TimeSpan.FromMilliseconds(300));
        using var redis = ConnectionMultiplexer.Connect(server.ConnectionString);
        var db = redis.GetDatabase();

        // Publish with zero groups → backlog
        db.Execute("HW.PUBLISH", "ret.ch", "old-message");

        Thread.Sleep(TimeSpan.FromMilliseconds(700));

        // Late subscriber arrives after the retention window
        db.Execute("HW.SUBSCRIBE", "ret.ch", "late-grp");
        var received = (RedisResult[])db.Execute("HW.RECEIVE", "ret.ch", "late-grp", "COUNT", "10")!;

        received.Should().BeEmpty(
            "backlog entries older than the retention window must not be delivered (004 Req 10 AC4)");
    }

    [Fact]
    public void Backlog_EntryCap_DropsOldest()
    {
        using var server = new HighwayTestServer(o => o.MaxBacklogEntries = 3);
        using var redis = ConnectionMultiplexer.Connect(server.ConnectionString);
        var db = redis.GetDatabase();

        for (var i = 1; i <= 5; i++)
            db.Execute("HW.PUBLISH", "cap.ch", $"m{i}");

        db.Execute("HW.SUBSCRIBE", "cap.ch", "cap-grp");
        var received = (RedisResult[])db.Execute("HW.RECEIVE", "cap.ch", "cap-grp", "COUNT", "10")!;

        var payloads = received.Select(r => (string)((RedisResult[])r!)[1]!).ToList();
        payloads.Should().Equal(["m3", "m4", "m5"],
            "the backlog is capped at MaxBacklogEntries, dropping oldest first (004 Req 10 AC4)");
    }
}
