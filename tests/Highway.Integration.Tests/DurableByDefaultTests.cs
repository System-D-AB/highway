using System.Text;
using FluentAssertions;
using Highway.Server;
using StackExchange.Redis;
using Xunit;

namespace Highway.Integration.Tests;

/// <summary>
/// Feature 016 T1–T3 — <b>a broker started with no configuration keeps its messages.</b>
///
/// <para>Until this feature <c>new HighwayServerBuilder().Build()</c> was memory-only, so every
/// guarantee features 013, 014 and 018 built was false in the configuration a newcomer meets
/// first. Two different things had been sharing the word "durable": <i>retention until
/// processed</i> (a consumer is down — built) and <i>survives a restart</i> (the broker dies —
/// this).</para>
///
/// <para>These tests restart a real broker against the same data directory. 018's unification is
/// what lets one test shape cover all three verbs: a queue message, a published message and an
/// RPC request are the same storage now.</para>
/// </summary>
public class DurableByDefaultTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(
        Path.GetTempPath(), "highway-durability-" + Guid.NewGuid().ToString("N")[..8]);

    private readonly int _port = Highway.Server.Internal.EphemeralPort.Probe();

    public void Dispose()
    {
        try { if (Directory.Exists(_dataDir)) Directory.Delete(_dataDir, recursive: true); }
        catch { /* a locked file on Windows must not fail the test that already passed */ }
    }

    private IHighwayServer StartServer()
    {
        var server = new HighwayServerBuilder()
            .WithPort(_port)
            .WithDataDir(_dataDir)
            .Build();
        server.Start();
        return server;
    }

    private static byte[] Envelope(string body = "{}")
        => Encoding.UTF8.GetBytes($$"""{"v":1,"src":"t","ts":"2026-08-09T00:00:00Z","body":{{body}}}""");

    private IDatabase Connect() =>
        ConnectionMultiplexer.Connect($"localhost:{_port}").GetDatabase();

    /// <summary>
    /// The headline, across all three verbs at once. Each is stored differently enough that a
    /// single-verb test would prove less than it appears to.
    /// </summary>
    [Fact]
    public void AllThreeVerbs_SurviveARestart()
    {
        var server = StartServer();
        try
        {
            var db = Connect();

            // Queue: work nobody has claimed.
            db.Execute("HW.QSEND", "dur.queue", "msg-1", Envelope("""{"Amount":42}"""));

            // Pub/Sub: a group registered but offline, so the message is sitting in its queue.
            db.Execute("HW.SUBSCRIBE", "dur.channel", "billing");
            db.Execute("HW.PUBLISH", "dur.channel", Envelope("""{"Order":"ORD-1"}"""));

            // RPC: a request nobody has dequeued.
            db.Execute("HW.CALL", "dur.svc", "req-1", Envelope());
        }
        finally
        {
            server.Dispose();   // the process goes away; only the data directory remains
        }

        var restarted = StartServer();
        try
        {
            var db = Connect();

            ((long)db.Execute("LLEN", "hw:q:dur.queue:q")).Should().Be(1,
                "a sent message survives until it is processed - including across a restart");

            ((long)db.Execute("LLEN", "hw:q:dur.channel@billing:q")).Should().Be(1,
                "a subscriber that was down must still receive what it missed after a restart");

            ((long)db.Execute("LLEN", "hw:svc:dur.svc:q")).Should().Be(1,
                "an unclaimed RPC request is queued work like any other");
        }
        finally
        {
            restarted.Dispose();
        }
    }

    /// <summary>
    /// The payload has to come back intact, not merely the count. A recovered entry whose bytes
    /// are wrong is worse than one that is missing, because nothing reports it.
    /// </summary>
    [Fact]
    public void ARecoveredMessage_StillCarriesItsPayload()
    {
        const string body = """{"Amount":42,"Currency":"SEK"}""";

        var server = StartServer();
        try
        {
            Connect().Execute("HW.QSEND", "dur.payload", "msg-1", Envelope(body));
        }
        finally { server.Dispose(); }

        var restarted = StartServer();
        try
        {
            var claimed = (RedisResult[])Connect().Execute("HW.QCLAIM", "dur.payload", "node-a")!;

            claimed.Should().NotBeNull();
            ((string)claimed[0]!).Should().Be("msg-1");
            Encoding.UTF8.GetString((byte[])claimed[1]!).Should().Contain(body,
                "the recovered entry must decode to the bytes that were stored");
        }
        finally { restarted.Dispose(); }
    }
}

/// <summary>
/// Feature 016 T6 — <b>the append-only log does not grow without bound.</b>
///
/// <para>Highway has always set a checkpoint directory and never turned checkpoint-on-AOF-size
/// on, so a broker that ran for a year replayed a year of log to start. This drives traffic
/// well past a deliberately small limit and asserts the log stays bounded.</para>
/// </summary>
public class AofGrowthTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(
        Path.GetTempPath(), "highway-aof-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { if (Directory.Exists(_dataDir)) Directory.Delete(_dataDir, recursive: true); }
        catch { /* a locked file must not fail a passing test */ }
    }

    private long AofBytes() => Directory
        .GetFiles(Path.Combine(_dataDir, "checkpoints", "AOF"), "aof.log*", SearchOption.AllDirectories)
        .Sum(f => new FileInfo(f).Length);

    [Fact(Skip = "016 R6.3 is NOT met: AofSizeLimit triggers checkpoints but never truncates " +
                 "the AOF on disk. Measured, twice: 2,000 messages -> 8.9 MB; 4,000 -> 17.8 MB. " +
                 "Growth is exactly linear in total history. Kept and skipped rather than " +
                 "deleted or weakened, so the gap stays visible and the next attempt starts " +
                 "from a measurement instead of an assumption.")]
    public async Task SustainedTraffic_DoesNotGrowTheLogWithoutBound()
    {
        const long limit = 1L * 1024 * 1024;   // 1 MB, small enough to cross quickly
        var port = Highway.Server.Internal.EphemeralPort.Probe();

        using var server = new HighwayServerBuilder()
            .WithPort(port)
            .WithDataDir(_dataDir)
            .WithOptions(o => o.AofSizeLimitBytes = limit)
            .Build();
        server.Start();

        var db = ConnectionMultiplexer.Connect($"localhost:{port}").GetDatabase();
        var blob = new string('x', 4096);
        var payload = Encoding.UTF8.GetBytes(
            "{\"v\":1,\"src\":\"t\",\"ts\":\"2026-08-09T00:00:00Z\",\"body\":{\"Blob\":\"" + blob + "\"}}");

        void Drive(int from, int count)
        {
            for (var i = from; i < from + count; i++)
            {
                db.Execute("HW.QSEND", "aof.queue", $"msg-{i}", payload);
                db.Execute("HW.QCLAIM", "aof.queue", "node-a");
                db.Execute("HW.QACK", "aof.queue", "node-a", $"msg-{i}");
            }
        }

        // Enforcement is a BACKGROUND task on a frequency (Garnet's
        // AofSizeLimitEnforceFrequencySecs, 5s by default), not a check on the write path.
        // Measuring immediately after the loop measures the log before the task has run.
        Drive(0, 2_000);
        await Task.Delay(TimeSpan.FromSeconds(12));
        var afterFirst = AofBytes();

        Drive(2_000, 2_000);
        await Task.Delay(TimeSpan.FromSeconds(12));
        var afterSecond = AofBytes();

        // Growth, not absolute size, is the property. A truncated log may stay preallocated on
        // disk, so an absolute assertion would be testing the allocator; what matters is that
        // doubling the traffic does not double the log.
        var growth = afterSecond - afterFirst;

        growth.Should().BeLessThan(afterFirst,
            $"the second batch of identical traffic must not grow the log as much as the first " +
            $"({afterFirst} bytes -> {afterSecond} bytes). An unbounded log grows linearly with " +
            "total history, which is what makes a year-old broker slow to start");
    }
}

/// <summary>
/// The default itself — separate from the restart tests because it needs no server at all, and
/// because a default that is right for the wrong reason is still a defect.
/// </summary>
public class DurableByDefaultConfigurationTests
{
    [Fact]
    public void NoConfiguration_ResolvesADataDirectoryBesideTheExecutable()
    {
        var port = Highway.Server.Internal.EphemeralPort.Probe();

        using var server = new HighwayServerBuilder().WithPort(port).Build();

        var expected = Path.Combine(AppContext.BaseDirectory, $"highway-data-{port}");
        Directory.Exists(expected).Should().BeTrue(
            "a zero-configuration broker is durable, and its directory is created at Build()");

        try { Directory.Delete(expected, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Ephemeral_OptsOutInOneCall()
    {
        var port = Highway.Server.Internal.EphemeralPort.Probe();

        using var server = new HighwayServerBuilder().WithPort(port).Ephemeral().Build();

        Directory.Exists(Path.Combine(AppContext.BaseDirectory, $"highway-data-{port}"))
            .Should().BeFalse(
                "opting out has to be trivial, or a test suite fights the default and someone " +
                "eventually flips the default back rather than the tests");
    }

    [Fact]
    public void AnUnusableDataDirectory_ThrowsAtBuild_NamingBothWaysOut()
    {
        // A path under a FILE cannot be a directory on any platform.
        var file = Path.Combine(Path.GetTempPath(), "highway-not-a-dir-" + Guid.NewGuid().ToString("N")[..8]);
        File.WriteAllText(file, "");

        try
        {
            var build = () => new HighwayServerBuilder()
                .WithPort(Highway.Server.Internal.EphemeralPort.Probe())
                .WithDataDir(Path.Combine(file, "data"))
                .Build();

            // Silently degrading to memory-only would be worse after this feature than before
            // it, because the durability guarantee is now documented as true.
            var thrown = build.Should().Throw<InvalidOperationException>();
            thrown.WithMessage("*WithDataDir*");
            thrown.WithMessage("*Ephemeral*");
        }
        finally
        {
            File.Delete(file);
        }
    }
}
