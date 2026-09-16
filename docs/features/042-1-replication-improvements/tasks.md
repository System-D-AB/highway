# Feature 042-1 — Replication Improvements: Tasks

*Implements [`requirements.md`](requirements.md) per [`design.md`](design.md). Refines feature
[042](../042-replication/tasks.md); reuses its replication machinery and reframes RD6's deadman as a
backstop. **042 is being completed concurrently — T0 reconciles the two models before anything else
is built on top.***

> **Decomposed 2026-09-15** (owner's call: this is a long feature touching client and server;
> each half gets its own plan). The tasks below remain the authority for *what*; execution
> lives in four sub-features, in order:
>
> | Sub-feature | Executes | Why it is a unit |
> |---|---|---|
> | [042-1a-contract](042-1a-contract/tasks.md) | T0, T1 | The reconciliation map + every wire shape and cross-cutting decision (willingness, epoch-bump timing, roster-in-store, narration channel). Both halves implement against it — nothing else starts first. |
> | [042-1b-herd-client](042-1b-herd-client/tasks.md) | T2, T3, T4 | `Highway.Client`: bootstrap + roster cache, HerdConnection (walk, three triggers, health timeout `x`), InFlightCache + same-id replay. |
> | [042-1c-herd-server](042-1c-herd-server/tasks.md) | T2.5, T5, T6, T6.5, T7, T7.5, T8 | `Highway.Server`: herd state + willingness, herd-arrival promotion, roster/JOIN/collision, narration + GOODBYE drain, heal reuse, no-preemption, deadman→backstop + witness deletion. |
> | [042-1d-herd-proof](042-1d-herd-proof/tasks.md) | T9, T10, T11, T12 | The gate: multi-client cohesion harness, partition matrix, named scenarios, observability, assurance rig, the record. |
>
> 042-1b and 042-1c run in parallel after 042-1a, meeting at one seam (B-T6 ↔ C-T4).
> A parent task below is ticked when its executing sub-feature task closes.

## Order and dependency graph

```
T0 (reconcile with 042 in-flight code)
      │
      ▼
T1 (protocol: narration push + GOODBYE + join-announce + connection-string spec, HIGHWAY-PROTOCOL.md)
      │
      ├─► T2 (client: multi-endpoint bootstrap connection string + parse)
      │        │
      │        ▼
      │   T2.5 (server+client: membership/roster — join announce, master-owned roster,
      │          collision-reject, roster propagation, client bootstrap→roster)
      │        │
      │        ▼
      │   T3 (client: HerdConnection — successor from roster, one live master, health timeout x)
      │        │
      │        ▼
      │   T4 (client: InFlightCache + replay on convergence)     ── gate: RPC-across-failover test
      │
      ├─► T5 (server: NarrationPush — advisory topology push to connected clients)
      │
      └─► T6 (server: master-only verb gating — no herd ⇒ no QACK/DLQ/reply/JOB-fire; -NOTPRIMARY)
                 │
                 ▼
           T6.5 (server+client: graceful GOODBYE drain — bounded, degrades to replay)
                 │
                 ▼
           T7 (server: heal — higher-epoch standdown + reconciliation report reuse)
                 │
                 ▼
           T7.5 (server: no auto-failback — returning higher-priority node waits as standby)
                 │
                 ▼
      T8 (deadman → backstop: rewire 042 RD6 so promotion is herd-driven, timer is backstop)
                 │
                 ▼
      T9 (harness — GATE: herd cohesion under crash + partition + GOODBYE + rejoin + collision)
                 │
                 ▼
      T10 (observability: HW.STATS role/epoch/client-count/roster/convergence latency)
                 │
                 ▼
      T11 (assurance rig: mid-turbulence herd transition, doorbells-off, RUNLOG)
                 │
                 ▼
      T12 (the record: constraints.md + product.md + roadmap.md, dated)
```

---

### - [x] T0 — Reconcile with the in-progress 042 implementation *(042-1a A-T1/A-T2, 2026-09-15 — see `042-1a-contract/reconciliation-map.md`)*

**Fulfills:** R-reconcile (design §"How this reframes 042 RD6")
Read what 042 actually shipped/is-shipping (Cursor's start, Claude's completion): the promotion
trigger, `FencingMonitor`/`PromotionMonitor`, epoch handling, `-NOTPRIMARY`, and the client's current
single-vs-multi endpoint state. Produce a short written map (in this task's notes or a design addendum)
of: what 042 already provides that 042-1 reuses as-is (WAL-ship, slots, snapshot, epoch, reconciliation
report), what 042-1 **changes** (promotion becomes herd-driven; deadman becomes backstop), and any code
that must move rather than be duplicated. **Do not build T2–T8 until this map exists** — two failover
models must not collide in the codebase.
**Done when:** the reuse/change map is written and the promotion-trigger reconciliation is decided
(herd-driven primary path, timer backstop), referenced by T8.

### - [x] T1 — Protocol: narration push + multi-endpoint connection string *(042-1a A-T3, 2026-09-15 — protocol v4.8; conformance green)*

**Fulfills:** R2, R7, R12, R13 (+ house rule: protocol changes land in this feature)
Define, in `docs/HIGHWAY-PROTOCOL.md` (and its changelog): (a) the server→client **narration** push
shape — the advisory topology notification, on the doorbell channel or a dedicated push (OD1);
(b) the **GOODBYE** message shape (server→clients+peers, carries no successor — pure timing, R12.1);
(c) the **join-announce** exchange (node→master: my priority; master→peers+clients: roster update /
`priority-taken` refusal, R13.1/R13.3); (d) the multi-endpoint **bootstrap** connection-string grammar
(R13.2). All additive; single-endpoint strings stay valid. `ProtocolConformanceTests` sees any new
`HW.*` command name/arity.
**Done when:** protocol doc + changelog updated; conformance test green with the new surface; the
narration, GOODBYE, join-announce messages and connection-string grammar are specified precisely enough
for T2/T2.5/T5/T6.5 to implement.

### - [x] T2 — Client: multi-endpoint connection string parsing

**Fulfills:** R2
Parse the priority-ordered endpoint list in `Highway.Client` connection settings; `HighwayOptions`
accepts the multi-endpoint form (decide `Server` multi vs. a `Servers` list — keep single-endpoint
back-compatible). Validate ordering; surface a bad string with a sentence.
**Done when:** unit tests parse N-endpoint strings into a bootstrap list; a single-endpoint string
yields a one-node list with unchanged behaviour; malformed input is rejected legibly.

### - [x] T2.5 — Membership and the roster (join announce, master-owned roster, collision reject) *(server half: 042-1c C-T2, 2026-09-16; the client's bootstrap→roster learning rides 042-1b B-T1)*

**Fulfills:** R13.1, R13.2, R13.3, R13.6
Server side: a starting node **announces its (own-config) priority** to the set; the **master** admits
it into the **authoritative roster** and **propagates** the roster to peers and connected clients. A
node announcing a **priority already held** by a live member is **rejected with a legible error**
naming the holder (R13.3 / OD5) — it does not join. The roster rides the warm-standby channel so a
promoted node already holds it (R13.6). Client side: the connection string is **bootstrap only**; the
client learns the **live roster** from the master and uses it (not the string) as the successor source
of truth (R13.2 / OD6 — dynamic membership, a node absent from the client's original string becomes
reachable via the roster).
**Done when:** integration — a joining node appears in every peer's and client's roster; a duplicate
priority is refused with a named error and the node stays out; a client reaches a node that was **not**
in its original connection string via the roster; a promoted standby serves with the current roster
intact.

### - [x] T3 — Client: HerdConnection (successor from roster, one live master, health timeout)

**Fulfills:** R1 (client side), R2, R3
The connection component that maintains **one** live master at a time and implements the successor
function **against the live roster (T2.5)**: on TCP drop or health-timeout `x` (default ≈ 3s, OD2),
compute the highest-priority reachable node (skip priority-`0`) and connect. Same roster + same
reachability → same successor (pure function), computed **at transition time**. List/roster-exhausted
surfaces the existing transient/connection error and keeps retrying.
**Done when:** unit: successor function is deterministic and priority-0-skipping over a given roster; a
simulated TCP drop re-homes to the next node; a frozen (no-exchange) master triggers re-home after `x`;
roster-exhaustion surfaces transient. (Multi-client cohesion is proven in T9.)

### - [x] T4 — Client: InFlightCache + replay on convergence

**Fulfills:** R4, R5 (client side)
Maintain the unacked-work cache: unacked sends (`HW.QSEND`/`HW.PUBLISH` with no ack) keyed by
requestId, plus the existing RPC pending-call registry. Remove an entry when its ack/reply arrives. On
converging to a new master (T3), re-establish the session (re-register services/subscriptions) then
**replay** every cached entry with the **same requestId**. Rely on the new master's replicated acked
history (042) to dedupe/count duplicates.
**Done when:** unit + integration — unacked sends and pending RPCs are replayed with identical ids to a
new master; an already-acked-and-replicated request deduplicates or produces a **counted** duplicate,
never a silent double; a request whose ack never happened executes fresh. **Gate:** the named
RPC-across-failover scenario (R11.3) passes here or in T9.

### - [x] T5 — Server: NarrationPush

**Fulfills:** R7
The master pushes an **advisory** topology notification to its connected clients over their existing
RESP connection (per T1's shape). Fire it when the master observes a peer loss, an operator handoff, or
its own step-down. Advisory only: the client re-runs its own rule; a wrong push is harmless.
**Done when:** integration — a push reaches all connected clients; a client acts by re-running its
successor rule (not by blindly following); a spurious push leaves a correctly-connected client put.

### - [x] T6 — Server: master-only verb gating

**Fulfills:** R1.3, R6
Gate delete/decide verbs (`HW.QACK`, DLQ ops, RPC reply-slot write, `HW.JOB` occurrence fire) on genuine
mastership (has-a-herd ∧ highest-epoch). A node not in the master role refuses them with the existing
`-NOTPRIMARY <endpoint> <epoch>` so the client re-drives. Ingest (`HW.QSEND`/`HW.PUBLISH`) stays
available (append-only, replay-safe); a herd-less node fires **no** job occurrences.
**Done when:** integration — a non-master refuses QACK/DLQ/reply/JOB-fire with `-NOTPRIMARY`; a
herd-less node never fires an occurrence; an ingest duplicate through a transition is counted, not
doubled.

### - [x] T6.5 — Graceful GOODBYE drain

**Fulfills:** R12
Server side: on an operator restart/offline request, the node issues **GOODBYE** (T1 shape) to its
clients and peers, **stops accepting new master-only work** (reuses T6's gate), and **lets in-flight
drain** — pending RPC replies return, unacked sends get acked — so clients' `InFlightCache` (T4) empties
before they move. Draining is **bounded** by a timeout (OD7); at the deadline the node proceeds and any
leftover in-flight falls back to the ordinary replay path (T4). The successor takes epoch E+1
(narration-driven, not TCP-drop). The departing node reaches a clean **stood-down** state.
Advisory-with-teeth: a client that ignores GOODBYE still gets a `-NOTPRIMARY` redirect (T6) and
converges.
**Done when:** integration — a GOODBYE moves the herd to the successor with **near-zero replay** (caches
drained first) and zero loss; a deliberately-stuck in-flight item at the drain deadline falls back to
replay and is not lost; a client that ignores the push still converges via `-NOTPRIMARY`; the departed
node is safe to restart and rejoins as a standby (T7.5).

### - [x] T7 — Server: heal — higher-epoch standdown + reconciliation reuse

**Fulfills:** R8
A returning/healing node that observes a higher epoch (peer or client `-NOTPRIMARY`) demotes: refuses
master-only verbs, emits the **042 reconciliation report** for its unreplicated tail (reuse, don't
rebuild). An islanded former-master (no herd) performs no master-only effects while islanded (falls out
of T6) and stands down on heal. The epoch is the deterministic tie-break (higher wins, no votes).
**Done when:** state-machine tests: islanded former-master does no master-only effects; on seeing a
higher epoch it stands down and emits the reconciliation report; two-nodes-both-believe-master resolves
to the higher epoch.

### - [x] T7.5 — No auto-failback (returning higher-priority node waits)

**Fulfills:** R13.4, R13.5, R2.5
A returning or newly joined **higher-priority** node joins as a **standby** (T2.5 announce + 042
snapshot/tail), syncs, and **waits** — it **never** forces the herd to move. The current master keeps
serving until it fails, partitions, or issues GOODBYE. "Master = highest-priority live node" is
**not** enforced anywhere; the successor is computed at transition against the live roster (T3). The
sanctioned failback is operator **GOODBYE** on the current master (T6.5) — there is no automatic
preemption path to build (this task is partly *ensuring one is not accidentally introduced* in T8).
**Done when:** integration — a higher-priority node rejoins while a lower-priority master serves; the
herd **does not move**; the returner sits as a warm standby; only when the master departs does the herd
converge (on the now-highest-priority returner). A test asserts no preemption path exists.

### - [x] T8 — Deadman → backstop (rewire 042 RD6)

**Fulfills:** R-reconcile, R8 (backstop), 042 R5 (invariant preserved)
Using T0's map: make the **primary** failover path herd-driven (T3/T4 select the successor; it becomes
master by connection). Keep 042's deadman timers as a **backstop** — a former master that lost its herd
but is still running moves toward read-only even absent client correction; the `T_promote > T_fence +
margin` invariant and epoch bump stay config-checked. One coherent model, not two.
**Done when:** the codebase has a single failover model (herd primary, timer backstop); 042's
deadman/fake-clock tests still pass reframed; config validation still refuses a bad timeout triple.

### - [x] T9 — Harness: herd cohesion under crash, partition, GOODBYE, rejoin, collision *(GATE)*

**Fulfills:** R11.1–R11.6
Extend 042's RD10 embedded failover harness: **multiple** in-process clients + a multi-node cluster.
Scenarios, each an explicit assertion:
- **Hard kill** → **every** client converges on the *same* successor (no split); in-flight RPC + unacked
  sends replay and are answered; zero acked-and-replicated loss; duplicates counted (R11.1).
- **Partition matrix:** master-isolated-from-peers-only (keeps herd); master-islanded (herd moves,
  island stands down on heal); client reachability differences — each lands in the designed state and
  **never** yields two live masters *with clients* (R11.2).
- **RPC-across-failover** (the named R4 scenario): kill mid-RPC, caller re-drives same id, gets reply
  (R11.3).
- **Graceful GOODBYE**: drain → herd moves with **near-zero replay**, zero loss; stuck-item-at-deadline
  falls back to replay (R11.4/R12).
- **Rejoin without preemption**: higher-priority node returns while a lower serves → herd does not move;
  converges on it only at the next transition (R11.5/R13.4).
- **Priority collision**: two nodes claim the same priority → first admitted, second rejected with a
  legible error, does not join (R11.6/R13.3).
**Done when:** all scenarios green in the normal suite; the no-split, no-dual-master-with-clients,
no-preemption, and collision-rejected assertions are explicit, not incidental.

### - [x] T10 — Observability

**Fulfills:** R9
`HW.STATS` + dashboard: role (master/standby/stood-down), epoch, **connected-client count**, the
**live roster** (members + priorities), and (on master) per-replica slot state/lag (042, unchanged).
Record each herd transition (cause — crash / narration / GOODBYE / higher-epoch, timing, from→to,
epoch before/after) and the **convergence latency** so `x` can be sanity-checked.
**Done when:** stats expose the fields incl. the roster; a transition (including a GOODBYE) and its
convergence latency are observable in a test; the dashboard renders role/epoch/client-count/roster.

### - [x] T11 — Assurance rig against a failing-over herd

**Fulfills:** R11.4
Run the assurance rig against a multi-node cluster with a **mid-turbulence master transition**; assert
I1–I5 green; include the **doorbells-off** variant; record both in `assurance/RUNLOG.md` (house pattern).
**Done when:** both runs PASS all invariants with a transition during turbulence; recorded in RUNLOG.

### - [x] T12 — The record (constraints + product docs)

**Fulfills:** R5.3, R5.4, R10.2, R13, R9 (docs)
Dated amendments (never rewrite history): `constraints.md` gains numbered entries for **the ack-is-the-
birth boundary** (where responsibility begins), **the async RPO window** (042 RD8, restated), **the
no-elections / herd-mastership position** (extends 042's), the **no-auto-failback / GOODBYE-is-the-
deliberate-failback** rule (R13.4/R13.5), the **priority-collision-reject** rule (R13.3), and the
**frozen-master `x` < `T_fence`** coupling. `product.md` gains the client-herd failover story in a
paragraph linking here; `roadmap.md` points its clustering/replication line at 042 + 042-1.
**Done when:** every entry named above carries its dated amendment; a read of the register describes
the shipped failover model with no stale line; protocol doc stays the single source for the wire
(linked, never copied).

---

## Gates

- **Gate G(042-1):** T9 green — herd cohesion proven under crash and the partition matrix; no split;
  never two live masters with clients; in-flight replayed; at-least-once holds; duplicates counted.
- **Reuse, not rebuild:** 042's WAL-ship, slots, snapshot, epoch, and reconciliation report are reused
  (T0 maps them); this feature adds narration + herd-connection + in-flight-replay and reframes the
  deadman. Any temptation to reimplement 042 machinery is out of scope.
