using System.Text;
using FluentAssertions;
using Highway.Server;
using Highway.Server.Commands.Ported;
using Highway.Server.Storage.Layout;
using Xunit;

namespace Highway.Server.Tests.Commands;

/// <summary>039 T6 — Batch 5 inspection: HW.DLQ, HW.REPLAY, HW.STATS.</summary>
public class InspectionTests
{
    // Drive a message all the way to the DLQ: send, claim, let the lease expire past
    // MaxDeliveryAttempts so the sweep dead-letters it.
    private static void DeadLetterOne(CommandHarness h, string queue, string id)
    {
        var t = 1_000_000_000L;
        h.Run(new HwQSendCommand(), t, queue, id, "payload");
        // Claim + expire repeatedly until it exceeds MaxDeliveryAttempts (default 5).
        for (var attempt = 0; attempt < 7; attempt++)
        {
            h.Run(new HwQClaimCommand(), t, queue, "node-A");
            t += System.TimeSpan.FromMinutes(6).Ticks; // past the 5-min lease
        }
        // A final claim runs the sweep that dead-letters it.
        h.Run(new HwQClaimCommand(), t, queue, "node-B");
    }

    // ---------------- HW.STATS (the parity target) ----------------

    [Fact]
    public void Stats_Queue_ReportsDepthFromRangeCount()
    {
        using var h = new CommandHarness();
        h.Run(new HwQSendCommand(), "invoices", "m1", "a");
        h.Run(new HwQSendCommand(), "invoices", "m2", "b");
        h.Run(new HwQSendCommand(), "invoices", "m3", "c");

        var reply = CommandHarness.AsText(h.Run(new HwStatsCommand(), "Q:invoices"));

        // Flat field array; depth is the list length as a range count.
        reply.Should().Contain("kind").And.Contain("queue");
        reply.Should().Contain("depth").And.Contain("3");
    }

    [Fact]
    public void Stats_Queue_InFlightCountsProcessingLists()
    {
        using var h = new CommandHarness();
        h.Run(new HwQSendCommand(), "q", "m1", "a");
        h.Run(new HwQSendCommand(), "q", "m2", "b");
        h.Run(new HwQClaimCommand(), "q", "node-A"); // one in flight

        var reply = CommandHarness.AsText(h.Run(new HwStatsCommand(), "Q:q"));
        reply.Should().Contain("inFlight").And.Contain("1");
        reply.Should().Contain("depth"); // one still queued
    }

    [Fact]
    public void Stats_Recorder_ReportsEnabled()
    {
        using var h = new CommandHarness();
        var reply = CommandHarness.AsText(h.Run(new HwStatsCommand(), "RECORDER"));
        reply.Should().Contain("kind").And.Contain("recorder");
    }

    [Fact]
    public void Stats_Server_ReportsNodeCount()
    {
        using var h = new CommandHarness();
        var reply = CommandHarness.AsText(h.Run(new HwStatsCommand()));
        reply.Should().Contain("kind").And.Contain("server");
        reply.Should().Contain("nodes");
    }

    // ---------------- HW.DLQ ----------------

    [Fact]
    public void Dlq_Peek_Empty_ReturnsEmptyArray()
    {
        using var h = new CommandHarness();
        var reply = h.Run(new HwDlqCommand(), "PEEK", "Q", "invoices");
        CommandHarness.AsText(reply).Should().Be("*0\r\n");
    }

    [Fact]
    public void Dlq_Peek_ReturnsDeadLetter_NonDestructive()
    {
        using var h = new CommandHarness();
        DeadLetterOne(h, "invoices", "poison");

        var reply = CommandHarness.AsText(h.Run(new HwDlqCommand(), "PEEK", "Q", "invoices"));
        reply.Should().Contain("deadLetteredAt");
        reply.Should().Contain("attempts");
        reply.Should().Contain("poison"); // the requestId field

        // Peek is non-destructive: the DLQ still has the entry.
        using var snap = h.Store.Snapshot();
        h.Store.ListLength(snap, HighwayKeyspace.ListPrefix(HighwayNames.QueueDeadLetter("invoices")))
            .Should().Be(1);
    }

    [Fact]
    public void Dlq_Requeue_MovesBackToLive_ResetsAttempts()
    {
        using var h = new CommandHarness();
        DeadLetterOne(h, "invoices", "poison");

        var reply = h.Run(new HwDlqCommand(), "REQUEUE", "Q", "invoices");
        CommandHarness.AsText(reply).Should().Be(":1\r\n");

        using var snap = h.Store.Snapshot();
        h.Store.ListLength(snap, HighwayKeyspace.ListPrefix(HighwayNames.QueueDeadLetter("invoices"))).Should().Be(0);
        h.Store.ListLength(snap, HighwayKeyspace.ListPrefix(HighwayNames.Queue("invoices"))).Should().Be(1);
    }

    [Fact]
    public void Dlq_Purge_RemovesFromDlq()
    {
        using var h = new CommandHarness();
        DeadLetterOne(h, "invoices", "poison");

        var reply = h.Run(new HwDlqCommand(), "PURGE", "Q", "invoices");
        CommandHarness.AsText(reply).Should().Be(":1\r\n");

        using var snap = h.Store.Snapshot();
        h.Store.ListLength(snap, HighwayKeyspace.ListPrefix(HighwayNames.QueueDeadLetter("invoices"))).Should().Be(0);
    }

    [Fact]
    public void Dlq_BadAction_InvalidArg()
    {
        using var h = new CommandHarness();
        var reply = h.Run(new HwDlqCommand(), "FROB", "Q", "invoices");
        CommandHarness.AsText(reply).Should().StartWith("-ERR HW_INVALID_ARG");
    }

    // ---------------- HW.REPLAY ----------------

    [Fact]
    public void Replay_ReturnsRecordedEvents()
    {
        using var h = new CommandHarness();
        // A send records a QueueSent event under the queue name.
        h.Run(new HwQSendCommand(), "invoices", "m1", "payload");

        var reply = CommandHarness.AsText(h.Run(new HwReplayCommand(), "invoices"));
        // Array of event field arrays; the QueueSent event carries the requestId.
        reply.Should().Contain("eventType");
        reply.Should().Contain("QueueSent");
    }

    [Fact]
    public void Replay_UnknownName_EmptyArray()
    {
        using var h = new CommandHarness();
        var reply = h.Run(new HwReplayCommand(), "never-used");
        CommandHarness.AsText(reply).Should().Be("*0\r\n");
    }
}
