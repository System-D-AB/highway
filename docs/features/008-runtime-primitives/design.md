# Design: Runtime Primitives

## Overview

Four features, one design: typed .NET wrappers over stock Garnet operations, flowing through the existing `HighwayConnection`. No new `HW.*` commands. No new server-side code. The runtime primitives are entirely client-side logic against standard RESP commands.

## Architecture

```
src/Highway.Abstractions/
├── IHighwayClient.cs               # EXTENDED — lock, counter, rate-limit methods
├── IDistributedLock.cs             # NEW — lock result interface
└── RateLimitResult.cs              # NEW — rate-limit result type

src/Highway.Client/
├── Runtime/
│   ├── HighwayDistributedCache.cs  # IDistributedCache implementation
│   ├── HighwayLock.cs              # IDistributedLock implementation
│   ├── LockReleaseScript.cs       # Lua script for safe release
│   ├── CounterOperations.cs       # INCR/DECRBY/GET wrappers
│   └── RateLimiter.cs             # Fixed-window rate limiter
├── HighwayClient.cs                # EXTENDED — delegates to Runtime/*
└── ServiceCollectionExtensions.cs  # EXTENDED — registers IDistributedCache
```

## Key Namespace

```
hw:rt:cache:{key}           — IDistributedCache entries
hw:rt:cache:slide:{key}     — Sliding-expiry metadata (original TTL seconds)
hw:rt:lock:{key}            — Lock token value
hw:rt:lock:seq:{key}        — Fencing token sequence (INCR)
hw:rt:seq:{key}             — Atomic counter values
hw:rt:rate:{key}:{windowId} — Rate-limit window counters
```

All runtime keys are under `hw:rt:` — isolated from messaging (`hw:svc:`, `hw:ch:`, `hw:rep:`, `hw:reg:`) and from flight recorder (`hw:fdr:`).

## Distributed Cache

### Implementation: `HighwayDistributedCache : IDistributedCache`

```csharp
public class HighwayDistributedCache : IDistributedCache
{
    private readonly HighwayConnection _conn;
    private readonly string _prefix;

    public byte[]? Get(string key)
        => GetAsync(key).GetAwaiter().GetResult();

    public async Task<byte[]?> GetAsync(string key, CancellationToken ct = default)
        => await _conn.GetBytesAsync($"{_prefix}{key}", ct);

    public async Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken ct = default)
    {
        var ttl = ComputeTtl(options);
        if (ttl.HasValue)
            await _conn.SetExAsync($"{_prefix}{key}", value, ttl.Value, ct);
        else
            await _conn.SetAsync($"{_prefix}{key}", value, ct);

        // Store sliding window metadata if needed
        if (options.SlidingExpiration.HasValue)
            await _conn.SetAsync($"{_prefix}slide:{key}",
                BitConverter.GetBytes(options.SlidingExpiration.Value.TotalSeconds), ct);
    }

    public async Task RemoveAsync(string key, CancellationToken ct = default)
    {
        await _conn.DeleteAsync($"{_prefix}{key}", ct);
        await _conn.DeleteAsync($"{_prefix}slide:{key}", ct);
    }

    public async Task RefreshAsync(string key, CancellationToken ct = default)
    {
        var slideBytes = await _conn.GetBytesAsync($"{_prefix}slide:{key}", ct);
        if (slideBytes is not null)
        {
            var seconds = BitConverter.ToDouble(slideBytes);
            await _conn.ExpireAsync($"{_prefix}{key}", TimeSpan.FromSeconds(seconds), ct);
        }
    }
}
```

**TTL computation:** `AbsoluteExpiration` → compute remaining time from now. `SlidingExpiration` → use that directly. Both set → minimum of the two.

### Registration

```csharp
// In ServiceCollectionExtensions.AddHighway:
if (options.RegisterDistributedCache)
    services.TryAddSingleton<IDistributedCache>(sp =>
        new HighwayDistributedCache(sp.GetRequiredService<HighwayConnection>(), options.CacheKeyPrefix));
```

`TryAddSingleton` ensures that if the application already registered its own `IDistributedCache` (e.g., for testing), Highway doesn't override it.

## Distributed Locking

### Interface (in Abstractions)

```csharp
public interface IDistributedLock : IAsyncDisposable
{
    bool Acquired { get; }
    string? Token { get; }
}
```

### Implementation

**Acquire:**
```
INCR hw:rt:lock:seq:{key}         → fencingToken (monotonic)
SET hw:rt:lock:{key} {fencingToken} NX EX {seconds}
  → OK: Acquired = true, Token = fencingToken
  → nil: Acquired = false (already held)
```

**Release (Lua script — atomic compare-and-delete):**
```lua
if redis.call("GET", KEYS[1]) == ARGV[1] then
    return redis.call("DEL", KEYS[1])
else
    return 0
end
```

This prevents releasing a lock held by someone else (e.g., if our lock expired and another process acquired it before we released).

**Retry overload:**
```csharp
public async Task<IDistributedLock> AcquireLockAsync(
    string key, TimeSpan expiry, TimeSpan retryInterval, TimeSpan maxWait, CancellationToken ct)
{
    var deadline = DateTime.UtcNow + maxWait;
    while (DateTime.UtcNow < deadline)
    {
        var result = await AcquireLockAsync(key, expiry, ct);
        if (result.Acquired) return result;
        await Task.Delay(retryInterval, ct);
    }
    return new HighwayLock(false, null, null, null); // not acquired
}
```

### Why Lua for release?

Garnet supports `EVAL` for Lua scripts. Without atomic compare-and-delete, the release sequence (`GET` → check → `DEL`) has a race window where another process could acquire between our GET and DEL. The Lua script makes it atomic.

## Atomic Counters

The simplest primitive — direct wrappers over `INCRBY` / `DECRBY` / `GET`:

```csharp
public async Task<long> IncrementAsync(string key, long amount = 1, CancellationToken ct = default)
    => await _conn.IncrByAsync($"{_prefix}{key}", amount, ct);

public async Task<long> DecrementAsync(string key, long amount = 1, CancellationToken ct = default)
    => await _conn.DecrByAsync($"{_prefix}{key}", amount, ct);

public async Task<long> GetCounterAsync(string key, CancellationToken ct = default)
{
    var val = await _conn.GetAsync($"{_prefix}{key}", ct);
    return val is null ? 0 : long.Parse(val);
}
```

`INCRBY` on a non-existent key initializes it to 0 then increments — first call returns `amount`. Atomic across all connections. Persistent with AOF.

## Rate Limiting (Fixed Window)

```csharp
public async Task<RateLimitResult> CheckRateLimitAsync(
    string key, int limit, TimeSpan window, CancellationToken ct = default)
{
    var windowId = GetWindowId(window);  // e.g., Unix minute for 1-min window
    var fullKey = $"{_prefix}{key}:{windowId}";

    var count = await _conn.IncrByAsync(fullKey, 1, ct);

    // Set expiry only on first increment (count == 1)
    if (count == 1)
        await _conn.ExpireAsync(fullKey, window + TimeSpan.FromSeconds(1), ct); // +1s grace

    if (count <= limit)
        return new RateLimitResult(true, limit - (int)count, TimeSpan.Zero);

    var windowEnd = GetWindowEnd(window, windowId);
    return new RateLimitResult(false, 0, windowEnd - DateTime.UtcNow);
}

private static string GetWindowId(TimeSpan window)
{
    var now = DateTimeOffset.UtcNow;
    var seconds = (long)window.TotalSeconds;
    return (now.ToUnixTimeSeconds() / seconds).ToString();
}
```

**Why fixed window over sliding:** One INCR + one EXPIRE = two commands. Sliding window needs a sorted set + range query = more complex, more round trips. Fixed window is 90% as accurate for 10% of the complexity. Sliding can be added later as an option.

**Garbage collection:** Each window key has a TTL = window + 1s. When the window passes, Garnet auto-deletes the key. No background cleanup.

## IHighwayClient Interface Extension

```csharp
// Added to IHighwayClient in Highway.Abstractions:

/// <summary>Acquires a distributed lock. Non-blocking: check Acquired on the result.</summary>
Task<IDistributedLock> AcquireLockAsync(string key, TimeSpan expiry, CancellationToken ct = default);

/// <summary>Acquires a lock with retry until maxWait or cancellation.</summary>
Task<IDistributedLock> AcquireLockAsync(string key, TimeSpan expiry, TimeSpan retryInterval, TimeSpan maxWait, CancellationToken ct = default);

/// <summary>Atomically increments a counter and returns the new value.</summary>
Task<long> IncrementAsync(string key, long amount = 1, CancellationToken ct = default);

/// <summary>Atomically decrements a counter and returns the new value.</summary>
Task<long> DecrementAsync(string key, long amount = 1, CancellationToken ct = default);

/// <summary>Gets the current counter value without modifying it.</summary>
Task<long> GetCounterAsync(string key, CancellationToken ct = default);

/// <summary>Checks a rate limit. Returns whether the request is allowed.</summary>
Task<RateLimitResult> CheckRateLimitAsync(string key, int limit, TimeSpan window, CancellationToken ct = default);
```

## Result Types (in Abstractions)

```csharp
public sealed class RateLimitResult
{
    public required bool Allowed { get; init; }
    public required int Remaining { get; init; }
    public required TimeSpan RetryAfter { get; init; }
}
```

## Error Handling

All runtime primitives follow the same pattern:
- Transport failures throw `HighwayTransportException` (same as `PublishAsync`)
- CancellationToken honored everywhere
- Invalid arguments (null/empty key, negative expiry) throw `ArgumentException` immediately — no network round trip for bad input
- These are NOT error-as-data (unlike RPC) because they're infrastructure calls, not business calls. An infrastructure failure that silently returns "success" would corrupt application state.

## Performance Characteristics

| Operation | Round trips | Garnet commands |
|---|---|---|
| Cache Get | 1 | GET |
| Cache Set (absolute) | 1 | SETEX |
| Cache Set (sliding) | 2 | SETEX + SET (metadata) |
| Cache Remove | 2 | DEL + DEL (metadata) |
| Cache Refresh | 2 | GET (metadata) + EXPIRE |
| Lock Acquire | 2 | INCR + SET NX EX |
| Lock Release | 1 | EVAL (Lua) |
| Counter Increment | 1 | INCRBY |
| Counter Get | 1 | GET |
| Rate Limit Check | 1-2 | INCR + conditional EXPIRE |

All operations are sub-millisecond against a local Garnet server.

## Dependencies

- No new package references. SE.Redis already supports `EVAL`, `SET NX EX`, `INCRBY`, `EXPIRE` — all standard RESP commands.
- No new server commands. Everything works against stock Garnet.
- `Microsoft.Extensions.Caching.Abstractions` is needed in `Highway.Client` for `IDistributedCache` / `DistributedCacheEntryOptions`. This is a framework package (part of .NET 10 shared framework), not an external dependency.

## Cross-References

- Runtime vision: `docs/product/runtime-vision.md`
- Existing connection: `docs/features/005-client-server-communication/design.md` § "HighwayConnection"
- Key namespace convention: `docs/product/runtime-vision.md` § "Key Schema Convention"
