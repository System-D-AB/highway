# Storage Engine

> **Status:** current
> **Guarantees:** durability, retention and size bounds are tracked line by line in
> [`constraints.md`](../product/constraints.md) (C4, C23, C24). This doc explains how the engine
> works and why; it never restates those guarantees.

*Highway's storage-engine choice was driven by two capabilities — log-structured-merge (LSM) space
reclamation and write-ahead-log (WAL) durability — and those requirements are what selected RocksDB.
Behind Highway's `IHighwayStore` abstraction it is a log-structured engine whose footprint tracks the
live working set, durable by default, with per-structure byte budgets and no unbounded growth.*

## Overview

Every durable thing Highway holds — queued work, RPC requests in flight, pub/sub group backlogs,
the registry, replication state — lives in one embedded **log-structured** database reached through the
`IHighwayStore` interface. Durability is the default: `new HighwayServerBuilder().Build()` opens a
data directory beside the executable, and a restart recovers from it. Memory-only is opt-in by name
(`Ephemeral()`), and a data directory that cannot be written throws at `Build()` — naming the path
and both ways out — rather than degrading to memory silently.

Highway originally ran on Garnet's append-only log; the engine was replaced by the current
log-structured store, and the change fixed a structural problem rather than tuning one (below).

## How it works

**Writes are durable at commit.** A queue send, an `HW.ACK`, a claim — each commits to the engine's
**write-ahead log** with sync-per-commit durability before the client sees success. That is what
makes "a sent message survives until processed" true across a restart.

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

**Everything that grows with traffic is bounded.** Each queue-like structure carries a byte budget
(`MaxQueueBytes`, default 1 GB), maintained by a running counter inside the same transaction that
pushes or pops, so the write path stays O(1). A full structure **refuses** the producer loudly
(`HW_QUEUE_FULL`, naming the queue) rather than dropping data — under Highway's durability contract
a queued message is one nobody has processed, so evicting the oldest would lose exactly what the
queue exists to protect. A test enumerates every key shape and requires each to name what bounds it,
so a new unbounded structure fails CI rather than shipping.

**The cache is a separate database.** When the opt-in broker-local cache is enabled it uses its
*own* database at `dataDir/cache` (or an in-memory store on an ephemeral broker) — a physically
separate database, which is precisely why a cache write cannot enter the replicated WAL. See
[distributed-cache.md](distributed-cache.md).

## Wire surface

The storage engine has no client-facing commands of its own; it is the substrate under every `HW.*`
command. Replication ships the WAL to standbys via the `HW.REPL.*` family — see
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
