using FluentAssertions;
using Highway.Abstractions;
using Highway.Client;
using Highway.Server;
using StackExchange.Redis;
using Xunit;

namespace Highway.Integration.Tests;

/// <summary>
/// Feature 025 — <b>SubscriptionGroup names the logical consumer; NodeName names the process.</b>
///
/// <para>Replicas sharing a group compete for one copy of each publish through the ordinary
/// queue machinery (the claimant IS the group); distinct groups each receive their own copy;
/// a group dies only when its last member is gone. The default — group = NodeName — is
/// 018's behavior unchanged, which the entire pre-existing suite already proves.</para>
/// </summary>
[Collection(SubscriberRecorderCollection.Name)]
public class SubscriptionGroupTests : IDisposable
{
    private readonly HighwayTestServer _server = new(o =>
    {
        o.Lease = TimeSpan.FromMilliseconds(300);   // fast redelivery for the recovery test
    });

    private readonly ConnectionMultiplexer _redis;
    private readonly IDatabase _db;

    public SubscriptionGroupTests()
    {
        SubscriberRecorder.Reset();
        SgFlakySubscriber.FailedOnce.Clear();
        _redis = ConnectionMultiplexer.Connect(_server.ConnectionString);
        _db = _redis.GetDatabase();
    }

    public void Dispose()
    {
        _redis.Dispose();
        _server.Dispose();
    }

    // ------------------------------------------------------------------ engines

    [Fact]
    public async Task Replicas_SharingAGroup_CompeteForOneCopy()
    {
        await using var replica1 = await EngineNode.StartAsync(
            _server.ConnectionString, "sgc-1", o => o.SubscriptionGroup = "sgc-billing");
        await using var replica2 = await EngineNode.StartAsync(
            _server.ConnectionString, "sgc-2", o => o.SubscriptionGroup = "sgc-billing");

        for (var i = 0; i < 6; i++)
            await replica1.Client.PublishAsync(new SgEvent { Data = $"c{i}" });

        // Every message delivered — and delivered ONCE between the two replicas.
        (await SubscriberRecorder.WaitForAsync(
                () => Enumerable.Range(0, 6).All(i => TotalFor($"c{i}") >= 1)))
            .Should().BeTrue("each published message must reach the group");

        // Settle, then the heart of the feature: no message was delivered twice.
        await Task.Delay(300);
        for (var i = 0; i < 6; i++)
            TotalFor($"c{i}").Should().Be(1,
                "replicas sharing a SubscriptionGroup must COMPETE, not each receive a copy");
    }

    [Fact]
    public async Task DistinctGroups_EachReceiveTheirOwnCopy()
    {
        // No SubscriptionGroup set — the default: every node is its own group (018 behavior).
        await using var nodeA = await EngineNode.StartAsync(_server.ConnectionString, "sgd-a");
        await using var nodeB = await EngineNode.StartAsync(_server.ConnectionString, "sgd-b");

        await nodeA.Client.PublishAsync(new SgEvent { Data = "fan" });

        (await SubscriberRecorder.WaitForAsync(() => TotalFor("fan") >= 2))
            .Should().BeTrue("both groups must receive the publish");

        await Task.Delay(200);
        SubscriberRecorder.CountEntries("sgd-a:fan").Should().Be(1);
        SubscriberRecorder.CountEntries("sgd-b:fan").Should().Be(1);
    }

    [Fact]
    public async Task FailedDelivery_IsRedeliveredToTheGroup_AndProcessedOnce()
    {
        await using var replica1 = await EngineNode.StartAsync(
            _server.ConnectionString, "sgf-1", o => o.SubscriptionGroup = "sgf-billing");
        await using var replica2 = await EngineNode.StartAsync(
            _server.ConnectionString, "sgf-2", o => o.SubscriptionGroup = "sgf-billing");

        // The first delivery attempt of this Data throws (whichever replica claims it); the
        // lease expires and the GROUP gets it again — either replica may finish the job.
        await replica1.Client.PublishAsync(new SgFlakyEvent { Data = "shaky" });

        (await SubscriberRecorder.WaitForAsync(
                () => SubscriberRecorder.CountEntries("sgf-1:ok:shaky")
                    + SubscriberRecorder.CountEntries("sgf-2:ok:shaky") >= 1,
                TimeSpan.FromSeconds(15)))
            .Should().BeTrue("a failed claim must return to the group, not be lost with the replica");

        await Task.Delay(400);
        (SubscriberRecorder.CountEntries("sgf-1:ok:shaky")
         + SubscriberRecorder.CountEntries("sgf-2:ok:shaky"))
            .Should().Be(1, "redelivery is at-least-once to the GROUP, exactly once here");
    }

    [Fact]
    public async Task Idempotent_MarkerIsGroupScoped_SoReplicasDoNotRerunEachOthersWork()
    {
        // Renewal off (MaxProcessingTime = 0): the first, slow handler must let its lease
        // lapse so the GROUP genuinely receives a redelivery while the work is in flight.
        await using var replica1 = await EngineNode.StartAsync(
            _server.ConnectionString, "sgi-1",
            o => { o.SubscriptionGroup = "sgi-billing"; o.MaxProcessingTime = TimeSpan.Zero; });
        await using var replica2 = await EngineNode.StartAsync(
            _server.ConnectionString, "sgi-2",
            o => { o.SubscriptionGroup = "sgi-billing"; o.MaxProcessingTime = TimeSpan.Zero; });

        await replica1.Client.PublishAsync(new SgIdemEvent { Data = "once" });

        (await SubscriberRecorder.WaitForAsync(
                () => SubscriberRecorder.CountEntries("sgi-1:idem:once")
                    + SubscriberRecorder.CountEntries("sgi-2:idem:once") >= 1,
                TimeSpan.FromSeconds(15)))
            .Should().BeTrue();

        // Let any pending redeliveries play out, then the two halves of the proof:
        await Task.Delay(1500);

        // 1. The handler ran exactly once across the group — the [Idempotent] marker is keyed
        //    by the DERIVED QUEUE {channel}@{group}, which replicas share, so replica-2 found
        //    replica-1's in-progress/completed marker instead of re-running the work.
        (SubscriberRecorder.CountEntries("sgi-1:idem:once")
         + SubscriberRecorder.CountEntries("sgi-2:idem:once"))
            .Should().Be(1, "the marker must dedup across REPLICAS, not per node");

        // 2. And that assertion is not vacuous: the broker's recorder shows the group really
        //    was handed the message more than once — the slow first attempt outlived its
        //    lease, so a second claim happened while the work was in flight. (Read in-process:
        //    HW.REPLAY's identifier rules reject the derived queue's '@'.)
        var claims = _server.Recorder
            .Read("sg.idem@sgi-billing", DateTimeOffset.MinValue, DateTimeOffset.MaxValue, null, int.MaxValue)
            .Count(e => e.EventType == Highway.Abstractions.Observability.HighwayEventType.QueueClaimed);

        claims.Should().BeGreaterThan(1, "without a second delivery the dedup was never exercised");
    }

    // ------------------------------------------------------------------ wire level

    [Fact]
    public void Membership_IsRecorded_AndSharedMarkerKeyIsGroupScoped()
    {
        _db.Execute("HW.SUBSCRIBE", "sgm.events", "sgm-g", "node-1");
        _db.Execute("HW.SUBSCRIBE", "sgm.events", "sgm-g", "node-2");
        _db.Execute("HW.SUBSCRIBE", "sgm.events", "sgm-g", "node-2"); // idempotent

        Members("sgm.events", "sgm-g").Should().Equal("node-1", "node-2");

        // The reverse index BYE PURGE walks.
        ((string?)_db.StringGet("hw:reg:node:node-1:subs")).Should().Contain("sgm.events@sgm-g");
    }

    [Fact]
    public void TwoArgumentSubscribe_KeepsThePre025Identity()
    {
        // An old client: no node argument. The group IS the node, so membership is {group} —
        // meaning retirement and purge behave exactly as 017 shipped them.
        _db.Execute("HW.SUBSCRIBE", "sgl.events", "sgl-old");

        Members("sgl.events", "sgl-old").Should().Equal("sgl-old");
    }

    [Fact]
    public void Purge_FromNonLastMember_PreservesTheGroup()
    {
        _db.Execute("HW.SUBSCRIBE", "sgp.events", "sgp-g", "sgp-node-a");
        _db.Execute("HW.SUBSCRIBE", "sgp.events", "sgp-g", "sgp-node-b");
        _db.Execute("HW.PUBLISH", "sgp.events", "{\"d\":1}");

        // First member departs forever: membership shrinks, nothing is destroyed.
        var first = (RedisResult[])_db.Execute("HW.HEARTBEAT", "sgp-node-a", "BYE", "PURGE")!;
        ((long)first[0]).Should().Be(0, "a shared group outlives any one member");

        Members("sgp.events", "sgp-g").Should().Equal("sgp-node-b");

        // The sibling's pending message is untouched and claimable.
        var claim = _db.Execute("HW.QCLAIM", "sgp.events@sgp-g", "sgp-g");
        claim.IsNull.Should().BeFalse("the group's queue must survive a non-last member's purge");

        // Last member departs: NOW the group dies, and says what it destroyed.
        var second = (RedisResult[])_db.Execute("HW.HEARTBEAT", "sgp-node-b", "BYE", "PURGE")!;
        ((long)second[0]).Should().Be(1, "the last member's purge destroys the group");

        ((string?)_db.StringGet($"hw:grp:members:sgp.events@sgp-g")).Should().BeNull();
    }

    [Fact]
    public async Task Retirement_SparesGroupWithOneLiveMember()
    {
        using var server = new HighwayTestServer(o =>
            o.SubscriberRetirementThreshold = TimeSpan.FromMilliseconds(400));
        using var redis = ConnectionMultiplexer.Connect(server.ConnectionString);
        var db = redis.GetDatabase();

        db.Execute("HW.SUBSCRIBE", "sgr.events", "sgr-g", "sgr-a");
        db.Execute("HW.SUBSCRIBE", "sgr.events", "sgr-g", "sgr-b");
        db.Execute("HW.HEARTBEAT", "sgr-a", "{\"services\":[],\"channels\":[]}");
        db.Execute("HW.HEARTBEAT", "sgr-b", "{\"services\":[],\"channels\":[]}");

        // Past the threshold for A; B keeps beating. The group must survive on B's pulse.
        await Task.Delay(600);
        db.Execute("HW.HEARTBEAT", "sgr-b");
        db.Execute("HW.PUBLISH", "sgr.events", "{\"d\":1}");

        ((string?)db.StringGet("hw:ch:sgr.events:grplist")).Should().Contain("sgr-g",
            "one live member keeps the whole group alive — liveness is the YOUNGEST member");

        // Now B goes silent too. The next publish retires the group (017's mechanism,
        // measuring 025's membership).
        await Task.Delay(600);
        db.Execute("HW.PUBLISH", "sgr.events", "{\"d\":2}");

        ((string?)db.StringGet("hw:ch:sgr.events:grplist") ?? "").Should().NotContain("sgr-g",
            "a group whose every member has gone silent past the threshold is retired");
    }

    [Fact]
    public async Task Catalogue_ListsAGroupsMembers_AsItsHosts()
    {
        _db.Execute("HW.SUBSCRIBE", "sgv.events", "sgv-g", "sgv-node-1");
        _db.Execute("HW.SUBSCRIBE", "sgv.events", "sgv-g", "sgv-node-2");
        _db.Execute("HW.PUBLISH", "sgv.events", "{\"d\":1}");

        // The projection the dashboard renders (no dashboard tests, per standing instruction —
        // the server computes, so the server is what gets tested).
        var catalogue = await _server.ReadCatalogueAsync();

        var group = catalogue.Should().Contain(e => e.Name == "sgv.events@sgv-g").Subject;
        group.Hosts.Should().Equal("sgv-node-1", "sgv-node-2");
    }

    // ------------------------------------------------------------------ helpers

    private static int TotalFor(string data)
        => SubscriberRecorder.CountEntries($"sgc-1:{data}") + SubscriberRecorder.CountEntries($"sgc-2:{data}")
         + SubscriberRecorder.CountEntries($"sgd-a:{data}") + SubscriberRecorder.CountEntries($"sgd-b:{data}");

    private string[] Members(string channel, string group)
        => ((string?)_db.StringGet($"hw:grp:members:{channel}@{group}") ?? "")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
}

// =============================================================================
// Fixtures. The subscribers record "{NodeName}:{Data}" so the tests can tell
// WHICH replica processed — the whole point of the feature.
// =============================================================================

[Channel("sg.events")]
public sealed class SgEvent : IPublish
{
    public string? Data { get; set; }
}

public sealed class SgRecordingSubscriber(HighwayOptions options) : ISubscribe<SgEvent>
{
    public Task SubscribeAsync(SgEvent message, CancellationToken ct = default)
    {
        SubscriberRecorder.Record($"{options.NodeName}:{message.Data}");
        return Task.CompletedTask;
    }
}

[Channel("sg.flaky")]
public sealed class SgFlakyEvent : IPublish
{
    public string? Data { get; set; }
}

[Channel("sg.idem")]
[Idempotent(WindowSeconds = 60)]
public sealed class SgIdemEvent : IPublish
{
    public string? Data { get; set; }
}

/// <summary>Slow on the first attempt — long enough to outlive the test server's 300ms lease.</summary>
public sealed class SgIdemSubscriber(HighwayOptions options) : ISubscribe<SgIdemEvent>
{
    private static int _attempts;

    public async Task SubscribeAsync(SgIdemEvent message, CancellationToken ct = default)
    {
        if (Interlocked.Increment(ref _attempts) == 1)
            await Task.Delay(800, ct);   // lease lapses; the group is redelivered mid-flight

        SubscriberRecorder.Record($"{options.NodeName}:idem:{message.Data}");
    }
}

/// <summary>Fails the FIRST delivery attempt of each Data — on whichever replica claims it.</summary>
public sealed class SgFlakySubscriber(HighwayOptions options) : ISubscribe<SgFlakyEvent>
{
    public static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> FailedOnce = new();

    public Task SubscribeAsync(SgFlakyEvent message, CancellationToken ct = default)
    {
        if (FailedOnce.TryAdd(message.Data ?? "", true))
            throw new InvalidOperationException("first attempt fails, by design");

        SubscriberRecorder.Record($"{options.NodeName}:ok:{message.Data}");
        return Task.CompletedTask;
    }
}
