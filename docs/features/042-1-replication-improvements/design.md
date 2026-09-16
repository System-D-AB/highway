# Feature 042-1 — Replication Improvements: Design

*Refines feature [042](../042-replication/design.md). 042's replication machinery (WAL-shipping,
slots, snapshot, epoch, reconciliation) is reused unchanged; this document designs the **failover
control model** that sits on top of it and reframes 042 RD6's deadman as a backstop. Read
[`requirements.md`](requirements.md) first — the mechanics here serve those requirements.*

## The one idea

> **The master is whichever node the client herd is connected to. A node with no clients is not a
> master.**

Mastership is not negotiated by servers; it is a *fact about client connectivity*. There is one
master because the clients are one herd on one node. Everything below exists to protect a single
invariant: **the herd stays a herd** — it converges on the same node at the same time and never
splits onto two live masters.

This inverts the usual model. A database negotiates mastership server-side and clients follow; Highway
lets the clients *be* the definition of mastership, which it can afford because its unit of work is a
message with at-least-once / duplicates-allowed semantics — divergence is reconciled by *replaying
messages* (safe), never by *merging rows* (impossible). That is why no election is needed.

## Architecture

```
   CONNECTION STRING (shared, priority-ordered, common knowledge)
   node4 > node3 > node2 > node1        ← identical on every client AND every node

        ┌──────────── the herd ────────────┐
        │ clientA   clientB   clientC  ...  │   all connected to ONE node at a time
        └───────────────────┬───────────────┘
                            │ connected → that node IS the master (R1)
                            ▼
   ┌────────────── node4 (MASTER, epoch E) ──────────────┐
   │ RESP server (040) + HW.REPL.* feeder (042)          │
   │ NarrationPush ──► pushes topology changes to herd   │ (R7, advisory)
   │ master-only verbs: QACK, DLQ, reply-slot, JOB fire  │ (R6)
   └──────────────────────────────────────────────┬──────┘
                            │ WAL-ship (042, unchanged) — replicates ACKED state
                            ▼
   node3 (standby, next in priority) ── node2 ── node1
   warm; hold replicated acked history; serve no client verbs (042 RD1 / OD3 stats-only)

   CLIENT (Highway.Client), per client:
   ┌─────────────────────────────────────────────────────────────┐
   │ HerdConnection: walks the ordered list; one live master.     │
   │ InFlightCache: unacked sends + unanswered RPCs (R4).         │
   │ on TCP-drop OR narration → re-select successor → REPLAY cache │
   └─────────────────────────────────────────────────────────────┘
```

Two new capabilities, both small, one per side:

- **Server:** a **narration push** — the master tells its connected clients about topology changes
  it observes (R7). Everything else the server needs (epoch, `-NOTPRIMARY`, WAL-ship, reconciliation)
  already exists in 042.
- **Client:** the **herd connection** (multi-endpoint, priority-walk, one live master) and the
  **in-flight cache + replay** (R4). This is the bulk of the work and it lives in `Highway.Client`
  (042 RD9 already licensed the client to change).

## Mastership, defined operationally (R1)

A node is in the master role iff clients are connected to it and it holds the current (highest) epoch.
Operationally:

- A node **becomes** master by the herd connecting to it (after selecting it, §"Convergence").
- A node **stops being** master when the herd leaves (it died, or narrated a handoff, or was
  out-epoched) — not by any server-side "resign."
- A node with **zero connected clients performs no master-only side effects** (R6). This is the
  clause that makes an islanded former-master harmless: it *thinks* it is master, but with no herd it
  neither acks, dead-letters, answers RPCs, nor fires jobs — so it cannot contradict the real master.

"At most one live master *with clients*" (R1.4) is therefore true by construction in the common and
partition cases, and enforced by the epoch tie-break in the pathological rejoining-island case (§Heal).

## The successor: deterministic, common knowledge (R2)

The connection string is the shared truth:

```
highway://node4.host:6500,node3.host:6500,node2.host:6500,node1.host:6500
         └─ priority order: leftmost = highest priority = serves first ─┘
```

- Every **client** parses this into an ordered endpoint list.
- Every **node** is configured with the *same* ordered list (and its own identity within it), so a
  node knows its own priority and who precedes/succeeds it. Startup validation refuses a node whose
  list disagrees with its peers' (mismatch is a config error, surfaced with a sentence).
- The **successor function is pure:** `successor(reachable) = first endpoint in priority order that
  answers a handshake with OK`, skipping priority-`0` nodes. Same list + same reachability → same
  answer, computed independently by every client with no coordination.

Priority `0` / never-promote keeps Redis `replica-priority` semantics (042 RD5), so a node can be a
pure standby that the herd never selects.

## Membership and the roster (R13)

The successor order is not a static file each node reads privately — it is a **live roster** the
master maintains and propagates. This is what lets the cluster grow at runtime and what catches
misconfiguration.

**Join announcement.** A starting node holds only *its own* priority in config. On startup it connects
to the set and **announces its priority**. The **master** admits it, adds it to the authoritative
roster, and **propagates** the roster ("node-X joined at priority p") to peers and to connected
clients. The master is the coordination point — the herd already trusts it, so it is the natural owner
of "who is in the set."

**Bootstrap vs. live truth.** A client's multi-endpoint connection string is only a **bootstrap**: it
gets the client to *some* reachable node, from which it learns the live roster. The **roster is the
running truth** for the successor order. Consequence: the cluster can grow beyond a client's original
connection string — a node that joins after the client started is reachable via the roster the master
narrates, with **no need to edit every client's config** (dynamic membership).

```
node-X starts (config: my priority = 3)
   └─ announce(priority=3) ─► master
        master: roster.add(node-X, 3); propagate roster ─► peers + herd
        node-X: snapshot + tail (042) ─► warm standby, waiting (no preemption, below)
```

**Priority collision — first announcer wins, loser rejected (OD5).** If node-X announces a priority
already held by a live roster member, the master **refuses admission with a legible error** naming the
holder: *"priority 3 is held by node-Y; fix this node's config."* The loser does not join (it does not
silently re-order behind the winner). This keeps `priority → successor` a **unique, timing-independent**
mapping — the succession order can never depend on the order nodes happened to reboot in. A collision is
an operator error surfaced immediately, not absorbed.

**Roster continuity across a transition.** A standby receives roster propagation continuously, so a
promoted node **already holds the current roster** the instant it takes over — succession never loses
membership knowledge. Roster state rides the same warm-standby channel as replicated data (042).

## Convergence: the two triggers (R3)

The herd moves on exactly two triggers, and both apply the *same* successor function so the herd lands
together:

### Trigger 1 — hard failure (no last words)

The master crashes/loses power. Every client's TCP to it drops. Each client immediately walks the
ordered list to the next node answering `OK`. Because the list and the rule are identical, all clients
land on the same successor. **This is the safety net** — it needs nothing from the dead master.

```
node4 dies (TCP drops on every client, ~instant)
   clientA ─┐
   clientB ─┼─► walk list: node3? OK ──► all connect to node3  ──► node3 IS master (R1)
   clientC ─┘        (node3 answers OK because it is next-priority and willing)
```

### Trigger 2 — narrated transition (master alive)

The master is alive and observes a topology change (it lost sight of a peer, or an operator issued a
handoff, or it is stepping down). It **pushes** a notification to its connected clients (R7). Every
client receives the *same message from the same source* and re-runs its own successor function. Fast,
and the herd hears it simultaneously.

Narration is **advisory** (R7.2): the client re-evaluates; it never connects somewhere unsafe purely
because it was told to. A wrong narration is harmless — the client re-runs its rule and may stay put.

### The frozen-master edge (R3.3)

A master that freezes without dropping TCP produces neither trigger. A **client-side health timeout**
`x` (target ≈ 3s, OD2) covers it: no successful exchange within `x` → treat as failure → walk the list.
`x` is the convergence ceiling; TCP-drop and narration are the common, near-instant paths. `x` is
chosen below 042's `T_fence` (5s) so the herd re-homes *before* the deadman backstop would fire.

## Client-held in-flight and replay (R4) — the durability boundary

The client is the durability boundary for **in-flight** (unacked) work. The broker preserves nothing
in-flight across a failover; it does not need to.

### What the client caches

```
InFlightCache
├── unacked sends:      requestId → (verb, args, bytes)   // no broker ack yet
└── unanswered RPCs:    requestId → pending await          // already in PendingCallRegistry today
```

An entry is **removed** the instant its ack/reply arrives. So the cache is exactly "work I asked for
that the cluster has not yet confirmed."

### Replay on convergence

On connecting to a new master:

1. Re-establish the session (auth, re-register services/subscriptions — the engine's existing startup).
2. **Replay every InFlightCache entry**, using the **same requestId**.
3. The new master already holds the durable, replicated **acked** history (042 WAL-ship). So:
   - a replayed request whose effect it already has → deduped (idempotent by identity), or produces a
     **counted** at-least-once duplicate — never a silent double.
   - a replayed request it has never seen (the ack never happened before the failover) → executed
     fresh. Correct: from the cluster's view this is its first sighting.

### Worked: RPC across failover (R4 scenario)

```
clientA ──HW.CALL(reqId=r1)──► node4   (r1 in clientA.InFlightCache, no reply yet)
node4 dies
clientA, clientB ──► converge on node3 (Trigger 1)
node3 now master; clientB re-registers its service
clientA replays HW.CALL(reqId=r1) ──► node3 ──► clientB dequeues, answers r1 ──► clientA gets reply
```

The call was never delivered and never lost — it lived in clientA's memory across the whole failover.
The reply slot did **not** need to survive; the *caller* is the source of truth for "unanswered."

## The ack is the birth (R5) — where the guarantee begins

Stated as the definition of the boundary, not a caveat:

- **Before the ack:** the request lives only in the client. If the client dies before the ack (and so
  before it can replay), the request never entered the cluster — a **non-birth**, not a loss. No system
  solves this (HTTP doesn't; a producer outbox only moves the boundary one hop and *its* write can fail
  first). Causality, not a Highway limitation.
- **After the ack:** the cluster's responsibility — durable on the master (sync-per-commit, 038),
  replicated within the measured lag window (042 RD8). Survives any single **server** failure
  independently of the client.

The async-replication RPO (042 RD8) is unchanged: a hard master loss *within* the lag window can lose
an acked-but-not-yet-replicated message; duplicates across failover are counted; loss is bounded by the
reported window, never silent. This goes into `constraints.md` as a numbered constraint (R5.3, R5.4).

## Master-only side effects during a transition (R6)

The transition window is safe because operations are split by their reconciliation shape:

| Operation | Class | Transition behaviour |
|---|---|---|
| `HW.QSEND`, `HW.PUBLISH` | ingest (append-only) | replay-safe; a transition duplicate is permitted & counted |
| `HW.QACK`, DLQ ops | delete/decide | **master-only**; a non-master refuses `-NOTPRIMARY`, client re-drives |
| RPC reply-slot write | decide | **master-only**; the caller holds the pending call and re-drives to the new master |
| `HW.JOB` occurrence fire | decide | **master-only**; a herd-less node never fires → no double-fire |

The distinction: an ingest duplicate is a *tolerated duplicate*; a delete-vs-pending or a double-fire is
a *contradiction*. Ingest stays available through a transition; decide/delete is gated on genuine
mastership (has-a-herd + highest-epoch), refusing with `-NOTPRIMARY` otherwise so the client re-drives.

## Heal: a former master stands down (R8)

```
STATES (per node)
  Master(E, herd>0) ──herd leaves / higher epoch seen──► StoodDown(E)
  StoodDown(E) ──re-sync as standby──► Standby
  Islanded(E, herd=0): harmless (no master-only effects, R1.3); on heal → StoodDown via epoch
```

- A returning/healing node that sees a **higher epoch** (from the node now serving the herd, or from a
  client's `-NOTPRIMARY <endpoint> <epoch>`) demotes immediately (042 RD7, unchanged): refuses
  master-only verbs, emits the **reconciliation report** for its unreplicated tail.
- The **epoch** is the deterministic tie-break of last resort (R8.4): if two nodes ever both believe
  they are master, the herd and the nodes obey the **higher** epoch. No votes — the number wins by rule.
- The reconciliation report (042 RD7): pure enqueues in the tail are replay-safe; conflicting events
  (ack-vs-pending) are **flagged** for the operator, never auto-merged.

### Why this is not split-brain

At most one node ever has the herd (R1.4). An islanded former-master has **no herd**, so it performs no
master-only effects (R6) — it cannot contradict the real master. The only state it can reach is
"talking to itself," which delivers nothing. On heal, the epoch makes it stand down. Two live masters
*with clients* never occurs in the common or single-partition case, and the epoch resolves the
pathological rejoin. This is why elections are unnecessary (R10).

## Graceful drain — GOODBYE (R12)

A crash is *loss of signal*; a GOODBYE is a *promise*. That difference is the whole value: a node
leaving on purpose is alive to **finish its in-flight work before it goes quiet**, so the herd departs
with **empty in-flight caches and near-zero replay** — a planned restart costs less than even the
duplicates a crash would.

Sequence:

1. **Announce, don't just leave.** The departing node pushes **GOODBYE** to its connected clients (via
   the narration channel, R7) and to its peers: *"I am leaving, converge on your known successor now."*
   The message names **no successor** — the successor is already common knowledge from the roster
   (R2/R13). GOODBYE is pure *timing*, turning the slow "wait for TCP-drop / health-timeout" into an
   instant "go now"; *where* to go was never in question.
2. **Quiesce, don't cut.** The node stops accepting *new* master-only work (R6) but lets **in-flight
   drain** — pending RPC replies come back, unacked sends get acked — so each client's `InFlightCache`
   (R4) empties **before** it moves.
3. **Bounded drain.** Draining is capped by a timeout (OD7). Anything still in flight at the deadline
   falls back to the ordinary replay path (R4): the client re-drives it to the new master. **Graceful
   degrades to failover; it never blocks** a maintenance action on one wedged handler.
4. **Hand over.** The successor takes epoch E+1; the herd re-forms on it (narration-driven, so fast and
   unanimous — not the TCP-drop fallback). The departing node reaches a clean **stood-down** state and
   is safe to restart/offline. On return it rejoins as a standby (below).
5. **Advisory-with-teeth.** A client that ignores the GOODBYE push still hits a `-NOTPRIMARY` redirect
   from the now-quiescing node (R6) and converges anyway. Announcement is the fast path; verb-refusal
   is the guarantee.

Graceful vs. the unplanned cases, made concrete:

| Case | Signal | In-flight | Replay | Epoch bump |
|---|---|---|---|---|
| Hard crash | TCP drop (inferred) | held in clients, replayed | some, deduped/counted | successor bumps after inferring death |
| **Graceful GOODBYE** | explicit push (promised) | **drained before handover** | **near-zero** | successor bumps *as part of* the handover |
| Partition / island | silence both ways | held, replayed on heal | some + reconciliation report | epoch resolves the rejoin |

## No auto-failback — no preemption (R13.4)

A returning or newly joined **higher-priority** node does **not** seize mastership. It joins as a
standby (join announcement + snapshot/tail), syncs, and **waits**. The current master keeps serving
until it fails, partitions, or issues GOODBYE. This is the standard HA choice (PostgreSQL,
`pg_auto_failover`, DB mirroring, Redis `replica-priority`) and it exists to avoid two real harms:

- **A second, needless disruption.** The herd already paid to move when the old master left; preemption
  would move it *again* the instant a higher-priority node returned — two transitions for one outage.
- **Flapping.** A higher-priority node that crash-loops would drag the whole herd back and forth with
  it. Non-preemption keeps that node's instability *its* problem until it has proven stable and the
  current master has a *real* reason to hand over.

So **priority means "who is the successor at the next transition," not "who is entitled to be master
now."** "Master = highest-priority live node" is deliberately **not** an invariant (R2.5): after a
higher-priority node rejoins, the roster ranks it above the running master, yet the master keeps the
herd. The successor is computed **at transition time** against the live roster, so the returner's
priority *is* honored — just deferred to the next real departure.

**Deliberate failback is GOODBYE.** An operator who wants the top node back after maintenance issues
**GOODBYE on the current master** (R12) — a chosen, timed, zero-loss handover — which is strictly
better than an automatic, timing-uncontrolled preemption. Every herd transition is therefore either
forced-by-failure or chosen-by-operator, never surprise-triggered by a node rebooting.

**Worked scenario (R13.4):** node2 is serving the herd; node5 (priority 5, higher) comes back. node5
announces, joins the roster as a **standby**, syncs, and waits. The herd **stays on node2**. Only when
node2 fails or says GOODBYE does the herd converge — and then, node5 being the highest-priority
reachable node, it converges on node5.

## How this reframes 042 RD6 (deadman) — backstop, not primary path

042's deadman fenced the primary and promoted a replica on timers. In this model the **primary path is
client-herd convergence** (connection-driven, near-instant, agreement-free). The deadman is retained as
a **backstop** for the narrow case it still helps:

- It ensures a former master that lost its herd but is *still running* moves toward read-only even if,
  for some reason, no client has yet corrected it (belt-and-suspenders behind R1.3's "no herd → no
  master-only effects").
- Its `T_promote > T_fence + margin` invariant and epoch bump remain valid and config-checked (042 R5).

**Reconciliation note for implementation:** feature 042 is being implemented concurrently (Cursor
started it; Claude is completing it). This design must be reconciled with that code — specifically, the
promotion trigger shifts from "replica self-promotes on timer (primary path)" to "herd selects
successor → successor becomes master by connection; timer is backstop." A task in
[`tasks.md`](tasks.md) owns that reconciliation explicitly rather than leaving two models to collide.

## Connection string format

```
highway://[user[:password]@]host1:port1,host2:port2,...,hostN:portN[/?opt=val]
```

- Endpoints are a **bootstrap** set — enough to reach *some* node. The **live successor order comes
  from the master's roster** (R13.2), not from string order, so the cluster can grow beyond this list.
  (Listing endpoints in priority order is a sensible convention for the first connect, but the roster
  is the running truth.)
- Backwards compatible: a single-endpoint string is a one-node "herd of one," unchanged behaviour.
- Options carry `x` (client health timeout), backoff bounds, and auth (existing).
- The parser lives in `Highway.Client` connection settings; `HighwayOptions.Server` accepts the
  multi-endpoint form (or a sibling `Servers` list — decided in tasks; single-endpoint stays valid).

## Sequence diagrams for the key flows

### Hard-kill failover with in-flight RPC (R4, R11.3)

```
clientA        clientB        node4(master,E)     node3(standby,E)
  │ HW.CALL r1 ───────────────► (queued, no reply)
  │ (r1 in InFlightCache)
  │                              ✗ CRASH
  │◄── TCP drop ──               ✗                 │
  │ walk list → node3 OK ───────────────────────► connect
  │                    clientB walk list → node3 ─► connect + re-register svc
  │                                                 node3 becomes master (herd here), epoch → E+1
  │ replay HW.CALL r1 ─────────────────────────────► dequeue → clientB answers r1
  │◄──────────────── reply r1 ───────────────────── │
```

### Narrated graceful handoff (R7)

```
node4(master)                     herd
  │ observes handoff/step-down
  │ NarrationPush "stand down → successor" ──────► every client (same message)
  │                                                 each re-runs successor rule → node3
  │ epoch bump on node3 (E+1); node4 → StoodDown; reconciliation report for tail
  herd re-forms on node3
```

### Islanded former-master on heal (R8)

```
node4(was master, islanded, herd=0, epoch E) — performs NO master-only effects while islanded
   ... partition heals ...
node4 sees node3 serving herd at epoch E+1 (or a client's -NOTPRIMARY <node3> E+1)
node4 → StoodDown(E): refuse master-only verbs, emit reconciliation report, re-sync as standby
```

### Graceful drain — GOODBYE (R12)

```
node4(master,E)                      herd (clientA, clientB, ...)      node3(standby)
  │ operator: restart / take offline
  │ GOODBYE push ─────────────────────► every client (and peers)      │
  │ stop new master-only work; drain in-flight:
  │   pending replies returned, unacked sends acked ─────────────────► clients' InFlightCache → empty
  │ (bounded by timeout; anything left → falls back to replay path)
  │                                     each client re-runs successor rule → node3
  │                                     epoch bump on node3 → E+1
  │ node4 → StoodDown (clean)           herd re-forms on node3 ────────► node3 IS master
  │ safe to restart/offline; on return rejoins as standby (no preemption)
```

### Node rejoin — no preemption (R13.4)

```
node2(master,E, herd here)           node5(returns, priority 5 > node2)
  │                                    │ announce(priority=5) ─► node2 (master)
  │ roster.add(node5,5); propagate ───► peers + herd
  │                                    node5: snapshot+tail → warm STANDBY, waits
  │ herd STAYS on node2 (no move)      │
  ... later: node2 fails or GOODBYEs ...
  herd computes successor at transition → node5 (highest-priority reachable) → converges on node5
```

## Testing strategy

| Layer | Proof | Requirement |
|---|---|---|
| Successor function | unit: same roster + reachability → same successor; priority-0 skipped; list-exhausted surfaces transient; successor computed at transition | R2 |
| Membership / roster | join announce admits + propagates; client learns a node absent from its bootstrap string via the roster (dynamic membership) | R13.1, R13.2 |
| Priority collision | two nodes claim priority p → first admitted, second **rejected with a legible error**, does not join | R13.3, R11.6 |
| Graceful GOODBYE | scripted drain → herd moves to successor with **near-zero replay** and zero loss; drain-timeout leftovers fall back to replay | R12, R11.4 |
| No preemption | higher-priority node returns while lower serves → herd **does not move**; returner waits as standby; converges on it only at the next transition | R13.4, R11.5 |
| Herd cohesion (hard kill) | N in-process clients + multi-node; kill master; assert **all** clients land on the same successor, no split | R3, R11.1 |
| In-flight replay | unacked sends + pending RPCs replayed with same id to new master; deduped/counted, never silent-double | R4, R11.1 |
| RPC across failover | the named R4 scenario: kill mid-RPC, caller re-drives to successor, gets reply | R11.3 |
| Ack-is-the-birth | client dies pre-ack → request absent from cluster (non-birth); client dies post-ack → survives on cluster | R5 |
| Master-only gating | non-master refuses QACK/DLQ/reply/JOB-fire with `-NOTPRIMARY`; ingest duplicate counted not doubled | R6 |
| Narration | server push reaches all connected clients; advisory (wrong push → client re-runs rule, harmless) | R7 |
| Heal / epoch | islanded former-master does no master-only effects; stands down on higher epoch; reconciliation report content | R8 |
| Partition matrix | master-isolated-from-peers-only (keeps herd); islanded (herd moves, island stands down); never two masters *with clients* | R11.2 |
| Observability | HW.STATS shows role/epoch/connected-client-count/priority-order; transition + convergence latency recorded | R9 |
| Assurance rig | I1–I5 with a mid-turbulence herd transition; doorbells-off variant; RUNLOG | R11.4 |
| 042 reconciliation | the deadman-as-backstop reframe: promotion via herd, timer as backstop, one coherent model | R-reconcile |

## Risks

| Risk | Mitigation |
|---|---|
| Herd splits during a transition (two clients pick different successors) | Successor is a pure function of shared ordered list + reachability; harness R11.1/R11.2 asserts no split under crash and partition. The only split possible is genuinely-doubly-partitioned clients, resolved as "one has no reachable master, surfaces transient" — never two live masters *with clients*. |
| A frozen (TCP-alive) master strands the herd | Client health timeout `x` < `T_fence` (OD2); after `x` the client walks the list. |
| Replay double-applies an already-acked request | Same-requestId replay + the new master's replicated acked history → dedup/count; R4.4 + R11.1 prove it. This is the existing at-least-once/`[Idempotent]` contract, not new. |
| Master-only verb slips through on a herd-less node | R1.3 gate + `-NOTPRIMARY` refusal; harness asserts a non-master refuses QACK/DLQ/reply/JOB-fire. |
| Collision with 042's in-progress deadman promotion code | Explicit reconciliation task; deadman becomes backstop, promotion becomes herd-driven — one model, tested together. |
| Narration abused as a covert vote | Narration is advisory and one-directional (R7.3); the client always re-runs its own rule; a wrong narration is harmless by construction. |
| Client-death-before-ack read as data loss | Stated as the boundary (R5) — the ack is the birth; not a gap, and documented in the register as the definition of where responsibility begins. |
| Convergence `x` mis-tuned vs. deadman constants | R9.3 makes convergence latency observable; OD2/OD4 revisit `x`, `T_fence`, `T_promote` against measured reality. |
| Priority collision silently re-orders succession (timing-dependent order) | Collision loser is **rejected**, not ordered-behind (R13.3 / OD5); `priority → successor` stays a unique, restart-order-independent mapping; the collision test proves the second announcer does not join. |
| Roster drifts from what clients believe | The master is the single roster owner and propagates on change (R13.1); a promoted node already holds it (R13.6); clients treat the connection string as bootstrap only (R13.2). |
| Someone adds auto-failback "so the big box is always master" | Non-preemption is a stated requirement (R13.4); the sanctioned failback is operator GOODBYE (R12) — deliberate and zero-loss. A preemption proposal must reopen R13.4 by name. |
| GOODBYE drain blocks on a stuck handler | Drain is bounded (R12.3 / OD7); leftovers fall back to the replay path; a maintenance departure is never held hostage by one message. |
