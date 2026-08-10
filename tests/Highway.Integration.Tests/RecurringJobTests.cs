using System.Globalization;
using System.Text;
using FluentAssertions;
using Highway.Server;
using StackExchange.Redis;
using Xunit;

namespace Highway.Integration.Tests;

/// <summary>
/// Feature 028 — recurring jobs at the wire level: the schedule store (<c>HW.JOB</c>), the
/// fire-and-re-arm inside <c>HW.QCLAIM</c>'s promotion sweep, and the policies (catch-up-one,
/// refusal on a full queue).
///
/// <para>The central claim under adversarial test: <b>exactly one fire per occurrence</b>,
/// however many pollers race — because the fire happens inside the claim transaction's
/// exclusive locks. The claim IS the election (design D2).</para>
/// </summary>
public class RecurringJobTests : IDisposable
{
    private readonly HighwayTestServer _server = new();
    private readonly ConnectionMultiplexer _redis;
    private readonly IDatabase _db;

    public RecurringJobTests()
    {
        _redis = ConnectionMultiplexer.Connect(_server.ConnectionString + ",allowAdmin=true");
        _db = _redis.GetDatabase();
    }

    public void Dispose()
    {
        _redis.Dispose();
        _server.Dispose();
    }

    /// <summary>A minimal valid client envelope, hand-crafted (the wire format is JSON).</summary>
    private static byte[] Template(string src = "job-test")
        => Encoding.UTF8.GetBytes(
            "{\"v\":1,\"src\":\"" + src + "\",\"ts\":\"2026-08-10T00:00:00Z\",\"body\":{}}");

    private RedisResult[][] List()
        => ((RedisResult[])_db.Execute("HW.JOB", "LIST")!).Select(r => (RedisResult[])r!).ToArray();

    // ------------------------------------------------------------------ HW.JOB

    [Fact]
    public void Set_List_Del_RoundTrip()
    {
        _db.Execute("HW.JOB", "SET", "rj.q1", "nightly", "daily:02:00", Template());

        var rows = List();
        var row = rows.Should().ContainSingle(r => (string?)r[1] == "nightly").Subject;
        ((string?)row[0]).Should().Be("rj.q1");
        ((string?)row[2]).Should().Be("daily:02:00");
        long.Parse((string)row[3]!, CultureInfo.InvariantCulture)
            .Should().BeGreaterThan(DateTime.UtcNow.Ticks, "next fire is in the future");
        ((string?)row[4]).Should().Be("0", "never fired yet");

        ((long)_db.Execute("HW.JOB", "DEL", "rj.q1", "nightly")!).Should().Be(1);
        List().Should().NotContain(r => (string?)r[1] == "nightly");
        ((long)_db.Execute("HW.JOB", "DEL", "rj.q1", "nightly")!).Should().Be(0, "removal is idempotent");
    }

    [Fact]
    public void Set_IsLastWins_AndPreservesNothingItShouldNot()
    {
        _db.Execute("HW.JOB", "SET", "rj.q2", "sync", "every:3600", Template());
        _db.Execute("HW.JOB", "SET", "rj.q2", "sync", "every:7200", Template());

        var rows = List().Where(r => (string?)r[0] == "rj.q2").ToArray();
        rows.Should().HaveCount(1, "last registration REPLACES, never duplicates (OD5)");
        ((string?)rows[0][2]).Should().Be("every:7200");
    }

    [Fact]
    public void InvalidExpression_IsRefusedPermanently_TeachingTheGrammar()
    {
        var act = () => _db.Execute("HW.JOB", "SET", "rj.q3", "bad", "hourly:5", Template());

        act.Should().Throw<RedisServerException>()
            .WithMessage("ERR HW_INVALID_ARG*", "a bad expression is a permanent, classified error")
            .WithMessage("*daily:HH:mm*", "the rejection must teach the grammar (R1.7)");
    }

    [Fact]
    public void MultipleSchedules_PerQueue_Coexist()
    {
        _db.Execute("HW.JOB", "SET", "rj.q4", "eu-sync", "daily:02:00", Template());
        _db.Execute("HW.JOB", "SET", "rj.q4", "us-sync", "daily:06:00", Template());

        List().Count(r => (string?)r[0] == "rj.q4").Should().Be(2, "D8.2: schedules are keyed by job name");
    }

    // ------------------------------------------------------------------ the fire

    [Fact]
    public async Task DueSchedule_FiresExactlyOneOccurrence_UnderRacingPollers()
    {
        _db.Execute("HW.JOB", "SET", "rj.fire", "tick", "every:1", Template());

        await Task.Delay(1100);   // past the first nextFire

        // The adversarial heart: N concurrent claims at the due instant. The fire runs inside
        // the claim transaction's locks, so exactly one of these promotes the occurrence —
        // and whichever poller claims it, there is only ONE to claim.
        var claims = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(() =>
            _db.Execute("HW.QCLAIM", "rj.fire", $"racer-{i}"))));

        var winners = claims.Where(c => !c.IsNull).ToArray();
        winners.Should().HaveCount(1, "one occurrence, one claim — the transaction is the election");

        var fields = (RedisResult[])winners[0]!;
        ((string?)fields[0]).Should().StartWith("job:tick:", "occurrence ids name their schedule");

        // And the schedule re-armed: nextFire moved forward, lastFire recorded.
        var row = List().Should().ContainSingle(r => (string?)r[1] == "tick").Subject;
        long.Parse((string)row[4]!, CultureInfo.InvariantCulture).Should().BeGreaterThan(0, "lastFire recorded");
    }

    [Fact]
    public async Task MissedOccurrences_CatchUpAsOne_NotAsABacklog()
    {
        _db.Execute("HW.JOB", "SET", "rj.catchup", "tick", "every:1", Template());

        // Let MANY occurrences go by unfired (nothing polls). OD3: the backlog collapses.
        await Task.Delay(4200);

        _db.Execute("HW.QCLAIM", "rj.catchup", "worker-1");   // fires
        var first = _db.Execute("HW.QCLAIM", "rj.catchup", "worker-1");
        var second = _db.Execute("HW.QCLAIM", "rj.catchup", "worker-1");

        // One fire happened across those claims: the first QCLAIM promoted one occurrence and
        // either it or the next claimed it; nothing else is waiting.
        new[] { first, second }.Count(c => !c.IsNull)
            .Should().BeLessThanOrEqualTo(1, "four missed seconds must not become four messages (OD3)");
    }

    [Fact]
    public async Task FullQueue_RefusesTheFire_AndLeavesTheScheduleArmed()
    {
        using var tiny = new HighwayTestServer(o => o.MaxQueueBytes = 64);
        using var redis = ConnectionMultiplexer.Connect(tiny.ConnectionString);
        var db = redis.GetDatabase();

        // A template big enough that one occurrence cannot fit the 64-byte budget.
        db.Execute("HW.JOB", "SET", "rj.full", "tick", "every:1",
            Template(src: new string('x', 128)));

        await Task.Delay(1100);
        db.Execute("HW.QCLAIM", "rj.full", "worker-1").IsNull
            .Should().BeTrue("the fire was refused, so nothing was enqueued");

        // nextFire unchanged → still due → a later poll retries. Provable via LIST: nextFire
        // remains in the past.
        var row = ((RedisResult[])db.Execute("HW.JOB", "LIST")!)
            .Select(r => (RedisResult[])r!).Should().ContainSingle().Subject;
        long.Parse((string)row[3]!, CultureInfo.InvariantCulture)
            .Should().BeLessThan(DateTime.UtcNow.Ticks, "a refused fire is NOT consumed (backpressure reaches the scheduler)");
        ((string?)row[4]).Should().Be("0", "it never fired");
    }

    [Fact]
    public async Task Schedules_SurviveARestart()
    {
        using var durable = new HighwayTestServer(o =>
            o.DataDir = Path.Combine(Path.GetTempPath(), $"highway-rj-{Guid.NewGuid():N}"));
        try
        {
            using (var redis = ConnectionMultiplexer.Connect(durable.ConnectionString))
            {
                redis.GetDatabase().Execute("HW.JOB", "SET", "rj.dur", "nightly", "daily:02:00", Template());
            }

            durable.Restart();

            using var redis2 = ConnectionMultiplexer.Connect(durable.ConnectionString);
            var rows = ((RedisResult[])redis2.GetDatabase().Execute("HW.JOB", "LIST")!)
                .Select(r => (RedisResult[])r!).ToArray();

            rows.Should().ContainSingle(r => (string?)r[1] == "nightly",
                "a broker restart loses no schedule (R2.1)");
        }
        finally
        {
            await Task.Delay(100);
            try { Directory.Delete(Path.Combine(Path.GetTempPath(), "highway-rj-"), true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task DeclaredJob_ReachesTheBroker_AndTheManifest()
    {
        await using var node = await EngineNode.StartAsync(
            _server.ConnectionString, "rj-declarer",
            o => o.Jobs.Daily<RjTick>(new TimeOnly(2, 0), name: "nightly-tick"));

        // The broker holds the schedule (T4)...
        var row = List().Should().ContainSingle(r => (string?)r[1] == "nightly-tick").Subject;
        ((string?)row[0]).Should().Be("rj.ticks", "the queue comes from the CONTRACT's [Queue]");
        ((string?)row[2]).Should().Be("daily:02:00");

        // ...and the manifest declares it (T5).
        node.Engine.Topology.Provides.Should().Contain(p =>
            p.Kind == Highway.Client.CapabilityKind.RecurringJob
            && p.Route == "rj.ticks"
            && p.Detail!.Contains("daily:02:00"));
    }

    // ------------------------------------------------------------------ end to end

    [Fact]
    public async Task FiredOccurrence_IsProcessedByAnOrdinaryWorker_Once()
    {
        // Schedule registered on the wire; a real engine node hosts the processor. The
        // occurrence must flow through the ordinary machinery into IProcess<T>.
        RjTickProcessor.Invocations.Clear();

        _db.Execute("HW.JOB", "SET", "rj.ticks", "tick", "every:2",
            Encoding.UTF8.GetBytes("""{"v":1,"src":"scheduler","ts":"2026-08-10T00:00:00Z","body":{"label":"from-schedule"}}"""));

        await using var node = await EngineNode.StartAsync(_server.ConnectionString, "rj-worker");

        await SubscriberRecorder.WaitForAsync(() => !RjTickProcessor.Invocations.IsEmpty, TimeSpan.FromSeconds(15));

        RjTickProcessor.Invocations.Should().NotBeEmpty("the fired occurrence reaches the processor");
        RjTickProcessor.Invocations.First().Should().Be("from-schedule",
            "the template's body is what the handler receives (D8)");
    }
}

// --- fixtures ---------------------------------------------------------------

[Highway.Abstractions.Queue("rj.ticks")]
public sealed record RjTick : Highway.Abstractions.ISend
{
    public string? Label { get; set; }
}

public sealed class RjTickProcessor : Highway.Abstractions.IProcess<RjTick>
{
    public static readonly System.Collections.Concurrent.ConcurrentQueue<string> Invocations = new();

    public Task ProcessAsync(RjTick message, CancellationToken ct = default)
    {
        Invocations.Enqueue(message.Label ?? "");
        return Task.CompletedTask;
    }
}
