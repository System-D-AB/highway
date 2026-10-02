using System.Text;
using FluentAssertions;
using Highway.Abstractions.Observability;
using Highway.Server;
using Highway.Server.Commands.Ported;
using Highway.Server.Commands.Runtime;
using Highway.Server.Internal;
using Highway.Server.Storage;
using Highway.Server.Storage.Boxes;
using Highway.Server.Storage.Layout;
using Xunit;

namespace Highway.Server.Tests.Commands;

/// <summary>
/// Feature 073 (T6): Feed consumption and resolution: HW.CLAIM DEAD and HW.DLQ RESOLVE.
/// - HW.CLAIM DEAD SVC|Q &lt;name&gt; &lt;node&gt; [COUNT n] claims notification entries from the feed.
/// - HW.DLQ RESOLVE &lt;token&gt; KEEP|DISCARD|REQUEUE [AFTER &lt;ms&gt;] commits atomically across feed and target.
/// - Outcomes: done (kept/discarded/requeued), gone (already missing/purged), refused (rpc/cap/full), and :0 (notheld).
/// </summary>
public class DeadLetterResolutionTests
{
    private static readonly long T0 = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc).Ticks;

    private static HighwayServerOptions CreateOptions(int maxAttempts = 1, int maxDeadEntries = 100, TimeSpan? lease = null, long maxQueueBytes = 0)
        => new()
        {
            MaxDeliveryAttempts = maxAttempts,
            MaxDeadLetterEntries = maxDeadEntries,
            Lease = lease ?? TimeSpan.FromSeconds(5),
            MaxQueueBytes = maxQueueBytes
        };

    private static void Session(CommandHarness h, long now, string node, params (string Kind, string Name)[] feeds)
    {
        var json = "{\"deadLetters\":[" + string.Join(",", feeds.Select(f => $"{{\"kind\":\"{f.Kind}\",\"name\":\"{f.Name}\"}}")) + "]}";
        CommandHarness.AsText(h.Run(new HwSessionCommand(), now, node, json)).Should().Be("+OK\r\n");
    }

    private static (string Token, string DeadId, string Payload) ClaimDead(CommandHarness h, long now, string kind, string name, string node)
    {
        var reply = h.Run(new HwClaimCommand(), now, "DEAD", kind, name, node);
        var rows = CommandHarness.Claims(reply);
        rows.Should().HaveCount(1);
        return (rows[0].Token, rows[0].Id, rows[0].Payload);
    }

    [Fact]
    public void ClaimDead_WhenEmptyOrNonExistent_ReturnsEmptyArray()
    {
        using var h = new CommandHarness();

        var emptyReply = CommandHarness.AsText(h.Run(new HwClaimCommand(), T0, "DEAD", "Q", "nonexistent", "node-1"));
        emptyReply.Should().Be("*0\r\n");
    }

    [Fact]
    public void ClaimDead_And_ResolveKeep_PreservesDeadLetter_AcksFeedNotification()
    {
        var options = CreateOptions(maxAttempts: 1);
        using var h = new CommandHarness(options: options);

        Session(h, T0, "worker-1", ("Q", "orders"));
        var targetBox = Catalog.Ensure(h.Store, h.Boxes, h.Locks, BoxKeys.Kind.Queue, "orders", T0);

        // Enqueue with attempts = 1 so expiration immediately dead-letters it
        var entry = Envelope.EncodeRpcEntry("ord-1"u8.ToArray(), "payload-1"u8.ToArray(), 1);
        using (var wb = new WorkBox(h.Store, h.Boxes, targetBox, tracksBytes: true))
        {
            wb.Enqueue(entry);
            wb.Commit();
        }

        // Claim and let lease expire to trigger sweep -> dead-letter + feed notification
        h.Run(new HwClaimCommand(), T0, "Q", "orders", "node-1");
        var t1 = T0 + TimeSpan.FromSeconds(10).Ticks;
        h.Run(new HwClaimCommand(), t1, "Q", "orders", "node-2");

        // Worker claims dead notification from feed
        var (token, deadId, record) = ClaimDead(h, t1, "Q", "orders", "worker-1");
        token.Should().NotBeNullOrEmpty();
        deadId.Should().HaveLength(24);

        // Resolve KEEP
        var res = h.Run(new HwDlqCommand(), t1, "RESOLVE", token, "KEEP");
        var resText = CommandHarness.AsText(res);
        resText.Should().Be("*2\r\n$4\r\ndone\r\n$4\r\nkept\r\n");

        // Target box dead letter remains
        using (var wb = new WorkBox(h.Store, h.Boxes, targetBox, tracksBytes: true))
        {
            wb.Dead.Should().Be(1);
        }

        // Feed notification is acknowledged; re-resolving returns :0 (notheld)
        var stale = CommandHarness.AsText(h.Run(new HwDlqCommand(), t1, "RESOLVE", token, "KEEP"));
        stale.Should().Be(":0\r\n");

        // Flight recorder recorded DeadLetterResolved event
        var events = h.Recorder.Read("Q:orders", DateTimeOffset.MinValue, DateTimeOffset.MaxValue, null, 100);
        events.Should().Contain(e => e.EventType == HighwayEventType.DeadLetterResolved && e.ErrorCode == "KEEP");
    }

    [Fact]
    public void ResolveDiscard_DeletesDeadLetter_AcksFeed()
    {
        var options = CreateOptions(maxAttempts: 1);
        using var h = new CommandHarness(options: options);

        Session(h, T0, "worker-1", ("Q", "orders"));
        var targetBox = Catalog.Ensure(h.Store, h.Boxes, h.Locks, BoxKeys.Kind.Queue, "orders", T0);

        var entry = Envelope.EncodeRpcEntry("ord-2"u8.ToArray(), "payload-2"u8.ToArray(), 1);
        using (var wb = new WorkBox(h.Store, h.Boxes, targetBox, tracksBytes: true))
        {
            wb.Enqueue(entry);
            wb.Commit();
        }

        h.Run(new HwClaimCommand(), T0, "Q", "orders", "node-1");
        var t1 = T0 + TimeSpan.FromSeconds(10).Ticks;
        h.Run(new HwClaimCommand(), t1, "Q", "orders", "node-2");

        var (token, deadId, _) = ClaimDead(h, t1, "Q", "orders", "worker-1");

        // Resolve DISCARD
        var res = CommandHarness.AsText(h.Run(new HwDlqCommand(), t1, "RESOLVE", token, "DISCARD"));
        res.Should().Be("*2\r\n$4\r\ndone\r\n$9\r\ndiscarded\r\n");

        // Target box dead letter is purged
        using (var wb = new WorkBox(h.Store, h.Boxes, targetBox, tracksBytes: true))
        {
            wb.Dead.Should().Be(0);
        }

        var events = h.Recorder.Read("Q:orders", DateTimeOffset.MinValue, DateTimeOffset.MaxValue, null, 100);
        events.Should().Contain(e => e.EventType == HighwayEventType.DeadLetterResolved && e.ErrorCode == "DISCARD");
    }

    [Fact]
    public void ResolveRequeue_MovesToLiveQueue_IncrementsLineage_CleansAttempts()
    {
        var options = CreateOptions(maxAttempts: 1);
        using var h = new CommandHarness(options: options);

        Session(h, T0, "worker-1", ("Q", "orders"));
        var targetBox = Catalog.Ensure(h.Store, h.Boxes, h.Locks, BoxKeys.Kind.Queue, "orders", T0);

        var entry = Envelope.EncodeRpcEntry("ord-3"u8.ToArray(), "payload-3"u8.ToArray(), 1);
        using (var wb = new WorkBox(h.Store, h.Boxes, targetBox, tracksBytes: true))
        {
            wb.Enqueue(entry);
            wb.Commit();
        }

        h.Run(new HwClaimCommand(), T0, "Q", "orders", "node-1");
        var t1 = T0 + TimeSpan.FromSeconds(10).Ticks;
        h.Run(new HwClaimCommand(), t1, "Q", "orders", "node-2");

        var (token, deadId, _) = ClaimDead(h, t1, "Q", "orders", "worker-1");

        // Resolve REQUEUE
        var res = CommandHarness.AsText(h.Run(new HwDlqCommand(), t1, "RESOLVE", token, "REQUEUE"));
        res.Should().Be("*2\r\n$4\r\ndone\r\n$8\r\nrequeued\r\n");

        // DLQ is now 0, live ready queue is 1
        using (var wb = new WorkBox(h.Store, h.Boxes, targetBox, tracksBytes: true))
        {
            wb.Dead.Should().Be(0);
            wb.Ready.Should().Be(1);
        }

        // Live claim gets the requeued message with lineage requeues = 1 and attempts reset to 0
        var liveClaims = CommandHarness.Claims(h.Run(new HwClaimCommand(), t1, "Q", "orders", "worker-2"));
        liveClaims.Should().HaveCount(1);
        liveClaims[0].Id.Should().Be("ord-3");

        // Verify lineage on storage entry
        using (var wb = new WorkBox(h.Store, h.Boxes, targetBox, tracksBytes: true))
        {
            var inFlight = wb.ReadInFlight(ulong.Parse(liveClaims[0].Token.Split('.')[1]));
            inFlight.Should().NotBeNull();
            Envelope.TryGetLineage(inFlight!.Entry, out var requeues, out _);
            requeues.Should().Be(1);
        }

        var events = h.Recorder.Read("Q:orders", DateTimeOffset.MinValue, DateTimeOffset.MaxValue, null, 100);
        events.Should().Contain(e => e.EventType == HighwayEventType.DeadLetterResolved && e.ErrorCode == "REQUEUE");
    }

    [Fact]
    public void ResolveRequeue_WithAfter_EnqueuesDelayed()
    {
        var options = CreateOptions(maxAttempts: 1);
        using var h = new CommandHarness(options: options);

        Session(h, T0, "worker-1", ("Q", "orders"));
        var targetBox = Catalog.Ensure(h.Store, h.Boxes, h.Locks, BoxKeys.Kind.Queue, "orders", T0);

        var entry = Envelope.EncodeRpcEntry("ord-4"u8.ToArray(), "payload-4"u8.ToArray(), 1);
        using (var wb = new WorkBox(h.Store, h.Boxes, targetBox, tracksBytes: true))
        {
            wb.Enqueue(entry);
            wb.Commit();
        }

        h.Run(new HwClaimCommand(), T0, "Q", "orders", "node-1");
        var t1 = T0 + TimeSpan.FromSeconds(10).Ticks;
        h.Run(new HwClaimCommand(), t1, "Q", "orders", "node-2");

        var (token, _, _) = ClaimDead(h, t1, "Q", "orders", "worker-1");

        // Resolve REQUEUE AFTER 5000
        var res = CommandHarness.AsText(h.Run(new HwDlqCommand(), t1, "RESOLVE", token, "REQUEUE", "AFTER", "5000"));
        res.Should().Be("*2\r\n$4\r\ndone\r\n$8\r\nrequeued\r\n");

        // Dead is 0, Delayed is 1
        using (var wb = new WorkBox(h.Store, h.Boxes, targetBox, tracksBytes: true))
        {
            wb.Dead.Should().Be(0);
            wb.Delayed.Should().Be(1);
            wb.Ready.Should().Be(0);
        }
    }

    [Fact]
    public void ResolveRequeue_OnRpcService_RefusedRpc()
    {
        var options = CreateOptions(maxAttempts: 1);
        using var h = new CommandHarness(options: options);

        Session(h, T0, "worker-1", ("SVC", "billing"));
        var targetBox = Catalog.Ensure(h.Store, h.Boxes, h.Locks, BoxKeys.Kind.Service, "billing", T0);

        var entry = Envelope.EncodeRpcEntry("req-1"u8.ToArray(), "charge"u8.ToArray(), 1);
        using (var wb = new WorkBox(h.Store, h.Boxes, targetBox, tracksBytes: false))
        {
            wb.Enqueue(entry);
            wb.Commit();
        }

        h.Run(new HwClaimCommand(), T0, "SVC", "billing", "node-1");
        var t1 = T0 + TimeSpan.FromSeconds(10).Ticks;
        h.Run(new HwClaimCommand(), t1, "SVC", "billing", "node-2");

        var (token, _, _) = ClaimDead(h, t1, "SVC", "billing", "worker-1");

        // Resolve REQUEUE on RPC service -> refused rpc
        var res = CommandHarness.AsText(h.Run(new HwDlqCommand(), t1, "RESOLVE", token, "REQUEUE"));
        res.Should().Be("*2\r\n$7\r\nrefused\r\n$3\r\nrpc\r\n");

        // Dead letter remains in DLQ
        using (var wb = new WorkBox(h.Store, h.Boxes, targetBox, tracksBytes: false))
        {
            wb.Dead.Should().Be(1);
        }

        var events = h.Recorder.Read("SVC:billing", DateTimeOffset.MinValue, DateTimeOffset.MaxValue, null, 100);
        events.Should().Contain(e => e.EventType == HighwayEventType.DeadLetterRequeueRefused && e.ErrorCode == "rpc");
    }

    [Fact]
    public void ResolveRequeue_WhenCapExceeded_RefusedCap()
    {
        var options = CreateOptions(maxAttempts: 1);
        using var h = new CommandHarness(options: options);

        Session(h, T0, "worker-1", ("Q", "orders"));
        var targetBox = Catalog.Ensure(h.Store, h.Boxes, h.Locks, BoxKeys.Kind.Queue, "orders", T0);

        // Set max requeues policy to 2
        using (var wb = new WorkBox(h.Store, h.Boxes, targetBox, tracksBytes: true))
        {
            wb.SetMaxRequeues(2);
            wb.Commit();
        }

        // Create an entry that already has 2 requeues
        var entry = Envelope.WithLineage(Envelope.EncodeRpcEntry("ord-cap"u8.ToArray(), "payload"u8.ToArray(), 1), requeues: 2);
        using (var wb = new WorkBox(h.Store, h.Boxes, targetBox, tracksBytes: true))
        {
            wb.Enqueue(entry);
            wb.Commit();
        }

        h.Run(new HwClaimCommand(), T0, "Q", "orders", "node-1");
        var t1 = T0 + TimeSpan.FromSeconds(10).Ticks;
        h.Run(new HwClaimCommand(), t1, "Q", "orders", "node-2");

        var (token, _, _) = ClaimDead(h, t1, "Q", "orders", "worker-1");

        // Attempting to requeue beyond cap of 2 -> refused cap
        var res = CommandHarness.AsText(h.Run(new HwDlqCommand(), t1, "RESOLVE", token, "REQUEUE"));
        res.Should().Be("*2\r\n$7\r\nrefused\r\n$3\r\ncap\r\n");

        // Dead letter remains kept in DLQ
        using (var wb = new WorkBox(h.Store, h.Boxes, targetBox, tracksBytes: true))
        {
            wb.Dead.Should().Be(1);
        }

        var events = h.Recorder.Read("Q:orders", DateTimeOffset.MinValue, DateTimeOffset.MaxValue, null, 100);
        events.Should().Contain(e => e.EventType == HighwayEventType.DeadLetterRequeueRefused && e.ErrorCode == "cap");
    }

    [Fact]
    public void ResolveRequeue_WhenQueueFull_RefusedFull()
    {
        // 100 bytes queue limit
        var options = CreateOptions(maxAttempts: 1, maxQueueBytes: 100);
        using var h = new CommandHarness(options: options);

        Session(h, T0, "worker-1", ("Q", "bounded"));
        var targetBox = Catalog.Ensure(h.Store, h.Boxes, h.Locks, BoxKeys.Kind.Queue, "bounded", T0);

        var entry = Envelope.EncodeRpcEntry("ord-full"u8.ToArray(), new byte[60], 1);
        using (var wb = new WorkBox(h.Store, h.Boxes, targetBox, tracksBytes: true))
        {
            wb.Enqueue(entry);
            wb.Commit();
        }

        h.Run(new HwClaimCommand(), T0, "Q", "bounded", "node-1");
        var t1 = T0 + TimeSpan.FromSeconds(10).Ticks;
        h.Run(new HwClaimCommand(), t1, "Q", "bounded", "node-2");

        var (token, _, _) = ClaimDead(h, t1, "Q", "bounded", "worker-1");

        // Now fill the queue with another entry taking 80 bytes
        var filler = Envelope.EncodeRpcEntry("filler"u8.ToArray(), new byte[50], 0);
        using (var wb = new WorkBox(h.Store, h.Boxes, targetBox, tracksBytes: true))
        {
            wb.Enqueue(filler);
            wb.Commit();
        }

        // Resolving REQUEUE exceeds 100 bytes -> refused full
        var res = CommandHarness.AsText(h.Run(new HwDlqCommand(), t1, "RESOLVE", token, "REQUEUE"));
        res.Should().Be("*2\r\n$7\r\nrefused\r\n$4\r\nfull\r\n");

        using (var wb = new WorkBox(h.Store, h.Boxes, targetBox, tracksBytes: true))
        {
            wb.Dead.Should().Be(1);
        }
    }

    [Fact]
    public void Resolve_WhenDeadLetterAlreadyPurged_ReturnsGone()
    {
        var options = CreateOptions(maxAttempts: 1);
        using var h = new CommandHarness(options: options);

        Session(h, T0, "worker-1", ("Q", "orders"));
        var targetBox = Catalog.Ensure(h.Store, h.Boxes, h.Locks, BoxKeys.Kind.Queue, "orders", T0);

        var entry = Envelope.EncodeRpcEntry("ord-gone"u8.ToArray(), "payload"u8.ToArray(), 1);
        using (var wb = new WorkBox(h.Store, h.Boxes, targetBox, tracksBytes: true))
        {
            wb.Enqueue(entry);
            wb.Commit();
        }

        h.Run(new HwClaimCommand(), T0, "Q", "orders", "node-1");
        var t1 = T0 + TimeSpan.FromSeconds(10).Ticks;
        h.Run(new HwClaimCommand(), t1, "Q", "orders", "node-2");

        var (token, deadId, _) = ClaimDead(h, t1, "Q", "orders", "worker-1");

        // Operator purges the dead letter directly via HW.DLQ PURGE
        var purgeRep = CommandHarness.AsText(h.Run(new HwDlqCommand(), t1, "PURGE", "Q", "orders", "ID", deadId));
        purgeRep.Should().Be(":1\r\n");

        // Now worker tries to resolve the feed notification -> reports gone
        var res = CommandHarness.AsText(h.Run(new HwDlqCommand(), t1, "RESOLVE", token, "KEEP"));
        res.Should().Be("*2\r\n$4\r\ngone\r\n$34\r\nentry missing or previously purged\r\n");

        // Feed claim is acknowledged, so repeating returns :0 (notheld)
        var stale = CommandHarness.AsText(h.Run(new HwDlqCommand(), t1, "RESOLVE", token, "KEEP"));
        stale.Should().Be(":0\r\n");
    }

    [Fact]
    public void Resolve_ValidationErrors()
    {
        using var h = new CommandHarness();

        // Malformed token
        var err1 = CommandHarness.AsText(h.Run(new HwDlqCommand(), T0, "RESOLVE", "badtoken", "KEEP"));
        err1.Should().StartWith("-ERR HW_INVALID_TOKEN");

        // Invalid resolve action
        var err2 = CommandHarness.AsText(h.Run(new HwDlqCommand(), T0, "RESOLVE", "16.1", "FOO"));
        err2.Should().StartWith("-ERR HW_INVALID_ARG");

        // AFTER with KEEP
        var err3 = CommandHarness.AsText(h.Run(new HwDlqCommand(), T0, "RESOLVE", "16.1", "KEEP", "AFTER", "1000"));
        err3.Should().StartWith("-ERR HW_INVALID_ARG");
    }
}
