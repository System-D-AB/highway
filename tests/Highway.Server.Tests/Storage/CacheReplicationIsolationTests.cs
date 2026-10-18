using System.Reflection;
using System.Text;
using FluentAssertions;
using Highway.Server.Storage;
using Highway.Server.Storage.Cache;
using Highway.Server.Storage.Crystal;
using Highway.Server.Storage.Replication;
using Xunit;

namespace Highway.Server.Tests.Storage;

/// <summary>
/// Feature 044 T5 gate — the feature's central safety property: a cache write <b>cannot</b> enter
/// replication. Proven two ways. Behaviourally (076): the cache lives in its own Crystal database, created
/// not replicated, and a cache write lands there and nowhere in the replicated <c>highway</c> database —
/// Crystal replicates whole databases, so a not-replicated one never ships. Structurally: the cache store
/// type holds no reference to the broker store or the replication policy, so the two cannot be coupled by a
/// later edit.
/// </summary>
public class CacheReplicationIsolationTests
{
    [Fact]
    public void ACacheWrite_LandsInANotReplicatedDatabase_AsABrokerWriteLandsInTheReplicatedOne()
    {
        var dir = TempDirs.NewTempDir();
        try
        {
            using var broker = CrystalHighwayStore.Open(dir);
            using var cache = CrystalCacheStore.Open(broker.Crystal, CrystalHighwayStore.CacheDatabaseName);

            var cacheToken = "CACHE-TOKEN-" + Guid.NewGuid().ToString("N");
            var brokerToken = "BROKER-TOKEN-" + Guid.NewGuid().ToString("N");

            cache.Set("k", Encoding.UTF8.GetBytes(cacheToken), DateTimeOffset.UtcNow.AddHours(1).UtcTicks);
            using (var batch = broker.NewBatch())
            {
                broker.Set(batch, [0, 0, 0, 0, 0x10, (byte)'k', (byte)'m'], Encoding.UTF8.GetBytes(brokerToken));
                batch.Commit();
            }

            var databases = broker.Crystal.ListDatabases().ToDictionary(d => d.Name);
            databases[CrystalHighwayStore.DatabaseName].Replicated.Should().BeTrue("the broker's data ships to standbys");
            databases[CrystalHighwayStore.CacheDatabaseName].Replicated.Should().BeFalse("the cache never ships");

            var replicated = broker.DumpData().SelectMany(e => e.Value).ToArray();
            Contains(replicated, Encoding.UTF8.GetBytes(brokerToken)).Should().BeTrue("sanity: the dump sees broker writes");
            Contains(replicated, Encoding.UTF8.GetBytes(cacheToken))
                .Should().BeFalse("the cache's writes never reach the replicated database");
        }
        finally { TempDirs.TryDelete(dir); }
    }

    [Fact]
    public void TheCacheStoreType_HoldsNoReferenceToTheBrokerStoreOrFeeder()
    {
        var fields = typeof(CrystalCacheStore)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        foreach (var f in fields)
        {
            typeof(IHighwayStore).IsAssignableFrom(f.FieldType)
                .Should().BeFalse($"cache store field '{f.Name}' must not be a broker store");
            typeof(HighwayReplication).IsAssignableFrom(f.FieldType)
                .Should().BeFalse($"cache store field '{f.Name}' must not be the replication policy");
        }
    }

    private static bool Contains(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length) return false;
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j]) { match = false; break; }
            }
            if (match) return true;
        }
        return false;
    }
}
