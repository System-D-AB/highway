# Feature 042-1 — Replication Improvements: Client-Herd Mastership and Failover

*Refines feature [042](../042-replication/requirements.md). 042 shipped the replication
mechanics — WAL-shipping (`HW.REPL.*`), retention slots, epoch fencing, the reconciliation
report, and the two-timeout deadman (RD6). This feature changes **how failover is driven**:
from a server-negotiated single primary (deadman-fenced) to a **client-herd** model where the
master is defined by where the clients are connected, the herd moves together to a
deterministic successor, and each client holds its own in-flight work and replays it to the new
master. 042's replication machinery is retained and reused; only the mastership/failover control
model is refined, and 042 RD6's deadman becomes a **backstop**, not the primary path.*

## Introduction

Highway is a delivery mechanism, not a data store. Its unit of work is a message with
**at-least-once, duplicates-allowed** semantics. That inverts the usual replication trade-offs and
lets Highway make a failover promise a database cannot: because divergent histories are reconciled
by *replaying messages* (safe — at-least-once permits it) rather than by *merging rows* (impossible),
the design does not need elections, votes, or a three-node quorum floor.

This feature records the failover model that follows from one definition:

> **The master is whichever node the client herd is connected to. A node with no clients is not a
> master and cannot act as one.**

Mastership is therefore not a claim a server makes or a vote it wins — it is a *fact about client
connectivity*. There is exactly one master because the clients move as one herd to one node. This
dissolves split-brain by construction: two nodes cannot both be master, because "master" means "the
node the herd is on," and the herd is on one node.

Everything else in this feature exists to protect **one invariant**: *the herd stays a herd* — all
clients converge on the same node at the same time, and never split onto different masters.

### Relationship to feature 042

| 042 decision | 042-1 disposition |
|---|---|
| RD1 one primary, N replicas, priority-ordered | **Kept.** Priority order becomes the clients' shared successor list. |
| RD2 WAL-shipping over `HW.REPL.*` | **Kept, unchanged.** Replication of *acked* state is exactly what makes a new master already hold the durable history. |
| RD3 retention slots + cap | **Kept, unchanged.** |
| RD4 snapshot + tail initial sync | **Kept, unchanged.** |
| RD5 successor = configured priority, `0` = never | **Kept, and elevated:** the priority list is common knowledge shared with every client via the connection string. |
| RD6 two-timeout deadman (fence/promote) | **Reframed as a backstop.** The primary path is client-herd convergence (this feature). The deadman remains as the safety net for the case where a former master is alive-but-islanded and must stand down (via epoch, R8). |
| RD7 epoch fencing + reconciliation report | **Kept, narrowed.** Epoch and reconciliation are **heal-time cleanup** for a former master's in-flight tail — not part of *deciding* mastership (the clients decided that by moving). |
| RD8 async acks (RPO = replication lag) | **Kept and sharpened** — see R5 (the ack is the birth). |
| RD9 multi-endpoint client, `-NOTPRIMARY` redirect, client may change | **This feature is largely RD9, expanded** into the full herd model. |
| RD10 failover is a CI test | **Kept and extended** — the harness must additionally prove *herd cohesion* (no split) under crash and partition. |

**No elections, in any form.** This feature does not add one. The master is defined by connection;
the successor is deterministic config; convergence is triggered by connection loss or the incumbent's
narration. Where genuine ambiguity could remain (a former master that thinks it is still master), the
**epoch** is a deterministic tie-break of last resort, not a tally of votes.

---

## Requirements

### Requirement 1: Mastership is defined by client connection

**User Story:** As an operator, I want exactly one node serving clients at any time, so that there is
never a "who is the master" question to resolve and never two nodes accepting conflicting client work.

#### Acceptance Criteria

1. The **master is the node the client herd is connected to.** A node serving no clients is **not**
   a master, regardless of its own belief, its priority, or its epoch.
2. Mastership is **never** established by a server-to-server negotiation, vote, or quorum. A node
   becomes master by *the herd connecting to it*, and stops being master by *the herd leaving*.
3. A node that believes it is master but has zero connected clients performs **no master-only
   side effects** that could conflict with another node (see R6 for what "master-only" covers).
4. At any wall-clock instant, at most one node is in the master role **as observed by the herd**: a
   client is either connected to the one master or in transit (disconnected, walking the successor
   list) — never connected to a *second, different* live master. (During a transition a client may be
   momentarily disconnected; that is "no master for me yet," not "a different master.")

### Requirement 2: The successor is deterministic and common knowledge

**User Story:** As a client, I want to know — before anything fails — exactly which node to move to
next, so that every client independently reaches the *same* answer and the herd never splits.

#### Acceptance Criteria

1. Each server has its **own priority hard-coded in its own config** (a single number; lower number =
   serves first, `0` = never-promote — Redis `replica-priority` semantics, 042 RD5). A node does not
   need the whole cluster's config baked in; it announces its priority at join (R13).
2. The **authoritative roster** — who is in the set and at what priority — is maintained by the
   **master** and propagated to peers and clients (R13). The successor order is a property of this
   *live roster*, not of any single node's static file.
3. Every client is configured with a **multi-endpoint connection string** (MongoDB-style;
   comma-separated endpoints). This is the client's **bootstrap** list — enough to reach *some* node
   and learn the live roster from the master. The live successor order comes from the roster, so the
   cluster may grow beyond a client's original bootstrap list (dynamic membership, R13).
4. The successor of a departed master is **computed identically** by every client and every node,
   **at transition time**, against the **live roster**: the highest-priority node then reachable and
   willing (answers a handshake `OK`), skipping priority-`0` nodes. Same roster + same reachability →
   same successor, with **no coordination** — a pure function.
5. **The running master is not necessarily the highest-priority live node** (see R13 no-preemption): a
   higher-priority node that returns waits as a standby. "Master = highest priority" is therefore
   **not** an invariant; "successor = highest-priority reachable node, chosen at the next transition"
   is.

**Worked scenario (recorded verbatim, R2/R3):** Four nodes, priorities `4 > 3 > 2 > 1` — node4 highest.
All clients bootstrap from a connection string listing all four; the herd connects to **node4** and
learns the roster from it. Every node and every client knows, before any failure, that the succession
is node4 → node3 → node2 → node1. No runtime decision establishes the *order*; it is the priority
roster. The *timing* of a move is triggered by failure or GOODBYE (R3, R12); the *destination* is the
highest-priority reachable node computed at that moment.

### Requirement 3: The herd moves together — the one invariant

**User Story:** As an operator, I want all clients to converge on the same new master together, so
that the "one master = where the herd is" definition (R1) actually holds under failure.

#### Acceptance Criteria

1. **Two triggers move the herd, and both land every client on the same successor:**
   - **Hard failure (no last words):** a client's TCP connection to the master drops. The client
     immediately walks the shared priority list (R2) to the next node answering `OK`.
   - **Narrated transition:** the incumbent master (still alive) pushes a topology change to its
     connected clients (see R7); every client acts on the *same message from the same source*.
2. Because the trigger and the rule are identical for all clients, **the herd converges on the same
   node** — the design's central invariant. A client must never independently *choose* a master by a
   rule other clients do not share.
3. Convergence latency has a bound `x` (default target ≈ 3 seconds), covering the slowest path (a
   master that froze without dropping TCP, detected by a client-side heartbeat/health timeout). TCP
   drop and narration are near-instant; `x` is the ceiling, not the common case.
4. A client that cannot reach the highest-priority successor walks the list to the next, and the next,
   until one answers `OK` or the list is exhausted (list exhausted → the cluster is down for that
   client; it surfaces the existing transient/connection error class and keeps retrying).

### Requirement 4: The client holds its in-flight work and replays it

**User Story:** As a service author, I want an unacknowledged send or an unanswered RPC to survive the
death of the node it was sent to, so that a single server failure does not lose work I already asked
for — without the broker having to preserve any in-flight state across failover.

#### Acceptance Criteria

1. Each client maintains, in its own memory, the set of requests it has issued that are **not yet
   acknowledged**: queue/publish sends that have not received their broker ack, and RPC calls that
   have not received their reply. (RPC pending calls are already held in the client's pending-call
   registry today; this generalizes the same idea to unacked sends.)
2. On converging to a new master (R3), the client **replays its cached unacked requests** to that
   master, using the **same request identifiers**, so the new master (which already holds the durable,
   replicated acked history via 042's WAL-shipping) can dedupe anything already delivered.
3. The broker relies on **nothing in-flight** surviving across a failover. It may lose 100% of its
   in-flight (unacked) state on a crash; correctness is preserved because that state lived in the
   clients and is replayed. Replication (042) preserves **acked** state, not in-flight.
4. Replay is idempotent-by-identity: a request whose effect the new master already has (because it was
   acked and replicated before the failover) is deduped or produces an at-least-once duplicate that is
   **counted, not silently doubled** — consistent with Highway's existing `[Idempotent]` and
   duplicates-allowed contract.

**Worked scenario (recorded verbatim, R4):** clientA calls an RPC hosted by clientB; both are on the
current master (node4). The call is in flight — clientA has sent it, no reply yet, so it sits in
clientA's pending-call registry. **node4 dies.** Both clientA and clientB converge on the successor
(node3) — clientA by TCP drop + priority walk, clientB the same. Once both are on node3 and the
service is re-registered, clientA **re-routes the same RPC (same request id) to node3**; clientB
dequeues and answers it; clientA gets its reply. The call was never delivered and never lost — it
lived in clientA's memory across the entire failover.

### Requirement 5: The ack is the birth — where the guarantee begins

**User Story:** As a service author, I want a precise, honest statement of when my message becomes the
cluster's responsibility, so that I know exactly what a broker failure can and cannot lose.

#### Acceptance Criteria

1. **Before the ack**, a request exists only where it was created — in the client. If the client dies
   before it receives the ack (and therefore before it can replay, R4), the request never entered the
   cluster. This is **not a loss**: the request was never delivered "at least zero" times because it
   never crossed the boundary. No system can solve this, and this feature does not pretend to.
2. **After the ack**, the request is the cluster's responsibility: durable on the master
   (sync-per-commit, 038) and replicated to standbys within the measured lag window (042 RD8). It
   survives any single **server** failure independently of the client.
3. The guarantee is stated in `constraints.md` as the **definition of where responsibility begins**,
   not as a caveat: *the ack is the moment a request becomes the cluster's responsibility; before it,
   responsibility is the sender's, which is where it must be, because only the sender knows the request
   exists.*
4. The async-replication RPO window from 042 RD8 is unchanged and stated alongside: a hard master loss
   **within** the replication-lag window can lose an acked-but-not-yet-replicated message; duplicates
   across failover are allowed and counted; loss is bounded by the reported window, never unbounded and
   never silent. ("Ack after replica applied," 042 OD2, closes this window post-v1.)

### Requirement 6: Master-only side effects during a transition

**User Story:** As an operator, I want the operations that *cannot* tolerate two concurrent actors to
be gated on genuine mastership, so that a brief transition window produces at most tolerable
duplicates, never contradictions.

#### Acceptance Criteria

1. **Ingest-path operations** (`HW.QSEND`, `HW.PUBLISH`) are append-only and replay-safe; a duplicate
   from a transition is permitted and counted. These are never the source of a contradiction.
2. **Delete/decide operations** — `HW.QACK` (a delete), dead-lettering, RPC reply-slot writes, and
   recurring-job (`HW.JOB`) *fires* — are **master-only** and must not be performed by a node that is
   not the herd's master (R1.3). A non-master that receives such a request refuses it with the existing
   `-NOTPRIMARY <endpoint> <epoch>` shape (042 RD9) so the client re-drives it to the real master.
2. A recurring-job occurrence firing exactly once per schedule interval is a master-only decision; a
   node with no herd never fires occurrences (prevents double-fire across a transition).
3. The reconciliation report (042 RD7) is the sanctioned mechanism for a former master's in-flight tail
   after it stands down (R8): pure enqueues in the tail are safe to replay; genuinely conflicting events
   (an ack on one side vs. still-pending on the other) are **flagged in the report** for the operator,
   never silently merged.

### Requirement 7: The narration channel

**User Story:** As a client, I want my current master to tell me about topology changes it observes,
so that graceful and partition transitions are fast and the whole herd hears the same thing at once.

#### Acceptance Criteria

1. The active master can **push** a topology notification to all its connected clients over the
   existing RESP connection (a server-initiated message on the doorbell/pub-sub channel the client
   already holds, or an equivalent push surface — design decides the exact shape, protocol doc updated
   in this feature per the house rule).
2. The notification is **advisory**: it tells clients "re-evaluate your connection" (e.g. "I have lost
   sight of node N," or "stand down to successor"). A client that receives it **re-runs its own rule**
   (R2/R3); it never blindly trusts the notification to the point of connecting somewhere unsafe. A
   *wrong* notification is therefore harmless — worst case a client re-evaluates and stays put.
3. Narration is **one-directional and never a vote**: a node narrating "I lost sight of node N" does
   not *decide* anything about node N's mastership; it informs the herd so the herd can converge faster
   than waiting for individual TCP timeouts.
4. When narration is impossible (the master died hard, no last words), R3.1's TCP-drop + priority-walk
   fallback is what converges the herd. Narration is the fast path; the shared priority order is the
   safety net.

### Requirement 8: A former master stands down deterministically (heal)

**User Story:** As an operator, I want a node that *was* master and then lost its herd (crash-recovery
or partition heal) to recognize it is no longer master and step down without merging conflicting state.

#### Acceptance Criteria

1. A node that comes back (or heals a partition) and observes a **higher epoch** anywhere — from a
   node now serving the herd, or from a client's `-NOTPRIMARY <endpoint> <epoch>` — **demotes
   immediately**: it stops acting as master and refuses master-only verbs with `-NOTPRIMARY` (042 RD7,
   unchanged).
2. An **islanded** former master (alive, but partitioned from every client and every peer) is
   harmless while islanded — it has no herd, so by R1 it is not a master and performs no conflicting
   master-only effects (R6). On heal it stands down per R8.1.
3. On stand-down, the former master's unreplicated in-flight tail becomes the **reconciliation report**
   (042 RD7): messages/acks by queue, replayable by an operator, never auto-merged.
4. The **epoch** is the deterministic tie-break of last resort: if two nodes ever both believe they
   are master (e.g. a stale island rejoining), the herd and the nodes obey the **higher epoch**. This
   is not an election — no votes are counted; the higher number wins by rule.

### Requirement 9: Observability

**User Story:** As an operator, I want to see the herd and mastership state, so that a failover is
legible during and after the fact.

#### Acceptance Criteria

1. `HW.STATS` and the dashboard show, on any node: its role (master / standby / stood-down), its
   epoch, its **connected-client count** (the thing that actually defines mastership), the configured
   priority order, and — on the master — per-replica slot state/lag (042 R7, unchanged).
2. A herd transition is recorded on every node that observed it: cause (TCP drop / narration / higher
   epoch), timing, from-node → to-node, and the epoch before/after.
3. Convergence latency (how long the herd took to re-form on the successor) is observable, so `x`
   (R3.3) can be sanity-checked against reality.

### Requirement 10: No elections — recorded position

**User Story:** As a maintainer, I want the no-elections stance stated so that a future "just add a
vote" proposal has to reopen it explicitly.

#### Acceptance Criteria

1. This feature adds **no** election, quorum, vote-tally, or committee, and no three-node data-node
   floor. Mastership is defined by client connection (R1); the successor is deterministic config (R2);
   convergence is connection-driven (R3); the epoch is a rule-based tie-break, not a tally (R8.4).
2. The stance is recorded in `constraints.md` (extending 042's no-elections constraint) so any change
   toward voting reopens R1/R10 by name.

### Requirement 11: Proof — the gates

**User Story:** As a maintainer, I want herd cohesion and failover correctness proven in CI, so the
central invariant is defended on every commit, not hoped.

#### Acceptance Criteria

1. **Herd-cohesion harness (extends 042 RD10):** multiple in-process clients + a multi-node cluster.
   Scripted **hard kill** of the master: assert every client converges on the *same* successor
   (no split), in-flight RPC and unacked sends are replayed and answered, at-least-once holds
   (zero acked-and-replicated loss), duplicates are counted. Runs in the normal suite.
2. **Partition matrix:** master-isolated-from-peers-but-not-clients (no handoff — master keeps its
   herd), master-isolated-from-everyone (islanded — herd moves to successor, island stands down on
   heal via epoch), client-side reachability differences — each lands in the **designed** state and
   **never** produces two live masters *with clients* (R1.4).
3. **The RPC-across-failover scenario** (R4 worked scenario) is a named test: kill the master mid-RPC,
   prove the caller re-drives the same request id to the successor and receives the reply.
4. **Graceful GOODBYE** (R12): a scripted drain moves the herd to the successor with **near-zero
   replay** (clients' in-flight caches drained before they move) and **zero loss**; the drain-timeout
   path degrades to ordinary replay for anything still in flight at the deadline.
5. **Rejoin without preemption** (R13): a higher-priority node returns while a lower-priority master
   serves; assert the herd **does not move** and the returner sits as a standby until the master
   departs, at which point the herd converges on the (now highest-priority) returner.
6. **Priority collision** (R13): two nodes announcing the same priority — the first is admitted, the
   second is **rejected with a legible error** and does not join.
7. **The assurance rig runs against a failing-over herd**: I1–I5 green with a mid-turbulence master
   transition; the doorbells-off variant included; recorded in `assurance/RUNLOG.md` (extends 042 R8.2).

### Requirement 12: Graceful drain — GOODBYE (planned departure)

**User Story:** As an operator, I want to restart or take a node offline for maintenance without
losing work or forcing a disruptive failover, so that planned departures cost near-nothing.

#### Acceptance Criteria

1. A node being restarted or taken offline issues **GOODBYE** — an explicit push to its connected
   clients (via the narration channel, R7) **and** to its peers — meaning "I am leaving; converge on
   your known successor now." GOODBYE carries **no successor name**: the successor is already common
   knowledge from the roster/priority (R2), so the message is pure timing, not new information.
2. On GOODBYE, the departing master **stops accepting new master-only work** (R6) and **lets in-flight
   drain**: pending RPC replies return and unacked sends get acked, so each client's in-flight cache
   (R4) empties **before** it moves. A client that leaves with an empty cache has **nothing to replay**
   — the near-zero-replay win that distinguishes graceful from crash.
3. Drain is **bounded** by a timeout. Anything not drained at the deadline falls back to the ordinary
   replay path (R4): the client re-drives it to the new master. Graceful **degrades to** failover and
   **never blocks** a maintenance action on one stuck message.
4. The successor takes the next **epoch** and the herd re-forms on it (narration-driven — fast and
   unanimous, not the TCP-drop fallback). The departing node reaches a clean **stood-down** state and
   is safe to restart/offline. On return it rejoins as a standby (R13; no preemption).
5. GOODBYE is **advisory-with-teeth**: a client that ignores the push still gets a clean `-NOTPRIMARY`
   redirect from the now-quiescing node (R6) and converges anyway. Announcement is the fast path;
   verb-refusal is the guarantee.

### Requirement 13: Membership, roster, and no auto-failback

**User Story:** As an operator, I want nodes to join a running set cleanly, misconfigured duplicates
to be caught, and a returning high-priority node not to disrupt a healthy master, so that membership
changes are safe and predictable.

#### Acceptance Criteria

1. **Join announcement.** A starting server connects to the set and **announces its priority**. The
   **master** admits it, adds it to the **authoritative roster**, and **propagates** the roster to
   peers and clients ("a node joined, priority X"). Membership is master-mediated; the master is the
   coordination point the herd already trusts.
2. **Bootstrap vs. live truth.** A client's connection string is the **bootstrap** set (how it reaches
   *a* node). The **live roster from the master is the running truth** for the successor order, so the
   cluster may grow beyond a client's original connection string (**dynamic membership**). A newly
   joined node becomes reachable to clients via the roster, not by editing every client.
3. **Priority collision — first announcer wins, loser rejected.** If a node announces a priority
   already held by a live roster member, it is **refused admission with a legible error** naming the
   holder ("priority X is held by node-Y; fix this node's config"). This keeps priority → successor a
   unique, **timing-independent** mapping: the succession order never depends on restart order.
4. **No auto-failback (no preemption).** A returning or newly joined **higher-priority** node joins as
   a **standby**, syncs (042 snapshot + tail), and **waits**. It **never forces the herd to move**. The
   current master keeps serving until it fails, partitions, or issues GOODBYE (R12). Priority determines
   the **successor at the next transition**, evaluated against the live roster then — it is **not** a
   standing right to seize mastership.
5. **Deliberate failback is GOODBYE.** An operator who wants a specific (e.g. higher-priority) node to
   (re)take mastership issues **GOODBYE on the current master** (R12) — a chosen, timed, zero-loss
   handover — rather than relying on any automatic preemption (which this feature does not provide).
6. **Roster continuity across a transition.** A promoted node already holds the current roster (it
   received propagation as a standby), so succession never loses membership knowledge. Roster state
   rides the same warm-standby channel as replicated data (042).

---

## Non-Goals

- **Elections, votes, quorums, committees** — explicitly rejected (R10).
- **Replicas serving client verbs** — standbys remain warm; the herd is on the master (042 RD1). A
  read-only `HW.STATS`/`HW.DISCOVER`/`HW.REPLAY` surface on a non-master is inherited from 042 OD3.
- **Solving client-death-before-ack** — impossible by causality; stated as the boundary (R5), not a gap.
- **Sync acks in v1** — the RPO window remains 042 RD8 / OD2.
- **Multi-primary, sharding, geo-replication, VIPs/listeners** — out, as in 042.
- **Replacing 042's replication machinery** — WAL-shipping, slots, snapshot, epoch, reconciliation are
  reused unchanged; this feature refines the *control model* around them.

## Open decisions

| | Question | Note |
|---|---|---|
| **OD1** | Narration surface: reuse the doorbell pub/sub channel for the server→client topology push, or a dedicated `HW.*` push? | **Closed 2026-09-15 (042-1a):** the existing doorbell surface, channel `hw:door:topology`; message grammar in the protocol doc. |
| **OD2** | Client health-timeout `x` for a frozen-but-TCP-alive master — exact default and how it composes with the doorbell keepalive. | **Closed 2026-09-15 (042-1a):** `x` = 3s default, with the ordering `x ≤ W (3s) < T_fence (5s)` — the willingness threshold `W` sits between, so a walking client meets a willing standby before the fence backstop matters. |
| **OD3** | Does a non-master accept and buffer ingest (`HW.QSEND`/`HW.PUBLISH`) during the brief window before it knows it is not master, or refuse with `-NOTPRIMARY` immediately? | R6 leans refuse-and-redirect (client re-drives); revisit if ingest-availability-under-partition becomes a stated demand. |
| **OD4** | Reconciliation of the RD6 deadman constants now that it is a backstop — do `T_fence`/`T_promote` change when the primary path is herd convergence? | Depends on measured convergence `x`; keep 042 OD1 values until the harness shows a reason. |
| **OD5** | Priority-collision loser: reject, or admit and order-behind via offset tie-break? | **Closed: reject** (R13.3). Keeps priority → successor unique and timing-independent; a mis-set node is refused with a legible error rather than silently re-ordered. Reopen only if a real deployment needs collision-tolerant admission. |
| **OD6** | Is the connection string the complete fixed cluster set, or a bootstrap with the master's roster as live truth? | **Closed: bootstrap + live roster** (R13.2), i.e. dynamic membership — the master can introduce nodes to clients at runtime. Reopen if a fixed, no-growth cluster is ever preferred for simplicity. *(042-1a adds: the grammar is the existing SE.Redis comma form — no `highway://` scheme; roster lives in the replicated store at `repl:roster`.)* |
| **OD7** | GOODBYE drain timeout default, and whether it is per-node config or a cluster constant. | Design (R12.3); target a few seconds, degrading to replay at the deadline. Must never let one stuck handler block a maintenance departure. |
