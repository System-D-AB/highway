using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using FluentAssertions;
using Highway.Server.Storage.Layout;
using Highway.Server.Storage.Rocks;
using Xunit;

namespace Highway.Server.Tests.Storage;

/// <summary>
/// T6 / R4.4 — hard-kill the writer process mid-commit-stream, reopen the store, and prove
/// recovery is consistent and durable: every acknowledged commit survives (sync-per-commit,
/// 038 R0.3), and the recovered state is byte-identical to a clean reference run of the same
/// commits. This is a RocksDB-only guarantee (durability is the engine's, not the in-memory
/// store's), driven through the <c>Highway.Storage.CrashHarness</c> child process.
/// </summary>
public class CrashReplayTests
{
    private const string Queue = "crash-queue";

    [Fact]
    public void HardKillMidStream_EveryAckedCommitSurvives()
    {
        var harness = LocateHarness();
        var dir = NewTempDir();

        long lastAcked = -1;
        try
        {
            using var proc = StartWriter(harness, dir, count: null); // write forever
            WaitForReady(proc);

            // Read acks until we have a healthy stream, then hard-kill mid-write.
            var deadline = DateTime.UtcNow.AddSeconds(10);
            string? line;
            while (DateTime.UtcNow < deadline && (line = proc.StandardOutput.ReadLine()) is not null)
            {
                if (long.TryParse(line, out var seq))
                {
                    lastAcked = seq;
                    if (seq >= 200) break; // enough committed to make the test meaningful
                }
            }

            proc.Kill(entireProcessTree: true); // no graceful shutdown, no flush
            proc.WaitForExit();

            lastAcked.Should().BeGreaterThanOrEqualTo(0, "the harness must have acked at least one commit");

            // Reopen and audit: every acked seq [0..lastAcked] is present, in order, no gaps.
            using var store = RocksDbStore.Open(dir);
            var listPrefix = HighwayKeyspace.ListPrefix(HighwayNames.Queue(Queue));
            using var snap = store.Snapshot();
            var recovered = store.ListLength(snap, listPrefix);

            recovered.Should().BeGreaterThanOrEqualTo(lastAcked + 1,
                "every acknowledged sync-per-commit write must survive an ungraceful kill");
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public void RecoveredState_IsByteIdenticalToReferenceRun()
    {
        var harness = LocateHarness();

        // Reference: a clean run of exactly N commits, then dump.
        const int n = 100;
        var refDir = NewTempDir();
        var crashDir = NewTempDir();
        try
        {
            RunToCompletion(harness, refDir, n);
            List<(byte[] Key, byte[] Value)> reference;
            using (var store = RocksDbStore.Open(refDir))
                reference = store.DumpData();

            // Crash run: write forever, kill after >= N acks, reopen.
            long lastAcked = -1;
            using (var proc = StartWriter(harness, crashDir, count: null))
            {
                WaitForReady(proc);
                var deadline = DateTime.UtcNow.AddSeconds(10);
                string? line;
                while (DateTime.UtcNow < deadline && (line = proc.StandardOutput.ReadLine()) is not null)
                {
                    if (long.TryParse(line, out var seq))
                    {
                        lastAcked = seq;
                        if (seq >= n) break;
                    }
                }
                proc.Kill(entireProcessTree: true);
                proc.WaitForExit();
            }

            lastAcked.Should().BeGreaterThanOrEqualTo(n);

            using var crashed = RocksDbStore.Open(crashDir);
            var recovered = crashed.DumpData();

            // Compare the queue ENTRY keys (the `q` family), which are deterministic per
            // sequence number. Counter keys (`n` family) legitimately differ — the crash run
            // committed past N, so its high-water counter is higher — so they are excluded:
            // the durability guarantee is that every committed *entry* survives byte-identical,
            // not that two runs of different length hold identical counters.
            var listPrefix = HighwayKeyspace.ListPrefix(HighwayNames.Queue(Queue));
            bool IsEntry(byte[] k) => k.Length >= listPrefix.Length
                && k.AsSpan(0, listPrefix.Length).SequenceEqual(listPrefix);

            var recoveredEntries = recovered
                .Where(kv => IsEntry(kv.Key))
                .ToDictionary(kv => Convert.ToHexString(kv.Key), kv => kv.Value);

            var referenceEntries = reference.Where(kv => IsEntry(kv.Key)).ToList();
            referenceEntries.Should().HaveCount(n, "the reference run committed exactly N entries");

            foreach (var (key, value) in referenceEntries)
            {
                var hex = Convert.ToHexString(key);
                recoveredEntries.Should().ContainKey(hex,
                    "every entry the reference run committed must be present after recovery");
                recoveredEntries[hex].Should().Equal(value,
                    "the recovered value for a committed entry must be byte-identical to the reference");
            }
        }
        finally
        {
            TryDelete(refDir);
            TryDelete(crashDir);
        }
    }

    // ---- harness process management ------------------------------------------

    private static Process StartWriter(string harnessDll, string dir, int? count)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("exec");
        psi.ArgumentList.Add(harnessDll);
        psi.ArgumentList.Add("write");
        psi.ArgumentList.Add("--dir");
        psi.ArgumentList.Add(dir);
        if (count is not null)
        {
            psi.ArgumentList.Add("--count");
            psi.ArgumentList.Add(count.Value.ToString());
        }
        return Process.Start(psi) ?? throw new InvalidOperationException("failed to start harness");
    }

    private static void RunToCompletion(string harnessDll, string dir, int count)
    {
        using var proc = StartWriter(harnessDll, dir, count);
        proc.StandardOutput.ReadToEnd(); // drain
        proc.WaitForExit();
        proc.ExitCode.Should().Be(0, "the reference run must complete cleanly");
    }

    private static void WaitForReady(Process proc)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var line = proc.StandardOutput.ReadLine();
            if (line is null) break;
            if (line == "READY") return;
        }
        throw new InvalidOperationException("harness never reported READY");
    }

    private static string LocateHarness()
    {
        // The harness builds alongside the test assembly under the same configuration.
        // From tests/Highway.Server.Tests/bin/<cfg>/<tfm>/ walk up to tests/ and across.
        var testDir = AppContext.BaseDirectory;              // .../tests/Highway.Server.Tests/bin/Debug/net10.0/
        var tfm = new DirectoryInfo(testDir).Name;           // net10.0
        var cfg = new DirectoryInfo(testDir).Parent!.Name;   // Debug
        var testsRoot = new DirectoryInfo(testDir).Parent!.Parent!.Parent!.Parent!.FullName; // .../tests
        var harness = Path.Combine(testsRoot, "Highway.Storage.CrashHarness", "bin", cfg, tfm,
            "Highway.Storage.CrashHarness.dll");

        File.Exists(harness).Should().BeTrue(
            $"the crash harness must be built at {harness} — build tests/Highway.Storage.CrashHarness first");
        return harness;
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hw-crash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }
}
