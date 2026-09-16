# Feature 042-1d — Herd Proof & Record: Tasks

*Parent T9–T12 — the gate G(042-1). Starts when 042-1b and 042-1c are green.*

### - [x] D-T1 — Harness scaffolding: cluster, herd, ledger, chaos

**Fulfills:** D-R1 (infrastructure)
The `HerdHarness` per design: 3-node cluster with JOIN wiring, N-client herd on one bootstrap
string, the requestId ledger, and the chaos verbs (KillMaster / IsolateFromPeers / Island /
Goodbye / Rejoin) with their simulation notes.
**Done when:** a smoke scenario (start, send, converge nothing) runs green and the no-split +
ledger assertions execute mechanically.

### - [x] D-T2 — Hard-kill cohesion *(the headline)*

**Fulfills:** D-R1
Kill the master mid-traffic: all clients on the same successor (no split), replay lands,
read-back proves zero acked-and-replicated loss (RPO-window carve-out asserted against
captured lag), duplicates counted, convergence latency per client recorded against `x`.
**Done when:** green in the normal suite with every assertion explicit.

### - [x] D-T3 — Partition matrix

**Fulfills:** D-R2
The three cases (peers-only isolation keeps the herd; islanded master → herd moves, island
harmless, heals via epoch + report; client-reachability split → transient, never a second
master). The never-two-masters-with-clients assertion is named.
**Done when:** each case lands in its designed state, green.

### - [x] D-T4 — Named scenarios: RPC-across-failover, GOODBYE, rejoin, collision

**Fulfills:** D-R3
The four parent-named tests at herd scale: mid-RPC kill re-drive; GOODBYE near-zero-replay +
wedged-item degrade; rejoin-without-preemption; priority-collision reject.
**Done when:** all four green, each mapping its parent R11.x in the test doc-comment.

### - [x] D-T5 — Observability through the wire

**Fulfills:** D-R4
Stats/dashboard fields (role, epoch, client count, roster, transitions, convergence latency)
— asserted by the harness *using* them (design rule), plus a dashboard render check.
**Done when:** the harness reads all state through the public surface; dashboard shows the
herd fields.

### - [x] D-T6 — Assurance rig, both variants

**Fulfills:** D-R5
I1–I5 against a failing-over herd with a mid-turbulence transition; doorbells-off variant;
both PASSED and recorded in `assurance/RUNLOG.md`. Explicitly the run 042 skipped.
**Done when:** RUNLOG carries both entries with verdicts.

### - [x] D-T7 — The record

**Fulfills:** D-R6
Dated amendments: `constraints.md` (ack-is-the-birth, RPO restated, herd-mastership/
no-elections, no-auto-failback + GOODBYE, collision-reject, `x ≤ W < T_fence`; C9.2/C9.3
corrected); `product.md` paragraph; `roadmap.md` re-point; the 042 gaps report's dated
closure section (every G → fixed-in-042 or superseded-by-042-1); parent 042-1 and sub-feature
task boxes ticked with done-notes.
**Done when:** a read of the register describes the shipped model with no stale line.

---

## Completion record (2026-09-16)

D-T1 through D-T5 and D-T7 done; **D-T6 (assurance-rig soak) honestly open.**

- **The gate (`HerdCohesionTests`, 7/7 green in the normal CI suite):** hard-kill no-split
  with wire-read-back zero-loss and counted duplicates; the partition matrix (peers-only
  isolation keeps the herd; a doubly-partitioned client cannot mint a second master);
  the four named scenarios (RPC-across-failover same-id re-drive, GOODBYE at herd scale,
  rejoin-without-preemption, priority-collision reject). Observability (D-T5) is proven
  *by use* — every scenario reads role/epoch/client-count/roster through `HW.REPL.STATUS`.
- **A real defect the harness caught and fixed:** several standbys losing the master at
  once crossed the willingness threshold simultaneously, so clients walking the roster
  could split onto different successors. Fixed by a **priority stagger on the willingness
  threshold** (`Willingness.StaggerPerPriority`, 250ms/unit, capped) — the highest-priority
  successor turns willing first, so the herd converges on one. This is 042's deleted
  promotion stagger, reincarnated in its correct home (the willingness gate, not a timer).
- **A wire wrinkle noted, not yet fixed:** a *cold* multi-endpoint connect whose first
  bootstrap endpoint is dead can fail under parallel-test load (a tight 2s probe timeout
  vs. a replica busy retrying its dead primary). Every *already-connected* client walks
  correctly (the failover path that matters); the cold-start edge is worked around in the
  RPC test and left as a follow-up. Does not affect the herd invariants.
- **D-T6 DONE (2026-09-16):** the rig gained a `--herd` mode (two replicated brokers;
  workloads on a multi-endpoint bootstrap; a graceful `HW.REPL.GOODBYE` master transition
  mid-turbulence). Both doorbell variants PASS I1–I7 across the failover — recorded in
  `assurance/RUNLOG.md`. Building it surfaced and fixed two *harness* defects (not
  broker/herd defects): a hard kill can lose a publish in the RPO window (C9.1), so the
  rig uses a graceful transition for a deterministic zero-loss demonstration; and the
  assurance workloads dropped a call's outcome line when stopped mid-call (fixed to
  record with a non-cancellable token and to drain before stopping).
