# Feature 042 — Replication: Tasks

> Starts only after 041 closes G2–G4 (single-node on RocksDB, proven). T2's
> verification runs first *within* the build because the apply-atomicity mechanism is
> the one unproven primitive.

```
T2v (toolkit surface + apply-atomicity) ──► T1 (feeder + protocol) ──► T2 (applier) ──► T3 (snapshot sync)
                                                             │                 │
                                                             ▼                 ▼
                                        T4 (slots + observability) ──► T5 (epoch + promote + fencing refusals)
                                                                            ├──► T6 (client failover)
                                                                            └──► T7 (deadman + witness)
                                                     T8 (harness + partitions + rig) ──► T9 (record)
```

### - [ ] T2v — Verify the toolkit surface + the apply-atomicity primitive *(first; half a day)*

**Fulfills:** design §apply-side, design addendum (2026-08-29)
Two verifications, both cheap, both before T1:
1. **Toolkit surface** — confirmed by the 2026-08-29 spike (reflected `RocksDB 11.1.2.3412`):
   `ReplicationSource.GetWalUpdates/GetPooledWalUpdates/GetInitialState`,
   `ReplicationConsumer.IngestBatch(seq, data)`, `ReplicationSession.GetManifest/OpenFile`,
   `RocksDb.GetLatestSequenceNumber/DisableFileDeletions/EnableFileDeletions/WalPath` are all
   **managed, no P/Invoke**. Re-assert this against the referenced version in a one-file probe so
   a package bump can't silently remove it.
2. **Apply atomicity** — decide between the two mechanisms the addendum names: **(a)** watermark =
   `GetLatestSequenceNumber()` after `IngestBatch` (no second write, preferred), or **(b)**
   outer-`WriteBatch` restage. Crash-inject between ingest and watermark; prove neither
   double-applies (counter-visible). Pick **(a)** unless the crash test shows the DB sequence is
   not durable at the last-ingested instant.
**Done when:** the probe green (surface present); a throwaway test double-applies *without* the
mechanism and cannot *with* it; the chosen mechanism (a/b) recorded in design.

### - [ ] T1 — `ReplicationFeeder` (adapter over `ReplicationSource`) + `HW.REPL.HELLO`/`PULL`/`ACK`

**Fulfills:** R1.1, R1.4, R3.1 (floor only)
`ReplicationFeeder` in `Storage/Rocks/` is a **thin adapter over
`ReplicationSource.GetPooledWalUpdates(fromSeq)`** (addendum 2026-08-29) — it does not
re-implement `GetUpdatesSince`. Highway owns only: page sizing by `maxBytes`, epoch stamping on
every reply, and the continuation-token `HW.REPL.PULL` shape (`ReplicationBatch.SequenceNumber`
+ `Data` is already the page shape). Configure retention with `SetWalTtlSeconds` +
`SetMaxTotalWalSize` — **not** a steady-state `DisableFileDeletions()` (addendum §retention).
Protocol doc updated **in this task**.
**Done when:** unit tests green; a raw SE.Redis client pulls pages off a live node;
protocol doc section reviewed against as-built.

### - [ ] T2 — `BatchApplier` (adapter over `ReplicationConsumer`) + watermark

**Fulfills:** R1.2, R1.3
`BatchApplier` wraps **`ReplicationConsumer.IngestBatch(seq, data)`** (addendum 2026-08-29).
Watermark by the mechanism T2v picked — preferably **derived** from `GetLatestSequenceNumber()`
rather than a second write; skip-by-watermark on re-pull; epoch refusal.
**Done when:** crash-injection between apply and watermark cannot double-apply
(counter-visible); re-pull idempotence and epoch-refusal tests green.

### - [ ] T3 — `HW.REPL.SNAPSHOT` initial/re-sync (over `ReplicationSession`)

**Fulfills:** R2
Snapshot source is **`ReplicationSource.GetInitialState(tempPath)` → `ReplicationSession`**
(addendum 2026-08-29): `GetManifest()` then stream each `OpenFile(name).FileStream` over
`HW.REPL.SNAPSHOT` in resumable chunks. **Not** the toolkit's `IngestFile` file-copy path —
Highway streams bytes over RESP so the replica needs no shared filesystem. Wrap the capture in a
narrow `DisableFileDeletions()`/`EnableFileDeletions()` window only (addendum §retention). Blank-node
bootstrap → open → tail from the checkpoint sequence; the dropped-slot re-sync uses the identical
path. (`ReplicationFileInfo.Hash`/`ReplicationDelta` skip-what-you-have re-sync is an OD, deferred.)
**Done when:** wire-only bootstrap green incl. mid-transfer resume; duration measured
and recorded.

### - [ ] T4 — Slots, cap, observability

**Fulfills:** R3, R7
Slot table, retention floor = min watermark, hard cap with named drop event;
role/epoch/lag/slot-state in `HW.STATS` + dashboard.
**Done when:** retention tests green (incl. dead-replica cap); stats render on the
dashboard.

### - [ ] T5 — Epoch, `promote`, fencing refusals, reconciliation report

**Fulfills:** R4
Explicit promote (admin command + 036-style host verb); demote-on-higher-epoch;
`-NOTPRIMARY` refusal shape; the per-queue reconciliation report file + surfacing.
**Done when:** state-machine table tests green; report content asserted against a
seeded diverged tail.

### - [ ] T6 — Client failover

**Fulfills:** R6
Multi-endpoint config, `-NOTPRIMARY`/loss retry with bounded backoff into the existing
transient class; doorbell re-subscribe on the new primary.
**Done when:** client tests green against a promoted embedded pair; application-facing
semantics unchanged (existing client tests still pass).

### - [ ] T7 — The deadman + optional witness

**Fulfills:** R5
`FencingMonitor`/`PromotionMonitor` with fake-clock tests; config validation enforces
`T_promote > T_fence + margin` (refuses to start otherwise); witness process +
protocol (OD4 decided here); every transition logged with cause and timings.
**Done when:** state tests green; validation rejection tests green; witness presence
provably defers fencing on replica loss.

### - [ ] T8 — The failover harness, partitions, and the rig *(the gate)*

**Fulfills:** R8, RD10
Two embedded nodes in-process: scripted kill/partition matrix (primary isolated,
replica isolated, witness lost), promotion by priority, resurrection fencing,
reconciliation, **zero acked-and-replicated loss with duplicates counted** — in the
normal suite. Then the assurance rig against a failing-over pair (mid-turbulence
promotion; doorbells-off variant), recorded in `assurance/RUNLOG.md`.
**Done when:** all green in CI; RUNLOG entries present.

### - [ ] T9 — The record

**Fulfills:** R9
New constraints (RPO window with the measured figure, fencing availability trade,
no-elections position); O10 closed in the roadmap; `product.md` paragraph; OD1
defaults revisited against harness/lag data and pinned.
**Done when:** register reads true; roadmap and product doc updated; ODs either
closed or explicitly carried with owners.
