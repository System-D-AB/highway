using System.Reflection;
using FluentAssertions;
using Highway.Client.Caching;
using Highway.Client.Engine;
using Highway.Server;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Highway.Client.Tests.Caching;

/// <summary>
/// Tests that <see cref="ServiceCollectionExtensions.AddHighway"/> auto-registers
/// <see cref="HighwayCache"/> as <see cref="IDistributedCache"/> using TryAdd semantics
/// (feature 026, T3).
/// </summary>
public class AddHighwayCacheRegistrationTests : IAsyncLifetime
{
    private readonly HighwayTestServer _server = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        _server.Dispose();
        await Task.CompletedTask;
    }

    /// <summary>
    /// Excludes the test assembly (which has intentionally-invalid test fixtures) from
    /// scanning so that AddHighway doesn't blow up on NoAttributeRequest etc.
    /// </summary>
    private static void ExcludeTestAssembly(HighwayOptions o)
    {
        var testAssembly = typeof(AddHighwayCacheRegistrationTests).Assembly;
        o.ExcludedAssemblies.Add(a => a == testAssembly);
    }

    // ─────────────────────────────────────────────────────────────────────
    // AddHighway registers IDistributedCache as HighwayCache
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AddHighway_RegistersIDistributedCache_AsHighwayCache()
    {
        var services = new ServiceCollection();
        services.AddHighway(o =>
        {
            o.Server = _server.ConnectionString;
            o.NodeName = "cache-reg-test";
            ExcludeTestAssembly(o);
        });

        await using var sp = services.BuildServiceProvider();
        var engine = sp.GetRequiredService<IHighwayEngine>();
        await engine.StartAsync();

        try
        {
            var cache = sp.GetService<IDistributedCache>();
            cache.Should().NotBeNull();
            cache.Should().BeOfType<HighwayCache>();
        }
        finally
        {
            await engine.StopAsync();
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // TryAdd semantics: does not override existing IDistributedCache
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AddHighway_DoesNotOverride_ExistingIDistributedCache()
    {
        var services = new ServiceCollection();

        // Pre-register a different IDistributedCache
        var existingCache = new FakeDistributedCache();
        services.AddSingleton<IDistributedCache>(existingCache);

        services.AddHighway(o =>
        {
            o.Server = _server.ConnectionString;
            o.NodeName = "cache-tryaddtest";
            ExcludeTestAssembly(o);
        });

        await using var sp = services.BuildServiceProvider();
        var engine = sp.GetRequiredService<IHighwayEngine>();
        await engine.StartAsync();

        try
        {
            var resolved = sp.GetRequiredService<IDistributedCache>();
            resolved.Should().BeSameAs(existingCache,
                "TryAdd semantics must not override an existing IDistributedCache registration");
        }
        finally
        {
            await engine.StopAsync();
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // HighwayCache resolved from AddHighway is functional
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AddHighway_CacheIsOperational_RoundTrips()
    {
        var services = new ServiceCollection();
        services.AddHighway(o =>
        {
            o.Server = _server.ConnectionString;
            o.NodeName = "cache-functional-test";
            ExcludeTestAssembly(o);
        });

        await using var sp = services.BuildServiceProvider();
        var engine = sp.GetRequiredService<IHighwayEngine>();
        await engine.StartAsync();

        try
        {
            var cache = sp.GetRequiredService<IDistributedCache>();

            var value = "hello-from-addHighway"u8.ToArray();
            await cache.SetAsync("hw-reg-test-key", value, new DistributedCacheEntryOptions());

            var result = await cache.GetAsync("hw-reg-test-key");
            result.Should().BeEquivalentTo(value);
        }
        finally
        {
            await engine.StopAsync();
        }
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
