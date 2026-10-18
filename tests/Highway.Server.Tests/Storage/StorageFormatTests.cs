using FluentAssertions;
using Highway.Server.Storage.Crystal;
using Highway.Server.Storage.Replication;
using StackExchange.Redis;
using Xunit;

namespace Highway.Server.Tests.Storage;

/// <summary>
/// Feature 065 T6 — storage format 4 (Highway 3.0) refuses data written by an earlier engine rather than serving it as
/// an empty, healthy-looking store; <c>--wipe-data</c> clears a directory for a fresh start and keeps the node's
/// replication identity. 076: the directory is a SystemD.Crystal store, and a RocksDB-era one (2.x, or a 3.0
/// pre-release) is the earlier engine.
/// </summary>
public class StorageFormatTests
{
    /// <summary>A directory Highway's RocksDB engine wrote: its <c>CURRENT</c> file and a table file.</summary>
    private static void PlantRocksDbDirectory(string dir)
    {
        File.WriteAllText(Path.Combine(dir, "CURRENT"), "MANIFEST-000001\n");
        File.WriteAllBytes(Path.Combine(dir, "000004.sst"), [1, 2, 3]);
    }

    [Fact]
    public void A2xDataDirectory_IsRefused_WithTheUpgradePath()
    {
        var dir = TempDirs.NewTempDir();
        try
        {
            PlantRocksDbDirectory(dir);
            var open = () => CrystalHighwayStore.Open(dir);
            open.Should().Throw<InvalidOperationException>()
                .WithMessage("*Highway 2.x*does not migrate in place*--wipe-data*");
            File.Exists(Path.Combine(dir, "000004.sst")).Should().BeTrue("a refusal touches nothing");
        }
        finally { TempDirs.TryDelete(dir); }
    }

    [Fact]
    public void WipeData_EmptiesTheDirectory_KeepingTheReplicationIdentity()
    {
        var dir = TempDirs.NewTempDir();
        try
        {
            string nodeId;
            using (var store = CrystalHighwayStore.Open(dir, replication: new HighwayReplicationOptions { StartAsReplica = true }))
            {
                store.Replication.Promote("test");
                store.Replication.Promote("again");   // already primary: a no-op
                store.Replication.Epoch.Should().Be(2);
                using var batch = store.NewBatch();
                store.Set(batch, Highway.Server.Storage.Layout.HighwayKeyspace.Kv("k"), "v"u8.ToArray());
                batch.Commit();
                nodeId = store.Replication.CrystalNodeId;
            }

            HighwayStorageMaintenance.WipeData(dir).Should().BeGreaterThan(0);

            using var reopened = CrystalHighwayStore.Open(dir);   // opens clean
            reopened.Replication.Epoch.Should().Be(2, "the node's epoch survives a wipe");
            reopened.Replication.CrystalNodeId.Should().Be(nodeId, "so does its identity");
            reopened.DumpData().Should().BeEmpty("every message is gone");
        }
        finally { TempDirs.TryDelete(dir); }
    }

    [Fact]
    public void WipeData_OfAnEarlierEnginesDirectory_EmptiesIt()
    {
        var dir = TempDirs.NewTempDir();
        try
        {
            PlantRocksDbDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "repl-epoch.txt"), "7");

            HighwayStorageMaintenance.WipeData(dir).Should().Be(3);

            Directory.EnumerateFileSystemEntries(dir).Should().BeEmpty("nothing of the old engine means anything to 3.0");
            using var store = CrystalHighwayStore.Open(dir);
            store.Replication.Epoch.Should().Be(1);
        }
        finally { TempDirs.TryDelete(dir); }
    }

    [Fact]
    public void WipeData_RefusesWhileABrokerHoldsTheDirectory()
    {
        var dir = TempDirs.NewTempDir();
        try
        {
            using var running = CrystalHighwayStore.Open(dir);
            var wipe = () => HighwayStorageMaintenance.WipeData(dir);
            wipe.Should().Throw<Exception>().Where(e => e.Message.Contains("lock", StringComparison.OrdinalIgnoreCase));
        }
        finally { TempDirs.TryDelete(dir); }
    }

    [Fact]
    public void AFormat3Stamp_IsRefused_WithTheSameGuidance()
    {
        var dir = TempDirs.NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "highway.format"), "3");
            var build = () => new HighwayServerBuilder().WithDataDir(dir).Build();
            build.Should().Throw<InvalidOperationException>().WithMessage("*Highway 2.x*--wipe-data*");
        }
        finally { TempDirs.TryDelete(dir); }
    }

    [Fact]
    public void AReplica_RefusesAPrimaryThatDoesNotEchoItsFormat()
    {
        // A 2.x primary ignores the replica's FORMAT and answers [epoch, minSeq]: that is not consent. A 3.0 pre-release
        // primary echoes format 4 but names no replication endpoint: it cannot be followed (076).
        StandbyAgent.PrimaryFormatMismatch([RedisResult.Create(1), RedisResult.Create(0)])
            .Should().Be("3 (Highway 2.x)");
        StandbyAgent.PrimaryFormatMismatch([RedisResult.Create(1), RedisResult.Create(0), RedisResult.Create(5), RedisResult.Create((RedisValue)"h:1")])
            .Should().Be("5");
        StandbyAgent.PrimaryFormatMismatch([RedisResult.Create(1), RedisResult.Create(0),
                RedisResult.Create(Highway.Server.HighwayServerBuilder.StorageFormatVersion)])
            .Should().Contain("without SystemD.Crystal replication");
        StandbyAgent.PrimaryFormatMismatch([RedisResult.Create(1), RedisResult.Create(0),
                RedisResult.Create(Highway.Server.HighwayServerBuilder.StorageFormatVersion), RedisResult.Create((RedisValue)"h:1")])
            .Should().BeNull();
    }
}
