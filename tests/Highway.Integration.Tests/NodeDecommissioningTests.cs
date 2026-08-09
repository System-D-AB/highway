using System.Text;
using Highway.Abstractions;
using FluentAssertions;
using Highway.Server;
using StackExchange.Redis;
using Xunit;

namespace Highway.Integration.Tests;

/// <summary>
/// Feature 017 — retiring a subscriber that is not coming back.
///
/// <para><b>The hazard.</b> Feature 016 turned a memory leak into an outage: a crashed
/// subscriber's group queue fills to <c>MaxQueueBytes</c>, and because a fan-out reaches every
/// registered group or none (018), every publish to that channel is then refused. One dead
/// subscriber takes down a live channel. 016's Open Decision 5 accepted that cost on the
/// condition this feature would exist.</para>
///
/// <para><b>The join.</b> The broker already knew the node was dead — feature 006's heartbeat
/// registry tracks liveness, and 018 made a subscriber group <i>be</i> a node. Both facts sat
/// in the same process and had never been introduced.</para>
/// </summary>
[Channel("dc.forever")]
public sealed record RetiredEvent : IPublish
{
    public string Note { get; init; } = "";
}

/// <summary>A subscriber so the node registers a group that retirement can then destroy.</summary>
public sealed class RetiredEventSubscriber : ISubscribe<RetiredEvent>
{
    public Task SubscribeAsync(RetiredEvent message, CancellationToken ct = default)
        => Task.CompletedTask;
}

public class NodeDecommissioningTests : IDisposable
{
    private const long Limit = 16 * 1024;

    // A retirement threshold short enough to test, and a node expiry short enough that a
    // registration goes stale within it.
    private readonly HighwayTestServer _server = new(o =>
    {
        o.MaxQueueBytes = Limit;
        o.SubscriberRetirementThreshold = TimeSpan.FromMilliseconds(300);
    });

    public void Dispose() => _server.Dispose();

    private IDatabase Db() => ConnectionMultiplexer.Connect(_server.ConnectionString).GetDatabase();

    private static byte[] Payload(int bytes = 2048)
        => Encoding.UTF8.GetBytes(
            "{\"v\":1,\"src\":\"t\",\"ts\":\"2026-08-09T00:00:00Z\",\"body\":{\"Blob\":\""
            + new string('x', bytes) + "\"}}");

    /// <summary>Registers a node so it has a heartbeat record that can then go stale.</summary>
    private static void Register(IDatabase db, string node, string service = "svc.x")
        => db.Execute("HW.HEARTBEAT", node,
            // The catalog's services are OBJECTS with a name, not bare strings — a plain
            // string array parses as "a pure caller with no services" and silently registers
            // nothing, which is how the first version of these tests failed.
            $$"""{"nodeId":"{{node}}","services":[{"name":"{{service}}"}],"channels":[],"queues":[]}""");

    // ---- T8: the feature's reason to exist ------------------------------------

    /// <summary>
    /// Fill a group until publishes are refused, let its node go stale, publish again — and the
    /// channel recovers. Everything else in this feature is plumbing whose purpose is
    /// unverified without this.
    /// </summary>
    [Fact]
    public async Task ABlockedChannel_RecoversWhenTheDeadGroupIsRetired()
    {
        var db = Db();
        const string channel = "dc.blocked";

        Register(db, "dead-node");
        db.Execute("HW.SUBSCRIBE", channel, "dead-node");

        // Fill the dead node's queue until the channel refuses.
        RedisServerException? refusal = null;
        for (var i = 0; i < 50 && refusal is null; i++)
        {
            try { db.Execute("HW.PUBLISH", channel, Payload()); }
            catch (RedisServerException ex) { refusal = ex; }
        }

        refusal.Should().NotBeNull("016 refuses a publish when any group's queue is full");
        refusal!.Message.Should().Contain("dead-node", "and names the group that caused it");

        // The node stops heartbeating and passes the retirement threshold.
        await Task.Delay(500);

        // The publish that would have been refused is the one that clears the blockage.
        var act = () => db.Execute("HW.PUBLISH", channel, Payload());
        act.Should().NotThrow("retiring the dead group releases its byte budget");

        ((long)db.Execute("EXISTS", $"hw:q:{channel}@dead-node:q")).Should().Be(0,
            "the retired group's queue is deleted, not merely unregistered");
    }

    [Fact]
    public async Task RetirementLeavesHealthyGroupsUntouched()
    {
        var db = Db();
        const string channel = "dc.mixed";

        Register(db, "dead-one");
        db.Execute("HW.SUBSCRIBE", channel, "dead-one");
        db.Execute("HW.PUBLISH", channel, Payload(64));

        await Task.Delay(500);   // dead-one goes stale

        // A live subscriber joins and keeps heartbeating.
        Register(db, "live-one");
        db.Execute("HW.SUBSCRIBE", channel, "live-one");
        db.Execute("HW.PUBLISH", channel, Payload(64));

        ((long)db.Execute("EXISTS", $"hw:q:{channel}@dead-one:q")).Should().Be(0,
            "the stale group is retired");
        ((long)db.Execute("LLEN", $"hw:q:{channel}@live-one:q")).Should().Be(1,
            "a live subscriber must be unaffected by its neighbour's retirement");
    }

    /// <summary>
    /// The difference between Highway and a blind idle timer. RabbitMQ's <c>x-expires</c> and
    /// Azure's <c>AutoDeleteOnIdle</c> would retire this group; Highway must not, because the
    /// node is demonstrably alive.
    /// </summary>
    [Fact]
    public async Task AnUnconsumedButLiveGroup_IsNotRetired()
    {
        var db = Db();
        const string channel = "dc.live";

        Register(db, "batch-node");
        db.Execute("HW.SUBSCRIBE", channel, "batch-node");
        db.Execute("HW.PUBLISH", channel, Payload(64));

        // Nobody ever consumes — like a nightly batch job — but the node keeps heartbeating.
        for (var i = 0; i < 5; i++)
        {
            await Task.Delay(120);
            db.Execute("HW.HEARTBEAT", "batch-node");
        }

        db.Execute("HW.PUBLISH", channel, Payload(64));

        ((long)db.Execute("LLEN", $"hw:q:{channel}@batch-node:q")).Should().Be(2,
            "retirement is driven by LIVENESS EVIDENCE, not a consumption gap - a group nobody " +
            "has consumed from is not dead, but a group whose node stopped heartbeating is");
    }

    [Fact]
    public void ANeverRegisteredSubscriber_IsNotRetired()
    {
        var db = Db();
        const string channel = "dc.noreg";

        // No HW.HEARTBEAT: this node has no registration record at all.
        db.Execute("HW.SUBSCRIBE", channel, "anon-node");
        db.Execute("HW.PUBLISH", channel, Payload(64));
        db.Execute("HW.PUBLISH", channel, Payload(64));

        ((long)db.Execute("LLEN", $"hw:q:{channel}@anon-node:q")).Should().Be(2,
            "absence of a registration is not evidence of death - only a record that EXISTS " +
            "and has gone stale proves the node was here and stopped");
    }

    // ---- the explicit paths ---------------------------------------------------

    [Fact]
    public void ByePurge_RetiresSubscriptionsAndReportsWhatItDestroyed()
    {
        var db = Db();
        const string channel = "dc.purge";

        Register(db, "going-node");
        db.Execute("HW.SUBSCRIBE", channel, "going-node");
        db.Execute("HW.PUBLISH", channel, Payload(64));
        db.Execute("HW.PUBLISH", channel, Payload(64));

        var result = (RedisResult[])db.Execute("HW.HEARTBEAT", "going-node", "BYE", "PURGE")!;

        ((long)result[0]).Should().Be(1, "one group retired");
        ((long)result[1]).Should().Be(2, "two messages discarded");
        ((long)result[2]).Should().BeGreaterThan(0, "and the bytes they occupied");

        ((long)db.Execute("EXISTS", $"hw:q:{channel}@going-node:q")).Should().Be(0);
    }

    /// <summary>
    /// Plain <c>BYE</c> is "I am stopping"; <c>BYE PURGE</c> is "I am never coming back". The
    /// difference is the subscriber's backlog, and confusing them loses data.
    /// </summary>
    [Fact]
    public void PlainBye_KeepsTheSubscriberBacklog()
    {
        var db = Db();
        const string channel = "dc.bye";

        Register(db, "restarting-node");
        db.Execute("HW.SUBSCRIBE", channel, "restarting-node");
        db.Execute("HW.PUBLISH", channel, Payload(64));

        db.Execute("HW.HEARTBEAT", "restarting-node", "BYE");

        ((long)db.Execute("LLEN", $"hw:q:{channel}@restarting-node:q")).Should().Be(1,
            "a node that shuts down cleanly still expects its pending messages when it returns");
    }

    [Fact]
    public void RetiringAnUnknownNode_ReturnsZeroRatherThanFailing()
    {
        var result = (RedisResult[])Db().Execute("HW.HEARTBEAT", "never-existed", "BYE", "PURGE")!;

        ((long)result[0]).Should().Be(0,
            "an operator cleaning up after an incident should not have to know which names " +
            "still exist");
    }

    [Fact]
    public void RetirementIsIdempotent()
    {
        var db = Db();
        Register(db, "twice-node");
        db.Execute("HW.SUBSCRIBE", "dc.twice", "twice-node");

        db.Execute("HW.HEARTBEAT", "twice-node", "BYE", "PURGE");
        var second = (RedisResult[])db.Execute("HW.HEARTBEAT", "twice-node", "BYE", "PURGE")!;

        ((long)second[0]).Should().Be(0, "retiring a node twice is not an error");
    }

    [Fact]
    public void AReturningNode_StartsEmpty()
    {
        var db = Db();
        const string channel = "dc.return";

        Register(db, "back-node");
        db.Execute("HW.SUBSCRIBE", channel, "back-node");
        db.Execute("HW.PUBLISH", channel, Payload(64));
        db.Execute("HW.HEARTBEAT", "back-node", "BYE", "PURGE");

        // It comes back and re-subscribes.
        Register(db, "back-node");
        db.Execute("HW.SUBSCRIBE", channel, "back-node");

        ((long)db.Execute("LLEN", $"hw:q:{channel}@back-node:q")).Should().Be(0,
            "retirement is irreversible by design - this is C2.4 working as intended, not a bug");
    }

    [Fact]
    public void ThresholdZero_DisablesAutomaticRetirement()
    {
        using var never = new HighwayTestServer(o =>
        {
            o.MaxQueueBytes = Limit;
            o.SubscriberRetirementThreshold = TimeSpan.Zero;
        });
        var db = ConnectionMultiplexer.Connect(never.ConnectionString).GetDatabase();

        Register(db, "kept-node");
        db.Execute("HW.SUBSCRIBE", "dc.off", "kept-node");
        db.Execute("HW.PUBLISH", "dc.off", Payload(64));

        Thread.Sleep(400);
        db.Execute("HW.PUBLISH", "dc.off", Payload(64));

        ((long)db.Execute("LLEN", "hw:q:dc.off@kept-node:q")).Should().Be(2,
            "a deployment may prefer the outage to the deletion, and zero means it");
    }

    // ---- CleanAndByeForever (T4) ----------------------------------------------

    /// <summary>
    /// <b>The defect T4 warns about.</b> The heartbeat loop re-registers the node, so a purge
    /// issued while it still runs is undone moments later and the node reappears with an empty
    /// catalog — which looks exactly like a purge that worked and then silently did not.
    ///
    /// <para>Asserting immediately after the call would pass either way. This waits out a full
    /// heartbeat interval, which is the only version of this test worth having.</para>
    /// </summary>
    [Fact]
    public async Task CleanAndByeForever_StaysGone_AcrossAHeartbeatInterval()
    {
        var db = Db();
        const string channel = "dc.forever";

        await using (var node = await EngineNode.StartAsync(_server.ConnectionString, "forever-node"))
        {
            await node.Client.PublishAsync(new RetiredEvent { Note = "before" });

            var destroyed = await node.Engine.CleanAndByeForeverAsync();
            destroyed.Groups.Should().BeGreaterThanOrEqualTo(0);
        }

        // Long enough for a heartbeat to have resurrected it, had the loops still been running.
        await Task.Delay(1_500);

        ((long)db.Execute("EXISTS", $"hw:reg:node:forever-node")).Should().Be(0,
            "the node must still be gone after a full heartbeat interval - stopping the loops " +
            "AFTER the purge would let the next beat re-register it");
        _ = channel;
    }

    // ---- the asymmetry (R4) ---------------------------------------------------

    /// <summary>
    /// Retirement destroys the subscriber's own queue but <b>requeues</b> RPC work, in one test
    /// so the difference is impossible to miss. A caller may still be waiting on that request;
    /// destroying it turns a slow answer into no answer.
    /// </summary>
    [Fact]
    public void RetiringANode_DeletesItsSubscriptions_ButRequeuesItsRpcWork()
    {
        var db = Db();
        const string channel = "dc.asym";
        const string service = "dc.svc";

        Register(db, "both-node", service);
        db.Execute("HW.SUBSCRIBE", channel, "both-node");
        db.Execute("HW.PUBLISH", channel, Payload(64));

        db.Execute("HW.CALL", service, "req-1", Payload(64));
        db.Execute("HW.DEQUEUE", service, "both-node");   // claimed, never acknowledged

        db.Execute("HW.HEARTBEAT", "both-node", "BYE", "PURGE");

        ((long)db.Execute("EXISTS", $"hw:q:{channel}@both-node:q")).Should().Be(0,
            "the subscriber's messages were addressed to it alone - nobody else can process them");

        ((long)db.Execute("LLEN", $"hw:svc:{service}:q")).Should().Be(1,
            "the RPC request goes back on the queue - a caller may still be waiting for it");
    }
}
