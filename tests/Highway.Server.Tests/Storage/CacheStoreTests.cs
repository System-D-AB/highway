using FluentAssertions;
using Highway.Server.Storage.Cache;
using Xunit;

namespace Highway.Server.Tests.Storage;

/// <summary>
/// Feature 044 T1 — the broker-local cache store seam and its two implementations. Both stores
/// must behave identically: set/get/remove/clear round-trip, filter-on-read expiry (a lapsed
/// entry reads as a miss before any sweep), and the size accounting the sweeper's cap rests on.
/// The <see cref="CacheSweeper"/> drops expired entries and clears wholesale over the cap.
/// </summary>
public class CacheStoreTests
{
    private static long Now => DateTimeOffset.UtcNow.UtcTicks;
    private static long InAnHour => DateTimeOffset.UtcNow.AddHours(1).UtcTicks;

    public static IEnumerable<object[]> Stores()
    {
        yield return new object[] { "in-memory" };
        yield return new object[] { "rocksdb" };
    }

    private static IHighwayCacheStore Open(string kind, out string? dir)
    {
        if (kind == "rocksdb")
        {
            dir = Path.Combine(Path.GetTempPath(), "hwcache-" + Guid.NewGuid().ToString("N"));
            return RocksDbCacheStore.Open(dir);
        }
        dir = null;
        return new InMemoryCacheStore();
    }

    private static void Cleanup(IHighwayCacheStore store, string? dir)
    {
        store.Dispose();
        if (dir is not null && Directory.Exists(dir))
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public void SetGetRemoveClear_RoundTrips(string kind)
    {
        var store = Open(kind, out var dir);
        try
        {
            store.Get("k", Now).Should().BeNull("an absent key is a miss");

            store.Set("k", "hello"u8.ToArray(), InAnHour);
            store.Get("k", Now).Should().Equal("hello"u8.ToArray());

            store.Set("k", "world"u8.ToArray(), InAnHour); // overwrite
            store.Get("k", Now).Should().Equal("world"u8.ToArray());

            store.Remove("k");
            store.Get("k", Now).Should().BeNull("a removed key is a miss");

            store.Set("a", "1"u8.ToArray(), InAnHour);
            store.Set("b", "2"u8.ToArray(), InAnHour);
            store.Clear();
            store.Get("a", Now).Should().BeNull("clear drops every entry");
            store.Get("b", Now).Should().BeNull();
        }
        finally { Cleanup(store, dir); }
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public void LapsedEntry_ReadsAsMiss_BeforeAnySweep(string kind)
    {
        var store = Open(kind, out var dir);
        try
        {
            var expiry = DateTimeOffset.UtcNow.AddMinutes(5).UtcTicks;
            store.Set("k", "v"u8.ToArray(), expiry);

            // Read with a 'now' past the expiry — filter-on-read returns a miss, no sweep needed.
            var future = DateTimeOffset.UtcNow.AddMinutes(10).UtcTicks;
            store.Get("k", future).Should().BeNull("an expired entry reads as gone before the sweeper runs");
            store.GetExpiryTicks("k", future).Should().BeNull();
        }
        finally { Cleanup(store, dir); }
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public void GetExpiryTicks_ReturnsTheAbsoluteExpiry_ForALiveEntry(string kind)
    {
        var store = Open(kind, out var dir);
        try
        {
            var expiry = InAnHour;
            store.Set("k", "v"u8.ToArray(), expiry);
            store.GetExpiryTicks("k", Now).Should().Be(expiry);
        }
        finally { Cleanup(store, dir); }
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public void SweepExpired_ReclaimsLapsedEntries(string kind)
    {
        var store = Open(kind, out var dir);
        try
        {
            store.Set("live", "v"u8.ToArray(), InAnHour);
            store.Set("dead", "v"u8.ToArray(), DateTimeOffset.UtcNow.AddSeconds(1).UtcTicks);

            var future = DateTimeOffset.UtcNow.AddMinutes(10).UtcTicks;
            store.SweepExpired(future);

            store.Get("dead", future).Should().BeNull();
            store.Get("live", Now).Should().NotBeNull("the sweep leaves a still-live entry alone");
        }
        finally { Cleanup(store, dir); }
    }

    [Fact]
    public void InMemoryStore_TracksApproximateSize()
    {
        using var store = new InMemoryCacheStore();
        store.ApproximateSizeBytes().Should().Be(0);
        store.Set("k", new byte[100], InAnHour);
        store.ApproximateSizeBytes().Should().Be(100);
        store.Set("k", new byte[40], InAnHour); // overwrite shrinks
        store.ApproximateSizeBytes().Should().Be(40);
        store.Remove("k");
        store.ApproximateSizeBytes().Should().Be(0);
    }

    [Fact]
    public void Sweeper_ClearsOverTheCap_AndRecordsTheEvent()
    {
        var store = new InMemoryCacheStore();
        var events = new List<string>();
        // A tiny cap; two entries push it over.
        using var sweeper = new CacheSweeper(
            store, maxSizeBytes: 50, interval: TimeSpan.FromMilliseconds(-1) /* not auto-firing */,
            clock: TimeProvider.System, onEvent: events.Add);

        store.Set("a", new byte[40], InAnHour);
        store.Set("b", new byte[40], InAnHour);
        store.ApproximateSizeBytes().Should().BeGreaterThan(50);

        sweeper.Tick();

        store.ApproximateSizeBytes().Should().Be(0, "over the cap → cleared wholesale");
        sweeper.ClearedOversizeCount.Should().Be(1);
        events.Should().ContainSingle().Which.Should().StartWith("cache-cleared-oversize");
    }

    [Fact]
    public void Sweeper_UnderTheCap_DoesNotClear()
    {
        var store = new InMemoryCacheStore();
        using var sweeper = new CacheSweeper(store, maxSizeBytes: 10_000, interval: TimeSpan.FromMilliseconds(-1));

        store.Set("a", new byte[40], InAnHour);
        sweeper.Tick();

        store.Get("a", Now).Should().NotBeNull("under the cap, the sweeper leaves live entries");
        sweeper.ClearedOversizeCount.Should().Be(0);
    }
}
