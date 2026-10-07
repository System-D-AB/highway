using System.Text;
using FluentAssertions;
using Highway.Abstractions.Observability;
using Highway.Server.Commands.Ported;
using Highway.Server.Storage;
using Highway.Server.Storage.Boxes;
using Highway.Server.Storage.Layout;
using Xunit;

namespace Highway.Server.Tests.Commands;

/// <summary>
/// Feature 070 (registry hardening). A session is the node's complete subscription set: a group it stops
/// declaring loses it, and a group whose last member left <b>drains</b> — no new messages, backlog kept,
/// retired after the threshold, resumed by a re-declare. The failure this prevents: a node redeployed
/// without a subscriber stayed a member for ever, its group queue filled, and every publish to the channel
/// was refused.
/// </summary>
public class RegistryHardeningTests
{
    private static readonly long T0 = new DateTime(2026, 9, 28, 12, 0, 0, DateTimeKind.Utc).Ticks;

    private static string Subs(params (string Channel, string Group)[] subs)
        => "{\"subscriptions\":[" + string.Join(",", subs.Select(s => $"{{\"channel\":\"{s.Channel}\",\"group\":\"{s.Group}\"}}")) + "]}";

    private static void Session(CommandHarness h, long now, string node, params (string Channel, string Group)[] subs)
        => CommandHarness.AsText(h.Run(new HwSessionCommand(), now, node, Subs(subs))).Should().Be("+OK\r\n");

    private static string Publish(CommandHarness h, long now, string channel, string payload)
        => CommandHarness.AsText(h.Run(new HwPublishCommand(), now, channel, payload));

    private static string[] Members(CommandHarness h, string setName)
    {
        using var snap = h.Store.Snapshot();
        return [.. h.Store.SetMembers(snap, HighwayKeyspace.SetPrefix(setName)).Select(m => Encoding.UTF8.GetString(m))];
    }

    /// <summary>A group queue's ready count and draining mark, or null when it has no box.</summary>
    private static (long Ready, long DrainingSince)? Group(CommandHarness h, string channel, string group)
    {
        var rt = new BoxRuntime();
        using var batch = h.Store.NewBatch();
        if (Catalog.Find(h.Store, batch, rt, BoxKeys.Kind.Queue, $"{channel}@{group}") is not { } box) return null;
        using var wb = new WorkBox(h.Store, rt, box, tracksBytes: true, batch);
        return (wb.Ready, wb.DrainingSince);
    }

    private static List<HighwayEvent> Events(CommandHarness h, string name)
        => [.. h.Recorder.Read(name, DateTimeOffset.MinValue, DateTimeOffset.MaxValue, null, 1000)];

    [Fact]
    public void DroppingASubscription_OthersRemain_OnlyTheMembershipGoes()
    {
        using var h = new CommandHarness();
        Session(h, T0, "node-1", ("news", "billing"));
        Session(h, T0, "node-2", ("news", "billing"));

        Session(h, T0, "node-1");   // node-1 redeployed without the subscriber

        Members(h, HighwayNames.GroupMembers("news", "billing")).Should().BeEquivalentTo("node-2");
        Members(h, HighwayNames.NodeSubs("node-1")).Should().BeEmpty();
        Publish(h, T0, "news", "m1").Should().Be(":1\r\n", "node-2 still backs the group");
        Group(h, "news", "billing")!.Value.DrainingSince.Should().Be(0);
        Events(h, "news").Should().Contain(e => e.EventType == HighwayEventType.MembershipRemoved);
    }

    [Fact]
    public void DroppingTheLastMember_DrainsTheGroup_BacklogKept_NoNewMessages()
    {
        using var h = new CommandHarness();
        Session(h, T0, "node-1", ("news", "billing"));
        Publish(h, T0, "news", "m1").Should().Be(":1\r\n");

        Session(h, T0 + 10, "node-1");

        Group(h, "news", "billing").Should().Be((1L, T0 + 10), "the backlog stays and the drain started");
        Publish(h, T0 + 20, "news", "m2").Should().Be(":0\r\n", "a draining group receives nothing new");
        Group(h, "news", "billing")!.Value.Ready.Should().Be(1);
        Events(h, "news").Should().Contain(e => e.EventType == HighwayEventType.GroupDraining);
    }

    [Fact]
    public void ReDeclaring_ResumesTheGroup_WithItsBacklog()
    {
        using var h = new CommandHarness();
        Session(h, T0, "node-1", ("news", "billing"));
        Publish(h, T0, "news", "m1");
        Session(h, T0 + 10, "node-1");                        // drains

        Session(h, T0 + 20, "node-1", ("news", "billing"));   // declared again before the threshold

        Group(h, "news", "billing").Should().Be((1L, 0L), "resumed with its backlog");
        Publish(h, T0 + 30, "news", "m2").Should().Be(":1\r\n");
        Group(h, "news", "billing")!.Value.Ready.Should().Be(2);
    }

    [Fact]
    public void ADrainingGroup_IsRetiredPastTheThreshold_AndCounted()
    {
        var options = new HighwayServerOptions { SubscriberRetirementThreshold = TimeSpan.FromHours(1) };
        using var h = new CommandHarness(options: options);
        Session(h, T0, "node-1", ("news", "billing"));
        Publish(h, T0, "news", "m1");
        Session(h, T0, "node-1");

        Publish(h, T0 + TimeSpan.FromMinutes(59).Ticks, "news", "m2").Should().Be(":0\r\n");
        Group(h, "news", "billing").Should().NotBeNull("still inside the threshold");

        Publish(h, T0 + TimeSpan.FromHours(2).Ticks, "news", "m3").Should().Be(":0\r\n");
        Group(h, "news", "billing").Should().BeNull("drained past the threshold: retired");
        Members(h, HighwayNames.ChannelGroups("news")).Should().BeEmpty();
        Events(h, "news").Should().Contain(e => e.EventType == HighwayEventType.GroupRetired && e.Count == 1,
            "the discarded backlog is counted");
    }

    [Fact]
    public void AFullAbandonedGroup_NoLongerBlocksTheChannel()
    {
        // The failure this prevents: before 070 this publish was refused HW_QUEUE_FULL for every group.
        var options = new HighwayServerOptions { MaxQueueBytes = 200 };
        using var h = new CommandHarness(options: options);
        Session(h, T0, "node-1", ("news", "billing"));
        Session(h, T0, "node-2", ("news", "shipping"));
        for (var i = 0; i < 20 && !Publish(h, T0, "news", "abcdefghij").StartsWith("-ERR"); i++) { }
        Publish(h, T0, "news", "abcdefghij").Should().StartWith("-ERR HW_QUEUE_FULL", "both groups are full now");

        Session(h, T0, "node-1");                               // node-1 no longer subscribes to billing
        Session(h, T0, "node-2");                               // nor node-2 to shipping
        Session(h, T0, "node-3", ("news", "audit"));            // a new, empty group

        Publish(h, T0 + 1, "news", "abcdefghij").Should().Be(":1\r\n",
            "draining groups are skipped, so a full abandoned group cannot refuse the channel");
    }

    [Fact]
    public void ASessionWithoutASubscriptionsList_LeavesMembershipsAlone()
    {
        using var h = new CommandHarness();
        Session(h, T0, "node-1", ("news", "billing"));

        CommandHarness.AsText(h.Run(new HwSessionCommand(), T0, "node-1", "{\"catalog\":{\"services\":[]}}"))
            .Should().Be("+OK\r\n");

        Members(h, HighwayNames.GroupMembers("news", "billing")).Should().BeEquivalentTo("node-1");
    }

    [Fact]
    public void AnUnchangedSession_WritesNoMembershipChanges()
    {
        using var h = new CommandHarness();
        Session(h, T0, "node-1", ("news", "billing"));
        var before = Events(h, "news").Count(e => e.EventType == HighwayEventType.GroupRegistered);

        Session(h, T0, "node-1", ("news", "billing"));

        Events(h, "news").Count(e => e.EventType == HighwayEventType.GroupRegistered).Should().Be(before,
            "a re-declared, unchanged session joins nothing");
    }

    [Fact]
    public void AMemberlessGroup_StartsDraining_InsteadOfFilling()
    {
        using var h = new CommandHarness();
        using (var batch = h.Store.NewBatch())
        {
            h.Store.SetAdd(batch, HighwayKeyspace.SetPrefix(HighwayNames.ChannelGroups("news")), "orphan"u8.ToArray());
            batch.Commit();
        }

        Publish(h, T0, "news", "m1").Should().Be(":0\r\n");
        Group(h, "news", "orphan")!.Value.DrainingSince.Should().Be(T0);
        Events(h, "news").Should().Contain(e => e.EventType == HighwayEventType.GroupDraining);
    }

    [Fact]
    public void ASessionChangingMemberships_TakesTheChannelLock()
    {
        // R3: membership changes serialize with publish-time retirement on the channel lock. Hold it, and a
        // session that joins a group on that channel must wait for it.
        using var h = new CommandHarness();
        Task session;
        using (h.Locks.Lock("news"))
        {
            session = Task.Run(() => Session(h, T0, "node-1", ("news", "billing")));
            session.Wait(TimeSpan.FromMilliseconds(300)).Should().BeFalse("the session waits for the channel lock");
        }
        session.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
        Members(h, HighwayNames.GroupMembers("news", "billing")).Should().BeEquivalentTo("node-1");
    }

    [Fact]
    public void TwoSessionsForOneNode_TheLastOneWins_NoStaleMembership()
    {
        // Review F1 (proved by the independent reviewer): session S2 plans, then waits for its locks; S1 for
        // the same node commits in between; S2's locked re-plan must act on S1's change too, or the node
        // stays a member of a group its last session no longer declares.
        using var h = new CommandHarness();
        const string node = "twin";
        var nodeStripe = h.Locks.StripeOf(node);
        // A channel whose stripe sorts BEFORE the node's: S2 parks on it without holding the node stripe.
        var a = Enumerable.Range(0, 10_000).Select(i => $"ca{i}").First(c => h.Locks.StripeOf(c) < nodeStripe);
        var b = Enumerable.Range(0, 10_000).Select(i => $"cb{i}")
            .First(c => h.Locks.StripeOf(c) != h.Locks.StripeOf(a) && h.Locks.StripeOf(c) != nodeStripe);

        // 070 R2-5: wait until S2 is really parked on a's stripe — under a starved pool a fixed delay could let S2
        // start only after S1 committed, and the test would pass without the race it exists for.
        using var s2Parked = new ManualResetEventSlim();
        var aStripe = h.Locks.StripeOf(a);
        Task s2;
        using (h.Locks.Lock(a))
        {
            h.Locks.StripeWaiting = stripe => { if (stripe == aStripe) s2Parked.Set(); };
            s2 = Task.Run(() => Session(h, T0, node, (a, "g2")));        // plans {a@g2}, parks on a's stripe
            s2Parked.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue("S2 has planned and is waiting for the channel lock");
            h.Locks.StripeWaiting = null;
            Session(h, T0, node, (b, "g1"));                               // S1 commits {b@g1} meanwhile
        }
        s2.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();

        Members(h, HighwayNames.NodeSubs(node)).Should().BeEquivalentTo([$"{a}@g2"], "the last session declared only a@g2");
        Members(h, HighwayNames.GroupMembers(b, "g1")).Should().BeEmpty();
        Members(h, HighwayNames.GroupMembers(a, "g2")).Should().BeEquivalentTo(node);
    }

    [Fact]
    public void ByePurge_ForgetsTheRetiredBox_BeforeReleasingItsLocks()
    {
        // Review F3: a retired group box id must leave the runtime cache while PURGE still holds the channel
        // lock. Afterwards a publish that re-resolves the group would otherwise get the deleted box and
        // enqueue into it — lost. Whoever takes the channel lock next must never see the stale id.
        // 070 R2-5: look at the exact moment PURGE releases the channel's stripe (a release hook), instead of racing
        // for the lock afterwards and catching the old order only a few times in a thousand.
        using var h = new CommandHarness();
        Session(h, T0, "node-1", ("news", "billing"));
        Publish(h, T0, "news", "m1");                                      // creates and caches news@billing
        h.Boxes.TryGetId(BoxKeys.Kind.Queue, "news@billing", out _).Should().BeTrue();

        var newsStripe = h.Locks.StripeOf("news");
        bool? cachedAtRelease = null;
        h.Locks.StripeReleasing = stripe =>
        {
            if (stripe == newsStripe && cachedAtRelease is null && Group(h, "news", "billing") is null)
                cachedAtRelease = h.Boxes.TryGetId(BoxKeys.Kind.Queue, "news@billing", out _);
        };
        h.Run(new HwHeartbeatCommand(), T0, "node-1", "BYE", "PURGE");
        h.Locks.StripeReleasing = null;

        cachedAtRelease.Should().NotBeNull("PURGE retired the group and released the channel lock");
        cachedAtRelease.Should().BeFalse("the retired box id must leave the cache before the channel lock is released");
    }

    [Fact]
    public void ByePurge_RacingASessionJoin_LeavesNoUnrecordedMembership()
    {
        // Review F2 (proved): PURGE deleted the node's NodeSubs without the node lock, so a session joining a
        // group while PURGE waited ended with GroupMembers(h) = {node} but NodeSubs = {} - a membership no
        // session diff or later PURGE can see, so the group could never drain or retire. Whatever order the
        // two take, a membership and the node's record of it must agree.
        using var h = new CommandHarness();
        const string node = "node-1";
        Session(h, T0, node, ("news", "billing"));
        Publish(h, T0, "news", "m1");
        var other = Enumerable.Range(0, 10_000).Select(i => $"cb{i}").First(c =>
            h.Locks.StripeOf(c) != h.Locks.StripeOf("news") && h.Locks.StripeOf(c) != h.Locks.StripeOf(node));

        Task purge, join;
        using (h.Locks.Lock("news"))
        {
            purge = Task.Run(() => h.Run(new HwHeartbeatCommand(), T0, node, "BYE", "PURGE"));
            purge.Wait(TimeSpan.FromMilliseconds(300)).Should().BeFalse("PURGE waits for the channel lock");
            join = Task.Run(() => Session(h, T0, node, ("news", "billing"), (other, "h")));
            join.Wait(TimeSpan.FromMilliseconds(300));
        }
        purge.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
        join.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();

        var member = Members(h, HighwayNames.GroupMembers(other, "h")).Contains(node);
        var recorded = Members(h, HighwayNames.NodeSubs(node)).Contains($"{other}@h");
        member.Should().Be(recorded, "a membership the node's own record does not list could never be drained or retired");
    }

    /// <summary>
    /// Review R2-1 (proved): a group's box id resolved before its lock, then retired (and the group
    /// re-joined) before the lock was taken, made a publish enqueue into the deleted box — ":1", message
    /// gone. Reproduced deterministically by putting the retired id back into the id cache.
    /// </summary>
    private static uint StaleGroupBox(CommandHarness h)
    {
        Session(h, T0, "node-1", ("news", "billing"));
        Publish(h, T0, "news", "m0").Should().Be(":1\r\n");                // creates + caches news@billing
        h.Boxes.TryGetId(BoxKeys.Kind.Queue, "news@billing", out var stale).Should().BeTrue();
        CommandHarness.AsText(h.Run(new HwUnsubscribeCommand(), T0, "news", "billing")).Should().Be("+OK\r\n");
        Session(h, T0, "node-1", ("news", "billing"));                       // re-joined: no box yet
        h.Boxes.Remember(BoxKeys.Kind.Queue, "news@billing", stale);         // the id resolved before the retire
        return stale;
    }

    [Fact]
    public void APublishHoldingARetiredGroupBoxId_LandsInTheLiveGroup()
    {
        using var h = new CommandHarness();
        var stale = StaleGroupBox(h);

        Publish(h, T0, "news", "m1").Should().Be(":1\r\n");

        h.Boxes.TryGetId(BoxKeys.Kind.Queue, "news@billing", out var live).Should().BeTrue();
        live.Should().NotBe(stale, "a retired box id is never reused");
        Group(h, "news", "billing")!.Value.Ready.Should().Be(1, "the message is in the live group, not the deleted range");
        h.ClaimOne("Q", "news@billing", "node-1", T0).Should().NotBeNull("a publish that answered :1 must be claimable");
    }

    [Fact]
    public void AClaimHoldingARetiredGroupBoxId_ClaimsFromTheLiveGroup()
    {
        using var h = new CommandHarness();
        var stale = StaleGroupBox(h);
        h.Boxes.Forget(BoxKeys.Kind.Queue, "news@billing", stale);
        Publish(h, T0, "news", "m1").Should().Be(":1\r\n");                // into a fresh, live box
        h.Boxes.Remember(BoxKeys.Kind.Queue, "news@billing", stale);         // a claim resolving the retired id

        h.ClaimOne("Q", "news@billing", "node-1", T0).Should().NotBeNull("the claim re-resolves the group under its lock");
        Group(h, "news", "billing")!.Value.Ready.Should().Be(0);
    }

    [Fact]
    public void ByePurge_StillRetiresAtOnce_ThroughLeaveGroup()
    {
        using var h = new CommandHarness();
        Session(h, T0, "node-1", ("news", "billing"));
        Publish(h, T0, "news", "m1");

        var reply = CommandHarness.AsText(h.Run(new HwHeartbeatCommand(), T0, "node-1", "BYE", "PURGE"));

        reply.Should().Be("*3\r\n:1\r\n:1\r\n:" + reply.Split("\r\n")[3][1..] + "\r\n");
        Group(h, "news", "billing").Should().BeNull("PURGE keeps its explicit retire-now semantics");
        Members(h, HighwayNames.NodeSubs("node-1")).Should().BeEmpty();
    }

    /// <summary>
    /// 070 third review F3: PURGE and the session diff planned their group box locks from the id cache and checked
    /// them under the locks from the same cache. A stale cached id (the group retired and re-created since) passed
    /// both, so the live box was left un-locked and un-retired — its name index deleted, its data orphaned — while the
    /// dead box was "retired" again. Now the check under the locks reads the name index itself.
    /// </summary>
    private static (uint Stale, uint Live) StaleCachedLiveGroup(CommandHarness h)
    {
        var stale = StaleGroupBox(h);
        h.Boxes.Forget(BoxKeys.Kind.Queue, "news@billing", stale);
        Publish(h, T0, "news", "m1").Should().Be(":1\r\n");                // the live box
        h.Boxes.TryGetId(BoxKeys.Kind.Queue, "news@billing", out var live).Should().BeTrue();
        h.Boxes.Remember(BoxKeys.Kind.Queue, "news@billing", stale);         // a stale id in the cache
        return (stale, live);
    }

    private static string? State(CommandHarness h, uint box)
    {
        using var batch = h.Store.NewBatch();
        return Catalog.ReadMeta(h.Store, batch, box)?.State;
    }

    [Fact]
    public void ByePurge_WithAStaleCachedGroupBox_RetiresTheLiveOne()
    {
        using var h = new CommandHarness();
        var (_, live) = StaleCachedLiveGroup(h);

        var reply = CommandHarness.AsText(h.Run(new HwHeartbeatCommand(), T0, "node-1", "BYE", "PURGE"));

        reply.Split("\r\n")[2].Should().Be(":1", "the live group's one message is counted as discarded");
        State(h, live).Should().Be("retired", "the live box is retired, not orphaned");
        Group(h, "news", "billing").Should().BeNull();
    }

    [Fact]
    public void LeavingAGroup_WithAStaleCachedGroupBox_DrainsTheLiveOne()
    {
        using var h = new CommandHarness();
        StaleCachedLiveGroup(h);

        Session(h, T0, "node-1");   // the last member stops declaring it: drain

        Group(h, "news", "billing")!.Value.DrainingSince.Should().NotBe(0, "the live box is marked draining, not the dead one");
    }

    [Fact]
    public void APublishRacingTheRetireOfAGroupWithNoBox_LeavesNoOrphanBox()
    {
        // 070 third review F4: the group is listed when the publish reads it, retired before the publish creates its box.
        using var h = new CommandHarness();
        Session(h, T0, "node-1", ("news", "billing"));                      // joined, never published: no box
        var catalogStripe = h.Locks.StripeOf(Catalog.LockName);
        catalogStripe.Should().NotBe(h.Locks.StripeOf("news"));
        h.Locks.StripeWaiting = stripe =>
        {
            if (stripe != catalogStripe) return;
            h.Locks.StripeWaiting = null;
            CommandHarness.AsText(h.Run(new HwUnsubscribeCommand(), T0, "news", "billing")).Should().Be("+OK\r\n");
        };

        Publish(h, T0, "news", "m1").Should().Be(":0\r\n");
        h.Locks.StripeWaiting = null;

        Group(h, "news", "billing").Should().BeNull("no group owns the box the publish created: it is retired, not left active");
    }

    [Fact]
    public void ByePurge_OnTheEveryStripeAttempt_StillRetiresTheLiveBox()
    {
        // Review of R2-4: the LockAll attempt skipped the live re-check, so a stale cached id survived into the retire.
        using var h = new CommandHarness();
        var (_, live) = StaleCachedLiveGroup(h);

        h.Run(new HwHeartbeatCommand { MaxPurgeAttempts = 1 }, T0, "node-1", "BYE", "PURGE");

        State(h, live).Should().Be("retired");
    }

    [Fact]
    public void LeavingAGroup_OnTheEveryStripeAttempt_StillDrainsTheLiveBox()
    {
        using var h = new CommandHarness();
        StaleCachedLiveGroup(h);

        CommandHarness.AsText(h.Run(new HwSessionCommand { MaxPlanAttempts = 1 }, T0, "node-1", Subs())).Should().Be("+OK\r\n");

        Group(h, "news", "billing")!.Value.DrainingSince.Should().NotBe(0);
    }

    [Fact]
    public void APublishRacingTheRetire_KeepsABoxSomethingWasWrittenTo()
    {
        // Review of F4: "empty" counted only queued entries. Anything written into the box in the window is accepted
        // state the publish must not delete with the box. (HW.JOB refuses '@' names, so a schedule cannot get there
        // today; the raw key stands in for any write a later path makes.)
        using var h = new CommandHarness();
        Session(h, T0, "node-1", ("news", "billing"));
        var catalogStripe = h.Locks.StripeOf(Catalog.LockName);
        Action<int> atLock = _ =>
        {
            h.Locks.StripeWaiting = null;   // the publish is about to take its locks: its box exists
            h.Boxes.TryGetId(BoxKeys.Kind.Queue, "news@billing", out var created).Should().BeTrue();
            using var batch = h.Store.NewBatch();
            h.Store.Set(batch, [.. BoxKeys.Box(created), 0x7F], [1]);
            batch.Commit();
        };
        h.Locks.StripeWaiting = stripe =>
        {
            if (stripe != catalogStripe) return;
            h.Locks.StripeWaiting = null;
            CommandHarness.AsText(h.Run(new HwUnsubscribeCommand(), T0, "news", "billing")).Should().Be("+OK\r\n");
            h.Locks.StripeWaiting = atLock;
        };

        Publish(h, T0, "news", "m1").Should().Be(":0\r\n");
        h.Locks.StripeWaiting = null;

        h.Boxes.TryGetId(BoxKeys.Kind.Queue, "news@billing", out var box).Should().BeTrue("a box with data in it is kept");
        State(h, box).Should().Be("active");
    }
}
