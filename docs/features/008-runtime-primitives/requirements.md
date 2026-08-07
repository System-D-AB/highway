# Feature: Runtime Primitives (Cache, Lock, Counter, Rate Limit)

> ## WITHDRAWN — not implemented, not planned as specified
>
> This spec was written under the "distributed application runtime" framing that
> `docs/product/runtime-vision.md` set out. **That document has been withdrawn** and
> deleted; `docs/product/roadmap.md` § "Beyond v1" records why in full.
>
> The short version: the framing borrowed Dapr's name, invited comparison on breadth
> against a product whose advantage is that it is easy, and several of the nine proposed
> primitives wrapped commands an application can already issue on the connection it
> already holds.
>
> **What replaced it:** feature 013 (Reliable Delivery) — dead letters, delayed delivery
> and deduplication — which fixes gaps Highway itself creates rather than adding surface.
>
> Of this spec's four primitives, two remain reasonable *small* additions later and two do
> not. See the roadmap. A distributed lock in particular is only worth shipping with a
> **fencing token**: `SET NX EX` alone is not a correctness lock, because a GC pause or
> clock skew lets two holders proceed at once.
>
> Left in place as a record of what was considered, per the project's rule against
> rewriting history.

## Introduction

Highway.Server is a full Garnet instance. Every node already has a persistent connection to it. This feature exposes four distributed primitives through that existing connection — no new server commands, no new processes, no new configuration. They ride the same wire the RPC and Pub/Sub traffic already uses.

- **Distributed Cache** — Standard .NET `IDistributedCache` backed by the Highway server
- **Distributed Locking** — Acquire/release named locks with expiry and fencing tokens
- **Atomic Counters** — Monotonically increasing distributed sequences
- **Rate Limiting** — Sliding/fixed window limiters backed by server-side counters

All four use stock Garnet operations (`GET`, `SET`, `DEL`, `INCR`, `EXPIRE`, `SET NX EX`). No custom `HW.*` commands are added. The features are opt-in — `AddHighway` registers them automatically when the server is configured, but they don't consume resources until called.

## Glossary

- **Runtime primitive** — A distributed building block (cache, lock, counter, rate limit) exposed through the Highway connection, requiring no additional infrastructure
- **Fencing token** — A monotonically increasing value returned with a lock, used by external systems to detect stale lock holders
- **Sliding window** — A rate-limiting strategy where the window slides with time (not aligned to fixed intervals)
- **Fixed window** — A rate-limiting strategy where the window resets at fixed boundaries (simpler, slightly less accurate)

## Requirements

### Requirement 1: Distributed Cache — IDistributedCache

**User Story:** As a developer, I want `IDistributedCache` backed by Highway's server with zero extra configuration, so that I can cache data without a separate Redis connection.

#### Acceptance Criteria

1. `AddHighway(...)` automatically registers `IDistributedCache` in the DI container — no separate `AddDistributedCache()` call needed
2. The implementation uses the existing `ConnectionMultiplexer` from the Highway engine — no new connections
3. `SetAsync(key, value, options)` maps to `SET hw:rt:cache:{key} {value}` with appropriate expiry (`SETEX` for absolute, `SET EX` for sliding)
4. `GetAsync(key)` maps to `GET hw:rt:cache:{key}`; returns `null` for missing keys
5. `RemoveAsync(key)` maps to `DEL hw:rt:cache:{key}`
6. `RefreshAsync(key)` extends the sliding expiry by re-applying the TTL (`EXPIRE`)
7. Both `AbsoluteExpiration` and `SlidingExpiration` from `DistributedCacheEntryOptions` are supported; when both are set, the entry expires at whichever comes first
8. Keys are namespaced under `hw:rt:cache:` — no collision with messaging keys or stock Garnet data
9. Cache operations are non-blocking and async throughout
10. Cache operations do not interfere with RPC/Pub/Sub traffic (shared multiplexer, independent key space)
11. An opt-out flag (`HighwayOptions.RegisterDistributedCache = true` by default) allows applications that bring their own `IDistributedCache` to skip registration

### Requirement 2: Distributed Locking

**User Story:** As a developer, I want to acquire exclusive distributed locks so that only one process runs a critical section across my cluster.

#### Acceptance Criteria

1. `IHighwayClient` exposes `AcquireLockAsync(string key, TimeSpan expiry, CancellationToken ct)` returning an `IDistributedLock`
2. `IDistributedLock` has: `bool Acquired`, `string? Token` (fencing token), `IAsyncDisposable` (release on dispose)
3. Lock acquisition uses `SET hw:rt:lock:{key} {token} NX EX {seconds}` — atomic set-if-not-exists with expiry
4. If the lock is already held, `Acquired` is `false` and no exception is thrown — the caller decides what to do
5. The fencing token is a monotonically increasing value (server-side `INCR hw:rt:lock:seq:{key}`) assigned at acquisition
6. Lock release (`DisposeAsync`) deletes the key only if the current value matches the token (Lua script or `WATCH`/`MULTI`) — prevents releasing another holder's lock
7. Lock expiry protects against holder crashes — if the holder dies, the lock auto-releases after TTL
8. A convenience overload `AcquireLockAsync(key, expiry, retryInterval, maxWait, ct)` retries acquisition at `retryInterval` until `maxWait` or cancellation
9. All lock keys live under `hw:rt:lock:` namespace
10. Lock operations work with `HighwayTestServer` for integration testing

### Requirement 3: Atomic Counters / Sequences

**User Story:** As a developer, I want globally unique monotonically increasing numbers (order IDs, invoice numbers) generated atomically across all nodes.

#### Acceptance Criteria

1. `IHighwayClient` exposes `IncrementAsync(string key, long amount = 1, CancellationToken ct)` returning `long` (the new value after increment)
2. `IHighwayClient` exposes `GetCounterAsync(string key, CancellationToken ct)` returning the current value without incrementing (or 0 if the key doesn't exist)
3. Counter uses `INCRBY hw:rt:seq:{key} {amount}` — atomic, returns the new value
4. Counter values start at 0 and increment — first `IncrementAsync("orders")` returns 1
5. Counters are persistent (survive server restart with AOF enabled)
6. Counters are namespace-isolated under `hw:rt:seq:`
7. A `DecrementAsync(key, amount, ct)` overload is provided via `DECRBY`
8. Concurrent increments from multiple nodes never produce duplicate values

### Requirement 4: Rate Limiting

**User Story:** As a developer, I want to enforce rate limits across all nodes so that shared resources are protected without node-local guessing.

#### Acceptance Criteria

1. `IHighwayClient` exposes `CheckRateLimitAsync(string key, int limit, TimeSpan window, CancellationToken ct)` returning a `RateLimitResult`
2. `RateLimitResult` contains: `bool Allowed`, `int Remaining`, `TimeSpan RetryAfter` (how long until the window resets, if rejected)
3. Fixed-window implementation: `INCR hw:rt:rate:{key}:{windowId}` + `EXPIRE` on first increment — counts requests in the current window boundary
4. Window boundaries are computed from UTC time (e.g., for a 1-minute window, `windowId` = Unix minute)
5. When the count exceeds `limit`, `Allowed` is `false` and `Remaining` is 0
6. When the count is within limit, `Allowed` is `true` and `Remaining` = `limit - count`
7. Expired window keys are garbage-collected by Garnet's TTL mechanism — no background cleanup needed
8. Rate limit is enforced globally across all nodes — not per-node
9. A sliding-window option is available as a future enhancement; v1 ships fixed-window only (simpler, fewer round trips)
10. All rate-limit keys live under `hw:rt:rate:` namespace

### Requirement 5: Public API Surface in Highway.Abstractions

**User Story:** As a library consumer, I want the runtime primitive interfaces in the Abstractions package so that shared libraries can depend on them without referencing the Client implementation.

#### Acceptance Criteria

1. `IDistributedLock` interface is defined in `Highway.Abstractions`
2. `RateLimitResult` type is defined in `Highway.Abstractions`
3. `IncrementAsync`, `GetCounterAsync`, `DecrementAsync`, `AcquireLockAsync`, and `CheckRateLimitAsync` are added to `IHighwayClient` interface in Abstractions
4. No implementation lives in Abstractions — zero dependencies preserved
5. The Abstractions package remains dependency-free after this change

### Requirement 6: Registration and Configuration

**User Story:** As a developer, I want the runtime primitives available immediately after `AddHighway` with sensible defaults and optional tuning.

#### Acceptance Criteria

1. `AddHighway(...)` registers `IDistributedCache` automatically (opt-out via `RegisterDistributedCache = false`)
2. No additional `AddXxx()` call is needed for locks, counters, or rate limiting — they're methods on `IHighwayClient` which is already registered
3. Configuration options on `HighwayOptions`: `CacheKeyPrefix` (default `hw:rt:cache:`), `LockKeyPrefix` (default `hw:rt:lock:`), `CounterKeyPrefix` (default `hw:rt:seq:`), `RateLimitKeyPrefix` (default `hw:rt:rate:`)
4. Prefix customization allows multiple Highway deployments sharing one Garnet server to isolate their runtime state
5. All defaults work with zero configuration — a developer who just calls `AddHighway(o => o.Server = "...")` gets everything

### Requirement 7: Testing

**User Story:** As a Highway contributor, I want all runtime primitives covered by unit and integration tests requiring no external infrastructure.

#### Acceptance Criteria

1. Integration tests run against `HighwayTestServer` — same pattern as all other Highway features
2. Cache tests cover: set/get/remove, absolute expiry, sliding expiry, refresh, missing key returns null, namespace isolation from messaging keys
3. Lock tests cover: acquire succeeds, second acquire returns `Acquired = false`, release frees for re-acquisition, expired lock auto-releases, fencing token is monotonic, safe release (can't release another holder's lock)
4. Counter tests cover: increment returns new value, first increment returns 1, concurrent increments from multiple connections produce unique values, decrement works, get without increment returns current value
5. Rate limit tests cover: within limit returns allowed, over limit returns rejected with correct remaining/retry-after, window reset allows new requests, global enforcement across multiple connections
6. No test requires external infrastructure; test naming follows `Method_Scenario_ExpectedBehavior`
