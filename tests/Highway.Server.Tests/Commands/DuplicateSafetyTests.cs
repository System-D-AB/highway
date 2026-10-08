using System.Text;
using FluentAssertions;
using Highway.Abstractions.Observability;
using Highway.Server.Commands.Ported;
using Highway.Server.Commands.Runtime;
using Highway.Server.Storage.Boxes;
using Xunit;

namespace Highway.Server.Tests.Commands;

/// <summary>
/// 074 (ported from 2.5.6 and 2.6.0): a retried <c>HW.CALL</c>, <c>HW.SEND</c> or <c>HW.PUBLISH … ID</c> is
/// acknowledged without being stored again. Ids are remembered only after the commit, so a refused or failed
/// one is never suppressed on retry; a dead-lettered call stops suppressing its re-send.
/// </summary>
public class DuplicateSafetyTests
{
    private static readonly long T0 = new DateTime(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc).Ticks;

    private static CommandHarness Harness(HighwayServerOptions? options = null) => new(options: options)
    {
        Calls = new RecentIds<byte>(TimeSpan.FromMinutes(10)),
        Sends = new RecentIds<byte>(TimeSpan.FromMinutes(10)),
        Publishes = new RecentIds<long>(TimeSpan.FromMinutes(10)),
    };

    /// <summary>Messages a box holds (ready + delayed), or 0 when it does not exist.</summary>
    private static long Held(CommandHarness h, BoxKeys.Kind kind, string name)
    {
        var rt = new BoxRuntime();
        using var batch = h.Store.NewBatch();
        if (Catalog.Find(h.Store, batch, rt, kind, name) is not { } box) return 0;
        using var wb = new WorkBox(h.Store, rt, box, tracksBytes: kind != BoxKeys.Kind.Service, batch);
        return wb.Ready + wb.Delayed;
    }

    private static long Dead(CommandHarness h, string service)
    {
        var rt = new BoxRuntime();
        using var batch = h.Store.NewBatch();
        if (Catalog.Find(h.Store, batch, rt, BoxKeys.Kind.Service, service) is not { } box) return 0;
        using var wb = new WorkBox(h.Store, rt, box, tracksBytes: false, batch);
        return wb.Dead;
    }

    private static void Subscribe(CommandHarness h, string node, string channel, string group)
        => CommandHarness.AsText(h.Run(new HwSessionCommand(), T0, node,
            $$"""{"subscriptions":[{"channel":"{{channel}}","group":"{{group}}"}]}""")).Should().Be("+OK\r\n");

    /// <summary>The value of one field in an <c>HW.STATS</c> field/value reply (bulk strings or integers).</summary>
    internal static string StatField(byte[] resp, string field)
    {
        var lines = Encoding.UTF8.GetString(resp).Split("\r\n");
        for (var i = 0; i + 2 < lines.Length; i++)
        {
            if (lines[i] != field) continue;
            var next = lines[i + 1];
            if (next.StartsWith(':')) return next[1..];
            if (next.StartsWith('$')) return lines[i + 2];
        }
        throw new InvalidOperationException($"field '{field}' not in HW.STATS reply");
    }

    private static string Text(byte[] b) => CommandHarness.AsText(b);

    // ---- HW.CALL ---------------------------------------------------------------------------------

    [Fact]
    public void ARepeatedCall_IsAcknowledged_ButQueuedOnce()
    {
        using var h = Harness();
        for (var i = 0; i < 12; i++)
            Text(h.Run(new HwCallCommand(), T0 + i, "prices", "req-1", "payload")).Should().Be("+OK\r\n");

        Held(h, BoxKeys.Kind.Service, "prices").Should().Be(1, "one call is one entry, however often it is re-sent");
        h.Doorbell.Rings.Should().HaveCount(1, "a suppressed copy wakes nobody");
        h.Recorder.Read("prices", DateTimeOffset.MinValue, DateTimeOffset.MaxValue, null, 100)
            .Count(e => e.EventType == HighwayEventType.RpcEnqueued).Should().Be(1);
    }

    [Fact]
    public void DistinctCalls_WithTheSameData_AreAllQueued()
    {
        using var h = Harness();
        h.Run(new HwCallCommand(), T0, "prices", "req-1", "same");
        h.Run(new HwCallCommand(), T0, "prices", "req-2", "same");

        Held(h, BoxKeys.Kind.Service, "prices").Should().Be(2);
    }

    [Fact]
    public void ACallThatFailedItsLastAttempt_StopsSuppressingItsReSend()
    {
        // 074: RPC is never dead-lettered; a request that fails its last attempt with time left is removed — and its
        // id forgotten, so a caller still waiting that re-sends it gets a fresh attempt.
        using var h = Harness(new HighwayServerOptions { MaxDeliveryAttempts = 1, Lease = TimeSpan.FromSeconds(5) });
        h.Run(new HwCallCommand(), T0, "prices", "req-1", "payload", "TIMEOUT", "3600000");

        var now = T0;
        for (var i = 0; i < 5 && !Ended(h); i++)
        {
            h.ClaimOne("SVC", "prices", "worker-1", now);
            now += TimeSpan.FromSeconds(10).Ticks;
        }
        Ended(h).Should().BeTrue("the request failed its last attempt");
        Dead(h, "prices").Should().Be(0, "RPC has no dead letters (074)");
        Held(h, BoxKeys.Kind.Service, "prices").Should().Be(0);

        Text(h.Run(new HwCallCommand(), now + 1, "prices", "req-1", "payload")).Should().Be("+OK\r\n");

        Held(h, BoxKeys.Kind.Service, "prices").Should().Be(1, "its original is gone, so a caller still waiting gets a fresh attempt");
    }

    private static bool Ended(CommandHarness h)
        => h.Recorder.Read("prices", DateTimeOffset.MinValue, DateTimeOffset.MaxValue, null, 100)
            .Any(e => e.EventType == HighwayEventType.RpcFailedFinal);

    [Fact]
    public void WithoutAFilter_RepeatedCallsAreQueued_AsBefore()
    {
        using var h = new CommandHarness();
        h.Run(new HwCallCommand(), T0, "prices", "req-1", "payload");
        h.Run(new HwCallCommand(), T0, "prices", "req-1", "payload");

        Held(h, BoxKeys.Kind.Service, "prices").Should().Be(2);
    }

    // ---- HW.SEND ---------------------------------------------------------------------------------

    [Fact]
    public void ARetriedSend_IsStoredOnce_ImmediateAndDelayed()
    {
        using var h = Harness();
        var later = (T0 + TimeSpan.FromMinutes(5).Ticks).ToString();
        for (var i = 0; i < 3; i++)
        {
            Text(h.Run(new HwSendCommand(), T0 + i, "invoices", "m-1", "p")).Should().Be("+OK\r\n");
            Text(h.Run(new HwSendCommand(), T0 + i, "invoices", "m-2", "p", "AT", later)).Should().Be("+OK\r\n");
        }

        Held(h, BoxKeys.Kind.Queue, "invoices").Should().Be(2, "one immediate and one delayed copy, each stored once");
        h.Sends!.Duplicates.Should().Be(4);
    }

    [Fact]
    public void ARefusedSend_IsNotRemembered_SoItsRetryIsStored()
    {
        var options = new HighwayServerOptions { MaxQueueBytes = 60 };
        using var h = Harness(options);

        Text(h.Run(new HwSendCommand(), T0, "invoices", "m-1", new string('x', 30))).Should().Be("+OK\r\n");
        Text(h.Run(new HwSendCommand(), T0 + 1, "invoices", "m-2", new string('y', 30))).Should().Contain("HW_QUEUE_FULL");

        options.MaxQueueBytes = 10_000;
        Text(h.Run(new HwSendCommand(), T0 + 2, "invoices", "m-2", new string('y', 30))).Should().Be("+OK\r\n");

        Held(h, BoxKeys.Kind.Queue, "invoices").Should().Be(2);
        h.Sends!.Duplicates.Should().Be(0);
    }

    // ---- HW.PUBLISH ID ---------------------------------------------------------------------------

    [Fact]
    public void ARetriedPublish_IsAnsweredWithTheOriginalCount_AndDeliveredOnce()
    {
        using var h = Harness();
        Subscribe(h, "node-1", "news", "billing");
        Subscribe(h, "node-2", "news", "audit");

        Text(h.Run(new HwPublishCommand(), T0, "news", "e", "ID", "pub-1")).Should().Be(":2\r\n");
        var rings = h.Doorbell.Rings.Count;
        Text(h.Run(new HwPublishCommand(), T0 + 1, "news", "e", "ID", "pub-1")).Should().Be(":2\r\n");

        Held(h, BoxKeys.Kind.Queue, "news@billing").Should().Be(1);
        Held(h, BoxKeys.Kind.Queue, "news@audit").Should().Be(1);
        h.Doorbell.Rings.Should().HaveCount(rings, "a suppressed publish wakes nobody");
    }

    [Fact]
    public void ARefusedPublish_IsNotRemembered_SoItsRetryIsDelivered()
    {
        var options = new HighwayServerOptions { MaxQueueBytes = 60 };
        using var h = Harness(options);
        Subscribe(h, "node-1", "news", "billing");

        Text(h.Run(new HwPublishCommand(), T0, "news", new string('x', 30), "ID", "pub-1")).Should().Be(":1\r\n");
        Text(h.Run(new HwPublishCommand(), T0 + 1, "news", new string('y', 30), "ID", "pub-2")).Should().Contain("HW_QUEUE_FULL");

        options.MaxQueueBytes = 10_000;
        Text(h.Run(new HwPublishCommand(), T0 + 2, "news", new string('y', 30), "ID", "pub-2")).Should().Be(":1\r\n");

        Held(h, BoxKeys.Kind.Queue, "news@billing").Should().Be(2);
    }

    [Fact]
    public void APublishWithAnEmptyId_IsRejected()
    {
        // Ported from 2.6.0 (PublishDuplicateTests).
        using var h = Harness();
        Text(h.Run(new HwPublishCommand(), T0, "news", "p", "ID", "")).Should().StartWith("-ERR HW_INVALID_ARG");
    }

    [Fact]
    public void APublishRepeatedAfterTheWindow_IsDeliveredAgain()
    {
        using var h = Harness();
        Subscribe(h, "node-1", "news", "billing");

        h.Run(new HwPublishCommand(), T0, "news", "p", "ID", "pub-1");
        h.Run(new HwPublishCommand(), T0 + TimeSpan.FromMinutes(11).Ticks, "news", "p", "ID", "pub-1");

        Held(h, BoxKeys.Kind.Queue, "news@billing").Should().Be(2, "the 10-minute window has passed: it is a new publish");
    }

    [Fact]
    public void AZeroPublishWindow_DisablesDeduplication()
    {
        using var h = new CommandHarness { Publishes = new RecentIds<long>(TimeSpan.Zero) };
        Subscribe(h, "node-1", "news", "billing");

        h.Run(new HwPublishCommand(), T0, "news", "p", "ID", "pub-1");
        h.Run(new HwPublishCommand(), T0 + 1, "news", "p", "ID", "pub-1");

        Held(h, BoxKeys.Kind.Queue, "news@billing").Should().Be(2);
    }

    [Fact]
    public void APublishWithoutId_IsNeverSuppressed()
    {
        using var h = Harness();
        Subscribe(h, "node-1", "news", "billing");
        h.Run(new HwPublishCommand(), T0, "news", "same");
        h.Run(new HwPublishCommand(), T0 + 1, "news", "same");

        Held(h, BoxKeys.Kind.Queue, "news@billing").Should().Be(2);
    }

    [Theory]
    [InlineData("ID", "pub-1", "AT", "1")]
    [InlineData("AT", "1", "ID", "pub-1")]
    public void PublishAcceptsIdAndAt_InEitherOrder(string k1, string v1, string k2, string v2)
    {
        using var h = Harness();
        Subscribe(h, "node-1", "news", "billing");
        Text(h.Run(new HwPublishCommand(), T0, "news", "e", k1, v1, k2, v2)).Should().StartWith(":");
        Text(h.Run(new HwPublishCommand(), T0 + 1, "news", "e", k1, v1, k2, v2)).Should().StartWith(":");
        h.Publishes!.Duplicates.Should().Be(1);
    }

    [Theory]
    [InlineData(new[] { "ID", "a", "ID", "b" }, "duplicate ID")]
    [InlineData(new[] { "AT", "1", "AT", "2" }, "duplicate AT")]
    [InlineData(new[] { "WHAT", "x" }, "expected AT or ID")]
    public void PublishRejectsRepeatedOrUnknownKeywords(string[] tail, string error)
    {
        using var h = Harness();
        Text(h.Run(new HwPublishCommand(), T0, ["news", "e", .. tail])).Should().Contain("HW_INVALID_ARG").And.Contain(error);
    }

    [Fact]
    public void TheSameId_OnAnotherQueueChannelOrService_IsNotADuplicate()
    {
        using var h = Harness();
        Subscribe(h, "node-1", "news", "billing");
        Subscribe(h, "node-2", "alerts", "billing");   // a session is a node's whole subscription set (070)

        h.Run(new HwSendCommand(), T0, "invoices", "id-1", "p");
        h.Run(new HwSendCommand(), T0, "refunds", "id-1", "p");
        h.Run(new HwPublishCommand(), T0, "news", "e", "ID", "id-1");
        h.Run(new HwPublishCommand(), T0, "alerts", "e", "ID", "id-1");
        h.Run(new HwCallCommand(), T0, "prices", "id-1", "p");
        h.Run(new HwCallCommand(), T0, "stock", "id-1", "p");

        Held(h, BoxKeys.Kind.Queue, "invoices").Should().Be(1);
        Held(h, BoxKeys.Kind.Queue, "refunds").Should().Be(1);
        Held(h, BoxKeys.Kind.Queue, "news@billing").Should().Be(1);
        Held(h, BoxKeys.Kind.Queue, "alerts@billing").Should().Be(1);
        Held(h, BoxKeys.Kind.Service, "prices").Should().Be(1);
        Held(h, BoxKeys.Kind.Service, "stock").Should().Be(1);
        (h.Sends!.Duplicates + h.Publishes!.Duplicates + h.Calls!.Duplicates).Should().Be(0);
    }

    [Fact]
    public void ARetriedDeferredPublish_IsHeldOnce_AndAnsweredZeroBothTimes()
    {
        using var h = Harness();
        Subscribe(h, "node-1", "news", "billing");
        var later = (T0 + TimeSpan.FromMinutes(5).Ticks).ToString();

        Text(h.Run(new HwPublishCommand(), T0, "news", "e", "AT", later, "ID", "pub-d")).Should().Be(":0\r\n");
        Text(h.Run(new HwPublishCommand(), T0 + 1, "news", "e", "ID", "pub-d", "AT", later)).Should().Be(":0\r\n");

        h.Publishes!.Duplicates.Should().Be(1);
        Text(h.Run(new HwPublishCommand(), T0 + TimeSpan.FromMinutes(6).Ticks, "news", "probe", "ID", "pub-p")).Should().Be(":1\r\n");
        // Once due, the deferred publish fans out exactly once next to the probe.
        Held(h, BoxKeys.Kind.Queue, "news@billing").Should().Be(2);
    }

    // ---- observability -----------------------------------------------------------------------------

    [Fact]
    public void Duplicates_AreNotRecorded_AndStatsCountThemExactly()
    {
        using var h = Harness();
        Subscribe(h, "node-1", "news", "billing");

        for (var i = 0; i < 3; i++) h.Run(new HwSendCommand(), T0 + i, "invoices", "m-1", "p");          // 2 duplicates
        for (var i = 0; i < 4; i++) h.Run(new HwPublishCommand(), T0 + i, "news", "e", "ID", "pub-1");   // 3 duplicates
        for (var i = 0; i < 2; i++) h.Run(new HwCallCommand(), T0 + i, "prices", "r-1", "p");             // 1 duplicate

        h.Recorder.Read("invoices", DateTimeOffset.MinValue, DateTimeOffset.MaxValue, null, 100)
            .Count(e => e.EventType == HighwayEventType.QueueSent).Should().Be(1);
        h.Recorder.Read("news", DateTimeOffset.MinValue, DateTimeOffset.MaxValue, null, 100)
            .Count(e => e.EventType == HighwayEventType.Published).Should().Be(1);

        var stats = h.Run(new HwStatsCommand());
        StatField(stats, "sendDuplicates").Should().Be("2");
        StatField(stats, "publishDuplicates").Should().Be("3");
        StatField(stats, "callDuplicates").Should().Be("1");
    }
}
