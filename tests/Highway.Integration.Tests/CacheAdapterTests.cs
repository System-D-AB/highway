using FluentAssertions;
using Highway.Client;
using Highway.Client.Caching;
using Highway.Server;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using Xunit;

namespace Highway.Integration.Tests;

/// <summary>
/// Feature 044 T6 (gate) — the client <see cref="IDistributedCache"/> adapter over an embedded
/// broker. Proves the round-trip (get/set/remove), that the adapter maps an expiration onto a
/// wire PX the broker honours/clamps, that <c>AddHighwayCache</c> registers it as the app's
/// <see cref="IDistributedCache"/>, and that a <see cref="HybridCache"/> built on it resolves
/// from L2 with no Highway-specific code.
/// </summary>
public class CacheAdapterTests
{
    private static readonly byte[] Payload = "the-cached-bytes"u8.ToArray();

    [Fact]
    public async Task IDistributedCache_GetSetRemove_RoundTrips()
    {
        using var server = new HighwayTestServer(o => o.Cache.Enabled = true);
        var mux = await ConnectionMultiplexer.ConnectAsync(server.ConnectionString);
        var cache = new HighwayCache(mux, new HighwayCacheOptions());

        (await cache.GetAsync("k")).Should().BeNull("a cold cache misses");

        await cache.SetAsync("k", Payload, new DistributedCacheEntryOptions());
        (await cache.GetAsync("k")).Should().Equal(Payload);

        await cache.RemoveAsync("k");
        (await cache.GetAsync("k")).Should().BeNull("removed → miss");
    }

    [Fact]
    public async Task Set_WithAbsoluteExpiration_LandsAsAWirePx()
    {
        using var server = new HighwayTestServer(o =>
        {
            o.Cache.Enabled = true;
            o.Cache.MaxTtl = TimeSpan.FromDays(7);
        });
        var mux = await ConnectionMultiplexer.ConnectAsync(server.ConnectionString);
        var cache = new HighwayCache(mux, new HighwayCacheOptions());

        await cache.SetAsync("k", Payload, new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10),
        });

        // The adapter prefixes with hw:cache:; read the raw TTL the broker stored.
        var ttl = await mux.GetDatabase().KeyTimeToLiveAsync("hw:cache:k");
        ttl.Should().NotBeNull();
        ttl!.Value.Should().BeGreaterThan(TimeSpan.FromMinutes(9))
            .And.BeLessThanOrEqualTo(TimeSpan.FromMinutes(10), "the adapter maps the expiration onto a PX");
    }

    [Fact]
    public async Task Set_WithAKeyPrefix_NamespacesUnderHwCache()
    {
        using var server = new HighwayTestServer(o => o.Cache.Enabled = true);
        var mux = await ConnectionMultiplexer.ConnectAsync(server.ConnectionString);
        var cache = new HighwayCache(mux, new HighwayCacheOptions { KeyPrefix = "orders:" });

        await cache.SetAsync("42", Payload, new DistributedCacheEntryOptions());

        ((byte[]?)await mux.GetDatabase().StringGetAsync("hw:cache:orders:42"))
            .Should().Equal(Payload, "the app prefix lands after the hw:cache: routing prefix");
    }

    [Fact]
    public async Task AddHighwayCache_RegistersItAsTheDistributedCache_AndRoundTrips()
    {
        using var server = new HighwayTestServer(o => o.Cache.Enabled = true);

        var services = new ServiceCollection();
        services.AddHighway(o =>
        {
            o.NodeName = "cache-di-test";
            o.Server = server.ConnectionString;
        });
        services.AddHighwayCache();

        await using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<IDistributedCache>();
        cache.Should().BeOfType<HighwayCache>();

        await cache.SetAsync("di-key", Payload, new DistributedCacheEntryOptions());
        (await cache.GetAsync("di-key")).Should().Equal(Payload);
    }

    [Fact]
    public async Task HybridCache_ResolvesFromTheHighwayL2()
    {
        using var server = new HighwayTestServer(o => o.Cache.Enabled = true);

        var services = new ServiceCollection();
        services.AddHighway(o =>
        {
            o.NodeName = "hybrid-l2-test";
            o.Server = server.ConnectionString;
        });
        services.AddHighwayCache();
        services.AddHybridCache();

        await using var provider = services.BuildServiceProvider();
        var hybrid = provider.GetRequiredService<HybridCache>();

        // Force both calls through L2 (no L1), so the second must resolve from the Highway cache.
        var l2Only = new HybridCacheEntryOptions
        {
            Flags = HybridCacheEntryFlags.DisableLocalCacheRead | HybridCacheEntryFlags.DisableLocalCacheWrite,
        };

        var factoryRuns = 0;
        var key = "hybrid:" + Guid.NewGuid().ToString("N");

        var first = await hybrid.GetOrCreateAsync(key,
            _ => { Interlocked.Increment(ref factoryRuns); return ValueTask.FromResult("from-source"); },
            l2Only);
        first.Should().Be("from-source");
        factoryRuns.Should().Be(1, "the first miss runs the factory and writes L2");

        var second = await hybrid.GetOrCreateAsync(key,
            _ => { Interlocked.Increment(ref factoryRuns); return ValueTask.FromResult("SHOULD-NOT-RUN"); },
            l2Only);
        second.Should().Be("from-source", "the second call resolves from the Highway L2");
        factoryRuns.Should().Be(1, "L2 hit means the factory does not run again");
    }
}
