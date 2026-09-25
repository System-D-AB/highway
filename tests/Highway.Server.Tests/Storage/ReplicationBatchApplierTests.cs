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

    /// <summary>
    /// 066 C2: a page of many commits is applied with one synced write, and the replica ends at the
    /// primary's sequence holding the same bytes, page after page.
    /// </summary>
    [Fact]
    public void ApplyPage_ContiguousPage_IsOneSyncedWrite_AndKeepsTheSequenceAligned()
    {
        var primaryDir = ReplicationToolkitSurfaceTests.NewTempDir();
        var replicaDir = ReplicationToolkitSurfaceTests.NewTempDir();
        try
        {
            using var primary = RocksDbStore.Open(primaryDir);
            using var replicaStore = RocksDbStore.Open(replicaDir);
            var applier = new BatchApplier(replicaStore.Replication.Engine);

            void Commits(int from, int to)
            {
                for (var i = from; i < to; i++)
                {
                    using var batch = primary.NewBatch();
                    primary.Set(batch, Encoding.UTF8.GetBytes($"key-{i:D3}"), Encoding.UTF8.GetBytes($"v{i}"));
                    if (i % 3 == 0) primary.Delete(batch, Encoding.UTF8.GetBytes($"key-{i - 1:D3}"));
                    if (i % 7 == 0) primary.DeleteRange(batch, Encoding.UTF8.GetBytes($"key-{i - 2:D3}"));
                    batch.Commit();
                }
            }

            // Written before the first pull, so served from the WAL: one chunk, already one batch.
            Commits(1, 40);
            var page = primary.Replication.Pull(0, 1_000_000);
            var first = applier.ApplyPage(page.Epoch, page.Batches);
            first.Applied.Should().Be(page.Batches.Count);
            applier.SyncedWrites.Should().Be(1, "the whole page is one write");
            applier.Watermark.Should().Be(primary.Replication.Engine.GetLatestSequenceNumber());

            // Written after it, so served from the in-memory tail one commit per batch: concatenated.
            Commits(40, 80);
            var next = primary.Replication.Pull(applier.Watermark, 1_000_000);
            next.Batches.Count.Should().BeGreaterThan(1, "the tail serves each commit as its own batch");
            applier.ApplyPage(next.Epoch, next.Batches);
            applier.SyncedWrites.Should().Be(2, "still one write for the whole page");
            applier.Watermark.Should().Be(primary.Replication.Engine.GetLatestSequenceNumber());

            using var ps = primary.Snapshot();
            using var rs = replicaStore.Snapshot();
            for (var i = 0; i < 80; i++)
            {
                var key = Encoding.UTF8.GetBytes($"key-{i:D3}");
                replicaStore.Get(rs, key).Should().Equal(primary.Get(ps, key), $"key-{i:D3}");
            }
        }
        finally
        {
            ReplicationToolkitSurfaceTests.TryDelete(primaryDir);
            ReplicationToolkitSurfaceTests.TryDelete(replicaDir);
        }
    }

    [Fact]
    public void TryConcat_RefusesANonContiguousRun()
    {
        static byte[] Batch(ulong seq, uint count)
        {
            var b = new byte[12];
            BinaryPrimitives.WriteUInt64LittleEndian(b, seq);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), count);
            return b;
        }

        BatchApplier.TryConcat([Batch(10, 2), Batch(12, 3)], out var merged).Should().BeTrue();
        BinaryPrimitives.ReadUInt64LittleEndian(merged).Should().Be(10);
        BinaryPrimitives.ReadUInt32LittleEndian(merged.AsSpan(8)).Should().Be(5);

        BatchApplier.TryConcat([Batch(10, 2), Batch(13, 1)], out _).Should().BeFalse("a gap");
        BatchApplier.TryConcat([Batch(10, 2), Batch(11, 1)], out _).Should().BeFalse("an overlap");
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
