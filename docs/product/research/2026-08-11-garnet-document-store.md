# Research: Stow — A Document Store on Garnet

**Date:** 2026-08-11 (named `Stow` 2026-08-12, D25 — `DOC.*` in this document is a
placeholder for the real `ST.*` command prefix)
**Status:** Exploratory — this is a sibling-project investigation, not a Highway feature. Highway is unaffected.
**Question:** Can Garnet carry a Marten/YesSQL-class document database for .NET — repository API, no SQL, targeting small-to-medium apps that don't want full MongoDB? What would it cost, and can Garnet handle 5–10 GB of data?

*Consolidated at the end of the same day's research session from its running addenda.
Superseded decisions are kept in the registers (Appendix A/B) with their supersession
noted, not deleted.*

**Verdicts at a glance:**

| Question | Verdict |
|---|---|
| Feasibility | **Yes** — YesSQL's model on Garnet's engine |
| 5–10 GB of data | **Yes**, with one design rule (documents in the main store) |
| Cluster deployment | **Yes** — placement/replication/failover, not intra-collection sharding |
| ACID scope | Per document + its derived state; nothing spans two documents |
| Client model | Mongo-shaped API, sessionless, on a multiplexed RESP connection |
| Databases & collections | First-class, as key-grammar namespaces — cluster-safe by construction |
| Serialization | UTF-8 JSON default behind a pluggable serializer; not BSON |
| Effort to credible v1 | **≈ 4–6 months solo** |
| Architectural template | **YesSQL, not Marten** |

---

## Part I · The landscape

The "Critter Stack" is the right mental model, and the analogy holds up cleanly:

| Role | Critter Stack | Garnet stack |
|---|---|---|
| Storage engine | PostgreSQL (JSONB) | **Garnet** (Tsavorite hybrid log) |
| Document + event store | Marten | **This project** (the gap to fill) |
| Messaging / command bus | Wolverine | **Highway** (already ~v1) |

**Marten** gets its document powers from PostgreSQL: JSONB storage, LINQ translated to SQL over JSONB operators, computed indexes, GIN indexes, full-text search, and real multi-statement transactions. Its `IDocumentSession` / `IQuerySession` unit-of-work API is the gold standard for developer experience. Crucially, *Postgres does the querying* — Marten is a translator, not a query engine.

**YesSQL** is the more interesting reference, because it solves the problem we actually have. It stores every document as an *opaque blob* in one table and refuses to query inside it. Instead, you declare indexes as plain C# classes (`MapIndex` for per-document projections, `ReduceIndex` for aggregations) via `IIndexProvider`; each index becomes its own table, maintained in the same transaction as the document write; queries run against index tables only. No query planner, no LINQ-to-storage translation over arbitrary fields — and it powers Orchard Core in production, so the model is proven at "small-to-medium app" scale.

> **The key insight:** Marten's feature set requires a SQL engine underneath. YesSQL's feature set only requires *atomic multi-key writes and sorted lookups* — which is exactly what Garnet gives you (custom transactional procedures + sorted sets). Build YesSQL's architecture with Marten's API ergonomics.

## Part II · Architecture

Documents live in the **main string store** as serialized bytes (JSON by default — Part VI). Indexes live in the **object store** as sorted sets and hashes. A custom transactional procedure makes each save atomic: write the document, remove stale index entries, add new ones, bump the version — one server-side transaction ("atomicity lives server-side").

```
Client package — IDocumentStore → IDatabase → ICollection<T>
  Immediate repository-style ops (no session — Part V), typed collections,
  index declaration (attributes or IIndexProvider-style classes), assembly scanning
        │
        ▼  RESP wire — custom DOC.* commands, one multiplexed connection
┌─────────────────────────────────────────────────────────────────┐
│ Garnet server extension                                         │
│   Custom transactional procedures:                              │
│   Save = check version → write doc → fix index entries          │
│                                                                 │
│  ┌──────────────────────────┐  ┌─────────────────────────────┐  │
│  │ Main store — documents   │  │ Object store — indexes      │  │
│  │ Raw bytes in hybrid log. │  │ Sorted sets (range/sort/    │  │
│  │ DISK-TIERED              │  │ page), sets/hashes (eq,     │  │
│  │ (--storage-tier): hot in │  │ unique), reduce hashes      │  │
│  │ RAM, cold spills to SSD. │  │ (aggregates). KEPT IN RAM — │  │
│  │ This is where 5–10 GB    │  │ small by construction.      │  │
│  │ lives.                   │  │                             │  │
│  └──────────────────────────┘  └─────────────────────────────┘  │
│                                                                 │
│  Durability & feed: AOF write-ahead log + checkpoints ·         │
│  native pub/sub publishes a change event per commit             │
└─────────────────────────────────────────────────────────────────┘
```

### Feature mapping

- **Identity** — ID strategies client-side (Guid v7, HiLo via `INCR`).
- **Map indexes** — a declared field becomes a sorted set: score for numeric/date ranges, lexicographic member encoding for strings. Equality → set lookup; range → `ZRANGEBYSCORE`; sorting and paging → the sorted set *is* the sort order. String range/sort indexes use a normalized 64-byte prefix (D8); equality indexes hash the full value.
- **Reduce indexes** — incremental aggregates in hashes (group key → count/sum), updated in the same transaction with old-vs-new deltas. YesSQL's map/reduce, without the bridge tables.
- **Unique constraints** — `SETNX`-style claim keys inside the save transaction; violation fails the save.
- **Optimistic concurrency** — version in the document envelope, checked inside the transaction (D3; not Garnet's native ETag, which is unsupported in cluster mode).
- **Queries** — a typed, deliberately closed API: `Query<T>().Where(x => x.Status == "active").OrderBy(...).Skip/Take`, where `Where` only accepts indexed members and fails at startup otherwise. This looks like LINQ but is a lookup dispatcher, not a query planner — that boundary is what keeps the effort sane.
- **TTL per document** — native `EXPIRE`. Neither Marten, YesSQL, nor LiteDB has this; Mongo needs a TTL index. In v1, restricted to collections without secondary indexes (D7).
- **Change feed** — publish `{db, collection, id, op, version}` on commit via Garnet pub/sub. Mongo-change-streams-shaped, nearly free, and the natural seam where Highway later plugs in (Wolverine-style) without any coupling today.
- **Multi-tenancy** — tenant segment in the key namespace (Part IV); `{tenant}` as the distribution unit co-locates a tenant's data (Part VII).

### The index menu: what Garnet's full structure set buys

YesSQL's substrate (relational tables) gives it only what SQL does with B-trees. Garnet's
native structures map to a richer index menu — several entries below are things YesSQL
punts to SQL for, and a few beat Mongo's equivalents:

| Capability | Backing structure | Query API | Tier |
|---|---|---|---|
| Primary index (automatic) | Sorted set of ids | `GetAll`, paged scan, **O(1) `Count()`** (`ZCARD`), `Drop` enumeration | v1 |
| Equality | Set per (field, value) | `Where(x => x.F == v)`, O(1) per-value count | v1 |
| Range & sort | Sorted set (score = number/date; lex strings per D8) | `<` `>` `Between`, `OrderBy` + token paging, `Min`/`Max` | v1 |
| Unique | Hash value → id | Constraint **and** O(1) `GetByEmail`-style lookup | v1 |
| Multikey (arrays) | Set per element | `Tags.Contains(v)`; **AND via `SINTER`, OR via `SUNION`**, counts via `SINTERCARD` — faceted filters as a primitive (Mongo needs index intersection) | v1 contains; ph3 algebra |
| Distinct values | Index keyspace | `Distinct(x => x.F)` without touching documents | v1 |
| Compound | Lex sorted set, concatenated normalized values | Equality-prefix + one range/sort tail — Mongo's ESR rule, enforced at declaration | ph2–3 |
| Reduce (aggregates) | Hash group → count/sum (`HINCRBY`, incremental in the save txn) + sorted set ranked by aggregate | `GroupBy` on declared key: `Count`/`Sum`/`Avg`; **Top-N groups** as O(log n) read | ph3 |
| Existence / sparse | Set of ids where field present/null | Soft-delete filters (`DeletedAt == null`) | ph3 |
| Geo | Geo (geohash sorted set), `GEOSEARCH` | `Near(point, radius)`, box queries — Marten needs PostGIS; LiteDB/YesSQL have nothing | ph3 |
| Approx distinct | HyperLogLog | `ApproxDistinct(x => x.F)` — ~12 KB fixed memory per counter | optional |
| Nested-path multikey | Set per emitted child value (emit-many) | Mongo dotted paths (`items.productId`) via `Emit(o) => o.Items.Select(...)`; `[IndexedPath]` sugar possible | ph2 |
| Element-level compound | Per-key sorted set of element composites (key + score from the *same* element) | The `$elemMatch` answer for **declared** combos — same-element correlation at index speed (Mongo's multikey can't); e.g. `Where<ItemsByProductQty>(x => x.Key == sku && x.Score > 2)` | ph3 |
| Array length | Computed count → range index | `$size`: `Where(x => x.Tags.Count > 3)` | ph3 |
| Dictionary / facet | Set per `key=value` composite | `Attributes["color"] == "red"`; faceted navigation AND/OR via `SINTER`/`SUNION` — dynamic-attribute catalogs (awkward in Mongo, impossible in LiteDB) | ph3 |
| Basic text | Inverted sets + lex prefix | Word-AND/OR, `StartsWith` — explicitly **not** RediSearch (no stemming/ranking); anything beyond this is a change-feed sidecar, never embedded Lucene (D16) | later |
| Vector | DiskANN (preview) | `NearestNeighbors(embedding, k)` — .NET-native semantic search | roadmap |
| Native range index | Bf-Tree (preview) | Could replace sorted-set indexes → would relieve C2 (indexes-fit-RAM) | roadmap |

**Composition rule:** v1 answers a query from *one* declared index. Phase 3 adds *bounded*
set algebra — AND/OR across set-shaped operands via `SINTER`/`SUNION`/`ZRANGESTORE` inside
a server procedure with cardinality caps. Never: regex, joins, arbitrary predicate trees,
unbounded NOT. The closed-surface philosophy (C1) is unchanged; the surface itself is
larger than YesSQL's.

### Map/Reduce: YesSQL's mechanism vs ours

YesSQL's `ReduceIndex` pipeline is Map (documents emit index rows with a group key) →
Group → Reduce (rows aggregate into one stored row per group), with bridge tables
recording which documents contributed where, so updates can retract an old contribution
and fold in the new one transactionally.

Ours collapses to **contribution accounting**: *each document deposits contributions into
its group; when the document changes, the delta moves.* The declaration states only what
one document contributes:

```csharp
public class OrdersPerCustomer : IReduceIndex<Order>
{
    public string GroupKey(Order o) => o.CustomerId;
    public void Contribute(Order o, Contribution c)
    {
        c.Count();
        c.Sum(x => x.Total);   // Avg free: Sum/Count
    }
}
```

Mechanics, Garnet-native and O(1) per save: group aggregates live in a hash
(`HINCRBY`/`HINCRBYFLOAT`) updated inside the save transaction (D10); the document's
previous contributions are already in its server-side membership record (Part II), so an
update applies `new − old` as one delta and a delete subtracts. No bridge tables, no group
re-reduction. A `ZINCRBY`-maintained sorted set keyed by the aggregate provides Top-N
groups as a native read. Query surface mirrors the declaration:
`db.Aggregate<OrdersPerCustomer>().GetAsync(key)` (O(1)),
`.TopAsync(10, x => x.Sum)`, paged group enumeration.

**In SQL terms, every reduce index is a pre-aggregated, incrementally-maintained
materialized view, and the save procedure is its trigger** — better-behaved than SQL
triggers because triggers stay *system-owned*: the server never executes user-supplied
write-time code (that would be a stored-procedure surface — security, versioning, and
latency-inside-a-lock hazards). User-facing reactivity happens one hop later via
`WatchAsync` (C19 semantics).

**Where each aggregate comes from — and what's free:**

| Aggregate | Collection-wide (indexed field) | Per-group (reduce index) |
|---|---|---|
| Count | Free — `ZCARD` of primary/field index | Counter, O(1) |
| Sum / Avg | Tiny counter pair via `[Indexed(Stats = true)]` → O(1) `SumAsync`/`AvgAsync` | Counters, O(1) |
| Min / Max | **Free — the ends of the range index** (`ZRANGE idx 0 0`), delete-safe since the index holds all remaining values | Opt-in per-group contribution sorted set (C20, memory cost) |

### Collation and language support (D22 expanded)

Storing any language is free — documents are UTF-8 JSON, full Unicode. Collation is
purely an *index-entry encoding* question, answered in three tiers:

1. **Default: ordinal, culture-invariant.** Deterministic, fast, stable across OS and
   .NET versions, forever.
2. **Folding tier (the workhorse): our own version-stable pipeline** — Unicode NFD →
   strip combining marks (accent-insensitive) → invariant uppercase (case-insensitive) →
   optional natural-numeric encoding (`"file2" < "file10"`). Declared per index:
   `[Indexed(CaseInsensitive = true, AccentInsensitive = true, NaturalSort = true)]`,
   applied to the index entry only. Because we own the folding tables, an index built
   today matches a lookup computed years later. Covers ~95% of applications.
3. **Culture tier: true collation via `CompareInfo.GetSortKey()`** —
   `[Indexed(Culture = "sv-SE")]` stores culture sort-key bytes as the sorted-set member
   (byte order = collation order). Hazard, well known from PostgreSQL's glibc/ICU
   corruption history: **sort keys are ICU-version-dependent, and ICU changes with the
   OS.** Mitigation: record `CompareInfo.Version` in the index catalog at build time,
   compare at startup, flag mismatches for rebuild (D21 machinery). Sort keys run 2–4× the
   string length — the D8 prefix budget widens for collated indexes.

Per-*query* collation is deliberately out: collation is an index property (Mongo cannot
use an index under a mismatched collation either — the closed model loses nothing).
Language-aware stemming remains out of core per D16 — that is the search sidecar's job.

### Index maintenance — resolved

Garnet transactions must declare their key set up front (locks are acquired before
execution), and which index entries to *remove* depends on the previous state of the
document. Early in this research two designs competed: (a) the client computes old and new
index entries and retries on version conflict, or (b) the server stores each document's
current index-membership list beside the document and cleans stale entries itself.

**The sessionless client model (D11) forced the choice to (b), and it is the better
design:** the client never needs the old document, a whole class of read-compute-retry
races disappears, and the cost is a small membership list stored per document. The Phase 0
spike prototypes only this variant.

### Index lifecycle (D28): every build is online, by design

There is no `Background` option — backgrounding is a property of the engine, not a choice
the developer remembers to make (Mongo itself deprecated `background: true` in 4.2 when
all builds became hybrid). Lifecycle, tracked in the catalog with a progress token:
**Declared → Building → Ready | Failed.** From the moment of declaration two things run
in parallel:

1. **New writes maintain the index immediately** — the save procedure updates all
   registered indexes including Building ones, so everything written after declaration is
   correctly indexed from second one.
2. **Backfill walks existing documents in the background** — batched over the primary
   index, token-resumable (the D21 migration-runner machinery, paying for itself twice),
   throttled to never starve live traffic.

Semantics: querying a Building index **fails fast** with progress ("index building, 43%")
rather than returning silently incomplete results; progress is visible in `ST.STATS` and
the dashboard (D19/D20). **Unique backfill fails honestly**: existing duplicates end the
build Failed with offending ids reported, while new writes enforce the claim from
declaration time. **`Hidden` is supported** — maintained but ignored for query resolution
(one catalog flag), the safe decommission tool paired with D19 usage counters: hide,
watch, then drop with confidence. Mongo-style TTL indexes (`expireAfterSeconds` on a date
field) are deliberately not copied — D7's native `EXPIRE` covers save-time TTL, and the
phase-3 sweeper generalizes to date-field expiry via a cheap `ZRANGEBYSCORE` over the
range index.

### Performance discipline: the allocation-free server hot path (D26)

Garnet's own onboarding states that Tsavorite and Garnet "rely heavily" on `Span` and
`SpanByte` "for allocating data in memory and then transferring it on the network layer."
Stow's server extension runs *inside* that hot path, where the enemy is GC pressure — a
Gen0 allocation per operation at high ops/sec becomes collection pauses that destroy the
tail latency Garnet exists to protect. The standing rule:

**Server hot path is allocation-free: `SpanByte` in, `SpanByte` out; strings exist only at
API and diagnostic boundaries.**

Concretely:

- **Never downgrade the extension API's spans to strings** — version compare, key
  composition, membership diff, counter updates are all span operations over the bytes
  the procedure was handed.
- **The envelope header earns its keep here:** the D3 version check is a span slice of a
  fixed binary prefix off the `SpanByte` — no JSON parse, no allocation, per save. This
  is the strongest single argument for the envelope design.
- **Key composition:** the catalog caches each collection's pre-encoded key prefix as
  `byte[]` once; per-op keys compose into `stackalloc`/pooled buffers. D22 folding runs
  UTF-8 span-to-span, never round-tripping through `string`.
- **The membership record is compact binary, not JSON** — it is diffed on every save;
  the diff must be a span walk.
- **The architecture already aligns (D15):** index-entry computation is client-side, so
  allocation/CPU-heavy work lives in horizontally-scaling client processes; the server —
  the single choke point — stays a byte-shuffler. Client ergonomics may allocate; the
  client serializer still uses `Utf8JsonWriter` into pooled buffers and
  `Guid.TryFormat`-style span formatting.
- **Enforced, not aspirational:** the Phase 0 spike measures allocations per operation
  (BenchmarkDotNet `MemoryDiagnoser`) with a stated target of **zero server-side heap
  allocations on steady-state save/get paths**, kept as a CI regression benchmark. No
  cargo-cult `unsafe` — spans first, profiler-justified `unsafe` only.

Reference: Garnet dev onboarding + `libs/storage/Tsavorite/.../VarLen/SpanByte.cs`.

## Part III · What we'd have — honestly

| | This (Garnet) | Marten | YesSQL | LiteDB | MongoDB |
|---|---|---|---|---|---|
| Typed documents, identity, immediate repository API | Yes | Yes (session) | Yes (session) | Partial | Yes |
| Queries on *declared indexed* fields (eq, range, sort, page) | Yes | Yes | Yes | Yes | Yes |
| Ad-hoc queries on *any* field | No — by design | Yes (JSONB) | No — by design | Yes | Yes |
| Map/reduce aggregation indexes | Yes (phase 3) | Projections | Yes | No | Pipeline |
| Unique constraints · optimistic concurrency | Yes · envelope version | Yes | Partial | Partial | Yes |
| Per-document TTL | **Native** | No | No | No | TTL index |
| Change feed / subscriptions | **Native pub/sub** | Via events | No | No | Change streams |
| Named databases above collections | Yes (key namespace) | Schemas | No | One file | Yes |
| Embedded in-process (LiteDB-style) | **Yes** | No | SQLite | Yes | No |
| Standalone server, many clients | Yes | Yes | Yes | No | Yes |
| Doubles as Redis-compatible cache | **Uniquely yes** | No | No | No | No |
| Full-text search | Later | PG FTS | Via SQL | No | Atlas |
| SQL reporting / joins over your data | No | It's Postgres | It's SQL | No | No |
| Event sourcing + projections | Phase 4 | Yes, mature | No | No | No |
| Multi-document transactions | No — by design | Yes | Yes | Partial | Yes (4.0+) |

The pitch that makes this *mainstream-able* rather than a curiosity: **one .NET-native binary that starts embedded like LiteDB and graduates to a server like Mongo — and is simultaneously your Redis-compatible cache**. `dotnet add package`, zero ops, and a growth path LiteDB doesn't have and Mongo can't offer. Two Garnet previews sweeten the roadmap: the **Bf-Tree range index** could eventually replace hand-rolled sorted-set indexes, and **DiskANN vector search** would give a small-app semantic search story Marten needs pgvector for.

What we must not promise: ad-hoc queries over non-indexed fields, aggregation pipelines, joins, SQL-side reporting, and multi-document transactions. YesSQL proves (via Orchard Core) that a closed, index-declared query surface is enough for real applications — but it has to be stated as a design position, loudly, or every third GitHub issue will be "why can't I query by X."

## Part IV · Data model: collections, databases, keys

**The convention model follows Highway (D17): plain C# objects with attributes drive
everything, discovered by assembly scanning at startup.** A document is a POCO — no base
class, no interface, no fluent mapping registry required. Attributes carry the metadata:
`[Collection("orders")]` (optional; type name by convention otherwise), `[Id]` (or the
`Id` property by convention), `[Indexed]` for map indexes, `[Unique]`, `[Compound(...)]`,
`[ExpireAfter]`-style TTL defaults, and reduce-index declarations as plain classes
(YesSQL's `IIndexProvider` shape) for the aggregate cases attributes can't express.
Startup scanning registers collections and index declarations into the server catalog and
validates queries against them (C1's fail-at-startup rule). Same DX as Highway's four
class shapes: write a class, add an attribute, it works.

**The index-declaration grammar (D17 refinement):** attributes and YesSQL-style classes
are formally equivalent — both compile to the same catalog entry, both maintained in the
same save transaction (D10). The rule for which to use: **attributes for stored values,
classes for computed values and aggregations.**

- `[Indexed]` on a property — the 90% case (a YesSQL `MapIndex` whose `Map()` projects one
  property). Name auto-derived as `collection/property`; an explicit name
  (`[Indexed("by_fullname")]`) is optional, diagnostics-only — **queries never reference
  an index by name**, they reference the property, and the catalog resolves the index. No
  `idx_01_*` naming conventions needed; that habit exists for SQL hints, which we don't have.
- `[Unique]`, `[CompoundIndex("status_created", nameof(Status), nameof(CreatedAt))]`
  (class level, ESR order explicit) — same story.
- `IMapIndex<T>` class with an `Emit(T)` method — when the indexed value is *computed*
  (e.g. `"{Last}, {First}".ToLowerInvariant()`); attributes can't express code.
  **`Emit` returns `IEnumerable` — emit-many per document is the single generalization
  that yields all array support** (YesSQL's `MapIndex` works the same way): scalar
  multikey, nested dotted paths, element-level compounds (the `$elemMatch` equivalent for
  declared combos), array length (`$size`), and dictionary/facet indexes are all just
  emit-many shapes, cleaned up on update/delete by the same server-side membership-delta
  mechanics. Mongo's array *update* operators (`$push`/`$pull`, positional `$`) are
  explicitly not this — they are server-side patching, the D15 vNext lever; v1 is
  read-modify-save.
- `IReduceIndex<T>` class with group key + deltas — YesSQL's `ReduceIndex`, aggregation
  across documents, incrementally maintained.

**Identity (D27): `Id` + UUIDv7 — Mongo's concept, not Mongo's artifact.** The property is
`Id` by convention (or `[Id]`) — never `_id`, a BSON/JavaScript-world habit alien to C#.
Default id type is **`Guid` via `Guid.CreateVersion7()`** (RFC 9562 UUIDv7):
auto-generated client-side on insert when unset, immutable once set, always the primary
index. Every ObjectId virtue is kept with upgrades — coordination-free uniqueness,
millisecond (vs second) time-ordering so **the primary index is insertion-ordered for
free** (UUIDv7's hex string sorts correctly lexicographically), and it's a *native* .NET
type: no custom struct, serializer, or converter tax. Mongo's ObjectId algorithm is
explicitly not ported — migrating data imports existing ObjectIds as plain string ids via
D21 dump/restore. Polymorphic ids where natural keys fit: `string` (slugs), `int`/`long`
via HiLo on `INCR`. Business-key uniqueness stays orthogonal via `[Unique]` (claims inside
the same atomic save, O(1) `GetByUniqueAsync`, composable with D22 folding and composite
`(TenantId, Slug)` uniques). Spec-phase detail reserved: optional base32 encoding of the
v7 bytes (26 chars vs 36) if key size ever matters — order-preserving.

**Collections are first-class and cheap (D1).** A metadata registry describes each
collection (codec, declared indexes, caps, placement); every collection auto-creates a
**primary id index** (a sorted set) — which is what makes a collection *feel* like a
table: `GetAll`, `Count`, paged scans, and `Drop` enumeration all come from it. C# type ↔
collection by convention, `[Collection("name")]` to override. Names `[a-z0-9_.-]{1,64}`;
`{ } : /` reserved by the key grammar.

**Databases are a key-grammar namespace, Mongo-style — not Garnet `SELECT` databases
(D14).** Garnet does have multiple logical databases (`MaxDatabases`, `SELECT`, `SWAPDB` —
PR #1005), but they are the wrong primitive here: `SELECT` is connection state (conflicts
with the multiplexed client, Part V), the PR explicitly excludes `SELECT` inside
transactions (our whole write path), the Redis-protocol convention is db0-only in cluster
mode and Garnet's docs are silent on the combination, and the databases are numbered
where Mongo's are named. So the database name lives in the key:

```
d:{crm/orders}:o-123        document   (hash tag = db/collection → one slot)
sys:catalog                  registry of databases → collections
```

API parity with Mongo: `store.GetDatabase("crm").GetCollection<Order>()`, `default`
database when unspecified. The server runs db0-only. Prefixes also buy what numbered
databases can't: per-database access control via ACL key patterns, readable keys in any
Redis CLI, cross-database admin on one connection, and database-drop as registry-driven
background enumeration.

**Document size (D2):** 1 MiB default cap, configurable per collection to an 8 MiB
absolute ceiling; warn at 256 KiB. Mongo's 16 MB is not a target — large values stress
hybrid-log paging, AOF, and lock hold times; big payloads belong in blob storage with a
reference in the document. The generous-enough cap also serves the aggregate-per-document
modeling guidance (Part V).

## Part V · Consistency, atomicity, and the client model

### Atomicity is per document — full stop (D10)

A save atomically covers one document and *all its derived state*: envelope version
check, the document write, secondary-index entries, unique-constraint claims,
primary-index entry, and the change-feed event. Nothing ever spans two documents — not
within a collection, not across collections, not single-node, not clustered.

This is Mongo's (pre-4.0) model, which carried small-to-medium apps for a decade. It
gives uniform semantics across deployment modes — an earlier draft decision (D5) allowed
cross-collection saves single-node but not in cluster, and that split was a trap in
waiting; D10 superseded it. Multi-document invariants are modeled as one aggregate
document (D2's cap is generous for that) or as eventually-consistent flows over the
change feed — the seam where Highway plugs in.

`SaveManyAsync` is a pipelined bulk operation: each document atomic, per-document
results, never all-or-nothing (D6).

### No session (D11)

A unit of work whose `SaveChanges` flushes N *independent* atomic writes looks
transactional while not being it — an API that lies about semantics. Marten and YesSQL
have sessions *because* their SQL engines have multi-statement transactions; Mongo's
driver is sessionless because its atomicity is per document. We are in the second camp,
and the API should say so: repository-shaped and immediate — `GetAsync` / `SaveAsync` /
`DeleteAsync` / `Query` / `SaveManyAsync`. No identity map, no change tracking. This
decision also forced the index-maintenance design in the right direction (Part II).

### What "stateless like Mongo" actually means — and where we go further

The MongoDB C# driver is stateless at the API level (`MongoClient` singleton, cheap
collection handles, immediate self-contained operations) but stateful in its
infrastructure: a connection *pool* per server, **implicit sessions** silently drawn for
every operation (powering retryable writes and causal consistency), and **server-side
cursors** with `getMore`, lifetimes, and leak modes.

RESP's native .NET model is StackExchange.Redis's `ConnectionMultiplexer`: not a pool but
**one shared, pipelined, thread-safe connection**, which enforces command-level
statelessness — per-caller connection state (`MULTI`, `SELECT`) is impossible by
construction. Our design is exactly what makes that safe: atomicity lives server-side, so
**one command is one atomic operation** (D12). The change feed's pub/sub — RESP's one
connection-stateful feature — runs on a dedicated subscriber connection, which the
multiplexer pattern already manages.

Two places we end up *more* stateless than Mongo:

- **No implicit sessions** — single self-contained commands leave nothing for a session to do.
- **No server-side cursors (D13)** — query paging uses opaque stateless continuation
  tokens (SCAN-style; the token encodes index position). Zero server memory per reader,
  nothing to time out or leak, nothing pinned to a node during failover.

The one thing Mongo's hidden state buys that we give up: retryable writes. On connection
loss mid-save the outcome is ambiguous (C17); a retried save surfaces a version conflict
if the first attempt landed — detectable and documented — and the envelope reserves room
for an optional idempotency key later.

### The operation catalog

Every `*Many` operation is per-document atomic, never all-or-nothing (D10/D6) — the same
semantics Mongo's `updateMany` has outside a transaction.

**Writes** (each one atomic server-side procedure): `InsertAsync` (fails if exists),
`SaveAsync` (upsert, optional expected version), `UpdateAsync` (fails if missing or
version mismatch), `DeleteAsync(id[, version])`; pipelined `SaveManyAsync` /
`InsertManyAsync` / `DeleteManyAsync(ids)` with per-document results;
`UpdateManyAsync(criteria, mutate)` and `DeleteManyAsync(criteria)` as client-orchestrated
loops (index query → read-modify-write with version retry — streamed, resumable,
documented as per-document). *Not v1:* server-side partial patching (`$set`/`Patch`) —
requires server-side document parsing, which is the D15 vNext lever.

**Reads:** `GetAsync`, `GetManyAsync(ids)`, `ExistsAsync`, `GetAllAsync` (paged),
`GetByUniqueAsync` (O(1) via unique index), and the closed `Query<T>()` builder: `Where`
on indexed members (eq/range/`Contains`), `OrderBy` matching an index, token paging,
`ToListAsync` / `ToAsyncEnumerable` / `FirstOrDefaultAsync` / `AnyAsync` / `CountAsync` /
`MinAsync` / `MaxAsync` / `DistinctAsync`. Phase 3: `GroupBy` on declared reduce indexes
(`Count`/`Sum`/`Avg`), Top-N groups, `Near(point, radius)`, `ApproxDistinctAsync`.
"Get by criteria" exists exactly as `Query<T>().Where(...)` under the C1 rule: criteria
means *indexed* members, enforced at startup.

**Beyond CRUD:** `WatchAsync` (change feed per collection/database), TTL as a save option
(`ExpireAfter`, D7 scope), admin (`EnsureIndexes`/backfill, `RebuildIndex`,
`DropCollection`, stats).

**Division of labor with Garnet:** the structures and their operations are native
(`ZRANGEBYSCORE`/`ZRANGEBYLEX`, `ZCARD`/`SCARD`, `SINTER`/`SUNION`, `GEOSEARCH`,
`HINCRBY`/`ZINCRBY`, `PFCOUNT`); ours is the discipline around them — the save procedure
populates them atomically with the document, the query layer translates typed calls into
native reads. Nothing in the v1/phase-3 menu needs an engine capability Garnet doesn't
ship today; only vector (DiskANN) and Bf-Tree are preview-gated roadmap items.

### The API shape: one small `DB` object (D18)

Not `IRepository<T>`-per-entity (boilerplate that multiplies with every type), not an
EF-style context class with collection properties (a manual registry — contradicting the
scanning convention), and no `AbstractRepository` base class (inheritance couples user
code to our hierarchy and adds nothing composition doesn't). Instead, the Highway move:
**one façade, a closed verb set, the type parameter as the router** — the same way Highway
routes by message type.

`IDB` carries seven verbs — `Get`, `Insert`, `Save`, `Update`, `Delete`, `Query`, `Watch`
(`*Many` as overloads) — and everything else is a class and an attribute. Named databases
route by `[Database("crm")]` on the class or explicitly via `db.GetDatabase("crm")` (D14).
`db.Collection<T>()` returns the cheap stateless handle (D12) for anyone who wants an
injectable per-entity abstraction — the repository pattern available as a *view*, never
required. Attribute vocabulary stays `[Collection]`, not `[Table]` — "table" invites
relational expectations C1 refuses.

The pitch symmetry: *Highway is two verbs for messaging; this is seven verbs for data.*

Because indexes are declared in attributes and the client is .NET-only (D15), a Roslyn
analyzer can validate `Query<T>().Where(...)` **at build time** — a non-indexed field is a
red squiggle, not a startup error. No document database offers that; it's the closed
surface (C1) turned from a limitation into tooling.

### Observability (D19): counters, profiler, dashboard

Three tiers, modeled on Mongo's but tiered so timestamped per-op logging never doubles
write load:

1. **Always-on counters (free):** in-memory per-collection/per-verb counters + latency
   histograms (p50/p95/p99), error/version-conflict rates, and per-index stats — entry
   counts, memory estimates, **usage counters** (Mongo `$indexStats` analog → "unused
   index" advice). Exposed via `DOC.STATS`. Lives in server memory, *not* the keyspace:
   no AOF traffic, no replication, no write amplification.
2. **Profiler (opt-in, capped — Mongo's model):** in-memory ring buffer of
   `{timestamp, verb, collection, id, duration, result}`; modes off /
   slow-ops-over-N-ms / sample-1-in-N / all; queryable via `DOC.PROFILE`. Bounded memory,
   zero disk. A permanent audit trail is a different feature — a change-feed consumer —
   not the profiler.
3. **Embedded dashboard (Compass-class):** the `Highway.Server.Dashboard` pattern — live
   ops/latency charts, slow-op list, collection/db/index sizes and usage, active
   watchers. Server publishes a stats snapshot each second on a `sys:stats` pub/sub
   channel; the dashboard is just another subscriber of our own change-feed machinery,
   in the same single binary.

### The data explorer (D20): Compass parity, dogfooded

The dashboard (D19) extends into a full data explorer, with the tree: **connection**
(server/cluster info, active clients and watchers, slot ownership in cluster mode) →
**databases** (from `sys:catalog`) → **collections** (O(1) doc counts via primary-index
`ZCARD`, TTL policy, placement tag, index list with definitions/entries/memory/usage +
rebuild/backfill actions) → **records** (token-paged browsing over the primary index,
pretty-printed JSON — D9's readability paying off — single-document view/edit/delete
through the normal version-checked save path) → **higher-level contents**: reduce indexes
rendered as live aggregate tables and Top-N leaderboards (O(1) reads, pre-computed),
a live change-feed tail via `WatchAsync`, and the D19 charts/profiler.

Two properties worth designing around: **every screen is a pure client of the public
surface** (catalog, indexed queries, token paging, watch, stats — no privileged backdoor
commands), and **the filter builder can only offer indexed fields** — the UI physically
teaches C1 instead of documenting it.

Scope honestly: v1 explorer = read-everything + single-document edit/delete + live tail
(phase 3, extending the management-surface line); richer editing later. Compass took
years; we ship the read path first.

### Read semantics

Read-committed-latest, no snapshot isolation (C10): a query reads an index then fetches
documents, and a document can change in between; materialization re-checks the envelope
version. No repeatable read across calls. Stated as documented semantics, not discovered
behavior.

## Part VI · Serialization

Garnet stores opaque byte sequences — the format is entirely the client package's choice,
and the server never parses documents (indexes are computed from typed declarations, and
membership is tracked server-side). **Decision (D9): UTF-8 JSON via source-generated
System.Text.Json as the default, behind an `IDocumentSerializer` abstraction.**

- **Not BSON.** BSON optimizes for in-place traversal (length prefixes + type tags), not
  size — output is typically the same size or *larger* than JSON, and its .NET libraries
  mean taking a `MongoDB.Bson` dependency in the project positioned as the Mongo
  alternative. LiteDB uses BSON because its engine traverses documents in place; ours
  doesn't.
- **JSON's debuggability is a product feature.** `GET d:{crm/orders}:o-123` from any Redis
  CLI returns readable data — decisive for onboarding, ops, the change feed, and any
  dashboard. Marten's trust story (inspectable JSONB) is precedent.
- **If binary-compact is ever wanted: MessagePack** (contractless/string-key mode — same
  schema-evolution semantics as JSON, ~20–40% smaller, faster) as an opt-in package.
  MemoryPack is faster still but schema-brittle — wrong for long-lived documents. CBOR and
  protobuf fit poorly.
- **Compression beats format-switching** if size becomes the pressure point: LZ4/Brotli
  per document over the JSON bytes, signalled by a flag in the envelope header.
- **Every payload carries a small envelope header** (format + version + flags + concurrency
  version) designed in from day one — it is what keeps every choice above reversible and
  reserves room for the C17 idempotency key.

## Part VII · Cluster and distribution

Verified facts about Garnet's cluster module: 16,384 slots; hash tags (`{...}`) select
the hashed substring; **multi-key operations execute only when all keys map to one
slot**; replicas serve reads with writes redirected via `-MOVED`; failover is **passive**
— an external control plane (e.g. a Kubernetes operator) must detect failure and request
it; **native ETag is not supported in cluster mode** (hence D3's envelope version).

**The distribution unit is a deployment choice (D4, refined by D14):** hash-tag by
`{db/collection}` (default), `{db}` (pin a whole database to one shard), or `{tenant}`
(co-locate a tenant's data — the SaaS shape). All keys of one unit share one slot, so
every save transaction is single-slot — always cluster-legal. This is our analog of
Mongo's shard key, chosen once per deployment.

**What a cluster buys: placement, replication, and failover — not Mongo-style horizontal
scaling of one collection.** A cluster spreads *units* across primaries and adds read
replicas; it never splits a unit (C3). Note the sizing consequence: **effective write
parallelism equals the number of distribution units, not the number of nodes or slots** —
a deployment with three collections utilizes at most three primaries however large the
cluster; this is precisely why `{tenant}` placement matters for SaaS (many tenants = many
units = real horizontal spread). Garnet itself scales the raw keyspace horizontally
(slot reassignment with live key migration, replicas for reads); the unit granularity is
our transaction model's constraint, not the engine's. For the small-to-medium target that is the right
trade, and it must be said out loud in the positioning. Databases and collections need no
cluster support of their own — they are pure key grammar, so they shard, replicate, and
migrate exactly as their hash tags dictate.

**Sharding ambition, bounded (D23):** Mongo-level dynamic sharding (chunks, balancer,
config servers, router query planning) is **explicitly never** — a decade of engineering
serving terabyte collections we've declared out of scope. If a single collection ever
outgrows one node, the designed-for extension is **Elasticsearch-style static
partitioning**: `[Collection(Partitions = 8)]` → sub-units `{orders#0}`…`{orders#7}`,
writes routed by `hash(id) % N` — each save still single-slot, so D10 survives untouched —
and queries scatter-gather with merges our earlier decisions already make tractable
(token-paged sorted-stream merge per D13; subtractable aggregates per C20 sum exactly
across partitions). Documented costs when that day comes: global unique constraints
restrict to id or partition-prefixed values (a global claim would need a second slot —
D10 forbids it), all queries fan out, and N is fixed at creation. The key grammar
**reserves the `#partition` suffix now** so adoption later breaks no keys.

One open question survived the research (C8): whether custom transactional procedures
execute in cluster mode is not documented. D4 guarantees same-slot key sets either way —
the most likely case to work — but the Phase 0 spike must verify it.

## Part VIII · Capacity: can Garnet handle 5–10 GB?

**Yes — provided documents live in the main store.** That's the load-bearing design rule:

- Garnet's main store sits on Tsavorite's hybrid log, explicitly built for
  *larger-than-memory* data: enable `--storage-tier`, and records beyond the configured
  log memory spill to disk; maintainers confirm the disk log grows unbounded with
  compaction available to cap it. Its FASTER lineage was benchmarked far beyond this
  scale. 5–10 GB of documents on a local NVMe is comfortably inside the envelope.
- The **object store is the weak flank**: its heap-memory accounting has open issues where
  `--obj-heap-memory` does not reliably cap usage, and its disk tiering is far less
  proven. So indexes — the only thing we put there — must fit in RAM (C2). By construction
  they do: indexing 5–10 declared fields over 10 GB of documents typically costs
  300 MB–1 GB.
- Durability: AOF (write-ahead) plus periodic checkpoints. The fsync policy sets the loss
  window — a real but standard trade-off to document (C6, C11).

Concrete budget for 10 GB of documents (~10 M docs at ~1 KB) on an 8 GB box:

| Component | Budget | Notes |
|---|---|---|
| Main store log memory (hot documents) | 2–3 GB | Rest of the 10 GB tiers to SSD; cold reads are single point lookups — fine on NVMe |
| Main store hash index | ~160 MB | Sizing rule: keys × 16 bytes |
| Object store (all secondary indexes) | 0.5–1 GB | Must stay resident; scales with indexed fields, not document size |
| AOF buffer + overhead | ~0.5 GB | |
| SSD: tiered log + AOF + checkpoints | ~25–30 GB | Log before compaction ≈ 1.5–2× data; keep headroom |

**Honest caveats:** tiered-storage configuration is manual and its documentation is
admittedly scattered (maintainers' words); anything that *scans* the disk tier is
expensive — another argument for the everything-through-indexes design; and single-node
remains the recommended small-app deployment (Part VII). Above ~50–100 GB per unit or high
write concurrency, point users at Mongo without apology.

## Part IX · Effort

Estimates assume one experienced developer who has already shipped a Garnet extension
with ~20 custom commands — which is exactly the situation. "From scratch" overstates it:
the server-extension skeleton, RESP client plumbing, embedded-Garnet test harness, and
protocol-conformance discipline from Highway are all reusable *patterns*, even in a fresh
repo.

| Phase | Contents | Effort |
|---|---|---|
| **0 · Spike** (de-risk first) | Embedded Garnet with storage tiering over a 10 GB synthetic set; the save procedure with server-side index membership (Part II); custom transactional procedures under cluster mode (C8); object-store memory caps measured (C13); cold-read and save latencies | 1–2 wks |
| **1 · Core store** | `IDocumentStore`/database/collection API, CRUD, envelope + version concurrency, TTL (D7 scope), change feed, multiplexed client, embedded + server hosting | 4–6 wks |
| **2 · Index engine** | Declared map indexes, atomic maintenance procedure, closed typed query API (eq/range/sort/page with continuation tokens), unique constraints, index backfill & rebuild | 8–12 wks |
| **3 · Grown-up features** | Reduce (aggregate) indexes, compiled queries, multi-tenancy placement modes, TTL sweeper (lifts D7), management/stats surface, cluster-aware client (C15) | 6–8 wks |
| **OSS launch tax** | Docs site, samples, benchmarks vs LiteDB/Mongo, CI, NuGet polish — mandatory for the "bring Garnet mainstream" ambition | 4–6 wks |
| **4 · Event store** (optional, later) | Append-only streams, global ordering, projections rebuilt from the log — Marten's second half; Highway becomes the Wolverine that consumes it | 8–12 wks |

**Bottom line: ~4–6 months to a credible, documented v1** (phases 0–2 + launch tax),
~6–8 months to feature-completeness against YesSQL, and the event store is a separate,
later bet. The single highest-leverage week is the Phase 0 spike — it converts the real
unknowns (tiering at 10 GB, cluster-mode transactions, object-store caps) from risk into
data before any API is designed.

## Part X · Risks

| Risk | Severity | Mitigation |
|---|---|---|
| Object-store memory caps not enforced (open Garnet issues) | Medium | Indexes-only in object store; measure in spike; budget RAM conservatively |
| Custom transactional procedures unverified in cluster mode (C8) | Medium | Spike item #1; D4 guarantees same-slot key sets — the most likely case to work |
| Garnet API churn (extension APIs are young) | Low–Med | Pin versions; isolate Garnet behind one adapter layer |
| Durability window (AOF fsync policy) | Low | Document it; offer a strict-fsync mode |
| Positioning ("why not Mongo/LiteDB?") | Medium | Lead with embedded→server growth path + cache-and-docs-in-one; state the closed query surface and per-document ACID as philosophy, like YesSQL does |

## Part XI · Recommendation

**Build it, in this order:** run the two-week Phase 0 spike before designing any public
API. If tiering holds at 10 GB, cluster-mode procedures work, and the server-side
index-membership save feels solid, commit to phases 1–2 with YesSQL's architecture and
Mongo's client ergonomics, and position it as the .NET-native embedded-to-server document
store that is also your cache. Keep the query surface closed and the atomicity scope
per-document — and be proud of both. Leave the event store until the document store has
users — Marten earned its event store after winning on documents.

---

## Part XI-b · The reference application (D24) and what remains

**D24 — the killer app is a trimmed, OrchardCore-shaped CMS**, built alongside the
database as its reference application. Coverage map: content types as POCOs (D17), slugs
as unique constraints, tags as multikey + faceted `SINTER` filters, dynamic custom fields
as the dictionary/facet index, listings as compound indexes (type+status+date — ESR in
real life), per-category counts and most-viewed as reduce/Top-N, TTL preview tokens (D7),
live preview via `WatchAsync` (C19's intended self-healing consumer), content-type
evolution via D21 migrations, dump/restore as import/export, **multi-tenancy exercising
`{tenant}` placement (D4)** — the feature nothing else would test — and the admin UI *is*
the D20 explorer. Governance role: release gate (every public feature exercised by the
CMS before shipping) and scope guard in reverse (if the CMS can't use a proposed feature,
question the feature). Not its job: 10 GB capacity and cluster failover — those belong to
the benchmark/chaos suite.

**What remains before code:**
1. ~~The project name~~ — **resolved: `Stow` (D25)**; command prefix `ST.*`.
2. The bootstrap kit: new folder, `CLAUDE.md`, this document copied in.
3. Spec 001 — the Phase 0 spike (C8, tiering at 10 GB, object-store caps, the
   server-side-membership save procedure).
4. The authoritative protocol document + conformance tests (Highway pattern, prior-art 1).
5. **The last open API decision: the error model** — Highway says errors are data
   (StatusCode); Mongo's driver throws; a DB client must pick before the API ships.
   Parked deliberately for the spec phase.
6. Benchmarks vs LiteDB/Mongo (OSS launch tax).

## Part XII · Completeness audit (the full-architect pass)

The canonical database-capability checklist against D1–D20 / C1–C20. Verdict: the data
plane is covered for the target market; the audit found **three operational gaps** (closed
below as D21/D22) and one posture statement to make explicit (C21).

| Concept | Status |
|---|---|
| Data model: documents, collections, named databases, ids, size caps | ✅ D1, D2, D14, D17 |
| CRUD + bulk with honest semantics | ✅ Operation catalog |
| Indexed queries: eq/range/sort/paging/distinct/O(1) counts | ✅ Part II |
| Index menu: unique, compound, multikey, computed, geo, facet, element-compound, reduce | ✅ Part II |
| Partial/filtered indexes | ✅ free — an `Emit` returning empty *is* a filtered index; `[Indexed(When = ...)]` sugar |
| Aggregations: pre-aggregated Sum/Count/Avg/Min/Max, Top-N | ✅ Part II |
| Transactions: per-document ACID | ✅ D10 · multi-document ❌ by design (C16) |
| Optimistic concurrency | ✅ D3 |
| Isolation: read-committed-latest | ✅ C10 · snapshot ❌ accepted |
| TTL | ✅ D7 |
| Change feed / CDC | ✅ C19 (live-only, documented) |
| Durability: AOF + checkpoints | ✅ inherited |
| Physical backup (checkpoint) | ✅ · **logical dump/restore → D21** |
| Schema evolution / migrations | **→ D21** (lazy upgrade-on-read + batch runner) |
| Collation / string ordering | **→ D22** |
| Validation on save | ✅ C# types + DataAnnotations honored client-side |
| Security: TLS, ACL users, per-db ACL key patterns | ✅ inherited from Garnet — stated as C21 |
| Replication / HA / cluster placement | ✅ D4, C5, C6 |
| Observability, profiler, explorer, `Explain()` | ✅ D19, D20 · Explain → D21 |
| Embedded + server modes, sequences/HiLo, materialized views (reduce) | ✅ |
| Ad-hoc queries, joins, pipelines, stored procedures, in-core FTS, intra-collection sharding | ❌ **by design** — C1, C16, D16, C3; stated as philosophy |

## Part XIII · Multi-app, multi-store, and the abstraction stance (D29–D31)

### D29 · Migration coordination: the server keeps the books, the client does the work

C# migration classes cannot coordinate alone — stateless app instances can disagree about
the contract. Three mechanisms close it:

1. **Catalog handshake.** At startup every client declares what it knows per collection
   (schema version, index-set hash, migration chain). The catalog records the **monotonic
   maximum** — versions only move forward. The server never understands contract *content*
   (D15/D26 hold); it arbitrates opaque numbers.
2. **The downgrade write guard — the piece Mongo cannot offer.** Schema version lives in
   the envelope header (span-readable, D26). The save procedure compares the writer's
   schema version against the stored document's: **a v1 writer cannot overwrite a v2
   document** — clean conflict, never silent field loss. Stale apps read tolerantly; they
   cannot destroy.
3. **The envelope-fed version index.** A system-maintained index (schema version →
   doc ids, plus per-version population counters) makes "what still needs migrating?" an
   O(1) query and the dashboard's migration progress bar free — where Mongo/Marten/LiteDB
   must scan. The background runner becomes **cooperative**: any app instance claims
   batches atomically (lease via claim mechanics); the deployed fleet drains old versions
   itself. Rolling deploys: the catalog tracks current + minimum-supported version;
   raising min-version retires stale clients explicitly at handshake.

Shared contract assemblies (the Highway pattern) are the distribution of truth; index
declarations from clients are **additive** — absence is not removal; dropping an index is
an explicit admin operation.

### D30 · Multi-store addressing: four servers, one injection

The address hierarchy gains its outermost tier: **Store (connection) → Database (D14
namespace) → Collection**. Stores are registered by logical name
(`services.AddStow(o => o.Store("analytics"))`); physical connection strings live in
configuration only. Types route by `[Store("analytics")]` + `[Database]` + `[Collection]`
— resolution precedence **configuration > attribute > convention**, unannotated types to
the default store. Startup scanning builds a single **ownership map** (every type →
exactly one store/database/collection); ambiguity or an unregistered store name is a
fail-fast startup error. The one `IDB` façade routes across stores by type (D18's "type
is the router," one level up); `db.Store("legacy")` and keyed DI give explicit handles.
Everything is scoped per store: catalog, handshake (D29), change feed, dashboard. Nothing
spans stores.

### D31 · The abstraction stance: `IDB` is the foundation; repository is a view

Grounded in review of a production Mongo `IRepository<T,TKey>` / `MongoRepository`
codebase (Bookkeeper): two promises in that shape Stow must refuse — **`IQueryable<T>` on
the interface** (a promise of arbitrary LINQ that C1 forbids; implement-and-throw is an
interface that lies) and **an `IEntity<TKey>` constraint** (D17 abolished required
interfaces). Predicate-taking methods survive with a changed contract: indexed members
only, startup-validated, analyzer-checked. What Stow keeps from the pattern:
`db.Collection<T>()` *is* the repository-shaped handle, and a documented ~60-line
`StowRepository<T>` adapter over `IDB` gives existing repository codebases a mechanical
migration path (swap the class, drop the constraint, let the analyzer list which
predicates need `[Indexed]`). Cross-cutting repository subclasses (Auditable-, Tenant-)
become **engine features, not inheritance**: auditing = change-feed consumer (D19),
tenancy = placement + ownership map (D4/D30). `IDB` is not "EF style" — no change
tracking, no unit of work (D11); it is Mongo-driver immediacy with type-routing sugar.

## A note on polyglot access (decided: .NET-only, bridge possible later — D15)

The database is protocol-defined, not .NET-defined: `DOC.*` commands are ordinary RESP,
callable from any language's Redis client (`sendCommand` in ioredis, etc.), and documents
are JSON on the wire — so reads, saves at the raw-protocol level, the change feed, and
token-paged queries are language-agnostic today. What is .NET-specific is the typed client
sugar. The one seam that decides how far this goes: v1 computes index entries in the .NET
client, so other languages should read but not write; registering index definitions as
JSON paths in the server catalog and extracting values server-side would make writes fully
polyglot. A vNext lever, deliberately not in scope for v1 delivery.

**And the case for staying .NET-only (the Highway pattern):** types are the schema — no
language-neutral value-encoding spec (dates, `decimal`, `Guid`, enums) ever needs writing;
index-entry encoding stays a private, changeable implementation detail instead of a frozen
wire format; client and server ship in lockstep so `DOC.*` can evolve without deprecation
windows or per-language conformance suites; the flagship embedded mode is .NET-only by
nature and already selects the audience; deep platform integration (DI,
`IAsyncEnumerable`, analyzers, Aspire, typed change feed) beats shallow breadth; and
"*the* document database for .NET" is an ownable category — Marten stayed .NET-only
through a decade of success. The cost — smaller addressable market — is ~zero against a
target market that was .NET shops from the first sentence of this research.

## Appendix A · Decision register

| # | Decision | Status |
|---|---|---|
| **D1** | Collections are first-class: metadata registry, auto-created primary id index, C# type ↔ collection by convention, `[Collection]` override; names `[a-z0-9_.-]{1,64}` | Active (Part IV) |
| **D2** | Document cap 1 MiB default, 8 MiB configurable ceiling, warn at 256 KiB | Active (Part IV) |
| **D3** | Optimistic concurrency via version-in-envelope, not native ETag (ETag unsupported in cluster mode) | Active (Parts V, VII) |
| **D4** | Distribution unit is a deployment choice via hash tag: `{db/collection}` default, `{db}`, or `{tenant}` — every save is single-slot | Active, refined by D14 (Part VII) |
| **D5** | ~~Cross-collection saves single-node; restricted in cluster~~ | **Superseded by D10** — the mode split was a trap |
| **D6** | Bulk writes (`SaveManyAsync`): pipelined, per-document atomicity, per-document results — never all-or-nothing | Active, revised per D10 (Part V) |
| **D7** | TTL in v1 only on collections without secondary indexes; phase 3 sweeper lifts the restriction | Active (Parts II, IX) |
| **D8** | String range/sort indexes use a normalized 64-byte prefix; equality hashes the full value | Active (Part II) |
| **D9** | Serializer pluggable behind `IDocumentSerializer`; UTF-8 JSON default; envelope header (format + version + flags) on every payload | Active (Part VI) |
| **D10** | Atomicity is per document + all derived state; nothing spans two documents, in any deployment mode | Active (Part V) |
| **D11** | No `ISession`/unit of work — repository-shaped immediate API; no identity map or change tracking | Active (Part V) |
| **D12** | Mongo-shaped API on one multiplexed RESP connection (StackExchange.Redis model); change feed on a dedicated subscriber connection | Active (Part V) |
| **D13** | No server-side cursors — paging via opaque stateless continuation tokens | Active (Part V) |
| **D14** | Databases are a key-grammar namespace (`d:{db/collection}:id`), Mongo-named, db0-only server; Garnet `SELECT` databases unused | Active (Part IV) |
| **D15** | **.NET-only, the Highway pattern.** The database ships with a .NET client exclusively; client and server evolve in lockstep; index-entry encoding stays private. A Node.js (or other-language) *bridge* may open later via the vNext lever (server-side index extraction from catalog-registered JSON paths) — a deliberate future door, not a v1 commitment | Active (Polyglot note) |
| **D16** | **No Lucene — ever, in the core.** Full-text relevance search (analyzers, stemming, BM25) is out of engine scope: embedding Lucene.NET would create a second storage engine with its own durability/recovery story that cannot join a Garnet transaction, breaking the single-binary single-engine premise and the atomic-save guarantee. Text beyond the basic inverted-set index is a **sidecar projection over the change feed** (the Atlas Search pattern: Mongo never embedded Lucene either); semantic retrieval is the DiskANN vector roadmap item | Active (Part II index menu) |
| **D17** | **Conventions follow Highway: attribute-driven POCOs + assembly scanning.** Documents are plain C# objects — no base class, no fluent mapping. `[Collection]`, `[Id]`, `[Indexed]`, `[Unique]`, `[Compound]`, TTL defaults as attributes; reduce indexes as plain declaration classes; startup scanning registers everything into the catalog and validates queries against it | Active (Part IV) |
| **D18** | **The API is one small `IDB` façade — seven verbs, type-routed.** `Get`/`Insert`/`Save`/`Update`/`Delete`/`Query`/`Watch`, `*Many` as overloads; `[Database]` attribute or `GetDatabase()` for named DBs; `db.Collection<T>()` as the optional injectable handle. No per-entity repositories, no context/registry class, no `AbstractRepository` base. Build-time query validation via Roslyn analyzer (enabled by D15 + D17) | Active (Part V) |
| **D19** | **Observability is three-tiered:** always-on in-memory counters + latency histograms + index usage stats (`DOC.STATS`, keyspace-free — no AOF/replication cost); opt-in capped ring-buffer profiler with timestamped op records (`DOC.PROFILE`, Mongo profiler model: off / slow-ms / sample / all); embedded Compass-class dashboard fed by a per-second `sys:stats` pub/sub snapshot. Permanent audit trails are a change-feed consumer, not the profiler | Active (Part V) |
| **D20** | **The dashboard extends into a data explorer (Compass parity):** connection → databases → collections → records → aggregates/live-tail tree; token-paged JSON browsing; single-doc edit through the version-checked save path; reduce indexes as live aggregate tables. Every screen is a pure client of the public surface (no backdoor commands); the filter builder offers only indexed fields (the UI teaches C1). v1 scope: read-everything + single-doc edit + live tail; richer editing later | Active (Part V) |
| **D21** | **Operational toolkit:** (a) **logical dump/restore** — JSON-lines export per collection/database via primary-index enumeration, import via bulk insert (migration, seeding, DR, version-upgrade escape hatch — the `mongodump` parity trust signal); (b) **schema evolution** — per-collection schema version in the envelope + registered `Upgrade(old → new)` callback for lazy upgrade-on-read, plus a background **migration runner** batching through the normal save path (version-checked, index-maintaining, token-resumable); (c) **`Explain()`** on queries — trivial without a planner (names the answering index), completes the toolkit | Active (Part XII) |
| **D22** | **Collation is three-tiered, per index:** (1) ordinal culture-invariant default; (2) own-code version-stable folding — case/accent-insensitive + natural-numeric sort (`[Indexed(CaseInsensitive = true, ...)]`), applied to index entries only; (3) true culture collation via `GetSortKey` (`[Indexed(Culture = "sv-SE")]`) with `CompareInfo.Version` recorded in the catalog and rebuild-on-mismatch (C22). No per-query collation — collation is an index property. Content storage is full Unicode regardless | Active (Part II) |
| **D23** | **Sharding ambition bounded:** Mongo-style dynamic sharding — never (chunks/balancer/routers serve a market we exclude). The designed-for future extension is ES-style **static partitioning** (`[Collection(Partitions = N)]`, sub-units `{coll#i}`, `hash(id) % N` routing) — preserves D10 exactly, merges tractable via D13/C20; costs documented (unique constraints restricted, fan-out queries, N fixed at creation). Key grammar reserves `#partition` now | Future-designed (Part VII) |
| **D24** | **Reference application: a trimmed OrchardCore-shaped CMS** — content types as POCOs, slug uniques, tag facets, dictionary custom fields, compound listings, reduce counts/Top-N, TTL previews, `Watch` live preview, D21 migrations, dump/restore, `{tenant}` multi-tenancy, admin = the D20 explorer. Doubles as release gate (every public feature exercised before shipping) and reverse scope guard (feature the CMS can't use → question it). Capacity/failover testing stays with the benchmark/chaos suite | Active (Part XI-b) |
| **D25** | **The project is named `Stow`; the brand is `StowDB`** — *"A document database native to .NET."* Marketing uses **StowDB** (unique, searchable, self-categorizing — the MongoDB/LiteDB/RavenDB/DuckDB convention); code uses **Stow**: packages `Stow.Abstractions` / `Stow.Client` / `Stow.Server` (+ `Stow.Cms` for D24); wire commands prefix **`ST.*`** — every `DOC.*` in this document is a placeholder now resolved to `ST.*`. Positioning line: *"Built on Garnet. Starts embedded like LiteDB, grows into a server like Mongo — and it's your cache too."* Known name overlap: GNU Stow (symlink manager, distant niche) — accepted | Active |
| **D26** | **Allocation-free server hot path:** `SpanByte` in, `SpanByte` out; strings only at API/diagnostic boundaries. Cached pre-encoded key prefixes + `stackalloc`/pooled composition; envelope version check as a binary span slice (never a JSON parse); membership record in compact binary; span-to-span folding. Enforced by a zero-alloc BenchmarkDotNet target in the Phase 0 spike, kept as a CI regression benchmark. Client-side ergonomics may allocate (D15 puts heavy work in scaling clients); no cargo-cult `unsafe` | Active (Part II) |
| **D27** | **Identity: `Id` + UUIDv7, not `_id` + ObjectId.** `Guid.CreateVersion7()` default — auto-generated client-side on insert, immutable, time-ordered (primary index insertion-ordered for free), native type with zero custom-serializer tax. ObjectId algorithm explicitly not ported (migrations import ObjectIds as string ids via D21). Polymorphic ids: `string` natural keys, `int`/`long` HiLo via `INCR`. `[Unique]` business keys stay orthogonal (same-save claims, O(1) lookup, D22-foldable, composite-capable). Order-preserving base32 compact encoding reserved as a spec-phase option | Active (Part IV) |
| **D28** | **Index lifecycle: every build online by design — no `Background` flag** (Mongo deprecated theirs). Catalog states Declared → Building → Ready/Failed with progress token; new writes maintain Building indexes immediately; backfill is token-resumable batched walk (D21 machinery); queries on Building indexes fail fast with progress; unique backfill Fails on existing duplicates with ids reported; `Hidden` supported (maintained, ignored for resolution — safe decommission with D19 usage counters). Mongo TTL-index semantics deferred to the phase-3 sweeper | Active (Part II) |
| **D29** | **Migration coordination:** catalog handshake with monotonic per-collection schema versions; **server-side downgrade write guard** (envelope schema version compared span-wise in the save procedure — a stale writer gets a conflict, never silent field loss); envelope-fed **version index** + population counters (O(1) "what's left", live dashboard progress); **cooperative runner** (any app instance claims batches via lease); current + minimum-supported version gates rolling deploys; shared contract assemblies distribute truth; client index declarations are additive | Active (Part XIII) |
| **D30** | **Multi-store addressing:** Store → Database → Collection hierarchy; logical store names in code, connection strings in configuration; ownership map built at startup (config > attribute > convention, fail-fast on ambiguity); one `IDB` routes across stores by type; `db.Store()`/keyed DI for explicit handles; catalog, handshake, and change feed scoped per store; nothing spans stores | Active (Part XIII) |
| **D31** | **Abstraction stance: `IDB` is the foundation, repository is a view.** No `IQueryable<T>` on any public abstraction (C1 forbids the promise); no entity base-interface constraints (D17); predicates = indexed members only, startup-validated + analyzer-checked. `db.Collection<T>()` is the repository-shaped handle; a documented `StowRepository<T>` adapter gives Mongo-repository codebases a mechanical migration path; auditing/tenancy are engine features, not repository subclasses | Active (Part XIII) |
| **D32** | **Garnet dependency strategy: pinned NuGet package — no fork, no submodule.** A fork adopts Tsavorite maintenance, forfeits upstream previews Stow's roadmap depends on (Bf-Tree, DiskANN), and kills the "built on Microsoft Garnet" credibility line; a source submodule is cost without cause — all 31 prior decisions ride public extension APIs, zero engine modifications needed. Escape ladder in order: extension-layer workaround (proven: D3, D15) → upstream PR (contribution-first) → temporary pinned source build with a `patches/` folder until merged → fork as last resort, decidable later with full information. Mechanics: Garnet behind one adapter seam in `Stow.Server`; version pinned exactly, bumped only through the conformance suite + D26 zero-alloc benchmark + checkpoint/AOF recovery test; Garnet version recorded in checkpoint metadata so mismatched restores fail loudly. Precedent: Marten doesn't fork Postgres | Active |

## Appendix B · Constraint register

| # | Constraint | Status |
|---|---|---|
| **C1** | No ad-hoc queries on non-indexed fields | **By design** — the YesSQL position, stated as philosophy |
| **C2** | All secondary indexes must fit in RAM (documents tier to disk; indexes never) | **Accepted** — ~3–10% of data size; startup advisory check |
| **C3** | One distribution unit (collection/db/tenant per D4) cannot exceed one shard | **Accepted v1** — fits ≤10 GB/unit target; no intra-unit sharding |
| **C4** | Writes to one collection serialize on its shared index keys — a per-collection throughput ceiling | **Accepted** — measure in spike; the known scaling wall |
| **C5** | Cluster failover requires an external control plane (Garnet's cluster is passive) | **Inherited, accepted** — ship a k8s operator recipe; single node + AOF is the default story |
| **C6** | Replication is async → failover has a data-loss window (RPO > 0) | **Inherited** — document; strict-fsync AOF narrows but doesn't close it |
| **C7** | Native ETag unavailable in cluster mode | **Solved** by D3 |
| **C8** | Custom transactional procedures under cluster mode undocumented | **OPEN — spike item #1**; D4 guarantees same-slot either way |
| **C9** | TTL expiry would orphan index entries | **Solved** by D7 (restrict, then sweep) |
| **C10** | No snapshot isolation; read-committed-latest; version re-check on materialization; no repeatable read | **Accepted** — documented semantics |
| **C11** | Backup = checkpoint + AOF replay; no fine-grained point-in-time restore | **Accepted** |
| **C12** | Document size capped (D2) | **By design** |
| **C13** | Garnet object-store memory caps unreliably enforced (open upstream issues) | **Mitigated** — indexes only, conservative budgets, spike measurement |
| **C14** | `Drop`/rename of a collection or database is an O(n) background operation | **Accepted** — enumerated via primary index / registry |
| **C15** | Cluster deployments need a cluster-aware client (`-MOVED`/`-ASK`, slot map) | **Accepted work item** — phase 3+ |
| **C16** | No multi-document or cross-collection transactions, in any mode | **By design** (D10) — aggregate = one document; cross-document consistency is eventual, via the change feed |
| **C17** | Ambiguous write outcome on connection loss mid-save (no retryable writes in v1) | **Accepted, documented** — retry surfaces a version conflict if the first attempt applied; envelope reserves an idempotency-key slot |
| **C18** | Garnet's numbered databases (`SELECT`/`SWAPDB`) unused and unsupported | **By design** (D14) — connection-stateful, excluded from transactions upstream, unverified in cluster |
| **C19** | The change feed (`WatchAsync`) is **live-only, at-most-once** — server-push over pub/sub with no history: a disconnected subscriber misses events, with no resume token (unlike Mongo change streams, which resume via the oplog) | **Accepted v1, documented** — positioned for self-healing consumers (cache invalidation, live UI, metrics), not guaranteed processing. Two future fixes already in the plan's shape: the phase 4 event store (append-only log → replayable feed with resume tokens), or Highway's durable queue as the delivery rail |
| **C20** | *Per-group* reduce aggregates must be *subtractable* — `Count`/`Sum`/`Avg` are native counter arithmetic; per-group `Min`/`Max` cannot be maintained by counters (deleting the extreme document leaves the counter blind) | **Accepted, narrowed** — collection-wide `Min`/`Max` on any indexed field is **free and delete-safe** (the ends of the range index); only per-group `Min`/`Max` needs the opt-in per-group contribution sorted set (incremental and correct, higher memory), documented cost |
| **C21** | Security is Garnet's, not ours: TLS, ACL users/passwords, and per-database access via ACL key patterns (D14). Custom `DOC.*` commands land in Garnet's `@dangerous` ACL category (the feature-012 finding) — `+@all -@dangerous` hardening silently refuses them, and a `nopass` default user is a total bypass. No field-level encryption in v1 | **Inherited, documented** — deployment docs must carry the `@dangerous` and `nopass` guidance from day one |
| **C22** | Culture-collated indexes (D22 tier 3) depend on the ICU sort-key version, which changes with the OS — the PostgreSQL glibc/ICU corruption failure mode | **Mitigated by design** — `CompareInfo.Version` recorded per index at build time, checked at startup, mismatch flags the index for rebuild (D21 machinery). Tiers 1–2 are immune (ordinal + own-code folding) |

## Prior art in the Highway repository

This document is self-contained, but the Highway repo (`c:\Software\ai\highway`) holds
four artifacts worth mining when implementation starts — referenced by repo + path, not
relative links, so the pointers survive copying this file into the new project:

1. **Highway repo: `docs/HIGHWAY-PROTOCOL.md` + `ProtocolConformanceTests`** — the
   template for protocol discipline: one authoritative protocol file, enforced by a
   conformance suite that parses it against a running server in both directions. Copy the
   pattern for `DOC.*`, not the content.
2. **Highway repo: `docs/product/constraints.md`** — the model for how C1–C19 should live
   once code exists: numbered guarantees, per-line implementation status, updated in the
   same feature that changes behavior.
3. **Highway repo: `docs/features/012-introduce-security/design.md`** — measured *Garnet*
   ACL behavior, directly inherited by the new server: custom commands land in the
   `@dangerous` category (`+@all -@dangerous` hardening silently refuses them — `DOC.*`
   will hit the identical trap), per-name ACL rules work for custom commands, and a
   `nopass` default user is a total authentication bypass.
4. **Highway repo: `Highway.Server` extension skeleton + embedded-Garnet test harness** —
   the working example of registering custom transactional procedures and testing against
   in-process Garnet; the Phase 0 spike starts from this shape.
5. **Highway repo: `HW.STATS` + the `Highway.Server.Dashboard` project** — the working
   precedent for D19's observability stack: a stats command over server-held counters and
   an embedded web dashboard served from the same binary.

## Sources

- [Marten — Introduction](https://martendb.io/introduction)
- [Wolverine](https://wolverinefx.net/)
- [YesSQL — GitHub](https://github.com/sebastienros/yessql)
- [Garnet — Documentation](https://microsoft.github.io/garnet/docs)
- [Garnet — Managing memory usage](https://microsoft.github.io/garnet/docs/getting-started/memory)
- [Garnet — Cluster overview (slots, hash tags, passive failover, multi-key rules)](https://microsoft.github.io/garnet/docs/cluster/overview)
- [Garnet — Custom transactions](https://microsoft.github.io/garnet/docs/extensions/transactions)
- [Garnet — Custom commands (ETag cluster-mode limitation)](https://microsoft.github.io/garnet/docs/dev/custom-commands)
- [Garnet — Dev onboarding (Span/SpanByte performance model)](https://microsoft.github.io/garnet/docs/dev/onboarding)
- [Garnet #847 — Larger-than-memory cache (maintainer guidance on tiering)](https://github.com/microsoft/garnet/discussions/847)
- [Garnet #464 — Max size of tiered storage](https://github.com/microsoft/garnet/discussions/464)
- [Garnet #1314 — obj-heap-memory not enforcing cap](https://github.com/microsoft/garnet/issues/1314)
- [Garnet #1015 — Memory issue with sets](https://github.com/microsoft/garnet/issues/1015)
- [Garnet #697 — Object store heap size configuration](https://github.com/microsoft/garnet/pull/697)
- [Garnet PR #1005 — Multi-Database Support](https://github.com/microsoft/garnet/pull/1005)
- [Redis SELECT — cluster db0 rule](https://redis.io/docs/latest/commands/select/)
- [Valkey #1681 — multi-DB in cluster mode design](https://github.com/valkey-io/valkey/issues/1681)
- [MongoDB driver sessions specification](https://github.com/mongodb/specifications/blob/master/source/sessions/driver-sessions.md)
- [MongoDB retryable writes specification](https://specifications.readthedocs.io/en/latest/retryable-writes/retryable-writes/)
- [MongoDB connection pool overview](https://www.mongodb.com/docs/manual/administration/connection-pool-overview/)
- [StackExchange.Redis — Basics (multiplexer model)](https://stackexchange.github.io/StackExchange.Redis/Basics)
## Appendix C · Senior architecture review — ChatGPT 5.6 Sol model recommendations

> **Attribution and preservation note.** This appendix records an independent architecture
> assessment produced by the **ChatGPT 5.6 Sol model**. It is intentionally preserved
> separately from the original research and its decision/constraint registers. These are
> recommendations, not silent corrections to, or replacements for, the historical conclusions
> above. **Stow remains a sibling project independent from Highway**; this review does not make
> Stow a Highway feature or couple either product's release plan to the other.

### Verdict: GO WITH GATES

The product idea is technically credible and differentiated, but the research currently treats
several hypotheses as settled architecture. The correct executive decision is **GO WITH GATES**:
fund and execute a bounded Phase 0 proof, but do not authorize a product build, publish capacity
claims, or promise cluster readiness until every exit gate below passes with reproducible evidence.
The first commercial-quality target should be an honest single-node document store with a future
cluster path—not a broad MongoDB substitute.

### Executive decision

Build the Phase 0 spike now because the core proposition is worth testing: a .NET-native,
Garnet-backed document store with immediate repository ergonomics, declared indexes, embedded and
server deployment, and a deliberately closed query model. Preserve the strongest discipline in the
research: one-document atomicity, no session that implies a nonexistent unit of work, no arbitrary
query planner, and no hidden Highway dependency.

Do **not** yet approve the 4–6 month estimate or the statement that all v1/phase-3 mechanics are
already supported in a product-ready way. A credible production schedule is **9–14 months for a
single-node v1** and **14–20 months total for a genuinely cluster-ready release**, assuming one
strong senior engineer, timely upstream answers, and disciplined scope. AI assistance can reduce
boilerplate, test generation, documentation, and code-navigation time; it does not compress soak
time, fault discovery, storage compatibility work, capacity experiments, upgrade testing, or the
calendar time needed to earn operational trust.

### What is sound

1. **YesSQL is the right architectural analogue.** Declared projections and maintained indexes are
   a better fit than trying to reproduce Marten's PostgreSQL-backed ad-hoc query engine.
2. **One document plus its derived state is the right atomic boundary.** It remains understandable
   in embedded, standalone, and eventual clustered deployment and avoids an API that over-promises.
3. **The sessionless API is honest.** Immediate operations and per-item bulk results expose the
   actual consistency boundary; a fake unit of work would obscure it.
4. **A closed query surface is a feature when paired with tooling.** Attributes/declaration classes,
   startup catalog validation, `Explain()`, and a Roslyn analyzer can convert a limitation into a
   predictable performance contract.
5. **The serializer envelope, version check, unique claims, and explicit document-size cap are good
   foundations.** They make upgrade, concurrency, and resource behavior explicit.
6. **Embedded-to-server is meaningful differentiation.** A .NET application can begin without an
   external database process and later retain the same model over RESP.
7. **The no-Lucene and no-multi-document-transaction boundaries are healthy.** They prevent Stow
   from becoming two storage engines or an incomplete distributed database.
8. **The project boundary is correct.** Stow should reuse lessons and perhaps infrastructure
   patterns from Highway, while remaining a separate repository/product with independent package,
   protocol, compatibility, and release contracts.

### Critical corrections and proof obligations

| Area | Correction to the current confidence level | Required disposition |
|---|---|---|
| Transaction rollback and faults | Garnet's [custom transaction documentation](https://microsoft.github.io/garnet/docs/extensions/transactions) says `Prepare` declares keys, `Main` runs under acquired locks, exceptions reset/unlock the transaction manager, and `Finalize` is non-transactional. It does **not** by itself prove that writes already performed by a failing `Main` are undone. “Locked together” must not be equated with rollback. | Fault-inject after every mutation. Prefer a procedure shape that performs all validation and allocation before the first write and has no expected failure path after mutation begins. If partial writes are observable after an exception, redesign around preflight plus a non-failing commit section; do not market ACID until this is proven. |
| Membership discovery in `Prepare` | The old membership record determines which stale index keys must be locked, but that record can change between discovery and execution unless the transaction framework provides a proven watched/read-validation mechanism. A naïve read in `Prepare` followed by `AddKey` can lock the wrong set under concurrent saves. | Prototype **watched `Prepare` membership discovery**: lock/watch the fixed document and membership keys, discover old members, add every old/new derived key, and prove the observed membership version cannot change before `Main`. If Garnet cannot support that sequence, use deterministic bounded key families, a two-stage protocol with compare/retry, or another membership layout. No hand-waving around this race. |
| Change notification | Pub/sub is not atomic with durable document commit. `Finalize` is explicitly non-transactional, while publishing inside `Main` would still be a non-durable external side effect that cannot be rolled back or replayed reliably. | Define `WatchAsync` as a **best-effort live invalidation hint** only: at-most-once, possibly missing, possibly duplicated if retries are introduced, and never evidence that durable processing occurred. Publish after successful commit; consumers must re-read. A durable feed requires an append-only durable record/outbox and is later scope. |
| Memory model | The current Garnet [memory documentation](https://microsoft.github.io/garnet/docs/getting-started/memory) describes **one unified Tsavorite store**, not an independently budgeted “main string store” and “object store.” Raw bytes are inline records; data-structure values reference heap objects; both are charged to the same main-log memory budget. | Treat the earlier “documents spill while indexes stay in a separate RAM store” diagram and C2 wording as stale assumptions. Re-measure raw strings, sets, hashes, and sorted sets under the unified budget, including heap accounting, GC behavior, read cache, checkpointing, and disk-tier cold reads. Do not promise 5–10 GB from the old split-store model. |
| Primary and secondary indexes | A single automatic primary sorted set is both a large managed object and a shared hot key. At roughly **10 million members**, IDs plus sorted-set/node/object overhead can plausibly consume hundreds of MiB to more than 1 GiB before secondary indexes; exact numbers are implementation- and ID-shape-dependent. Every insert/delete also touches the same key, creating a collection-level lock/throughput wall. Secondary set-per-value designs can create millions of keys and severe skew on hot values. | Measure 1M, 5M, and 10M members. Compare the monolithic sorted set with a segmented ID directory plus explicit count, and measure uniform and Zipfian indexes. Make enumeration order unspecified and continuation-token based so the implementation can segment later. Add per-index cardinality and hot-key admission guidance. |
| Cluster capability | Garnet supports slots, same-slot multi-key operations, replication, migration commands, and redirection, but that is not the same as a productized database cluster. Its [cluster overview](https://microsoft.github.io/garnet/docs/cluster/overview) also makes the external/passive control-plane responsibility explicit. Procedure execution during `MOVED`, `ASK`, migration, failover, replica replay, and catalog rollout still needs proof. | Separate **procedure support** from **cluster productization**. Prove same-slot custom procedures first; later build topology refresh, redirection/retry rules, migration fencing, control-plane guidance, rolling deployment semantics, and failure recovery. “Hash tags make it cluster-safe” is necessary but insufficient. |
| AOF compatibility | Transaction/procedure logging can bind durable history to registered procedure identifiers and implementation behavior. Registration order, renamed procedures, changed payloads, and changed replay code may make an old AOF unsafe or unreplayable. | Establish a stable procedure-ID manifest, immutable input framing per version, golden AOF fixtures, and an explicit replay compatibility matrix. On startup, reject unknown/incompatible procedure IDs rather than guessing. Prove old AOF plus checkpoint recovery across at least two procedure versions before v1. |
| Client-computed indexes | If the client supplies index entries, a stale, buggy, or malicious client can make the document and indexes disagree even when the server transaction is atomic. TLS/ACLs authenticate a caller; they do not make caller-computed derived state trustworthy. Lockstep .NET packages reduce accidental skew but do not remove the trust boundary. | Make the server authoritative for catalog/version and derived-state validation. The preferred product design is deterministic server-registered extraction generated from the same declarations; a transitional design may send client-computed entries only if the server validates catalog version, canonical encoding, bounds, and recomputed digest/entries. Never accept arbitrary index keys from a client. |

These corrections do not invalidate the concept. They change the order of work: storage and
failure semantics must be proven before the rich feature menu is treated as a roadmap commitment.

### API and model recommendations

Keep the small type-routed `IDB` façade and cheap `Collection<T>()` view, but make behavior more
explicit than the original API sketch:

- Use `InsertAsync`, `ReplaceAsync`/`SaveAsync`, `DeleteAsync`, `GetAsync`, `Query<T>()`, and
  `WatchAsync` with `CancellationToken` on every asynchronous path. If both `Save` and `Update`
  remain, define upsert/missing-document semantics in names and XML docs rather than by convention.
- Return a write receipt containing document ID, old/new version, operation kind, and a server
  commit token suitable for diagnostics—not a promise of globally ordered or resumable events.
- Require an expected version by default for replacement of an existing document. Make blind
  last-write-wins an explicit opt-in method/option.
- Keep bulk methods pipelined and return ordered per-document results. Never use a result shape that
  can be mistaken for all-or-nothing success.
- Keep query semantics closed and deterministic: one declared index in first v1, explicit sort
  compatibility, bounded page size, opaque continuation token, and no stable snapshot claim.
  A token must encode index/catalog version and fail clearly after rebuild rather than silently skip
  or duplicate arbitrary ranges.
- Make index declarations immutable, versioned catalog artifacts. Changes create a new physical
  index, backfill it, validate it, atomically switch the catalog pointer, then retire the old index.
- Do not expose physical index names or key grammar in the public API. This preserves freedom to
  segment primary directories, change encodings, and revise storage layouts.
- Define canonical index encoding once: null/missing distinction, signed numeric ordering, IEEE
  exceptional values, UTC/date precision, Unicode normalization, collation version, maximum emitted
  entries, and duplicate emission behavior.
- Bound every write by maximum document bytes, index count, emitted entries, total derived bytes,
  and transaction key count. Reject before mutation with a stable error code.
- Treat `WatchAsync` payloads as invalidations (`db`, `collection`, `id`, observed version, operation)
  and instruct clients to re-read. Do not put document payloads or durable workflow semantics there.
- Prefer a segmented primary ID directory (or prove the monolithic sorted set is acceptable).
  Preserve O(1) count with a transactional counter; make full enumeration admin-oriented and
  continuation-token based rather than promising sorted-ID table semantics.
- Keep logical databases and collections as key namespaces, but version the grammar and reserve
  segments now. Validate hash-tag placement server-side; do not allow clients to construct keys.
- Ship dump/restore before a graphical explorer. A storage product needs an escape hatch,
  compatibility inspection, and repair tooling before it needs a polished browser.

### Community value and differentiation

The strongest community proposition is not “MongoDB implemented on Garnet.” It is:

> **A document database native to .NET: declared, compile-time-checkable indexes; one-document
> atomic writes; starts embedded; grows into a server; operationally transparent over RESP.**

That proposition is valuable to .NET teams that have outgrown a file database but do not want a
SQL schema/ORM or MongoDB deployment. Stow can offer a notably coherent experience: POCOs,
`System.Text.Json`, DI/Aspire integration, analyzers that reject non-indexed queries at build time,
and one deployment artifact. Garnet compatibility also leaves room for cache-like structures, but
“database plus arbitrary Redis cache in the same keyspace” should not be a first-v1 headline until
resource isolation and noisy-neighbor behavior are measured.

Differentiation that is defensible early:

- compile-time validation of the deliberately limited query model;
- identical embedded and networked programming model;
- immediate/sessionless API whose consistency boundary is visible;
- versioned declared indexes rather than runtime query surprises;
- .NET-native diagnostics, OpenTelemetry, DI, and Aspire support;
- human-readable protocol/admin access without making Redis compatibility the product contract.

Differentiation to defer until proven: “larger than memory,” “cluster-ready,” “Mongo-like change
streams,” “simultaneously your cache,” sophisticated reduce indexes, geo/text/vector, and automatic
online schema migration.

### Pros and cons

| Pros | Cons / costs |
|---|---|
| Clear niche between LiteDB, YesSQL/Marten, and MongoDB | Building a trustworthy database is dominated by failure, recovery, and compatibility work, not CRUD code |
| Reuses a high-performance .NET storage engine and RESP ecosystem | Product correctness depends on Garnet extension behavior that is not fully documented for these compositions |
| Closed index model enables predictable plans and analyzers | Users must declare every queryable shape and rebuild indexes when models change |
| One-document atomicity is simple and cluster-compatible in principle | Cross-document invariants, durable subscriptions, and transactions remain application concerns |
| Embedded/server symmetry is compelling | Embedded lifecycle, file locking, backup, upgrade, and packaging add a second operational mode to test |
| Single-language focus allows excellent .NET ergonomics | Smaller ecosystem and no polyglot driver story in first v1 |
| Sibling-project independence protects Highway's scope | Shared people/infrastructure can still create opportunity cost; releases must remain decoupled |
| Garnet gives persistence, structures, replication primitives, and protocol | Unified memory, managed-object indexes, hot keys, AOF compatibility, and passive cluster control plane become Stow product risks |

### Realistic delivery estimate

The following assumes one senior engineer focused on Stow, access to Garnet maintainers when
needed, no simultaneous Highway milestone ownership, and no major engine changes. Ranges include
engineering, documentation, compatibility fixtures, and soak time.

| Stage | Scope | Elapsed estimate |
|---|---|---:|
| Phase 0 | Transaction/membership/fault spike, AOF replay, unified-memory capacity, cluster procedure probe, go/no-go report | 6–10 weeks |
| Single-node core | Protocol/catalog, canonical envelope/index encoding, CRUD/concurrency, equality/range/unique indexes, client API, analyzer basics | 3–5 months after Phase 0 |
| Production hardening | Backup/restore, index rebuild, observability, security docs, embedded/server lifecycle, fault/soak/performance qualification, packaging | 4–6 additional months |
| **Credible single-node v1** | Supported embedded + standalone product with bounded declared-index queries | **9–14 months total** |
| Cluster productization | Cluster-aware client, migration/failover semantics, procedure/catalog rollout compatibility, control-plane deployment recipe, chaos/upgrade qualification | 5–8 additional months, partly parallelizable late |
| **Credible cluster-ready release** | Explicit topology, migration, failover, upgrade, and recovery contract | **14–20 months total** |

Adding engineers can parallelize client tooling, docs, and admin tooling after the on-disk/protocol
contracts stabilize. It cannot safely parallelize an unsettled persistence model. AI assistance
improves throughput but does not justify deleting the hardening ranges.

### Recommended first-v1 scope

First v1 should be deliberately smaller than the research's complete menu:

1. .NET-only packages and one versioned `ST.*` protocol; Stow remains independent from Highway.
2. Embedded single-process mode and standalone single-node server mode using the same public API.
3. POCO discovery, `[Collection]`, `[Id]`, `[Indexed]`, and `[Unique]`; computed map declaration only
   if its deterministic server-validation path passes Phase 0.
4. UTF-8 `System.Text.Json` default, pluggable serializer behind a versioned envelope, with a strict
   1 MiB default and 8 MiB absolute cap.
5. Point get, insert, version-checked replace/save, delete, exists, get-many, and pipelined per-item
   bulk operations.
6. One-document atomic maintenance of primary directory, equality index, one range/sort index, and
   unique claims; bounded emitted entries and transaction keys.
7. Query by one declared index: equality, numeric/date range, matching order, count, first, and
   opaque token paging. No query intersections.
8. Versioned catalog, offline-or-online-copy index build with validated cutover, and an `Explain()`
   result naming the logical index and bounds.
9. Best-effort live `WatchAsync` invalidations, prominently documented as non-durable and
   non-resumable.
10. Logical dump/restore, startup compatibility checks, stats, structured logs, health checks, and
    a minimal repair/verification command.
11. TLS/ACL deployment guidance and server-side validation of every namespace, catalog version,
    key count, emitted entry, and payload bound.
12. Reproducible benchmarks and published tested capacity envelopes rather than general claims.

### Explicitly NOT in first v1

- Cluster support or a “cluster-ready” marketing claim; static hash-tag grammar may be reserved.
- Multi-document transactions, sessions/unit of work, identity maps, or all-or-nothing bulk writes.
- Durable/replayable change streams, exactly-once delivery, Highway coupling, or a transactional
  outbox.
- Reduce indexes, group aggregates, Top-N maintenance, set algebra/intersection, compound indexes,
  multikey/nested-path indexes, geo, text, vector, HyperLogLog, or Bf-Tree preview dependencies.
- Indexed TTL, server-side patch/update operators, migrations that execute arbitrary user code in a
  lock, retryable writes, or global ordering.
- Culture-dependent collation; ship ordinal and, only if fully versioned/tested, owned invariant
  folding. ICU-backed culture indexes can follow with rebuild tooling.
- Dynamic sharding, static collection partitioning, automatic balancing, or an in-house cluster
  control plane.
- GUI data explorer/dashboard, CMS reference application, cross-language clients, or Redis as a
  public compatibility guarantee.
- Ad-hoc predicates, joins, aggregation pipelines, SQL/reporting, full-text relevance, or promises
  that non-indexed fields can be queried.

### Test strategy

Tests must establish invariants across API, procedure, storage, restart, and deployment boundaries;
unit coverage alone is not evidence of database correctness.

```text
 declarations / generated catalog
              |
              v
  [encoding + bounds properties] -----> [golden protocol vectors]
              |                                  |
              v                                  v
 [procedure model/state machine] ------> [embedded real Garnet]
              |                                  |
     fault after every step               restart / checkpoint /
              |                            AOF replay / upgrade
              v                                  v
 [document == membership == indexes == unique claims]
              |
              +-------- concurrent writers --------+
              |                                     |
              v                                     v
      [single-node soak]                  [3-node cluster probe]
              |                           MOVED/ASK/migrate/failover
              +------------------+------------------+
                                 v
                    [capacity + latency envelope]
                                 |
                                 v
                         Phase 0 gate report
```

The minimum test portfolio is:

- **Pure unit and property tests:** canonical key/value encodings preserve intended ordering;
  serialization round-trips; null/missing and boundary values; duplicate emitted entries;
  document/index membership delta equivalence; continuation-token validation; catalog and procedure
  version compatibility.
- **State-machine/model tests:** randomized insert/replace/delete/version-conflict sequences compared
  with a simple reference model. After every operation, assert that the document, primary directory,
  membership record, equality/range entries, count, and unique claims agree.
- **Real-engine transaction tests:** no mocked Garnet. Inject rejection and exception points before
  and after each procedure mutation, then inspect all keys. Record whether post-mutation exceptions
  roll back; design to observed behavior.
- **Concurrency tests:** same-ID writers, delete-versus-save, unique-value races, membership changes,
  hot index values, catalog cutover during reads, and ambiguous client disconnects. Force the
  `Prepare` discovery race and prove watch/version retry behavior.
- **Durability tests:** clean restart, process kill at each mutation/log boundary, checkpoint plus AOF,
  truncated/corrupt tail behavior, disk-full and write-error behavior, and repeated recovery. Golden
  fixtures must span procedure/catalog versions and registration orders.
- **Index lifecycle tests:** build from a changing collection, catch-up/cutover, cancellation,
  restart midway, validation, rollback to old index, and token failure after catalog version change.
- **Cluster probe and later qualification:** same-slot enforcement, cross-slot rejection, `MOVED`,
  `ASK`, migration fencing, replica replay, primary loss, asynchronous replication loss window,
  control-plane failover, rolling mixed-version nodes, and catalog/procedure deployment ordering.
- **Long soak and resource tests:** uniform and Zipfian workloads, large/hot documents, GC pauses,
  memory-accounting drift, disk-tier cold reads, checkpoint interference, AOF growth/rewrite, file
  descriptor/socket stability, and subscriber churn.
- **API/compatibility tests:** analyzer diagnostics match runtime validation; every public operation
  has protocol vectors; unknown server/client/catalog/procedure versions fail closed with actionable
  errors.

### Required performance and capacity measurements

Every result must include Garnet commit/version, Stow commit, OS/runtime, CPU, RAM, storage, server
options, durability mode, dataset generator, warm-up, run length, and raw output. Report median,
p95, p99, maximum, throughput, allocation rate, GC pause/time, resident/managed memory, disk bytes,
AOF/checkpoint size, and recovery time; averages alone are insufficient.

| Measurement | Required matrix / question |
|---|---|
| Baselines | Raw `SET`/`GET`; document only; document + primary directory; +1 equality; +1 range; +unique; replace changing every membership; delete |
| Document sizes | 256 B, 1 KiB, 16 KiB, 256 KiB, 1 MiB; reject-path behavior at and above caps |
| Cardinality | 100K, 1M, 5M, and **10M** documents; measure resident memory and checkpoint/AOF/recovery at each point |
| Primary directory | One 10M-member sorted set versus segmented alternatives; insertion/deletion lock contention, enumeration, count, memory after GC, checkpoint and recovery time |
| Secondary indexes | Low/high cardinality; uniform and Zipfian values; 1/3/5 indexes; emit-many bounds; hot-value key contention and managed-object growth |
| Concurrency | 1/4/16/64 clients; point reads, independent writes, same-document races, and same-index-hot-value writes |
| Durability | No AOF, every-second, and strict durability modes supported by the chosen Garnet version; quantify latency and acknowledged-write loss windows |
| Tiering | Hot and cold point reads at 5 GB and 10 GB logical datasets; read-cache on/off; verify the unified memory budget and heap accounting rather than assuming indexes are separately resident |
| Maintenance | Checkpoint, AOF recovery, index build/cutover, dump, restore, and integrity scan while foreground load continues |
| Cluster (later) | Direct owner versus redirection, migration, replica lag, failover, topology refresh, and mixed-version rollout |

Before running, record an acceptance envelope. Recommended Phase 0 starting gates on a documented
commodity 8-core/32-GB/NVMe host are: 1 KiB documents with primary + one equality + one range index;
p99 point reads below 10 ms and p99 acknowledged writes below 20 ms at 70% of measured saturation;
indexed-save throughput at least 40% of same-durability raw Garnet `SET`; no unbounded resident-memory
growth during a 24-hour steady-state churn test; and successful restart/recovery of the 10M-document
dataset within 30 minutes. These are go/no-go product targets, not universal public promises. If
hardware or audience assumptions differ, change the envelope **before** observing results and record
why.

### Failure-mode review

| Failure | Required semantics | Proof / mitigation |
|---|---|---|
| Validation/version/unique rejection | No document or derived-state mutation | Preflight before first write; inspect every key after injected rejects |
| Exception during `Main` | Never assume rollback; either observed full rollback or a procedure with no fallible post-mutation path | Mutation-by-mutation fault injection and integrity scan |
| Process kill / power loss | Outcome follows documented AOF durability mode; recovery yields either complete old or complete new derived state, never a mixed committed state | Kill at log/checkpoint boundaries; repeated AOF recovery |
| Disk full / AOF write error | Fail closed or enter a clearly surfaced read-only/unhealthy state; never acknowledge durability not obtained | Quota/faulted filesystem test and health assertion |
| Client disconnect after send | Outcome may be ambiguous; retry with expected version/idempotency policy must not duplicate derived state | Disconnect injection; read-after-reconnect resolution API/docs |
| Concurrent save/delete | One version wins; membership and indexes correspond exactly to winning document or absence | Model/state-machine concurrency test |
| Unique-value race | Exactly one owner; losing operation makes no mutation | Barrier-synchronized competing procedures |
| Membership changes during `Prepare` | Retry/fail; never lock an obsolete subset and commit partial cleanup | Watched membership/version test with forced interleaving |
| Pub/sub loss/disconnect | Commit remains correct; watcher may miss event and must re-read/resync | Subscriber churn test; explicit best-effort contract |
| Index build interruption | Existing index remains authoritative; partial new index is resumable or safely discardable | Kill/restart at each build phase; catalog pointer checks |
| Catalog/client mismatch | Operation rejected before mutation with supported-version information | Mixed-version compatibility suite |
| Unknown/changed AOF procedure ID | Startup/recovery fails clearly; never invoke a different procedure by accident | Golden old-AOF fixtures and stable manifest validation |
| Checkpoint/AOF corruption | Detect and stop or recover only according to documented engine guarantee; run integrity verification | Truncation/bit-flip fixtures and recovery runbook |
| Hot key / cardinality explosion | Bound latency and memory or reject index declaration/write before destabilization | Zipfian load, quotas, emitted-entry/key-count limits |
| Replica lag / primary loss | Documented RPO/RTO; no claim of synchronous durability | Cluster chaos and measured lag/loss window |
| Slot migration (`MOVED`/`ASK`) | No unsafe blind replay; retry only when operation outcome is known or version-resolvable | Migration-fencing and redirection suite |
| Backup during writes | Restore is internally consistent to the documented point; otherwise backup is explicitly offline | Restore-and-integrity test, not backup-command success alone |
| ICU/runtime/serializer upgrade | Incompatible index/envelope detected and rebuild/migration required | Version stamps and cross-runtime golden fixtures |

### Build now, build later

**Build now:** only Phase 0, as a disposable-but-well-instrumented engineering proof. Implement the
smallest real procedure for a document envelope, fixed membership record, primary-directory
candidate, one equality index, one range index, and one unique claim. Include a tiny .NET driver,
reference model, fault hooks, integrity scanner, benchmark harness, stable procedure manifest, and
AOF fixtures. Run it in embedded, standalone, and a minimal three-node cluster probe. The spike's
output is evidence and a go/no-go report—not production API polish.

**Build after the gates:** the single-node first-v1 scope above: stable protocol/catalog, hardened
client, analyzer, backup/restore, index lifecycle, observability, security, packaging, docs, and
long-running qualification.

**Build later:** cluster productization, durable feed/outbox, richer indexes, indexed TTL, culture
collation, server patches, idempotent/retryable writes, GUI explorer, CMS reference app, static
partitioning, and preview Garnet structures. Build each only from demonstrated user demand and a
new compatibility/capacity budget.

### Phase 0 exit criteria

Phase 0 passes only when all of the following are checked into the independent Stow project with
reproduction commands and raw artifacts:

1. **Transaction semantics:** a written mutation sequence and fault matrix demonstrate either true
   rollback or, more realistically, complete preflight plus a commit section with no expected
   post-mutation failures. Every injected application-level failure leaves document and all derived
   state unchanged.
2. **Membership correctness:** the watched/versioned `Prepare` design survives forced concurrent
   membership changes and at least one million randomized model operations with zero integrity
   mismatches. If no safe discovery mechanism exists, an alternative deterministic/segmented layout
   is implemented and measured.
3. **Trust boundary:** a stale or hostile client cannot select physical keys, bypass bounds, use an
   unknown catalog version, or cause accepted document/index disagreement. Server authority and the
   extraction/validation mechanism are documented.
4. **AOF/recovery compatibility:** clean restart, checkpoint+AOF recovery, process-kill recovery, and
   golden replay across two procedure versions and changed registration order all produce the
   expected state or fail closed before serving traffic.
5. **Unified-memory capacity:** measured 1M/5M/10M datasets show where raw records and managed
   structures reside, how the configured budget behaves, and whether 5–10 GB is viable on the
   declared hardware. A 10M-member primary-directory decision is made from measured alternatives,
   not retained by default.
6. **Performance envelope:** the documented baseline matrix is complete and the agreed p99,
   throughput-ratio, 24-hour bounded-memory, and recovery-time gates above pass without disabling the
   intended durability mode.
7. **Cluster path:** a three-node probe proves custom procedure registration/execution, same-slot
   enforcement, replica recovery, redirection handling, and behavior during a controlled migration
   and failover—or the project explicitly removes cluster growth from its positioning and plan.
8. **Notification honesty:** tests and API text show commit correctness is independent of pub/sub;
   disconnects lose notifications without losing data, and watchers always re-read.
9. **Operational escape hatch:** the spike can export, restore into a fresh instance, and integrity-
   scan its representative dataset; restored document/index/version state matches the source.
10. **Upstream-risk register:** every depended-on Garnet behavior is pinned to a tested version and
    classified as documented, source-observed, or experimentally inferred, with an owner and
    fallback for unresolved assumptions.
11. **Scope and schedule approval:** the team accepts the first-v1 and explicit non-v1 lists above,
    the 9–14 month single-node estimate, and the 14–20 month cluster-ready estimate. No roadmap item
    is pulled forward merely because generated code makes the happy path look cheap.

If any criterion fails, the correct outcome is not to soften the gate. Either redesign the narrow
primitive and rerun Phase 0, narrow the product claim (for example, single-node only), or stop before
committing to a database support burden.

### Final decision

**Build Phase 0 now. Authorize the Stow product only if all gates pass.** Until then, preserve the
original research as valuable hypothesis and direction—not as proof of rollback, capacity,
recoverability, or cluster product readiness.