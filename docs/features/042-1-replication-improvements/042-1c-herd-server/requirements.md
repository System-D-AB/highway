# Feature 042-1c — The Herd Server: Requirements

*Third sub-feature of [042-1](../requirements.md); implements the server half against the
[042-1a contract](../042-1a-contract/design.md). Executes parent T2.5, T5, T6, T6.5, T7, T7.5,
T8. Touches `Highway.Server` (and the host verbs). Starts after 042-1a; independent of 042-1b
except where marked.*

## Introduction

The server's job in the herd model is smaller than 042's was: answer the willingness handshake
honestly, promote when the herd actually arrives, own the roster, narrate what it observes,
drain gracefully on GOODBYE, stand down on a higher epoch (all reusing 042 machinery) — and
**delete** the timer-promotion/witness apparatus so exactly one failover model exists.

## Requirements

### C-R1: Herd state and the willingness answer (parent R1, R2.4; contract A-R2)

1. The RESP layer tracks **connected authenticated client sessions** (replica pullers and
   `HW.REPL.*`-identified peers excluded). This count is the mastership input and an
   observability field (parent R9.1).
2. `HW.REPL.HELLO CLIENT` is answered per the contract predicate: willing iff priority ≠ 0 ∧
   (own master-link lost ≥ `W` ∨ GOODBYE received); unwilling answers carry the still-seen
   master's endpoint + epoch. `x ≤ W < T_fence` is validated at startup.

### C-R2: Promotion by herd arrival (parent R1.1, R3; contract A-R3)

1. A willing standby promotes — 042 `TryPromote` reused: epoch++ persisted, promote
   announcement to the old master — on the **first accepted non-`HW.REPL.*` client verb**
   after a willing handshake. Probes/handshakes/STATUS never promote.
2. The transition is recorded with cause `herd-arrival` (parent R9.2).

### C-R3: Membership — join, roster, collision (parent R13.1–R13.3, R13.6; contract A-R5)

1. `HW.REPL.JOIN <nodeId> <priority> <endpoint>`: the **master** admits the joiner into the
   roster (stored as replicated KV `repl:roster`, master-writes-only, versioned) and narrates
   `ROSTER-UPDATE`; a live-held priority is refused `ERR HW_PRIORITY_TAKEN` naming the holder.
2. A joining node announces its own-config priority at startup (the `ReplicaPuller` join path)
   and enters as a warm standby (042 snapshot + tail, unchanged).
3. A promoted node serves from the roster already in its own store (no transfer step —
   asserted).
4. Roster read: `HW.REPL.STATUS` `roster.*` fields (contract A-OD3 closure).

### C-R4: Narration + GOODBYE drain (parent R7, R12; contract A-R4)

1. The master pushes `TOPOLOGY`/`ROSTER-UPDATE`/`GOODBYE` on `hw:door:topology` via the
   existing doorbell publish surface. Advisory; lossy is acceptable; `-NOTPRIMARY` remains the
   teeth.
2. **GOODBYE drain** (operator-initiated: host verb `highways --goodbye` and an admin command
   — design decides the exact carrier): push GOODBYE to clients and peers → stop accepting
   new master-only work (the existing gate) → let in-flight drain (pending replies return,
   unacked sends ack) → bounded by `Replication.GoodbyeDrainTimeout` (parent OD7; default a
   few seconds) → reach **stood-down** (a demoted-equivalent state at the same epoch;
   the successor takes epoch+1 on herd arrival) → safe to restart; rejoins as standby.
3. A client that ignores GOODBYE still converges via `-NOTPRIMARY` (kept).

### C-R5: Heal and no-preemption (parent R8, R13.4, R13.5)

1. Higher-epoch stand-down, the reconciliation report, and the islanded-node harmlessness are
   **042 reuse** — re-asserted under the new model, not rebuilt.
2. **No auto-failback**: a returning higher-priority node joins, syncs, and waits. No code
   path promotes on rejoin; a test asserts the herd does not move (the willingness predicate
   makes this structural: a standby with a healthy master-link is unwilling).

### C-R6: One failover model — deadman to backstop, witness deleted (parent T8; contract D6/D7)

1. The deadman keeps its **fence** half (no herd + no peer/witness contact past `T_fence` →
   read-only) with unchanged constants and validation.
2. The **promote** half, the witness loops, the `WITNESS <id> <role>` peer-question, the
   witness-answer machinery, and the priority stagger are **deleted**, citing 042-1a's
   reconciliation map. `HW.REPL.WITNESS` reverts to the bare `+OK` probe. 042's affected
   fake-clock tests are reframed (fence/unfence keep coverage; promotion tests move to
   herd-arrival shape).
3. Config surface follows: witness options removed or marked obsolete (host schema test
   updated in the same task — 031 R2.1 discipline), `WillingnessThreshold` and
   `GoodbyeDrainTimeout` added.

### C-R7: Proof, server-scoped

1. Unit/fake-clock: willingness truth table against real feeder state; herd-arrival promotion
   (probe does not promote, first verb does); collision-reject; roster write/read/version;
   GOODBYE state machine incl. drain timeout degrade; fence backstop still fences; no
   promotion path exists on timers (asserted).
2. Integration (pair fixture): join → roster narrated; promoted standby holds the roster;
   GOODBYE moves a connected raw client via narration or `-NOTPRIMARY`.

## Non-Goals

Client walk/replay (042-1b); multi-client cohesion and partitions (042-1d); ingest-buffering
on non-masters (parent OD3 stays refuse-and-redirect); elections.
