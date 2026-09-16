# Feature 044 — Broker-Local Cache: Design

*Implements [`requirements.md`](requirements.md). The hard question this feature had to answer
was isolation from replication; the answer (a separate database) also makes every other piece
small. Read the requirements first — the mechanics here serve them.*

## The one idea

> A cache that cannot enter replication, cannot outlive mastership, and cannot fill the disk —
> because each of those is structural, not a discipline.

- **Cannot enter replication** — it is its own RocksDB database; the WAL feeder never sees it.
- **Cannot outlive mastership** — it is wiped on every epoch change.
- **Cannot fill the disk** — TTL turnover plus a clear-on-cap backstop.

Everything else (the client adapter, the wire routing) is ordinary plumbing.

## Components

```
Highway.Server
├── Storage/Cache/IHighwayCacheStore.cs   ← seam: Get / Set(value,ttl) / Remove / Clear / SizeBytes
├── Storage/Cache/RocksDbCacheStore.cs     ← own RocksDb at dataDir/cache; ExpiryFraming values
├── Storage/Cache/InMemoryCacheStore.cs    ← ConcurrentDictionary + expiry; ephemeral brokers
├── Storage/Cache/CacheSweeper.cs          ← periodic: drop expired; enforce maxSizeBytes
├── Resp/RespSession.cs                    ← route hw:cache:* on SET/GET/DEL/SETEX/TTL/…
├── Resp/RespServer.cs                     ← construct the cache store; wire the epoch-wipe hook
├── Storage/Rocks/ReplicationFeeder.cs     ← + OnEpochChanged event (fired by promote/adopt)
└── HighwayServerOptions.cs                ← + CacheOptions

Highway.Client
├── Caching/HighwayCache.cs                ← IDistributedCache over the shared connection
└── Caching/ServiceCollectionExtensions.cs ← AddHighwayCache(...)
```

## Isolation from replication (R2) — why a separate database

RocksDB keeps **one WAL per database**, shared by every column family, and
`ReplicationSource.GetUpdatesSince` ships that whole WAL. So a cache *column family in the
broker database would still replicate*. The only way a write is structurally incapable of
shipping is to put it in a **different database** whose WAL is never fed to the source. Hence
`RocksDbCacheStore` opens its own `RocksDb` at `dataDir/cache` (OD1), entirely separate from
`RocksDbStore`. The `ReplicationFeeder` is constructed over the broker DB only and never learns
the cache DB exists. Proven the way 042 proved the recorder holds no store reference: a type
test asserts the cache store and the feeder share no reference.

`RocksDbCacheStore` is deliberately **not** a `RocksDbStore` — it needs none of the
key-family/column-family machinery, the sync-per-commit WriteOptions, or the replication
feeder. It is a thin K/V: `Get(key)`, `Set(key, value, ttl)`, `Remove(key)`, `Clear()`,
`SizeBytes()`. Cache writes may even use `disableWAL` within the cache DB (a cache need not be
crash-durable), keeping cache churn out of *its own* WAL too — a small efficiency, decided at
implementation.

## The wire: routing by prefix (R4)

The raw-key handlers in `RespSession` already branch by prefix (`hw:rep:` → reply slots,
`hw:idem:` → idempotency). Cache adds a third branch:

| Command | `hw:rep:*` | `hw:idem:*` | `hw:cache:*` (new) |
|---|---|---|---|
| `GET` | read reply slot | read idempotency marker | read cache |
| `SET [PX\|EX] [NX]` | — | claim marker | **cache set** (TTL from PX/EX, clamped to maxTtl; defaultTtl if none) |
| `SETEX`/`PSETEX` | — | record marker | **cache set** with the given TTL (clamped) |
| `DEL`/`UNLINK` | clear slot | release marker | **cache remove** |
| `TTL`/`PTTL` | — | marker TTL | **cache entry TTL** |

This is *routing*, not filtering — a one-branch dispatch decision, no shared-keyspace carving.
Any key outside the three families is still `ERR HW_INVALID_ARG`.

**Non-master gating (R4.4):** the existing `EnsureWritable` gate on the raw write handlers
(042-1 C9.3) already covers `hw:cache:*` writes — a non-master refuses them with `-NOTPRIMARY`.
Cache *reads* on a non-master are allowed and simply miss (a cold/irrelevant standby cache).

The client's `HighwayCache` adapter therefore issues, e.g.,
`SET hw:cache:{key} {bytes} PX {ttlMs}` / `GET hw:cache:{key}` / `DEL hw:cache:{key}` — the
same shapes an `IDistributedCache`-over-RESP adapter always used. *(Considered and rejected:
new `HW.CACHE.*` commands — they would enlarge the `HW.*` conformance surface for no gain over
prefix-routing the stock commands, which is exactly how idempotency already works.)*

## TTL (R5)

Values are stored with the existing `ExpiryFraming` (`[0x01][8B expiry BE][value]`) that
idempotency SetEx already uses, so read-side expiry and the sweep are reuse, not new code.

- **defaultTtl**: applied when the caller supplied no expiration.
- **maxTtl**: every expiration (caller-supplied or default) is clamped to this — nothing is
  cached longer than the operator allows.
- A lapsed entry reads as a miss even before the sweep reaches it (filter-on-read), so TTL is
  authoritative on the read path.

## Epoch-triggered invalidation (R6) — the coherence rule

The `ReplicationFeeder` already owns every mastership transition. It gains:

```csharp
public event Action? OnEpochChanged;   // fired inside TryPromote (epoch++) and ObserveHigherEpoch (adopt)
```

`RespServer` wires it: `feeder.OnEpochChanged += () => cacheStore.Clear();`

**Correctness argument (recorded):** the cache on a node is coherent with that node's data as
long as the node is the sole writer — i.e. as long as it is continuously the master. The only
way a cached value becomes stale is if *another* node wrote the underlying data while this node
was not master. That window opens and closes exactly across a mastership change, and a
mastership change is exactly an **epoch bump**. So clearing on epoch change invalidates
precisely the entries that could be stale, and nothing else:

- **Promotion** (a standby becomes master, epoch++): its cache was populated while it was a
  replica against a *different* master's data — clear it.
- **Adopt-higher-epoch** (this node demotes/heals): another node is/was master — clear it.
- **Master holds its herd through a peer-only partition**: no promotion, **no epoch change**,
  so its cache (still coherent, it never stopped being the sole writer) is **kept**. This is
  the case where a naive "wipe on any partition" would be wrong, and the epoch trigger is
  right.

This is why R6.3 states the epoch rule *is* "invalidate on failover/partition" — the same
thing, made precise.

## Bounded size (R7)

`CacheSweeper` runs periodically (`server.cache.sweepInterval`):

1. Delete expired entries (TTL turnover — the primary bound).
2. If `SizeBytes()` still exceeds `maxSizeBytes`, **`Clear()`** the cache and record a
   `cache-cleared-oversize` event. Safe because every entry is regenerable; self-heals as the
   cache repopulates on demand. (OD2: clear-all vs. refuse-new — clear-all chosen to match the
   failover cold-cache path and avoid a wedged-full cache.)

No LRU in v1 (R7.2, a stated non-goal).

## Config (R1)

```
server.cache:
  enabled: false            # opt-in
  defaultTtl: 1.00:00:00    # 24h — applied when the caller gives none (decided 2026-09-16)
  maxTtl: 7.00:00:00        # 7d — caps any expiration; must be ≥ defaultTtl
  maxSizeBytes: 256 MiB     # soft cap → clear-on-exceed
  sweepInterval: 00:00:30
  # path: dataDir/cache (OD1; a separate cachePath is a possible knob)
```

Default TTL is **24 hours** (owner's call, 2026-09-16): a cache entry with no caller-supplied
expiration lives a day, then turns over. The max cap sits above it (7d) so a caller who knows
better can ask for longer, up to the cap; a caller asking for less gets exactly what they ask.
The validation invariant is `0 < defaultTtl ≤ maxTtl` (T2).

`CacheOptions` on `HighwayServerOptions`, bound in the host schema (031 R2.1), schema-tested.

## Ephemeral brokers (OD3)

When the broker has no data directory (in-memory), the cache — if enabled — uses
`InMemoryCacheStore` (a `ConcurrentDictionary` with the same expiry framing and the same
epoch-wipe / size-cap behaviour). "Enabled means enabled," bounded by RAM.

## Client adapter (R3)

`HighwayCache : IDistributedCache` over the shared `IHighwayConnection`:

- `Get`/`GetAsync` → `GET hw:cache:{prefix}{key}` → bytes or null (miss).
- `Set`/`SetAsync` → `SET hw:cache:… PX {ttl}` with the caller's `DistributedCacheEntryOptions`
  (absolute/sliding mapped to a PX; sliding refresh on read is a client-side re-SET, as the old
  026 adapter did).
- `Remove` → `DEL hw:cache:…`.
- `Refresh` → a `GET` (+ sliding re-SET) — the interface's touch.
- `AddHighwayCache(services, configure)` registers it as the `IDistributedCache` singleton over
  the existing Highway connection; `HybridCache` (if the app adds it) picks it up as L2 with no
  Highway code (R3.2).

Because it rides the herd connection, a cache op mid-failover re-drives to the new master and
is a miss there — no special handling.

## The cold-cache burst (documented, not solved)

At the instant the herd lands on a new master, every cache key misses at once → a burst of
backing-store traffic while it repopulates. For most workloads a brief spike; for a very
cache-heavy read path, worth knowing. It is the accepted cost of a per-node, epoch-invalidated
cache and strictly safer than serving stale data. Stated in `constraints.md` (R8.1) so a
post-failover DB blip surprises no one.

## Testing strategy

| Layer | Proof | Requirement |
|---|---|---|
| Isolation | type test: cache store ↔ feeder share no reference; a cache write does not appear in `GetUpdatesSince` on the broker DB | R2.2 |
| Routing | `SET/GET/DEL/TTL` on `hw:cache:*` hit the cache store; on `hw:idem:*`/`hw:rep:*` unchanged; other keys refused | R4.1/R4.2 |
| Non-master | cache write on a replica → `-NOTPRIMARY`; cache read → miss | R4.4 |
| TTL | default applied; caller TTL clamped to max; lapsed entry reads as miss pre-sweep | R5 |
| Epoch-wipe | promote clears the cache; adopt-higher-epoch clears it; a herd-holding master through a peer partition does NOT (epoch unchanged) | R6 |
| Size cap | over `maxSizeBytes` → sweep, then clear, with the named event; never grows unbounded | R7.1 |
| Client adapter | `IDistributedCache` get/set/remove round-trip against an embedded broker; `HybridCache` L2 smoke test | R3 |
| Ephemeral | in-memory cache store when the broker has no data dir | OD3 |
| Docs | served-subset section updated; `ProtocolConformanceTests` still green (no `HW.*` change) | R4.3 |
