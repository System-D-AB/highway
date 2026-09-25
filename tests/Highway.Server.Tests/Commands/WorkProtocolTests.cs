using System.Text;
using FluentAssertions;
using Highway.Server.Commands.Ported;
using Highway.Server.Storage.Layout;
using Xunit;

namespace Highway.Server.Tests.Commands;

/// <summary>
/// Protocol 5.0 (features 065 / 068): the work commands over the box layout — one claim path for services,
/// queues and group queues, settled by token; the declarative session; HW.RESULT and HW.IDEM; the expiry
/// sweep. Each test states the guarantee it pins.
/// </summary>
public class WorkProtocolTests
{
    private const long T0 = 638_000_000_000_000_000L;
    private static readonly long Second = TimeSpan.TicksPerSecond;

    private static string Text(byte[] reply) => CommandHarness.AsText(reply);

    private static HighwayServerOptions Options(Action<HighwayServerOptions>? tweak = null)
    {
        var o = new HighwayServerOptions { Lease = TimeSpan.FromSeconds(30), MaxDeliveryAttempts = 3 };
        tweak?.Invoke(o);
        return o;
    }

    // ------------------------------------------------------------------ claim + settle

    [Fact]
    public void Claim_IsFifo_AndReturnsAToken_ThatAcksOnce()
    {
        using var h = new CommandHarness(options: Options());
        h.Run(new HwSendCommand(), T0, "orders", "m1", "a");
        h.Run(new HwSendCommand(), T0, "orders", "m2", "b");

        var first = h.ClaimOne("Q", "orders", "w1", T0)!.Value;
        var second = h.ClaimOne("Q", "orders", "w1", T0)!.Value;
        (first.Id, first.Payload, second.Id).Should().Be(("m1", "a", "m2"));
        first.Token.Should().NotBe(second.Token);

        Text(h.Run(new HwAckCommand(), T0, first.Token)).Should().Be(":1\r\n");
        Text(h.Run(new HwAckCommand(), T0, first.Token)).Should().Be(":0\r\n", "a second ack of the same claim finds nothing held");
        h.ClaimOne("Q", "orders", "w1", T0).Should().BeNull("both messages were claimed");
        Text(h.Run(new HwClaimCommand(), T0, "Q", "orders", "w1")).Should().Be("*0\r\n", "nothing due is an empty array");
    }

    [Fact]
    public void Claim_WithCount_TakesSeveral_InOrder()
    {
        using var h = new CommandHarness(options: Options());
        for (var i = 0; i < 5; i++) h.Run(new HwSendCommand(), T0, "orders", "m" + i, "p");
        var rows = CommandHarness.Claims(h.Run(new HwClaimCommand(), T0, "Q", "orders", "w", "COUNT", "3"));
        rows.Select(r => r.Id).Should().Equal("m0", "m1", "m2");
        Text(h.Run(new HwClaimCommand(), T0, "Q", "orders", "w", "COUNT", "0")).Should().StartWith("-ERR HW_INVALID_COUNT");
    }

    [Fact]
    public void ServiceAndQueue_WithTheSameName_AreSeparateBoxes()
    {
        using var h = new CommandHarness(options: Options());
        h.Run(new HwCallCommand(), T0, "x", "req-1", "call");
        h.Run(new HwSendCommand(), T0, "x", "msg-1", "send");
        h.ClaimOne("SVC", "x", "w", T0)!.Value.Id.Should().Be("req-1");
        h.ClaimOne("Q", "x", "w", T0)!.Value.Id.Should().Be("msg-1");
    }

    [Fact]
    public void AStaleToken_AfterLeaseRecovery_DoesNotSettleTheRedelivery()
    {
        using var h = new CommandHarness(options: Options());
        h.Run(new HwSendCommand(), T0, "orders", "m1", "a");
        var stale = h.ClaimOne("Q", "orders", "slow-worker", T0)!.Value;

        var later = T0 + 31 * Second;   // past the 30 s lease: the next claim recovers and redelivers it
        var redelivered = h.ClaimOne("Q", "orders", "other-worker", later)!.Value;
        redelivered.Id.Should().Be("m1");
        redelivered.Token.Should().NotBe(stale.Token, "a redelivery is a new claim");

        Text(h.Run(new HwAckCommand(), later, stale.Token)).Should().Be(":0\r\n", "the slow worker's late ack must not settle the new claim");
        Text(h.Run(new HwAckCommand(), later, redelivered.Token)).Should().Be(":1\r\n");
    }

    [Fact]
    public void Touch_RestartsTheLease()
    {
        using var h = new CommandHarness(options: Options());
        h.Run(new HwSendCommand(), T0, "orders", "m1", "a");
        var claim = h.ClaimOne("Q", "orders", "w", T0)!.Value;

        Text(h.Run(new HwTouchCommand(), T0 + 25 * Second, claim.Token)).Should().Be(":1\r\n");
        h.ClaimOne("Q", "orders", "w2", T0 + 40 * Second).Should().BeNull("touched at +25 s, the lease runs to +55 s");
        h.ClaimOne("Q", "orders", "w2", T0 + 56 * Second)!.Value.Id.Should().Be("m1", "once the renewed lease lapses it is recovered");
    }

    [Fact]
    public void Token_Grammar_IsChecked_AndARetiredBoxHoldsNothing()
    {
        using var h = new CommandHarness(options: Options());
        Text(h.Run(new HwAckCommand(), T0, "not-a-token")).Should().StartWith("-ERR HW_INVALID_TOKEN");
        Text(h.Run(new HwAckCommand(), T0, "3.1")).Should().StartWith("-ERR HW_INVALID_TOKEN", "boxes below 16 are reserved");
        Text(h.Run(new HwAckCommand(), T0, "999.1")).Should().Be(":0\r\n", "a box that does not exist holds nothing");
    }

    // ------------------------------------------------------------------ recovery and dead letters

    [Fact]
    public void LeaseRecovery_CountsAttempts_ThenDeadLetters_WithTheReportedFailure()
    {
        using var h = new CommandHarness(options: Options(o => o.MaxDeliveryAttempts = 2));
        h.Run(new HwSendCommand(), T0, "orders", "m1", "a");

        var t = T0;
        var claim = h.ClaimOne("Q", "orders", "w", t)!.Value;                       // attempts 0
        Text(h.Run(new HwFailCommand(), t, claim.Token, "System.TimeoutException", "{\"message\":\"slow\"}")).Should().Be(":1\r\n");
        h.ClaimOne("Q", "orders", "w", t += 31 * Second).Should().NotBeNull();       // recovered (1), claimed
        h.ClaimOne("Q", "orders", "w", t += 31 * Second).Should().NotBeNull();       // recovered (2), claimed
        h.ClaimOne("Q", "orders", "w", t += 31 * Second).Should().BeNull("3 > 2 attempts: dead-lettered, not redelivered");

        var peek = Text(h.Run(new HwDlqCommand(), t, "PEEK", "Q", "orders"));
        peek.Should().Contain("MAX_ATTEMPTS").And.Contain("m1").And.Contain("System.TimeoutException");

        Text(h.Run(new HwDlqCommand(), t, "REQUEUE", "Q", "orders")).Should().Be(":1\r\n");
        var again = h.ClaimOne("Q", "orders", "w", t)!.Value;
        again.Id.Should().Be("m1", "an operator requeue returns it with attempts reset");
        Text(h.Run(new HwDlqCommand(), t, "PURGE", "Q", "orders")).Should().Be(":0\r\n");
    }

    [Fact]
    public void DeadNodeRecovery_ReturnsAStaleServiceNode_sClaims()
    {
        using var h = new CommandHarness(options: Options(o =>
        {
            o.Lease = TimeSpan.FromHours(1);
            o.NodeExpiry = TimeSpan.FromSeconds(15);
            o.PruningEnabled = true;
        }));
        Text(h.Run(new HwSessionCommand(), T0, "dying", "{\"catalog\":{\"services\":[{\"name\":\"svc\"}]}}")).Should().Be("+OK\r\n");
        h.Run(new HwCallCommand(), T0, "svc", "r1", "p");
        h.ClaimOne("SVC", "svc", "dying", T0)!.Value.Id.Should().Be("r1");

        // 'dying' stops beating; 20 s later a live node claims and recovers its work.
        h.ClaimOne("SVC", "svc", "alive", T0 + 20 * Second)!.Value.Id.Should().Be("r1", "the stale node's claim returned to the queue");
        Text(h.Run(new HwHeartbeatCommand(), T0 + 20 * Second, "dying")).Should().Be("+SESSION\r\n", "and it left the registry");
    }

    // ------------------------------------------------------------------ delays and budgets

    [Fact]
    public void DelayedSend_IsPromotedByTheFirstClaimAfterItsTime()
    {
        using var h = new CommandHarness(options: Options());
        var due = T0 + 10 * Second;
        h.Run(new HwSendCommand(), T0, "orders", "later", "x", "AT", due.ToString());
        h.ClaimOne("Q", "orders", "w", T0).Should().BeNull("not before its time");
        h.ClaimOne("Q", "orders", "w", due)!.Value.Id.Should().Be("later");
    }

    [Fact]
    public void ByteBudget_RefusesRatherThanDrops_CountingDelayedWork()
    {
        // Each entry is a 5-byte header + id + payload: 57 bytes here, so two need 114 > 100.
        using var h = new CommandHarness(options: Options(o => o.MaxQueueBytes = 100));
        Text(h.Run(new HwSendCommand(), T0, "q", "m1", new string('a', 50), "AT", (T0 + Second).ToString())).Should().Be("+OK\r\n");
        Text(h.Run(new HwSendCommand(), T0, "q", "m2", new string('b', 50))).Should().StartWith("-ERR HW_QUEUE_FULL",
            "the delayed message already holds most of the budget");
    }

    // ------------------------------------------------------------------ jobs

    [Fact]
    public void Job_FiresOnce_OnTheFirstClaimAfterItIsDue()
    {
        using var h = new CommandHarness(options: Options());
        Text(h.Run(new HwJobCommand(), T0, "SET", "nightly", "cleanup", "every:60", "occurrence")).Should().Be("+OK\r\n");
        h.ClaimOne("Q", "nightly", "w", T0).Should().BeNull();
        var fired = h.ClaimOne("Q", "nightly", "w", T0 + 61 * Second)!.Value;
        fired.Id.Should().StartWith("job:cleanup:");
        fired.Payload.Should().Be("occurrence");
        h.ClaimOne("Q", "nightly", "w", T0 + 62 * Second).Should().BeNull("re-armed from now: the next fire is a minute away");
        Text(h.Run(new HwJobCommand(), T0, "LIST")).Should().Contain("cleanup").And.Contain("every:60");
        Text(h.Run(new HwJobCommand(), T0, "DEL", "nightly", "cleanup")).Should().Be(":1\r\n");
    }

    // ------------------------------------------------------------------ session, pub/sub

    [Fact]
    public void Session_DeclaresRegistration_Subscriptions_AndJobs_InOneCommand()
    {
        using var h = new CommandHarness(options: Options());
        Text(h.Run(new HwHeartbeatCommand(), T0, "node-1")).Should().Be("+SESSION\r\n", "no session yet");

        var template = Convert.ToBase64String("tick"u8.ToArray());
        var session = "{\"catalog\":{\"services\":[{\"name\":\"quotes\"}]}," +
                      "\"subscriptions\":[{\"channel\":\"news\",\"group\":\"billing\"}]," +
                      "\"jobs\":[{\"queue\":\"ticks\",\"job\":\"minutely\",\"expression\":\"every:60\",\"template\":\"" + template + "\"}]}";
        Text(h.Run(new HwSessionCommand(), T0, "node-1", session)).Should().Be("+OK\r\n");
        Text(h.Run(new HwSessionCommand(), T0, "node-1", session)).Should().Be("+OK\r\n", "idempotent");

        Text(h.Run(new HwHeartbeatCommand(), T0, "node-1")).Should().Be("+OK\r\n");
        Text(h.Run(new HwDiscoverCommand(), T0, "quotes")).Should().Contain("node-1");
        Text(h.Run(new HwPublishCommand(), T0, "news", "headline")).Should().Be(":1\r\n", "the declared group receives it");
        h.ClaimOne("Q", "news@billing", "node-1", T0)!.Value.Payload.Should().Be("headline");
        h.ClaimOne("Q", "ticks", "w", T0 + 61 * Second)!.Value.Payload.Should().Be("tick");
    }

    [Fact]
    public void Session_WithoutACatalog_RegistersNothing()
    {
        using var h = new CommandHarness(options: Options());
        Text(h.Run(new HwSessionCommand(), T0, "quiet", "{\"subscriptions\":[{\"channel\":\"news\",\"group\":\"quiet\"}]}")).Should().Be("+OK\r\n");
        Text(h.Run(new HwHeartbeatCommand(), T0, "quiet")).Should().Be("+SESSION\r\n",
            "a node with heartbeats off must not look registered — it would look dead");
        Text(h.Run(new HwPublishCommand(), T0, "news", "x")).Should().Be(":1\r\n", "but its subscription exists");
    }

    [Fact]
    public void Publish_IsAllOrNothing_AcrossGroups_UnderTheBudget()
    {
        using var h = new CommandHarness(options: Options(o => o.MaxQueueBytes = 100));
        h.Run(new HwSessionCommand(), T0, "a", "{\"subscriptions\":[{\"channel\":\"news\",\"group\":\"a\"},{\"channel\":\"news\",\"group\":\"b\"}]}");
        Text(h.Run(new HwPublishCommand(), T0, "news", new string('f', 60))).Should().StartWith(":2");
        var aCopy = h.ClaimOne("Q", "news@a", "a", T0)!.Value;          // group a drains its copy; b does not
        h.Run(new HwAckCommand(), T0, aCopy.Token);

        Text(h.Run(new HwPublishCommand(), T0, "news", new string('x', 40))).Should().StartWith("-ERR HW_QUEUE_FULL",
            "group b cannot take it, so no group may");
        h.ClaimOne("Q", "news@a", "a", T0).Should().BeNull("a refused publish reaches no group, even one with room");
    }

    [Fact]
    public void DelayedPublish_IsBudgeted_AndRefusedWhole()
    {
        // 5.0: a deferred publish counts against each group's byte budget, as a deferred send always did.
        // Before 5.0 it bypassed the budget entirely, so a stream of delayed publishes grew without bound.
        using var h = new CommandHarness(options: Options(o => o.MaxQueueBytes = 100));
        h.Run(new HwSessionCommand(), T0, "a", "{\"subscriptions\":[{\"channel\":\"news\",\"group\":\"a\"}]}");
        var later = (T0 + 60 * Second).ToString();

        Text(h.Run(new HwPublishCommand(), T0, "news", new string('x', 60), "AT", later)).Should().StartWith(":0", "deferred: reached no group yet");
        Text(h.Run(new HwPublishCommand(), T0, "news", new string('y', 60), "AT", later)).Should().StartWith("-ERR HW_QUEUE_FULL",
            "the first deferred copy already holds most of group a's budget");
        Text(h.Run(new HwStatsCommand(), T0, "Q:news@a")).Should().Contain("deferred\r\n$1\r\n1");
    }

    [Fact]
    public void Unsubscribe_RetiresTheGroupBox()
    {
        using var h = new CommandHarness(options: Options());
        h.Run(new HwSessionCommand(), T0, "a", "{\"subscriptions\":[{\"channel\":\"news\",\"group\":\"a\"}]}");
        h.Run(new HwPublishCommand(), T0, "news", "x");
        Text(h.Run(new HwUnsubscribeCommand(), T0, "news", "a")).Should().Be("+OK\r\n");
        Text(h.Run(new HwPublishCommand(), T0, "news", "y")).Should().Be(":0\r\n");
        h.ClaimOne("Q", "news@a", "a", T0).Should().BeNull("the group's queued message went with it");
    }

    // ------------------------------------------------------------------ result, idempotency, expiry

    [Fact]
    public void Result_TakesTheReply_Once()
    {
        using var h = new CommandHarness(options: Options());
        Text(h.Run(new HwResultCommand(), T0, "r1")).Should().Be("$-1\r\n", "no reply yet");
        h.Run(new HwReplyCommand(), T0, "r1", "answer");
        Text(h.Run(new HwResultCommand(), T0, "r1")).Should().Be("$6\r\nanswer\r\n");
        Text(h.Run(new HwResultCommand(), T0, "r1")).Should().Be("$-1\r\n", "taken: gone");
    }

    [Fact]
    public void Idem_Claims_ReportsBusy_ThenReplaysTheResult()
    {
        using var h = new CommandHarness(options: Options());
        Text(h.Run(new HwIdemCommand(), T0, "BEGIN", "orders", "m1", "60")).Should().Be("+CLAIMED\r\n");
        Text(h.Run(new HwIdemCommand(), T0, "BEGIN", "orders", "m1", "60")).Should().Be("+BUSY\r\n");
        Text(h.Run(new HwIdemCommand(), T0, "END", "orders", "m1", "60", "done")).Should().Be("+OK\r\n");
        Text(h.Run(new HwIdemCommand(), T0, "BEGIN", "orders", "m1", "60")).Should().Be("*2\r\n$4\r\nDONE\r\n$4\r\ndone\r\n");
        Text(h.Run(new HwIdemCommand(), T0 + 61 * Second, "BEGIN", "orders", "m1", "60")).Should().Be("+CLAIMED\r\n", "the window lapsed");
        Text(h.Run(new HwIdemCommand(), T0, "ABORT", "orders", "m2")).Should().Be("+OK\r\n");
    }

    [Fact]
    public void ExpiredReplies_AreDeleted_NotMerelyHidden()
    {
        using var h = new CommandHarness(options: Options(o => o.ReplySlotTtl = TimeSpan.FromSeconds(10)));
        h.Run(new HwReplyCommand(), T0, "uncollected", "x");
        var key = HighwayKeyspace.Kv(HighwayNames.ReplySlot("uncollected"));
        using (var snap = h.Store.Snapshot()) h.Store.Get(snap, key).Should().NotBeNull();

        h.Run(new HwReplyCommand(), T0 + 11 * Second, "another", "y");   // any expiring write sweeps what is due
        using (var snap = h.Store.Snapshot())
            h.Store.Get(snap, key).Should().BeNull("an uncollected reply is deleted once it expires (it used to stay on disk forever)");
    }

    // ------------------------------------------------------------------ stats

    [Fact]
    public void Stats_AreCounters_Exact()
    {
        using var h = new CommandHarness(options: Options());
        for (var i = 0; i < 4; i++) h.Run(new HwSendCommand(), T0, "orders", "m" + i, "p");
        h.Run(new HwSendCommand(), T0, "orders", "d", "p", "AT", (T0 + 60 * Second).ToString());
        h.ClaimOne("Q", "orders", "w", T0);
        var stats = Text(h.Run(new HwStatsCommand(), T0, "Q:orders"));
        stats.Should().Contain("depth\r\n$1\r\n3").And.Contain("inFlight\r\n$1\r\n1").And.Contain("deferred\r\n$1\r\n1");
    }
}
