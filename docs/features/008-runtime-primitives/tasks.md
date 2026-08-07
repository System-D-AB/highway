# Tasks: Runtime Primitives

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

## Task Dependency Graph

```
T1 (Abstractions: IDistributedLock, RateLimitResult, IHighwayClient extension)    [independent]
T2 (HighwayOptions: runtime config)                                                [independent]
T3 (HighwayConnection: runtime helpers — GET, SET, SETEX, DEL, INCRBY, EXPIRE, EVAL) → T2
T4 (HighwayDistributedCache)     → T1, T3
T5 (HighwayLock + Lua release)   → T1, T3
T6 (CounterOperations)           → T1, T3
T7 (RateLimiter)                 → T1, T3
T8 (HighwayClient — wire methods to implementations) → T4, T5, T6, T7
T9 (AddHighway registration)    → T4, T8
T10 (Unit tests)                 → T4, T5, T6, T7
T11 (Integration tests)          → T8, T9
```

## Tasks

- [ ] ### Task 1: Abstractions — Interfaces and Types

**Fulfills:** Requirement 5

**Steps:**
1. Create `src/Highway.Abstractions/IDistributedLock.cs`: `bool Acquired`, `string? Token`, `IAsyncDisposable`
2. Create `src/Highway.Abstractions/RateLimitResult.cs`: `bool Allowed`, `int Remaining`, `TimeSpan RetryAfter` (all `required init`)
3. Extend `IHighwayClient` with: `AcquireLockAsync` (both overloads), `IncrementAsync`, `DecrementAsync`, `GetCounterAsync`, `CheckRateLimitAsync`
4. XML doc comments on every member
5. Verify Abstractions still compiles with zero dependencies

**Done criteria:**
- All interfaces and types defined; Abstractions package remains zero-dependency; existing code compiles unchanged

---

- [ ] ### Task 2: HighwayOptions — Runtime Configuration

**Fulfills:** Requirement 6

**Steps:**
1. Add to `HighwayOptions`: `RegisterDistributedCache` (bool, default true), `CacheKeyPrefix` (default `"hw:rt:cache:"`), `LockKeyPrefix` (default `"hw:rt:lock:"`), `CounterKeyPrefix` (default `"hw:rt:seq:"`), `RateLimitKeyPrefix` (default `"hw:rt:rate:"`)
2. XML docs explaining prefix customization for shared-server scenarios
3. Unit test: defaults correct

**Done criteria:**
- Options added without breaking existing tests

---

- [ ] ### Task 3: HighwayConnection — Runtime Command Helpers

**Fulfills:** Foundation for all four primitives

**Steps:**
1. Add to `HighwayConnection`: `GetBytesAsync(key)`, `SetAsync(key, value)`, `SetExAsync(key, value, TimeSpan)`, `DeleteAsync(key)`, `ExpireAsync(key, TimeSpan)`, `IncrByAsync(key, amount)`, `DecrByAsync(key, amount)`, `SetNxExAsync(key, value, TimeSpan)` → bool, `EvalAsync(script, keys, args)` → RedisResult
2. Each method maps directly to the stock RESP command (no `HW.*` prefix)
3. Unit tests verifying command names/args match expected RESP (mocked or against test server)

**Done criteria:**
- All runtime Garnet operations available through typed helpers on the shared connection

---

- [ ] ### Task 4: Distributed Cache Implementation

**Fulfills:** Requirement 1

**Steps:**
1. Create `src/Highway.Client/Runtime/HighwayDistributedCache.cs` implementing `IDistributedCache`
2. `Get`/`GetAsync` → `GET {prefix}{key}`
3. `Set`/`SetAsync` → `SETEX` (absolute) or `SET EX` (sliding) + store sliding metadata
4. `Remove`/`RemoveAsync` → `DEL {prefix}{key}` + `DEL {prefix}slide:{key}`
5. `Refresh`/`RefreshAsync` → read sliding metadata → `EXPIRE`
6. TTL computation: absolute remaining time, sliding window, both → min
7. Add `Microsoft.Extensions.Caching.Abstractions` package reference to `Highway.Client.csproj` (framework package, add to `Directory.Packages.props`)

**Done criteria:**
- Full `IDistributedCache` contract implemented; handles absolute, sliding, and combined expiry

---

- [ ] ### Task 5: Distributed Lock Implementation

**Fulfills:** Requirement 2

**Steps:**
1. Create `src/Highway.Client/Runtime/HighwayLock.cs` implementing `IDistributedLock`
2. Create `src/Highway.Client/Runtime/LockReleaseScript.cs` with the Lua compare-and-delete script
3. Acquire: `INCR {prefix}seq:{key}` → token; `SET {prefix}{key} {token} NX EX {seconds}` → acquired?
4. Release (DisposeAsync): `EVAL` the Lua script with `KEYS[1]` = lock key, `ARGV[1]` = token
5. Retry overload: loop with `Task.Delay(retryInterval)` until acquired, maxWait, or cancellation
6. If not acquired, `DisposeAsync` is a no-op

**Done criteria:**
- Lock acquire/release is atomic and safe; fencing token is monotonic; expired lock auto-releases

---

- [ ] ### Task 6: Counter Implementation

**Fulfills:** Requirement 3

**Steps:**
1. Create `src/Highway.Client/Runtime/CounterOperations.cs`
2. `IncrementAsync` → `INCRBY {prefix}{key} {amount}`
3. `DecrementAsync` → `DECRBY {prefix}{key} {amount}`
4. `GetCounterAsync` → `GET {prefix}{key}` parsed to long (0 if null)
5. Validate: key non-empty, amount > 0

**Done criteria:**
- Counters work atomically across concurrent connections; first increment returns the amount

---

- [ ] ### Task 7: Rate Limiter Implementation

**Fulfills:** Requirement 4

**Steps:**
1. Create `src/Highway.Client/Runtime/RateLimiter.cs`
2. Compute `windowId` from UTC time: `UnixTimeSeconds / windowSeconds`
3. `INCR {prefix}{key}:{windowId}` → current count
4. If count == 1: `EXPIRE` the key with `window + 1s` grace
5. Return `RateLimitResult` with `Allowed`, `Remaining`, `RetryAfter`
6. Validate: key non-empty, limit > 0, window > 0

**Done criteria:**
- Rate limiting enforced globally; expired windows are auto-cleaned by Garnet TTL

---

- [ ] ### Task 8: HighwayClient — Wire Methods to Implementations

**Fulfills:** Requirement 2, 3, 4 (client surface)

**Steps:**
1. Inject/resolve `CounterOperations`, `RateLimiter`, lock acquire into `HighwayClient`
2. Implement `IHighwayClient.AcquireLockAsync` (both overloads)
3. Implement `IHighwayClient.IncrementAsync`, `DecrementAsync`, `GetCounterAsync`
4. Implement `IHighwayClient.CheckRateLimitAsync`
5. Existing `ExecuteAsync` and `PublishAsync` unchanged

**Done criteria:**
- All new `IHighwayClient` methods callable; existing RPC/Pub/Sub behavior untouched

---

- [ ] ### Task 9: AddHighway Registration

**Fulfills:** Requirement 6

**Steps:**
1. In `ServiceCollectionExtensions.AddHighway`: register `HighwayDistributedCache` as `IDistributedCache` via `TryAddSingleton` (skip if already registered or opt-out)
2. Register `CounterOperations`, `RateLimiter` as internal singletons consumed by `HighwayClient`
3. Verify `AddHighway` still works unchanged when no runtime features are used

**Done criteria:**
- Zero-config: `AddHighway(o => o.Server = "...")` gets cache, lock, counters, and rate limiting automatically

---

- [ ] ### Task 10: Unit Tests

**Fulfills:** Requirement 7 (unit level)

**Steps:**
1. Cache tests: TTL computation for absolute/sliding/combined, prefix isolation, null return for missing
2. Lock tests: acquire returns token, second acquire returns not-acquired, release Lua script logic, retry timeout
3. Counter tests: increment returns correct value, decrement, get on empty returns 0, argument validation
4. Rate limit tests: within limit, over limit, window boundary computation, retry-after calculation
5. Use mocked `HighwayConnection` where possible

**Done criteria:**
- All logic paths tested without a running server

---

- [ ] ### Task 11: Integration Tests

**Fulfills:** Requirement 7 (integration level)

**Steps:**
1. All tests against `HighwayTestServer` — no external infrastructure
2. Cache: set → get returns value; set with absolute TTL → wait → get returns null; refresh extends sliding; remove → get returns null; namespace isolation (messaging keys unaffected)
3. Lock: acquire → acquired true; second acquire → acquired false; release → re-acquire works; wait past TTL → lock auto-released; fencing token increments monotonically; safe release (can't release another's lock)
4. Counter: increment from 3 concurrent connections → all unique values, final value = 3; decrement; get after increments shows correct total; survives server restart (AOF test via `HighwayTestServer.Restart()`)
5. Rate limit: 5 requests within limit of 5 → all allowed; 6th → rejected with remaining=0 and retry-after > 0; wait for window reset → allowed again; global enforcement (2 connections share one counter)
6. Cross-feature: cache set + RPC call + counter increment all work in the same test without interference

**Done criteria:**
- All four primitives proven end-to-end with real Garnet; no flaky timing (use short windows/TTLs)
