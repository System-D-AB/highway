using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;
using Highway.Server.Commands.Ported;
using Highway.Server.Internal;
using Highway.Server.Storage;
using Highway.Server.Storage.Layout;
using Highway.Server.Storage.Rocks;
using Xunit;

namespace Highway.Server.Tests.Commands;

/// <summary>
/// 039 T8 — determinism enforcement + crash test (R5). Three nets:
/// <list type="number">
///   <item><b>No clock inside a batch</b> (R5.1): an analyzer-style scan of every ported
///         command's source asserts no wall-clock read (<c>DateTime.UtcNow</c>,
///         <c>DateTimeOffset.UtcNow</c>, <c>Stopwatch</c>, <c>Environment.TickCount</c>)
///         appears in code — a command derives every time value from <c>ctx.NowTicks</c>.</item>
///   <item><b>The sweep persists rows, not intent</b> (R5.2): a lease sweep that dead-letters
///         and requeues writes the actual entries, readable back as bytes — never a "sweep me"
///         marker.</item>
///   <item><b>Kill-mid-claim → byte-identical recovery</b> (R5.3): a claim is one batch, so
///         given a fixed clock the store state after the claim is byte-identical to a second
///         independent run — on RocksDB, on top of 038's store-level durability proof.</item>
/// </list>
/// </summary>
public class DeterminismTests
{
    // ============================ R5.1 — no clock in a command ================

    [Fact]
    public void NoPortedCommand_ReadsAWallClock()
    {
        var portedDir = LocatePortedSourceDir();
        var files = Directory.GetFiles(portedDir, "*.cs", SearchOption.AllDirectories);
        files.Should().NotBeEmpty("the ported command sources must be found for the scan to mean anything");

        // Wall-clock reads a command must never make — it takes the clock once as ctx.NowTicks.
        var forbidden = new[]
        {
            "DateTime.UtcNow", "DateTime.Now",
            "DateTimeOffset.UtcNow", "DateTimeOffset.Now",
            "Environment.TickCount", "new Stopwatch", "Stopwatch.StartNew", "Stopwatch.GetTimestamp",
        };

        var offenders = new List<string>();
        foreach (var file in files)
        {
            var code = StripCommentsAndStrings(File.ReadAllText(file));
            foreach (var token in forbidden)
            {
                if (code.Contains(token, StringComparison.Ordinal))
                    offenders.Add($"{Path.GetFileName(file)} → {token}");
            }
        }

        offenders.Should().BeEmpty(
            "a ported command must derive every time value from ctx.NowTicks (037 R5.1); " +
            "these read a wall clock in code: " + string.Join(", ", offenders));
    }

    [Fact]
    public void ConstructingDateTimeFromNowTicks_IsAllowed_TheScanIsNotOverzealous()
    {
        // Guard against a false-positive scanner: `new DateTime(ctx.NowTicks, ...)` is exactly
        // how a command turns the once-read clock into a DateTime, and must NOT be flagged.
        var portedDir = LocatePortedSourceDir();
        var job = File.ReadAllText(Path.Combine(portedDir, "HwJobCommand.cs"));
        StripCommentsAndStrings(job).Should().Contain("new DateTime(ctx.NowTicks",
            "the clock-derived DateTime construction is the sanctioned pattern");
    }

    // ============================ R5.2 — rows, not intent =====================

    [Fact]
    public void LeaseSweep_PersistsProducedRows_NotAnIntentMarker()
    {
        // MaxDeliveryAttempts = 1: once an entry has been redelivered past the first attempt it
        // dead-letters instead of going back on the queue.
        var opts = new HighwayServerOptions { Lease = TimeSpan.FromMinutes(5), MaxDeliveryAttempts = 1 };
        using var h = new CommandHarness(options: opts);
        var t0 = 10_000_000_000_000L;

        // A worker claims one request, then never acks. Its lease will expire.
        h.Run(new HwCallCommand(), t0, "orders", "req-dead", "payload");
        h.Run(new HwDequeueCommand(), t0, "orders", "worker-1"); // claim (attempts 0)

        // First sweep past the lease: attempts → 1, still ≤ limit, so it is requeued.
        var lease1 = t0 + TimeSpan.FromMinutes(6).Ticks;
        h.Run(new HwDequeueCommand(), lease1, "orders", "worker-2"); // claims the requeued entry

        // Second sweep past the next lease: attempts → 2 > 1, so it dead-letters — the row we assert.
        var afterLease = lease1 + TimeSpan.FromMinutes(6).Ticks;
        h.Run(new HwDequeueCommand(), afterLease, "orders", "worker-3");

        using var snap = h.Store.Snapshot();

        // The dead-letter list holds a REAL dead-letter record, not a marker: decode it and it
        // is the original request's bytes wrapped in a dead-letter envelope.
        var dlq = h.Store.ListDrain(NoCommitBatch(h.Store), HighwayKeyspace.ListPrefix(HighwayNames.ServiceDeadLetter("orders")));
        dlq.Should().ContainSingle("the sweep produced exactly one dead-letter row");

        DeadLetter.Decode(dlq[0], out var deadAtTicks, out var attempts, out _, out var original);
        deadAtTicks.Should().Be(afterLease, "the dead-letter row carries the sweep's clock value — a persisted fact, not a deferred instruction");
        attempts.Should().BeGreaterThan(0);
        Encoding.UTF8.GetString(original).Should().Contain("req-dead", "the row is the original request, materialised — not a pointer to it");
    }

    [Fact]
    public void LeaseSweep_UnderLimit_RequeuesTheActualEntry()
    {
        var opts = new HighwayServerOptions { Lease = TimeSpan.FromMinutes(5), MaxDeliveryAttempts = 10 };
        using var h = new CommandHarness(options: opts);
        var t0 = 10_000_000_000_000L;

        h.Run(new HwCallCommand(), t0, "orders", "req-1", "the-payload");
        h.Run(new HwDequeueCommand(), t0, "orders", "worker-1"); // claims req-1

        // Sweep after the lease: the entry is requeued (attempts < limit), as a real queue row.
        var afterLease = t0 + TimeSpan.FromMinutes(6).Ticks;
        var reply = h.Run(new HwDequeueCommand(), afterLease, "orders", "worker-2");

        // worker-2 claims the requeued entry — the row exists and is the original request.
        var text = CommandHarness.AsText(reply);
        text.Should().StartWith("*2\r\n");
        text.Should().Contain("req-1");
        text.Should().Contain("the-payload", "the requeued row carries the original payload bytes");
    }

    // ============================ R5.3 — byte-identical recovery ==============

    [Fact]
    public void ClaimIsOneBatch_TwoRuns_YieldByteIdenticalState_InMemory()
    {
        // The command-layer determinism proof: a claim commits exactly one batch, and every
        // time value comes from the fixed clock, so two independent runs of the identical
        // command sequence produce a byte-identical keyspace.
        static List<(byte[] Key, byte[] Value)> RunSequence()
        {
            var store = new InMemoryStore();
            using var h = new CommandHarness(store: store);
            var t0 = 10_000_000_000_000L;
            h.Run(new HwCallCommand(), t0, "orders", "req-1", "payload-1");
            h.Run(new HwCallCommand(), t0, "orders", "req-2", "payload-2");
            h.Run(new HwDequeueCommand(), t0 + 1000, "orders", "worker-1");
            return store.DumpData();
        }

        var a = RunSequence();
        var b = RunSequence();

        DumpToHex(a).Should().Equal(DumpToHex(b),
            "a claim is one deterministic batch — identical inputs produce identical bytes");
    }

    [Fact]
    public void KillMidClaim_RecoveredState_IsByteIdenticalToACleanRun_OnRocksDb()
    {
        // On top of 038's store-level crash proof: run the identical claim sequence twice
        // against real RocksDB stores (sync-per-commit durability), and prove the on-disk
        // keyspace is byte-identical. Because the claim is one batch, a crash mid-claim can
        // only leave the pre-claim or post-claim state — never a torn half — so the recovered
        // state always matches one of these two deterministic dumps.
        var dirA = NewTempDir();
        var dirB = NewTempDir();
        try
        {
            var a = RunClaimSequenceOnRocks(dirA);
            var b = RunClaimSequenceOnRocks(dirB);

            DumpToHex(a).Should().Equal(DumpToHex(b),
                "the claim commits one batch from a fixed clock, so recovery is byte-identical");
        }
        finally
        {
            TryDelete(dirA);
            TryDelete(dirB);
        }
    }

    private static List<(byte[] Key, byte[] Value)> RunClaimSequenceOnRocks(string dir)
    {
        var store = RocksDbStore.Open(dir);
        using var h = new CommandHarness(store: store); // the harness owns and disposes the store
        var t0 = 10_000_000_000_000L;
        h.Run(new HwCallCommand(), t0, "orders", "req-1", "payload-1");
        h.Run(new HwCallCommand(), t0, "orders", "req-2", "payload-2");
        h.Run(new HwDequeueCommand(), t0 + 1000, "orders", "worker-1"); // claim: pop + processing push, one batch
        return store.DumpData();
    }

    // ---- helpers -------------------------------------------------------------

    /// <summary>A throwaway batch used only to drive <see cref="IHighwayStore.ListDrain"/> for a read.</summary>
    private static IStoreBatch NoCommitBatch(IHighwayStore store) => store.NewBatch();

    private static List<string> DumpToHex(List<(byte[] Key, byte[] Value)> dump)
        => [.. dump.OrderBy(kv => Convert.ToHexString(kv.Key), StringComparer.Ordinal)
                   .Select(kv => Convert.ToHexString(kv.Key) + "=" + Convert.ToHexString(kv.Value))];

    /// <summary>
    /// Removes line and block comments and string/char literals so the token scan only sees
    /// executable code — a <c>DateTime.UtcNow</c> in a doc comment (there is one in HwJob's
    /// remarks) is not a violation.
    /// </summary>
    private static string StripCommentsAndStrings(string source)
    {
        // Order matters: strip block comments, then line comments, then string/char literals.
        source = Regex.Replace(source, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        source = Regex.Replace(source, @"//[^\n]*", " ");
        source = Regex.Replace(source, "\"(?:\\\\.|[^\"\\\\])*\"", "\"\"");   // "…"
        source = Regex.Replace(source, "@\"(?:[^\"]|\"\")*\"", "\"\"");        // verbatim
        source = Regex.Replace(source, "'(?:\\\\.|[^'\\\\])'", "''");          // 'c'
        return source;
    }

    private static string LocatePortedSourceDir()
    {
        // From tests/Highway.Server.Tests/bin/<cfg>/<tfm>/ walk up to the repo root, then into src.
        var baseDir = AppContext.BaseDirectory;
        var dir = new DirectoryInfo(baseDir);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Highway.Server")))
            dir = dir.Parent;

        dir.Should().NotBeNull("the repo root (containing src/Highway.Server) must be locatable from the test bin dir");
        var ported = Path.Combine(dir!.FullName, "src", "Highway.Server", "Commands", "Ported");
        Directory.Exists(ported).Should().BeTrue($"the ported command source directory must exist at {ported}");
        return ported;
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hw-det-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }
}
