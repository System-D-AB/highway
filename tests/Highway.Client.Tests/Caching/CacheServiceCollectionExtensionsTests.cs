using FluentAssertions;
using Highway.Client.Caching;
using Highway.Client.Engine;
using Highway.Server;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Highway.Client.Tests.Caching;

/// <summary>
/// Tests for <see cref="CacheServiceCollectionExtensions.AddHighwayCache"/> — the
/// standalone entry point that registers cache without the Highway engine.
/// </summary>
public class CacheServiceCollectionExtensionsTests : IDisposable
{
    private readonly HighwayTestServer _server = new();

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _server.Dispose();
    }

    // ─────────────────────────────────────────────────────────────────────
    // Registration: IDistributedCache resolves and works
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void AddHighwayCache_RegistersIDistributedCache()
    {
        var services = new ServiceCollection();
        services.AddHighwayCache(o => o.Server = _server.ConnectionString);

        using var sp = services.BuildServiceProvider();
        var cache = sp.GetService<IDistributedCache>();

        cache.Should().NotBeNull();
        cache.Should().BeOfType<HighwayCache>();
    }

    [Fact]
    public async Task AddHighwayCache_CacheOperationsWork_AgainstGarnet()
    {
        var services = new ServiceCollection();
        services.AddHighwayCache(o => o.Server = _server.ConnectionString);

        using var sp = services.BuildServiceProvider();
        var cache = sp.GetRequiredService<IDistributedCache>();

        var value = "standalone-cache-value"u8.ToArray();
        await cache.SetAsync("standalone-key", value, new DistributedCacheEntryOptions());

        var result = await cache.GetAsync("standalone-key");
        result.Should().BeEquivalentTo(value);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Fail-fast: missing Server throws InvalidOperationException
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void AddHighwayCache_MissingServer_ThrowsInvalidOperationException()
    {
        var services = new ServiceCollection();

        var act = () => services.AddHighwayCache(o => { /* Server not set */ });

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Server*required*");
    }

    [Fact]
    public void AddHighwayCache_EmptyServer_ThrowsInvalidOperationException()
    {
        var services = new ServiceCollection();

        var act = () => services.AddHighwayCache(o => o.Server = "   ");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Server*required*");
    }

    // ─────────────────────────────────────────────────────────────────────
    // No engine: HighwayEngine is NOT started
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void AddHighwayCache_DoesNotRegisterHighwayEngine()
    {
        var services = new ServiceCollection();
        services.AddHighwayCache(o => o.Server = _server.ConnectionString);

        using var sp = services.BuildServiceProvider();

        // No IHighwayEngine should be registered
        var engine = sp.GetService<IHighwayEngine>();
        engine.Should().BeNull("standalone cache mode must not start the Highway engine");
    }

    [Fact]
    public void AddHighwayCache_DoesNotRegisterHostedServices()
    {
        var services = new ServiceCollection();
        services.AddHighwayCache(o => o.Server = _server.ConnectionString);

        using var sp = services.BuildServiceProvider();

        // No IHostedService should be registered (no engine hosted service, no heartbeat)
        var hostedServices = sp.GetServices<IHostedService>();
        hostedServices.Should().BeEmpty("standalone cache mode must not register any hosted services");
    }

    // ─────────────────────────────────────────────────────────────────────
    // TryAdd semantics: does not override an existing registration
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void AddHighwayCache_DoesNotOverrideExistingRegistration()
    {
        var services = new ServiceCollection();

        // Pre-register a different IDistributedCache
        var existingCache = new FakeDistributedCache();
        services.AddSingleton<IDistributedCache>(existingCache);

        services.AddHighwayCache(o => o.Server = _server.ConnectionString);

        using var sp = services.BuildServiceProvider();
        var resolved = sp.GetRequiredService<IDistributedCache>();

        resolved.Should().BeSameAs(existingCache, "TryAdd must not override an existing registration");
    }

    // ─────────────────────────────────────────────────────────────────────
    // Custom key prefix works
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AddHighwayCache_CustomKeyPrefix_IsApplied()
    {
        var services = new ServiceCollection();
        services.AddHighwayCache(o =>
        {
            o.Server = _server.ConnectionString;
            o.KeyPrefix = "app:";
        });

        using var sp = services.BuildServiceProvider();
        var cache = sp.GetRequiredService<IDistributedCache>();

        var value = "custom-prefix"u8.ToArray();
        await cache.SetAsync("test", value, new DistributedCacheEntryOptions());

        var result = await cache.GetAsync("test");
        result.Should().BeEquivalentTo(value);
    }

    /// <summary>
    /// A trivial <see cref="IDistributedCache"/> used to verify TryAdd semantics.
    /// </summary>
    private sealed class FakeDistributedCache : IDistributedCache
    {
        public byte[]? Get(string key) => null;
        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => Task.FromResult<byte[]?>(null);
        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) { }
        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default) => Task.CompletedTask;
        public void Refresh(string key) { }
        public Task RefreshAsync(string key, CancellationToken token = default) => Task.CompletedTask;
        public void Remove(string key) { }
        public Task RemoveAsync(string key, CancellationToken token = default) => Task.CompletedTask;
    }
}
