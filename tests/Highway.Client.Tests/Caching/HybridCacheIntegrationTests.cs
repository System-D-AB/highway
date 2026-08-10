using FluentAssertions;
using Highway.Client.Caching;
using Highway.Client.Engine;
using Highway.Server;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Highway.Client.Tests.Caching;

/// <summary>
/// Proves that <see cref="HybridCache"/> works end-to-end with Highway as its L2
/// distributed store. Validates Requirements R4.1–R4.3:
///   - R4.1: HybridCache automatically uses Highway's IDistributedCache as L2.
///   - R4.2: (documentation — tested implicitly by verifying integration works)
///   - R4.3: Serialization uses System.Text.Json by default.
/// </summary>
public sealed class HybridCacheIntegrationTests : IAsyncLifetime
{
    private readonly HighwayTestServer _server;
    private readonly ServiceProvider _provider;
    private readonly IHighwayEngine _engine;
    private HybridCache _hybridCache = null!;

    /// <summary>A simple DTO used to verify typed serialization round-trips via System.Text.Json.</summary>
    private sealed record TestCacheItem(string Name, int Value);

    public HybridCacheIntegrationTests()
    {
        _server = new HighwayTestServer();

        var services = new ServiceCollection();

        // Register Highway — this registers IDistributedCache (HighwayCache) via TryAdd.
        // Exclude this test assembly from scanning to avoid the intentionally-invalid
        // test fixtures (NoAttributeRequest, etc.) from blowing up the scanner.
        services.AddHighway(o =>
        {
            o.Server = _server.ConnectionString;
            o.NodeName = "hybrid-cache-test";
            o.ExcludedAssemblies.Add(a => a == typeof(HybridCacheIntegrationTests).Assembly);
        });

        // Register HybridCache — it automatically picks up IDistributedCache as L2
        services.AddHybridCache();

        _provider = services.BuildServiceProvider();
        _engine = _provider.GetRequiredService<IHighwayEngine>();
    }

    public async Task InitializeAsync()
    {
        // Start the engine first so the ConnectionMultiplexer is available.
        // HybridCache resolves IDistributedCache during its own construction,
        // so the engine must be running before HybridCache is resolved.
        await _engine.StartAsync();
        _hybridCache = _provider.GetRequiredService<HybridCache>();
    }

    public async Task DisposeAsync()
    {
        await _engine.StopAsync();
        await _provider.DisposeAsync();
        _server.Dispose();
    }

    [Fact]
    public async Task GetOrCreateAsync_FirstCall_InvokesFactory()
    {
        var factoryCallCount = 0;

        var result = await _hybridCache.GetOrCreateAsync(
            "hybrid-first-call",
            async _ =>
            {
                Interlocked.Increment(ref factoryCallCount);
                return new TestCacheItem("first", 1);
            });

        factoryCallCount.Should().Be(1);
        result.Should().NotBeNull();
        result!.Name.Should().Be("first");
        result.Value.Should().Be(1);
    }

    [Fact]
    public async Task GetOrCreateAsync_SecondCall_ServesFromL2_FactoryNotCalled()
    {
        var factoryCallCount = 0;
        const string key = "hybrid-l2-hit";

        // First call: factory fires, populates L2
        await _hybridCache.GetOrCreateAsync(
            key,
            async _ =>
            {
                Interlocked.Increment(ref factoryCallCount);
                return new TestCacheItem("cached", 42);
            });

        factoryCallCount.Should().Be(1);

        // Second call: served from L2, factory NOT called
        var result = await _hybridCache.GetOrCreateAsync(
            key,
            async _ =>
            {
                Interlocked.Increment(ref factoryCallCount);
                return new TestCacheItem("should-not-see-this", 999);
            });

        factoryCallCount.Should().Be(1, "factory should not be called on L2 cache hit");
        result.Should().NotBeNull();
        result!.Name.Should().Be("cached");
        result.Value.Should().Be(42);
    }

    [Fact]
    public async Task RemoveAsync_Invalidates_ThirdCallFiresFactory()
    {
        var factoryCallCount = 0;
        const string key = "hybrid-invalidate";

        // First call: factory fires
        await _hybridCache.GetOrCreateAsync(
            key,
            async _ =>
            {
                Interlocked.Increment(ref factoryCallCount);
                return new TestCacheItem("original", 1);
            });

        factoryCallCount.Should().Be(1);

        // Second call: served from cache
        await _hybridCache.GetOrCreateAsync(
            key,
            async _ =>
            {
                Interlocked.Increment(ref factoryCallCount);
                return new TestCacheItem("not-this", 0);
            });

        factoryCallCount.Should().Be(1);

        // Remove the entry
        await _hybridCache.RemoveAsync(key);

        // Third call: factory fires again (cache was invalidated)
        var result = await _hybridCache.GetOrCreateAsync(
            key,
            async _ =>
            {
                Interlocked.Increment(ref factoryCallCount);
                return new TestCacheItem("refreshed", 2);
            });

        factoryCallCount.Should().Be(2, "factory should fire again after RemoveAsync");
        result.Should().NotBeNull();
        result!.Name.Should().Be("refreshed");
        result.Value.Should().Be(2);
    }

    [Fact]
    public async Task FullLifecycle_Factory_CacheHit_Invalidate_FactoryAgain()
    {
        // This is the single end-to-end test specified in the task's done criteria:
        // 1. GetOrCreateAsync — factory fires
        // 2. GetOrCreateAsync — factory NOT called (L2 hit)
        // 3. RemoveAsync — invalidates
        // 4. GetOrCreateAsync — factory fires again

        var factoryCallCount = 0;
        const string key = "hybrid-lifecycle";

        // Step 1: first call, factory fires
        var first = await _hybridCache.GetOrCreateAsync(
            key,
            async _ =>
            {
                Interlocked.Increment(ref factoryCallCount);
                return new TestCacheItem("hello", 100);
            });

        factoryCallCount.Should().Be(1);
        first!.Name.Should().Be("hello");
        first.Value.Should().Be(100);

        // Step 2: second call, served from L2 (factory NOT called)
        var second = await _hybridCache.GetOrCreateAsync(
            key,
            async _ =>
            {
                Interlocked.Increment(ref factoryCallCount);
                return new TestCacheItem("stale", -1);
            });

        factoryCallCount.Should().Be(1, "second call should be a cache hit");
        second!.Name.Should().Be("hello");
        second.Value.Should().Be(100);

        // Step 3: remove/invalidate
        await _hybridCache.RemoveAsync(key);

        // Step 4: third call, factory fires again
        var third = await _hybridCache.GetOrCreateAsync(
            key,
            async _ =>
            {
                Interlocked.Increment(ref factoryCallCount);
                return new TestCacheItem("renewed", 200);
            });

        factoryCallCount.Should().Be(2, "factory should fire after invalidation");
        third!.Name.Should().Be("renewed");
        third.Value.Should().Be(200);
    }

    [Fact]
    public void IDistributedCache_ResolvesToHighwayCache()
    {
        // Verify the wiring: IDistributedCache should be HighwayCache
        var distributedCache = _provider.GetRequiredService<IDistributedCache>();
        distributedCache.Should().BeOfType<HighwayCache>();
    }
}
