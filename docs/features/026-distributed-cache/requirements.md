# Feature: Distributed Cache

## Introduction

Highway runs on Garnet — a high-performance cache-store. Every Highway application already
holds a connection to it. Today that connection is used exclusively for messaging (`HW.*`
commands). This feature exposes Garnet's native caching capability through .NET's standard
caching interfaces, so applications get a distributed cache without adding a second piece of
infrastructure.

The goal is zero-ceremony caching backed by the server you already run. Register Highway,
get `IDistributedCache` and `HybridCache` working — no second connection string, no Redis
package, no separate process.

### Design principle

Use native .NET interfaces wherever they exist. Highway adds the *implementation*, not a
competing abstraction. A developer who already knows `IDistributedCache` or `HybridCache`
uses them unchanged — the only difference is what backs them.

## Requirements

### Requirement 1: `IDistributedCache` Implementation

**User Story:** As a developer using Highway, I want a distributed cache backed by my
existing Highway broker, so that I do not need a separate Redis/Memcached deployment for
caching.

#### Acceptance Criteria

1. Highway SHALL provide an implementation of `IDistributedCache`
   (`Microsoft.Extensions.Caching.Distributed`) that reads and writes to the Highway broker
   (Garnet) over the existing client connection.
2. The implementation SHALL support all `IDistributedCache` operations: `Get`, `GetAsync`,
   `Set`, `SetAsync`, `Refresh`, `RefreshAsync`, `Remove`, `RemoveAsync`.
3. The implementation SHALL respect `DistributedCacheEntryOptions`: `AbsoluteExpiration`,
   `AbsoluteExpirationRelativeToNow`, and `SlidingExpiration`.
4. Sliding expiration SHALL be implemented by storing the sliding window and refreshing the
   TTL on each `Get` or `Refresh` call.
5. Cache keys SHALL be stored with a configurable prefix (default `hw:cache:`) to avoid
   collision with Highway's internal keys (`hw:svc:`, `hw:ch:`, `hw:q:`, etc.).
6. The implementation SHALL use standard Garnet/Redis string commands (`GET`, `SET`, `DEL`,
   `EXPIRE`, `PEXPIRE`) — not custom `HW.*` commands — because caching is a Garnet-native
   capability that does not need Highway protocol extensions.

### Requirement 2: `IBufferDistributedCache` Implementation

**User Story:** As a performance-conscious developer, I want the cache implementation to
support the buffer-based interface, so that `HybridCache` can avoid `byte[]` allocations.

#### Acceptance Criteria

1. The implementation SHALL additionally implement `IBufferDistributedCache`
   (`Microsoft.Extensions.Caching.Distributed`), providing `SetAsync` and `TryGetAsync`
   overloads that work with `ReadOnlySequence<byte>` and `IBufferWriter<byte>`.
2. When `HybridCache` is registered, it SHALL automatically use the buffer path for reduced
   allocations.

### Requirement 3: Registration — Automatic and Standalone

**User Story:** As a developer, I want the cache to register with minimal configuration,
either as part of `AddHighway` or standalone.

#### Acceptance Criteria

1. Calling `AddHighway(...)` SHALL automatically register the `IDistributedCache`
   implementation in the DI container (via `TryAdd` — it does not override an existing
   registration).
2. A standalone `AddHighwayCache(...)` extension method SHALL be available for applications
   that want only the cache without the messaging engine (services, queues, pub/sub).
3. The standalone method SHALL accept the server connection string and an optional key
   prefix. It SHALL NOT start the Highway engine, worker loops, or heartbeat.
4. Both registration paths SHALL reuse the same underlying Garnet connection when both
   messaging and caching are active in the same process.

### Requirement 4: `HybridCache` Integration

**User Story:** As a developer, I want to use .NET's `HybridCache` with Highway as the L2
store, so that I get typed caching, stampede protection and L1/L2 layering out of the box.

#### Acceptance Criteria

1. When both `AddHighway` (or `AddHighwayCache`) and `AddHybridCache` are registered,
   `HybridCache` SHALL automatically use Highway's `IDistributedCache` as its L2 secondary
   store — no additional wiring required.
2. The UserGuide SHALL document this integration: register Highway, register `HybridCache`,
   inject `HybridCache` and use `GetOrCreateAsync<T>`. One code example showing the full
   setup.
3. Serialization SHALL use `System.Text.Json` by default (consistent with Highway's
   existing serialization choice). Users MAY configure alternative serializers via
   `HybridCache`'s standard `AddSerializer` / `AddSerializerFactory` extension points.

### Requirement 5: Key Isolation and Safety

**User Story:** As a developer, I want cache keys to be isolated from Highway's internal
state, so that a cache write cannot corrupt messaging infrastructure.

#### Acceptance Criteria

1. All cache keys SHALL be prefixed (default `hw:cache:`) before being sent to Garnet.
2. The prefix SHALL be configurable via `HighwayCacheOptions.KeyPrefix`.
3. The implementation SHALL NOT use keys in any namespace used by Highway's messaging
   (`hw:svc:`, `hw:ch:`, `hw:q:`, `hw:grp:`, `hw:dlq:`, `hw:reg:`, `hw:idem:`, etc.).
4. A key containing the prefix separator internally SHALL NOT cause ambiguity — the prefix
   is prepended literally, not parsed.

### Requirement 6: Connection Lifecycle

**User Story:** As a developer, I want the cache to respect the application's lifecycle
without adding its own background work.

#### Acceptance Criteria

1. The cache implementation SHALL be stateless beyond the connection — no background threads,
   no timers, no sweeps.
2. If the Garnet connection is unavailable, cache operations SHALL throw (or return null for
   `Get`) consistent with how `StackExchangeRedisCache` behaves — no silent fallback, no
   retry loop.
3. The cache SHALL NOT interfere with Highway's graceful shutdown (`DrainTimeout`). Cache
   operations in-flight during shutdown MAY fail; queued messages are unaffected.

### Requirement 7: Samples and Documentation

**User Story:** As a new developer, I want to see caching in action alongside messaging in
the samples.

#### Acceptance Criteria

1. The storefront sample SHALL demonstrate cache usage: cache an order lookup result, show a
   cache hit on repeated `get` commands.
2. The UserGuide SHALL gain a "Distributed Cache" section following the same pattern as RPC,
   Queues, Pub/Sub: concept → objects → usage → behavior.
3. The section SHALL state that Highway uses Garnet's native caching and that `HybridCache`
   provides the typed, stampede-protected layer on top.

### Requirement 8: Nothing Breaks

#### Acceptance Criteria

1. The full test suite SHALL pass with no test edited.
2. Applications that do not use caching SHALL see no behavioral difference — the
   `IDistributedCache` registration is additive (`TryAdd`).
3. `dotnet build --no-incremental` SHALL report zero warnings.
4. The samples SHALL run unchanged (caching additions are additive commands in the
   storefront, not changes to existing ones).
