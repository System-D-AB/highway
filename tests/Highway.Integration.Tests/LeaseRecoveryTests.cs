using FluentAssertions;
using Highway.Server;
using StackExchange.Redis;
using Xunit;

namespace Highway.Integration.Tests;

/// <summary>
/// Feature 004.1 Task 10 — Requirement 6: lease expiry and redelivery.
/// Proves the at-least-once recovery path feature 005 relies on, for both
/// RPC (lazy sweep in HW.DEQUEUE) and pub/sub (lazy reap in HW.RECEIVE),
/// using a short lease configured via the test-server delegate.
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
            var claimed = db.Execute("HW.DEQUEUE", "lease.svc", "node-A");
            claimed.IsNull.Should().BeFalse();
            ((string)((RedisResult[])claimed!)[0]!).Should().Be("req-1");

            Thread.Sleep(WaitPastLease);

            // node-B's dequeue sweeps node-A's expired entry and claims it
            var redelivered = db.Execute("HW.DEQUEUE", "lease.svc", "node-B");
            redelivered.IsNull.Should().BeFalse("the expired claim must be requeued and claimable by another node");
            var arr = (RedisResult[])redelivered!;
            ((string)arr[0]!).Should().Be("req-1");
            ((string)arr[1]!).Should().Be("payload-1");
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

            var claimed = db.Execute("HW.DEQUEUE", "lease.svc2", "node-A");
            claimed.IsNull.Should().BeFalse();

            db.Execute("HW.ACK", "lease.svc2", "node-A", "req-2").ToString().Should().Be("OK");

            Thread.Sleep(WaitPastLease);

            var redelivered = db.Execute("HW.DEQUEUE", "lease.svc2", "node-B");
            redelivered.IsNull.Should().BeTrue("an acknowledged request must never be redelivered");
        }
    }

    [Fact]
    public void Rpc_AckAfterRequeue_ReturnsOk_LeavesNoResidue()
    {
        var (server, redis, db) = StartServer(ShortLease);
        using (server)
        using (redis)
        {
            db.Execute("HW.CALL", "lease.svc3", "req-3", "payload-3");

            db.Execute("HW.DEQUEUE", "lease.svc3", "node-A");           // claim, no ACK
            Thread.Sleep(WaitPastLease);
            db.Execute("HW.DEQUEUE", "lease.svc3", "node-B");           // sweep requeues, B claims

            // The slow-but-alive original worker ACKs late — must be accepted
            db.Execute("HW.ACK", "lease.svc3", "node-A", "req-3").ToString().Should().Be("OK");

            // No residue: both nodes' queues/processing are drained of req-3
            db.Execute("HW.DEQUEUE", "lease.svc3", "node-A").IsNull.Should().BeTrue();
            db.Execute("HW.DEQUEUE", "lease.svc3", "node-C").IsNull.Should().BeTrue();
            // B's copy sits in its processing list; ACK it and confirm empty afterwards
            db.Execute("HW.ACK", "lease.svc3", "node-B", "req-3").ToString().Should().Be("OK");
            db.Execute("HW.DEQUEUE", "lease.svc3", "node-B").IsNull.Should().BeTrue();
        }
    }

    [Fact]
    public void PubSub_UnackedMessage_RedeliveredAtHead_PreservingOrder()
    {
        var (server, redis, db) = StartServer(ShortLease);
        using (server)
        using (redis)
        {
            db.Execute("HW.SUBSCRIBE", "lease.ch", "grp");
            db.Execute("HW.PUBLISH", "lease.ch", "m1");
            db.Execute("HW.PUBLISH", "lease.ch", "m2");

            // Receive only m1 — moves to processing, never RACKed
            var first = (RedisResult[])db.Execute("HW.RECEIVE", "lease.ch", "grp", "COUNT", "1")!;
            first.Should().HaveCount(1);
            ((string)((RedisResult[])first[0]!)[1]!).Should().Be("m1");

            Thread.Sleep(WaitPastLease);

            db.Execute("HW.PUBLISH", "lease.ch", "m3"); // queue is now m2, m3

            // Expired m1 must come back at the HEAD: order m1, m2, m3
            var second = (RedisResult[])db.Execute("HW.RECEIVE", "lease.ch", "grp", "COUNT", "10")!;
            var payloads = second.Select(r => (string)((RedisResult[])r!)[1]!).ToList();
            payloads.Should().ContainInOrder("m1", "m2", "m3");
            payloads.Should().HaveCount(3);
        }
    }

    [Fact]
    public void PubSub_RackInOneGroup_DoesNotAffectOtherGroup()
    {
        var (server, redis, db) = StartServer(ShortLease);
        using (server)
        using (redis)
        {
            db.Execute("HW.SUBSCRIBE", "indep.ch", "grpX");
            db.Execute("HW.SUBSCRIBE", "indep.ch", "grpY");
            db.Execute("HW.PUBLISH", "indep.ch", "shared");

            var rx = (RedisResult[])db.Execute("HW.RECEIVE", "indep.ch", "grpX", "COUNT", "10")!;
            var ry = (RedisResult[])db.Execute("HW.RECEIVE", "indep.ch", "grpY", "COUNT", "10")!;
            rx.Should().HaveCount(1);
            ry.Should().HaveCount(1);

            // grpX acknowledges its copy
            var msgIdX = (string)((RedisResult[])rx[0]!)[0]!;
            db.Execute("HW.RACK", "indep.ch", "grpX", msgIdX).ToString().Should().Be("OK");

            Thread.Sleep(WaitPastLease);

            // grpY's copy is untouched by grpX's RACK: it expires and redelivers
            var ryAgain = (RedisResult[])db.Execute("HW.RECEIVE", "indep.ch", "grpY", "COUNT", "10")!;
            ryAgain.Should().HaveCount(1, "grpY's in-flight copy must survive grpX's RACK and redeliver after the lease");
            ((string)((RedisResult[])ryAgain[0]!)[1]!).Should().Be("shared");
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

            db.Execute("HW.DEQUEUE", "nolease.svc", "node-A"); // claim, no ACK

            Thread.Sleep(WaitPastLease);

            // With the sweep disabled the expired entry stays in node-A's
            // processing list and is never requeued
            var redelivered = db.Execute("HW.DEQUEUE", "nolease.svc", "node-B");
            redelivered.IsNull.Should().BeTrue("Lease=Zero must disable lazy requeue entirely");
        }
    }
}
