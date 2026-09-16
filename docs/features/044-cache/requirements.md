# Feature 044 — Broker-Local Cache: Requirements

*Brings back the distributed-cache capability that feature 026 provided and 041 removed —
rebuilt for the RocksDB + herd-replication stack, and deliberately **not** a replicated,
consistent cache. It is a per-node cache with a shared client API: fast, best-effort,
cold after a failover. See the [reversal note in 041](../041-garnet-removal/requirements.md)
(R1.5, the cache removal) — this feature is the successor, not a restoration.*

## Introduction

Under Garnet the cache was free: Garnet was a full Redis server, so an `IDistributedCache`
adapter just issued `SET`/`GET` on `hw:cache:*` keys. The RocksDB + `HW.*`-only stack has no
general keyspace, and the cache had no substrate — so it was cut (041 T0). RocksDB *is* a K/V
store, so the substrate is trivially available again; the design question that kept it out was
never "can we store it" but "how does a cache coexist with replication without becoming a
replicated, coherence-bearing thing it must not be." This feature answers that:

> **The cache lives in its own RocksDB database, is never replicated, is bounded by TTL, and
> is wiped whenever mastership moves (an epoch change). It is broker-local: after a failover
> the new master's cache is cold, a miss is one more trip to the backing store, and that is
> the entire contract.**

Everything below protects that framing — a cache that is honest about being a cache.

## Requirements

### Requirement 1: Opt-in, and off by default

**User Story:** As an operator, I want the cache to exist only when I ask for it, so a broker
that does not need one carries no cache surface, store, or config weight.

#### Acceptance Criteria

1. A new `HighwayServerOptions.Cache` section with `Enabled` defaulting to **false**. A broker
   with the cache disabled serves no cache commands and opens no cache store — byte-identical
   to today.
2. When enabled, the cache is configured entirely from `highway.json` (`server.cache.*`), with
   the host schema binding 1:1 (031 R2.1), exercised by `SchemaCompletenessTests`.
3. Enabling the cache never changes any core-broker behaviour, wire shape, or the replicated
   data path.

### Requirement 2: A separate, never-replicated store

**User Story:** As a maintainer, I want it structurally impossible for cache data to enter
replication, so no discipline-on-every-write is required and a cache can never fill a
replica's disk or compete with real data for WAL bandwidth.

#### Acceptance Criteria

1. The cache is a **separate store**, behind an `IHighwayCacheStore` seam
   (`Get`/`Set`-with-ttl/`Remove`/`Clear`), with a durable implementation (its **own**
   RocksDB database at a path beside — not inside — the broker's data directory) and an
   in-memory implementation (used when the broker is ephemeral).
2. The cache database is **never handed to the `ReplicationSource`** and its writes never
   reach the broker's replicated WAL. This is proven structurally: the cache store holds no
   reference to the replicated store, and the WAL feeder holds no reference to the cache
   store. (RocksDB has one WAL per database; a *separate database* is the only way to be
   certain a write cannot ship — a separate column family in the broker DB would still ship.)
3. On a broker restart the cache may come back cold or partially warm (whatever survived a
   clean flush) — either is correct. Nothing in the broker's correctness depends on cache
   contents.

### Requirement 3: The `IDistributedCache` client adapter

**User Story:** As a service author, I want the same `IDistributedCache` I had before, so my
application code is unchanged and `HybridCache` gets an L2 for free.

#### Acceptance Criteria

1. `Highway.Client` re-exposes a cache adapter implementing `Microsoft.Extensions.Caching.
   Distributed.IDistributedCache` (get/set/remove, sync and async, with the expiration
   options the interface carries), registered by an `AddHighwayCache(...)` DI extension over
   the existing Highway connection (it shares the connection/multiplexer, not a second one).
2. Because .NET's `HybridCache` uses any `IDistributedCache` as its L2, `HybridCache` works on
   top of this with no Highway-specific code — asserted by a smoke test.
3. The adapter follows the herd client: it rides the same connection, so a cache call during a
   failover behaves like any other call (it re-drives to the new master, where it is a miss).
4. `Highway.Client` gains this **without** violating any frozen-client rule — 037's freeze
   applied to the 037 train; this is a new, opt-in, additive capability in its own feature.

### Requirement 4: The wire surface — routing, not new protocol

**User Story:** As a protocol maintainer, I want the cache to add the smallest possible wire
surface, so the `HW.*` command set and its conformance guarantees are untouched.

#### Acceptance Criteria

1. The cache reuses the **existing stock RESP commands** the raw-key surface already serves —
   `SET` / `GET` / `DEL` / `UNLINK` / `SETEX` / `PSETEX` / `TTL` / `PTTL` — extended to accept
   the `hw:cache:*` key prefix, **routed** to the cache store. No new `HW.*` command is added;
   `ProtocolConformanceTests` (which checks `HW.*` names/arities) is unaffected.
2. The three raw-key families are then: `hw:rep:*` (reply slots), `hw:idem:*` (idempotency),
   `hw:cache:*` (cache) — each routed to its own store; every other key is still refused with
   `ERR HW_INVALID_ARG`.
3. `HIGHWAY-PROTOCOL.md`'s served-subset section (the one permitted client-surface doc) gains
   the `hw:cache:*` prefix on those commands — protocol prose only; no `HW.*` change.
4. On a **non-master** (replica/fenced/demoted/draining), cache **writes** are refused with
   `-NOTPRIMARY` exactly like the other raw-key writes (042-1 C9.3) — the cache is the
   master's; a client pointed at a standby re-drives. Cache **reads** on a non-master return a
   miss (an honest answer; a standby's cache is cold/irrelevant).

### Requirement 5: TTL policy — nothing lives forever

**User Story:** As an operator, I want every cache entry bounded in time, so a cache cannot
silently accumulate stale data or unbounded size.

#### Acceptance Criteria

1. A **default TTL** (`server.cache.defaultTtl`) is applied to any entry whose caller supplied
   no expiration. A **max TTL** (`server.cache.maxTtl`) caps any caller-supplied expiration —
   a request for a longer (or absent) lifetime is clamped to the max.
2. Expiry reuses the existing value framing and filter-on-read (`ExpiryFraming` + the sweep,
   as idempotency keys use): an expired entry reads as a miss and is swept.
3. Expiration is enforced on read regardless of the sweep, so a lapsed entry is never served.

### Requirement 6: Epoch-triggered invalidation — the coherence rule

**User Story:** As a service author, I want the cache to never serve data that went stale
because mastership moved, so a failover or partition-heal can't hand me a value another node
mutated while this node wasn't in charge.

#### Acceptance Criteria

1. The cache is **cleared whenever the node's epoch changes** — on promotion, and on adopting
   a higher epoch (demotion/heal). Rationale, recorded: an epoch change is exactly the
   condition "another node may have written the underlying data since this node last owned
   it," so its cached copies are suspect. A node that *keeps* mastership through a partition
   (peers gone, herd retained) does **not** change epoch and correctly does **not** wipe.
2. The wipe is automatic and server-side (a hook on the replication feeder's epoch
   transitions); no client action, no protocol message.
3. This is the precise meaning of "the cache is invalidated on partition/failover": it is a
   consequence of the epoch rule, not a separate mechanism.

### Requirement 7: Bounded size — a cache cannot fill the disk

**User Story:** As an operator, I want the cache's disk use bounded, so an enabled cache is
never the reason a broker runs out of space.

#### Acceptance Criteria

1. A `server.cache.maxSizeBytes` soft cap. Size is primarily bounded by TTL (turnover); the
   cap is the backstop. When the cache store exceeds the cap, the sweep first deletes expired
   entries, and if still over, **clears the cache** (safe — every entry is regenerable) and
   records a named event. It never blocks, never fills the disk, and self-heals (a cold cache
   repopulates on demand).
2. There is **no LRU / hard per-key eviction in v1** — this is stated as a non-goal, not a
   silent gap; a request for LRU reopens this feature.

### Requirement 8: The record

**User Story:** As a maintainer, I want the shipped semantics written down so no one mistakes
this for a replicated or consistent cache.

#### Acceptance Criteria

1. `constraints.md` gains numbered entries: the cache is **broker-local and not replicated**;
   **cold after a failover** (a post-failover cache-miss burst on the backing store is the
   documented, accepted cost); **TTL-bounded and epoch-invalidated**; **best-effort — a miss
   is normal and callers must treat it so**.
2. `product.md`'s cache line (currently "removed, breaking") is amended with a dated pointer to
   this feature; `roadmap.md` gains the row. The 041 cache-removal note gets a dated "succeeded
   by 044" pointer (no history rewrite).
3. `HIGHWAY-PROTOCOL.md` served-subset section updated (R4.3).

## Non-Goals

- **Replicating the cache**, or any cross-node cache consistency. It is per-node by design.
- **Surviving a failover warm** — the new master's cache is cold; that is the contract.
- **LRU / hard size eviction** in v1 (R7.2) — TTL + a clear-on-cap backstop only.
- **A general keyspace** — only `hw:cache:*`, only via the routed stock commands.
- **Using the cache as a store** — it is best-effort; nothing durable or authoritative belongs
  in it.

## Open decisions

| | Question | Note |
|---|---|---|
| **OD1** | Durable cache path: fixed `dataDir/cache`, or a separate configurable `cachePath`? | **Closed 2026-09-16: fixed `dataDir/cache` by convention, no setting.** Keeps the surface minimal; a separate-disk cache path can reopen this if a deployment needs it. |
| **OD2** | Over-cap behaviour: clear-all (R7.1) vs. refuse-new-writes until TTL frees space | Design leans clear-all (self-heals, matches the failover cold-cache path); refuse-new is gentler on the backing store but leaves the cache wedged-full. Revisit if the clear burst is a problem. |
| **OD3** | Should an ephemeral (in-memory) broker offer the cache at all, or require a data dir? | Design: yes, an in-memory cache store — consistent "enabled means enabled". Reopen if the RAM bound is a surprise. |
