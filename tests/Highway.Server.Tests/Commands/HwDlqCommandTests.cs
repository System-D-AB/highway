using System.Text;
using FluentAssertions;
using Highway.Server;
using Highway.Server.Commands.Ported;
using Highway.Server.Internal;
using Highway.Server.Storage;
using Highway.Server.Storage.Boxes;
using Xunit;

namespace Highway.Server.Tests.Commands;

public class HwDlqCommandTests
{
    private static (uint Box, ulong Seq) DeadLetterOne(CommandHarness h, string targetKind, string name, string id, ushort requeues = 0)
    {
        var t = 1_000_000_000L;
        var kind = targetKind == "Q" ? BoxKeys.Kind.Queue : BoxKeys.Kind.Service;
        var box = Catalog.Ensure(h.Store, h.Boxes, h.Locks, kind, name, t);

        var entry = Envelope.EncodeRpcEntry(Encoding.UTF8.GetBytes(id), Encoding.UTF8.GetBytes("payload-" + id), 0);
        if (requeues > 0)
            entry = Envelope.WithLineage(entry, requeues);

        using (var wb = new WorkBox(h.Store, h.Boxes, box, tracksBytes: kind == BoxKeys.Kind.Queue))
        {
            wb.Enqueue(entry);
            wb.Commit();
        }

        for (var attempt = 0; attempt < 7; attempt++)
        {
            h.Run(new HwClaimCommand(), t, targetKind, name, "node-A");
            t += TimeSpan.FromMinutes(6).Ticks;
        }
        h.Run(new HwClaimCommand(), t, targetKind, name, "node-B");

        using (var wb = new WorkBox(h.Store, h.Boxes, box, tracksBytes: kind == BoxKeys.Kind.Queue))
        {
            var dead = wb.PeekDead(10);
            var last = dead.Last(d =>
            {
                DeadLetter.Decode(d.Value, out _, out _, out _, out var orig);
                Envelope.DecodeRpcEntry(orig, out var origId, out _, out _);
                return Encoding.UTF8.GetString(origId) == id;
            });
            return (box, BoxKeys.TrailingSeq(last.Key));
        }
    }

    [Fact]
    public void Dlq_Peek_QueueTarget_ReturnsDeadId_Requeues_And_MessageId()
    {
        using var h = new CommandHarness();
        DeadLetterOne(h, "Q", "orders.dlq", "ord-123", requeues: 2);

        var reply = CommandHarness.AsText(h.Run(new HwDlqCommand(), "PEEK", "Q", "orders.dlq"));
        reply.Should().Contain("deadId");
        reply.Should().Contain("requeues");
        reply.Should().Contain("2");
        reply.Should().Contain("messageId");
        reply.Should().Contain("ord-123");
        reply.Should().NotContain("requestId");
    }

    [Fact]
    public void Dlq_Peek_ServiceTarget_ReturnsDeadId_Requeues_And_RequestId()
    {
        using var h = new CommandHarness();
        DeadLetterOne(h, "SVC", "billing.charge", "req-456");

        var reply = CommandHarness.AsText(h.Run(new HwDlqCommand(), "PEEK", "SVC", "billing.charge"));
        reply.Should().Contain("deadId");
        reply.Should().Contain("requeues");
        reply.Should().Contain("0");
        reply.Should().Contain("requestId");
        reply.Should().Contain("req-456");
        reply.Should().NotContain("messageId");
    }

    [Fact]
    public void Dlq_Requeue_And_Purge_ById_Success_And_AlreadyGone()
    {
        using var h = new CommandHarness();
        var (b1, s1) = DeadLetterOne(h, "Q", "invoices", "inv-1");
        var (b2, s2) = DeadLetterOne(h, "Q", "invoices", "inv-2");

        var deadId1 = DeadLetter.FormatDeadId(b1, s1);
        var deadId2 = DeadLetter.FormatDeadId(b2, s2);

        // Requeue inv-1 by ID
        var reqReply = CommandHarness.AsText(h.Run(new HwDlqCommand(), "REQUEUE", "Q", "invoices", "ID", deadId1));
        reqReply.Should().Be(":1\r\n");

        // Requeuing same ID again returns :0 (already gone)
        var reqAgain = CommandHarness.AsText(h.Run(new HwDlqCommand(), "REQUEUE", "Q", "invoices", "ID", deadId1));
        reqAgain.Should().Be(":0\r\n");

        // Purge inv-2 by ID
        var purgeReply = CommandHarness.AsText(h.Run(new HwDlqCommand(), "PURGE", "Q", "invoices", "ID", deadId2));
        purgeReply.Should().Be(":1\r\n");

        // Purging same ID again returns :0 (already gone)
        var purgeAgain = CommandHarness.AsText(h.Run(new HwDlqCommand(), "PURGE", "Q", "invoices", "ID", deadId2));
        purgeAgain.Should().Be(":0\r\n");
    }

    [Fact]
    public void Dlq_Requeue_ById_WithAfter_DelaysWork()
    {
        using var h = new CommandHarness();
        var (box, seq) = DeadLetterOne(h, "Q", "tasks", "tsk-1");
        var deadId = DeadLetter.FormatDeadId(box, seq);

        var reply = CommandHarness.AsText(h.Run(new HwDlqCommand(), "REQUEUE", "Q", "tasks", "ID", deadId, "AFTER", "5000"));
        reply.Should().Be(":1\r\n");

        using (var wb = new WorkBox(h.Store, h.Boxes, box, tracksBytes: true))
        {
            wb.Dead.Should().Be(0);
            wb.Delayed.Should().Be(1);
            wb.Ready.Should().Be(0);
        }
    }

    [Fact]
    public void Dlq_Validation_Errors()
    {
        using var h = new CommandHarness();
        DeadLetterOne(h, "Q", "val.queue", "v1");

        // PEEK with ID is refused
        var r1 = CommandHarness.AsText(h.Run(new HwDlqCommand(), "PEEK", "Q", "val.queue", "ID", "000000100000000000000001"));
        r1.Should().StartWith("-ERR HW_INVALID_ARG");

        // COUNT and ID are mutually exclusive
        var r2 = CommandHarness.AsText(h.Run(new HwDlqCommand(), "REQUEUE", "Q", "val.queue", "COUNT", "1", "ID", "000000100000000000000001"));
        r2.Should().StartWith("-ERR HW_INVALID_ARG");

        // AFTER with PURGE is refused
        var r3 = CommandHarness.AsText(h.Run(new HwDlqCommand(), "PURGE", "Q", "val.queue", "ID", "000000100000000000000001", "AFTER", "1000"));
        r3.Should().StartWith("-ERR HW_INVALID_ARG");

        // Malformed ID
        var r4 = CommandHarness.AsText(h.Run(new HwDlqCommand(), "PURGE", "Q", "val.queue", "ID", "bad-id"));
        r4.Should().StartWith("-ERR HW_INVALID_ARG");
    }
}
