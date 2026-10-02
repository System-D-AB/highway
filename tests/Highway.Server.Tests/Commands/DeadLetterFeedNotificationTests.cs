using System.Text;
using FluentAssertions;
using Highway.Abstractions.Observability;
using Highway.Server;
using Highway.Server.Commands.Ported;
using Highway.Server.Internal;
using Highway.Server.Storage;
using Highway.Server.Storage.Boxes;
using Highway.Server.Storage.Layout;
using Xunit;

namespace Highway.Server.Tests.Commands;

/// <summary>
/// Feature 073 (T5): Dead-lettering writes the notification to the dead-letter feed.
/// - When a feed is declared (CFeedOn > 0), sweep writes dead letter + notification in the same batch/commit.
/// - Without a declared feed, dead-lettering writes no notification (zero overhead).
/// - Feed bounding (MaxDeadLetterEntries) drops oldest notification and records DeadLetterFeedOverflow.
/// - Doorbell for the feed is rung on commit.
/// - Feeds never feed.
/// </summary>
public class DeadLetterFeedNotificationTests
{
    private static readonly long T0 = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc).Ticks;

    private static HighwayServerOptions CreateOptions(int maxAttempts = 1, int maxDeadEntries = 100, TimeSpan? lease = null)
        => new()
        {
            MaxDeliveryAttempts = maxAttempts,
            MaxDeadLetterEntries = maxDeadEntries,
            Lease = lease ?? TimeSpan.FromSeconds(5)
        };

    private static void Session(CommandHarness h, long now, string node, params (string Kind, string Name)[] feeds)
    {
        var json = "{\"deadLetters\":[" + string.Join(",", feeds.Select(f => $"{{\"kind\":\"{f.Kind}\",\"name\":\"{f.Name}\"}}")) + "]}";
        CommandHarness.AsText(h.Run(new HwSessionCommand(), now, node, json)).Should().Be("+OK\r\n");
    }

    [Fact]
    public void DeadLetterWithDeclaredFeed_WritesNotification_InOneCommit()
    {
        var options = CreateOptions(maxAttempts: 1);
        using var h = new CommandHarness(options: options);

        // 1. Declare feed for Q:payments
        Session(h, T0, "node-1", ("Q", "payments"));

        // 2. Enqueue message to payments (attempts = 1, so after this delivery it exceeds MaxDeliveryAttempts=1)
        var targetBox = Catalog.Ensure(h.Store, h.Boxes, h.Locks, BoxKeys.Kind.Queue, "payments", T0);
        var entry = Envelope.EncodeRpcEntry(Encoding.UTF8.GetBytes("pay-100"), Encoding.UTF8.GetBytes("charge-100"), 1);
        using (var wb = new WorkBox(h.Store, h.Boxes, targetBox, tracksBytes: true))
        {
            wb.Enqueue(entry);
            wb.Commit();
        }

        // 3. Claim message at T0 (attempt 2)
        var claim1 = CommandHarness.AsText(h.Run(new HwClaimCommand(), T0, "Q", "payments", "node-1"));
        claim1.Should().Contain("pay-100");

        // 4. Advance time past lease (lease is 5s)
        var t1 = T0 + TimeSpan.FromSeconds(10).Ticks;

        // Clear doorbells recorded so far
        h.Doorbell.Rings.Clear();

        // 5. Claim again at t1 -> SweepLeases runs: attempt 2 > MaxDeliveryAttempts(1) -> dead letters + feed notification
        h.Run(new HwClaimCommand(), t1, "Q", "payments", "node-2");

        // Verify target box has dead letter
        using (var wb = new WorkBox(h.Store, h.Boxes, targetBox, tracksBytes: true))
        {
            wb.Dead.Should().Be(1);
            var dead = wb.PeekDead(1);
            dead.Should().HaveCount(1);
        }

        // Verify feed box exists and has 1 ready notification
        var feedBox = Catalog.Find(h.Store, h.Store.NewBatch(), h.Boxes, BoxKeys.Kind.DeadFeed, "Q:payments");
        feedBox.Should().NotBeNull();

        using (var wbFeed = new WorkBox(h.Store, h.Boxes, feedBox!.Value, tracksBytes: false))
        {
            wbFeed.Ready.Should().Be(1);
            // Read ready notification from feed box
            var readyScan = h.Store.Scan(wbFeed.Batch, BoxKeys.Space(feedBox.Value, BoxKeys.Ready), BoxKeys.Seq(feedBox.Value, BoxKeys.Ready, 0), 1);
            readyScan.Should().HaveCount(1);

            Envelope.DecodeRpcEntry(readyScan[0].Value, out var notifId, out var notifPayload, out var attempts);
            var deadIdString = Encoding.UTF8.GetString(notifId);
            deadIdString.Should().HaveLength(24);
            attempts.Should().Be(0);

            // Payload is the DeadLetter record
            DeadLetter.Decode(notifPayload.ToArray(), out var deadAt, out var totalAttempts, out var reason, out var originalEntry);
            Encoding.UTF8.GetString(reason).Should().Be(DeadLetter.MaxAttempts);
            totalAttempts.Should().Be(2);

            Envelope.DecodeRpcEntry(originalEntry, out var originalId, out var originalPayload, out _);
            Encoding.UTF8.GetString(originalId).Should().Be("pay-100");
            Encoding.UTF8.GetString(originalPayload).Should().Be("charge-100");
        }

        // Verify doorbell was rung for the feed
        h.Doorbell.Rings.Should().Contain(e => e.Channel == HighwayKeys.DeadLetterDoorbell("Q:payments"));
    }

    [Fact]
    public void NoFeedDeclared_DeadLetterWritesNoNotification()
    {
        var options = CreateOptions(maxAttempts: 1);
        using var h = new CommandHarness(options: options);

        // Enqueue to Q:unfed without declaring any feed (attempts = 1)
        var targetBox = Catalog.Ensure(h.Store, h.Boxes, h.Locks, BoxKeys.Kind.Queue, "unfed", T0);
        var entry = Envelope.EncodeRpcEntry(Encoding.UTF8.GetBytes("unfed-1"), Encoding.UTF8.GetBytes("payload"), 1);
        using (var wb = new WorkBox(h.Store, h.Boxes, targetBox, tracksBytes: true))
        {
            wb.Enqueue(entry);
            wb.Commit();
        }

        // Claim at T0
        h.Run(new HwClaimCommand(), T0, "Q", "unfed", "node-1");

        // Expire lease and sweep
        var t1 = T0 + TimeSpan.FromSeconds(10).Ticks;
        h.Doorbell.Rings.Clear();
        h.Run(new HwClaimCommand(), t1, "Q", "unfed", "node-2");

        // Target box has 1 dead letter
        using (var wb = new WorkBox(h.Store, h.Boxes, targetBox, tracksBytes: true))
        {
            wb.Dead.Should().Be(1);
        }

        // Feed box does NOT exist in catalog
        var feedBox = Catalog.Find(h.Store, h.Store.NewBatch(), h.Boxes, BoxKeys.Kind.DeadFeed, "Q:unfed");
        feedBox.Should().BeNull();

        // No feed doorbell rung
        h.Doorbell.Rings.Should().NotContain(e => e.Channel == HighwayKeys.DeadLetterDoorbell("Q:unfed"));
    }

    [Fact]
    public void FeedNotificationOverflow_DropsOldest_RecordsOverflow()
    {
        var options = CreateOptions(maxAttempts: 1, maxDeadEntries: 2);
        using var h = new CommandHarness(options: options);

        Session(h, T0, "node-1", ("Q", "bounded"));
        var targetBox = Catalog.Ensure(h.Store, h.Boxes, h.Locks, BoxKeys.Kind.Queue, "bounded", T0);

        // Enqueue 3 messages (attempts = 1)
        for (var i = 1; i <= 3; i++)
        {
            var entry = Envelope.EncodeRpcEntry(Encoding.UTF8.GetBytes($"msg-{i}"), Encoding.UTF8.GetBytes($"p-{i}"), 1);
            using var wb = new WorkBox(h.Store, h.Boxes, targetBox, tracksBytes: true);
            wb.Enqueue(entry);
            wb.Commit();
        }

        // Claim all 3 at T0
        h.Run(new HwClaimCommand(), T0, "Q", "bounded", "node-1", "COUNT", "3");

        // Advance time past lease and claim to trigger lease sweep for all 3
        var t1 = T0 + TimeSpan.FromSeconds(10).Ticks;
        h.Run(new HwClaimCommand(), t1, "Q", "bounded", "node-2");

        // Target box has at most MaxDeadLetterEntries (2) dead letters
        using (var wb = new WorkBox(h.Store, h.Boxes, targetBox, tracksBytes: true))
        {
            wb.Dead.Should().Be(2);
        }

        // Feed box has at most MaxDeadLetterEntries (2) notifications
        var feedBox = Catalog.Find(h.Store, h.Store.NewBatch(), h.Boxes, BoxKeys.Kind.DeadFeed, "Q:bounded");
        feedBox.Should().NotBeNull();

        using (var wbFeed = new WorkBox(h.Store, h.Boxes, feedBox!.Value, tracksBytes: false))
        {
            wbFeed.Ready.Should().Be(2);
        }

        // Verify DeadLetterFeedOverflow was recorded
        var events = h.Recorder.Read("Q:bounded", DateTimeOffset.MinValue, DateTimeOffset.MaxValue, null, 100);
        events.Should().Contain(e => e.EventType == HighwayEventType.DeadLetterFeedOverflow);
    }

    [Fact]
    public void FeedsNeverFeed_DeadFeedSweepDoesNotFeedSubFeed()
    {
        var options = CreateOptions(maxAttempts: 1);
        using var h = new CommandHarness(options: options);

        // Create a DeadFeed box directly
        var feedBox = Catalog.Ensure(h.Store, h.Boxes, h.Locks, BoxKeys.Kind.DeadFeed, "Q:feedtarget", T0);
        var entry = Envelope.EncodeRpcEntry(Encoding.UTF8.GetBytes("deadid-1"), Encoding.UTF8.GetBytes("record-1"), 1);

        using (var wb = new WorkBox(h.Store, h.Boxes, feedBox, tracksBytes: false))
        {
            wb.Enqueue(entry);
            wb.Commit();
        }

        // Claim from the DeadFeed box (simulate claim on feed)
        h.Run(new HwClaimCommand(), T0, "Q", "feedtarget", "node-1"); // Wait, this claims Q:feedtarget, not the deadfeed box directly
        // Let's sweep the feed box directly
        using (var wb = new WorkBox(h.Store, h.Boxes, feedBox, tracksBytes: false))
        {
            wb.Claim("node-1", T0, 1, "feed:Q:feedtarget");
            wb.Commit();
        }

        var t1 = T0 + TimeSpan.FromSeconds(10).Ticks;
        using (var wb = new WorkBox(h.Store, h.Boxes, feedBox, tracksBytes: false))
        {
            var dead = wb.SweepLeases(t1 - TimeSpan.FromSeconds(5).Ticks, t1, maxAttempts: 1, maxDeadEntries: 100);
            dead.Should().HaveCount(1);
            wb.Dead.Should().Be(1);
            wb.Commit();
        }

        // Ensure no nested sub-feed was created
        var subFeed = Catalog.Find(h.Store, h.Store.NewBatch(), h.Boxes, BoxKeys.Kind.DeadFeed, "feed:Q:feedtarget");
        subFeed.Should().BeNull();
    }
}
