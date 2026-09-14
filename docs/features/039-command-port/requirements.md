# Feature 039 — The Command Port

*Executes 037 Phase 3: the 23 `HW.*` command files move from Garnet's `Prepare`/`Main`
transaction model onto `IHighwayStore` (038), preserving observable behavior exactly.
Authorities: 037 R1 (wire unchanged), R4/R5 (one batch, effects never intent), design
§2–§3 (what gets deleted rather than ported), physical-layout §3 (every key mapped).
This is where the port either keeps every promise or fails loudly enough to notice.*

## Requirements

### Requirement 1: The template is reviewed as a pattern (037 T3.1)

#### Acceptance Criteria

1. `HwQSendCommand` is ported whole first: read clock once → per-queue lock → snapshot
   → decide → one batch → commit. No `AddKey`, no `StoreType`, no mirrors, errors stay
   data (`Output.StatusCode`), `CancellationToken` observed.
2. The ported file is reviewed as **the pattern all 22 others follow** — its shape is
   the deliverable as much as its behavior.

### Requirement 2: All 23 commands run on the seam (037 R1.1, T3.2)

#### Acceptance Criteria

1. Every `HW.*` command compiles and runs against `IHighwayStore` with the Garnet type
   substitutions from 037 T3.2 replaced by our own; no `Garnet.*`/`Tsavorite.*` type
   remains in any command file.
2. **Reply shapes are byte-compatible** — existing reply-shape tests pass unmodified;
   where a command lacked one, a golden-reply test is added *from the current Garnet
   behavior before porting it*.
3. The sorted-set score **culture bug cannot recur**: a regression test runs a
   promote/fire path under a comma-decimal culture (the `6,39E+17` bug 037 records).

### Requirement 3: The mirrors collapse, provably (037 T3.3, Risk 4)

#### Acceptance Criteria

1. `QueueNodeList`, `JobIndex` and the self-mirror keys (physical-layout §3) have one
   source of truth: the set, read via `SetMembers`.
2. **Every former mirror reader has a before/after equivalence test**: seeded state →
   old answer (recorded from Garnet behavior) == new answer, including ordering
   sensitivity if any consumer depended on it.

### Requirement 4: Command logic is testable with no server (037 T3.4, R10.2)

#### Acceptance Criteria

1. Lease sweep, attempt counting, dead-lettering, delayed promotion, job firing and
   byte accounting run in-process against `InMemoryStore` — no socket, no broker.
2. The dispatch path is exercised without any transport (constructs a command, runs
   it, asserts the reply) — the 037 R10 transport-seam proof.
3. This suite becomes the fast tier the RESP server (040) and the full suite (041)
   sit above.

### Requirement 5: Determinism at the command layer (037 R5)

#### Acceptance Criteria

1. No `DateTime.UtcNow`/`Stopwatch`/clock read occurs inside any batch scope across
   all 23 files — enforced by an analyzer-style test over the command assembly, not
   convention.
2. The lease sweep persists produced rows, never intent (037 R5.2) — asserted on the
   swept output.
3. Kill-mid-claim crash test at the command layer: recovery yields byte-identical
   state (037 R5.3), on top of 038's store-level proof.

### Requirement 6: The claim path's semantics survive the model change

`HW.QCLAIM` is the most entangled command (seven-key lock set today, promotion, job
firing, lease rows). Singled out the way 037 §2 singles it out.

#### Acceptance Criteria

1. Claim under contention: two concurrent claimants on one queue get disjoint
   messages (per-queue lock), proven with real parallelism on `InMemoryStore`.
2. Promote-then-claim, fire-then-claim, and claim-empty paths each have an explicit
   test with the same observable results as today's suite expects.
3. Multi-entry claim uses one batch and pops distinct entries — the 038 read-view
   contract, exercised through the real command.
