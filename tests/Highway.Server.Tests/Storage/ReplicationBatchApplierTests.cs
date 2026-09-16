using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using FluentAssertions;
using Highway.Server.Storage.Rocks;
using RocksDbSharp;
using Xunit;

namespace Highway.Server.Tests.Storage;

/// <summary>
/// 042 T2 — <see cref="BatchApplier"/>: skip-by-watermark on re-pull, epoch refusal,
/// and the T2v crash-inject guarantee (derived sequence after sync ingest).
/// </summary>
public class ReplicationBatchApplierTests
{
    private static readonly byte[] Key = "k"u8.ToArray();

    [Fact]
    public void ApplyPage_FromFeeder_ReplicaSeesPrimaryBytes()
    {
        var primaryDir = ReplicationToolkitSurfaceTests.NewTempDir();
        var replicaDir = ReplicationToolkitSurfaceTests.NewTempDir();
        try
        {
            using var primary = Highway.Server.Storage.Rocks.RocksDbStore.Open(primaryDir);
            using var replicaStore = Highway.Server.Storage.Rocks.RocksDbStore.Open(replicaDir);

            using (var batch = primary.NewBatch())
            {
                primary.Set(batch, Key, "v1"u8.ToArray());
                batch.Commit();
            }

            var page = primary.Replication.Pull(0, 1_000_000);
            var applier = new BatchApplier(replicaStore.Replication.Engine);
            var result = applier.ApplyPage(page.Epoch, page.Batches);

            result.Status.Should().Be(ApplyStatus.Applied);
            result.Applied.Should().BeGreaterThan(0);
            using var snap = replicaStore.Snapshot();
            Encoding.UTF8.GetString(replicaStore.Get(snap, Key)!)
                .Should().Be("v1");
            applier.SeenEpoch.Should().Be(page.Epoch);
            applier.Watermark.Should().Be(result.Watermark);
        }
        finally
        {
            ReplicationToolkitSurfaceTests.TryDelete(primaryDir);
            ReplicationToolkitSurfaceTests.TryDelete(replicaDir);
        }
    }

    [Fact]
    public void ApplyPage_RePull_IsIdempotent()
    {
        var primaryDir = ReplicationToolkitSurfaceTests.NewTempDir();
        var replicaDir = ReplicationToolkitSurfaceTests.NewTempDir();
        try
        {
            using var primary = ReplicationToolkitSurfaceTests.Open(primaryDir);
            using var replica = ReplicationToolkitSurfaceTests.Open(replicaDir);

            var encoded = new byte[8];
            BinaryPrimitives.WriteInt64BigEndian(encoded, 1);
            primary.Put(Key, encoded, writeOptions: ReplicationToolkitSurfaceTests.Sync());
            var page = new ReplicationSource(primary).GetWalUpdates(0).ToList();
            var batches = page.Select(p => (p.SequenceNumber, p.Data)).ToList();

            var applier = new BatchApplier(replica);
            var first = applier.ApplyPage(epoch: 1, batches);
            first.Applied.Should().Be(page.Count);
            var watermark = applier.Watermark;

            var second = applier.ApplyPage(epoch: 1, batches);
            second.Applied.Should().Be(0);
            second.Skipped.Should().Be(page.Count);
            applier.Watermark.Should().Be(watermark);
            BinaryPrimitives.ReadInt64BigEndian(replica.Get(Key)).Should().Be(1);
        }
        finally
        {
            ReplicationToolkitSurfaceTests.TryDelete(primaryDir);
            ReplicationToolkitSurfaceTests.TryDelete(replicaDir);
        }
    }

    [Fact]
    public void ApplyPage_LowerEpoch_RefusesAndWritesNothing()
    {
        var primaryDir = ReplicationToolkitSurfaceTests.NewTempDir();
        var replicaDir = ReplicationToolkitSurfaceTests.NewTempDir();
        try
        {
            using var primary = ReplicationToolkitSurfaceTests.Open(primaryDir);
            using var replica = ReplicationToolkitSurfaceTests.Open(replicaDir);

            primary.Put(Key, "new"u8.ToArray(), writeOptions: ReplicationToolkitSurfaceTests.Sync());
            var page = new ReplicationSource(primary).GetWalUpdates(0).ToList();
            var batches = page.Select(p => (p.SequenceNumber, p.Data)).ToList();

            var applier = new BatchApplier(replica);
            applier.ApplyPage(epoch: 3, []).Status.Should().Be(ApplyStatus.Applied);
            applier.SeenEpoch.Should().Be(3);

            var refused = applier.ApplyPage(epoch: 2, batches);
            refused.Status.Should().Be(ApplyStatus.EpochRefused);
            refused.Applied.Should().Be(0);
            replica.Get(Key).Should().BeNull("a stale-epoch page must not be ingested");
            applier.SeenEpoch.Should().Be(3);
        }
        finally
        {
            ReplicationToolkitSurfaceTests.TryDelete(primaryDir);
            ReplicationToolkitSurfaceTests.TryDelete(replicaDir);
        }
    }

    [Fact]
    public void CrashBetweenApplyAndAnythingElse_DerivedWatermarkStillSkips()
    {
        // The T2v harness already crash-injects ReplicationApply (the primitive BatchApplier
        // uses). This test asserts BatchApplier's skip still holds after that resurrection.
        var harness = LocateHarness();
        var primaryDir = ReplicationToolkitSurfaceTests.NewTempDir();
        var replicaDir = ReplicationToolkitSurfaceTests.NewTempDir();
        try
        {
            using var proc = StartReplIngest(harness, primaryDir, replicaDir);
            var ingested = WaitForIngested(proc);
            proc.Kill(entireProcessTree: true);
            proc.WaitForExit();

            using var replica = ReplicationToolkitSurfaceTests.Open(replicaDir);
            var applier = new BatchApplier(replica);
            applier.Watermark.Should().Be(ingested);

            var pageBytes = File.ReadAllBytes(PagePath(replicaDir));
            var second = applier.ApplyPage(1, [(ingested, pageBytes)]);
            second.Status.Should().Be(ApplyStatus.Applied);
            second.Applied.Should().Be(0);
            second.Skipped.Should().Be(1);
        }
        finally
        {
            ReplicationToolkitSurfaceTests.TryDelete(primaryDir);
            ReplicationToolkitSurfaceTests.TryDelete(replicaDir);
            try { File.Delete(PagePath(replicaDir)); } catch { /* best effort */ }
        }
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
