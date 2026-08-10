using System.Buffers.Binary;
using Microsoft.Extensions.Caching.Distributed;
using StackExchange.Redis;

namespace Highway.Client.Caching;

/// <summary>
/// <see cref="IDistributedCache"/> implementation backed by Garnet/Redis string commands
/// over Highway's existing SE.Redis connection.
/// </summary>
public sealed class HighwayCache : IDistributedCache, IDisposable
{
    // Header layout (stored when sliding expiration is involved):
    // [1 byte version][8 bytes absoluteDeadline UTC ticks (0 = none)][2 bytes slidingSeconds (0 = none)]
    private const int HeaderSize = 11;
    private const byte HeaderVersion = 1;

    private readonly IDatabase _db;
    private readonly string _prefix;

    internal HighwayCache(IConnectionMultiplexer connection, HighwayCacheOptions options)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(options);

        _db = connection.GetDatabase();
        _prefix = options.KeyPrefix;
    }

    // ─────────────────────────────────────────────────────────────────────
    // IDistributedCache — Get
    // ─────────────────────────────────────────────────────────────────────

    public byte[]? Get(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var redisKey = PrefixKey(key);

        RedisValue raw = _db.StringGet(redisKey);
        if (raw.IsNull)
            return null;

        return ProcessGetResult(redisKey, (byte[])raw!, isRefreshOnly: false);
    }

    public async Task<byte[]?> GetAsync(string key, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        var redisKey = PrefixKey(key);

        RedisValue raw = await _db.StringGetAsync(redisKey).ConfigureAwait(false);
        if (raw.IsNull)
            return null;

        return await ProcessGetResultAsync(redisKey, (byte[])raw!, isRefreshOnly: false).ConfigureAwait(false);
    }

    // ─────────────────────────────────────────────────────────────────────
    // IDistributedCache — Set
    // ─────────────────────────────────────────────────────────────────────

    public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(options);

        var redisKey = PrefixKey(key);
        var (payload, expiry) = BuildPayloadAndExpiry(value, options);

        _db.StringSet(redisKey, payload, expiry);
    }

    public async Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(options);

        var redisKey = PrefixKey(key);
        var (payload, expiry) = BuildPayloadAndExpiry(value, options);

        await _db.StringSetAsync(redisKey, payload, expiry).ConfigureAwait(false);
    }

    // ─────────────────────────────────────────────────────────────────────
    // IDistributedCache — Remove
    // ─────────────────────────────────────────────────────────────────────

    public void Remove(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        _db.KeyDelete(PrefixKey(key));
    }

    public async Task RemoveAsync(string key, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        await _db.KeyDeleteAsync(PrefixKey(key)).ConfigureAwait(false);
    }

    // ─────────────────────────────────────────────────────────────────────
    // IDistributedCache — Refresh
    // ─────────────────────────────────────────────────────────────────────

    public void Refresh(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var redisKey = PrefixKey(key);

        RedisValue raw = _db.StringGet(redisKey);
        if (raw.IsNull)
            return;

        ProcessGetResult(redisKey, (byte[])raw!, isRefreshOnly: true);
    }

    public async Task RefreshAsync(string key, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        var redisKey = PrefixKey(key);

        RedisValue raw = await _db.StringGetAsync(redisKey).ConfigureAwait(false);
        if (raw.IsNull)
            return;

        await ProcessGetResultAsync(redisKey, (byte[])raw!, isRefreshOnly: true).ConfigureAwait(false);
    }

    // ─────────────────────────────────────────────────────────────────────
    // IDisposable
    // ─────────────────────────────────────────────────────────────────────

    public void Dispose()
    {
        // The cache does not own the connection — nothing to dispose.
    }

    // ─────────────────────────────────────────────────────────────────────
    // Internal helpers
    // ─────────────────────────────────────────────────────────────────────

    private string PrefixKey(string key) => _prefix + key;

    /// <summary>
    /// Builds the stored payload and the initial TTL for a Set operation.
    /// When sliding expiration is involved, a header is prepended so that Get/Refresh
    /// can recompute the correct TTL on each access.
    /// When only absolute expiration is set, no header — just raw value + TTL.
    /// </summary>
    private static (byte[] Payload, TimeSpan? Expiry) BuildPayloadAndExpiry(
        byte[] value, DistributedCacheEntryOptions options)
    {
        var absoluteDeadline = ComputeAbsoluteDeadline(options);
        var slidingSeconds = options.SlidingExpiration.HasValue
            ? (ushort)Math.Min((int)options.SlidingExpiration.Value.TotalSeconds, ushort.MaxValue)
            : (ushort)0;

        bool hasSliding = slidingSeconds > 0;

        byte[] payload;
        TimeSpan? expiry;

        if (hasSliding)
        {
            // Sliding is involved (with or without absolute) — store header.
            long absoluteTicks = absoluteDeadline?.UtcTicks ?? 0;

            payload = new byte[HeaderSize + value.Length];
            payload[0] = HeaderVersion;
            BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(1), absoluteTicks);
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(9), slidingSeconds);
            value.CopyTo(payload.AsSpan(HeaderSize));

            // Initial TTL = sliding, capped by absolute if present.
            var sliding = TimeSpan.FromSeconds(slidingSeconds);
            if (absoluteDeadline.HasValue)
            {
                var timeToAbsolute = absoluteDeadline.Value - DateTimeOffset.UtcNow;
                expiry = timeToAbsolute < sliding ? timeToAbsolute : sliding;
                if (expiry <= TimeSpan.Zero)
                    expiry = TimeSpan.FromMilliseconds(1);
            }
            else
            {
                expiry = sliding;
            }
        }
        else if (absoluteDeadline.HasValue)
        {
            // Absolute only: no header, just raw value with TTL.
            payload = value;
            var timeToAbsolute = absoluteDeadline.Value - DateTimeOffset.UtcNow;
            expiry = timeToAbsolute > TimeSpan.Zero ? timeToAbsolute : TimeSpan.FromMilliseconds(1);
        }
        else
        {
            // No expiration at all.
            payload = value;
            expiry = null;
        }

        return (payload, expiry);
    }

    /// <summary>
    /// Computes the absolute deadline as a UTC DateTimeOffset.
    /// </summary>
    private static DateTimeOffset? ComputeAbsoluteDeadline(DistributedCacheEntryOptions options)
    {
        if (options.AbsoluteExpiration.HasValue)
            return options.AbsoluteExpiration.Value.ToUniversalTime();

        if (options.AbsoluteExpirationRelativeToNow.HasValue)
            return DateTimeOffset.UtcNow + options.AbsoluteExpirationRelativeToNow.Value;

        return null;
    }

    /// <summary>
    /// Processes the raw value from Redis: handles header logic, refreshes
    /// sliding TTL, and returns the user payload (or null if logically expired).
    /// </summary>
    private byte[]? ProcessGetResult(string redisKey, byte[] raw, bool isRefreshOnly)
    {
        if (raw.Length >= HeaderSize && raw[0] == HeaderVersion)
        {
            var absoluteTicks = BinaryPrimitives.ReadInt64LittleEndian(raw.AsSpan(1));
            var slidingSeconds = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(9));

            if (slidingSeconds > 0)
            {
                // Entry has sliding expiration — refresh TTL.
                var sliding = TimeSpan.FromSeconds(slidingSeconds);
                TimeSpan newTtl;

                if (absoluteTicks > 0)
                {
                    var absoluteDeadline = new DateTimeOffset(absoluteTicks, TimeSpan.Zero);
                    var timeToAbsolute = absoluteDeadline - DateTimeOffset.UtcNow;

                    if (timeToAbsolute <= TimeSpan.Zero)
                    {
                        // Past absolute deadline — logically expired.
                        _db.KeyDelete(redisKey);
                        return null;
                    }

                    newTtl = timeToAbsolute < sliding ? timeToAbsolute : sliding;
                }
                else
                {
                    // Sliding only, no absolute cap.
                    newTtl = sliding;
                }

                _db.KeyExpire(redisKey, newTtl);

                return isRefreshOnly ? null : raw.AsSpan(HeaderSize).ToArray();
            }
        }

        // No sliding header — absolute-only or no-expiration entry.
        // No TTL refresh needed.
        return isRefreshOnly ? null : raw;
    }

    /// <summary>
    /// Async version of <see cref="ProcessGetResult"/>.
    /// </summary>
    private async Task<byte[]?> ProcessGetResultAsync(string redisKey, byte[] raw, bool isRefreshOnly)
    {
        if (raw.Length >= HeaderSize && raw[0] == HeaderVersion)
        {
            var absoluteTicks = BinaryPrimitives.ReadInt64LittleEndian(raw.AsSpan(1));
            var slidingSeconds = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(9));

            if (slidingSeconds > 0)
            {
                var sliding = TimeSpan.FromSeconds(slidingSeconds);
                TimeSpan newTtl;

                if (absoluteTicks > 0)
                {
                    var absoluteDeadline = new DateTimeOffset(absoluteTicks, TimeSpan.Zero);
                    var timeToAbsolute = absoluteDeadline - DateTimeOffset.UtcNow;

                    if (timeToAbsolute <= TimeSpan.Zero)
                    {
                        await _db.KeyDeleteAsync(redisKey).ConfigureAwait(false);
                        return null;
                    }

                    newTtl = timeToAbsolute < sliding ? timeToAbsolute : sliding;
                }
                else
                {
                    newTtl = sliding;
                }

                await _db.KeyExpireAsync(redisKey, newTtl).ConfigureAwait(false);

                return isRefreshOnly ? null : raw.AsSpan(HeaderSize).ToArray();
            }
        }

        return isRefreshOnly ? null : raw;
    }
}
