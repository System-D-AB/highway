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

    // A durable test server (RocksDB): Restart() reopens the same on-disk directory, exercising
    // recovery. Feature 041 re-pointed this class off the Garnet-hosted HighwayServerBuilder +
    // raw LLEN reads onto the shipped RESP + RocksDB stack. The post-restart assertions read the
    // store directly through Inspect (the store keeps the hw:q:X:q logical key spellings), because
    // the RESP server serves no raw LLEN.
    private readonly HighwayTestServer _server;

    public DurableByDefaultTests()
        => _server = new HighwayTestServer(o =>
        {
            o.DataDir = _dataDir;
            o.Ephemeral = false;
        });

    public void Dispose()
    {
        try { _server.Dispose(); } catch { /* teardown must not fail a passing test */ }
        try { if (Directory.Exists(_dataDir)) Directory.Delete(_dataDir, recursive: true); }
        catch { /* a locked file on Windows must not fail the test that already passed */ }
    }

    private static byte[] Envelope(string body = "{}")
        => Encoding.UTF8.GetBytes($$"""{"v":1,"src":"t","ts":"2026-08-09T00:00:00Z","body":{{body}}}""");

    private IDatabase Connect() =>
        ConnectionMultiplexer.Connect(_server.ConnectionString).GetDatabase();

    /// <summary>
    /// The headline, across all three verbs at once. Each is stored differently enough that a
    /// single-verb test would prove less than it appears to.
    /// </summary>
    [Fact]
    public void AllThreeVerbs_SurviveARestart()
    {
        var db = Connect();

        // Queue: work nobody has claimed.
        db.Execute("HW.QSEND", "dur.queue", "msg-1", Envelope("""{"Amount":42}"""));

        // Pub/Sub: a group registered but offline, so the message is sitting in its queue.
        db.Execute("HW.SUBSCRIBE", "dur.channel", "billing");
        db.Execute("HW.PUBLISH", "dur.channel", Envelope("""{"Order":"ORD-1"}"""));

        // RPC: a request nobody has dequeued.
        db.Execute("HW.CALL", "dur.svc", "req-1", Envelope());

        // The process goes away; only the RocksDB directory remains, and Restart() recovers it.
        _server.Restart();

        _server.Inspect.ListLength("hw:q:dur.queue:q").Should().Be(1,
            "a sent message survives until it is processed - including across a restart");

        _server.Inspect.ListLength("hw:q:dur.channel@billing:q").Should().Be(1,
            "a subscriber that was down must still receive what it missed after a restart");

        _server.Inspect.ListLength("hw:svc:dur.svc:q").Should().Be(1,
            "an unclaimed RPC request is queued work like any other");
    }

    /// <summary>
    /// The payload has to come back intact, not merely the count. A recovered entry whose bytes
    /// are wrong is worse than one that is missing, because nothing reports it.
    /// </summary>
    [Fact]
    public void ARecoveredMessage_StillCarriesItsPayload()
    {
        const string body = """{"Amount":42,"Currency":"SEK"}""";

        Connect().Execute("HW.QSEND", "dur.payload", "msg-1", Envelope(body));

        _server.Restart();

        var claimed = (RedisResult[])Connect().Execute("HW.QCLAIM", "dur.payload", "node-a")!;

        claimed.Should().NotBeNull();
        ((string)claimed[0]!).Should().Be("msg-1");
        Encoding.UTF8.GetString((byte[])claimed[1]!).Should().Contain(body,
            "the recovered entry must decode to the bytes that were stored");
    }
}

/// <summary>
/// Feature 016 T6 — <b>the append-only log does not grow without bound.</b>
///
/// <para><b>Feature 041 T5 — C4.6, retired (gate G4).</b> The Garnet-AOF version of this test
/// measured <c>checkpoints/AOF/aof.log*</c> segment files and was skipped because Garnet's
/// <c>TsavoriteLog</c> never reclaimed retired segments on disk: total history grew linearly
/// (12k×8KB → 102 MB, 24k×8KB → 205 MB, files 4→7). That failure was <b>structural to Garnet's
/// append-only log</b> — logical truncation (<c>TruncateUntil</c>) moved the begin address but
/// returned no disk.</para>
///
/// <para>On the RocksDB engine the failure mode is absent by construction, not fixed by tuning:
/// consumed messages are deleted, deletes become tombstones, and compaction reclaims their space
/// as the engine's ordinary background job. There is no append-only log whose segments accumulate.
/// The Garnet-shaped assertion (AOF segment-file counts) has no RocksDB analogue to re-point onto,
/// and re-measuring space reclamation on a mainstream LSM engine would only re-confirm documented,
/// externally-verified engine behaviour. C4.6 is therefore recorded as met in
/// <c>docs/product/constraints.md</c> and the test is retired rather than carried skipped — the
/// skip existed to hold the Garnet measurements, and those now live in the register as history.</para>
/// </summary>
public static class C46Retired
{
    // Intentionally no [Fact]. See the class summary and constraints.md C4.6 (2026-09-15 addendum).
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

    /// <summary>
    /// A data directory written by an older build must be refused <b>before</b> Garnet tries to
    /// recover from it.
    ///
    /// <para><b>Found by running the samples.</b> Garnet's AOF stores a positional
    /// stored-procedure id per record; feature 018 removed two commands, so every id after them
    /// shifted and replaying an older log fails with "Transaction procedure N not found".
    /// Recovery then aborts and the broker carries on with an <b>empty store</b> — healthy to
    /// every outward appearance, and missing every message it was asked to keep.</para>
    ///
    /// <para>018's own guard scanned for leftover <c>hw:ch:*:grp:*</c> keys, which can only be
    /// found when recovery <i>succeeded</i>. It looked for a symptom that is absent in exactly
    /// the worst case.</para>
    /// </summary>
    [Fact]
    public void ADataDirectoryFromAnOlderBuild_IsRefusedBeforeRecovery()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hw-oldfmt-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(dir, "checkpoints"));   // looks like a used dir
        try
        {
            var build = () => new HighwayServerBuilder()
                .WithPort(Highway.Server.Internal.EphemeralPort.Probe())
                .WithDataDir(dir)
                .Build();

            var thrown = build.Should().Throw<InvalidOperationException>();
            thrown.WithMessage("*storage format*");
            thrown.WithMessage("*delete the directory*");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void AMismatchedStorageFormat_IsRefusedNamingWhatItFound()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hw-badfmt-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "highway.format"), "1");
        try
        {
            var build = () => new HighwayServerBuilder()
                .WithPort(Highway.Server.Internal.EphemeralPort.Probe())
                .WithDataDir(dir)
                .Build();

            build.Should().Throw<InvalidOperationException>()
                .WithMessage("*format '1'*", "the message names what it found, not just that it disagreed");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void AFreshDataDirectory_IsStampedSoTheNextBuildCanCheckIt()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hw-fresh-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            using (var server = new HighwayServerBuilder()
                .WithPort(Highway.Server.Internal.EphemeralPort.Probe())
                .WithDataDir(dir)
                .Build())
            {
                File.Exists(Path.Combine(dir, "highway.format")).Should().BeTrue();
            }

            // And a second start against its own directory is fine — the stamp matches.
            var again = () => new HighwayServerBuilder()
                .WithPort(Highway.Server.Internal.EphemeralPort.Probe())
                .WithDataDir(dir)
                .Build();
            again.Should().NotThrow();
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
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
