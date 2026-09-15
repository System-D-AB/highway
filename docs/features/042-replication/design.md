# Feature 042 — Replication: Design

## Architecture

```
            PRIMARY (epoch E)                          REPLICA (priority p)
  ┌──────────────────────────────┐          ┌──────────────────────────────────┐
  │ commands → WritePath → WAL   │          │  ReplPuller (client of HW.REPL.*)│
  │   (sync-per-commit, 038 T0)  │          │    HELLO(id, watermark, epoch)   │
  │ ReplicationFeeder            │◄─pull────│    PULL(fromSeq, page)           │
  │   GetUpdatesSince(seq)       │──pages──►│  BatchApplier                    │
  │   slot table + cap (RD3)     │          │    apply bytes + watermark       │
  │ FencingMonitor (RD6b)        │          │    in ONE outer WriteBatch       │
  │   hears replica/witness or   │          │  PromotionMonitor (RD5/RD6)      │
  │   → read-only after T_fence  │          │    silent primary > T_promote →  │
  └──────────────────────────────┘          │    epoch=E+1, go writable        │
                                            └──────────────────────────────────┘
        WITNESS (optional, RD6c): answers "I see you" to both; holds no data.
```

Everything rides surfaces that already exist: the 040 RESP server gains the `HW.REPL.*`
family; the 038 store gains nothing (the feeder reads the WAL via the engine API beneath
the seam — replication is an **engine-level** concern, documented as the one sanctioned
consumer of RocksDB APIs outside `RocksDbStore`, co-located with it in `Storage/Rocks/`).

## The two-timeout rule (why no dual-writable, without votes)

- Primary: writable only while it heard replica-or-witness within `T_fence`; else
  read-only (`-NOTPRIMARY` to writers, still serving reads/stats).
- Replica: may promote only after `T_promote` of primary silence, where config
  validation enforces `T_promote > T_fence + margin` (margin covers clock *rate* skew
  and scheduling stalls — no absolute clock comparison exists anywhere).
- Therefore at wall-time t where the replica promotes, the primary has been silent to
  it ≥ `T_promote`; the primary, symmetric-silent ≥ `T_promote − ε > T_fence`, fenced
  itself before t. One writable node at a time, by construction. The harness proves the
  claim under induced partition (R8.3); the reasoning lives here so the constants stay
  honest when tuned (OD1).

States: `Primary(E)` ⇄ `Fenced(E)` (contact regained without higher epoch → writable
again) ; `Fenced(E)` → `Demoted` (saw E′>E — permanent until re-synced as replica) ;
`Replica` → `Promoting` → `Primary(E+1)`.

## Apply-side atomicity (the one subtle mechanism)

`GetUpdatesSince` yields WAL entries as `WriteBatch` byte payloads with sequences. The
replica must make *apply* and *watermark advance* one atomic act, or a crash between
them double-applies (visible on counters). Mechanism: wrap the shipped batch bytes and
a `Put(sys|repl|watermark, seq)` into **one outer WriteBatch** before `db.Write` —
RocksDB batches concatenate; **T2 verifies `RocksDbSharp` exposes what this needs
first** (append or construct-from-data), with the fallback being decode-and-restage
through the same builder (slower, same guarantee). Watermark lives beside the store's
other `sys` keys; a re-pulled page whose last sequence ≤ watermark is skipped whole.

## Protocol sketch (final shapes belong to the protocol doc, T1)

```
HW.REPL.HELLO   <replicaId> <lastAppliedSeq> <epoch>   → +OK <primaryEpoch> <minSeq>
HW.REPL.PULL    <fromSeq> <maxBytes>                   → array: <epoch> <page of (seq, batchBytes)> <nextSeq|nil>
HW.REPL.SNAPSHOT <cursor|BEGIN>                        → chunked checkpoint transfer, resumable
HW.REPL.ACK     <replicaId> <appliedSeq>               → +OK          (advances the slot)
PROMOTE / DEMOTE / REPL STATUS                          → admin surface (exact form: T5, with 036-style host verbs)
```

Pull is client-driven and stateless per house style — the replica owns its cursor; the
primary owns only slots. `-NOTPRIMARY <endpoint> <epoch>` is the single refusal shape
clients and replicas both understand.

## Reconciliation report (RD7 / R4.2)

On demotion, the tail `(lastReplicatedSeq, lastLocalSeq]` is decoded through the
existing envelope readers into a per-queue listing — message ids, ack/dead-letter
events, byte deltas — written as a dated file under the data directory and surfaced in
the log and `HW.STATS`. Operator tooling may replay it through normal client sends;
the broker never merges it.

## Testing strategy

| Layer | Proof |
|---|---|
| Feeder/slots | unit: retention floor = min watermark, cap drops slot with event; pages sized by `maxBytes` |
| Applier | apply+watermark atomicity (crash-inject between—must not double-apply), re-pull idempotence, epoch refusal |
| Snapshot | blank-node bootstrap over the wire; resume mid-transfer; measured duration recorded (R2.1) |
| Fencing/promotion | state-machine table tests with a fake clock; config validation rejects bad timeout triples |
| **Failover harness (RD10)** | two embedded nodes: kill → priority promotion → epoch → resurrection fences → reconciliation content → zero acked-replicated loss, duplicates counted |
| Partitions (R8.3) | scripted: isolate primary / isolate replica / lose witness — each lands in the designed state |
| Rig (R8.2) | I1–I5 with mid-turbulence promotion; doorbells-off variant; RUNLOG |

## Risks

| Risk | Mitigation |
|---|---|
| `RocksDbSharp` lacks batch-append for the outer-batch trick | T2 verifies first; decode-and-restage fallback keeps the guarantee at some CPU cost |
| WAL format couples replica to primary's RocksDB version | pin one version repo-wide (already true); version echoed in HELLO, mismatch refuses with a sentence |
| Deadman constants wrong in the field | OD1 revisited with measured lag; validation enforces the invariant; auto mode stays opt-in |
| Snapshot transfer stalls on large DLQs | resumable chunks; DLQ CF is bounded by design (038 layout) |
| Scope creep toward elections/sharding | Non-goals restated; any "just add a vote" proposal reopens RD6 explicitly or not at all |


---

## Addendum — 2026-08-29: adopt the RocksDbSharp replication toolkit (architect review)

*This addendum records a design change from an architecture review; the analysis above is
left intact per the spec-workflow rule (correct with dated addenda, never silent rewrite).
The headline: the pinned engine binding **already ships the WAL/checkpoint mechanism this
feature planned to hand-build**, so T1–T3 become thin adapters over it and the one unproven
primitive (T2v) is largely de-risked. All Highway **policy** above — the RESP `HW.REPL.*`
surface, slots+cap, epoch, deadman, reconciliation — is unchanged.*

### What the spike found (verified, not assumed)

Reflecting the pinned package `RocksDB 11.1.2.3412` (`lib/net10.0/RocksDbSharp.dll` — the
Curiosity/Warren Falk binding this repo already uses) confirms a managed `RocksDbSharp`
replication surface, **no P/Invoke required**:

| Type / member | Shape | Maps to |
|---|---|---|
| `ReplicationSource(RocksDb db)` | ctor | the feeder's engine handle |
| `IEnumerable<ReplicationBatch> GetWalUpdates(ulong seq)` | WAL tail from a sequence | `ReplicationFeeder` core (RD2) |
| `IEnumerable<PooledReplicationBatch> GetPooledWalUpdates(ulong seq)` | same, `ArrayPool`-rented `PooledData`+`Length` | the zero-alloc feed path |
| `ReplicationBatch { ulong SequenceNumber; byte[] Data }` | one page | **exactly** the `(seq, batchBytes)` shape the `HW.REPL.PULL` sketch already assumed |
| `ReplicationSession GetInitialState(string tempPath)` | a checkpoint captured under a temp dir | `HW.REPL.SNAPSHOT` source (RD4) |
| `ReplicationSession`: `List GetManifest()`, `ReplicationFile OpenFile(string)`, `Dispose()` | manifest of files, each openable | the snapshot **is a list of files, each a `Stream`** |
| `ReplicationFile { string FileName; ulong FileSize; Stream FileStream }` | one checkpoint file | what we chunk over the wire |
| `ReplicationFileInfo { FileName; long Size; string Hash }` + `ReplicationDelta`/`ReplicationDeltaPlan` | per-file signatures | cheaper re-sync (skip files a replica already has) — an OD, not v1 |
| `ReplicationConsumer(RocksDb db)`: `IngestBatch(ulong seq, ReadOnlySpan<byte> data)`, `IngestBatch(ReplicationBatch)`, `IngestFile(ReplicationFile, string destDbPath)` | apply | `BatchApplier` core (T2) |
| `Checkpoint(IntPtr handle).Save(string dir, ulong logSizeForFlush)` | raw checkpoint | lower-level fallback |
| `RocksDb`: `GetLatestSequenceNumber()`, `DisableFileDeletions()`, `EnableFileDeletions()`, `WalPath`, `Flush(FlushOptions)`, `GetLiveFilesMetadata(bool)` | managed | retention + watermark, **no P/Invoke** |
| `RocksDbWalInspector` | offline WAL record walk | reconciliation-report ally (RD7); requires WAL compression **off** |

This **closes the 037-reference worry** (`reference/README.md`: "RocksDbSharp does not expose
[WAL flush/recovery]; you will need the P/Invoke") for the replication surface specifically —
the sequence, WAL and file-deletion controls are all managed here.

### The change: toolkit is the engine mechanism, Highway keeps the policy

Replication remains an **engine-level concern** in `Storage/Rocks/` (as the design already
says). The change is that `ReplicationFeeder`, `BatchApplier`, and the snapshot source become
**thin adapters over the toolkit** rather than from-scratch `GetUpdatesSince`/checkpoint
plumbing:

- **Feeder** wraps `ReplicationSource.GetPooledWalUpdates(fromSeq)`, sizes a page by `maxBytes`,
  and hands `(SequenceNumber, Data)` to the `HW.REPL.PULL` writer. The paging, `maxBytes`, epoch
  stamping, and continuation-token style are Highway's; the WAL production is the toolkit's.
- **Applier** wraps `ReplicationConsumer.IngestBatch(seq, data)`. See the atomicity note below.
- **Snapshot** wraps `ReplicationSession`: `GetManifest()` → stream each `OpenFile(name).FileStream`
  over `HW.REPL.SNAPSHOT` in resumable chunks; the replica writes files then opens and tails from
  the checkpoint sequence. This is **not** the toolkit's file-copy path (`IngestFile` assumes
  filesystem/shared-FS access) — Highway streams bytes over RESP so a replica stays a pure RESP
  client with no shared-FS assumption. `ReplicationFileInfo.Hash` + `ReplicationDelta` are the
  path to skip-what-you-have re-sync, deferred (OD).

**What the toolkit does NOT provide, and Highway still owns in full:** the `HW.REPL.*` RESP
surface (RD2), per-replica retention slots with a hard cap (RD3), epoch fencing (RD7), priority
promotion + the two-timeout deadman + witness (RD5/RD6), the `-NOTPRIMARY` refusal, and the
reconciliation report. None of that changes.

### Apply-side atomicity (T2v, sharpened by the spike)

`IngestBatch(seq, data)` ingests a batch **preserving its sequence number** but exposes **no
single call to atomically append a watermark `Put`** to that same batch. So the design's
double-apply concern is real. The spike narrows T2v to a decision between two mechanisms, both
of which give the guarantee:

- **(a) Derive, don't store a separate intent.** After `IngestBatch`, the applied watermark
  *is* `GetLatestSequenceNumber()` — the batch carried its own sequence, and RocksDB advanced the
  DB's sequence atomically with the ingest. A separate `sys|repl|watermark` key becomes a cached
  read of that number, and a re-pulled page whose last seq ≤ `GetLatestSequenceNumber()` is
  skipped whole. **No second write, so no apply-vs-watermark gap to crash between** — this is the
  preferred mechanism if the spike's crash test confirms `GetLatestSequenceNumber()` survives an
  ungraceful kill exactly at the last ingested batch.
- **(b) Outer-WriteBatch restage** (the design's original): decode the shipped bytes and re-stage
  them plus the watermark `Put` into one `WriteBatch` we build, then `db.Write` once. Same
  guarantee, higher CPU, and it must re-derive the sequence rather than preserve the primary's —
  a subtle difference the crash test must check does not break skip-by-watermark.

T2v now: crash-inject between ingest and (a)'s watermark-read / (b)'s outer write, prove neither
double-applies (counter-visible), and **pick (a) unless the crash test shows the DB sequence is
not durable at that instant**. Record the choice here.

### Retention vs `DisableFileDeletions()` — resolving the tension (RD3)

The toolkit's guidance ("`DisableFileDeletions()` so compaction doesn't GC a WAL a follower
needs; always `EnableFileDeletions()` on shutdown") is a **blunt all-or-nothing** knob and is
**directly at odds with RD3's "a dead replica can never fill the disk."** A naive
`DisableFileDeletions()` lets a dead replica pin the WAL forever — the exact failure RD3 exists to
prevent. Resolution, to be implemented in T1/T4:

- Retention is driven by **`SetWalTtlSeconds` + `SetMaxTotalWalSize`** (bounded by construction —
  the hard cap), **not** by holding `DisableFileDeletions()` across normal operation.
- The slot table computes the retention floor as `min(acked watermark over live slots)`; a slot
  past the cap is **dropped with a named event** and its replica re-bootstraps via snapshot — so
  the WAL is never pinned below the cap regardless of a dead replica.
- `DisableFileDeletions()` is used only for the **narrow window of a snapshot capture**
  (`GetInitialState`), re-enabled immediately after — never as steady state. This keeps "the
  process owns the DB for its lifetime" from silently becoming "one dead replica owns the disk."

This is the single most bite-prone mechanism in the feature; T4's retention tests must include
the dead-replica-hits-cap case and assert the WAL is reclaimed.

### WAL compression must stay off

`RocksDbWalInspector` (the natural engine for RD7's reconciliation report and for out-of-band
audit) **cannot parse WAL records when WAL compression is on**. `RocksDbStore.Open` sets no WAL
compression today; this addendum makes that a **recorded coupling**, not an accident: leave WAL
compression off so the inspector remains usable, and if a future tuning pass wants
`SetWalCompression`, it reopens this decision explicitly. (Batch/SST compression is unaffected —
this is WAL-only.)

### The RPO hole, stated bluntly (feeds R9.1 / constraints)

RD8 is async-ack, and R6.2's "silent loss not" is true **only outside** the async window. Inside
it, a hard primary loss can lose an **acked** message. The honest, numbered statement for the
constraints register:

> An ack is durable on the primary the instant `HW.ACK`/enqueue commits (sync-per-commit, 038),
> and durable on replicas within the **measured replication lag window** (RD8, surfaced by R7).
> A primary loss **within that window** can lose an acked-but-not-yet-replicated message. This is
> the v1 RPO; "ack after replica applied" (OD2) closes it and is affordable at Highway's sizes but
> is not a v1 deliverable. Duplicates across failover are allowed and counted; loss is bounded by
> the window, never unbounded and never silent (the window is reported).

### Net effect on the plan

- **Risk down:** the "RocksDbSharp lacks batch-append" risk in the Risks table is downgraded —
  the toolkit ships the apply path; T2v decides between derive-watermark and restage, both proven
  in a half-day.
- **Code down:** T1–T3 shrink to adapters; the WAL-tail, checkpoint, and file-manifest code is
  the toolkit's, tested upstream (it powers the binding's `ReplicationTest` sample).
- **Policy unchanged:** every distinctive Highway decision (RESP surface, slots+cap, epoch,
  deadman, witness, reconciliation, client failover) stands exactly as designed above.
