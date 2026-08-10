using System.Buffers;
using System.Text;
using FluentAssertions;
using Highway.Client.Caching;
using Highway.Server;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using Xunit;

namespace Highway.Client.Tests.Caching;

/// <summary>
/// Integration tests for <see cref="HighwayCache"/>'s <see cref="IBufferDistributedCache"/>
/// implementation. Verifies round-trip with buffer-based Set/TryGet, and confirms that DI
/// resolves the buffer interface for HybridCache compatibility.
/// </summary>
public class HighwayCacheBufferTests : IDisposable
{
    private readonly HighwayTestServer _server = new();
    private readonly ConnectionMultiplexer _redis;
    private readonly HighwayCache _cache;

    public HighwayCacheBufferTests()
    {
        _redis = ConnectionMultiplexer.Connect(_server.ConnectionString);
        _cache = new HighwayCache(_redis, new HighwayCacheOptions());
    }

    public void Dispose()
    {
        _cache.Dispose();
        _redis.Dispose();
        _server.Dispose();
        GC.SuppressFinalize(this);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Round-trip: SetAsync(ReadOnlySequence) then TryGetAsync(IBufferWriter)
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SetAsync_ReadOnlySequence_ThenTryGetAsync_RoundTrips()
    {
        var original = Encoding.UTF8.GetBytes("buffer-round-trip");
        var sequence = new ReadOnlySequence<byte>(original);

        IBufferDistributedCache bufferCache = _cache;
        await bufferCache.SetAsync("buf-rt", sequence, new DistributedCacheEntryOptions());

        var writer = new ArrayBufferWriter<byte>();
        var found = await bufferCache.TryGetAsync("buf-rt", writer);

        found.Should().BeTrue();
        writer.WrittenSpan.ToArray().Should().BeEquivalentTo(original);
    }

    [Fact]
    public async Task TryGetAsync_NonExistentKey_ReturnsFalse()
    {
        IBufferDistributedCache bufferCache = _cache;
        var writer = new ArrayBufferWriter<byte>();

        var found = await bufferCache.TryGetAsync("does-not-exist", writer);

        found.Should().BeFalse();
        writer.WrittenCount.Should().Be(0);
    }

    [Fact]
    public void Set_ReadOnlySequence_ThenTryGet_RoundTrips()
    {
        var original = Encoding.UTF8.GetBytes("buffer-sync-rt");
        var sequence = new ReadOnlySequence<byte>(original);

        IBufferDistributedCache bufferCache = _cache;
        bufferCache.Set("buf-sync-rt", sequence, new DistributedCacheEntryOptions());

        var writer = new ArrayBufferWriter<byte>();
        var found = bufferCache.TryGet("buf-sync-rt", writer);

        found.Should().BeTrue();
        writer.WrittenSpan.ToArray().Should().BeEquivalentTo(original);
    }

    [Fact]
    public void TryGet_NonExistentKey_ReturnsFalse()
    {
        IBufferDistributedCache bufferCache = _cache;
        var writer = new ArrayBufferWriter<byte>();

        var found = bufferCache.TryGet("not-here", writer);

        found.Should().BeFalse();
        writer.WrittenCount.Should().Be(0);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Cross-path: Set via byte[] API, retrieve via buffer API (and vice versa)
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SetByteArray_ThenTryGetAsync_ReturnsCorrectData()
    {
        var original = Encoding.UTF8.GetBytes("set-via-bytes");
        await _cache.SetAsync("cross-path-1", original, new DistributedCacheEntryOptions());

        IBufferDistributedCache bufferCache = _cache;
        var writer = new ArrayBufferWriter<byte>();
        var found = await bufferCache.TryGetAsync("cross-path-1", writer);

        found.Should().BeTrue();
        writer.WrittenSpan.ToArray().Should().BeEquivalentTo(original);
    }

    [Fact]
    public async Task SetReadOnlySequence_ThenGetAsync_ReturnsCorrectData()
    {
        var original = Encoding.UTF8.GetBytes("set-via-sequence");
        var sequence = new ReadOnlySequence<byte>(original);

        IBufferDistributedCache bufferCache = _cache;
        await bufferCache.SetAsync("cross-path-2", sequence, new DistributedCacheEntryOptions());

        var result = await _cache.GetAsync("cross-path-2");
        result.Should().BeEquivalentTo(original);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Multi-segment ReadOnlySequence round-trips correctly
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SetAsync_MultiSegmentSequence_RoundTrips()
    {
        // Create a multi-segment ReadOnlySequence
        var part1 = Encoding.UTF8.GetBytes("hello-");
        var part2 = Encoding.UTF8.GetBytes("world");
        var sequence = CreateMultiSegmentSequence(part1, part2);

        IBufferDistributedCache bufferCache = _cache;
        await bufferCache.SetAsync("multi-seg", sequence, new DistributedCacheEntryOptions());

        var writer = new ArrayBufferWriter<byte>();
        var found = await bufferCache.TryGetAsync("multi-seg", writer);

        found.Should().BeTrue();
        var expected = Encoding.UTF8.GetBytes("hello-world");
        writer.WrittenSpan.ToArray().Should().BeEquivalentTo(expected);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Empty value round-trips
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SetAsync_EmptySequence_RoundTrips()
    {
        var sequence = new ReadOnlySequence<byte>(Array.Empty<byte>());

        IBufferDistributedCache bufferCache = _cache;
        await bufferCache.SetAsync("buf-empty", sequence, new DistributedCacheEntryOptions());

        var writer = new ArrayBufferWriter<byte>();
        var found = await bufferCache.TryGetAsync("buf-empty", writer);

        found.Should().BeTrue();
        writer.WrittenCount.Should().Be(0);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Buffer path respects sliding expiration
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TryGetAsync_WithSlidingExpiration_RefreshesTtl()
    {
        var original = Encoding.UTF8.GetBytes("sliding-buf");
        var sequence = new ReadOnlySequence<byte>(original);

        IBufferDistributedCache bufferCache = _cache;
        await bufferCache.SetAsync("buf-slide", sequence, new DistributedCacheEntryOptions
        {
            SlidingExpiration = TimeSpan.FromSeconds(2)
        });

        // Access at 1.5s — should refresh
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        var writer = new ArrayBufferWriter<byte>();
        var found = await bufferCache.TryGetAsync("buf-slide", writer);
        found.Should().BeTrue();
        writer.WrittenSpan.ToArray().Should().BeEquivalentTo(original);

        // Access at 1.5s after last access — should still be alive
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        writer = new ArrayBufferWriter<byte>();
        found = await bufferCache.TryGetAsync("buf-slide", writer);
        found.Should().BeTrue();

        // Wait without accessing — should expire
        await Task.Delay(TimeSpan.FromSeconds(2.5));
        writer = new ArrayBufferWriter<byte>();
        found = await bufferCache.TryGetAsync("buf-slide", writer);
        found.Should().BeFalse();
    }

    // ─────────────────────────────────────────────────────────────────────
    // DI resolution: IBufferDistributedCache resolves to HighwayCache
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void DI_ResolvesIBufferDistributedCache_WhenRegisteredDirectly()
    {
        // Simulate what T3 will do: register HighwayCache as both interfaces.
        var services = new ServiceCollection();
        services.AddSingleton<IConnectionMultiplexer>(_redis);
        services.AddSingleton(new HighwayCacheOptions());
        services.AddSingleton<HighwayCache>(sp =>
            new HighwayCache(sp.GetRequiredService<IConnectionMultiplexer>(), sp.GetRequiredService<HighwayCacheOptions>()));
        services.AddSingleton<IDistributedCache>(sp => sp.GetRequiredService<HighwayCache>());
        services.AddSingleton<IBufferDistributedCache>(sp => sp.GetRequiredService<HighwayCache>());

        using var provider = services.BuildServiceProvider();

        var distributedCache = provider.GetService<IDistributedCache>();
        var bufferCache = provider.GetService<IBufferDistributedCache>();

        distributedCache.Should().NotBeNull();
        bufferCache.Should().NotBeNull();
        bufferCache.Should().BeSameAs(distributedCache, "both interfaces should resolve to the same HighwayCache instance");
        bufferCache.Should().BeOfType<HighwayCache>();
    }

    // ─────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────

    private static ReadOnlySequence<byte> CreateMultiSegmentSequence(byte[] first, byte[] second)
    {
        var secondSegment = new MemorySegment<byte>(second);
        var firstSegment = new MemorySegment<byte>(first, secondSegment);
        return new ReadOnlySequence<byte>(firstSegment, 0, secondSegment, second.Length);
    }

    /// <summary>
    /// A simple linked-list based ReadOnlySequenceSegment for building multi-segment sequences.
    /// </summary>
    private sealed class MemorySegment<T> : ReadOnlySequenceSegment<T>
    {
        public MemorySegment(ReadOnlyMemory<T> memory, MemorySegment<T>? next = null)
        {
            Memory = memory;
            if (next != null)
            {
                Next = next;
                next.RunningIndex = memory.Length;
            }
        }
    }
}
