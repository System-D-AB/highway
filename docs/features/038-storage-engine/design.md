# Feature 038 — Storage Engine: Design

Physical design lives in [037's physical-layout.md](../037-rocksdb-engine/physical-layout.md)
and is **not restated**. This file covers only what that document left to
implementation.

## What exists vs what this feature builds

| Exists (draft, compiles) | This feature builds |
|---|---|
| `IHighwayStore`, `IStoreBatch`, `IStoreSnapshot` | contract test suite; any call-site-justified seam adjustments |
| `KeyWriter`, `KeyEncoding` (order-preserving encoders) | encoder property tests (round-trip, ordering, self-delimiting) |
| `HighwayKeyspace`, `HighwayNames`, `HighwayColumnFamilies` | `RocksDbStore` that opens the DB and wires them |
| — | `InMemoryStore` (first), seq allocator, expiry mechanism, fault injection + crash tests |

## Imported de-risking (2026-09-15)

The sibling's RocksDB probes (`C:\Software\ai\stow-rocksdb\spike\probes\rocksdb-*`,
findings in its `engineering/research/2026-08-13-v2-decisions.md`) already exercised,
on the same `RocksDbSharp`: multi-CF atomic `WriteBatch`, snapshot isolation, ordered
prefix iteration, `DeleteRange`, merge-operator surface, `SetSync`, and a working
`GetUpdatesSince` (three probes). T3 is therefore wiring proven primitives, not
exploring them — and the bake-off's **~89 MB flat at 1 M documents** is an early
preview of the C4.6 answer 041 must still measure on Highway's own traffic profile.

## The two mechanisms R0 decides, applied

- **Read-view (R0.4):** either `WriteBatchWithIndex` or the managed overlay
  (staged-key map inside the `IStoreBatch` wrapper) — the one question the sibling
  never faced. Either way the behavior is pinned by contract tests (R2.1), so 039's
  claim loop can pop N distinct entries in one batch without knowing the mechanism.
- **Durability (R0.3):** WAL always on; sync per the recorded policy. `Commit()` maps
  to `db.Write` with the chosen `WriteOptions`. Recovery mode and the CF-order assert
  follow the stow reference discipline (`reference/stow-engine/`).

## InMemoryStore shape

Per-store lock + `SortedDictionary<byte[], byte[]>` with a bytewise comparer — chosen
because it makes *ordering* behavior identical to RocksDB's by construction, which is
what the contract suite exercises. Batches stage into an overlay applied on commit;
snapshots are cheap copies of the map version. Simplicity over speed; it exists to
define semantics and to make 041's command tests fast.

## Increment: locked read-add-stage, no merge operator (proposed)

The seam's `Increment` returns the new value, which a merge operator cannot give
without a read anyway; and every increment site runs under the per-queue striped lock.
So: read (snapshot + batch overlay) → add → stage → return. The
`CounterMergeOperator` from the reference stays unused unless contention is *measured*
(OD1) to demand it. Recorded here so nobody ports the merge operator by reflex.

## Testing strategy

| Layer | Proof |
|---|---|
| Encoders | property tests: round-trip, byte-order = numeric/lexical order, `("ab","c") ≠ ("a","bc")` |
| Contract | one suite × two stores (R2) |
| Crash | kill the process mid-write-stream (child-process harness), reopen, byte-compare full dump |
| G1 | reflection test over seam surface + compile |
| Packaging | distribution verify script finds the native lib in both RIDs |
