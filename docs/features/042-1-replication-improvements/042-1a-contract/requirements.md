# Feature 042-1a — The Herd Contract: Requirements

*First sub-feature of [042-1](../requirements.md). Executes parent T0 (reconcile with the
as-built 042 code) and T1 (the protocol surface). **Nothing in 042-1b/c/d starts until this
closes** — the client and server halves are built by different tasks against the shapes this
feature pins, and two failover models must not collide in the codebase.*

## Introduction

042-1's model has one novel hinge the parent spec deliberately left open: **when is a standby
"willing"** to answer a client's handshake with OK (parent R2.4)? That predicate *is* the
no-split guarantee. This sub-feature settles it, along with the other cross-cutting decisions
(epoch-bump timing, where the roster lives, which channel narrates), records the
reuse/change/delete map against the code 042 actually shipped, and lands every wire shape in
`HIGHWAY-PROTOCOL.md` so 042-1b (client) and 042-1c (server) implement against one contract.

## Requirements

### A-R1: The reconciliation map (parent T0)

1. A dated design addendum records, with **file references into the current tree**, what 042-1:
   - **reuses unchanged** — WAL-ship (`ReplicationFeeder` Pull/Hello/Ack, slots+cap), snapshot
     (`HW.REPL.SNAPSHOT`, `ReplicaPuller.DownloadSnapshot`), gap refusal + resync re-bootstrap
     (`HW_REPL_GAP`, resync marker), epoch persistence (`repl-epoch.txt`),
     `ObserveHigherEpoch` demote/adopt, the WAL-tail reconciliation report (`WalTailSummary`),
     `-NOTPRIMARY` + computed redirect, raw-write gating, the promote announcement
     (epoch+endpoint HELLO gossip);
   - **changes** — the promotion trigger (timer → herd arrival), the client's successor rule
     (probe-for-Primary → roster priority walk);
   - **deletes** — the witness apparatus (witness loops, the `WITNESS <id> <role>`
     peer-question form, the witness-gated promotion, the priority stagger) and the deadman's
     self-promote path.
2. The map records the structural insight that makes R1.3/R6 cheap: **Highway's server performs
   no autonomous side effects** — acks, dead-letters, job fires, sweeps and reply-slot writes
   all ride client verbs — so "no herd ⇒ no master-only effects" holds by construction, and the
   verb gate exists only for the stale-client-reaches-old-master case.

### A-R2: The willingness predicate — the no-split rule

1. A standby answers the herd handshake **willing** iff **all** of:
   (a) its priority ≠ 0; (b) it has **itself lost the master** — its puller has had no
   successful exchange with the master for at least the willingness threshold, **or** it
   received the master's GOODBYE/step-down narration. A standby whose link to the master is
   healthy answers **unwilling**, whatever any client believes.
2. Rationale recorded: promotion therefore requires **two independent observers** of the loss —
   the client (its trigger) and the standby (its own dead link) — which is 042's witness role
   absorbed into the topology, with no third process.
3. The willingness threshold is a named constant/config with a stated relation to the client
   health timeout `x` and the fence backstop: `x ≤ willingness threshold < T_fence` — a client
   may start walking before a standby is willing (it retries), but a standby must become
   willing before the old master's fence backstop is the only thing standing.
4. The unwilling reply names the master the standby still sees (endpoint + epoch), so a
   confused client is *redirected*, not merely refused.

### A-R3: Promotion by herd arrival

1. A willing standby **becomes master** — bumps the epoch (persisted), starts serving, and
   fires the 042 promote announcement toward the old master — on the **first accepted client
   verb** after a willing handshake, not on connection establishment (probes must not bump
   epochs).
2. The transition is recorded (cause = herd-arrival, epoch before/after) for parent R9.2.

### A-R4: The narration channel and message shapes (parent R7, R12.1)

1. Narration rides the **existing doorbell push surface** (the client already holds a
   subscribed connection): a reserved topology channel (`hw:door:topology` or as design names
   it). No new push protocol.
2. Message shapes are specified for: **topology-changed** (advisory; carries the roster
   version), **step-down/GOODBYE** (pure timing; names no successor), and **roster-update**
   (a node joined/left, with priority). Each is advisory per parent R7.2 — a client re-runs
   its own rule.

### A-R5: Membership surface and the roster's home (parent R13)

1. **The roster lives in the replicated store** as ordinary KV state written by the master —
   so it WAL-ships to standbys with zero new machinery and a promoted node already holds it
   (parent R13.6 by construction).
2. Wire surface specified: **join-announce** (node → master: id, priority, endpoint; master
   admits into the roster or refuses `priority-taken` naming the holder — parent R13.3), and a
   **roster read** for clients (extend `HW.REPL.STATUS` or a small `HW.REPL.ROSTER` — design
   decides) returning members, priorities, endpoints, and a roster version.
3. The collision-refusal error shape is legible and permanent-class (`ERR HW_` family).

### A-R6: The bootstrap connection-string grammar (parent R13.2)

1. The existing SE.Redis comma form **is** the bootstrap grammar
   (`host1:port1,host2:port2,…,password=…`) — no new URI scheme. The string is bootstrap-only;
   the live roster is the running truth. Single-endpoint strings stay valid, byte-compatible.
2. Recorded as the closure of parent design §"Connection string format"'s `highway://` sketch:
   rejected in favour of the form the client already parses.

### A-R7: The protocol document (house rule)

1. Every shape above lands in `HIGHWAY-PROTOCOL.md` in this sub-feature: changelog entry, any
   new command with arity in the Command Index, the narration/GOODBYE message grammar, the
   willingness handshake, and the bootstrap-string note. `ProtocolConformanceTests` green in
   both directions.

## Non-Goals

Implementation of any of it — 042-1b/c build against this contract. No elections (parent R10).

## Open decisions (to close in this sub-feature)

| | Question |
|---|---|
| **A-OD1** | **Closed 2026-09-15:** fold into the HELLO family — `HW.REPL.HELLO CLIENT <clientId> <lastSeenEpoch>` (design D2). No new command name. |
| **A-OD2** | **Closed 2026-09-15:** `W = 3s` (`Replication.WillingnessThreshold`), server-validated `0 < W < T_fence`; `x ≤ W` is a documented default coupling (x is client-side config; both default 3s) recorded in the constraints register by 042-1d. |
| **A-OD3** | **Closed 2026-09-15:** extend `HW.REPL.STATUS` with `roster.*` fields — no new command. |
| **A-OD4** | **Closed 2026-09-15:** KV `repl:roster` (`k` family), `RosterRecord` v1 encoding: format byte, `u64 version`, `u16 count`, then per member length-prefixed `nodeId`, `i32 priority`, length-prefixed `endpoint` (big-endian, UTF-8). |
