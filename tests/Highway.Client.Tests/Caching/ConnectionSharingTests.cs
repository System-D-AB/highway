using FluentAssertions;
using Highway.Client.Caching;
using Highway.Client.Engine;
using Highway.Server;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Highway.Client.Tests.Caching;

/// <summary>
/// Tests that when both <c>AddHighway</c> and <c>AddHighwayCache</c> are registered in
/// the same DI container, only a single <see cref="IDistributedCache"/> registration
/// exists — proving connection sharing via <c>TryAddSingleton</c> semantics
/// (feature 026, T5 — R3.4, R6.1).
/// </summary>
public class ConnectionSharingTests : IAsyncLifetime
{
    private readonly HighwayTestServer _server = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        _server.Dispose();
        await Task.CompletedTask;
    }

    /// <summary>
    /// Excludes the test assembly from scanning so AddHighway doesn't blow up on
    /// intentionally-invalid test fixtures.
    /// </summary>
    private static void ExcludeTestAssembly(HighwayOptions o)
    {
        var testAssembly = typeof(ConnectionSharingTests).Assembly;
        o.ExcludedAssemblies.Add(a => a == testAssembly);
    }

    // ─────────────────────────────────────────────────────────────────────
    // AddHighway first, then AddHighwayCache → one IDistributedCache
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AddHighwayFirst_ThenAddHighwayCache_SingleCacheInstance()
    {
        var services = new ServiceCollection();

        // Register AddHighway first — its TryAddSingleton<IDistributedCache> wins.
        services.AddHighway(o =>
        {
            o.Server = _server.ConnectionString;
            o.NodeName = "conn-share-hw-first";
            ExcludeTestAssembly(o);
        });

        // Register AddHighwayCache second — its TryAddSingleton is a no-op.
        services.AddHighwayCache(o => o.Server = _server.ConnectionString);

        await using var sp = services.BuildServiceProvider();
        var engine = sp.GetRequiredService<IHighwayEngine>();
        await engine.StartAsync();

        try
        {
            // Resolve IDistributedCache twice — must be the same singleton instance.
            var cache1 = sp.GetRequiredService<IDistributedCache>();
            var cache2 = sp.GetRequiredService<IDistributedCache>();

            cache1.Should().BeSameAs(cache2,
                "only one IDistributedCache registration should exist — TryAdd prevents duplicates");
            cache1.Should().BeOfType<HighwayCache>(
                "the winning registration should be HighwayCache");

            // Prove the cache is operational (the single connection works).
            var value = "shared-connection-test"u8.ToArray();
            await cache1.SetAsync("conn-share-key-1", value, new DistributedCacheEntryOptions());
            var result = await cache1.GetAsync("conn-share-key-1");
            result.Should().BeEquivalentTo(value);
        }
        finally
        {
            await engine.StopAsync();
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // AddHighwayCache first, then AddHighway → one IDistributedCache
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AddHighwayCacheFirst_ThenAddHighway_SingleCacheInstance()
    {
        var services = new ServiceCollection();

        // Register AddHighwayCache first — its TryAddSingleton<IDistributedCache> wins.
        services.AddHighwayCache(o => o.Server = _server.ConnectionString);

        // Register AddHighway second — its TryAddSingleton is a no-op.
        services.AddHighway(o =>
        {
            o.Server = _server.ConnectionString;
            o.NodeName = "conn-share-cache-first";
            ExcludeTestAssembly(o);
        });

        await using var sp = services.BuildServiceProvider();
        var engine = sp.GetRequiredService<IHighwayEngine>();
        await engine.StartAsync();

        try
        {
            // Resolve IDistributedCache twice — must be the same singleton instance.
            var cache1 = sp.GetRequiredService<IDistributedCache>();
            var cache2 = sp.GetRequiredService<IDistributedCache>();

            cache1.Should().BeSameAs(cache2,
                "only one IDistributedCache registration should exist — TryAdd prevents duplicates");
            cache1.Should().BeOfType<HighwayCache>(
                "the winning registration should be HighwayCache");

            // Prove the cache is operational (the single connection works).
            var value = "shared-connection-reverse"u8.ToArray();
            await cache1.SetAsync("conn-share-key-2", value, new DistributedCacheEntryOptions());
            var result = await cache1.GetAsync("conn-share-key-2");
            result.Should().BeEquivalentTo(value);
        }
        finally
        {
            await engine.StopAsync();
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Only one IDistributedCache service descriptor is registered
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void BothRegistered_OnlyOneServiceDescriptorForIDistributedCache()
    {
        var services = new ServiceCollection();

        services.AddHighway(o =>
        {
            o.Server = _server.ConnectionString;
            o.NodeName = "conn-share-descriptor";
            ExcludeTestAssembly(o);
        });

        services.AddHighwayCache(o => o.Server = _server.ConnectionString);

        var descriptors = services
            .Where(sd => sd.ServiceType == typeof(IDistributedCache))
            .ToList();

        descriptors.Should().HaveCount(1,
            "TryAddSingleton semantics mean only the first registration wins — " +
            "one connection, not two");
    }

    [Fact]
    public void BothRegisteredReversed_OnlyOneServiceDescriptorForIDistributedCache()
    {
        var services = new ServiceCollection();

        services.AddHighwayCache(o => o.Server = _server.ConnectionString);

        services.AddHighway(o =>
        {
            o.Server = _server.ConnectionString;
            o.NodeName = "conn-share-descriptor-rev";
            ExcludeTestAssembly(o);
        });

        var descriptors = services
            .Where(sd => sd.ServiceType == typeof(IDistributedCache))
            .ToList();

        descriptors.Should().HaveCount(1,
            "TryAddSingleton semantics mean only the first registration wins — " +
            "one connection, not two");
    }
}
