# Distributed Cache

> **Status:** current (opt-in; off by default)
> **Protocol:** the cache is served on the stock `GET`/`SET`/`DEL`/`SETEX`/`TTL`… surface over the
> `hw:cache:*` key family — **no new `HW.*` command**; see the changelog and *Key Schema* in
> [`docs/HIGHWAY-PROTOCOL.md`](../HIGHWAY-PROTOCOL.md). Guarantees are C10 in
> [`constraints.md`](../product/constraints.md). This doc explains how the cache works and why.

*Highway ships an opt-in `IDistributedCache` (and `HybridCache` L2) built into the broker, so an
application gets a distributed cache on the same connection it already uses — no Redis alongside.
It is broker-local and never replicated, which is exactly what makes it safe.*

## Overview

Turn it on (`server.cache.enabled`, off by default) and the broker answers the standard cache verbs
against an `hw:cache:*` key family, wired into `IDistributedCache`/`HybridCache` on the client. A
broker with it off carries no cache surface, store or behaviour delta — byte-identical to a broker
without the feature. This is not the return of the old Garnet-era cache: that one existed only
because Garnet was natively a cache store, and it was removed with Garnet. This one is a deliberate,
narrowly-scoped add-on with a genuinely different guarantee.

## How it works

**A separate, never-replicated store.** Cache entries live in their *own* RocksDB database at
`dataDir/cache` (or an in-memory store on an ephemeral broker) — physically distinct from the
replicated dataset. Because RocksDB has one WAL per database, a cache write *cannot* enter the WAL
that replication ships; the cache is provably invisible to standbys. This is verified by a type test
(cache store and replication feeder share no reference) and a behaviour test (a cache write is
absent from the replicated DB's update stream), not merely asserted.

**Cold after a failover, on purpose.** Since the cache doesn't replicate, a new master starts with
an empty one; and any **epoch change wipes** the cache wholesale, because a mastership move means
another node may have mutated the system of record, so every cached value is suspect. A master that
keeps its herd through a peer-only partition does not change epoch and does not wipe. The accepted
cost is a **cold-cache burst** — every key misses at once at the instant the herd lands on a new
master — which is strictly safer than serving stale data across a mastership change.

**TTL- and size-bounded.** A set with no caller TTL gets the broker's `defaultTtl` (24 h); a caller
TTL is clamped to `maxTtl` (7 days) — no entry is immortal. A background sweeper drops lapsed
entries, and when the store exceeds `maxSizeBytes` it is **cleared wholesale** with a named event
(v1 has no per-key LRU — a cleared cache is one round of misses, not data loss).

**Best-effort, and a miss is never loss.** The cache rides the shared herd connection, so an op
issued mid-failover re-drives to the new master and simply misses there; a read on a non-master is a
miss, a write on a non-master is refused `-NOTPRIMARY` like any write. In every case the caller does
what a cache miss always means — one trip to the system of record, then re-cache. The cache is an
optimisation over the durable, replicated dataset, never a substitute for it.

## Wire surface

The cache reuses the stock RESP key-value verbs (`GET`/`SET`/`DEL`/`UNLINK`/`SETEX`/`PSETEX`/
`TTL`/`PTTL`) routed onto the `hw:cache:*` family — deliberately *no* new `HW.*` command. The exact
routing, TTL handling and non-master behaviour are in
[`docs/HIGHWAY-PROTOCOL.md`](../HIGHWAY-PROTOCOL.md) (protocol 4.9 changelog and Key Schema).

## Guarantees & limits

- **Opt-in:** off by default; zero footprint when off (C10).
- **Broker-local, never replicated** (C10.1) — structurally impossible to replicate, by separate
  database.
- **Cold after failover / epoch change** (C10.2) — an empty or wiped cache is the safe state; the
  cost is a repopulation burst, not stale reads.
- **TTL-bounded per entry, size-bounded store** (C10.3) — never immortal, never unbounded; over the
  size cap it clears wholesale (no per-key LRU in v1).
- **Best-effort** (C10.4): a miss — including mid-failover — costs one trip to the system of record;
  it is never data loss.
