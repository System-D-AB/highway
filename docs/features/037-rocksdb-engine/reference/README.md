# Reference material for feature 037

This folder is **prior art, not Highway code.** It is copied from a sibling project
(`stow-rocksdb`) that built a MongoDB-like document store on RocksDB using the same
`RocksDbSharp` binding, the same ordered-key philosophy, and the same "one
transactional write path" discipline that feature 037 mandates.

**Why it is here.** The spec's T2.1–T2.4 say the seam and store must be *derived*
rather than designed forward, and warn that the RESP reader's buffer-boundary bug and
the seq-allocation race are "the bug Garnet already found." The stow project already
hit and solved the RocksDB-specific versions of every one of those. Rather than
rediscover them, the implementer should read these files first.

**It is reference, not a dependency.** Nothing in `src/` compiles against this folder.
Do not add a project reference to it. When a stow file is the right answer, port the
*idea* (and, where noted, the code) into `Highway.Server`, adapting the key vocabulary
from `(collectionCode, StowId)` to Highway's `(queue, seq)` / `(name, member)`.

---

## Two folders

- **`stow-engine/`** — the source files (Engine + Layout + Encoding). The code you port.
- **`stow-tech/`** — the engineering design docs that explain the physical layout and,
  crucially, the failure and replay behaviour the code alone does not show. **Read these
  first** — they are how stow documents *why* the keyspace is shaped the way it is and
  which traps it hit.

### The design docs (`stow-tech/`)

| Doc | What it explains | Why it matters to 037 |
|---|---|---|
| `keyspace.md` | The ordered keyspace: 4-byte prefix per namespace, prefix iterate, range scan, `SeekForPrev`, **`DeleteRange`**, id encoding | This *is* the answer to OD4 (Highway's key layout). Every Highway structure — queue, lease, delayed, DLQ, registry, group — is a prefix with an ordered suffix, exactly as stow scopes each collection by a `<coll:4>` prefix. `DeleteRange` is the primitive group-retirement and node-decommission want (see below) |
| `document-layout.md` | The `meta` record: a 1-byte **format version first**, flags, an 8-byte sequence that is the version *and* the CAS token *and* the replication position, a catalog-generation field | Highway entries are opaque bytes, but the *record-framing discipline* transfers: put a format-version byte first (037's `Envelope`/`StorageFormatException` already gate on this), and let one sequence number serve replay position and read-your-writes — which is what stage-2 replication needs |
| `storage-model.md` | The LSM lifecycle (WAL → memtable → L0 → compaction), single-writer directory lock, the hot/cold `meta`/`body` split, **compaction is where deleted bytes are reclaimed**, measured 89 MB flat vs Garnet's 1 658 MB at 1 M docs | **This is C4.6, settled.** Space reclamation is compaction; a `DeleteRange` on a drained queue physically reclaims at the next compaction. The single-writer lock is why an in-process striped lock is sufficient (no distributed lock needed on one node) |
| `transactions.md` | The Garnet `Prepare`/`Main`/`Finalize` model in full, watch-abort contract, **"there is no rollback,"** commit-ordering for fault-idempotence, and the AOF stored-procedure replay traps | This is the *before* picture — the model 037 is escaping — written by someone who measured every trap. The commit-ordering lesson (below) is the one thing here the seam draft does **not** yet capture, and it is load-bearing |
| `index-structures.md` | Order-preserving encoders per type, compound-key delimitation, unique-key layout (id in value = the constraint), skip-scan, `DeleteRange` to drop an index | The encoder table is the spec for Highway's score/member encoding. Highway has no secondary indexes, so the query-execution half does not transfer — but the *encoding* half is exactly the lease/delayed score encoding |

---

## What each `stow-engine/` file is, and how it maps to Highway

| Stow file | What it does | Maps to Highway 037 |
|---|---|---|
| `WritePath.cs` | The single transactional method: lock → snapshot → build one `WriteBatch` → `db.Write` → read sequence after commit | **T2.4** the one place a batch is committed. The template `HwQSend`→every command follows (T3.1). Note the pattern: values computed *before* the batch, written *into* it |
| `StripedLock.cs` | Per-key lock, hash-to-stripe, lock-ordered acquisition to avoid deadlock | The per-queue lock the claim path needs (`design.md` §3, `_locks.Acquire(queue)`). Re-key from `(collectionCode, id)` to the queue/lease name |
| `SequenceSource.cs` | `ExactSnapshotSequence` (read-at) vs `EngineSequenceAfterCommit` (safe lower bound). The **B1 defect** comment explains why a global sequence cannot be predicted under a per-object lock | **The seq-allocation warning in `design.md` §1** ("allocated inside the same batch … or two concurrent sends can collide"). Highway's per-queue seq counter must be allocated in-batch, exactly as this file argues |
| `CounterMergeOperator.cs` | An i64-add RocksDB merge operator: atomic increment/decrement with no read-modify-write race | Byte accounting (C4.7) and the per-queue seq counter. Replaces the Garnet `Increment` and the GET/SET byte-counter with a race-free primitive |
| `Layout/ColumnFamilies.cs` | CF names, fixed order, "never reorder, only append," `AssertOrder` on open | Highway's CF split (queue data / lease / registry / DLQ). The `AssertOrder` guard prevents the silent "wrote to the wrong family" bug |
| `Layout/DocKey.cs` | `<coll:4><encoded id>` — a keyspace partitioned by a 4-byte prefix, big-endian for prefix scans | Highway's `q\|<queue>\|<seq>` and `z\|<name>\|<score>\|<member>` key builders (`design.md` §1). Same shape, different vocabulary |
| `Layout/IndexKey.cs` | `<coll:4><idx:2><encoded value…><encoded id>` — the id suffix makes entries unique and id-ordered within a value prefix | The ordered-set member layout: score prefix then member suffix, so a range scan by score returns members in order |
| `Encoding/KeyWriter.cs` | stackalloc-first, pool-on-overflow key builder. The single allocation control point every encoder writes through | Every Highway key builder should write through one of these. Copy near-verbatim |
| `Encoding/Int64Encoder.cs` | **Order-preserving** signed-int encoding: sign-bit flipped, big-endian, so lexicographic byte order == numeric order | **The heart of the lease table and delayed set.** Encode expiry/score as an Int64 this way and a prefix range-scan yields "everything before now" in order. This is what replaces Garnet's `SortedSet` |
| `Encoding/StringEncoder.cs` | Order-preserving string encoding with `0x00→0x00 0xFF` escaping and `0x00 0x00` terminator, so compound keys are self-delimiting (`("ab","c") ≠ ("a","bc")`) | Encoding queue/channel/group names into compound keys without a delimiter collision. Membership-set keys (`s\|<name>\|<member>`) need this |
| `Engine/ReadPath.cs` | Snapshot-scoped reads and `ScanDocuments`: `iter.Seek(prefix)` → check `StartsWith(prefix)` → iterate | **The queue-pop and range-scan loop.** Every list-pop (seek-first-on-prefix) and ordered-set range in the seam is this pattern. Also shows snapshot lifetime discipline |
| `Engine/WalFlusher.cs` | P/Invokes `rocksdb_options_set_wal_recovery_mode(kTolerateCorruptedTailRecords)` and `rocksdb_flush_wal` | **The crash-recovery knob for T3.5** (ungraceful kill → drop torn tail record → replay). RocksDbSharp does not expose these; you will need the P/Invoke |
| `Engine/InstanceCheckpoint.cs` | Native hardlink snapshot of the whole DB in <10ms | Not needed for single-node 037, but it is exactly the primitive **stage-2 replication** (research Part VI) will use to seed a secondary. Kept for that reason only |

## The hazards stow already paid for — read these first

1. **The global-sequence trap (`SequenceSource.cs`, defect B1).** You cannot predict
   the post-commit sequence from `GetLatestSequenceNumber() + 1` under a per-object
   lock, because any *other* write advances it. Stow separates a per-document revision
   (needs only local uniqueness, guarded by the lock it already holds) from the engine
   sequence (global, read *after* commit). Highway's per-queue message seq is the
   "revision" case: allocate it from a per-queue counter **inside the same batch**, never
   from the engine sequence and never from a separate transaction.

2. **The clock-in-transaction trap.** Stow's `WritePath` reads every value it needs
   *before* opening the batch and writes the resulting absolute value *into* it. Feature
   037-R5.1 makes this a hard rule enforced by a test: no `DateTime.UtcNow` inside the
   batch. The inventory found the current Garnet code reads the clock in nearly every
   `Main` — that is the behaviour the port must invert.

3. **There is no rollback — order the commit so any prefix is repairable
   (`transactions.md` §4).** This is the lesson the code files do not show and the seam
   draft does not yet capture. A `WriteBatch` is atomic, so a *single* batch has no
   partial-commit problem — but a command that does work in stages (the sweep: drain a
   processing list, dead-letter some, requeue others, adjust the byte counter) must still
   be ordered so that if it is interrupted and retried, no message is lost or double-counted.
   Stow's rule, measured against injected faults: **stale-entry cleanup first, then new
   entries, then membership, then the document, then the counter** — and the one that bit
   them, *"a naively incremented count is not fault-idempotent"* (a fault between the write
   and the increment leaves permanent drift). For Highway this means the byte counter and
   any length counter must be **derived or reconciled, not blindly incremented** across a
   multi-step operation. The single-batch commit removes most of this; the sweep and
   retirement paths are where it still applies.

4. **`DeleteRange` is the retirement primitive (`keyspace.md` §7).** When a queue, a
   subscriber group, or a decommissioned node's whole key range must go, it is one
   `DeleteRange` over `[<prefix><0x00…>, <prefix+1><0x00…>)` — measured at ~1 ms for
   50 000 keys — and the bytes reclaim at the next compaction. This is what
   `Decommission.RetireGroup` and node teardown become: not a drain-and-pop loop, but a
   range delete. The seam should expose it (a `DeleteRange(batch, prefix)` the current
   draft does not have yet) rather than force retirement to enumerate-then-delete. **This
   is also the mechanical answer to C4.6** — a drained queue's range is physically
   reclaimed, not merely logically truncated the way Garnet's `TruncateUntil` was.

## What does NOT carry over — the Layer A / Layer B line

Stow's "layout" is two layers, and only one is Highway's. Getting this wrong means
porting a query engine that nothing calls.

- **Layer A — the physical KV mechanics (ADOPT whole).** Prefix-partitioned ordered
  keyspace, order-preserving encoders (`KeyWriter`, `Int64Encoder`, `StringEncoder`),
  one `WriteBatch` per operation, snapshot reads, the striped lock, the counter merge
  operator, `DeleteRange`, WAL recovery mode. Semantics-agnostic. This is what the
  `stow-engine/` files and the `keyspace.md` / `storage-model.md` docs are, and it is
  what Highway's `src/Highway.Server/Storage/Layout/` realizes.

- **Layer B — the document-store semantics (DROP entirely).** Collections and collection
  codes, the catalog (`Catalog/`, `DatabaseRecord`, `CollectionRecord`), the `meta`/`body`
  split and `MetaRecord`, secondary indexes and the `index`/`uniq` families
  (`IndexKey`/`UniqKey`), unique constraints, CAS-by-version, the `Query/` layer,
  `IndexSelector`, `DocumentProjector`. **All of this exists to answer content queries** —
  "find documents whose fields match." Highway issues no content query in any of its 23
  commands, so none of it has a consumer.

**There is no collection → document model under Highway.** The unit is not a document with
an incidental id; it is an **opaque entry at an ordered position**, and the position (a
list seq, an ordered-set score, a set member) is the whole point. `document-layout.md`,
`index-structures.md`, and the collection/catalog language throughout the `stow-tech/`
docs describe Layer B — read them to understand the *physical technique* (prefix scans,
order-preserving encoding, self-delimiting compound keys), then leave the document
semantics behind. The one habit worth keeping from `MetaRecord` is the leading
format-version byte, which Highway's `Envelope` already has.

The full mapping — every Highway structure to its adopted key layout — is
[`../physical-layout.md`](../physical-layout.md). Read it before T2.3 so nobody reaches
for a collection code out of habit.
