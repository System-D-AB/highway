# Feature 043 — Formal Model: Tasks

*Implements [`requirements.md`](requirements.md) per [`design.md`](design.md). Safety-first sequencing:
the highest-value invariants (no-dual-writable, epoch) land before liveness and GOODBYE, so the feature
delivers value even if it stops after safety. **Blocked on Claude's 042 implementation settling** — the
protocol must be stable enough to model.*

## Order and dependency graph

```
T0 (gate: 042 settled — snapshot the protocol to model)
      │
      ▼
T1 (toolchain: tla2tools.jar, formal/replication/ skeleton, run.ps1)
      │
      ▼
T2 (model the role/epoch state machine + SAFETY invariants)      ── first value: NoDualWritable, EpochSafety
      │
      ▼
T3 (add crash / partition / heal / rejoin actions + logical time)
      │
      ├─► T4 (herd + willingness + herd-arrival promotion; AtMostOneMasterWithClients)
      │        │
      │        ▼
      │   T5 (message log + watermark; NoAckedReplicatedLoss + RPO window)
      │        │
      │        ▼
      │   T6 (GOODBYE drain: quiesce + terminates)
      │
      └─► T7 (liveness: HerdConverges, WillingEventuallyServes, NoLivelock, under fairness)
                 │
                 ▼
      T8 (scenario reachability — prove R5 states reached; green run is non-vacuous)   *(GATE)*
                 │
                 ▼
      T9 (run TLC on the bounded config; triage EVERY counterexample; FINDINGS.md)
                 │
                 ▼
      T10 (feed confirmed defects back: dated 042/042-1 addenda + code-fix issues w/ trace)
                 │
                 ▼
      T11 (record: constraints.md dated note — protocol model-checked, bound + properties named)
                 │
                 ▼
      T12 (STRETCH, optional: trace-validation — C# execution traces vs the spec)
```

---

### - [ ] T0 — Gate: 042 settled; snapshot the protocol to model

**Fulfills:** dependency (requirements §Sequencing)
Confirm Claude's 042 implementation has settled (herd model of 042-1 is the version to model; RD6's
deadman is the backstop it became). Snapshot the exact protocol: read `ReplicationFeeder.cs`,
`Willingness.cs`, `CommandDispatcher.cs` (writability gate + herd-arrival + GOODBYE quiesce),
`HighwayConnectionSource.cs`. Write a one-page "protocol as-built" note in this feature recording the
state machine, the willingness rule, the timeout constants, and the transition triggers **as shipped**.
**Done when:** the as-built note exists and matches the code; discrepancies between 042/042-1 specs and
code are listed (they become model decisions, not silent assumptions).

### - [ ] T1 — Toolchain and skeleton

**Fulfills:** R1 (setup)
Set up TLA+/TLC: `tla2tools.jar` (or the Toolbox) runnable from CLI; create `formal/replication/` in the
repo with a `Replication.tla` skeleton, a `Replication.cfg`, the needed community modules, and a
`run.ps1` that invokes TLC and prints the result. No model logic yet — just a spec that declares the R1
constants (Nodes, Clients, priorities, `x`/`W`/`FenceT`/`PromoteT` as ordered nats) and type-checks.
**Done when:** `run.ps1` runs TLC on an empty-but-valid spec and reports success; the constant ordering
`x <= W < FenceT < PromoteT` is asserted and TLC accepts it.

### - [ ] T2 — Role/epoch state machine + the first safety invariants

**Fulfills:** R1, R2.2, R2.3
Model `role` (Primary/Replica/Fenced/Demoted/Down), `epoch`, and the transitions `Fence`, `Unfence`,
`ObserveHigherEpoch` (Primary/Fenced→Demoted, Replica→adopt), and an explicit `Promote` (herd-arrival is
added in T4). Check **`NoDualWritable`** and **`EpochSafety`** (monotone + higher-epoch-wins) on this
core. Include the negative check: setting `W >= FenceT` (breaking the ordering) must produce a
dual-writable counterexample — proving the ordering is load-bearing (R2.2).
**Done when:** TLC checks `NoDualWritable` + `EpochSafety` green on the role/epoch core; the broken-order
negative check produces the expected counterexample. Each action cites its `ReplicationFeeder` member.

### - [ ] T3 — Crash / partition / heal / rejoin + logical time

**Fulfills:** R1.2, R5 (mechanics)
Add `Tick` (advance `silence`), `PeerContact`, `Crash`, `Recover` (as Replica, `seenMaster:=FALSE`),
`Partition`/`Heal` (flip `link`), `ClientPartition`/`ClientHeal` (flip `clientLink`). Wire `silence` into
`Fence` (past `FenceT`, and only when herd-less — added in T4). Keep time logical: thresholds compared to
a node's own `silence`, never to another node's clock.
**Done when:** the failure/partition actions exist and TLC still checks T2's safety invariants green with
them enabled; the state space stays tractable at 2 nodes.

### - [ ] T4 — Herd, willingness, herd-arrival promotion + the central invariant

**Fulfills:** R1, R2.1, R2.5
Model `conn` (client→node), `Herd(n)`, `Willing(n)` (the `Willingness.Decide` rule incl. the `seenMaster`
refinement), `ClientConnect`/`ClientDrop` (walk to a reachable willing/primary node), and
`HerdArrivalPromote` (a `Willing` node promotes on a client verb — "the verb IS the promotion"). Add the
master-only-action guard (enabled only when `Writable(n) /\ Herd(n) # {}`). Check
**`AtMostOneMasterWithClients`** (R2.1) and the herd-less-no-master-only property (R2.5).
**Done when:** TLC checks `AtMostOneMasterWithClients` green across crash+partition; the doubly-confused
client (R5.3c) cannot mint a second master; a herd-less node performs no master-only action. Actions cite
`CommandDispatcher` / `HighwayConnectionSource` / `Willingness`.

### - [ ] T5 — Message log + watermark; the no-loss invariant and the RPO window

**Fulfills:** R2.4, R4
Add the abstract `log` (per-node seq of opaque msg ids), `watermark`, `acked` (set), and actions `Ack`,
`Replicate` (append + advance watermark), `ClientReplay` (re-drive an unacked id to the new node). Model
`Crash` losing the *unreplicated* tail. Check **`NoAckedReplicatedLoss`** (an acked, replicated msg is on
whatever node the herd converges to) and confirm loss is **confined** to the acked-but-not-replicated RPO
window (never a replicated msg lost; never silent).
**Done when:** TLC checks `NoAckedReplicatedLoss` green; a deliberate "lose a replicated msg" mutation
produces a counterexample; the RPO-window loss is reachable but bounded (matches 042 R9 / 042-1 R5).

### - [ ] T6 — GOODBYE drain

**Fulfills:** R2.6, R5.7
Add `goodbye`, `BeginGoodbye` (quiesce: no new master-only action admitted while draining),
`CompleteGoodbye` (deadline or herd-left → Demoted). Check the GOODBYE quiesce invariant and that the
drain **always terminates**. Confirm GOODBYE-mid-traffic (R5.7) is reachable and loses nothing replicated.
**Done when:** TLC checks the quiesce invariant green and drain-termination as a liveness property; the
GOODBYE scenario is reachable and preserves `NoAckedReplicatedLoss`.

### - [ ] T7 — Liveness under fairness

**Fulfills:** R3
Declare fairness (weak fairness on progress actions — `Tick`, `ClientConnect`, `HerdArrivalPromote`,
`Heal`; **never** on `Crash`/`Partition`). Check **`HerdConverges`** (eventually all connected clients
share one master), **`WillingEventuallyServes`**, and **`NoLivelock`** (no infinite promote/demote with
nobody stably serving).
**Done when:** TLC checks the three temporal properties green under the stated fairness; the fairness
constraints are documented so a reviewer can judge them; a mutation that permits ping-pong mastership
produces a `NoLivelock` counterexample.

### - [ ] T8 — Scenario reachability *(GATE: a green run must be non-vacuous)*

**Fulfills:** R5
For each R5 scenario (hard crash, frozen-but-TCP-alive, partition matrix a/b/c, rejoin-without-preemption,
stale-island-at-epoch-boundary, GOODBYE-mid-traffic), add a negated-reachability lemma (an invariant that
*should* fail because the state is reachable) or a TLC coverage check, proving the model actually reaches
that state. A safety run that never reached these would be a false green.
**Done when:** every R5 scenario is shown reachable; the coverage/reachability evidence is recorded, so
the T9 green run is known to have exercised the interesting states.

### - [ ] T9 — Run TLC on the bounded config; triage every counterexample

**Fulfills:** R6, acceptance
Run TLC on the design's bounded config (2 nodes → 3 nodes → optional 4; 2 clients; epoch≤4; |Msg|≤3;
symmetry over clients; `StateConstraint`). For **every** invariant/property: pass, or a counterexample.
Triage each counterexample into: (a) real protocol defect, (b) modeling error, (c) invariant too strict.
Record all in `FINDINGS.md` (property, verdict, trace, disposition).
**Done when:** every property in R2/R3/R4 has a verdict; `FINDINGS.md` records each, with traces for any
counterexample and its disposition; modeling errors are fixed and re-run; invariant-too-strict cases are
refined with a recorded reason.

### - [ ] T10 — Feed confirmed protocol defects back

**Fulfills:** R6.2
For each **real protocol defect** (triage category (a)): write a **dated addendum** to 042 and/or 042-1
(house rule — never rewrite the existing analysis) describing the defect and the corrected protocol, and
file a code-fix issue against the implementation with the **TLC trace attached** as the reproducer. If
there are none, record that explicitly (a clean bounded model is a valid, valuable outcome).
**Done when:** every category-(a) finding has a dated spec addendum + a code-fix reference with its
trace; a "no protocol defects found" outcome is recorded plainly if that is the result.

### - [ ] T11 — The record

**Fulfills:** R7
Add a dated note to `constraints.md` (extending the 042/042-1 failover constraints): the protocol's
safety and liveness invariants were model-checked with TLA+/TLC over a bounded cluster; name the config
bound and the checked properties; state the residual (unmodeled) risk honestly (bounded N, design-not-code
unless the stretch ran). Ensure `formal/replication/` is runnable (a maintainer re-runs TLC after a
protocol change).
**Done when:** the dated `constraints.md` note is in place, naming the bound and properties; the model +
`run.ps1` are committed and re-runnable from a clean checkout.

### - [ ] T12 — STRETCH (optional): trace validation — code vs the spec

**Fulfills:** Non-goal boundary (design §Trace validation)
Only if the core feature lands with time to spare. Instrument the C# implementation to emit a trace of
spec-variable updates (role, epoch, conn, watermark) per transition; use the TLA+ trace-validation
technique (arXiv 2404.16075) to confirm a real execution is a behaviour the spec allows. This narrows the
"does the code match the protocol" gap the core feature explicitly does not close.
**Done when:** a recorded C# execution trace validates against the spec (or a discrepancy is found and
triaged like a T9 finding). Explicitly optional — does **not** gate the feature.

---

## Gates

- **Gate G(safety):** T2 + T4 + T5 green — `NoDualWritable`, `EpochSafety`, `AtMostOneMasterWithClients`,
  `NoAckedReplicatedLoss` hold on the bounded model. The feature delivers real value at this point even
  if liveness/GOODBYE/stretch are deferred.
- **Gate G(non-vacuous):** T8 — the R5 scenarios are proven reachable, so a green safety run is known to
  have exercised the crash/partition/rejoin/timing states, not trivially passed.
- **Reuse, not replace:** the assurance rig and `HerdCohesionTests` remain the code-level assurance; this
  feature exhausts the interleavings they sample. Nothing shipped depends on TLA+/TLC.
