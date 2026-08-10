using System.Text;
using FluentAssertions;
using Highway.Client.Caching;
using Highway.Server;
using Microsoft.Extensions.Caching.Distributed;
using StackExchange.Redis;
using Xunit;

namespace Highway.Client.Tests.Caching;

/// <summary>
/// Integration tests for <see cref="HighwayCache"/> running against an embedded
/// HighwayTestServer (Garnet). Covers round-trip, expiration, sliding, combined,
/// remove, refresh, key prefix, and isolation.
/// </summary>
public class HighwayCacheTests : IDisposable
{
    private readonly HighwayTestServer _server = new();
    private readonly ConnectionMultiplexer _redis;
    private readonly HighwayCache _cache;

    public HighwayCacheTests()
    {
        _redis = ConnectionMultiplexer.Connect(_server.ConnectionString);
        _cache = new HighwayCache(_redis, new HighwayCacheOptions());
    }

    public void Dispose()
    {
        _cache.Dispose();
        _redis.Dispose();
        _server.Dispose();
    }

    // ─────────────────────────────────────────────────────────────────────
    // Round-trip: Set then Get returns same bytes (sync and async)
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Set_ThenGet_ReturnsSameBytes_Sync()
    {
        var value = Encoding.UTF8.GetBytes("hello-world");
        _cache.Set("rt-sync", value, new DistributedCacheEntryOptions());

        var result = _cache.Get("rt-sync");

        result.Should().BeEquivalentTo(value);
    }

    [Fact]
    public async Task SetAsync_ThenGetAsync_ReturnsSameBytes()
    {
        var value = Encoding.UTF8.GetBytes("hello-async");
        await _cache.SetAsync("rt-async", value, new DistributedCacheEntryOptions());

        var result = await _cache.GetAsync("rt-async");

        result.Should().BeEquivalentTo(value);
    }

    [Fact]
    public void Get_NonExistentKey_ReturnsNull()
    {
        var result = _cache.Get("does-not-exist");
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_NonExistentKey_ReturnsNull()
    {
        var result = await _cache.GetAsync("does-not-exist");
        result.Should().BeNull();
    }

    // ─────────────────────────────────────────────────────────────────────
    // Absolute expiration
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AbsoluteExpiration_AfterExpiry_GetReturnsNull()
    {
        var value = Encoding.UTF8.GetBytes("expires-soon");
        await _cache.SetAsync("abs-exp", value, new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(1)
        });

        // Immediately should still be there
        var immediate = await _cache.GetAsync("abs-exp");
        immediate.Should().NotBeNull();

        // Wait for expiry
        await Task.Delay(TimeSpan.FromSeconds(1.5));

        var expired = await _cache.GetAsync("abs-exp");
        expired.Should().BeNull();
    }

    [Fact]
    public async Task AbsoluteExpiration_DateTimeOffset_AfterExpiry_GetReturnsNull()
    {
        var value = Encoding.UTF8.GetBytes("expires-at-time");
        await _cache.SetAsync("abs-exp-dto", value, new DistributedCacheEntryOptions
        {
            AbsoluteExpiration = DateTimeOffset.UtcNow.AddSeconds(1)
        });

        var immediate = await _cache.GetAsync("abs-exp-dto");
        immediate.Should().NotBeNull();

        await Task.Delay(TimeSpan.FromSeconds(1.5));

        var expired = await _cache.GetAsync("abs-exp-dto");
        expired.Should().BeNull();
    }

    // ─────────────────────────────────────────────────────────────────────
    // Sliding expiration
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SlidingExpiration_GetRefreshesTtl()
    {
        var value = Encoding.UTF8.GetBytes("sliding-value");
        await _cache.SetAsync("slide", value, new DistributedCacheEntryOptions
        {
            SlidingExpiration = TimeSpan.FromSeconds(2)
        });

        // Access at 1.5s — should refresh the TTL
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        var refreshed = await _cache.GetAsync("slide");
        refreshed.Should().BeEquivalentTo(value);

        // Access at 1.5s after last access (3s total) — should still be alive
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        var stillAlive = await _cache.GetAsync("slide");
        stillAlive.Should().BeEquivalentTo(value);

        // Wait without accessing — should expire
        await Task.Delay(TimeSpan.FromSeconds(2.5));
        var expired = await _cache.GetAsync("slide");
        expired.Should().BeNull();
    }

    // ─────────────────────────────────────────────────────────────────────
    // Combined expiration: sliding does not extend past absolute deadline
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CombinedExpiration_SlidingDoesNotExtendPastAbsolute()
    {
        var value = Encoding.UTF8.GetBytes("combined-value");
        // Absolute in 3s, sliding every 2s — but we can't slide past 3s total.
        await _cache.SetAsync("combined", value, new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(3),
            SlidingExpiration = TimeSpan.FromSeconds(2)
        });

        // Access at 1.5s — refreshes, but capped by absolute (3s from start)
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        var refreshed = await _cache.GetAsync("combined");
        refreshed.Should().BeEquivalentTo(value);

        // Access at 2.5s from start — should still work (absolute is at 3s)
        await Task.Delay(TimeSpan.FromSeconds(1));
        var beforeAbsolute = await _cache.GetAsync("combined");
        beforeAbsolute.Should().BeEquivalentTo(value);

        // Wait past the absolute deadline (3s from start is now passed)
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        var afterAbsolute = await _cache.GetAsync("combined");
        afterAbsolute.Should().BeNull();
    }

    // ─────────────────────────────────────────────────────────────────────
    // Remove: Set then Remove then Get returns null
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Remove_ThenGet_ReturnsNull()
    {
        var value = Encoding.UTF8.GetBytes("to-remove");
        _cache.Set("remove-me", value, new DistributedCacheEntryOptions());

        _cache.Remove("remove-me");

        _cache.Get("remove-me").Should().BeNull();
    }

    [Fact]
    public async Task RemoveAsync_ThenGetAsync_ReturnsNull()
    {
        var value = Encoding.UTF8.GetBytes("to-remove-async");
        await _cache.SetAsync("remove-async", value, new DistributedCacheEntryOptions());

        await _cache.RemoveAsync("remove-async");

        var result = await _cache.GetAsync("remove-async");
        result.Should().BeNull();
    }

    // ─────────────────────────────────────────────────────────────────────
    // Refresh: refreshes TTL without returning value
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Refresh_ExtendsSlidingTtl()
    {
        var value = Encoding.UTF8.GetBytes("refresh-target");
        await _cache.SetAsync("refresh-key", value, new DistributedCacheEntryOptions
        {
            SlidingExpiration = TimeSpan.FromSeconds(2)
        });

        // Wait 1.5s, then refresh
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        await _cache.RefreshAsync("refresh-key");

        // Wait another 1.5s — should still be alive because refresh extended the TTL
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        var result = await _cache.GetAsync("refresh-key");
        result.Should().BeEquivalentTo(value);
    }

    [Fact]
    public void Refresh_NonExistentKey_DoesNotThrow()
    {
        // Refresh on a missing key should be a no-op.
        var act = () => _cache.Refresh("no-such-key");
        act.Should().NotThrow();
    }

    // ─────────────────────────────────────────────────────────────────────
    // Key prefix: stored keys carry prefix in Garnet
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void KeyPrefix_StoredWithPrefix_InGarnet()
    {
        var value = Encoding.UTF8.GetBytes("prefixed");
        _cache.Set("my-key", value, new DistributedCacheEntryOptions());

        var db = _redis.GetDatabase();

        // The actual Redis key should be hw:cache:my-key
        var rawWithPrefix = db.StringGet("hw:cache:my-key");
        rawWithPrefix.IsNullOrEmpty.Should().BeFalse("key should exist with prefix");

        // Without prefix, should not find it
        var rawWithoutPrefix = db.StringGet("my-key");
        rawWithoutPrefix.IsNullOrEmpty.Should().BeTrue("key should NOT exist without prefix");
    }

    [Fact]
    public void CustomPrefix_IsApplied()
    {
        using var cache = new HighwayCache(_redis, new HighwayCacheOptions { KeyPrefix = "app:" });
        var value = Encoding.UTF8.GetBytes("custom-prefixed");
        cache.Set("data", value, new DistributedCacheEntryOptions());

        var db = _redis.GetDatabase();
        var raw = db.StringGet("app:data");
        raw.IsNullOrEmpty.Should().BeFalse("key should exist with custom prefix");
    }

    // ─────────────────────────────────────────────────────────────────────
    // Isolation: cache keys cannot collide with hw:svc:* keys
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Isolation_CacheCannotReadHighwayInternalKey()
    {
        var db = _redis.GetDatabase();
        // Write directly to an internal Highway key
        db.StringSet("hw:svc:some-service", "internal-data");

        // The cache, looking up "some-service", should NOT find it
        // because the cache prefixes with "hw:cache:" not "hw:svc:"
        var result = _cache.Get("some-service");
        result.Should().BeNull();
    }

    [Fact]
    public void Isolation_CacheWriteDoesNotTouchInternalKeys()
    {
        var db = _redis.GetDatabase();
        // Write via cache
        _cache.Set("svc:test", Encoding.UTF8.GetBytes("from-cache"), new DistributedCacheEntryOptions());

        // The key stored should be hw:cache:svc:test, NOT hw:svc:test
        var cacheKey = db.StringGet("hw:cache:svc:test");
        cacheKey.IsNullOrEmpty.Should().BeFalse();

        var internalKey = db.StringGet("hw:svc:test");
        internalKey.IsNullOrEmpty.Should().BeTrue("cache write should not touch internal namespace");
    }

    // ─────────────────────────────────────────────────────────────────────
    // Edge cases
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Set_EmptyByteArray_RoundTrips()
    {
        var value = Array.Empty<byte>();
        _cache.Set("empty", value, new DistributedCacheEntryOptions());

        var result = _cache.Get("empty");
        result.Should().BeEquivalentTo(value);
    }

    [Fact]
    public void Set_LargeValue_RoundTrips()
    {
        var value = new byte[8192];
        Random.Shared.NextBytes(value);
        _cache.Set("large", value, new DistributedCacheEntryOptions());

        var result = _cache.Get("large");
        result.Should().BeEquivalentTo(value);
    }

    [Fact]
    public void KeyWithPrefixSeparator_DoesNotCauseAmbiguity()
    {
        // A user key that contains the prefix separator itself
        var value = Encoding.UTF8.GetBytes("tricky");
        _cache.Set("hw:cache:nested", value, new DistributedCacheEntryOptions());

        // Should be stored at hw:cache:hw:cache:nested — no parsing confusion
        var result = _cache.Get("hw:cache:nested");
        result.Should().BeEquivalentTo(value);

        var db = _redis.GetDatabase();
        var raw = db.StringGet("hw:cache:hw:cache:nested");
        raw.IsNullOrEmpty.Should().BeFalse();
    }
}
