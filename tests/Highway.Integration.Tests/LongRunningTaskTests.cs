using System.Text;
using FluentAssertions;
using Highway.Abstractions;
using Highway.Server;
using StackExchange.Redis;
using Xunit;

namespace Highway.Integration.Tests;

// ---- contracts -------------------------------------------------------------

[Queue("lr.slow")]
public sealed record SlowWork : ISend
{
    public string Tag { get; init; } = "";
}

/// <summary>Runs longer than the lease, and counts how many times it was started.</summary>
public sealed class SlowWorkProcessor : IProcess<SlowWork>
{
    public static int Starts;
    public static TimeSpan Duration = TimeSpan.FromSeconds(3);

    public async Task ProcessAsync(SlowWork message, CancellationToken ct = default)
    {
        Interlocked.Increment(ref Starts);
        await Task.Delay(Duration, ct);
    }
}

/// <summary>
/// Feature 019 — <b>a handler may run longer than the lease without being duplicated.</b>
///
/// <para>Before this, a worker that claimed a message started a clock it could not stop. A
/// handler outliving <c>Lease</c> had its message requeued <i>while it was still running</i> —
/// not a duplicate after a failure, but a concurrent duplicate caused by nothing more than
/// being slow. A twenty-minute job against a five-minute lease ran five times and then
/// dead-lettered, having done the work five times and reported failure.</para>
///
/// <para>The symptom was made actively misleading by 015: that dead letter says
/// <c>MAX_ATTEMPTS</c> with <c>failure: not reported</c>, because the handler never threw. An
/// operator reads "failed five times, no exception" about work that succeeded every time.</para>
/// </summary>
public class LongRunningTaskTests : IDisposable
{
    // A lease far shorter than the handler, so the defect would reproduce in seconds.
    private readonly HighwayTestServer _server = new(o =>
    {
        o.Lease = TimeSpan.FromMilliseconds(800);
        o.MaxDeliveryAttempts = 3;
    });

    public LongRunningTaskTests()
    {
        SlowWorkProcessor.Starts = 0;
        SlowWorkProcessor.Duration = TimeSpan.FromSeconds(3);
    }

    public void Dispose() => _server.Dispose();

    private IDatabase Db() => ConnectionMultiplexer.Connect(_server.ConnectionString).GetDatabase();

    private static byte[] Envelope()
        => Encoding.UTF8.GetBytes(
            """{"v":1,"src":"t","ts":"2026-08-09T00:00:00Z","body":{"Tag":"x"}}""");

    // ---- the feature ----------------------------------------------------------

    /// <summary>
    /// The headline. A 3-second handler against a 0.8-second lease must run <b>once</b>.
    /// Renewal is on by default, so this needs no configuration — which is the point: a
    /// developer should not have to read the source to discover their handler is not safe.
    /// </summary>
    [Fact]
    public async Task ASlowHandler_RunsOnce_NotOncePerLeasePeriod()
    {
        var db = Db();

        await using (var node = await EngineNode.StartAsync(
            _server.ConnectionString, "lr-node",
            o => o.LeaseRenewalInterval = TimeSpan.FromMilliseconds(200)))
        {
            db.Send("lr.slow", "msg-1", Envelope());

            // Long enough for the 0.8s lease to have expired three times over.
            await Task.Delay(4_500);
        }

        SlowWorkProcessor.Starts.Should().Be(1,
            "renewal keeps the claim alive while the handler runs - without it the sweep would " +
            "requeue the message underneath a handler that was working perfectly well");

        (_server.Inspect.ListLength("hw:q:lr.slow:dlq")).Should().Be(0,
            "and work that succeeds must never dead-letter");
    }

    /// <summary>
    /// The cap is the feature, not a limitation of it. Unbounded renewal would delete lease
    /// recovery: a deadlocked handler would hold its message forever, never redelivered, never
    /// dead-lettered, never visible as a problem.
    /// </summary>
    [Fact]
    public async Task AHungHandler_IsStillRecovered_OnceTheCapIsReached()
    {
        var db = Db();
        SlowWorkProcessor.Duration = TimeSpan.FromSeconds(30);   // "hung"

        await using (var node = await EngineNode.StartAsync(
            _server.ConnectionString, "lr-hung",
            o =>
            {
                o.LeaseRenewalInterval = TimeSpan.FromMilliseconds(200);
                o.MaxProcessingTime = TimeSpan.FromSeconds(1);   // give up quickly
            }))
        {
            db.Send("lr.slow", "msg-hung", Envelope());

            // Past the cap, renewal stops and the ordinary sweep takes over: requeue,
            // attempts++, and eventually the dead letter.
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < deadline)
            {
                if (_server.Inspect.ListLength("hw:q:lr.slow:dlq") > 0) break;
                await Task.Delay(200);
                db.ClaimQ("lr.slow", "sweeper");
            }
        }

        (_server.Inspect.ListLength("hw:q:lr.slow:dlq")).Should().BeGreaterThan(0,
            "past MaxProcessingTime the message returns to exactly the behaviour it had before " +
            "this feature - the cap does not invent a new outcome, it restores the old one");
    }

    [Fact]
    public async Task RenewalDisabled_RestoresThePreviousBehaviourExactly()
    {
        var db = Db();

        await using (var node = await EngineNode.StartAsync(
            _server.ConnectionString, "lr-off",
            o => o.MaxProcessingTime = TimeSpan.Zero))
        {
            db.Send("lr.slow", "msg-off", Envelope());
            await Task.Delay(4_000);
        }

        SlowWorkProcessor.Starts.Should().BeGreaterThan(1,
            "TimeSpan.Zero is the documented opt-out, and it must genuinely opt out - the " +
            "handler is duplicated again, which is the behaviour someone choosing this wants");
    }

    // ---- the command ----------------------------------------------------------

    [Fact]
    public void Touch_RenewsAClaimedEntry_WithoutAcknowledgingIt()
    {
        var db = Db();
        db.Send("lr.cmd", "msg-1", Envelope());
        var claim = db.ClaimQ("lr.cmd", "node-a");
        claim.Should().NotBeNull();

        var renewed = db.Touch(claim!.Value.Token);

        renewed.Should().Be(1);
        (_server.Inspect.ListLength("hw:q:lr.cmd:proc:node-a")).Should().Be(1,
            "renewal moves a deadline, it does not finish a message");
    }

    [Fact]
    public void Touch_ResetsTheClock_SoTheSweepLeavesTheEntryAlone()
    {
        // Its own server: a lease long enough to observe, short enough to test.
        using var slow = new HighwayTestServer(o => o.Lease = TimeSpan.FromMilliseconds(600));
        var db = ConnectionMultiplexer.Connect(slow.ConnectionString).GetDatabase();

        db.Send("lr.clock", "msg-1", Envelope());
        var claim = db.ClaimQ("lr.clock", "node-a");
        claim.Should().NotBeNull();

        // Keep renewing across more than a full lease period.
        for (var i = 0; i < 5; i++)
        {
            Thread.Sleep(200);
            db.Touch(claim!.Value.Token).Should().Be(1);
        }

        // A claim runs the sweep; the renewed entry must not have been requeued by it.
        db.ClaimQ("lr.clock", "other-node").Should().BeNull("the renewed claim is still held by node-a");

        (slow.Inspect.ListLength("hw:q:lr.clock:proc:node-a")).Should().Be(1,
            "the sweep decides expiry from the claim timestamp, so moving it forward IS " +
            "restarting the lease");
    }

    [Fact]
    public void Touch_OnAMessageThatIsGone_ReturnsZero()
    {
        var db = Db();
        db.Send("lr.gone", "msg-1", Envelope());
        var claim = db.ClaimQ("lr.gone", "node-a");
        claim.Should().NotBeNull();
        db.Ack(claim!.Value.Token).Should().Be(1);

        db.Touch(claim.Value.Token).Should().Be(0,
            "a late renewal is a race the client cannot avoid, not an error to investigate");
    }

    [Fact]
    public void Touch_PreservesTheFailureBlock()
    {
        var db = Db();
        db.Send("lr.block", "msg-1", Envelope());
        var claim = db.ClaimQ("lr.block", "node-a");
        claim.Should().NotBeNull();
        var token = claim!.Value.Token;

        db.Fail(token, "System.TimeoutException", "first").Should().Be(1);
        db.Touch(token).Should().Be(1);
        db.Fail(token, "System.InvalidOperationException", "second").Should().Be(1);

        // If renewal had rebuilt the entry from its decoded parts it would have dropped the
        // trailer, and firstType with it - the exact miss 015 made at three of four sites.
        // The lease is 800ms, so each pass has to outlast it or nothing ever expires and the
        // message never reaches the dead letter this test reads.
        for (var i = 0; i < 6; i++)
        {
            Thread.Sleep(900);
            db.ClaimQ("lr.block", "node-a");
        }

        var peeked = (RedisResult[])db.Execute("HW.DLQ", "PEEK", "Q", "lr.block")!;
        peeked.Should().NotBeEmpty();

        var flat = (RedisResult[])peeked[0]!;
        var map = new Dictionary<string, string>();
        for (var i = 0; i + 1 < flat.Length; i += 2)
            map[flat[i].ToString()!] = flat[i + 1].ToString()!;

        map.Should().ContainKey("failureFirstType").WhoseValue
            .Should().Be("System.TimeoutException",
                "renewal must carry the failure block across, not rebuild the entry without it");
    }

    [Fact]
    public void Touch_RejectsAMalformedToken_NamingWhereTokensComeFrom()
    {
        // 5.0: HW.TOUCH takes only the token (the SVC|Q target grammar moved to HW.CLAIM), so the
        // actionable rejection is now for a token that does not parse.
        var act = () => Db().Execute("HW.TOUCH", "not-a-token");

        act.Should().Throw<RedisServerException>()
            .WithMessage("ERR HW_INVALID_TOKEN*HW.CLAIM*");
    }

    [Fact]
    public void Touch_WorksForAnRpcRequest()
    {
        var db = Db();
        db.Execute("HW.CALL", "lr.svc", "req-1", Envelope());
        var claim = db.ClaimSvc("lr.svc", "node-a");
        claim.Should().NotBeNull();

        db.Touch(claim!.Value.Token).Should().Be(1,
            "one token grammar settles service claims exactly as it does queue claims");
    }
}
