# Feature 042 — Implementation Gap Report

*Review date: 2026-09-15. Reviewed against `requirements.md` (RD1–RD10, R1–R9), `design.md`
(incl. the 2026-08-29 and 2026-09-15 addenda), and the working tree as of this date
(uncommitted, on `rocksdb`).*

> **Status update 2026-09-15 (post-review):** G1–G9 were fixed in the tree, and then feature
> [042-1](../042-1-replication-improvements/requirements.md) redefined the failover control
> model (client-herd mastership). Under it, the **G1 witness protocol** and the **G3
> promotion stagger** are **superseded and deleted** — timer-driven promotion no longer
> exists, so the problem they solved is gone (see the
> [reconciliation map](../042-1-replication-improvements/042-1a-contract/reconciliation-map.md)).
> G2, G4–G9 carry forward as 042-1's reused machinery. G10/G11 close under 042-1d's harness
> and record tasks.
>
> **Closure 2026-09-16 (feature 042-1 complete):** every gap is now resolved or superseded —
>
> | Gap | Disposition |
> |---|---|
> | G1 witness / no-split | **Superseded.** No-split is now the willingness predicate (a standby is willing only when its *own* master-link is dead) — two independent observers of the loss, no third process. Witness code deleted. |
> | G2 epoch persistence | **Carried forward** (`repl-epoch.txt`), load-bearing in the herd-arrival + heal paths. |
> | G3 promotion stagger | **Reincarnated correctly** — moved from the deleted promote *timer* to the *willingness* threshold (`Willingness.StaggerPerPriority`), where it makes the highest-priority successor turn willing first so the herd converges on one node. Proven by `HerdCohesionTests.HardKill…`. |
> | G4 WAL-gap re-bootstrap | **Carried forward** (`HW_REPL_GAP` + resync marker). |
> | G5 redirect endpoints | **Carried forward and extended** — the herd walk follows a standby's redirect; a self-redirect falls back to the roster walk. |
> | G6 client failover | **Replaced** by the full herd walk (roster-ordered, willingness handshake) + same-id replay. |
> | G7 raw-write gating | **Carried forward** (C9.3). |
> | G8 host schema | **Carried forward**; `WillingnessThreshold`/`GoodbyeDrainTimeout` added, `WitnessServer` removed, all schema-tested. |
> | G9 reconciliation content | **Carried forward** (`WalTailSummary`), reused at GOODBYE stand-down and heal. |
> | G10 harness rigor | **Met** by `HerdCohesionTests` (real kills, wire read-back, counted duplicates, partition matrix, RPC-across-failover). The assurance-rig-against-a-herd soak remains honestly pending (RUNLOG 2026-09-16). |
> | G11 constraint statuses | **Corrected** — C9.2/C9.3 rewritten for the herd model; C9.2a/C9.6/C9.7 added. |

**Suite status at review:** 1,324 tests, **1 failing** —
`SchemaCompletenessTests.EveryHighwayServerOption_IsReachableFromTheSchema` (G8). Everything
else green, including `ProtocolConformanceTests` over the eight new `HW.REPL.*` commands.

**What is sound and needs no rework:** the T2v/T2 apply-atomicity core (mechanism (a),
sync ingest, derived watermark, hard-kill proven), the T1 pull protocol (paged, resumable,
epoch-stamped), the T3 RESP-streamed snapshot bootstrap, the protocol-doc §Replication
Commands + changelog 4.6/4.7, and the dated design addenda recording the toolkit couplings.
The gaps below sit almost entirely in the **failover safety layer** (deadman, witness,
epoch, retention enforcement) and in **claims that outrun the code**.

Severity: **S1** = violates a safety guarantee the spec sells (dual-writable or silent
loss possible). **S2** = a required behaviour is missing; failure is visible, not silent.
**S3** = claim/documentation exceeds the implementation.

---

## S1 — Safety

### G1. Witness contact feeds the wrong clocks — dual-writable with a witness present, or auto-failover silently disabled

- **Spec:** RD6(c), R5.3 — witness contact lets a *primary* survive replica loss without
  fencing. The two-timeout no-dual-writable argument (C9.2) assumes the fence and promote
  clocks are coupled to the *same* silence.
- **As built:** `ReplicaPuller.WitnessLoopAsync`
  ([ReplicaPuller.cs:158-183](../../../src/Highway.Server/Storage/Rocks/ReplicaPuller.cs))
  pings the witness and calls `NoteContact()` on whatever node it runs on; the deadman
  (`ReplicationFeeder.TickDeadman`) reads that single `LastReplicaContact` timestamp for
  **both** the fence decision and the promote decision.
- **Failure scenarios:**
  1. Partition between primary and replica; witness sees both. Primary keeps witness
     contact → never fences (by design). Replica's promote clock ignores the witness →
     self-promotes at `T_promote`. **Two writable nodes, witness present.**
  2. `WitnessServer` configured on the *replica*: witness pings reset the replica's
     contact clock → the replica **never promotes even with the primary dead**.
- **Close by:** separate the contact sources. Witness contact may defer *fencing* on a
  node whose role is Primary, and must never feed a replica's promote clock. For the
  replica to promote safely while a witness exists, it must either confirm the witness
  cannot see the primary (extend `HW.REPL.WITNESS` to answer about a named peer) or the
  witness must be dropped from v1 with RD6(c) marked unimplemented. Add a pair test:
  primary↔replica partition with a live witness ends with exactly one writable node.

### G2. Epoch is not persisted — fencing fails across restart

- **Spec:** RD7 — "a **monotonic** epoch, incremented by every promotion"; R4.2 —
  resurrected lower-epoch primary demotes on first contact.
- **As built:** `ReplicationFeeder` starts every process at `Epoch = 1`
  ([ReplicationFeeder.cs:33](../../../src/Highway.Server/Storage/Rocks/ReplicationFeeder.cs));
  `BatchApplier.SeenEpoch` is also process-memory only. Nothing writes the epoch to disk.
- **Failure scenario:** replica promotes (epoch 2); the promoted node restarts (crash,
  deploy); it reopens at epoch 1. The old primary — also epoch 1 — is never demoted on
  contact; neither node yields. The harness misses this because its "resurrection" is an
  in-process `HELLO` simulation, not a restart.
- **Close by:** persist the epoch durably on every change (a small KV in the store's own
  column family, written sync — it changes only on promote/demote, so cost is nil) and
  restore it in the feeder and applier on open. Add a restart-the-promoted-node test.

### G3. Fenced primary unfences on *any* inbound contact; RD5 priority ordering is unimplemented

- **Spec:** RD5 — "successor = configured priority (lowest number promotes first);
  offset breaks ties"; RD6(b) — *the top-priority live replica* self-promotes.
- **As built:** `UnfenceIfContact` flips Fenced→Primary on any `HELLO`/`PULL`/`ACK`/
  `WITNESS` contact. Priority is only checked as `0 = never`
  ([ReplicationFeeder.cs:201](../../../src/Highway.Server/Storage/Rocks/ReplicationFeeder.cs));
  there is no ordering, no stagger, no offset tiebreak.
- **Failure scenarios:** (1) two replicas, A promotes at `T_promote`; B — lower priority,
  still configured against the old primary — keeps pulling it, and those pulls unfence
  the old primary at the old epoch. (2) Two eligible replicas both hit `T_promote`
  simultaneously and **both promote**.
- **Close by:** for v1 either document the topology limit (exactly one promotable
  replica) and validate it, or stagger promotion by priority (Redis-style
  `T_promote + priority × step`) and make unfence epoch-aware (contact carrying a higher
  epoch demotes; only contact at own epoch unfences).

### G4. No WAL-gap detection, and a dropped slot resurrects itself — silent divergence

- **Spec:** R3.1 — WAL deleted "only past the minimum acked watermark — and never
  retained past the configured cap"; RD3 — a replica past the cap "loses its slot and
  **re-bootstraps**"; R1.3 — re-pull detected and skipped *without effect*.
- **As built:** retention is only `SetWalTtlSeconds(86_400)` + `SetMaxTotalWalSize(1 GiB)`,
  hardcoded in [RocksDbStore.cs](../../../src/Highway.Server/Storage/Rocks/RocksDbStore.cs);
  the min-acked watermark is computed and returned by `HELLO` but **drives nothing**.
  `Pull` never verifies that the first returned batch actually follows `fromSeq`
  (`GetUpdatesSince` on a truncated WAL can hand back a later starting point). The
  slot-drop event fires, but the puller's next `HELLO` — 200 ms later — unconditionally
  re-registers the slot as Active
  ([ReplicationFeeder.cs:75](../../../src/Highway.Server/Storage/Rocks/ReplicationFeeder.cs)),
  so nothing ever forces the re-bootstrap. Only a **blank** data directory bootstraps
  ([RocksDbStore.Open](../../../src/Highway.Server/Storage/Rocks/RocksDbStore.cs)).
- **Failure scenario:** replica offline > 24 h (or primary writes > 1 GiB of WAL). WAL
  segments below the replica's watermark are deleted. The replica resumes pulling and
  either ingests a gapped stream or spins on empty pages forever — in both cases
  believing itself healthy. **Silent divergence**, the exact thing RD3's drop/re-bootstrap
  cycle exists to prevent.
- **Close by:** (a) in `Pull`, when the first available batch's sequence >
  `fromSeq + 1`, refuse with a named error (`HW_REPL_GAP` or similar) instead of serving
  a gapped page; (b) in the puller, treat that refusal — and a Dropped slot reply — as
  "wipe local state and re-run the snapshot bootstrap path"; (c) make `HELLO` refuse to
  resurrect a Dropped slot at a stale watermark (reply names the snapshot requirement);
  (d) surface the retention knobs in `HighwayReplicationOptions` instead of consts.

---

## S2 — Required behaviour missing

### G5. `AdvertiseEndpoint` defaults to the node's own address — `-NOTPRIMARY` redirects to itself

- **Spec:** RD9 / R6.1 — the refusal carries the endpoint "where the client should go".
- **As built:** both hosts default it to the node's own `bind:port`
  ([HighwayTestServer.cs](../../../src/Highway.Server/HighwayTestServer.cs),
  [RespHighwayServer.cs](../../../src/Highway.Server/RespHighwayServer.cs)); a replica's
  refusal therefore advertises the replica. The pair test hand-sets the value —
  and mutates it mid-test — to make failover work
  ([ReplicationPairTests.cs:74,140](../../../tests/Highway.Integration.Tests/ReplicationPairTests.cs)).
- **Close by:** a replica should advertise its `PrimaryServer` host by default; a node
  that promotes should start advertising itself; a demoted/fenced node should advertise
  the highest-epoch peer it has heard from. Assert in the pair test with **no** manual
  assignment.

### G6. Client does not fail over on a dead primary

- **Spec:** R6.1 — "on `-NOTPRIMARY` **or connection loss** it retries against the
  others". This is the headline scenario: the primary is *gone*, so nothing speaks the
  refusal.
- **As built:** `TrySwitchOnNotPrimary`
  ([HighwayConnection.cs](../../../src/Highway.Client/Engine/HighwayConnection.cs))
  switches only when a live server replies `-NOTPRIMARY`. There is no multi-endpoint
  list; `HighwayConnectionSource` holds one active server. Kill the primary and the
  client reconnects to the corpse indefinitely.
  Secondary issue: the switch swaps `_redis`/`_db`/`_subscriber` unsynchronized and
  disposes the old multiplexer while in-flight operations may still hold it.
- **Close by:** accept a list of endpoints (settings or connection-string form); on
  connection loss, walk the list probing `HW.REPL.STATUS` (or any read) for the writable
  node with the existing bounded backoff. Guard the multiplexer swap (swap-then-dispose
  after a drain, or interlocked reference). Add a kill-the-primary client test — no
  fence, no refusal, process actually stopped.

### G7. Replicas accept raw-wire writes

- **Spec:** RD1 / C9.3 — "replicas serve no client traffic"; R4.3 — degraded but never
  pretending.
- **As built:** the `-NOTPRIMARY` gate lives in `CommandDispatcher.Dispatch` (HW.* only).
  `RespSession`'s raw surface — `SET`/`SETEX`/`PSETEX` on `hw:idem:*`, `DEL`/`UNLINK` on
  `hw:idem:*`/`hw:rep:*` — is ungated, so a client pointed at a replica silently writes
  markers into the replica's local store, which the next pulled page may then interleave
  with.
- **Close by:** route the same writability check through the `RespSession` raw-key
  handlers (reads may stay; writes answer `-NOTPRIMARY`). One unit test per verb.

### G8. `Replication` options unreachable from `highway.json` — the failing test

- **Spec:** 031 R2.1 (host schema completeness — the guard test that is currently red).
- **As built:** `HighwayServerOptions.Replication` exists; the host schema has no
  `server.replication.*` mapping, so operators cannot configure any of this from the
  config file — while the host *did* gain `--promote`.
- **Close by:** add the schema section (all `HighwayReplicationOptions` leaves except
  `Clock`), regenerate/extend `HostArguments` plumbing, and the red test goes green.
  This is the commit blocker: **CI is red until it lands.**

### G9. Reconciliation report has no content

- **Spec:** R4.2 / RD7 — the unreplicated tail as "messages/acks in the tail, **by
  queue**, replayable by an operator"; T5's own done-when — "report content asserted
  against a seeded diverged tail"; R9.1 records `RocksDbWalInspector` as the engine for
  exactly this.
- **As built:** `WriteReconciliationReport`
  ([ReplicationFeeder.cs:310-323](../../../src/Highway.Server/Storage/Rocks/ReplicationFeeder.cs))
  writes three sequence numbers and a sentence. No queues, no messages, no acks, no WAL
  inspection; no test seeds a diverged tail.
- **Close by:** walk the WAL from `lastReplicatedSeq` to `lastLocalSeq` with the
  inspector, group decoded ops by key family/queue, and write that. Test: partition,
  write N messages to the old primary, promote the replica, demote — assert the report
  names the queue and the N message ids.

---

## S3 — Claims that outrun the code (fix the documents if the code stays)

### G10. The T8 harness proves less than its names and the tasks claim

[ReplicationPairTests.cs](../../../tests/Highway.Integration.Tests/ReplicationPairTests.cs)
vs R8.1's list:

| R8.1 requires | Harness reality |
|---|---|
| Scripted **kill** of the primary | No process/server is ever killed — fence and a simulated `HELLO` stand in |
| Promotion **by priority** | Single replica; only the epoch increment is asserted |
| Old primary fences **on resurrection** | Demotion via in-process `HELLO` carrying a higher epoch; no restart (see G2) |
| Reconciliation report **contents** | Asserts the file exists and contains the boilerplate sentence |
| **Zero acked-and-replicated loss** | `…_ZeroLoss` never reads the message back on the new primary — nothing verifies survival |
| **Duplicates counted** | Not counted anywhere |
| Partition matrix: primary isolated / replica isolated / witness lost | Only primary-isolated; the "witness" test drives inbound pings from the test body, not the witness loop |

R8.2 (assurance rig against a failing-over pair, doorbells-off variant) was **not run** —
the RUNLOG entry says so itself. Either run it or strike R8.2 from the done-claim.

### G11. Constraint statuses and protocol text overstate

- **C9.2 "Met"** — not while G1–G3 stand.
- **C9.3 "Met"** — not for the raw wire surface (G7).
- **Protocol §HW.REPL.ACK** states "WAL files are eligible for deletion only past the
  minimum acked watermark" and "that replica re-bootstraps via `HW.REPL.SNAPSHOT`" —
  neither is enforced (G4). The protocol file is the contract; it must describe as-built
  or the code must catch up, in the same feature.
- `tasks.md` T5/T7/T8 done-notes should be amended to record the above (dated, not
  rewritten), per the house spec-discipline rule.

---

## Suggested order of attack

1. **G8** — unbreaks CI; mechanical.
2. **G1 + G2** — the two safety holes; both small in code, both need new tests
   (partition-with-witness; restart-after-promote).
3. **G4** — gap refusal + forced re-bootstrap; this is the difference between a demo and
   RD3.
4. **G5, G6, G7** — failover actually working end-to-end from a client's seat.
5. **G3, G9** — either implement or narrow the spec claim with a dated amendment.
6. **G10/G11** — after the code settles, re-run the harness additions and the assurance
   rig, then correct the done-notes and constraint statuses to match reality.

Until (at minimum) G8, G1, G2 and the G11 status corrections land, 042 should not be
recorded as complete.
