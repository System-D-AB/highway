using System.Buffers.Binary;
using Highway.Client.Engine;
using Microsoft.Extensions.Caching.Distributed;
using StackExchange.Redis;

namespace Highway.Client.Caching;

/// <summary>
/// Feature 044 — an <see cref="IDistributedCache"/> backed by the broker's <b>broker-local,
/// never-replicated</b> cache store, reached over Highway's existing herd connection.
///
/// <para>Every operation is a plain RESP string command against a <c>hw:cache:</c>-prefixed key;
/// the server routes that prefix to a separate RocksDB (durable brokers) or an in-memory store
/// (ephemeral brokers) that the replication feeder never sees. Because the calls ride the shared
/// herd connection, a cache op issued mid-failover re-drives to the new master and is simply a
/// miss there — no special handling, no stale read (044 R3.3, design "cold-cache burst").</para>
///
/// <para>TTL policy lives on the server: a set with no expiry gets the broker's default TTL, and
/// any caller TTL is clamped to the broker's max. Absolute expirations map to a PX; sliding
/// expirations are refreshed on read by re-SETting the value with a fresh window (a client-side
/// re-SET, as the removed 026 adapter did — the server routes no <c>EXPIRE</c> to the cache).</para>
/// </summary>
public sealed class HighwayCache : IDistributedCache
{
    /// <summary>The prefix the server routes to its broker-local cache store (must match
    /// <c>CommandDispatcher.CacheKeyPrefix</c>). Not configurable — it is the wire contract.</summary>
    internal const string CachePrefix = "hw:cache:";

    // Sliding-expiration header, prepended to the stored value ONLY when a sliding window is set,
    // so a later Get can recompute the window and re-SET. Layout:
    //   [4B magic "HWCH" BE][1B version][8B absolute-deadline UTC ticks, 0 = none][2B sliding seconds]
    private const uint HeaderMagic = 0x48574348; // 'H''W''C''H'
    private const int HeaderSize = 15;
    private const byte HeaderVersion = 1;

    private readonly HighwayConnectionSource? _connectionSource;
    private readonly IConnectionMultiplexer? _directConnection;
    private readonly string _prefix;

    /// <summary>The production path: rides the shared herd connection (so failover re-drives).</summary>
    public HighwayCache(HighwayConnectionSource connectionSource, HighwayCacheOptions options)
    {
        _connectionSource = connectionSource ?? throw new ArgumentNullException(nameof(connectionSource));
        ArgumentNullException.ThrowIfNull(options);
        _prefix = CachePrefix + options.KeyPrefix;
    }

    /// <summary>A direct-multiplexer path, for tests and embedded scenarios.</summary>
    public HighwayCache(IConnectionMultiplexer connection, HighwayCacheOptions options)
    {
        _directConnection = connection ?? throw new ArgumentNullException(nameof(connection));
        ArgumentNullException.ThrowIfNull(options);
        _prefix = CachePrefix + options.KeyPrefix;
    }

    private IDatabase Db => _directConnection?.GetDatabase() ?? _connectionSource!.GetDatabase();

    private RedisKey Key(string key) => _prefix + key;

    // ── Get ───────────────────────────────────────────────────────────────

    public byte[]? Get(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var redisKey = Key(key);
        RedisValue raw = Db.StringGet(redisKey);
        return raw.IsNull ? null : ProcessGet(redisKey, (byte[])raw!, refreshOnly: false);
    }

    public async Task<byte[]?> GetAsync(string key, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        var redisKey = Key(key);
        RedisValue raw = await Db.StringGetAsync(redisKey).ConfigureAwait(false);
        return raw.IsNull ? null : await ProcessGetAsync(redisKey, (byte[])raw!, refreshOnly: false).ConfigureAwait(false);
    }

    // ── Set ───────────────────────────────────────────────────────────────

    public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(options);
        var (payload, expiry) = BuildPayloadAndExpiry(value, options);
        Db.StringSet(Key(key), payload, expiry);
    }

    public async Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(options);
        var (payload, expiry) = BuildPayloadAndExpiry(value, options);
        await Db.StringSetAsync(Key(key), payload, expiry).ConfigureAwait(false);
    }

    // ── Remove ────────────────────────────────────────────────────────────

    public void Remove(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        Db.KeyDelete(Key(key));
    }

    public async Task RemoveAsync(string key, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        await Db.KeyDeleteAsync(Key(key)).ConfigureAwait(false);
    }

    // ── Refresh (the interface's touch) ─────────────────────────────────────

    public void Refresh(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var redisKey = Key(key);
        RedisValue raw = Db.StringGet(redisKey);
        if (!raw.IsNull)
            ProcessGet(redisKey, (byte[])raw!, refreshOnly: true);
    }

    public async Task RefreshAsync(string key, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        var redisKey = Key(key);
        RedisValue raw = await Db.StringGetAsync(redisKey).ConfigureAwait(false);
        if (!raw.IsNull)
            await ProcessGetAsync(redisKey, (byte[])raw!, refreshOnly: true).ConfigureAwait(false);
    }

    // ── Payload framing ─────────────────────────────────────────────────────

    /// <summary>
    /// A sliding entry carries a header so a later read can recompute its window; an absolute-only
    /// or unbounded entry is stored raw. Returns the bytes to store and the initial TTL (a PX on
    /// the wire; null lets the broker apply its default TTL).
    /// </summary>
    private static (byte[] Payload, TimeSpan? Expiry) BuildPayloadAndExpiry(
        byte[] value, DistributedCacheEntryOptions options)
    {
        var absoluteDeadline = ComputeAbsoluteDeadline(options);
        var slidingSeconds = options.SlidingExpiration is { } s
            ? (ushort)Math.Clamp((long)s.TotalSeconds, 0, ushort.MaxValue)
            : (ushort)0;

        if (slidingSeconds > 0)
        {
            var payload = new byte[HeaderSize + value.Length];
            BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(0), HeaderMagic);
            payload[4] = HeaderVersion;
            BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(5), absoluteDeadline?.UtcTicks ?? 0);
            BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(13), slidingSeconds);
            value.CopyTo(payload.AsSpan(HeaderSize));
            return (payload, SlidingTtl(TimeSpan.FromSeconds(slidingSeconds), absoluteDeadline));
        }

        if (absoluteDeadline is { } deadline)
        {
            var timeToAbsolute = deadline - DateTimeOffset.UtcNow;
            return (value, timeToAbsolute > TimeSpan.Zero ? timeToAbsolute : TimeSpan.FromMilliseconds(1));
        }

        // No caller expiry — the broker applies its default TTL.
        return (value, null);
    }

    private static DateTimeOffset? ComputeAbsoluteDeadline(DistributedCacheEntryOptions options)
    {
        if (options.AbsoluteExpiration is { } abs)
            return abs.ToUniversalTime();
        if (options.AbsoluteExpirationRelativeToNow is { } rel)
            return DateTimeOffset.UtcNow + rel;
        return null;
    }

    private static TimeSpan SlidingTtl(TimeSpan sliding, DateTimeOffset? absoluteDeadline)
    {
        if (absoluteDeadline is not { } deadline)
            return sliding;
        var timeToAbsolute = deadline - DateTimeOffset.UtcNow;
        var ttl = timeToAbsolute < sliding ? timeToAbsolute : sliding;
        return ttl > TimeSpan.Zero ? ttl : TimeSpan.FromMilliseconds(1);
    }

    /// <summary>
    /// Interprets a read value. A sliding entry re-SETs itself with a fresh window (or is deleted
    /// past its absolute cap) and returns the unwrapped payload; a raw entry is returned as-is.
    /// <paramref name="refreshOnly"/> performs the slide but returns nothing (the touch).
    /// </summary>
    private byte[]? ProcessGet(RedisKey redisKey, byte[] raw, bool refreshOnly)
    {
        if (TryReadSlidingHeader(raw, out var sliding, out var absoluteDeadline))
        {
            if (absoluteDeadline is { } deadline && deadline <= DateTimeOffset.UtcNow)
            {
                Db.KeyDelete(redisKey);
                return null;
            }
            // Re-SET the whole payload with a fresh window — the server routes no EXPIRE to the cache.
            Db.StringSet(redisKey, raw, SlidingTtl(sliding, absoluteDeadline));
            return refreshOnly ? null : raw.AsSpan(HeaderSize).ToArray();
        }

        return refreshOnly ? null : raw;
    }

    private async Task<byte[]?> ProcessGetAsync(RedisKey redisKey, byte[] raw, bool refreshOnly)
    {
        if (TryReadSlidingHeader(raw, out var sliding, out var absoluteDeadline))
        {
            if (absoluteDeadline is { } deadline && deadline <= DateTimeOffset.UtcNow)
            {
                await Db.KeyDeleteAsync(redisKey).ConfigureAwait(false);
                return null;
            }
            await Db.StringSetAsync(redisKey, raw, SlidingTtl(sliding, absoluteDeadline)).ConfigureAwait(false);
            return refreshOnly ? null : raw.AsSpan(HeaderSize).ToArray();
        }

        return refreshOnly ? null : raw;
    }

    private static bool TryReadSlidingHeader(byte[] raw, out TimeSpan sliding, out DateTimeOffset? absoluteDeadline)
    {
        sliding = default;
        absoluteDeadline = null;
        if (raw.Length < HeaderSize ||
            BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(0)) != HeaderMagic ||
            raw[4] != HeaderVersion)
            return false;

        var slidingSeconds = BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(13));
        if (slidingSeconds == 0)
            return false;

        var absoluteTicks = BinaryPrimitives.ReadInt64BigEndian(raw.AsSpan(5));
        sliding = TimeSpan.FromSeconds(slidingSeconds);
        absoluteDeadline = absoluteTicks > 0 ? new DateTimeOffset(absoluteTicks, TimeSpan.Zero) : null;
        return true;
    }
}
