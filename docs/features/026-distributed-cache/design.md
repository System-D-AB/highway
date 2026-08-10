# Design: Distributed Cache

## Overview

```
┌─────────────────────────────────────────────────────────────────────────────┐
│  Application                                                                 │
│                                                                              │
│  ┌──────────────────┐    ┌──────────────────────────────────────────────┐   │
│  │  HybridCache     │    │  IHighwayClient                              │   │
│  │  (L1 memory +    │    │  ExecuteAsync / SendAsync / PublishAsync      │   │
│  │   L2 distributed)│    │                                              │   │
│  └────────┬─────────┘    └──────────────────┬───────────────────────────┘   │
│           │                                  │                               │
│           ▼                                  │                               │
│  ┌──────────────────┐                        │                               │
│  │ IDistributedCache │◄──────────────────────┼── same connection             │
│  │ IBufferDistrib...│                        │                               │
│  │ (HighwayCache)   │                        │                               │
│  └────────┬─────────┘                        │                               │
│           │                                  │                               │
└───────────┼──────────────────────────────────┼───────────────────────────────┘
            │                                  │
            ▼                                  ▼
     ┌─────────────────────────────────────────────────┐
     │  SE.Redis ConnectionMultiplexer                  │
     │  (shared — one connection for HW.* and GET/SET)  │
     └──────────────────────┬──────────────────────────┘
                            │
                            ▼
     ┌─────────────────────────────────────────────────┐
     │  Highway.Server (Garnet)                         │
     │  HW.* commands + native GET/SET/DEL/EXPIRE       │
     └─────────────────────────────────────────────────┘
```

The implementation is deliberately thin. Garnet already *is* a cache-store — the job is to
wire .NET's interface to it through Highway's existing connection, not to build caching
machinery.

## Key decisions

### D1 — Use the same `ConnectionMultiplexer` Highway already holds

`HighwayEngine` creates a `ConnectionMultiplexer` at start for the `HW.*` commands. The cache
implementation shares it. This means:

- One TCP connection to the broker, not two.
- No separate connection string to configure.
- Connection lifecycle is owned by the engine (or by `AddHighwayCache` in standalone mode).

The multiplexer already handles concurrent operations — SE.Redis pipelines commands across a
single TCP socket. Cache `GET`/`SET` and Highway `HW.CALL` coexist without interference.

### D2 — Standard Redis commands, not custom `HW.*`

Caching uses `GET`, `SET` (with `EX`/`PX`/`EXAT`/`PXAT`), `DEL`, `GETEX`, and `PERSIST` —
all natively supported by Garnet. No protocol extension needed. This is a feature, not a
limitation:

- Cache entries are visible via `redis-cli` and standard tooling.
- No protocol document update required.
- No stored procedure registration, no AOF command-set coupling.
- Garnet's own memory management, eviction, and storage tiering apply to cache keys
  naturally.

### D3 — Key prefix isolates cache from messaging state

All cache keys are stored as `{prefix}{userKey}`. Default prefix: `hw:cache:`.

Highway's internal keys use namespaces: `hw:svc:`, `hw:ch:`, `hw:q:`, `hw:grp:`, `hw:dlq:`,
`hw:reg:`, `hw:idem:`, `hw:rec:`. The cache prefix is chosen to be distinct from all of
these. A user who sets `"session:42"` stores `hw:cache:session:42` — unreachable by any
Highway command's key derivation.

### D4 — Sliding expiration via `GETEX`

`IDistributedCache.Get` with sliding expiration must refresh the TTL. Garnet supports
`GETEX` (get and set expiry atomically) — one round-trip, no race. The implementation:

- `Set`: stores with `PXAT` = min(absolute, now + sliding). Also stores the sliding window
  as a suffix in metadata (see D5).
- `Get`: if sliding, uses `GETEX PX {slidingMs}` capped at the absolute deadline.
- `Refresh`: same as `Get` but discards the value.

### D5 — Metadata encoding for sliding + absolute combined expiration

`IDistributedCache` allows both sliding and absolute expiration on the same entry. Garnet's
TTL is a single value, so the implementation must track the sliding window and absolute
deadline to compute the correct TTL on each access.

Approach: store a small header before the payload bytes.

```
[1 byte: version] [8 bytes: absoluteDeadline (UTC ticks, 0 = none)] [2 bytes: slidingSeconds (0 = none)] [payload]
```

- Total overhead: 11 bytes.
- Version byte allows future changes without breaking existing entries.
- `absoluteDeadline` = 0 means no absolute expiry (only sliding).
- `slidingSeconds` = 0 means no sliding (only absolute, or the TTL-only case via SET EX).
- When both are zero (no options), the entry has no TTL and the header is still present for
  uniformity, but the TTL is not set on the key.

On `Get`:
1. Read the header.
2. If `slidingSeconds > 0`: compute new TTL = min(slidingSeconds, absoluteDeadline - now).
3. If new TTL ≤ 0: the entry has logically expired — return null, delete the key.
4. Otherwise: `GETEX PX {newTtlMs}` (or the initial `GET` already returned the value;
   issue `PEXPIRE` separately — two commands but the header has the information).

Simpler alternative for the common case: when only absolute *or* only sliding is set (not
both), skip the metadata entirely:
- Absolute only: `SET key value PXAT {deadline}` — no header needed, `Get` is plain `GET`.
- Sliding only: `SET key value PX {sliding}` — `Get` is `GETEX PX {sliding}`.
- Both: use the header.

This keeps the zero-overhead path for the 90% case (most callers set one or the other, not
both).

### D6 — `IBufferDistributedCache` for zero-copy with `HybridCache`

The `IBufferDistributedCache` interface (introduced alongside `HybridCache`) provides:
- `SetAsync(key, ReadOnlySequence<byte>, options)` — avoids allocating a `byte[]` for the
  value.
- `TryGetAsync(key, IBufferWriter<byte>)` — writes directly into the caller's buffer.

SE.Redis supports `RedisValue` from `ReadOnlyMemory<byte>` and can write results into pooled
buffers. The implementation maps these directly.

### D7 — Registration: `TryAdd` semantics, two entry points

```csharp
// Entry point 1: messaging + cache (the common case)
services.AddHighway(o => { o.Server = "..."; });
// Internally calls TryAddSingleton<IDistributedCache, HighwayCache>()
// Does NOT override an existing registration — if someone already registered
// StackExchangeRedisCache or SqlServerCache, Highway defers.

// Entry point 2: cache only (no messaging engine)
services.AddHighwayCache(o => { o.Server = "..."; o.KeyPrefix = "app:"; });
// Registers IDistributedCache + IBufferDistributedCache
// Creates its own ConnectionMultiplexer (no engine, no worker loops)
```

When both are registered in the same process, the connection is shared — `AddHighway`
detects an existing `HighwayCache` and gives it the engine's multiplexer rather than creating
a second one.

### D8 — `HybridCache` layering — documentation only, no code

Highway does not implement or extend `HybridCache`. It provides the L2 (`IDistributedCache`)
and the standard `AddHybridCache()` from `Microsoft.Extensions.Caching.Hybrid` does the
rest. The feature's contribution is:

1. The `IDistributedCache` implementation (the L2 store).
2. Documentation showing the one-two registration:
   ```csharp
   services.AddHighway(o => { o.Server = "..."; });
   services.AddHybridCache(o =>
   {
       o.DefaultEntryOptions = new() { Expiration = TimeSpan.FromMinutes(5) };
   });
   ```
3. A note that `HybridCache` uses `System.Text.Json` by default (matching Highway) and that
   alternative serializers are configured through `HybridCache`'s own API.

No Highway code touches `HybridCache` internals. The integration is purely via the standard
DI contract.

## Class structure

```csharp
// Highway.Client/Caching/HighwayCache.cs
namespace Highway.Client.Caching;

public sealed class HighwayCache : IDistributedCache, IBufferDistributedCache, IDisposable
{
    private readonly IDatabase _db;
    private readonly string _prefix;

    internal HighwayCache(IConnectionMultiplexer connection, HighwayCacheOptions options) { ... }

    // IDistributedCache
    public byte[]? Get(string key) => ...;
    public async Task<byte[]?> GetAsync(string key, CancellationToken ct = default) => ...;
    public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => ...;
    public async Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken ct = default) => ...;
    public void Refresh(string key) => ...;
    public async Task RefreshAsync(string key, CancellationToken ct = default) => ...;
    public void Remove(string key) => ...;
    public async Task RemoveAsync(string key, CancellationToken ct = default) => ...;

    // IBufferDistributedCache
    public async ValueTask<bool> TryGetAsync(string key, IBufferWriter<byte> destination, CancellationToken ct = default) => ...;
    public async ValueTask SetAsync(string key, ReadOnlySequence<byte> value, DistributedCacheEntryOptions options, CancellationToken ct = default) => ...;
}

// Highway.Client/Caching/HighwayCacheOptions.cs
public sealed class HighwayCacheOptions
{
    public string? Server { get; set; }
    public string KeyPrefix { get; set; } = "hw:cache:";
}
```

## Error handling

| Condition | Behavior |
|---|---|
| Connection unavailable | Operations throw `RedisConnectionException` (same as StackExchangeRedisCache) |
| Key not found | `Get` returns `null`; `TryGet` returns `false` |
| Entry logically expired (absolute deadline passed but TTL not yet fired) | `Get` returns `null`, issues `DEL` |
| `Server` not set in standalone mode | `InvalidOperationException` at registration (fail-fast) |
| Key prefix collision with HW internal namespace | Prevented by default prefix; validated at registration if custom |

## What already exists (reuse, not rebuild)

- The `ConnectionMultiplexer` in `HighwayEngine` — shared, not duplicated.
- SE.Redis `IDatabase.StringGetAsync` / `StringSetAsync` / `KeyDeleteAsync` /
  `KeyExpireAsync` / `Execute("GETEX", ...)` — the entire implementation is thin wrappers.
- `HybridCache` from `Microsoft.Extensions.Caching.Hybrid` — no code to write; document the
  integration.
- `HighwayTestServer` — cache tests use the same embedded server; no new infrastructure.

## Test strategy

1. **Round-trip:** `Set` then `Get` returns the same bytes. Async and sync paths.
2. **Absolute expiration:** `Set` with 1-second absolute; sleep; `Get` returns null.
3. **Sliding expiration:** `Set` with 2-second sliding; `Get` at 1.5s refreshes; `Get` at
   3s from last access returns null; `Get` within the window succeeds.
4. **Combined expiration:** sliding does not extend past absolute deadline.
5. **Remove:** `Set` then `Remove` then `Get` returns null.
6. **Refresh:** refreshes TTL without returning the value.
7. **Key prefix:** keys stored in Garnet carry the prefix; raw access without prefix finds
   nothing.
8. **Isolation:** a cache key cannot read/write a `hw:svc:*` key.
9. **Buffer interface:** `TryGetAsync` writes to buffer; `SetAsync` from
   `ReadOnlySequence<byte>` round-trips correctly.
10. **`HybridCache` integration:** `GetOrCreateAsync<T>` with Highway as L2 — factory called
    on first access, served from cache on second.
11. **Standalone mode:** `AddHighwayCache` without `AddHighway` — cache works, no engine
    starts.
12. **Connection reuse:** both `AddHighway` and `AddHighwayCache` in one process — one
    multiplexer, both paths work.

All tests run against `HighwayTestServer` (embedded Garnet). No external infrastructure.

## Packages and dependencies

The implementation lives in `Highway.Client` — no new package. Dependencies added:

- `Microsoft.Extensions.Caching.Abstractions` (already transitively present via
  `Microsoft.Extensions.Hosting`).

`Microsoft.Extensions.Caching.Hybrid` is NOT a dependency of Highway. It is a dependency of
the *application* that wants typed caching — registered independently via `AddHybridCache`.
The UserGuide documents the combination; Highway does not force it.
