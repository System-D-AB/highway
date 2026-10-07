using System.Text;
using FluentAssertions;
using Highway.Server.Commands.Ported;
using Highway.Server.Internal;
using Highway.Server.Storage.Boxes;
using Xunit;

namespace Highway.Server.Tests.Commands;

/// <summary>
/// 073 fix (found in verification, 2026-10-06): what a dead-letter handler receives through <c>HW.CLAIM DEAD</c>
/// is the dead letter exactly as stored — including the failure that killed the message and its requeue count.
/// A feed entry's payload is a dead-letter record that ENDS with the original entry's trailers; the feed used to
/// read them as its own and strip them, so <c>LastFailure</c> was always null and <c>Requeues</c> always 0.
/// These tests take the record from the broker's real reply, never build it by hand.
/// </summary>
public class DeadLetterFeedRecordTests
{
    private static readonly long T0 = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc).Ticks;
    private static long At(int seconds) => T0 + TimeSpan.FromSeconds(seconds).Ticks;

    /// <summary>Minimal RESP reader: an array of arrays of bulk strings.</summary>
    private static List<List<byte[]>> Rows(byte[] resp)
    {
        var i = 0;
        string Line() { var s = i; while (resp[i] != '\r') i++; var l = Encoding.ASCII.GetString(resp, s, i - s); i += 2; return l; }
        var rows = new List<List<byte[]>>();
        var top = Line();
        if (!top.StartsWith('*')) throw new InvalidOperationException("not an array: " + top);
        for (var r = int.Parse(top[1..]); r > 0; r--)
        {
            var row = new List<byte[]>();
            for (var c = int.Parse(Line()[1..]); c > 0; c--)
            {
                var len = int.Parse(Line()[1..]);
                row.Add(resp.AsSpan(i, len).ToArray());
                i += len + 2;
            }
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>A queue "payments" with a declared feed, holding one dead letter that was requeued twice and failed with a timeout.</summary>
    private static (CommandHarness H, byte[] Stored) DeadLetterWithHistory()
    {
        var h = new CommandHarness(options: new HighwayServerOptions { MaxDeliveryAttempts = 1, Lease = TimeSpan.FromSeconds(5) });
        CommandHarness.AsText(h.Run(new HwSessionCommand(), T0, "node-1",
            """{"deadLetters":[{"kind":"Q","name":"payments"}]}""")).Should().Be("+OK\r\n");

        var box = Catalog.Ensure(h.Store, h.Boxes, h.Locks, BoxKeys.Kind.Queue, "payments", T0);
        var plain = Envelope.EncodeRpcEntry("pay-1"u8.ToArray(), "charge"u8.ToArray(), 1);
        var failed = Envelope.WithFailureBlock(plain,
            Envelope.EncodeFailureBlock("System.TimeoutException"u8, default, "gateway timed out"u8));
        using (var wb = new WorkBox(h.Store, h.Boxes, box, tracksBytes: true)) { wb.Enqueue(Envelope.WithLineage(failed, 2)); wb.Commit(); }

        h.Run(new HwClaimCommand(), T0, "Q", "payments", "node-1");          // claim
        h.Run(new HwClaimCommand(), At(10), "Q", "payments", "node-2");      // lease expiry → dead letter + notification

        using var read = new WorkBox(h.Store, h.Boxes, box, tracksBytes: true);
        return (h, read.PeekDead(1)[0].Value);
    }

    private static void ShouldCarryTheHistory(byte[] record)
    {
        DeadLetter.Decode(record, out _, out _, out _, out var original);
        Envelope.TryGetLineage(original, out var requeues, out var rest).Should().BeTrue("the handler must see the requeue count");
        requeues.Should().Be(2);
        Envelope.TryGetFailureBlock(rest, out var block, out _).Should().BeTrue("the handler must see why the message died");
        Envelope.DecodeFailureBlock(block, out var type, out _, out var detail);
        Encoding.UTF8.GetString(type).Should().Be("System.TimeoutException");
        Encoding.UTF8.GetString(detail).Should().Be("gateway timed out");
    }

    [Fact]
    public void AHandler_ReceivesTheDeadLetterAsStored_WithItsFailureAndRequeueCount()
    {
        var (h, stored) = DeadLetterWithHistory();
        using var _ = h;

        var rows = Rows(h.Run(new HwClaimCommand(), At(11), "DEAD", "Q", "payments", "node-1"));

        rows.Should().ContainSingle();
        rows[0][^1].Should().Equal(stored, "the notification carries the record byte for byte");
        ShouldCarryTheHistory(rows[0][^1]);
    }

    [Fact]
    public void ANotificationRedeliveredAfterItsLease_StillCarriesTheRecordUnchanged()
    {
        var (h, stored) = DeadLetterWithHistory();
        using var _ = h;

        Rows(h.Run(new HwClaimCommand(), At(11), "DEAD", "Q", "payments", "node-1")).Should().ContainSingle();
        // node-1 dies holding it: the feed's own lease sweep returns it (attempts + 1) — a re-encode of the feed
        // entry, which must neither strip nor duplicate the record's trailers.
        var again = Rows(h.Run(new HwClaimCommand(), At(30), "DEAD", "Q", "payments", "node-2"));

        again.Should().ContainSingle();
        again[0][^1].Should().Equal(stored);
        ShouldCarryTheHistory(again[0][^1]);
    }

    [Fact]
    public void AFailureReportAgainstANotification_IsRefused_AndLeavesTheRecordIntact()
    {
        var (h, stored) = DeadLetterWithHistory();
        using var _ = h;

        var rows = Rows(h.Run(new HwClaimCommand(), At(11), "DEAD", "Q", "payments", "node-1"));
        var token = Encoding.ASCII.GetString(rows[0][0]);

        CommandHarness.AsText(h.Run(new HwFailCommand(), At(12), token, "System.Exception", "x"))
            .Should().Be(":0\r\n", "a notification carries no failure of its own");

        var again = Rows(h.Run(new HwClaimCommand(), At(30), "DEAD", "Q", "payments", "node-2"));
        again[0][^1].Should().Equal(stored);
    }

    [Fact]
    public void ANotificationWhoseHandlerKeepsDying_IsNeverItselfDeadLettered()
    {
        // The feed's lease sweep used MaxDeliveryAttempts and pushed such a notification into the feed's own dead
        // space, where nothing reads it. It now returns to the feed every time.
        var (h, stored) = DeadLetterWithHistory();
        using var _ = h;

        var now = 11;
        for (var i = 0; i < 5; i++, now += 10)
            Rows(h.Run(new HwClaimCommand(), At(now), "DEAD", "Q", "payments", "node-" + i)).Should().ContainSingle($"lapse {i}");

        Rows(h.Run(new HwClaimCommand(), At(now), "DEAD", "Q", "payments", "node-9"))[0][^1].Should().Equal(stored);
    }
}
