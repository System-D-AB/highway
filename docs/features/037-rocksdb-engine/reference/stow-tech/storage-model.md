# Storage model — where things physically live, and what they cost

*Authority: mechanics and measurements. For **why** the design is shaped this way see the
v2 decision record (V1–V15); for what we guarantee see
[`../product/constraints.md`](../product/constraints.md).*

**Engine:** RocksDB 11.1.2.3412 via the `RocksDB` NuGet package (Curiosity GmbH + Warren
Falk, BSD-2-Clause). Verified by the bake-off
(`../specs/v2-001-engine-bakeoff/artifacts/`).

---

## 1 · Architecture: LSM tree, single writer

RocksDB is a **log-structured merge tree**. Writes land in an in-memory **memtable**
(sorted by key), then flush as immutable **L0 SST files**, then compact through levels.
The whole lifecycle is:

```
 write → WAL  →  memtable  →  flush to L0 SST  →  compaction L0→L1→…→Ln
```

**One process only.** RocksDB takes an exclusive directory lock on open — no concurrent
writers, no multi-process access (C25). This is why an in-process striped lock suffices
for unique constraints (V9) and why it is an implementation detail hidden behind the
server, never a user-facing constraint (V16).

## 2 · Column families

Five column families, created in a **fixed, asserted order** (R3.2). Batches address
families by numeric id; a mismatched order writes to the wrong family silently.

| # | Name | Contents | Notes |
|---|---|---|---|
| 0 | `default` | unused | RocksDB requires it |
| 1 | `meta` | envelope + indexed values + inlined body | hot; sized for block-cache residency |
| 2 | `body` | separated payloads | cold; heavier compression |
| 3 | `index` | ordinary index entries (empty values) | |
| 4 | `uniq` | unique claims (id in value) | |
| 5 | `catalog` | structural metadata | tiny |

See [`document-layout.md`](document-layout.md) for the `meta` record format and
[`keyspace.md`](keyspace.md) for key layouts.

## 3 · Durability

**WAL (Write-Ahead Log).** Every `WriteBatch` is journaled before acknowledgement. The
WAL survives process crashes; on restart, un-flushed entries are replayed from it.

`WalTtlSeconds` is set at open even though replication is out of scope — without it WAL
segments recycle, and a later spec would inherit a store whose feed silently truncates.

**Snapshots** provide point-in-time reads. A snapshot is created and released within
each request (design § 5); holding one across requests would pin compaction — which is
why no public API exposes a held snapshot (V8 caution 2).

## 4 · Where data lives

| Temperature | Structure | Physical home |
|---|---|---|
| **Hot** | Meta records, block index, bloom filters | block cache (RAM) |
| **Warm** | Recently written data | memtable (RAM) |
| **Cold** | Body records, old SSTs | disk — read on demand via block cache |

The `meta`/`body` split exists so the hot path (index maintenance, stale-entry
computation, CAS checks) never reads large payloads. At 50.5 bytes per meta record
(measured, `artifacts/meta-footprint.md`), the meta CF stays block-cache resident up to
~170 M documents in an 8 GB cache.

## 5 · Write path cost

Measured with 2 indexes (equality + range), ~150 B documents
(`../specs/v2-002-storage-foundation/artifacts/write-throughput.md`):

| Writers | Throughput | p50 | p99 |
|---:|---:|---:|---:|
| 1 | 67 143/s | 13 µs | 45 µs |
| 4 | 95 585/s | 35 µs | 125 µs |
| 16 | 107 523/s | 138 µs | 346 µs |
| 64 | 84 151/s | 244 µs | 561 µs |

The bake-off measured 112 676/s at 64 writers with a simplified layout. The ~25% gap is
the cost of type checking, meta envelope construction, stale-entry computation, striped
lock acquisition, unique constraint checks, and counter merges — the cost of correctness.

There is **no per-collection write ceiling**. The v1 Garnet design peaked at 18.5 k/s
and degraded past 4 writers because of shared exclusive locks on collection objects.
Those locks do not exist here — each document's entries are independent keys in one
atomic batch.

## 6 · Read path cost

Point reads: **1.31 µs per id** (measured in the bake-off). `Get` reads `meta`, then
`body` only when the body is separated. `Exists` answers from `meta` key presence alone.
`GetMany` uses batch reads under one snapshot for consistency (R5.2).

## 7 · Compaction and deletion

Deleted keys become tombstones reclaimed at compaction. `DeleteRange` is used for
collection drops — measured at ~1 ms for 50 000 keys in the bake-off.

**C24:** deleted bytes persist until compaction. No encryption at rest (C23) — volume-level
encryption is the stated posture.

## 8 · Capacity

Measured at 1 M documents against a 32 MB block cache: **~89 MB process memory, flat**
(vs Garnet's 1 658 MB). Reopened in 558 ms. The 100–200 GB target (V2) is designed for
but **unpublished until measured at 50 and 200 GB with realistic documents** (C35).
