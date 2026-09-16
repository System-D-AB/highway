# Changelog

All notable changes to Highway are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and Highway uses
[semantic versioning](https://semver.org/spec/v2.0.0.html).

The wire protocol has its own authoritative history in
[`docs/HIGHWAY-PROTOCOL.md`](docs/HIGHWAY-PROTOCOL.md); this file records the product and
package changes and links there rather than restating command-level detail.

## [2.0.1] — 2026-09-16

### Fixed

- **The packaged broker now writes log files.** A broker installed as a Windows service produced no
  readable log: its console output is discarded by the Service Control Manager, and the shipped
  `logs/` folder was never written to. The host now logs through Serilog to **rolling daily files**
  in `logs/` (resolved from the executable location, so it is correct under the service whose working
  directory is `System32`), in addition to the console for interactive runs. Files are size- and
  retention-bounded. No configuration is required (feature 045).

## [2.0.0] — 2026-09-16

The broker is now a purpose-built stack — a RocksDB storage engine behind a Highway-native RESP
server — with replication and client-herd failover and an opt-in broker-local cache. This is a
major release: the storage substrate changed, one command surface was removed, and the
distributed-cache package was removed and later reintroduced with a different guarantee.

### Added

- **Replication with client-herd failover** (features 042 + 042-1). Configure one or more
  standbys (`HighwayReplicationOptions`: start-as-replica, a primary connection string, a
  priority) and the cluster tolerates a single server failure without losing acked-and-replicated
  work. The model is **client-herd mastership** — the master is the node the client herd is on —
  with a deterministic roster-priority successor, a willingness stagger that prevents a split,
  epoch fencing, client-held in-flight replay (the caller re-drives the same request id), and a
  graceful `HW.REPL.GOODBYE` drain. **No elections and no quorum**, by design. Replicas serve no
  client writes; a non-primary refuses a write with `-NOTPRIMARY <endpoint> <epoch>` so the client
  follows the new master. RPO is the async-replication lag window, stated in
  [`constraints.md`](docs/product/constraints.md) C9, not papered over.
- **Broker-local cache** (feature 044). An opt-in (`server.cache.enabled`, off by default)
  `IDistributedCache` — and a `HybridCache` L2 with no Highway-specific code — backed by a
  **separate, never-replicated** store (its own RocksDB at `dataDir/cache`, or in memory on an
  ephemeral broker). It is cold after a failover and wiped on any epoch change: a cache miss is
  one more trip to the system of record, never data loss. TTL policy is server-side (default 24h,
  max 7d). Add it with `AddHighwayCache(...)` on the client. Details in
  [`constraints.md`](docs/product/constraints.md) C10.
- A formal-verification plan for the failover protocol (feature 043 — a TLA+/TLC model of the
  herd/replication state machine). Spec only in this release; no model shipped yet.

### Changed

- **Storage engine: Garnet → RocksDB.** The broker now runs an `IHighwayStore` RocksDB engine
  (feature 038) behind a Kestrel-hosted, Highway-native RESP server (feature 040). The wire
  protocol (`HW.*`, RESP) and the client programming model are unchanged, so application code does
  not change.
- **Bounded storage growth** is now met: RocksDB compaction reclaims consumed messages
  ([`constraints.md`](docs/product/constraints.md) C4.6), which the Garnet append-only log
  structurally could not do.
- Protocol advanced to **4.9** (additive throughout) — see the
  [protocol changelog](docs/HIGHWAY-PROTOCOL.md#protocol-version--changelog).

### Removed

- **Garnet is no longer a dependency** (feature 041): its package and submodule are deleted. One
  RESP output-formatter source file remains vendored with its MIT header intact (see
  [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md)).
- The original **Garnet-era distributed cache** (feature 026) was removed with the engine that
  made it free. A cache returns in 2.0 as the broker-local cache above — a deliberately different
  guarantee, not a drop-in restore of the old one.

### Breaking changes / upgrade notes

- **On-disk data does not carry over.** The Garnet AOF format is gone; a 2.0 broker starts on a
  fresh RocksDB data directory. There is no in-place migration — drain queues before upgrading, or
  treat the cluster as new.
- **`Microsoft.Extensions.Caching` on the client:** the 1.x cache adapter was removed in 041 and a
  new one (`Highway.Client.Caching.HighwayCache`, via `AddHighwayCache`) ships in 044. The
  interface is still `IDistributedCache`, but it is now explicitly broker-local and non-replicated;
  do not rely on it surviving a failover.
- A broker started against pre-2.0 data refuses to start with a diagnostic rather than
  misinterpreting it.

## [1.0.0-preview.1]

The first public preview: the three verbs (durable queues, pub/sub, RPC), dead letters, delayed
delivery, `[Idempotent]`, recurring jobs, the flight recorder and dashboard, authentication and
TLS, and NuGet packaging — on the original Garnet-backed broker.

[2.0.1]: https://github.com/systemdab/highway/releases/tag/v2.0.1
[2.0.0]: https://github.com/systemdab/highway/releases/tag/v2.0.0
[1.0.0-preview.1]: https://github.com/systemdab/highway/releases/tag/v1.0.0-preview.1
