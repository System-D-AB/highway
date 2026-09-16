# Feature 042 — Replication: Two Nodes, Priorities, No Elections

*Stage 2 of the 037 program (D7's deferral, research Part VI), startable only after 041
closes G2–G4. Authority for the mechanics beneath it: 038 (WAL sync-per-commit,
`GetUpdatesSince` proven in the sibling probes) and 037 R5 (effects-never-intent — the
property that makes shipped WAL replay byte-identical, adopted early for exactly this
feature).*

## Introduction

Highway is a broker in the true sense: a small, drainable, high-churn catalogue whose
acks are at-least-once **promises** — not terabytes whose reads need scaling. That
inverts the usual replication trade-offs: initial sync is seconds (so aggressive
retention caps are safe), replicas serve **no client traffic** (so all of Mongo's
read-preference machinery is deleted), and failover is **rehearsable in CI** with two
embedded nodes — a luxury none of the systems we borrowed from has.

### The lineage, recorded once (settled 2026-09-15)

| Piece | Borrowed from |
|---|---|
| Ship deterministic effects (the WAL itself) | PostgreSQL/SQL Server physical logs; Redis's command-rewrite is the same law |
| Resume by sequence; per-replica retention **slots with a cap** | PostgreSQL slots + `max_slot_wal_keep_size` lesson |
| Checkpoint copy + tail as initial sync | `pg_basebackup`; Redis RDB full sync |
| Priority-ordered successor, `0` = never promote | Redis `replica-priority`, verbatim semantics |
| Primary self-fences without replica/witness contact | Redis `min-replicas-to-write`, made a first-class rule |
| Epoch fencing on every promotion | Postgres timelines; Redis Cluster `configEpoch` |
| 2 data nodes + optional witness process; **no data-node votes, ever** | DB mirroring (principal/mirror/witness), WSFC witness, `pg_auto_failover` |
| Client-side writable-host selection | Postgres multi-host connection strings |

**Explicitly rejected:** Sentinel committees (needs 3 monitors), Redis-Cluster/Mongo
elections (needs 3 voting data nodes), listeners/VIPs, read-scaling replicas,
sharding, multi-primary. The 3-node floor exists only where data nodes vote; we
removed the voting, so the floor goes with it.

## Decisions — RESOLVED (2026-09-15)

| | Decision |
|---|---|
| **RD1** | **One primary, N replicas, priority-ordered.** Replicas serve no `HW.*` client traffic — warm standbys (read-only stats surface is an OD, not v1) |
| **RD2** | **Transport = the primary's own WAL**, tailed via `GetUpdatesSince`, served over a new `HW.REPL.*` RESP surface on the 040 server: **pull-based, paged, resumable by sequence** — house continuation-token style, no streaming state |
| **RD3** | **Retention = per-replica slots with a hard cap.** The primary retains WAL to the minimum acked watermark of registered replicas; a replica past the cap loses its slot and re-bootstraps (cheap — drainable data). A dead replica can never fill the disk. *(Mechanism resolved in the 2026-08-29 design addendum: driven by `SetWalTtlSeconds`/`SetMaxTotalWalSize`, never a steady-state `DisableFileDeletions()`, which would let a dead replica pin the WAL.)* |
| **RD4** | **Initial sync = checkpoint + tail**: RocksDB checkpoint shipped over `HW.REPL.SNAPSHOT` (chunked, same transport), then tail from the checkpoint's sequence |
| **RD5** | **Successor = configured priority** (lowest number promotes first; `0` = never — Redis semantics, to surprise nobody). Offset breaks ties |
| **RD6** | **No elections, three failover tiers:** (a) v1 default — explicit `promote` (operator/automation); (b) opt-in auto — the **two-timeout deadman**: primary self-fences to read-only after `T_fence` without replica/witness contact; the top-priority live replica self-promotes after `T_promote > T_fence + margin`, so both are never writable at once; (c) optional **witness** — a tiny third process that only answers "I can see you", letting a primary survive replica loss without fencing |
| **RD7** | **Epoch fencing:** a monotonic epoch, incremented by every promotion, carried on every `HW.REPL.*` exchange and heartbeat. A lower-epoch primary demotes itself on first contact; its unreplicated tail becomes a **named reconciliation report**, never a silent merge |
| **RD8** | **Acks: async in v1** — the RPO window *is* the replication lag, measured and surfaced. "Ack after replica applied" is a designed-for opt-in (OD2), affordable at our sizes, not a v1 deliverable |
| **RD9** | **Clients:** multi-endpoint connection string; a non-primary refuses writes with `-NOTPRIMARY <primary-endpoint> <epoch>`; the client retries the other endpoint. `Highway.Client` **may change** in this feature (unlike 037) — this is new capability, not a port |
| **RD10** | **Failover is a CI test, not a runbook hope:** two embedded nodes in-process; kill, promote, fence, reconcile — asserted on every commit |

## Requirements

### Requirement 1: The replication stream

1. `HW.REPL.HELLO` (register/resume a slot: replica id, last applied sequence, epoch)
   and `HW.REPL.PULL` (paged WAL batches from a sequence) exist on the 040 server;
   `HIGHWAY-PROTOCOL.md` gains them **in this feature** (house rule).
2. A replica applies pulled batches **byte-exactly** and records its applied watermark
   **atomically with the batch it covers** — a crash between apply and watermark can
   never double-apply (counters make double-apply visible; the test proves it).
3. Pull is resumable at every page boundary; a re-pulled page is detected by watermark
   and skipped without effect.
4. The stream carries the primary's epoch; a replica refuses a lower epoch than it has
   seen (RD7).

### Requirement 2: Initial sync and re-sync

1. A blank replica bootstraps entirely over the wire: `HW.REPL.SNAPSHOT` (chunked
   checkpoint transfer, resumable) → open → tail from the checkpoint sequence.
   Measured end-to-end time on a healthy-sized broker is recorded (expected: seconds).
2. A replica whose slot was dropped (RD3 cap) re-bootstraps through the identical path
   — one code path for both cases.

### Requirement 3: Retention slots

1. The primary tracks each registered replica's acked watermark; WAL files are deleted
   only past the minimum watermark — **and never retained past the configured cap**.
2. Slot states (active / lagging / dropped) and per-replica lag (sequences and bytes)
   are visible in `HW.STATS` and the dashboard.
3. A dropped slot is an event with a name in the log, not a silent state.

### Requirement 4: Promotion, epoch, fencing

1. `promote` exists as an explicit verb (036-style host verb and/or admin command —
   design decides), usable by an operator or external automation. It increments the
   epoch, flips the node writable, and records the promotion.
2. A primary that observes a higher epoch (any channel) **demotes immediately**:
   refuses writes with `-NOTPRIMARY`, preserves its unreplicated tail, and emits the
   **reconciliation report** — messages/acks in the tail, by queue, replayable by an
   operator; never auto-merged.
3. A fenced or demoted node keeps serving reads/stats — degraded, legible, never
   pretending.

### Requirement 5: Opt-in automatic failover (the deadman)

1. Off by default. When enabled, configuration **validates the timeout invariant**
   (`T_promote > T_fence + margin`) at startup and refuses to run otherwise — the
   no-dual-writable guarantee is config-checked, not hoped.
2. Primary self-fences to read-only after `T_fence` without replica or witness
   contact; the top-priority live replica promotes after `T_promote`; priority `0`
   never promotes.
3. The optional witness is a separate tiny process speaking one question; primary
   contact with **either** replica or witness defers fencing. Losing only the replica
   with a witness present does not fence the primary.
4. Every automatic transition logs cause, timings and epoch — auditable after the
   fact.

### Requirement 6: The client survives failover

1. `Highway.Client` accepts multiple endpoints; on `-NOTPRIMARY` (or connection loss)
   it retries against the others with bounded backoff; in-flight sends surface as the
   existing transient class, so application retry semantics are unchanged.
2. At-least-once holds across failover: **duplicates allowed, silent loss not** —
   within RD8's stated async window, which is measured (R7) and documented in the
   constraints register.
3. The doorbell subscriber reconnects and re-subscribes to the new primary; the
   backstop sweep remains the correctness path (unchanged law).

### Requirement 7: Observability

1. `HW.STATS` and the dashboard show: role, epoch, per-replica slot state and lag,
   fencing state, last promotion (when, why, from what).
2. Replication lag is exported in a form the deadman margin can be sanity-checked
   against.

### Requirement 8: Proof — the gates

1. **The embedded failover harness (RD10):** two in-process nodes + client; scripted
   kill of the primary; asserts — promotion by priority, epoch increment, old primary
   fences on resurrection, reconciliation report contents, **zero acked-and-replicated
   loss, duplicates counted** — running in the normal test suite.
2. **The assurance rig runs against a failing-over pair**: I1–I5 green with a
   mid-turbulence promotion; the doorbells-off variant included. Recorded in
   `assurance/RUNLOG.md`.
3. A soak of the deadman with induced partitions (primary isolated; replica isolated;
   witness lost) proving each lands in the designed state, never dual-writable.

### Requirement 9: The record

1. New constraints, each numbered in `constraints.md`:
   - **The RPO statement (async window)** — in the blunt form fixed by the 2026-08-29 design
     addendum §"The RPO hole, stated bluntly": an ack is durable on the primary immediately
     (sync-per-commit, 038) and on replicas within the *measured* lag window (R7); a primary loss
     within that window can lose an acked-but-not-yet-replicated message; duplicates across
     failover are allowed and counted; loss is bounded by the reported window, never unbounded and
     never silent. "Ack after replica applied" (OD2) closes it, post-v1.
   - **The fencing availability trade** — replica loss fences a witness-less primary (RD6c).
   - **The no-elections position** — RD6.
   - **WAL compression stays off** (design addendum §"WAL compression must stay off"): a recorded
     engine-config coupling, because `RocksDbWalInspector` — the reconciliation-report/audit engine
     (RD7) — cannot parse compressed WAL. A future `SetWalCompression` reopens this decision
     explicitly. (WAL-only; batch/SST compression unaffected.)
2. Roadmap's clustering entry re-pointed at this feature; **O10 is closed** by RD6
   (the 2-node fork resolved: priorities + fencing, witness optional).
3. `product.md` gains the replication story in one paragraph, linking here.

## Non-Goals

Sharding, multi-primary, elections in any form, replicas serving client verbs,
witness-as-requirement, listener/VIP infrastructure, geo-replication, sync-ack in v1
(OD2), automatic reconciliation of a diverged tail.

## Open decisions

| | Question | Owner |
|---|---|---|
| **OD1** | `T_fence` / `T_promote` / margin defaults | **Closed 2026-09-15:** 5s / 8s / 1s. `AutoFailover` remains off by default |
| **OD2** | Ack-after-replica-applied: adopt when, as what surface (per-queue? global?) | post-v1 of this feature |
| **OD3** | Read-only stats endpoint on replicas (serve `HW.STATS` while refusing verbs?) | **Closed 2026-09-15:** yes — `HW.STATS` / `HW.DISCOVER` / `HW.REPLAY` run on a non-primary; mutating verbs get `-NOTPRIMARY` |
| **OD4** | Witness protocol shape (one RESP question vs a file/blob lease) | **Closed 2026-09-15:** `HW.REPL.WITNESS` → `+OK` |
