# Storage Engine

> **Status:** current
> **Guarantees:** durability, retention and size bounds are tracked line by line in
> [`constraints.md`](../product/constraints.md) (C4, C23, C24). This doc explains how the engine
> works and why; it never restates those guarantees.

*Highway's storage-engine choice was driven by two capabilities — log-structured-merge (LSM) space
reclamation and write-ahead-log (WAL) durability — and those requirements are what selected RocksDB.
Since 3.0 Highway reaches it through SystemD.Crystal, a storage kernel built from Highway's own box
layout, which also carries replication. Behind Highway's `IHighwayStore` abstraction it is a
log-structured engine whose footprint tracks the live working set, durable by default, with
per-structure byte budgets and no unbounded growth.*

## Overview

Every durable thing Highway holds — queued work, RPC requests in flight, pub/sub group backlogs,
the registry, replication state — lives in one embedded **log-structured** database reached through the
`IHighwayStore` interface. Durability is the default: `new HighwayServerBuilder().Build()` opens a
data directory beside the executable, and a restart recovers from it. Memory-only is opt-in by name
(`Ephemeral()`), and a data directory that cannot be written throws at `Build()` — naming the path
and both ways out — rather than degrading to memory silently.

Highway originally ran on Garnet's append-only log; the engine was replaced by the current
log-structured store, and the change fixed a structural problem rather than tuning one (below).

**The engine is SystemD.Crystal** (feature 076; NuGet `SystemD.Crystal`), a storage kernel over
RocksDB 11.8.1: named boxes inside databases, atomic batches across boxes, synced commits, bounded
scans, range deletes, and replication with no failover policy of its own. Crystal owns the whole data
directory — it stamps it, locks it, and refuses any directory it did not stamp. A broker's store holds
three databases:

| Database | Replicated | Holds |
|---|---|---|
| `highway` | yes | every Highway box |
| `highway.cache` | no | the opt-in broker-local cache |
| `highway.node` | no | node-local state that must survive a restart (a scheduled rejoin) |

Highway box `n` is the Crystal box `b/{n:x8}` in `highway`; a Highway key `[box:4][rest]` is the key
`rest` inside it, so the key layout below is unchanged from RocksDB. A box is created the first time
its id is written. Every box uses Crystal's `DeleteHeavy` profile — the compact-on-deletion collector
and the memtable scan-flush trigger, the tuning Highway set on RocksDB itself before 3.0.

## How it works

**Writes are durable at commit.** A queue send, an `HW.ACK`, a claim — each is **one Crystal batch**
across every box the command touches, committed `Strict` (synced to the write-ahead log) before the
client sees success. That is what makes "a sent message survives until processed" true across a
restart. A command that stages nothing commits nothing, so an idle poll writes and syncs nothing.

**The footprint tracks the live set, not the history.** This is the reason the engine changed. On
the old append-only log, logical truncation moved a begin-pointer but never returned disk, and
retired segment files were never deleted — so total on-disk size grew linearly with everything the
broker had *ever* written (measured: 12k messages → 102 MB, 24k → 205 MB), and a restart replayed
all of it. The log-structured engine has no such log: consumed messages are deleted, deletes become **tombstones**,
and **compaction reclaims their space as the engine's ordinary background job**. A broker that
drains what it is sent reaches a bounded steady state; it does not carry a year of history into its
data directory. Sizing a data directory is therefore a function of in-flight and
retained-until-processed volume, not cumulative traffic.

**Deletion is logical until compaction.** A delete is invisible to reads immediately but occupies
disk until compaction merges the SST files holding it. So a point-in-time directory size is the live
set *plus* not-yet-compacted tombstones and overwritten versions — it fluctuates above the logical
size between compactions, but does not grow without bound.

**Every structure lives in its own box** (storage format 4, feature 065). A key is
`[box:4][space:1][rest]`. Each service, queue and subscriber group's queue is one box, allocated on
first use and found by name through the **catalog** in the system box (box 0); box ids are never
reused. Inside a box, separate sub-ranges hold waiting work (by sequence), claims (by sequence — the
claim token), a lease index (by lease start), deferred work (by due time), dead letters, job schedules
and their due-time index, the workers that have claimed, and the box's counters (head, next sequence,
depth, in flight, deferred, dead, bytes). So:

- a claim reads from the stored head, recovery and promotion seek their time indexes from a known
  floor, and an ack is a point lookup — nothing walks deleted history or a list of in-flight work;
- every count the broker reports is a counter, never a scan, and listing every queue reads only the
  catalog (before 3.0 the dashboard and metrics dumped the whole keyspace to find queue names);
- retiring a subscriber group is one range delete of its box, inside the same batch as the rest of
  the retirement, so it is atomic; the emptied Crystal box is dropped at the next start;
- time-ordered indexes use process-local floors that are lowered on every insert below them and
  raised only after a committed scan, so a clock that steps back (another node after a failover, an
  NTP correction) can never hide an entry.

Registry, membership, reply slots and idempotency state keep their family encoding inside the system
box. Expiring keys (reply slots, idempotency claims) are indexed by expiry and **deleted** when they
expire — before 3.0 they were filtered on read but stayed on disk forever.

**No in-place migration.** A 3.0 broker refuses a data directory an earlier engine wrote — 2.x
(format 3), or a 3.0 pre-release on RocksDB, recognised by its `highway.format` stamp or its RocksDB
files — rather than serving it as an empty, healthy-looking store, and names what it found. The
upgrade is a fresh start per node: drain with the version that wrote it, `highways --wipe-data`, start
3.0; a standby re-syncs from the upgraded primary. On a Crystal store `--wipe-data` resets it: the
databases go, the node's identity and epoch stay. A standby declares its storage format in every hello
and the primary answers with its own; either side refuses a mismatch, so a 2.x node and a 3.0 node
never replicate from each other in either direction.

**Tombstones are also a read cost, so they are bounded in time, not just in space** (features 061,
062). A range scan steps over every deleted key in its range until compaction removes it — in the
memtable as well as in SST files. Three engine settings keep that bounded on a lightly written broker,
whose memtable may otherwise sit unflushed for hours: every iterator carries an upper bound for its
key range; `memtable_op_scan_flush_trigger` flushes the memtable once a single read step skips 1 000
hidden entries; and the compact-on-deletion collector compacts any SST file that is delete-dense —
both now set by Crystal's `DeleteHeavy` profile. The hot paths also avoid creating churn and start
from a queue's known head (see [durable-queues](durable-queues.md)). `highways --compact` runs Crystal's
offline compaction of a stopped broker's store; nothing in the box layout needs it routinely.

**Everything that grows with traffic is bounded.** Each queue-like structure carries a byte budget
(`MaxQueueBytes`, default 1 GB), maintained by a running counter inside the same transaction that
pushes or pops, so the write path stays O(1). A full structure **refuses** the producer loudly
(`HW_QUEUE_FULL`, naming the queue) rather than dropping data — under Highway's durability contract
a queued message is one nobody has processed, so evicting the oldest would lose exactly what the
queue exists to protect. A test enumerates every key shape and requires each to name what bounds it,
so a new unbounded structure fails CI rather than shipping.

**The cache is a separate database.** When the opt-in broker-local cache is enabled it uses its
*own* Crystal database, `highway.cache`, created **not replicated** (or an in-memory store on an
ephemeral broker). Crystal replicates whole databases, so a cache write cannot reach a standby. See
[distributed-cache.md](distributed-cache.md).

## Wire surface

The storage engine has no client-facing commands of its own; it is the substrate under every `HW.*`
command. A primary's data reaches its standbys over Crystal's replication port; Highway's policy
(heartbeat, handshake, promotion, roster) is the `HW.REPL.*` family — see
[replication-and-failover.md](replication-and-failover.md) and the *Replication Commands* section of
[`docs/HIGHWAY-PROTOCOL.md`](../HIGHWAY-PROTOCOL.md).

## Guarantees & limits

- **Durable by default**, recovers on restart; `Ephemeral()` is the explicit, named opt-out.
- **Bounded footprint over time** — compaction reclaims consumed work; the data directory tracks
  the live set, not the write history (C4.6, C24).
- **Per-structure byte budgets** that refuse rather than drop (C4.2–C4.4). The budget bounds a
  *queue*, not the whole process — ten full queues is ten budgets (C4.7, a deliberate non-guarantee).
- **No time-based retention yet** — entries carry no timestamp, so 100-day retention (C4.1) awaits a
  breaking framing change; the byte budget is the limit that binds first in practice.
- **No encryption at rest** — SST files and the WAL are written in the clear; volume/filesystem
  encryption is the stated answer (C23), not a Highway-owned key-management story.
