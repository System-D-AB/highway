using System.Text;
using FluentAssertions;
using Highway.Server.Commands.Ported;
using Highway.Server.Internal;
using Highway.Server.Storage.Boxes;
using Xunit;

namespace Highway.Server.Tests.Commands;

/// <summary>
/// 073 verification: behaviours the first 073 tests named but could not catch breaking (mutations that survived).
/// Each test pins one: a refusal still settles the notification; <c>AFTER</c> is measured from now; <c>PEEK</c>
/// reports the exact requeue count; <c>HW.FAIL</c> keeps an entry's requeue count.
/// </summary>
public class DeadLetterStrengthTests
{
    private static readonly long T0 = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc).Ticks;
    private static long At(double seconds) => T0 + TimeSpan.FromSeconds(seconds).Ticks;

    /// <summary>"orders" with a declared feed and one message that dead-letters at its first lease expiry.</summary>
    private static CommandHarness OneDeadLetter(byte[]? entry = null, Action<WorkBox>? configure = null, long maxQueueBytes = 0)
    {
        var h = new CommandHarness(options: new HighwayServerOptions
        {
            MaxDeliveryAttempts = 1, Lease = TimeSpan.FromSeconds(5), MaxQueueBytes = maxQueueBytes,
        });
        CommandHarness.AsText(h.Run(new HwSessionCommand(), T0, "worker-1",
            """{"deadLetters":[{"kind":"Q","name":"orders"}]}""")).Should().Be("+OK\r\n");
        var box = Catalog.Ensure(h.Store, h.Boxes, h.Locks, BoxKeys.Kind.Queue, "orders", T0);
        using (var wb = new WorkBox(h.Store, h.Boxes, box, tracksBytes: true))
        {
            configure?.Invoke(wb);
            wb.Enqueue(entry ?? Envelope.EncodeRpcEntry("ord-1"u8, "payload"u8, 1));
            wb.Commit();
        }
        h.Run(new HwClaimCommand(), T0, "Q", "orders", "node-1");
        h.Run(new HwClaimCommand(), At(10), "Q", "orders", "node-2");   // lease expiry → dead letter + notification
        return h;
    }

    private static string ClaimDeadToken(CommandHarness h, long now)
        => CommandHarness.Claims(h.Run(new HwClaimCommand(), now, "DEAD", "Q", "orders", "worker-1")).Single().Token;

    private static string Text(byte[] b) => CommandHarness.AsText(b);

    private static long Dead(CommandHarness h)
    {
        using var batch = h.Store.NewBatch();
        var box = Catalog.Find(h.Store, batch, h.Boxes, BoxKeys.Kind.Queue, "orders")!.Value;
        using var wb = new WorkBox(h.Store, h.Boxes, box, tracksBytes: true, batch);
        return wb.Dead;
    }

    [Fact]
    public void ARefusedRequeue_StillSettlesTheNotification_AndKeepsTheDeadLetter()
    {
        using var h = OneDeadLetter(
            Envelope.WithLineage(Envelope.EncodeRpcEntry("ord-1"u8, "payload"u8, 1), 2),
            wb => wb.SetMaxRequeues(2));
        var token = ClaimDeadToken(h, At(11));

        Text(h.Run(new HwDlqCommand(), At(11), "RESOLVE", token, "REQUEUE")).Should().Be("*2\r\n$7\r\nrefused\r\n$3\r\ncap\r\n");

        Text(h.Run(new HwDlqCommand(), At(12), "RESOLVE", token, "KEEP"))
            .Should().Be(":0\r\n", "the refusal settled the notification; its token is no longer held");
        Text(h.Run(new HwClaimCommand(), At(30), "DEAD", "Q", "orders", "worker-2"))
            .Should().Be("*0\r\n", "a settled notification is never handed out again");
        Dead(h).Should().Be(1, "a refusal keeps the dead letter for an operator");
    }

    [Fact]
    public void RequeueAfter_IsMeasuredFromNow_NotFromTheEpoch()
    {
        using var h = OneDeadLetter();
        var token = ClaimDeadToken(h, At(11));

        Text(h.Run(new HwDlqCommand(), At(11), "RESOLVE", token, "REQUEUE", "AFTER", "5000"))
            .Should().Be("*2\r\n$4\r\ndone\r\n$8\r\nrequeued\r\n");

        Text(h.Run(new HwClaimCommand(), At(15), "Q", "orders", "node-1")).Should().Be("*0\r\n", "not due until now + 5 s");
        CommandHarness.Claims(h.Run(new HwClaimCommand(), At(16.5), "Q", "orders", "node-1"))
            .Should().ContainSingle(c => c.Id == "ord-1", "due at now + 5 s");
    }

    [Fact]
    public void Peek_ReportsTheExactRequeueCount()
    {
        using var h = OneDeadLetter(Envelope.WithLineage(Envelope.EncodeRpcEntry("ord-1"u8, "payload"u8, 1), 7));

        var reply = h.Run(new HwDlqCommand(), At(11), "PEEK", "Q", "orders");

        DuplicateSafetyTests.StatField(reply, "requeues").Should().Be("7");
    }

    [Fact]
    public void AReportedFailure_KeepsTheEntrysRequeueCount()
    {
        var h = new CommandHarness(options: new HighwayServerOptions { MaxDeliveryAttempts = 1, Lease = TimeSpan.FromSeconds(5) });
        using var _ = h;
        var box = Catalog.Ensure(h.Store, h.Boxes, h.Locks, BoxKeys.Kind.Queue, "orders", T0);
        using (var wb = new WorkBox(h.Store, h.Boxes, box, tracksBytes: true))
        {
            wb.Enqueue(Envelope.WithLineage(Envelope.EncodeRpcEntry("ord-1"u8, "payload"u8, 1), 3));
            wb.Commit();
        }
        var token = CommandHarness.Claims(h.Run(new HwClaimCommand(), T0, "Q", "orders", "node-1")).Single().Token;

        Text(h.Run(new HwFailCommand(), At(1), token, "System.TimeoutException", "slow")).Should().Be(":1\r\n");
        h.Run(new HwClaimCommand(), At(10), "Q", "orders", "node-2");   // dead-letters it

        var reply = h.Run(new HwDlqCommand(), At(11), "PEEK", "Q", "orders");
        DuplicateSafetyTests.StatField(reply, "requeues").Should().Be("3", "recording why it failed must not erase how often it was requeued");
        DuplicateSafetyTests.StatField(reply, "failureType").Should().Be("System.TimeoutException");
    }
}
