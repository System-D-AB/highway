using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Highway.Server;
using Highway.Server.Commands.Ported;
using Highway.Server.Commands.Runtime;
using Highway.Server.Storage;
using Highway.Server.Storage.Layout;
using Xunit;

namespace Highway.Server.Tests.Commands;

/// <summary>039 T4 — Batch 3 consumers: HW.QCLAIM, HW.DEQUEUE (R6).</summary>
public class ConsumersTests
{
    private static byte[] Send(CommandHarness h, string queue, string id, string payload)
    {
        return h.Run(new HwQSendCommand(), queue, id, payload);
    }

    // R6.2 — empty path
    [Fact]
    public void QClaim_Empty_ReturnsNullArray()
    {
        using var h = new CommandHarness();
        var reply = h.Run(new HwQClaimCommand(), "invoices", "node-A");
        CommandHarness.AsText(reply).Should().Be("*-1\r\n");
    }

    [Fact]
    public void QClaim_ClaimsHead_ReturnsIdAndPayload()
    {
        using var h = new CommandHarness();
        Send(h, "invoices", "m1", "hello");

        var reply = h.Run(new HwQClaimCommand(), "invoices", "node-A");
        // [messageId, payload]
        CommandHarness.AsText(reply).Should().Be("*2\r\n$2\r\nm1\r\n$5\r\nhello\r\n");

        // The message moved from live queue to the node's processing list.
        using var snap = h.Store.Snapshot();
        h.Store.ListLength(snap, HighwayKeyspace.ListPrefix(HighwayNames.Queue("invoices"))).Should().Be(0);
        h.Store.ListLength(snap, HighwayKeyspace.ListPrefix(HighwayNames.QueueProcessing("invoices", "node-A"))).Should().Be(1);
    }

    [Fact]
    public void QClaim_FifoOrder_AcrossClaims()
    {
        using var h = new CommandHarness();
        Send(h, "q", "m1", "a");
        Send(h, "q", "m2", "b");
        Send(h, "q", "m3", "c");

        FirstId(h.Run(new HwQClaimCommand(), "q", "n")).Should().Be("m1");
        FirstId(h.Run(new HwQClaimCommand(), "q", "n")).Should().Be("m2");
        FirstId(h.Run(new HwQClaimCommand(), "q", "n")).Should().Be("m3");
    }

    // R6.2 — promote-then-claim
    [Fact]
    public void QClaim_PromotesMaturedDelayed_ThenClaims()
    {
        using var h = new CommandHarness();
        var now = 5_000_000L;
        // Send deferred to a time already in the past relative to the claim's clock.
        h.Run(new HwQSendCommand(), 1_000_000L, "q", "m1", "hi", "AT", "2000000");

        // Claim at now=5M: 2M has matured, so it promotes and claims it.
        var reply = h.Run(new HwQClaimCommand(), now, "q", "node-A");
        FirstId(reply).Should().Be("m1");

        using var snap = h.Store.Snapshot();
        h.Store.SortedSetLength(snap, HighwayKeyspace.SortedSetPrefix(HighwayNames.QueueDelayed("q"))).Should().Be(0);
    }

    // R6.1 — claim under contention: disjoint messages
    [Fact]
    public async Task QClaim_UnderContention_TwoClaimants_GetDisjointMessages()
    {
        using var h = new CommandHarness();
        const int n = 200;
        for (var i = 0; i < n; i++) Send(h, "q", $"m{i}", $"p{i}");

        var claimed = new ConcurrentBag<string>();

        async Task ClaimLoop()
        {
            while (true)
            {
                var reply = h.Run(new HwQClaimCommand(), "q", "node-" + Thread.CurrentThread.ManagedThreadId);
                if (CommandHarness.AsText(reply).StartsWith("*-1")) break;
                claimed.Add(FirstId(reply));
                await Task.Yield();
            }
        }

        await Task.WhenAll(Task.Run(ClaimLoop), Task.Run(ClaimLoop));

        claimed.Should().HaveCount(n, "every message is claimed exactly once");
        claimed.Distinct().Should().HaveCount(n, "no message is claimed twice — the per-queue lock makes claims disjoint");
    }

    // Lease sweep: an expired claim is redelivered on the next claim.
    [Fact]
    public void QClaim_ExpiredLease_Redelivered()
    {
        var opts = new HighwayServerOptions { Lease = System.TimeSpan.FromMinutes(5) };
        using var h = new CommandHarness(options: opts);
        var t0 = 1_000_000_000L;

        Send(h, "q", "m1", "hi");
        FirstId(h.Run(new HwQClaimCommand(), t0, "q", "node-A")).Should().Be("m1");

        // node-A never acks. A later claim past the lease sweeps it back and re-claims it.
        var later = t0 + System.TimeSpan.FromMinutes(6).Ticks;
        var reply = h.Run(new HwQClaimCommand(), later, "q", "node-B");
        FirstId(reply).Should().Be("m1", "an expired lease is redelivered (at-least-once)");
    }

    // R2.3 — the culture bug cannot recur: promote under a comma-decimal culture.
    [Fact]
    public void QClaim_Promote_UnderCommaDecimalCulture_Works()
    {
        var original = Thread.CurrentThread.CurrentCulture;
        try
        {
            // A culture where a large number formats with a comma (the 6,39E+17 bug 037 records).
            Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");

            using var h = new CommandHarness();
            h.Run(new HwQSendCommand(), 1_000_000L, "q", "m1", "hi", "AT", "2000000");

            var reply = h.Run(new HwQClaimCommand(), 5_000_000L, "q", "node-A");
            FirstId(reply).Should().Be("m1",
                "the score is bytes, not a culture-formatted double — the promotion path is culture-immune");
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = original;
        }
    }

    // ---- HW.DEQUEUE (RPC) ----

    [Fact]
    public void Dequeue_Empty_ReturnsNullArray()
    {
        using var h = new CommandHarness();
        CommandHarness.AsText(h.Run(new HwDequeueCommand(), "orders", "node-A")).Should().Be("*-1\r\n");
    }

    [Fact]
    public void Dequeue_ClaimsRpcRequest()
    {
        using var h = new CommandHarness();
        h.Run(new HwCallCommand(), "orders", "req-1", "body");

        var reply = h.Run(new HwDequeueCommand(), "orders", "node-A");
        CommandHarness.AsText(reply).Should().Be("*2\r\n$5\r\nreq-1\r\n$4\r\nbody\r\n");
    }

    // -- helpers: parse the first bulk string out of a *2 array reply --

    private static string FirstId(byte[] reply)
    {
        var text = Encoding.UTF8.GetString(reply);
        // *2\r\n$<len>\r\n<id>\r\n$<len>\r\n<payload>\r\n — extract the first bulk string body.
        var parts = text.Split("\r\n");
        // parts[0]=*2, parts[1]=$len, parts[2]=id
        return parts.Length >= 3 ? parts[2] : text;
    }
}
