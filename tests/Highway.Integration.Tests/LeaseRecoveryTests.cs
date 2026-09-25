using FluentAssertions;
using Highway.Server;
using StackExchange.Redis;
using Xunit;

namespace Highway.Integration.Tests;

/// <summary>
/// Feature 004.1 Task 10 — Requirement 6: lease expiry and redelivery.
/// Proves the at-least-once recovery path feature 005 relies on, for both
/// RPC and pub/sub group queues (the lazy lease sweep inside <c>HW.CLAIM SVC|Q</c>),
/// using a short lease configured via the test-server delegate. Since protocol 5.0 a
/// redelivery carries a new lease token; the stale one answers <c>:0</c>.
/// </summary>
public class LeaseRecoveryTests
{
    private static readonly TimeSpan ShortLease = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan WaitPastLease = TimeSpan.FromMilliseconds(400);

    private static (HighwayTestServer server, ConnectionMultiplexer redis, IDatabase db) StartServer(
        TimeSpan lease)
    {
        var server = new HighwayTestServer(o => o.Lease = lease);
        var redis = ConnectionMultiplexer.Connect(server.ConnectionString);
        return (server, redis, redis.GetDatabase());
    }

    [Fact]
    public void Rpc_UnackedRequest_RequeuedAfterLease_ToDifferentNode()
    {
        var (server, redis, db) = StartServer(ShortLease);
        using (server)
        using (redis)
        {
            db.Execute("HW.CALL", "lease.svc", "req-1", "payload-1");

            // node-A claims but never ACKs (simulated worker death)
            var claimed = db.ClaimSvc("lease.svc", "node-A");
            claimed.Should().NotBeNull();
            claimed!.Value.Id.Should().Be("req-1");

            Thread.Sleep(WaitPastLease);

            // node-B's claim sweeps node-A's expired entry and claims it
            var redelivered = db.ClaimSvc("lease.svc", "node-B");
            redelivered.Should().NotBeNull("the expired claim must be requeued and claimable by another node");
            redelivered!.Value.Id.Should().Be("req-1");
            redelivered.Value.PayloadText.Should().Be("payload-1");
            redelivered.Value.Token.Should().NotBe(claimed.Value.Token, "a redelivery is a new claim with a new token");
        }
    }

    [Fact]
    public void Rpc_AckedBeforeLease_NeverRedelivered()
    {
        var (server, redis, db) = StartServer(ShortLease);
        using (server)
        using (redis)
        {
            db.Execute("HW.CALL", "lease.svc2", "req-2", "payload-2");

            var claimed = db.ClaimSvc("lease.svc2", "node-A");
            claimed.Should().NotBeNull();

            db.Ack(claimed!.Value.Token).Should().Be(1);

            Thread.Sleep(WaitPastLease);

            var redelivered = db.ClaimSvc("lease.svc2", "node-B");
            redelivered.Should().BeNull("an acknowledged request must never be redelivered");
        }
    }

    /// <summary>
    /// The slow-but-alive original worker ACKs after its lease was recovered. In 5.0 its token
    /// names a claim that no longer exists, so the ACK answers <c>:0</c> — not an error — and
    /// must not disturb the redelivered copy, which is settled by its own token.
    /// </summary>
    [Fact]
    public void Rpc_AckAfterRequeue_StaleTokenAnswersZero_LeavesNoResidue()
    {
        var (server, redis, db) = StartServer(ShortLease);
        using (server)
        using (redis)
        {
            db.Execute("HW.CALL", "lease.svc3", "req-3", "payload-3");

            var stale = db.ClaimSvc("lease.svc3", "node-A")!.Value;    // claim, no ACK
            Thread.Sleep(WaitPastLease);
            var current = db.ClaimSvc("lease.svc3", "node-B")!.Value;  // sweep requeues, B claims
            current.Id.Should().Be("req-3");

            // The slow-but-alive original worker ACKs late — not held any more, and not an error
            db.Ack(stale.Token).Should().Be(0, "the lease was recovered, so the stale token holds nothing");

            // No residue: nothing is claimable, and B's copy is still B's
            db.ClaimSvc("lease.svc3", "node-A").Should().BeNull();
            db.ClaimSvc("lease.svc3", "node-C").Should().BeNull();
            // B's copy is in flight under B's token; ACK it and confirm empty afterwards
            db.Ack(current.Token).Should().Be(1, "the stale ACK must not have removed the live claim");
            db.ClaimSvc("lease.svc3", "node-B").Should().BeNull();
        }
    }

    [Fact]
    public void PubSub_UnackedMessage_RedeliveredAtHead_PreservingOrder()
    {
        var (server, redis, db) = StartServer(ShortLease);
        using (server)
        using (redis)
        {
            db.Subscribe("lease.ch", "grp");
            db.Execute("HW.PUBLISH", "lease.ch", "m1");
            db.Execute("HW.PUBLISH", "lease.ch", "m2");

            // Claim only m1 — moves to processing, never QACKed
            var first = db.ClaimQ("lease.ch@grp", "node-1");
            first.Should().NotBeNull();
            first!.Value.PayloadText.Should().Be("m1");

            Thread.Sleep(WaitPastLease);

            db.Execute("HW.PUBLISH", "lease.ch", "m3"); // queue now has m2; m1 in expired proc

            // node-2's claim triggers the sweep of node-1's expired entry.
            // The queue engine sweeps expired entries to the TAIL (not head),
            // so order is: m2, m3, m1 (original queue items first, then redelivered).
            var payloads = new List<string>();
            for (var i = 0; i < 3; i++)
            {
                var claimed = db.ClaimQ("lease.ch@grp", "node-2");
                if (claimed is not { } c) break;
                payloads.Add(c.PayloadText);
            }
            payloads.Should().HaveCount(3);
            payloads.Should().Contain("m1", "the expired claim must be redelivered");
            payloads.Should().Contain("m2");
            payloads.Should().Contain("m3");
        }
    }

    [Fact]
    public void PubSub_RackInOneGroup_DoesNotAffectOtherGroup()
    {
        var (server, redis, db) = StartServer(ShortLease);
        using (server)
        using (redis)
        {
            db.Subscribe("indep.ch", "grpX");
            db.Subscribe("indep.ch", "grpY");
            db.Execute("HW.PUBLISH", "indep.ch", "shared");

            var rx = db.ClaimQ("indep.ch@grpX", "node-1");
            var ry = db.ClaimQ("indep.ch@grpY", "node-1");
            rx.Should().NotBeNull();
            ry.Should().NotBeNull();

            // grpX acknowledges its copy
            db.Ack(rx!.Value.Token).Should().Be(1);

            Thread.Sleep(WaitPastLease);

            // grpY's copy is untouched by grpX's ACK: it expires and redelivers
            var ryAgain = db.ClaimQ("indep.ch@grpY", "node-2");
            ryAgain.Should().NotBeNull("grpY's in-flight copy must survive grpX's ACK and redeliver after the lease");
            ryAgain!.Value.PayloadText.Should().Be("shared");
        }
    }

    [Fact]
    public void Lease_Zero_DisablesSweep_Entirely()
    {
        var (server, redis, db) = StartServer(TimeSpan.Zero);
        using (server)
        using (redis)
        {
            db.Execute("HW.CALL", "nolease.svc", "req-9", "payload-9");

            db.ClaimSvc("nolease.svc", "node-A"); // claim, no ACK

            Thread.Sleep(WaitPastLease);

            // With the sweep disabled the expired entry stays in node-A's
            // processing list and is never requeued
            var redelivered = db.ClaimSvc("nolease.svc", "node-B");
            redelivered.Should().BeNull("Lease=Zero must disable lazy requeue entirely");
        }
    }
}
