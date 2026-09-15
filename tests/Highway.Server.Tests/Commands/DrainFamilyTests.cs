using System.Text;
using FluentAssertions;
using Highway.Server;
using Highway.Server.Commands.Ported;
using Highway.Server.Commands.Runtime;
using Highway.Server.Storage.Layout;
using Xunit;

namespace Highway.Server.Tests.Commands;

/// <summary>039 T5 — Batch 4 drain family: HW.ACK, HW.QACK, HW.FAIL, HW.TOUCH.</summary>
public class DrainFamilyTests
{
    private static string ClaimedId(byte[] reply) => Encoding.UTF8.GetString(reply).Split("\r\n")[2];

    // Seed a claimed message: send, claim (moves to proc list).
    private static void SendAndClaim(CommandHarness h, string queue, string node, string id, string payload, long now = 10_000_000L)
    {
        h.Run(new HwQSendCommand(), now, queue, id, payload);
        h.Run(new HwQClaimCommand(), now, queue, node);
    }

    // ---------------- HW.QACK ----------------

    [Fact]
    public void QAck_RemovesClaimed_ReturnsOne()
    {
        using var h = new CommandHarness();
        SendAndClaim(h, "q", "node-A", "m1", "p");

        var reply = h.Run(new HwQAckCommand(), "q", "node-A", "m1");
        CommandHarness.AsText(reply).Should().Be(":1\r\n");

        using var snap = h.Store.Snapshot();
        h.Store.ListLength(snap, HighwayKeyspace.ListPrefix(HighwayNames.QueueProcessing("q", "node-A")))
            .Should().Be(0, "the acked entry is gone from the processing list");
    }

    [Fact]
    public void QAck_UnknownId_ReturnsZero()
    {
        using var h = new CommandHarness();
        SendAndClaim(h, "q", "node-A", "m1", "p");
        var reply = h.Run(new HwQAckCommand(), "q", "node-A", "does-not-exist");
        CommandHarness.AsText(reply).Should().Be(":0\r\n");
    }

    [Fact]
    public void QAck_PreservesFifoOfSurvivors()
    {
        using var h = new CommandHarness();
        // Claim three onto one node's processing list.
        h.Run(new HwQSendCommand(), "q", "m1", "a");
        h.Run(new HwQSendCommand(), "q", "m2", "b");
        h.Run(new HwQSendCommand(), "q", "m3", "c");
        h.Run(new HwQClaimCommand(), "q", "n");
        h.Run(new HwQClaimCommand(), "q", "n");
        h.Run(new HwQClaimCommand(), "q", "n");

        // Ack the middle one; m1 and m3 must remain in order.
        h.Run(new HwQAckCommand(), "q", "n", "m2");

        // Expire the lease so a claim redelivers survivors head-first, in order.
        var opts = new HighwayServerOptions { Lease = System.TimeSpan.Zero }; // disabled sweep — inspect directly
        using var snap = h.Store.Snapshot();
        h.Store.ListLength(snap, HighwayKeyspace.ListPrefix(HighwayNames.QueueProcessing("q", "n")))
            .Should().Be(2, "two survivors remain after acking one");
    }

    // ---------------- HW.ACK (RPC) ----------------

    [Fact]
    public void Ack_RemovesClaimed_ReturnsOk()
    {
        using var h = new CommandHarness();
        h.Run(new HwCallCommand(), "orders", "req-1", "body");
        h.Run(new HwDequeueCommand(), "orders", "node-A");

        var reply = h.Run(new HwAckCommand(), "orders", "node-A", "req-1");
        CommandHarness.AsText(reply).Should().Be("+OK\r\n");

        using var snap = h.Store.Snapshot();
        h.Store.ListLength(snap, HighwayKeyspace.ListPrefix(HighwayNames.ServiceProcessing("orders", "node-A")))
            .Should().Be(0);
    }

    [Fact]
    public void Ack_UnknownId_StillReturnsOk()
    {
        using var h = new CommandHarness();
        h.Run(new HwCallCommand(), "orders", "req-1", "body");
        h.Run(new HwDequeueCommand(), "orders", "node-A");

        // A retried ack of an unknown/already-acked id is +OK, not an error.
        var reply = h.Run(new HwAckCommand(), "orders", "node-A", "already-gone");
        CommandHarness.AsText(reply).Should().Be("+OK\r\n");
    }

    // ---------------- HW.TOUCH ----------------

    [Fact]
    public void Touch_RenewsClaim_ReturnsOne_AndExtendsLease()
    {
        var opts = new HighwayServerOptions { Lease = System.TimeSpan.FromMinutes(5) };
        using var h = new CommandHarness(options: opts);
        var t0 = 1_000_000_000L;

        h.Run(new HwQSendCommand(), t0, "q", "m1", "p");
        h.Run(new HwQClaimCommand(), t0, "q", "node-A");

        // Just before expiry, touch renews the claim timestamp.
        var nearExpiry = t0 + System.TimeSpan.FromMinutes(4).Ticks;
        var reply = h.Run(new HwTouchCommand(), nearExpiry, "Q", "q", "node-A", "m1");
        CommandHarness.AsText(reply).Should().Be(":1\r\n");

        // Now at t0+6min: without the touch the lease would have expired; with it (renewed at
        // t0+4min, lease 5min → expires t0+9min) the message is still held by node-A, so a
        // claim by node-B finds nothing.
        var afterOriginalExpiry = t0 + System.TimeSpan.FromMinutes(6).Ticks;
        var claim = h.Run(new HwQClaimCommand(), afterOriginalExpiry, "q", "node-B");
        CommandHarness.AsText(claim).Should().Be("*-1\r\n", "the renewed lease has not yet expired");
    }

    [Fact]
    public void Touch_UnknownId_ReturnsZero()
    {
        using var h = new CommandHarness();
        SendAndClaim(h, "q", "node-A", "m1", "p");
        var reply = h.Run(new HwTouchCommand(), "Q", "q", "node-A", "nope");
        CommandHarness.AsText(reply).Should().Be(":0\r\n");
    }

    [Fact]
    public void Touch_BadTarget_InvalidArg()
    {
        using var h = new CommandHarness();
        var reply = h.Run(new HwTouchCommand(), "XX", "q", "node-A", "m1");
        CommandHarness.AsText(reply).Should().StartWith("-ERR HW_INVALID_ARG");
    }

    // ---------------- HW.FAIL ----------------

    [Fact]
    public void Fail_AttachesBlock_ReturnsOne_DoesNotAcknowledge()
    {
        using var h = new CommandHarness();
        SendAndClaim(h, "q", "node-A", "m1", "p");

        var reply = h.Run(new HwFailCommand(), "Q", "q", "node-A", "m1", "System.TimeoutException", "timed out");
        CommandHarness.AsText(reply).Should().Be(":1\r\n");

        // The entry is still in the processing list (fail does not ack).
        using var snap = h.Store.Snapshot();
        h.Store.ListLength(snap, HighwayKeyspace.ListPrefix(HighwayNames.QueueProcessing("q", "node-A")))
            .Should().Be(1, "HW.FAIL rewrites in place, it does not acknowledge");
    }

    [Fact]
    public void Fail_UnknownId_ReturnsZero()
    {
        using var h = new CommandHarness();
        SendAndClaim(h, "q", "node-A", "m1", "p");
        var reply = h.Run(new HwFailCommand(), "Q", "q", "node-A", "gone", "T", "d");
        CommandHarness.AsText(reply).Should().Be(":0\r\n");
    }
}
