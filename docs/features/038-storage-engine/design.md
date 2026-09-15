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

## T0 — the four paper decisions (recorded 2026-09-15)

Settled against the imported sibling evidence, no measurement code (R0). The first two
also carry dated addenda in `../../product/constraints.md`; all four are pinned below.

### OD1 — throughput target (R0.1)
**10 000 msg/s at 8 KB, 20 queues, 40 consumers, one broker, sync-per-commit.** A design
floor, not a measured claim. Chosen against the bake-off's 84–112 k indexed saves/s (a
heavier workload than a Highway enqueue), so it is comfortable headroom. Full rationale
and the "no figure published to users" caveat are in `constraints.md` C5. This is what lets
038 defer RocksDB tuning and the merge operator until the floor is *measured* at risk.

### OD2 — parked-constraint adoptions (R0.2)
| Constraint | Decision | Why (recorded in full at the cited home) |
|---|---|---|
| **C4.1** retention (100 days) | **Deferred** | Command-layer (frame timestamp + sweep), not storage-layer. 038 makes it cheaper later (the `z`-family range-by-time pattern); adopts nothing. `constraints.md` C4.1 addendum. |
| **C4.7** process-wide byte budget | **Deferred** | Global accountant + cross-structure eviction policy — unchanged by the engine swap, command-layer. `constraints.md` C4.7 addendum. |
| **C9** per-message TTL | **Deferred** | The `SetEx` primitive 038 builds for reply slots (OD5) is the *mechanism* a per-message TTL would use, so 038 leaves the door open — but per-message TTL is a protocol/command decision (a new arg + a sweep), not storage. Not adopted. |
| **C19** change feed | **Deferred, but de-risked** | `GetUpdatesSince` is proven working in the sibling (three probes), so the primitive C19 was postponed for *now exists*. Adopting it is a real feature (a resumable client-facing feed with its own protocol surface), deliberately not smuggled into 038. Recorded so it is a decision, not an omission. |

### WAL sync policy (R0.3)
**Sync-per-commit is the default.** `IStoreBatch.Commit()` maps to `db.Write` with
`WriteOptions.SetSync(true)`. This is the durability wording 041's C4.x amendments will
carry: an acknowledged write is on disk, not merely in the OS page cache. It is *not*
configurable in 038 — a periodic-fsync fast path is a measured, opt-in future change gated
on OD1 pressure, not a launch knob. (The one exception is cache writes, which 041 will make
`disableWAL` — out of scope here.)

### Read-view mechanism (R0.4)
**Decided WBWI; implemented the managed overlay — a recorded deviation.** The read-your-own-writes
need is real: `ListLeftPop` after a `ListRightPush` in the same batch, and `Increment` reserving a
seq a push in the same batch consumes, must both see staged writes. `WriteBatchWithIndex` (WBWI) is
the engine's own answer and was the stated primary. **In implementation (T3) the managed overlay
was chosen instead** — a `SortedDictionary` overlay inside the `IStoreBatch` wrapper, read
overlay-first-then-DB. Two reasons: (1) its correctness is verified by the *same* contract suite
that already passed in-memory, so there is one read-your-own-writes implementation, tested once and
shared in shape by both stores; (2) WBWI's merged-iterator behaviour under CF-scoped snapshots is a
binding detail this feature cannot measure, and betting the store on it unverified is the larger
risk. **WBWI remains the documented optimisation** if the overlay's per-batch allocation is ever
measured to matter (OD1) — the seam does not change either way. **Pinned by R2's contract tests**
(pop-after-push, multi-pop distinctness, increment-visibility-within-batch), which pass identically
on both stores — so the mechanism is proven, not assumed.

## OD5 — reply-slot expiry (decided T5, 2026-09-15)

**Expiry field filtered on read, with lazy + explicit physical cleanup — not a compaction
filter.** A `SetEx` value is stored as `[8-byte absolute-expiry ticks BE][value]`. `Get`
reads the header: if `now >= expiry` the key reads as **absent** (the contract an expired
slot must satisfy immediately), and the expired key is **staged for physical delete** on a
best-effort basis so a read both hides and reclaims it. A `SweepExpired(nowTicks)` store
method physically removes every expired KV key in one batch — called by the reply-slot
maintenance path and by the T5 "physically gone" test.

Why not a RocksDB compaction filter: it would be RocksDB-only, so the two stores would no
longer behave identically (the property the whole contract suite rests on), and compaction
timing is non-deterministic — untestable as "physically absent after cleanup." The
filter-on-read mechanism is a few lines, identical on both stores, and deterministically
testable. `now` is passed **in** to `Get`-with-expiry and `SweepExpired` (never read inside
the store — 037 R5.1); the command layer reads the clock once and passes the value, exactly
as `SetEx` already takes an absolute `expiresAtTicks`.

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
| Contract | one suite × two stores (R2) — `StoreContractTests` bound to `InMemoryStoreContractTests` + `RocksDbStoreContractTests` |
| Crash | kill the process mid-write-stream (`Highway.Storage.CrashHarness` child process), reopen, byte-compare the committed entries |
| G1 | `GateG1Tests`: reflection over the seam surface (no engine type), single `Commit` + single `db.Write`, no clock in the store layer |
| Packaging | native lib present for the host RID now; the cross-RID (win-x64 + linux-x64) distribution assert is deferred to **041**, where the packaged server first references RocksDB — 038's server build proves the package resolves its native asset |

> **R5.1 scope (T7, 2026-09-15).** The distribution zip does not yet contain RocksDB — the
> `highways` server still runs on Garnet until 041 swaps the engine. So the "native lib
> reaches win-x64 + linux-x64 artifacts" assert cannot run meaningfully in 038: there is no
> RocksDB-bearing distribution to check. 038 proves the weaker, real thing — the RocksDB
> native library resolves into `Highway.Server`'s build output for the host RID (else no
> `RocksDbStore` test could open a DB). The cross-RID distribution assert moves to 041's
> packaging task, noted here so it is deferred, not dropped.
