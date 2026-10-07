using FluentAssertions;
using Highway.Abstractions.Observability;
using Highway.Server.Commands.Ported;
using Highway.Server.Commands.Runtime;
using Highway.Server.Internal;
using Highway.Server.Storage.Boxes;
using Xunit;

namespace Highway.Server.Tests.Commands;

/// <summary>
/// 074 Part A, T3–T4: a request is never handed out, or handed out again, after its caller's deadline; a claim's
/// lease ends with the deadline; a request that fails its last attempt is removed, never dead-lettered.
/// </summary>
public class RpcDeadlineLifecycleTests
{
    private static readonly long T0 = new DateTime(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc).Ticks;
    private static long At(double seconds) => T0 + TimeSpan.FromSeconds(seconds).Ticks;
    private static string Text(byte[] b) => CommandHarness.AsText(b);

    private static CommandHarness Harness(HighwayServerOptions? options = null) => new(options: options)
    {
        Calls = new RecentIds<byte>(TimeSpan.FromHours(1)),
    };

    private static void Call(CommandHarness h, double at, string id, int timeoutMs)
        => Text(h.Run(new HwCallCommand(), At(at), "prices", id, "p", "TIMEOUT", timeoutMs.ToString())).Should().Be("+OK\r\n");

    private static List<CommandHarness.Claim> Claim(CommandHarness h, double at, string node = "w-1")
        => CommandHarness.Claims(h.Run(new HwClaimCommand(), At(at), "SVC", "prices", node));

    private static List<HighwayEvent> Events(CommandHarness h, HighwayEventType type)
        => [.. h.Recorder.Read("prices", DateTimeOffset.MinValue, DateTimeOffset.MaxValue, null, 500).Where(e => e.EventType == type)];

    private static (long Ready, long InFlight, long Dead) Counts(CommandHarness h)
    {
        using var batch = h.Store.NewBatch();
        if (Catalog.Find(h.Store, batch, h.Boxes, BoxKeys.Kind.Service, "prices") is not { } box) return (0, 0, 0);
        using var wb = new WorkBox(h.Store, h.Boxes, box, tracksBytes: false, batch);
        return (wb.Ready + wb.Delayed, wb.InFlight, wb.Dead);
    }

    // ---- T3: the claim ------------------------------------------------------------------------------

    [Fact]
    public void AnExpiredRequest_IsNeverHandedOut_AndIsRecordedAsExpiredInTheQueue()
    {
        using var h = Harness();
        Call(h, 0, "r-1", 1000);

        Claim(h, 2).Should().BeEmpty("its caller stopped waiting a second ago");

        Counts(h).Should().Be((0, 0, 0), "removed, not parked");
        Events(h, HighwayEventType.RpcExpired).Should().ContainSingle(e => e.RequestId == "r-1" && e.ErrorCode == "queue");
    }

    [Fact]
    public void ARequestWithTooLittleTimeLeft_IsDropped_AndItsIdForgotten()
    {
        using var h = Harness();   // MinRemainingToStart = 100 ms
        Call(h, 0, "r-1", 1000);

        Claim(h, 0.95).Should().BeEmpty("50 ms is not enough time to do the work");

        Call(h, 0.96, "r-1", 5000);   // the caller (still waiting) re-sends: the id was forgotten, so it is queued
        Counts(h).Ready.Should().Be(1);
    }

    [Fact]
    public void ALiveRequest_CarriesItsRemainingTime()
    {
        using var h = Harness();
        Call(h, 0, "r-1", 30_000);

        var row = Claim(h, 10).Should().ContainSingle().Subject;

        row.RemainingMs.Should().Be(20_000, "measured by the broker at the claim, relative so no clocks are compared");
    }

    [Fact]
    public void AClaimStopsAtTheExpiryBound_AndRingsSoTheWorkerComesStraightBack()
    {
        using var h = Harness(new HighwayServerOptions { MaxExpiredPerClaim = 2 });
        for (var i = 0; i < 5; i++) Call(h, 0, $"old-{i}", 1000);
        Call(h, 0, "live", 60_000);
        var rings = h.Doorbell.Rings.Count;

        Claim(h, 5).Should().BeEmpty("the bound stopped it after two");
        h.Doorbell.Rings.Count.Should().BeGreaterThan(rings, "a stopped claim rings the service doorbell");

        var got = Claim(h, 5);
        got = got.Count > 0 ? got : Claim(h, 5);
        got.Should().ContainSingle(c => c.Id == "live", "the expired tail is cleared two at a time, then the live request is served");
        Events(h, HighwayEventType.RpcExpired).Should().HaveCount(5);
    }

    [Fact]
    public void AClaimsLease_EndsWithTheDeadlinePlusGrace_NotTheFullLease()
    {
        using var h = Harness(new HighwayServerOptions { Lease = TimeSpan.FromMinutes(5), RpcLeaseGrace = TimeSpan.FromSeconds(5) });
        Call(h, 0, "r-1", 100_000);
        Claim(h, 0).Should().ContainSingle();

        Claim(h, 104, "w-2").Should().BeEmpty();
        Counts(h).InFlight.Should().Be(1, "within deadline + grace the claim holds");

        Claim(h, 106, "w-2").Should().BeEmpty();
        Counts(h).Should().Be((0, 0, 0), "at deadline + grace the lease lapses — not five minutes later — and the request has expired");
        Events(h, HighwayEventType.RpcExpired).Should().ContainSingle(e => e.ErrorCode == "redelivery");
    }

    [Fact]
    public void RenewingAClaim_NeverCarriesItsLeasePastTheDeadline()
    {
        using var h = Harness(new HighwayServerOptions { Lease = TimeSpan.FromMinutes(5), RpcLeaseGrace = TimeSpan.FromSeconds(5) });
        Call(h, 0, "r-1", 100_000);
        var token = Claim(h, 0).Single().Token;

        Text(h.Run(new HwTouchCommand(), At(100), token)).Should().Be(":1\r\n");

        Claim(h, 106, "w-2");
        Counts(h).InFlight.Should().Be(0, "a renewal at 100 s must not hold the claim to 400 s");
    }

    // ---- T4: redelivery ----------------------------------------------------------------------------

    [Fact]
    public void ARedeliveryWithTimeLeft_KeepsTheOriginalDeadline()
    {
        using var h = Harness(new HighwayServerOptions { Lease = TimeSpan.FromSeconds(5), RpcLeaseGrace = TimeSpan.Zero });
        Call(h, 0, "r-1", 60_000);
        Claim(h, 0).Should().ContainSingle();

        var again = Claim(h, 10, "w-2").Should().ContainSingle().Subject;

        again.RemainingMs.Should().Be(50_000, "redelivered with its caller's deadline, not a fresh one");
    }

    [Fact]
    public void ARequestThatFailsItsLastAttempt_IsRemoved_WithItsFailure_NeverDeadLettered()
    {
        using var h = Harness(new HighwayServerOptions { Lease = TimeSpan.FromSeconds(5), MaxDeliveryAttempts = 1, RpcLeaseGrace = TimeSpan.Zero });
        Call(h, 0, "r-1", 600_000);
        var token = Claim(h, 0).Single().Token;
        Text(h.Run(new HwFailCommand(), At(1), token, "System.TimeoutException", "db slow")).Should().Be(":1\r\n");
        Claim(h, 10, "w-2").Should().ContainSingle("attempt 1 of 1 left: redelivered");
        Claim(h, 20, "w-3").Should().BeEmpty();

        Counts(h).Should().Be((0, 0, 0), "RPC has no dead letters (D2)");
        Events(h, HighwayEventType.RpcFailedFinal).Should().ContainSingle()
            .Which.ErrorCode.Should().Contain("System.TimeoutException").And.Contain("db slow");
    }

    [Fact]
    public void ARetryBackoffThatWouldLandPastTheDeadline_EndsTheRequestAsExpired()
    {
        using var h = Harness(new HighwayServerOptions { Lease = TimeSpan.FromSeconds(5), RpcLeaseGrace = TimeSpan.Zero });
        Text(h.Run(new HwSessionCommand(), At(0), "w-1",
            """{"policies":[{"kind":"SVC","name":"prices","maxAttempts":5,"backoff":"fixed:10s","maxRequeues":3}]}""")).Should().Be("+OK\r\n");
        Call(h, 0, "r-1", 12_000);
        Claim(h, 0).Should().ContainSingle();

        Claim(h, 6, "w-2").Should().BeEmpty();   // lease lapsed at 5 s; retry at 16 s would be past the 12 s deadline

        Counts(h).Should().Be((0, 0, 0));
        Events(h, HighwayEventType.RpcExpired).Should().ContainSingle(e => e.ErrorCode == "redelivery");
    }

    [Fact]
    public void ADepartingWorker_ReturnsOnlyRequestsSomeoneStillWaitsFor()
    {
        using var h = Harness(new HighwayServerOptions { Lease = TimeSpan.FromMinutes(5) });
        Text(h.Run(new HwSessionCommand(), At(0), "w-1", """{"catalog":{"services":[{"name":"prices"}]}}""")).Should().Be("+OK\r\n");
        Call(h, 0, "short", 2_000);
        Call(h, 0, "long", 600_000);
        CommandHarness.Claims(h.Run(new HwClaimCommand(), At(0), "SVC", "prices", "w-1", "COUNT", "2")).Should().HaveCount(2);

        h.Run(new HwHeartbeatCommand(), At(5), "w-1", "BYE");

        Counts(h).Ready.Should().Be(1, "only the request whose caller still waits goes back");
        Claim(h, 5, "w-2").Should().ContainSingle(c => c.Id == "long");
        Events(h, HighwayEventType.RpcExpired).Should().ContainSingle(e => e.RequestId == "short" && e.ErrorCode == "redelivery");
    }

    // ---- T6: HW.CANCEL and the late reply ----------------------------------------------------------

    [Fact]
    public void Cancel_WithdrawsAQueuedRequest_OnlyThatOne_AndForgetsItsId()
    {
        using var h = Harness();
        Call(h, 0, "a", 30_000);
        Call(h, 0, "b", 30_000);
        Call(h, 0, "c", 30_000);

        Text(h.Run(new HwCancelCommand(), At(1), "prices", "b")).Should().Be(":1\r\n");

        Claim(h, 2).Should().ContainSingle(c => c.Id == "a");
        Claim(h, 2).Should().ContainSingle(c => c.Id == "c", "the withdrawn request is never handed out");
        Events(h, HighwayEventType.RpcCancelled).Should().ContainSingle(e => e.RequestId == "b");
        Call(h, 3, "b", 30_000);
        Counts(h).Ready.Should().Be(1, "its id was forgotten: a new call with it is new work");
    }

    [Fact]
    public void Cancel_OfAClaimedOrUnknownRequest_IsZero_AndInterruptsNothing()
    {
        using var h = Harness();
        Call(h, 0, "a", 30_000);
        Claim(h, 0).Should().ContainSingle();

        Text(h.Run(new HwCancelCommand(), At(1), "prices", "a")).Should().Be(":0\r\n", "already claimed: its worker sees the deadline");
        Text(h.Run(new HwCancelCommand(), At(1), "prices", "nope")).Should().Be(":0\r\n");
        Text(h.Run(new HwCancelCommand(), At(1), "unknown-service", "a")).Should().Be(":0\r\n");
        Counts(h).InFlight.Should().Be(1);
    }

    [Fact]
    public void AReplyInTime_IsWritten_AndALateOne_IsDiscarded()
    {
        using var h = Harness();
        Call(h, 0, "on-time", 10_000);
        Call(h, 0, "late", 10_000);
        var rows = CommandHarness.Claims(h.Run(new HwClaimCommand(), At(0), "SVC", "prices", "w-1", "COUNT", "2"));
        var onTime = rows.Single(r => r.Id == "on-time").Token;
        var late = rows.Single(r => r.Id == "late").Token;

        Text(h.Run(new HwReplyCommand(), At(5), "on-time", "answer", "TOKEN", onTime)).Should().Be(":1\r\n");
        Text(h.Run(new HwReplyCommand(), At(11), "late", "answer", "TOKEN", late)).Should().Be(":0\r\n", "its caller stopped waiting at 10 s");

        Text(h.Run(new HwResultCommand(), At(12), "on-time")).Should().Contain("answer");
        Text(h.Run(new HwResultCommand(), At(12), "late")).Should().NotContain("answer", "a late reply is never written");
        h.Recorder.Read(HwReplyCommand.RecorderName, DateTimeOffset.MinValue, DateTimeOffset.MaxValue, null, 10)
            .Should().ContainSingle(e => e.EventType == HighwayEventType.RpcReplyLate && e.RequestId == "late");
    }

    [Fact]
    public void AReplyWithoutAToken_IsWrittenAsBefore()
    {
        using var h = Harness();
        Text(h.Run(new HwReplyCommand(), At(0), "r-1", "answer")).Should().Be("+OK\r\n");
    }

    [Fact]
    public void ARequestLeavingTheQueue_LeavesNoCancelIndexBehind()
    {
        // The id → position index (HW.CANCEL) must not outlive its request, or it grows without bound.
        using var h = Harness();
        Call(h, 0, "claimed", 30_000);
        Call(h, 0, "expires", 1_000);
        Call(h, 0, "withdrawn", 30_000);
        h.Run(new HwCancelCommand(), At(0), "prices", "withdrawn");

        Claim(h, 0.5).Should().ContainSingle(c => c.Id == "claimed");
        Claim(h, 2).Should().BeEmpty("the other one expired");

        using var batch = h.Store.NewBatch();
        var box = Catalog.Find(h.Store, batch, h.Boxes, BoxKeys.Kind.Service, "prices")!.Value;
        var index = BoxKeys.Space(box, BoxKeys.RpcIndex);
        h.Store.Scan(batch, index, index, 10).Should().BeEmpty("claimed, expired and withdrawn requests are all unindexed");
    }

    [Fact]
    public void AReplyForAClaimNoLongerHeld_IsDiscarded()
    {
        // The claim was swept as expired (or redelivered to another worker): this answer is not the one that counts.
        using var h = Harness(new HighwayServerOptions { Lease = TimeSpan.FromMinutes(5), RpcLeaseGrace = TimeSpan.FromSeconds(1) });
        Call(h, 0, "r-1", 2_000);
        var token = Claim(h, 0).Single().Token;
        Claim(h, 4, "w-2");   // deadline 2 s + grace 1 s: the claim is swept

        Text(h.Run(new HwReplyCommand(), At(5), "r-1", "answer", "TOKEN", token)).Should().Be(":0\r\n");
        Text(h.Run(new HwResultCommand(), At(5), "r-1")).Should().NotContain("answer");
    }

    [Fact]
    public void TheOutcomeSummary_LogsAnOutagesTail_WithoutWaitingForAnotherEvent()
    {
        var log = new RpcDeadlineTests.CapturingLogger();
        var outcomes = new RpcOutcomeLog();
        var interval = TimeSpan.FromMilliseconds(200);
        var now = DateTime.UtcNow.Ticks;

        outcomes.Expired(log, "prices", "queue", 1, now, interval);        // logged at once
        outcomes.Expired(log, "prices", "queue", 4, now + 1, interval);    // accumulates

        for (var i = 0; i < 50 && log.Entries.Count < 2; i++) Thread.Sleep(20);
        lock (log.Entries) log.Entries.Should().HaveCount(2, "the tail is logged by the timer once its interval passes");
        lock (log.Entries) log.Entries[1].Text.Should().Contain("4 request(s) expired");
    }
}
