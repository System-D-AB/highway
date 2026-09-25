using FluentAssertions;
using Highway.Server;
using StackExchange.Redis;
using Xunit;

namespace Highway.Integration.Tests;

/// <summary>
/// Feature 004.1 Task 12 — Requirement 7 AC1–AC4: reply-slot TTL,
/// last-writer-wins replies. Reply slots are read with <c>HW.RESULT</c> since protocol 5.0
/// (was stock <c>GET</c> on <c>hw:rep:*</c>). The backlog cases it once covered were removed with the
/// backlog itself (feature 014 follow-up).
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
        server.Inspect.ReplySlotExists("ttl-req").Should().BeTrue("the reply is waiting");

        Thread.Sleep(TimeSpan.FromMilliseconds(1200));

        server.Inspect.ReplySlotExists("ttl-req").Should().BeFalse(
            "an unretrieved reply slot must expire after its TTL");
        db.Result("ttl-req").HasValue.Should().BeFalse(
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

        ((string)db.Result("lww-req")!).Should().Be("second",
            "the documented rule is last-writer-wins (004 Req 4 AC4)");
    }

    /// <summary>
    /// The channel backlog is gone (feature 014 follow-up). A publish with no registered
    /// group is delivered to nobody, and a group registering later starts empty.
    ///
    /// <para>Highway used to hold such messages for a future subscriber, which produced a
    /// surprising rule — a late group received an arbitrary prefix of history determined by
    /// when the <i>first</i> subscriber happened to start. It existed because nothing else
    /// could hold a message until someone could handle it. <c>SendAsync</c> now can.</para>
    /// </summary>
    [Fact]
    public void PublishWithNoSubscribers_IsDeliveredToNobody()
    {
        using var server = new HighwayTestServer();
        using var redis = ConnectionMultiplexer.Connect(server.ConnectionString);
        var db = redis.GetDatabase();

        db.Execute("HW.PUBLISH", "retention.ch", "orphan"u8.ToArray());

        // Subscribing afterwards starts empty — the message was not kept for anyone.
        db.Subscribe("retention.ch", "late-group");

        db.ClaimQ("retention.ch@late-group", "node-1")
            .Should().BeNull("a publish with no registered group reaches nobody");
    }
}
