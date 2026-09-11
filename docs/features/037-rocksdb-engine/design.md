# Feature 037 — The RocksDB Engine: Design

## Architecture Overview

**Everything above the dashed line is unchanged.** That is the design.

```
   Highway.Client  ──  SE.Redis (pinned NuGet)  ──▶  [ RESP over TCP, TLS ]
   worker loops, PendingCallRegistry,                       │
   DoorbellWatcher, BackstopSweeper                         │
   ── UNCHANGED ──                                          │
  ═══════════════════════════════════════════════════════════════════════════
                                                            ▼
                          ┌──────────────────────────────────────────────┐
                          │  Kestrel ConnectionHandler        (D3)       │
                          │  socket · pipelines · TLS · shutdown         │
                          ├──────────────────────────────────────────────┤
                          │  RESP codec                                  │
                          │   reader: arrays of bulk strings  (ours)     │
                          │   writer: RespWriteUtils.cs   (vendored, D4) │
                          ├──────────────────────────────────────────────┤
                          │  Command dispatch          ── transport seam │
                          ├──────────────────────────────────────────────┤
                          │  23 HW.* commands            ── PORTED       │
                          │  Prepare/Main  ──▶  one transactional method │
                          ├──────────────────────────────────────────────┤
                          │  IHighwayStore               ── storage seam │
                          │     ├── RocksDbStore                         │
                          │     └── InMemoryStore   (tests)              │
                          └──────────────────────────────────────────────┘
```

Three layers are new, one is ported, and the two that carry every delivery guarantee to the
application — the client and the protocol — are not touched at all.

---

## 1 · The storage seam

Derived from what the commands actually call today: **15 primitives at ~106 call sites**, in four
families. The seam covers those and nothing speculative.

| Family | Today (Garnet) | On ordered keys |
|---|---|---|
| KV | `GET` `SET` `SETEX` `DELETE` `Increment` | direct |
| List | `ListRightPush` `ListLeftPop` `ListLeftPush` `ListLength` | `q\|<queue>\|<seq>` with a monotonic seq; **pop is seek-first on the prefix** |
| Ordered set | `SortedSetAdd` `Remove` `Range` `Length` | `z\|<name>\|<big-endian score>\|<member>`; **range is a prefix iterate** |
| Membership set | `SetAdd` `SetRemove` | `s\|<name>\|<member>` → empty value; membership is a point get |

**The structural win, stated plainly:** every one of these stops being a single large mutable
value. A queue becomes a key range rather than a List object; a lease table becomes a key range
ordered by expiry rather than a SortedSet object. That removes the shared hot value the exclusive
locks existed to protect, and it is the same change that removes C4.6's growth problem, because
reclamation becomes compaction.

**Seq allocation.** List keys need a monotonic sequence per queue. It is allocated **inside the
same batch** as the write it belongs to, from a per-queue counter key — never from a clock and
never from a separate transaction, or two concurrent sends can collide.

---

## 2 · What gets deleted rather than ported

`HwQClaimCommand.PrepareCore` today declares an exclusive lock set of **seven keys plus one per
known node**, across two stores, and carries this comment:

> *"Read the worker list from the main-store mirror, never the object-store set: an object-store
> read here registers a watch that the exclusive locks below would then fail (004.1)."*

That is not queue logic. It is the toll for Garnet's transaction model, and all of it goes:

- the `Prepare`/`Main` split
- key pre-declaration via `AddKey`
- `StoreType.Main` vs `StoreType.Object`
- the **main-store mirrors that exist only so `Prepare` can read them** (`QueueNodeList`, `JobIndex`)
- `LockType.Exclusive` bookkeeping

A claim becomes: take the per-queue lock → read through a snapshot → build a `WriteBatch` → commit.
One method.

**The mirrors are the part to watch.** They are currently two sources of truth for the same fact,
kept in sync by hand. Collapsing them to one is the correct outcome — and it is also the most
likely place for a behavioural regression, so the port must confirm each mirror's reader gets the
same answer from the single remaining copy.

---

## 3 · The transaction model, and the determinism it buys

```csharp
// before — Garnet: declare keys, then run. The clock is read during the replayed procedure.
PrepareCore(api, ref input) { AddKey(...); AddKey(...); /* × 7 + nodes */ }
Main(api, ref input, ref output) {
    var leaseExpiry = DateTime.UtcNow.Ticks - _opts.Lease.Ticks;   // ← re-read on AOF replay
    ...
}

// after — RocksDB: read the clock first, commit effects once.
using var _ = _locks.Acquire(queue);
var now   = _clock.UtcNow;                    // read ONCE, outside the batch
var snap  = _store.Snapshot();
var batch = _store.NewBatch();
... // decide everything against snap + now
batch.Commit();                               // the WAL records the RESULT
```

Garnet's AOF logs the **invocation** and re-runs the procedure on recovery, so `DateTime.UtcNow`
inside `Main` is read again, at a different time, against different state. RocksDB's WAL logs the
**effect**. The clock is read once and the resulting value is what becomes durable.

**The bug class disappears rather than being fixed** — and R5.3 makes that a test rather than a
claim. It is also the same property stage 2's WAL shipping will need, which is why R5 is adopted
now even though replication is deferred (D7).

---

## 4 · The RESP server

### 4.1 Why the codec is small

Garnet's ~2 800 lines of codec implement a complete Redis server: RESP2 **and** RESP3, inline
commands, doubles, maps, sets, verbatim strings, big numbers, attributes, push types.

**Inbound from a Redis client is always an array of bulk strings.** Two types. Outbound needs the
five RESP2 types the `HW.*` replies already use. Roughly 15% of the surface, which is why D4 copies
the writer and writes the reader rather than inheriting 1 333 lines of unused paths that a future
reader would assume are supported.

### 4.2 The reader's only real difficulty

Partial frames at buffer boundaries. `SequenceReader<byte>` over the Kestrel pipe, with an
incomplete frame returning "need more" rather than throwing.

**R6.1 is the test that matters**: feed every message one byte at a time and assert the same parse.
That is the bug Garnet already found, and it is cheap to find deliberately.

### 4.3 The handshake

SE.Redis supports partial servers first-class through **`CommandMap`** — there are built-in presets
for non-Redis servers (`Twemproxy`, `SSDB`, `Envoy`). So the approach is to **declare the subset and
suppress the probing**, not to implement commands until the client stops complaining.

Expected subset — **OD3 verifies this against the pinned version rather than trusting the list**:
`PING`, `AUTH`, `ECHO`, `CLIENT SETNAME` (a no-op `+OK`), `SUBSCRIBE`/`UNSUBSCRIBE`, and `HW.*`.

Anything outside it returns an error naming the subset. **Never a plausible `+OK`** — a stub that
succeeds silently is how a client ends up depending on behaviour that does not exist.

### 4.4 Pub/sub

SE.Redis opens a **separate connection** for subscriptions. Needed: the subscribe/unsubscribe
confirmation arrays, push frames (`*3` / `message` / channel / payload), and RESP2's
subscribed-connection restricted mode.

**The relief is that this is the one part where being imperfect is explicitly correct.**
`DoorbellWatcher`: *"Doorbells are a pure latency optimization — correctness rides on the backstop
sweep."* A lossy fan-out to connected subscribers is not a compromise here; it is the specified
behaviour. R7.1 proves it by running the whole rig with doorbells off.

---

## 5 · Risks

| # | Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|---|
| 1 | **SE.Redis will not talk to our server** | low | **fatal to D1** | **Spike it first** (T1). A stub answering `PING` before anything else is built. If this fails, the protocol decision reopens on day one rather than in month two |
| 2 | The reader mishandles a frame split at a buffer boundary | **medium** | high | R6.1's byte-by-byte test, written before the reader |
| 3 | A command's behaviour changes subtly in the port | **medium** | **high** | The rig's I1–I5, plus the existing test suite, which must pass unmodified |
| 4 | Collapsing the main-store mirrors changes what a reader sees (§2) | medium | high | Each mirror's consumer gets an explicit before/after equivalence test |
| 5 | Key layout regretted after data exists | low | low | **Deliberately low**: a broker's data is drainable (C2.2 — undelivered work is near zero in a healthy system), so this is a maintenance window, not a migration. This is why D7 defers the epoch |
| 6 | RocksDB tuning is wrong under this write pattern | medium | medium | Defer tuning until OD1 exists. Default options first; a measured change second |
| 7 | Scope creep into replication | **medium** | high | R-non-goals, stated three times. The two rules adopted from that work (R4, R5) are adopted on **single-node** merit and are justified that way |

**Risk 1 is the one to run first**, because it is the only one that can invalidate the plan rather
than delay it.

---

## 6 · Gates

| Gate | Condition |
|---|---|
| **G0 — the protocol decision holds** | SE.Redis connects to a stub server and round-trips one `HW.*` command. If not, D1 reopens before any engine work |
| **G1 — the seam holds** | `Highway.Server` compiles against `IHighwayStore` with **no** RocksDB type on that interface (R3.2). If it does not, the seam is wrong and the port must not start |
| **G2 — parity** | The existing test suite passes with `Highway.Client` unmodified (R1.2) |
| **G3 — assurance** | I1–I5 green including turbulence, with the rig's applications unmodified (R8) |
| **G4 — the register is true** | C4.6 measured; C4.1, C4.7, C9, C19 each explicitly adopted or deferred with a reason (R9) |

---

## 7 · Deliberately not done

- **Replication.** Stage 2. The research document's Part VI holds the design; nothing here
  anticipates it beyond R4 and R5, which stand on their own.
- **The epoch and the primary-only mutation gate.** D7 — they serve replication only, and adding
  dead code with no test is worse than adding them later to a drainable store.
- **Any protocol improvement.** Batch claim, multi-queue claim, piggybacked ack and SSE were all
  evaluated (research Part III.6) and all belong to a protocol change this feature explicitly is
  not.
- **The distributed cache** (feature 026). Removed, not reimplemented.
- **Performance work.** Faster is expected as a by-product. Optimising before OD1 exists is
  optimising against an unknown.
