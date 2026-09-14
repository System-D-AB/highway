# Feature 038 — The Storage Engine

*Executes 037 Phase 2 (plus its Phase-0 paper decisions): the seam (`IHighwayStore`),
both implementations, the physical layout, and the transactional write path.
Authorities: 037 R3–R5,
[physical-layout.md](../037-rocksdb-engine/physical-layout.md) (the key families,
encodings, column families, seq allocation), and the **imported sibling evidence**
(`C:\Software\ai\stow-rocksdb\spike`, spec `v2-001-engine-bakeoff`): multi-CF atomic
`WriteBatch`, snapshot isolation, ordered iteration, `DeleteRange`, `SetSync`, working
`GetUpdatesSince`, and 84–112 k indexed saves/s (~89 MB flat at 1 M) on the same
`RocksDbSharp`. The draft seam + layout code under `src/Highway.Server/Storage/` is
the starting point, not a green field.*

## Requirements

### Requirement 0: The paper decisions are recorded before code (037 Phase 0)

#### Acceptance Criteria

1. **OD1**: a throughput target with workload shape in `constraints.md` against C5,
   chosen against the imported bake-off numbers — no measurement code.
2. **OD2**: C4.1, C4.7, C9, C19 each adopted/deferred-with-reason (C19 decided against
   the *proven* `GetUpdatesSince`).
3. **WAL sync policy**: sync-per-commit or periodic-with-stated-window — the durability
   wording 041's C4.x amendments will carry.
4. **Read-view mechanism**: `WriteBatchWithIndex` vs managed overlay — the one RocksDB
   question the sibling never answered (its write path reads before staging; verified).
   Decided here, then **pinned by R2's contract tests**, so a wrong guess fails fast in
   this feature.

### Requirement 1: The seam is finalized — gate G1 (037 R3)

#### Acceptance Criteria

1. `IHighwayStore` covers the four families + snapshot/batch + `DeleteRange` +
   `SetMembers`, exactly as drafted; any change from the draft is justified against a
   real call site (derived, not designed — 037 R3.1).
2. **No engine type on the seam**, asserted by a reflection test over the interface's
   full member surface, not by review alone.
3. `Highway.Server` compiles against it — **G1**; failure here stops 041 from starting.

### Requirement 2: The contract is defined by tests, then satisfied twice (037 R3.3, R3.4)

#### Acceptance Criteria

1. A **contract test suite** runs identically against both implementations and defines
   the seam's semantics, including at minimum: FIFO order across push/pop; **multi-pop
   in one batch returns distinct entries** (R0.4's mechanism, asserted);
   pop-after-push-in-same-batch; drain-then-push-back in one batch; `Increment`
   visibility within its batch and monotonicity under the per-key lock; sorted-set
   range bounds inclusive/exclusive semantics and the `limit`; member ordering;
   `SetAdd`'s added/existed answer; idempotent deletes; `DeleteRange` prefix bounds
   (`ab` must not delete `abc…` under a *sibling* name — the order-preserving
   encoding's self-delimiting property, asserted).
2. `InMemoryStore` is written **first** and passes the suite — the contract comes from
   what commands need, not what RocksDB happens to do.
3. `RocksDbStore` passes the identical suite.

### Requirement 3: The physical layout is realized as specified

#### Acceptance Criteria

1. Keys follow physical-layout.md §2–§4 exactly: family tags, order-preserving string
   and int64 encoders, big-endian seqs. The layout doc is amended in the same feature
   if reality diverges — never silently.
2. Column families per §6, order **asserted on open**.
3. Per-list seq allocated **inside the same batch** via the counter family (the B1
   trap, §5); the head-push scheme (§5, default scheme 1) is chosen and recorded in
   the layout doc.
4. Reply-slot expiry (OD5) is decided and implemented here — expiry field filtered
   on read with physical cleanup, or compaction filter — and recorded; a test proves
   an expired slot is unreadable *and* eventually physically gone.

### Requirement 4: One transactional write path (037 R4, R5.1)

#### Acceptance Criteria

1. Exactly one commit point (`IStoreBatch.Commit`), verified by search-assert test.
2. Fault injection between two must-be-atomic writes leaves **no partial state**.
3. No clock read inside a batch — enforced by test/analyzer over the store layer.
4. Store-level crash test: ungraceful kill mid-commit-stream, reopen, state is
   **byte-identical** to a reference (037 R5.3 at the store layer; 041 re-proves it at
   the command layer).

### Requirement 5: The dependency ships correctly

#### Acceptance Criteria

1. `RocksDbSharp` pinned; the **native library reaches the distribution artifacts**
   for win-x64 and linux-x64 (the 031/035 zip + package pipeline), verified by the
   existing distribution verification script.
2. The WAL sync policy from R0.3 is the implemented default and is configurable only
   if R0.3 said so.
