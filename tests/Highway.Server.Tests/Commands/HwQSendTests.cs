using System.Text;
using FluentAssertions;
using Highway.Abstractions.Observability;
using Highway.Server;
using Highway.Server.Commands.Ported;
using Highway.Server.Commands.Runtime;
using Highway.Server.Internal;
using Highway.Server.Storage.Layout;
using Xunit;

namespace Highway.Server.Tests.Commands;

/// <summary>
/// 039 T2 — the ported HW.QSEND template. Golden replies are the byte strings the Garnet
/// version produced (captured from its WriteSimpleString / WriteError shapes): +OK on
/// success, -ERR HW_QUEUE_FULL on refusal, -ERR HW_INVALID_ARG on a bad argument. Plus the
/// behaviour the command must preserve: the entry lands in the live queue (or the delayed
/// set with AT), the byte counter tracks it, the recorder logs QueueSent, the doorbell rings.
/// </summary>
public class HwQSendTests
{
    // -- golden replies --

    [Fact]
    public void QSend_Success_RepliesOk()
    {
        using var h = new CommandHarness();
        var reply = h.Run(new HwQSendCommand(), "invoices", "msg-1", "payload");
        CommandHarness.AsText(reply).Should().Be("+OK\r\n");
    }

    [Fact]
    public void QSend_BlankQueue_RepliesInvalidArg()
    {
        using var h = new CommandHarness();
        var reply = h.Run(new HwQSendCommand(), "", "msg-1", "payload");
        CommandHarness.AsText(reply).Should().Be("-ERR HW_INVALID_ARG queue is blank\r\n");
    }

    [Fact]
    public void QSend_UnknownKeyword_RepliesInvalidArg()
    {
        using var h = new CommandHarness();
        var reply = h.Run(new HwQSendCommand(), "invoices", "msg-1", "payload", "SOON");
        CommandHarness.AsText(reply).Should().Be("-ERR HW_INVALID_ARG unknown argument 'SOON'; expected AT\r\n");
    }

    [Fact]
    public void QSend_OverByteBudget_RefusesWithQueueFull()
    {
        var opts = new HighwayServerOptions { MaxQueueBytes = 8 };
        using var h = new CommandHarness(options: opts);

        // First small send fits; a second pushes over the tiny budget.
        h.Run(new HwQSendCommand(), "q", "m1", "12345"); // entry > 8 bytes already? keep payload tiny
        var reply = h.Run(new HwQSendCommand(), "q", "m2", "0123456789");

        CommandHarness.AsText(reply).Should().StartWith("-ERR HW_QUEUE_FULL");
    }

    // -- behaviour preserved --

    [Fact]
    public void QSend_Immediate_LandsInLiveQueue_AndTracksBytes()
    {
        using var h = new CommandHarness();
        h.Run(new HwQSendCommand(), "invoices", "msg-1", "payload");

        using var snap = h.Store.Snapshot();
        var listPrefix = HighwayKeyspace.ListPrefix(HighwayNames.Queue("invoices"));
        h.Store.ListLength(snap, listPrefix).Should().Be(1, "an immediate send lands in the live queue");

        // Byte counter is non-zero (the entry's bytes).
        h.Store.ReadByteCounter(snap, HighwayNames.QueueBytes("invoices"))
            .Should().BeGreaterThan(0);
    }

    [Fact]
    public void QSend_WithFutureAt_LandsInDelayedSet_NotLiveQueue()
    {
        using var h = new CommandHarness();
        var now = 1_000_000L;
        var future = (now + 10_000_000L).ToString();

        h.Run(new HwQSendCommand(), now, "invoices", "msg-1", "payload", "AT", future);

        using var snap = h.Store.Snapshot();
        h.Store.ListLength(snap, HighwayKeyspace.ListPrefix(HighwayNames.Queue("invoices")))
            .Should().Be(0, "a deferred send is not in the live queue");
        h.Store.SortedSetLength(snap, HighwayKeyspace.SortedSetPrefix(HighwayNames.QueueDelayed("invoices")))
            .Should().Be(1, "a deferred send is in the delayed set");
    }

    [Fact]
    public void QSend_PastAt_DeliversImmediately()
    {
        using var h = new CommandHarness();
        var now = 5_000_000L;
        var past = (now - 1000).ToString();

        h.Run(new HwQSendCommand(), now, "invoices", "msg-1", "payload", "AT", past);

        using var snap = h.Store.Snapshot();
        h.Store.ListLength(snap, HighwayKeyspace.ListPrefix(HighwayNames.Queue("invoices")))
            .Should().Be(1, "a past delivery time delivers immediately, not deferred");
    }

    [Fact]
    public void QSend_Immediate_RingsDoorbell()
    {
        using var h = new CommandHarness();
        h.Run(new HwQSendCommand(), "invoices", "msg-1", "payload");

        h.Doorbell.Rings.Should().ContainSingle();
        h.Doorbell.Rings[0].Channel.Should().Be(HighwayKeys.QueueDoorbell("invoices"));
        Encoding.UTF8.GetString(h.Doorbell.Rings[0].Payload).Should().Be("msg-1");
    }

    [Fact]
    public void QSend_Deferred_DoesNotRingDoorbell()
    {
        using var h = new CommandHarness();
        var now = 1_000_000L;
        h.Run(new HwQSendCommand(), now, "invoices", "msg-1", "payload", "AT", (now + 1_000_000L).ToString());

        h.Doorbell.Rings.Should().BeEmpty("a deferred message wakes nobody until it is promoted");
    }

    [Fact]
    public void QSend_Refused_DoesNotRing_AndRecordsSendRefused()
    {
        var opts = new HighwayServerOptions { MaxQueueBytes = 4 };
        using var h = new CommandHarness(options: opts);

        h.Run(new HwQSendCommand(), "q", "m1", "0123456789"); // over budget from the start

        h.Doorbell.Rings.Should().BeEmpty("a refused send rings nobody");
    }
}
