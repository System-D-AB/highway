using FluentAssertions;
using Highway.Server.Storage.Rocks;
using RocksDbSharp;
using StackExchange.Redis;
using Xunit;

namespace Highway.Server.Tests.Storage;

/// <summary>
/// Feature 065 T6 — storage format 4 (Highway 3.0) refuses 2.x data rather than serving it as an empty,
/// healthy-looking store, whatever the stamp says; <c>--wipe-data</c> clears a directory for a fresh start
/// and keeps the node's replication identity.
/// </summary>
public class StorageFormatTests
{
    /// <summary>Writes a 2.x-layout key (family tag first) straight into the data column family.</summary>
    private static void PlantLegacyKey(string dir)
    {
        using (RocksDbStore.Open(dir)) { }   // create the families
        var families = new ColumnFamilies();
        foreach (var name in Highway.Server.Storage.Layout.HighwayColumnFamilies.OrderedNames.Skip(1))
            families.Add(name, new ColumnFamilyOptions());
        using var db = RocksDb.Open(new DbOptions(), dir, families);
        db.Put("kreg:node:old\0\0"u8.ToArray(), "x"u8.ToArray(), db.GetColumnFamily("data"));
    }

    [Fact]
    public void A2xDataDirectory_IsRefused_WithTheUpgradePath()
    {
        var dir = ReplicationToolkitSurfaceTests.NewTempDir();
        try
        {
            PlantLegacyKey(dir);
            var open = () => RocksDbStore.Open(dir);
            open.Should().Throw<InvalidOperationException>()
                .WithMessage("*Highway 2.x*does not migrate in place*--wipe-data*");
        }
        finally { ReplicationToolkitSurfaceTests.TryDelete(dir); }
    }

    [Fact]
    public void WipeData_EmptiesTheDirectory_KeepingTheReplicationIdentity()
    {
        var dir = ReplicationToolkitSurfaceTests.NewTempDir();
        try
        {
            PlantLegacyKey(dir);
            File.WriteAllText(Path.Combine(dir, "repl-epoch.txt"), "7");

            HighwayStorageMaintenance.WipeData(dir).Should().BeGreaterThan(0);

            File.ReadAllText(Path.Combine(dir, "repl-epoch.txt")).Should().Be("7", "the node's epoch survives a wipe");
            using var store = RocksDbStore.Open(dir);   // opens clean on format 4
            store.Replication.Epoch.Should().Be(7);
        }
        finally { ReplicationToolkitSurfaceTests.TryDelete(dir); }
    }

    [Fact]
    public void WipeData_RefusesWhileABrokerHoldsTheDirectory()
    {
        var dir = ReplicationToolkitSurfaceTests.NewTempDir();
        try
        {
            using var running = RocksDbStore.Open(dir);
            var wipe = () => HighwayStorageMaintenance.WipeData(dir);
            wipe.Should().Throw<Exception>().Where(e => e.Message.Contains("lock", StringComparison.OrdinalIgnoreCase));
        }
        finally { ReplicationToolkitSurfaceTests.TryDelete(dir); }
    }

    [Fact]
    public void AFormat3Stamp_IsRefused_WithTheSameGuidance()
    {
        var dir = ReplicationToolkitSurfaceTests.NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "highway.format"), "3");
            var build = () => new HighwayServerBuilder().WithDataDir(dir).Build();
            build.Should().Throw<InvalidOperationException>().WithMessage("*Highway 2.x*--wipe-data*");
        }
        finally { ReplicationToolkitSurfaceTests.TryDelete(dir); }
    }

    [Fact]
    public void AReplica_RefusesAPrimaryThatDoesNotEchoItsFormat()
    {
        // A 2.x primary ignores the replica's FORMAT and answers [epoch, minSeq]: that is not consent.
        ReplicaPuller.PrimaryFormatMismatch([RedisResult.Create(1), RedisResult.Create(0)])
            .Should().Be("3 (Highway 2.x)");
        ReplicaPuller.PrimaryFormatMismatch([RedisResult.Create(1), RedisResult.Create(0), RedisResult.Create(5)])
            .Should().Be("5");
        ReplicaPuller.PrimaryFormatMismatch([RedisResult.Create(1), RedisResult.Create(0),
                RedisResult.Create(Highway.Server.HighwayServerBuilder.StorageFormatVersion)])
            .Should().BeNull();
    }
}
