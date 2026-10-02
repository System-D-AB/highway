using System.Text;
using FluentAssertions;
using Highway.Abstractions;
using Highway.Server.Commands.Ported;
using Highway.Server.Internal;
using Highway.Server.Storage.Boxes;
using Highway.Server.Tests.Commands;
using Xunit;

namespace Highway.Server.Tests.Storage;

public class RetryPolicyWorkBoxTests
{
    [Fact]
    public void WorkBox_SetAndGetRetryPolicy_RoundTripsAllFields()
    {
        using var h = new CommandHarness();
        var box = Catalog.Ensure(h.Store, h.Boxes, h.Locks, BoxKeys.Kind.Queue, "test.retry.q", 1000);

        var policy = new RetryPolicy(6, BackoffKind.Exponential, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(2), true, 4);

        using (var wb = new WorkBox(h.Store, h.Boxes, box, tracksBytes: true))
        {
            wb.SetRetryPolicy(policy);
            wb.Commit();
        }

        using (var wb = new WorkBox(h.Store, h.Boxes, box, tracksBytes: true))
        {
            var loaded = wb.GetRetryPolicy();
            loaded.MaxAttempts.Should().Be(6);
            loaded.BackoffKind.Should().Be(BackoffKind.Exponential);
            loaded.MinBackoff.Should().Be(TimeSpan.FromSeconds(1));
            loaded.MaxBackoff.Should().Be(TimeSpan.FromMinutes(2));
            loaded.Jitter.Should().BeTrue();
            loaded.MaxRequeues.Should().Be(4);
        }
    }

    [Fact]
    public void SweepLeases_WithRetryBackoff_ReschedulesToDelayedSpace()
    {
        using var h = new CommandHarness();
        var box = Catalog.Ensure(h.Store, h.Boxes, h.Locks, BoxKeys.Kind.Queue, "test.delayed.sweep", 1000);

        // Fixed 5-second backoff, max 3 attempts
        var policy = new RetryPolicy(3, BackoffKind.Fixed, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), false, 3);
        using (var wb = new WorkBox(h.Store, h.Boxes, box, tracksBytes: true))
        {
            wb.SetRetryPolicy(policy);
            var entry = Envelope.EncodeRpcEntry(Encoding.UTF8.GetBytes("msg-1"), Encoding.UTF8.GetBytes("payload-1"), 0);
            wb.Enqueue(entry);
            wb.Commit();
        }

        // 1. Claim entry at t = 10_000_000
        var t = 10_000_000L;
        using (var wb = new WorkBox(h.Store, h.Boxes, box, tracksBytes: true))
        {
            var claimed = wb.Claim("node-1", t, 1, "test.delayed.sweep");
            claimed.Should().HaveCount(1);
            wb.Commit();
        }

        // 2. Lease expires: t + 10s. SweepLeases runs at t + 10s
        var leaseExpiry = t + TimeSpan.FromSeconds(1).Ticks;
        var nowTicks = t + TimeSpan.FromSeconds(10).Ticks;
        using (var wb = new WorkBox(h.Store, h.Boxes, box, tracksBytes: true))
        {
            var dead = wb.SweepLeases(leaseExpiry, nowTicks, maxAttempts: 5, maxDeadEntries: 100);
            wb.Commit();

            // Not dead-lettered because attempt 1 <= MaxAttempts (3)
            dead.Should().BeEmpty();

            // Ready is empty because it was placed in Delayed space!
            wb.Ready.Should().Be(0);
            wb.InFlight.Should().Be(0);
            wb.Delayed.Should().Be(1);
        }

        // 3. Before due time (e.g. nowTicks + 2s), PromoteDue does nothing
        using (var wb = new WorkBox(h.Store, h.Boxes, box, tracksBytes: true))
        {
            var promoted = wb.PromoteDue(nowTicks + TimeSpan.FromSeconds(2).Ticks);
            promoted.Should().Be(0);
            wb.Ready.Should().Be(0);
            wb.Delayed.Should().Be(1);
            wb.Commit();
        }

        // 4. After due time (e.g. nowTicks + 6s >= nowTicks + 5s backoff), PromoteDue promotes to Ready!
        using (var wb = new WorkBox(h.Store, h.Boxes, box, tracksBytes: true))
        {
            var promoted = wb.PromoteDue(nowTicks + TimeSpan.FromSeconds(6).Ticks);
            promoted.Should().Be(1);
            wb.Ready.Should().Be(1);
            wb.Delayed.Should().Be(0);
            wb.Commit();
        }
    }

    [Fact]
    public void SweepLeases_WhenAttemptsExhausted_MovesToDeadLetter()
    {
        using var h = new CommandHarness();
        var box = Catalog.Ensure(h.Store, h.Boxes, h.Locks, BoxKeys.Kind.Queue, "test.exhaust.sweep", 1000);

        // Max 2 attempts, fixed backoff
        var policy = new RetryPolicy(2, BackoffKind.Fixed, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), false, 3);
        using (var wb = new WorkBox(h.Store, h.Boxes, box, tracksBytes: true))
        {
            wb.SetRetryPolicy(policy);
            // Already on attempt 2
            var entry = Envelope.EncodeRpcEntry(Encoding.UTF8.GetBytes("msg-ex"), Encoding.UTF8.GetBytes("payload-ex"), 2);
            wb.Enqueue(entry);
            wb.Commit();
        }

        var t = 10_000_000L;
        using (var wb = new WorkBox(h.Store, h.Boxes, box, tracksBytes: true))
        {
            wb.Claim("node-1", t, 1, "test.exhaust.sweep");
            wb.Commit();
        }

        // Sweep lease: attempt was 2, next becomes 3 > maxAttempts (2) -> dead lettered!
        using (var wb = new WorkBox(h.Store, h.Boxes, box, tracksBytes: true))
        {
            var dead = wb.SweepLeases(t + TimeSpan.FromSeconds(5).Ticks, t + TimeSpan.FromSeconds(10).Ticks, maxAttempts: 5, maxDeadEntries: 100);
            wb.Commit();

            dead.Should().HaveCount(1);
            dead[0].Id.Should().Be("msg-ex");
            dead[0].Attempts.Should().Be(3);
            wb.Dead.Should().Be(1);
            wb.Ready.Should().Be(0);
            wb.Delayed.Should().Be(0);
        }
    }

    [Fact]
    public void HwSession_DeclaresPolicies_AndLastDeclarationWins()
    {
        using var h = new CommandHarness();

        var sessionJson1 = """
            {
              "policies": [
                {
                  "kind": "Q",
                  "name": "orders.retry",
                  "maxAttempts": 4,
                  "backoff": "fixed:5s",
                  "maxRequeues": 2
                }
              ]
            }
            """;

        var res1 = CommandHarness.AsText(h.Run(new HwSessionCommand(), "node-1", sessionJson1));
        res1.Should().Contain("OK");

        var box = Catalog.Ensure(h.Store, h.Boxes, h.Locks, BoxKeys.Kind.Queue, "orders.retry", 1000);
        using (var wb = new WorkBox(h.Store, h.Boxes, box, tracksBytes: true))
        {
            var policy = wb.GetRetryPolicy();
            policy.MaxAttempts.Should().Be(4);
            policy.BackoffKind.Should().Be(BackoffKind.Fixed);
            policy.MaxRequeues.Should().Be(2);
        }

        // Second session updates policy (last declaration wins)
        var sessionJson2 = """
            {
              "policies": [
                {
                  "kind": "Q",
                  "name": "orders.retry",
                  "maxAttempts": 7,
                  "backoff": "exp:1s..30s jitter",
                  "maxRequeues": 5
                }
              ]
            }
            """;

        var res2 = CommandHarness.AsText(h.Run(new HwSessionCommand(), "node-2", sessionJson2));
        res2.Should().Contain("OK");

        using (var wb = new WorkBox(h.Store, h.Boxes, box, tracksBytes: true))
        {
            var policy = wb.GetRetryPolicy();
            policy.MaxAttempts.Should().Be(7);
            policy.BackoffKind.Should().Be(BackoffKind.Exponential);
            policy.MinBackoff.Should().Be(TimeSpan.FromSeconds(1));
            policy.MaxBackoff.Should().Be(TimeSpan.FromSeconds(30));
            policy.Jitter.Should().BeTrue();
            policy.MaxRequeues.Should().Be(5);
        }
    }
}
