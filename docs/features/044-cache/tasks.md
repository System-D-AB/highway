# Feature 044 — Broker-Local Cache: Tasks

*Implements [`requirements.md`](requirements.md) per [`design.md`](design.md). An opt-in add-on
— it must add zero core-broker behaviour change when disabled. Independent of the herd feature
except for the one epoch-wipe hook (T4), which reuses the feeder's existing transitions.*

```
T1 (cache store seam + impls + sweeper)
      │
      ▼
T2 (options + host schema)          T4 (feeder OnEpochChanged hook)
      │                                     │
      ▼                                     │
T3 (wire routing: hw:cache:* on the raw surface) ◄──────┘
      │
      ▼
T5 (server wiring: construct store, wire epoch-wipe + sweeper, non-master gating)
      │
      ▼
T6 (client: HighwayCache + AddHighwayCache)  ── gate: IDistributedCache round-trip + HybridCache L2
      │
      ▼
T7 (the record: constraints, protocol served-subset, product/roadmap, 041 pointer)
```

---

### - [x] T1 — The cache store seam and implementations

**Fulfills:** R2.1, R5.2, R7.1
`IHighwayCacheStore` (`Get`/`Set(value,ttl)`/`Remove`/`Clear`/`SizeBytes`). `RocksDbCacheStore`
over its **own** `RocksDb` at `dataDir/cache` (values via `ExpiryFraming`, filter-on-read).
`InMemoryCacheStore` (ConcurrentDictionary + the same expiry). `CacheSweeper`: drop expired,
then clear-on-`maxSizeBytes` with a named event.
**Done when:** unit — set/get/remove/clear round-trip on both impls; a lapsed entry reads as a
miss before the sweep; the sweeper clears when over the cap and records the event.

### - [x] T2 — Options and host schema

**Fulfills:** R1.1, R1.2
`CacheOptions` on `HighwayServerOptions` (`Enabled` default false, `DefaultTtl`, `MaxTtl`,
`MaxSizeBytes`, `SweepInterval`), validated (`0 < DefaultTtl ≤ MaxTtl`, positive cap). Host
`server.cache.*` section + applicator + effective-config printer, **with
`SchemaCompletenessTests` in the same commit** (031 R2.1).
**Done when:** schema round-trips; disabled-by-default asserted; validation rejects a bad TTL
pair; schema-completeness green.

### - [x] T3 — Wire routing: `hw:cache:*` on the raw surface

**Fulfills:** R4.1, R4.2, R4.4
Extend `RespSession`'s `SET`/`GET`/`DEL`/`UNLINK`/`SETEX`/`PSETEX`/`TTL`/`PTTL` handlers with a
third prefix branch → the cache store (TTL from PX/EX, default when none, clamped to `MaxTtl`).
The existing `EnsureWritable` gate already covers cache writes on a non-master (`-NOTPRIMARY`);
cache reads on a non-master return a miss. Every non-`hw:{rep,idem,cache}:` key stays
`ERR HW_INVALID_ARG`. **No new `HW.*` command.**
**Done when:** unit — cache commands hit the cache store; idempotency/reply-slot behaviour
unchanged; non-master cache write refused, read misses; conformance untouched.

### - [x] T4 — Feeder epoch-change hook

**Fulfills:** R6.1, R6.2
Add `event Action? OnEpochChanged` to `ReplicationFeeder`, fired inside `TryPromote` (after
`epoch++`) and `ObserveHigherEpoch` (on adopt/demote). No behaviour change when nothing
subscribes.
**Done when:** unit — promote and adopt-higher-epoch each raise the event exactly once; a
herd-holding master with no epoch change raises nothing.

### - [x] T5 — Server wiring

**Fulfills:** R1.3, R2.2, R6.1, R7.1, OD3
In `RespServer`: when `Cache.Enabled`, construct the cache store (RocksDB at `dataDir/cache`
when durable, in-memory when ephemeral), start the `CacheSweeper`, and wire
`feeder.OnEpochChanged += cacheStore.Clear`. When disabled, none of it exists. Dispose cleanly.
The cache store holds **no** reference to the replicated store or the feeder (isolation).
**Done when:** integration — an enabled durable broker caches and serves; a promote clears the
cache; the isolation type-test passes (cache store ↔ feeder share no reference; a cache write is
absent from the broker DB's `GetUpdatesSince`); a disabled broker is byte-identical to today.

### - [x] T6 — Client adapter *(gate)*

**Fulfills:** R3
`Highway.Client/Caching/HighwayCache.cs` implementing `IDistributedCache` over the shared
`IHighwayConnection` (get/set/remove/refresh; absolute/sliding → PX; sliding-refresh re-SET on
read). `AddHighwayCache(...)` DI extension registering it as the `IDistributedCache` singleton
over the existing connection. Re-add the `Microsoft.Extensions.Caching.Abstractions` reference
(and `Hybrid` for the smoke test only).
**Done when:** integration against an embedded broker — `IDistributedCache` get/set/remove
round-trips; a set honours/clamps TTL; a `HybridCache` built on it resolves from L2; a cache
call mid-failover re-drives and misses (no special handling), the herd client unchanged.

### - [x] T7 — The record

**Fulfills:** R4.3, R8
`HIGHWAY-PROTOCOL.md` served-subset section gains `hw:cache:*` on the routed commands (prose
only; changelog bump). `constraints.md`: broker-local / not-replicated / cold-after-failover
(with the cache-miss-burst note) / TTL-bounded + epoch-invalidated / best-effort. `product.md`
cache line amended with a dated 044 pointer; `roadmap.md` row added; the 041 R1.5 cache-removal
note gets a dated "succeeded by 044" pointer (no history rewrite).
**Done when:** a read of the register and the protocol doc describes the shipped cache with no
stale line; conformance green.

---

## Gates

- **Isolation (T5):** structurally proven that a cache write cannot enter replication — the
  feature's central safety property.
- **Client round-trip (T6):** `IDistributedCache` works and `HybridCache` gets an L2, with the
  cache honest about being broker-local and cold-after-failover.
- **Disabled = no change:** an enabled-off broker carries no cache surface, store, or behaviour
  delta — asserted, not assumed.
