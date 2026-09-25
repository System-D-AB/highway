using System.Text;
using FluentAssertions;
using Highway.Server.Storage.Rocks;
using Xunit;

namespace Highway.Server.Tests.Storage;

/// <summary>
/// Feature 066 — a replica behind the in-memory tail catches up through one held WAL tailer, not a fresh
/// WAL read per page (each of which re-read the live WAL file from its start), and applies each page as
/// one synced write. The replica must end byte-for-byte equal to the primary.
/// </summary>
public class ReplicaCatchUpTests
{
    /// <summary>A key inside a work box (format 4), so a reopened store does not take it for 2.x data.</summary>
    private static byte[] Key(int i) => [0, 0, 0, 32, 1, .. Encoding.UTF8.GetBytes($"key-{i:D6}")];

    private static void Commits(RocksDbStore store, int from, int to)
    {
        for (var i = from; i < to; i++)
        {
            using var batch = store.NewBatch();
            store.Set(batch, Key(i), Encoding.UTF8.GetBytes(new string('v', 150) + i));
            if (i % 5 == 0) store.Delete(batch, Key(i - 3));
            batch.Commit();
        }
    }

    /// <summary>Pulls page after page until the replica reaches the primary's sequence; returns the page count.</summary>
    private static int CatchUp(ReplicationFeeder primary, BatchApplier applier, int pageBytes)
    {
        var pages = 0;
        while (applier.Watermark < primary.Engine.GetLatestSequenceNumber())
        {
            var page = primary.Pull(applier.Watermark, pageBytes);
            page.Batches.Should().NotBeEmpty("a replica behind is always served something or refused");
            applier.ApplyPage(page.Epoch, page.Batches).Status.Should().Be(ApplyStatus.Applied);
            pages++;
        }
        return pages;
    }

    private static void ShouldMatch(RocksDbStore primary, RocksDbStore replica, int keys)
    {
        replica.Replication.Engine.GetLatestSequenceNumber().Should().Be(primary.Replication.Engine.GetLatestSequenceNumber());
        using var ps = primary.Snapshot();
        using var rs = replica.Snapshot();
        for (var i = 0; i < keys; i++)
        {
            var key = Key(i);
            replica.Get(rs, key).Should().Equal(primary.Get(ps, key), $"key-{i:D6}");
        }
    }

    [Fact]
    public void AFarBehindReplica_CatchesUpThroughOneTailer_AndMatchesThePrimary()
    {
        var primaryDir = ReplicationToolkitSurfaceTests.NewTempDir();
        var replicaDir = ReplicationToolkitSurfaceTests.NewTempDir();
        try
        {
            using var primary = RocksDbStore.Open(primaryDir);
            using var replica = RocksDbStore.Open(replicaDir);
            Commits(primary, 0, 1_500);   // written before any pull: not in the in-memory tail

            var applier = new BatchApplier(replica.Replication.Engine);
            var pages = CatchUp(primary.Replication, applier, pageBytes: 16 * 1024);

            pages.Should().BeGreaterThan(5, "the catch-up spans many pages");
            primary.Replication.WalTailers.Opened.Should().Be(1, "every page after the first continues the same tailer");
            applier.SyncedWrites.Should().Be(pages, "one synced write per page");
            ShouldMatch(primary, replica, 1_500);
        }
        finally
        {
            ReplicationToolkitSurfaceTests.TryDelete(primaryDir);
            ReplicationToolkitSurfaceTests.TryDelete(replicaDir);
        }
    }

    [Fact]
    public void AfterAPrimaryRestart_ABehindReplica_CatchesUpAndMatches()
    {
        var primaryDir = ReplicationToolkitSurfaceTests.NewTempDir();
        var replicaDir = ReplicationToolkitSurfaceTests.NewTempDir();
        try
        {
            using var replica = RocksDbStore.Open(replicaDir);
            var applier = new BatchApplier(replica.Replication.Engine);

            using (var primary = RocksDbStore.Open(primaryDir))
            {
                Commits(primary, 0, 500);
                CatchUp(primary.Replication, applier, 32 * 1024);
                Commits(primary, 500, 1_500);   // the replica falls behind, then the primary restarts
            }

            using (var primary = RocksDbStore.Open(primaryDir))
            {
                Commits(primary, 1_500, 2_000);   // interleaved with the catch-up below, as live traffic would be
                CatchUp(primary.Replication, applier, 32 * 1024);
                ShouldMatch(primary, replica, 2_000);
            }
        }
        finally
        {
            ReplicationToolkitSurfaceTests.TryDelete(primaryDir);
            ReplicationToolkitSurfaceTests.TryDelete(replicaDir);
        }
    }

    [Fact]
    public void AnEpochChange_DropsTheHeldTailers()
    {
        var primaryDir = ReplicationToolkitSurfaceTests.NewTempDir();
        var replicaDir = ReplicationToolkitSurfaceTests.NewTempDir();
        try
        {
            using var primary = RocksDbStore.Open(primaryDir);
            using var replica = RocksDbStore.Open(replicaDir);
            Commits(primary, 0, 1_000);
            var applier = new BatchApplier(replica.Replication.Engine);

            var page = primary.Replication.Pull(applier.Watermark, 16 * 1024);
            applier.ApplyPage(page.Epoch, page.Batches);
            primary.Replication.WalTailers.Opened.Should().Be(1);

            primary.Replication.WalTailers.HoldingWal.Should().BeTrue("a held tailer pauses WAL deletion");

            primary.Replication.ObserveHigherEpoch(primary.Replication.Epoch + 1, "test: a new history");
            primary.Replication.WalTailers.HoldingWal.Should().BeFalse("the epoch change closed it: retention resumes");
            page = primary.Replication.Pull(applier.Watermark, 16 * 1024);
            applier.ApplyPage(page.Epoch, page.Batches);
            primary.Replication.WalTailers.Opened.Should().Be(2, "nothing held across an epoch change");
        }
        finally
        {
            ReplicationToolkitSurfaceTests.TryDelete(primaryDir);
            ReplicationToolkitSurfaceTests.TryDelete(replicaDir);
        }
    }
}
