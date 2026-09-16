using System.Reflection;
using System.Text;
using FluentAssertions;
using Highway.Server.Storage;
using Highway.Server.Storage.Cache;
using Highway.Server.Storage.Rocks;
using Xunit;

namespace Highway.Server.Tests.Storage;

/// <summary>
/// Feature 044 T5 gate — the feature's central safety property: a cache write <b>cannot</b> enter
/// replication. Proven two ways. Behaviourally: a value written to the cache store is absent from
/// the broker DB's WAL page (`Pull`), while a broker write is present — because the cache is a
/// physically separate RocksDB, not a column family in the replicated one (RocksDB ships one WAL
/// per database). Structurally: the cache store type holds no reference to the broker store or the
/// replication feeder, so the two cannot be coupled by a later edit.
/// </summary>
public class CacheReplicationIsolationTests
{
    [Fact]
    public void ACacheWrite_IsAbsentFromTheBrokerWal_AsABrokerWriteIsPresent()
    {
        var dir = ReplicationToolkitSurfaceTests.NewTempDir();
        try
        {
            using var broker = RocksDbStore.Open(dir);
            using var cache = RocksDbCacheStore.Open(Path.Combine(dir, "cache"));

            var cacheToken = "CACHE-TOKEN-" + Guid.NewGuid().ToString("N");
            var brokerToken = "BROKER-TOKEN-" + Guid.NewGuid().ToString("N");

            cache.Set("k", Encoding.UTF8.GetBytes(cacheToken),
                DateTimeOffset.UtcNow.AddHours(1).UtcTicks);

            using (var batch = broker.NewBatch())
            {
                broker.Set(batch, "kv-marker"u8.ToArray(), Encoding.UTF8.GetBytes(brokerToken));
                batch.Commit();
            }

            var page = broker.Replication.Pull(0, maxBytes: 10_000_000);
            var wal = page.Batches.SelectMany(b => b.Data).ToArray();

            Contains(wal, Encoding.UTF8.GetBytes(brokerToken))
                .Should().BeTrue("a broker write ships on the WAL — sanity that Pull sees writes");
            Contains(wal, Encoding.UTF8.GetBytes(cacheToken))
                .Should().BeFalse("the cache lives in a separate database; its writes never reach the replicated WAL");
        }
        finally { ReplicationToolkitSurfaceTests.TryDelete(dir); }
    }

    [Fact]
    public void TheCacheStoreType_HoldsNoReferenceToTheBrokerStoreOrFeeder()
    {
        // Structural isolation: no field of the durable cache store can be a broker store or a
        // feeder. If a later edit tried to couple them, this fails at the type level.
        var fields = typeof(RocksDbCacheStore)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        foreach (var f in fields)
        {
            typeof(IHighwayStore).IsAssignableFrom(f.FieldType)
                .Should().BeFalse($"cache store field '{f.Name}' must not be a broker store");
            typeof(ReplicationFeeder).IsAssignableFrom(f.FieldType)
                .Should().BeFalse($"cache store field '{f.Name}' must not be the replication feeder");
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
