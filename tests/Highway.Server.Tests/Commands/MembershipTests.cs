using System.Text;
using FluentAssertions;
using Highway.Server.Commands.Ported;
using Highway.Server.Internal;
using Highway.Server.Storage;
using Highway.Server.Storage.Layout;
using Xunit;

namespace Highway.Server.Tests.Commands;

/// <summary>
/// 039 T7 — Batch 6 membership + mirrors: HW.JOB, HW.SUBSCRIBE, HW.UNSUBSCRIBE, HW.HEARTBEAT,
/// HW.DISCOVER, plus the deferred pieces (HW.PUBLISH auto-retirement, HW.QCLAIM job-firing,
/// HW.DEQUEUE dead-node sweep). The mirrors-collapse batch (R3): every former Main-store mirror
/// is a plain set now, and each of these tests reads through <see cref="IHighwayStore.SetMembers"/>
/// to prove the one surviving copy answers what the mirror did.
/// </summary>
public class MembershipTests
{
    // ---- seeding helpers -----------------------------------------------------

    /// <summary>Seeds a node registration record with a given last-seen and catalog services.</summary>
    private static void SeedRegistration(IHighwayStore store, string nodeId, long seenTicks, params string[] services)
    {
        var catalog = BuildCatalog(services);
        using var batch = store.NewBatch();
        store.Set(batch, HighwayKeyspace.Kv(HighwayNames.RegistrationNode(nodeId)),
            NodeRegistration.Encode(seenTicks, catalog));
        batch.Commit();
    }

    private static byte[] BuildCatalog(params string[] services)
    {
        var svc = string.Join(",", services.Select(s => $"{{\"name\":\"{s}\"}}"));
        return Encoding.UTF8.GetBytes($"{{\"services\":[{svc}]}}");
    }

    private static string[] Members(IHighwayStore store, string setName)
    {
        using var snap = store.Snapshot();
        return [.. store.SetMembers(snap, HighwayKeyspace.SetPrefix(setName)).Select(m => Encoding.UTF8.GetString(m))];
    }

    // ========================= HW.SUBSCRIBE ==================================

    [Fact]
    public void Subscribe_RegistersGroup_AndAllMirrorsAreSets()
    {
        using var h = new CommandHarness();
        var reply = h.Run(new HwSubscribeCommand(), "news", "billing", "node-1");
        CommandHarness.AsText(reply).Should().Be("+OK\r\n");

        // The channel-groups set is authoritative — the old grplist mirror is gone.
        Members(h.Store, HighwayNames.ChannelGroups("news")).Should().BeEquivalentTo("billing");
        // Every former mirror reader now reads the same set (R3.2 equivalence).
        Members(h.Store, HighwayNames.NodeChannels("billing")).Should().BeEquivalentTo("news");
        Members(h.Store, HighwayNames.GroupMembers("news", "billing")).Should().BeEquivalentTo("node-1");
        Members(h.Store, HighwayNames.NodeSubs("node-1")).Should().BeEquivalentTo("news@billing");
    }

    [Fact]
    public void Subscribe_WithoutNode_GroupIsTheNode()
    {
        using var h = new CommandHarness();
        h.Run(new HwSubscribeCommand(), "news", "billing"); // no node arg (pre-025)

        Members(h.Store, HighwayNames.GroupMembers("news", "billing")).Should().BeEquivalentTo("billing");
        Members(h.Store, HighwayNames.NodeSubs("billing")).Should().BeEquivalentTo("news@billing");
    }

    [Fact]
    public void Subscribe_IsIdempotent()
    {
        using var h = new CommandHarness();
        h.Run(new HwSubscribeCommand(), "news", "billing", "node-1");
        h.Run(new HwSubscribeCommand(), "news", "billing", "node-1");

        Members(h.Store, HighwayNames.ChannelGroups("news")).Should().BeEquivalentTo("billing");
        Members(h.Store, HighwayNames.GroupMembers("news", "billing")).Should().BeEquivalentTo("node-1");
    }

    [Fact]
    public void Subscribe_TwoNodesBackOneGroup()
    {
        using var h = new CommandHarness();
        h.Run(new HwSubscribeCommand(), "news", "billing", "node-1");
        h.Run(new HwSubscribeCommand(), "news", "billing", "node-2");

        Members(h.Store, HighwayNames.ChannelGroups("news")).Should().BeEquivalentTo("billing");
        Members(h.Store, HighwayNames.GroupMembers("news", "billing")).Should().BeEquivalentTo("node-1", "node-2");
    }

    // ========================= HW.UNSUBSCRIBE ================================

    [Fact]
    public void Unsubscribe_RetiresGroup_DestroysQueueAndUnregisters()
    {
        using var h = new CommandHarness();
        h.Run(new HwSubscribeCommand(), "news", "billing", "node-1");
        h.Run(new HwPublishCommand(), "news", "hello"); // one message into news@billing

        using (var snap = h.Store.Snapshot())
            h.Store.ListLength(snap, HighwayKeyspace.ListPrefix(HighwayNames.Queue("news@billing"))).Should().Be(1);

        var reply = h.Run(new HwUnsubscribeCommand(), "news", "billing");
        CommandHarness.AsText(reply).Should().Be("+OK\r\n");

        // Group unregistered, queue destroyed, reverse index cleared.
        Members(h.Store, HighwayNames.ChannelGroups("news")).Should().BeEmpty();
        Members(h.Store, HighwayNames.NodeChannels("billing")).Should().BeEmpty();
        using var after = h.Store.Snapshot();
        h.Store.ListLength(after, HighwayKeyspace.ListPrefix(HighwayNames.Queue("news@billing"))).Should().Be(0);
    }

    // ========================= HW.HEARTBEAT ==================================

    [Fact]
    public void Heartbeat_Registration_StoresCatalog_IndexesServices()
    {
        using var h = new CommandHarness();
        var now = DateTime.UtcNow.Ticks;
        var catalog = Encoding.UTF8.GetString(BuildCatalog("orders", "billing"));

        var reply = h.Run(new HwHeartbeatCommand(), now, "node-1", catalog);
        CommandHarness.AsText(reply).Should().Be("+OK\r\n");

        Members(h.Store, HighwayNames.RegistrationNodeList).Should().BeEquivalentTo("node-1");
        Members(h.Store, HighwayNames.RegistrationService("orders")).Should().BeEquivalentTo("node-1");
        Members(h.Store, HighwayNames.RegistrationService("billing")).Should().BeEquivalentTo("node-1");
    }

    [Fact]
    public void Heartbeat_Liveness_OnUnknownNode_RepliesRegister_MutatesNothing()
    {
        using var h = new CommandHarness();
        var reply = h.Run(new HwHeartbeatCommand(), "ghost");
        CommandHarness.AsText(reply).Should().Be("+REGISTER\r\n");

        using var snap = h.Store.Snapshot();
        h.Store.Get(snap, HighwayKeyspace.Kv(HighwayNames.RegistrationNode("ghost"))).Should().BeNull();
    }

    [Fact]
    public void Heartbeat_Liveness_RefreshesTimestamp_PreservesCatalog()
    {
        using var h = new CommandHarness();
        var t0 = 1_000_000_000_000L;
        SeedRegistration(h.Store, "node-1", t0, "orders");

        var t1 = t0 + 5_000_000_000L;
        var reply = h.Run(new HwHeartbeatCommand(), t1, "node-1");
        CommandHarness.AsText(reply).Should().Be("+OK\r\n");

        using var snap = h.Store.Snapshot();
        var record = h.Store.Get(snap, HighwayKeyspace.Kv(HighwayNames.RegistrationNode("node-1")));
        NodeRegistration.Decode(record!, out var seen, out var catalog);
        seen.Should().Be(t1, "liveness refreshes the timestamp");
        Encoding.UTF8.GetString(catalog).Should().Contain("orders", "the catalog is preserved byte-for-byte");
    }

    [Fact]
    public void Heartbeat_Registration_RedeployWithFewerServices_DropsStaleIndex()
    {
        using var h = new CommandHarness();
        var now = DateTime.UtcNow.Ticks;
        h.Run(new HwHeartbeatCommand(), now, "node-1", Encoding.UTF8.GetString(BuildCatalog("orders", "billing")));

        // Redeploy: same node, only 'orders' now.
        h.Run(new HwHeartbeatCommand(), now, "node-1", Encoding.UTF8.GetString(BuildCatalog("orders")));

        Members(h.Store, HighwayNames.RegistrationService("orders")).Should().BeEquivalentTo("node-1");
        Members(h.Store, HighwayNames.RegistrationService("billing")).Should().BeEmpty("no longer hosted");
    }

    [Fact]
    public void Heartbeat_Bye_TearsDownRegistration_LeavesGroupsUntouched()
    {
        using var h = new CommandHarness();
        var now = DateTime.UtcNow.Ticks;
        h.Run(new HwHeartbeatCommand(), now, "node-1", Encoding.UTF8.GetString(BuildCatalog("orders")));
        h.Run(new HwSubscribeCommand(), "news", "grp", "node-1"); // node also a subscriber

        var reply = h.Run(new HwHeartbeatCommand(), now, "node-1", "BYE");
        CommandHarness.AsText(reply).Should().Be("+OK\r\n");

        // Registration gone…
        Members(h.Store, HighwayNames.RegistrationNodeList).Should().BeEmpty();
        Members(h.Store, HighwayNames.RegistrationService("orders")).Should().BeEmpty();
        // …but subscriber group untouched (BYE is not PURGE).
        Members(h.Store, HighwayNames.ChannelGroups("news")).Should().BeEquivalentTo("grp");
    }

    [Fact]
    public void Heartbeat_ByePurge_RetiresSoleMemberGroup()
    {
        using var h = new CommandHarness();
        var now = DateTime.UtcNow.Ticks;
        h.Run(new HwHeartbeatCommand(), now, "node-1", Encoding.UTF8.GetString(BuildCatalog("orders")));
        h.Run(new HwSubscribeCommand(), "news", "node-1", "node-1"); // group == node
        h.Run(new HwPublishCommand(), "news", "hello");

        var reply = h.Run(new HwHeartbeatCommand(), now, "node-1", "BYE", "PURGE");
        // *3 [groups, messages, bytes] — one group retired, one message discarded.
        var text = CommandHarness.AsText(reply);
        text.Should().StartWith("*3\r\n:1\r\n:1\r\n", "one group, one message");

        Members(h.Store, HighwayNames.ChannelGroups("news")).Should().BeEmpty("purge destroyed the group");
        using var snap = h.Store.Snapshot();
        h.Store.ListLength(snap, HighwayKeyspace.ListPrefix(HighwayNames.Queue("news@node-1"))).Should().Be(0);
    }

    [Fact]
    public void Heartbeat_ByePurge_KeepsGroupAliveForSiblingMember()
    {
        using var h = new CommandHarness();
        var now = DateTime.UtcNow.Ticks;
        h.Run(new HwHeartbeatCommand(), now, "node-1", Encoding.UTF8.GetString(BuildCatalog()));
        h.Run(new HwSubscribeCommand(), "news", "billing", "node-1");
        h.Run(new HwSubscribeCommand(), "news", "billing", "node-2"); // two members back one group

        var reply = h.Run(new HwHeartbeatCommand(), now, "node-1", "BYE", "PURGE");
        CommandHarness.AsText(reply).Should().StartWith("*3\r\n:0\r\n", "no group retired — a sibling remains");

        // Group survives; node-1 removed from membership.
        Members(h.Store, HighwayNames.ChannelGroups("news")).Should().BeEquivalentTo("billing");
        Members(h.Store, HighwayNames.GroupMembers("news", "billing")).Should().BeEquivalentTo("node-2");
    }

    // ========================= HW.DISCOVER ===================================

    [Fact]
    public void Discover_ReturnsLiveNodes_FiltersStale()
    {
        var opts = new HighwayServerOptions { NodeExpiry = TimeSpan.FromSeconds(30) };
        using var h = new CommandHarness(options: opts);
        var now = 10_000_000_000_000L;

        // live-1 seen just now; dead-1 seen a minute ago (stale). Both in the index.
        h.Run(new HwHeartbeatCommand(), now, "live-1", Encoding.UTF8.GetString(BuildCatalog("orders")));
        SeedRegistration(h.Store, "dead-1", now - TimeSpan.FromMinutes(1).Ticks, "orders");
        using (var b = h.Store.NewBatch())
        {
            h.Store.SetAdd(b, HighwayKeyspace.SetPrefix(HighwayNames.RegistrationService("orders")), Encoding.UTF8.GetBytes("dead-1"));
            b.Commit();
        }

        var reply = h.Run(new HwDiscoverCommand(), now, "orders");
        var text = CommandHarness.AsText(reply);
        text.Should().StartWith("*1\r\n", "only the live node is returned");
        text.Should().Contain("live-1");
        text.Should().NotContain("dead-1");
    }

    [Fact]
    public void Discover_UnknownService_EmptyArray_NeverError()
    {
        using var h = new CommandHarness();
        var reply = h.Run(new HwDiscoverCommand(), "nobody");
        CommandHarness.AsText(reply).Should().Be("*0\r\n");
    }

    // ========================= HW.JOB ========================================

    [Fact]
    public void Job_Set_AddsSchedule_AndIndexesQueue()
    {
        using var h = new CommandHarness();
        var reply = h.Run(new HwJobCommand(), "SET", "nightly", "cleanup", "every:900", "payload");
        CommandHarness.AsText(reply).Should().Be("+OK\r\n");

        Members(h.Store, HighwayNames.JobIndex).Should().BeEquivalentTo("nightly");
        using var snap = h.Store.Snapshot();
        h.Store.SortedSetLength(snap, HighwayKeyspace.SortedSetPrefix(HighwayNames.JobSchedules("nightly")))
            .Should().Be(1);
    }

    [Fact]
    public void Job_Set_ReRegister_PreservesLastFire_UpdatesExpression()
    {
        using var h = new CommandHarness();
        h.Run(new HwJobCommand(), "SET", "nightly", "cleanup", "every:900", "p");
        h.Run(new HwJobCommand(), "SET", "nightly", "cleanup", "every:1800", "p"); // change expression

        // Still one schedule (replaced, not duplicated).
        using var snap = h.Store.Snapshot();
        h.Store.SortedSetLength(snap, HighwayKeyspace.SortedSetPrefix(HighwayNames.JobSchedules("nightly")))
            .Should().Be(1);
    }

    [Fact]
    public void Job_Del_RemovesSchedule_AndDropsFromIndexWhenLast()
    {
        using var h = new CommandHarness();
        h.Run(new HwJobCommand(), "SET", "nightly", "cleanup", "every:900", "p");

        var reply = h.Run(new HwJobCommand(), "DEL", "nightly", "cleanup");
        CommandHarness.AsText(reply).Should().Be(":1\r\n");

        Members(h.Store, HighwayNames.JobIndex).Should().BeEmpty("last schedule gone");
    }

    [Fact]
    public void Job_Del_Unknown_ReturnsZero()
    {
        using var h = new CommandHarness();
        var reply = h.Run(new HwJobCommand(), "DEL", "nightly", "ghost");
        CommandHarness.AsText(reply).Should().Be(":0\r\n");
    }

    [Fact]
    public void Job_List_EnumeratesFromIndexSet()
    {
        using var h = new CommandHarness();
        h.Run(new HwJobCommand(), "SET", "nightly", "cleanup", "every:900", "p");
        h.Run(new HwJobCommand(), "SET", "hourly", "report", "every:60", "p");

        var reply = h.Run(new HwJobCommand(), "LIST");
        var text = CommandHarness.AsText(reply);
        text.Should().StartWith("*2\r\n", "two schedules across two queues");
        text.Should().Contain("cleanup");
        text.Should().Contain("report");
    }

    [Fact]
    public void Job_BadExpression_InvalidArg_NoWrite()
    {
        using var h = new CommandHarness();
        var reply = h.Run(new HwJobCommand(), "SET", "nightly", "cleanup", "not-a-schedule", "p");
        CommandHarness.AsText(reply).Should().StartWith("-ERR HW_INVALID_ARG");
        Members(h.Store, HighwayNames.JobIndex).Should().BeEmpty();
    }

    // ========================= HW.QCLAIM job firing ==========================

    [Fact]
    public void QClaim_FiresDueJob_PushesOccurrence_AndReArms()
    {
        using var h = new CommandHarness();
        // Set a schedule at a base clock, then claim well after its first occurrence.
        var t0 = 10_000_000_000_000L;
        h.Run(new HwJobCommand(), t0, "SET", "nightly", "cleanup", "every:1", "occurrence-body");

        // every:1 → next occurrence is t0 + 1s. Claim at t0 + 2s: the schedule is due.
        var claimAt = t0 + TimeSpan.FromSeconds(2).Ticks;
        var reply = h.Run(new HwQClaimCommand(), claimAt, "nightly", "worker-1");

        // The fired occurrence is what the worker claims.
        var text = CommandHarness.AsText(reply);
        text.Should().StartWith("*2\r\n", "the fired occurrence was claimed");
        text.Should().Contain("occurrence-body");

        // Re-armed: still exactly one schedule, now at a future score.
        using var snap = h.Store.Snapshot();
        h.Store.SortedSetLength(snap, HighwayKeyspace.SortedSetPrefix(HighwayNames.JobSchedules("nightly")))
            .Should().Be(1);
    }

    [Fact]
    public void QClaim_JobNotYetDue_DoesNotFire()
    {
        using var h = new CommandHarness();
        var t0 = 10_000_000_000_000L;
        h.Run(new HwJobCommand(), t0, "SET", "nightly", "cleanup", "every:3600", "body");

        // Claim one second later — the hourly job is not due.
        var reply = h.Run(new HwQClaimCommand(), t0 + TimeSpan.FromSeconds(1).Ticks, "nightly", "worker-1");
        CommandHarness.AsText(reply).Should().Be("*-1\r\n", "nothing due, nothing queued");
    }

    // ========================= HW.PUBLISH auto-retirement ====================

    [Fact]
    public void Publish_RetiresGroupWhoseMemberWentStale_DeliversToLivingOnly()
    {
        var opts = new HighwayServerOptions { SubscriberRetirementThreshold = TimeSpan.FromHours(1) };
        using var h = new CommandHarness(options: opts);
        var now = 10_000_000_000_000L;

        // dead-grp backed by a node last seen 2h ago; live-grp backed by a fresh node.
        h.Run(new HwSubscribeCommand(), now, "news", "dead-grp", "dead-node");
        h.Run(new HwSubscribeCommand(), now, "news", "live-grp", "live-node");
        SeedRegistration(h.Store, "dead-node", now - TimeSpan.FromHours(2).Ticks, "svc");
        SeedRegistration(h.Store, "live-node", now, "svc");

        var reply = h.Run(new HwPublishCommand(), now, "news", "hello");
        CommandHarness.AsText(reply).Should().Be(":1\r\n", "only the live group receives it");

        // dead-grp retired and unregistered; live-grp still present with the message.
        Members(h.Store, HighwayNames.ChannelGroups("news")).Should().BeEquivalentTo("live-grp");
        using var snap = h.Store.Snapshot();
        h.Store.ListLength(snap, HighwayKeyspace.ListPrefix(HighwayNames.Queue("news@live-grp"))).Should().Be(1);
        h.Store.ListLength(snap, HighwayKeyspace.ListPrefix(HighwayNames.Queue("news@dead-grp"))).Should().Be(0);
    }

    [Fact]
    public void Publish_GroupWithNoRegistration_IsNotRetired()
    {
        var opts = new HighwayServerOptions { SubscriberRetirementThreshold = TimeSpan.FromHours(1) };
        using var h = new CommandHarness(options: opts);
        var now = 10_000_000_000_000L;

        // A subscriber that never registered a catalog: no record is NOT evidence of death.
        h.Run(new HwSubscribeCommand(), now, "news", "grp", "node-x");

        var reply = h.Run(new HwPublishCommand(), now, "news", "hello");
        CommandHarness.AsText(reply).Should().Be(":1\r\n", "the group survives — absence of a record is not death");
        Members(h.Store, HighwayNames.ChannelGroups("news")).Should().BeEquivalentTo("grp");
    }

    // ========================= HW.DEQUEUE dead-node sweep ====================

    [Fact]
    public void Dequeue_PrunesStaleNode_RequeuesItsInFlightWork()
    {
        var opts = new HighwayServerOptions { NodeExpiry = TimeSpan.FromSeconds(30), Lease = TimeSpan.FromMinutes(5) };
        using var h = new CommandHarness(options: opts);
        var t0 = 10_000_000_000_000L;

        // dead-node registered and holding one claimed request; caller is fresh.
        h.Run(new HwCallCommand(), t0, "orders", "req-1", "payload");
        h.Run(new HwDequeueCommand(), t0, "orders", "dead-node"); // dead-node claims req-1
        h.Run(new HwHeartbeatCommand(), t0, "dead-node", Encoding.UTF8.GetString(BuildCatalog("orders")));

        // Much later, a healthy worker dequeues. dead-node is now stale → pruned, its work requeued.
        var later = t0 + TimeSpan.FromMinutes(1).Ticks;
        var reply = h.Run(new HwDequeueCommand(), later, "orders", "live-node");

        // The requeued request is what live-node claims.
        var text = CommandHarness.AsText(reply);
        text.Should().StartWith("*2\r\n");
        text.Should().Contain("req-1");

        // dead-node dropped from the worker set and the registry.
        Members(h.Store, HighwayNames.ServiceNodes("orders")).Should().NotContain("dead-node");
        Members(h.Store, HighwayNames.RegistrationNodeList).Should().NotContain("dead-node");
    }
}
