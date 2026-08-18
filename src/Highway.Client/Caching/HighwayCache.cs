using System.Buffers;
using System.Buffers.Binary;
using Highway.Client.Engine;
using Microsoft.Extensions.Caching.Distributed;
using StackExchange.Redis;

namespace Highway.Client.Caching;

/// <summary>
/// <see cref="IDistributedCache"/> and <see cref="IBufferDistributedCache"/> implementation
/// backed by Garnet/Redis string commands over Highway's existing SE.Redis connection.
/// </summary>
public sealed class HighwayCache : IDistributedCache, IBufferDistributedCache, IDisposable
{
    /// <summary>
    /// The exact Redis string/key commands issued by <see cref="HighwayCache"/>.
    /// Used by ACL allowlist derivation and validation (feature 034).
    /// </summary>
    public static readonly string[] SupportedCommands = ["GET", "SET", "DEL", "EXPIRE"];

    // Header layout (stored when sliding expiration is involved):
    // [4 bytes magic "HWCH"][1 byte version][8 bytes absoluteDeadline UTC ticks (0 = none)][2 bytes slidingSeconds (0 = none)]
    private const uint HeaderMagic = 0x48435748; // "HWCH"
    private const int HeaderSize = 15;
    private const byte HeaderVersion = 1;

    private readonly HighwayConnectionSource? _connectionSource;
    private readonly IConnectionMultiplexer? _directConnection;
    private readonly string _prefix;

    public HighwayCache(HighwayConnectionSource connectionSource, HighwayCacheOptions options)
    {
        _connectionSource = connectionSource ?? throw new ArgumentNullException(nameof(connectionSource));
        ArgumentNullException.ThrowIfNull(options);
        _prefix = options.KeyPrefix;
    }

    public HighwayCache(IConnectionMultiplexer connection, HighwayCacheOptions options)
    {
        _directConnection = connection ?? throw new ArgumentNullException(nameof(connection));
        ArgumentNullException.ThrowIfNull(options);
        _prefix = options.KeyPrefix;
    }

    private IDatabase Db => _directConnection?.GetDatabase() ?? _connectionSource!.GetDatabase();

    // ─────────────────────────────────────────────────────────────────────
    // IDistributedCache — Get
    // ─────────────────────────────────────────────────────────────────────

    public byte[]? Get(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var redisKey = PrefixKey(key);

        RedisValue raw = Db.StringGet(redisKey);
        if (raw.IsNull)
            return null;

        return ProcessGetResult(redisKey, (byte[])raw!, isRefreshOnly: false);
    }

    public async Task<byte[]?> GetAsync(string key, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        var redisKey = PrefixKey(key);

        RedisValue raw = await Db.StringGetAsync(redisKey).ConfigureAwait(false);
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

        Db.StringSet(redisKey, payload, expiry);
    }

    public async Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(options);

        var redisKey = PrefixKey(key);
        var (payload, expiry) = BuildPayloadAndExpiry(value, options);

        await Db.StringSetAsync(redisKey, payload, expiry).ConfigureAwait(false);
    }

    // ─────────────────────────────────────────────────────────────────────
    // IDistributedCache — Remove
    // ─────────────────────────────────────────────────────────────────────

    public void Remove(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        Db.KeyDelete(PrefixKey(key));
    }

    public async Task RemoveAsync(string key, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        await Db.KeyDeleteAsync(PrefixKey(key)).ConfigureAwait(false);
    }

    // ─────────────────────────────────────────────────────────────────────
    // IDistributedCache — Refresh
    // ─────────────────────────────────────────────────────────────────────

    public void Refresh(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var redisKey = PrefixKey(key);

        RedisValue raw = Db.StringGet(redisKey);
        if (raw.IsNull)
            return;

        ProcessGetResult(redisKey, (byte[])raw!, isRefreshOnly: true);
    }

    public async Task RefreshAsync(string key, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        var redisKey = PrefixKey(key);

        RedisValue raw = await Db.StringGetAsync(redisKey).ConfigureAwait(false);
        if (raw.IsNull)
            return;

        await ProcessGetResultAsync(redisKey, (byte[])raw!, isRefreshOnly: true).ConfigureAwait(false);
    }

    // ─────────────────────────────────────────────────────────────────────
    // IBufferDistributedCache — Get / Set via ReadOnlySequence / IBufferWriter
    // ─────────────────────────────────────────────────────────────────────

    public bool TryGet(string key, IBufferWriter<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(destination);
        var redisKey = PrefixKey(key);

        RedisValue raw = Db.StringGet(redisKey);
        if (raw.IsNull)
            return false;

        return ProcessGetToBuffer(redisKey, (byte[])raw!, destination);
    }

    public async ValueTask<bool> TryGetAsync(string key, IBufferWriter<byte> destination, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(destination);
        var redisKey = PrefixKey(key);

        RedisValue raw = await Db.StringGetAsync(redisKey).ConfigureAwait(false);
        if (raw.IsNull)
            return false;

        return await ProcessGetToBufferAsync(redisKey, (byte[])raw!, destination).ConfigureAwait(false);
    }

    public void Set(string key, ReadOnlySequence<byte> value, DistributedCacheEntryOptions options)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(options);

        // Convert ReadOnlySequence to byte[] for storage
        var bytes = value.ToArray();
        Set(key, bytes, options);
    }

    public async ValueTask SetAsync(string key, ReadOnlySequence<byte> value, DistributedCacheEntryOptions options, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(options);

        var bytes = value.ToArray();
        await SetAsync(key, bytes, options, token).ConfigureAwait(false);
    }

    // ─────────────────────────────────────────────────────────────────────
    // IDisposable
    // ─────────────────────────────────────────────────────────────────────

    public void Dispose()
    {
        // ConnectionMultiplexer lifecycle is managed externally (by DI container).
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
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0), HeaderMagic);
            payload[4] = HeaderVersion;
            BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(5), absoluteTicks);
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(13), slidingSeconds);
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
        if (raw.Length >= HeaderSize &&
            BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(0)) == HeaderMagic &&
            raw[4] == HeaderVersion)
        {
            var absoluteTicks = BinaryPrimitives.ReadInt64LittleEndian(raw.AsSpan(5));
            var slidingSeconds = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(13));

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
                        Db.KeyDelete(redisKey);
                        return null;
                    }

                    newTtl = timeToAbsolute < sliding ? timeToAbsolute : sliding;
                }
                else
                {
                    // Sliding only, no absolute cap.
                    newTtl = sliding;
                }

                Db.KeyExpire(redisKey, newTtl);

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
        if (raw.Length >= HeaderSize &&
            BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(0)) == HeaderMagic &&
            raw[4] == HeaderVersion)
        {
            var absoluteTicks = BinaryPrimitives.ReadInt64LittleEndian(raw.AsSpan(5));
            var slidingSeconds = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(13));

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
                        await Db.KeyDeleteAsync(redisKey).ConfigureAwait(false);
                        return null;
                    }

                    newTtl = timeToAbsolute < sliding ? timeToAbsolute : sliding;
                }
                else
                {
                    newTtl = sliding;
                }

                await Db.KeyExpireAsync(redisKey, newTtl).ConfigureAwait(false);

                return isRefreshOnly ? null : raw.AsSpan(HeaderSize).ToArray();
            }
        }

        return isRefreshOnly ? null : raw;
    }

    /// <summary>
    /// Processes a Get result and writes the payload to an <see cref="IBufferWriter{T}"/>.
    /// Returns true if the entry was found and written; false if logically expired or missing.
    /// </summary>
    private bool ProcessGetToBuffer(string redisKey, byte[] raw, IBufferWriter<byte> destination)
    {
        if (raw.Length >= HeaderSize &&
            BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(0)) == HeaderMagic &&
            raw[4] == HeaderVersion)
        {
            var absoluteTicks = BinaryPrimitives.ReadInt64LittleEndian(raw.AsSpan(5));
            var slidingSeconds = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(13));

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
                        Db.KeyDelete(redisKey);
                        return false;
                    }

                    newTtl = timeToAbsolute < sliding ? timeToAbsolute : sliding;
                }
                else
                {
                    newTtl = sliding;
                }

                Db.KeyExpire(redisKey, newTtl);

                var payload = raw.AsSpan(HeaderSize);
                var span = destination.GetSpan(payload.Length);
                payload.CopyTo(span);
                destination.Advance(payload.Length);
                return true;
            }
        }

        // No sliding header — write the raw value directly.
        var data = raw.AsSpan();
        var target = destination.GetSpan(data.Length);
        data.CopyTo(target);
        destination.Advance(data.Length);
        return true;
    }

    /// <summary>
    /// Async version of <see cref="ProcessGetToBuffer"/>.
    /// </summary>
    private async ValueTask<bool> ProcessGetToBufferAsync(string redisKey, byte[] raw, IBufferWriter<byte> destination)
    {
        if (raw.Length >= HeaderSize &&
            BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(0)) == HeaderMagic &&
            raw[4] == HeaderVersion)
        {
            var absoluteTicks = BinaryPrimitives.ReadInt64LittleEndian(raw.AsSpan(5));
            var slidingSeconds = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(13));

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
                        await Db.KeyDeleteAsync(redisKey).ConfigureAwait(false);
                        return false;
                    }

                    newTtl = timeToAbsolute < sliding ? timeToAbsolute : sliding;
                }
                else
                {
                    newTtl = sliding;
                }

                await Db.KeyExpireAsync(redisKey, newTtl).ConfigureAwait(false);

                var payload = raw.AsSpan(HeaderSize);
                var span = destination.GetSpan(payload.Length);
                payload.CopyTo(span);
                destination.Advance(payload.Length);
                return true;
            }
        }

        // No sliding header — write the raw value directly.
        var data = raw.AsSpan();
        var target = destination.GetSpan(data.Length);
        data.CopyTo(target);
        destination.Advance(data.Length);
        return true;
    }

    /// <summary>
    /// Builds stored payload and TTL from a <see cref="ReadOnlySequence{T}"/> value.
    /// Same logic as the byte[] overload but works with sequences to avoid allocation.
    /// </summary>
    private static (byte[] Payload, TimeSpan? Expiry) BuildPayloadAndExpiry(
        ReadOnlySequence<byte> value, DistributedCacheEntryOptions options)
    {
        var absoluteDeadline = ComputeAbsoluteDeadline(options);
        var slidingSeconds = options.SlidingExpiration.HasValue
            ? (ushort)Math.Min((int)options.SlidingExpiration.Value.TotalSeconds, ushort.MaxValue)
            : (ushort)0;

        bool hasSliding = slidingSeconds > 0;
        int valueLength = (int)value.Length;

        byte[] payload;
        TimeSpan? expiry;

        if (hasSliding)
        {
            long absoluteTicks = absoluteDeadline?.UtcTicks ?? 0;

            payload = new byte[HeaderSize + valueLength];
            payload[0] = HeaderVersion;
            BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(1), absoluteTicks);
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(9), slidingSeconds);
            value.CopyTo(payload.AsSpan(HeaderSize));

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
            payload = new byte[valueLength];
            value.CopyTo(payload);
            var timeToAbsolute = absoluteDeadline.Value - DateTimeOffset.UtcNow;
            expiry = timeToAbsolute > TimeSpan.Zero ? timeToAbsolute : TimeSpan.FromMilliseconds(1);
        }
        else
        {
            payload = new byte[valueLength];
            value.CopyTo(payload);
            expiry = null;
        }

        return (payload, expiry);
    }
}
