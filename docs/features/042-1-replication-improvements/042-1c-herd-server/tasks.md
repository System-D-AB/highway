# Feature 042-1c — The Herd Server: Tasks

*Parent T2.5, T5, T6, T6.5, T7, T7.5, T8. Starts after 042-1a. C-T4 is the seam 042-1b's
integration (B-T6) waits on.*

### - [x] C-T1 — Herd state: client-session counting + standby master-link clock

**Fulfills:** C-R1.1
`RespSession`/`RespServer` count authenticated client sessions (peer connections excluded by
their `HW.REPL.*` preamble); feeder exposes `ConnectedClients` and the standby-side
`LastMasterContact` clock (fed by the puller's successful exchanges).
**Done when:** unit — a client session counts, a puller/peer does not; the clock stamps on
pull success.

### - [x] C-T2 — Roster: record, store, JOIN, collision, STATUS fields

**Fulfills:** C-R3
`RosterRecord` (versioned encode/decode) at KV `repl:roster`, master-writes-only through the
batch path; `HW.REPL.JOIN` (admit + version + narrate; `HW_PRIORITY_TAKEN` on collision;
`-NOTPRIMARY` on non-master); `HW.REPL.STATUS` gains `roster.*`; `ReplicaPuller` announces
JOIN after sync readiness with backoff. Departure pruning recorded as an OD, not built.
**Done when:** design's roster test rows green; a promoted standby serves the current roster
from its own store with no transfer step.

### - [x] C-T3 — Narration publish

**Fulfills:** C-R4.1
`hw:door:topology` messages (TOPOLOGY / ROSTER-UPDATE / GOODBYE, contract grammar) published
via `SubscriptionRegistry`; emission on roster change, observed peer loss, and GOODBYE.
**Done when:** integration — a subscribed raw client receives each shape; loss of delivery
breaks nothing (advisory asserted by killing the subscriber mid-push).

### - [x] C-T4 — Willingness answer + herd-arrival promotion *(the 042-1b seam)*

**Fulfills:** C-R1.2, C-R2
`HW.REPL.HELLO CLIENT` per the contract (willing / unwilling+redirect); `WillingnessThreshold`
config + `x ≤ W < T_fence` validation; the dispatcher's first-accepted-verb promotion hook
(probes never promote; concurrent firsts promote once); transition recorded as
`herd-arrival`.
**Done when:** design rows for willingness + herd-arrival green; 042's promote announcement
observed firing toward the old master on herd-arrival.

### - [x] C-T5 — GOODBYE drain + host verb

**Fulfills:** C-R4.2, C-R4.3
The Draining→StoodDown state machine with `GoodbyeDrainTimeout` (default per parent OD7);
`HW.REPL.GOODBYE` admin command + `highways --goodbye`; new master-only work refused during
drain; reconciliation report only when a tail exists; ignoring clients converge via
`-NOTPRIMARY`.
**Done when:** drain-completes, deadline-degrades, and ignore-the-push rows green; host verb
drives it end-to-end against a running broker.

### - [x] C-T6 — Heal re-assertion + no-preemption

**Fulfills:** C-R5
Re-run/reframe 042's higher-epoch stand-down, reconciliation-report and islanded-harmless
tests under the herd model; add the rejoin test: higher-priority node joins while a
lower-priority master serves — roster updated, herd unmoved, returner unwilling while its
master-link is healthy.
**Done when:** all green; an explicit assertion that no preemption path exists (C-R6.1's
guard covers timers; this covers rejoin).

### - [x] C-T7 — One model: deadman → backstop, witness deleted

**Fulfills:** C-R6 (cites 042-1a's reconciliation map)
Delete the witness apparatus and the timer-promote branch per design §Deletion; `WITNESS`
reverts to bare probe; options surface updated (add `WillingnessThreshold`,
`GoodbyeDrainTimeout`; remove `WitnessServer`) **with the host schema + schema-completeness
test in the same commit** (031 R2.1); reframe 042's fake-clock tests (fence coverage kept,
promotion tests replaced by C-T4's).
**Done when:** the guard test proves no timer-promotion path; full server suite green;
protocol doc already matches (042-1a A-T3).

> **Mostly executed early with 042-1a (2026-09-15)** so protocol doc, code and conformance
> landed coherently: witness apparatus + timer-promote branch + stagger deleted;
> `WitnessServer` removed and `WillingnessThreshold` added (schema updated in step);
> `WITNESS` reverted to the bare probe; 042's fake-clock tests reframed
> (`Deadman_SilentReplica_NeverSelfPromotes_ButBecomesWilling` replaces timer promotion).
> Remaining for this task: `GoodbyeDrainTimeout` (with C-T5) and the explicit
> no-timer-promotion guard test.

### - [x] C-T8 — Server-scoped integration

**Fulfills:** C-R7.2
Pair fixture: join → roster narrated → promoted standby holds roster; GOODBYE moves a
connected raw client; fence backstop still fences an islanded ex-master.
**Done when:** green; hands the baton to 042-1d for cohesion/partition scale.

---

## Completion record (2026-09-16)

All eight tasks done; server suite 530/530, host 70/70, pair integration 8 green
(+1 skip owned by 042-1b). Notes and honest partials, for 042-1d to pick up:

- **Contract amendment (recorded in 042-1a design D1, dated):** the willingness predicate
  gained `hasSeenMaster` — a standby that never reached its master waits the outer
  `T_fence` bound, not `W`, or a slow-starting standby promotes under a living master.
  Found by the herd-arrival integration test.
- **C-T2:** roster **departure pruning** is an open decision (not built) — an operator
  removes a member by re-announcing or a future admin verb; record as OD in 042-1d's
  scope if it bites the harness.
- **C-T3 partial:** narration emits on roster change (`ROSTER-UPDATE`), GOODBYE, and
  herd-arrival promotion (`TOPOLOGY`). The "master observes a *peer* loss" emission point
  has no observer wired yet (the master learns of standby silence only via slot lag) —
  042-1d may add it if the harness shows convergence needs it; the TCP-drop walk covers
  the herd regardless.
- **C-T6:** heal + no-preemption are proven structurally (healthy-link standby answers
  `standby`; no timer promotes — the guard test) plus 042's re-passing heal tests; the
  full rejoin-at-herd-scale scenario is 042-1d D-T4's named test.
- **`HW.REPL.GOODBYE`** landed as protocol v4.8 surface with `highways --goodbye`.
