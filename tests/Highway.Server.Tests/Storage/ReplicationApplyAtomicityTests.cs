using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using FluentAssertions;
using Highway.Server.Storage.Rocks;
using RocksDbSharp;
using Xunit;

namespace Highway.Server.Tests.Storage;

/// <summary>
/// 042 T2v.2 — apply-atomicity. Without a skip, restaging an increment against the replica's
/// current value double-applies (visible on the counter). With mechanism (a) — watermark =
/// <c>GetLatestSequenceNumber()</c> after a sync ingest — a re-pulled page is skipped and
/// the counter stays put. A child-process hard-kill between ingest and any extra write
/// proves the sequence itself is durable, so the skip still holds after resurrection.
/// </summary>
public class ReplicationApplyAtomicityTests
{
    private static readonly byte[] CounterKey = "counter"u8.ToArray();

    [Fact]
    public void WithoutMechanism_LogicalRestageOfIncrement_DoubleApplies()
    {
        var dir = ReplicationToolkitSurfaceTests.NewTempDir();
        try
        {
            using var db = ReplicationToolkitSurfaceTests.Open(dir);
            LogicalIncrement(db);
            LogicalIncrement(db);
            ReadCounter(db).Should().Be(2, "read-modify-write restage against current value double-applies");
        }
        finally
        {
            ReplicationToolkitSurfaceTests.TryDelete(dir);
        }
    }

    [Fact]
    public void WithDerivedWatermark_RePulledPage_DoesNotDoubleApply()
    {
        var primaryDir = ReplicationToolkitSurfaceTests.NewTempDir();
        var replicaDir = ReplicationToolkitSurfaceTests.NewTempDir();
        try
        {
            using var primary = ReplicationToolkitSurfaceTests.Open(primaryDir);
            using var replica = ReplicationToolkitSurfaceTests.Open(replicaDir);

            WriteCounter(primary, 1);
            var page = SinglePage(primary);

            ReplicationApply.TryIngest(replica, page.SequenceNumber, page.Data).Should().BeTrue();
            ReadCounter(replica).Should().Be(1);
            var watermark = ReplicationApply.Watermark(replica);
            watermark.Should().Be(page.SequenceNumber);

            // Re-pull of the same page: skip, counter stays 1.
            ReplicationApply.TryIngest(replica, page.SequenceNumber, page.Data).Should().BeFalse();
            ReadCounter(replica).Should().Be(1);
            ReplicationApply.Watermark(replica).Should().Be(watermark);
        }
        finally
        {
            ReplicationToolkitSurfaceTests.TryDelete(primaryDir);
            ReplicationToolkitSurfaceTests.TryDelete(replicaDir);
        }
    }

    [Fact]
    public void CrashBetweenIngestAndExtraWatermarkWrite_DerivedSequenceStillSkips()
    {
        var harness = LocateHarness();
        var primaryDir = ReplicationToolkitSurfaceTests.NewTempDir();
        var replicaDir = ReplicationToolkitSurfaceTests.NewTempDir();
        try
        {
            using var proc = StartReplIngest(harness, primaryDir, replicaDir);
            var ingested = WaitForIngested(proc);

            proc.Kill(entireProcessTree: true);
            proc.WaitForExit();

            ingested.Should().BeGreaterThan(0UL);

            using var replica = ReplicationToolkitSurfaceTests.Open(replicaDir);
            ReplicationApply.Watermark(replica).Should().Be(ingested,
                "GetLatestSequenceNumber after a sync ingest must survive an ungraceful kill — that is mechanism (a)");
            ReadCounter(replica).Should().Be(1);

            // Primary WAL is gone after the kill (GetUpdatesSince only sees live WAL).
            // The harness persisted the page bytes before hanging so skip can be proven.
            var pageBytes = File.ReadAllBytes(PagePath(replicaDir));
            ReplicationApply.TryIngest(replica, ingested, pageBytes).Should().BeFalse();
            ReadCounter(replica).Should().Be(1, "skip-by-derived-watermark must not double-apply after resurrection");
        }
        finally
        {
            ReplicationToolkitSurfaceTests.TryDelete(primaryDir);
            ReplicationToolkitSurfaceTests.TryDelete(replicaDir);
            try { File.Delete(PagePath(replicaDir)); } catch { /* best effort */ }
        }
    }

    private static ReplicationBatch SinglePage(RocksDb primary)
    {
        var pages = new ReplicationSource(primary).GetWalUpdates(0).ToList();
        pages.Should().HaveCount(1);
        return pages[0];
    }

    private static void WriteCounter(RocksDb db, long value)
    {
        var encoded = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(encoded, value);
        db.Put(CounterKey, encoded, writeOptions: ReplicationToolkitSurfaceTests.Sync());
    }

    private static void LogicalIncrement(RocksDb db)
    {
        var next = ReadCounter(db) + 1;
        var encoded = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(encoded, next);
        db.Put(CounterKey, encoded, writeOptions: ReplicationToolkitSurfaceTests.Sync());
    }

    private static long ReadCounter(RocksDb db)
    {
        var bytes = db.Get(CounterKey);
        return bytes is { Length: >= 8 } ? BinaryPrimitives.ReadInt64BigEndian(bytes) : 0L;
    }

    private static Process StartReplIngest(string harnessDll, string primaryDir, string replicaDir)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("exec");
        psi.ArgumentList.Add(harnessDll);
        psi.ArgumentList.Add("repl-ingest");
        psi.ArgumentList.Add("--primary");
        psi.ArgumentList.Add(primaryDir);
        psi.ArgumentList.Add("--replica");
        psi.ArgumentList.Add(replicaDir);
        psi.ArgumentList.Add("--page");
        psi.ArgumentList.Add(PagePath(replicaDir));
        return Process.Start(psi) ?? throw new InvalidOperationException("failed to start harness");
    }

    private static ulong WaitForIngested(Process proc)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var line = proc.StandardOutput.ReadLine();
            if (line is null) break;
            if (line.StartsWith("INGESTED ", StringComparison.Ordinal)
                && ulong.TryParse(line.AsSpan("INGESTED ".Length), out var seq))
                return seq;
        }
        throw new InvalidOperationException("harness never reported INGESTED");
    }

    private static string PagePath(string replicaDir)
        => replicaDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + ".page";

    private static string LocateHarness()
    {
        var testDir = AppContext.BaseDirectory;
        var tfm = new DirectoryInfo(testDir).Name;
        var cfg = new DirectoryInfo(testDir).Parent!.Name;
        var testsRoot = new DirectoryInfo(testDir).Parent!.Parent!.Parent!.Parent!.FullName;
        var harness = Path.Combine(testsRoot, "Highway.Storage.CrashHarness", "bin", cfg, tfm,
            "Highway.Storage.CrashHarness.dll");
        File.Exists(harness).Should().BeTrue($"crash harness must be built at {harness}");
        return harness;
    }
}
