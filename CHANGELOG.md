# Changelog

All notable changes to Highway are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and Highway uses
[semantic versioning](https://semver.org/spec/v2.0.0.html).

The wire protocol has its own authoritative history in
[`docs/HIGHWAY-PROTOCOL.md`](docs/HIGHWAY-PROTOCOL.md); this file records the product and
package changes and links there rather than restating command-level detail.

## [3.0.0] — Unreleased

A major release: a new storage layout, protocol 5.0, and a failover that no longer splits under
load. **The broker, the client packages and every data directory move together** — a 3.0 broker
refuses a 2.x data directory, a 2.x client cannot talk to it, and 2.x and 3.0 nodes never replicate
from each other. Upgrading is a drain and a fresh start per node: see runbook 7 in the
[server distribution guide](docs/distribution/README.md). The public client API is unchanged.

### Breaking

- **Storage format 4 — every queue in its own key range** (feature 065). Each service, queue and
  subscriber group's queue is a *box* found through a catalog; claims, leases, deferred work, dead
  letters, job schedules and counters each have their own sub-range. There is **no in-place
  migration**: a 3.0 broker refuses a format-3 directory, by its stamp and by recognising 2.x keys,
  and names the recovery. New `highways --wipe-data` empties a data directory for the fresh start
  while keeping the node's replication identity.
- **Protocol 5.0** (features 067, 068). One work path: `HW.CLAIM SVC|Q` returns claim tokens, and
  `HW.ACK` / `HW.TOUCH` / `HW.FAIL` take the token. `HW.QSEND` is `HW.SEND`. A node declares its
  whole session (catalog, subscriptions, jobs) with `HW.SESSION`. Reply slots and idempotency are
  reached only through `HW.RESULT` and `HW.IDEM`; stock `GET`/`SET`/`DEL` now serve the opt-in cache
  only. See the 5.0 row of [`docs/HIGHWAY-PROTOCOL.md`](docs/HIGHWAY-PROTOCOL.md).
- Replication declares the storage format in both directions (`HW.REPL.HELLO ... FORMAT 4`, and the
  primary's reply echoes its own). Snapshot bootstrap checks it too: `HW.REPL.SNAPSHOT BEGIN` takes
  `FORMAT 4` and its reply states the primary's format, so neither a 3.0 nor a 2.x replica copies a
  snapshot in the other's format. Found by rehearsing the upgrade runbook with real 2.5.6 and 3.0
  binaries: a blank 3.0 standby pointed at a 2.x primary used to copy its data, then refuse to start on
  it; now it copies nothing and waits for the primary to be upgraded.
- **The default `NodeName` includes the hosting name** (feature 072). A process running as a Windows
  service or in an IIS application pool now defaults to `{app}-{machine}-{service}` instead of
  `{app}-{machine}`, so several services from the same binaries on one machine are separate nodes. The
  default `SubscriptionGroup` keeps the old value, `{app}-{machine}`, so those copies still share one
  group and pub/sub behaves as before. An explicitly set `NodeName` or `SubscriptionGroup` is unchanged.
- **RPC requests have deadlines, and no dead letters** (feature 074). A request lives exactly as long as
  its caller waits and never runs after it; one that fails its last attempt is removed and recorded
  (`RpcFailedFinal`) instead of dead-lettered. `HW.DLQ`, `HW.CLAIM DEAD` and session `deadLetters` refuse
  `SVC` targets, an `IDeadLetter<T>` for an RPC request is a startup error, and `IHighwayDeadLetters`
  refuses service contracts. `HW.CLAIM SVC` rows gain a fourth element (the remaining time), `HW.CALL`
  takes `TIMEOUT`, `HW.REPLY` takes `TOKEN`, and a call to a service no node has ever served answers
  `HW_NO_WORKERS` (`503`). `IHighwayClient` gains `ExecuteAsync(request, timeout, ct)`.
- **Upgrading from 2.5.6:** its `server.rpcRequestTtl` and `server.rpcDuplicateWindow` are not in 3.0 —
  every call carries its own deadline, and the duplicate filter remembers a call until that deadline
  (`server.rpcDuplicateFilter`).

### Fixed

- **An outage left a backlog that ran for nobody, and a hung service needed a restart** (feature 074).
  Requests queued while workers were down ran in full when they came back, long after their callers had
  given up; a handler hung on a dependency held its worker until the process restarted; and every
  connection blip re-sent every pending call. Now: a request is never handed out, redelivered or answered
  after its caller's deadline; the handler's token fires at the deadline and a handler that ignores it is
  abandoned and its claim acknowledged, so the service keeps answering; and pending calls are re-sent only
  when the master actually moved, once, with the time left.
- **A retried call, send or publish could run twice** (feature 074, carried from 2.5.6 and 2.6.0). A
  request whose reply was lost was re-sent and stored again. The broker now remembers the ids it has
  stored — `HW.CALL` by request id, `HW.SEND` by message id, and `HW.PUBLISH` by a new `ID` the client
  sends once per publish and reuses across its retries — and answers a repeat (a call until its own deadline, a
  send or publish within a 10-minute window)
  without storing it. Ids are remembered only after their commit, so a refused one is never suppressed;
  a call's id is forgotten when its request leaves unanswered. Settings: `server.rpcDuplicateFilter` (calls are
  remembered until their own deadline),
  `server.sendDuplicateWindow`, `server.publishDuplicateWindow` (0 disables).
- **A hung handler was duplicated, and kept its worker slot** (feature 074). At `MaxProcessingTime`
  renewal stopped while the handler kept running, so another worker started the same message, and a
  handler that never returned held its slot until the process restarted. The handler is now cancelled
  at the cap and renewal continues until it stops; a handler that ignores the cancellation is abandoned
  after `AbandonGrace` (30 s), freeing its slot, and at most `MaxAbandonedHandlers` may be running
  abandoned before the worker stops claiming. RPC callers get `504 PROCESSING_TIME_EXCEEDED`.

- **A redeploy without a subscriber could block a whole channel** (feature 070). Subscriptions were
  add-only and the client never unsubscribes, so a node that dropped a subscriber but kept running stayed
  a member of that group; the group queue filled to its byte budget and, since a publish refuses when any
  target group is full, every publish to the channel failed. A node's session is now its complete
  subscription set: a group it no longer declares loses it, and a group whose last member left drains —
  publishes skip it, its backlog is kept, and the first publish after the retirement threshold retires it unless declared
  again. `HW.SESSION` also now takes the channel and group locks for membership changes, closing a race
  with publish-time retirement, and group retirements and drains are written to the broker log.
- **Two primaries after a failover under load** (feature 069). With two or more standbys, a client
  whose probe of the live successor was slow could walk on to a lower-priority standby, which promoted
  by its timer alone; both stayed primary at one epoch. A standby that has lost its master now asks its
  higher-priority peers first (`HW.REPL.HELLO PEER`) and defers to any that could take the herd, and
  two primaries at one epoch settle by priority (the lower one rejoins the other). The 3.0 client also
  fails over faster: it fails fast when it has other endpoints, compares endpoints by identity rather
  than spelling, probes a dead node once instead of three times, and treats a disposed connection as a
  lost one.
- **Subscriptions and jobs lost after a failover.** They were registered once at startup; a broker
  that had not seen that registration served the node without them. The client now re-declares its
  session whenever a broker asks (`+SESSION`) and after every failover it follows, and re-subscribes
  queue doorbells.
- **Expired reply slots and idempotency markers were never deleted** — only hidden from reads. They
  are now indexed by expiry and deleted.
- Races between publish and claim, fail and touch, and job update and job fire on one queue: every
  operation on a queue now holds that queue's lock.
- The dashboard and metrics no longer scan the whole keyspace to list queues.
- The dashboard showed a node whose name contains a space as "not connected": the client sanitises the
  connection name and the broker compared the raw one (072).
- A failing broker log sink could fail a command after its commit, through the registry log lines 070
  added; those writes are now guarded like every other diagnostic (C7.1, 072).
- `HW.SESSION` and `BYE PURGE` could fail after half-applying when concurrent sessions for the same node
  kept moving the plan; they now always complete, under every lock on the last attempt (logged) (070).
- A stale cached group-queue id could make `BYE PURGE` or a session retire the old queue again and leave
  the live one orphaned; membership changes now re-check the queue against the store under their locks.
  A publish racing a group's retirement no longer leaves an empty, member-less queue behind (070).

### Added

- **RPC deadlines** (feature 074). The timeout is the call's own (`ExecuteAsync(request, timeout)`), the
  contract's (`[Service(Timeout = "00:05:00")]`) or `CallTimeout`; inside a handler, calls inherit the time
  left. The broker fixes the deadline with its own clock; workers get the remaining time. A caller that
  gives up withdraws its queued request (`HW.CANCEL`). Broker options `defaultRpcTimeout` (30 s),
  `maxRpcTimeout` (1 h), `minRemainingToStart`, `rpcLeaseGrace`, `rpcFailFast`, `maxExpiredPerClaim`,
  `rpcLogInterval`. Events `RpcExpired`, `RpcFailedFinal`, `RpcReplyLate`, `RpcCancelled`, `RpcFailFast`;
  metrics `highway.rpc.expired`, `.failed`, `.replies.late`, `.failfast`, `.cancelled`, `.queue_wait` and
  client `highway.client.rpc.deadline_exceeded`, `.cancels`, `.replies.late`; `HW.STATS` service rows show
  the oldest waiting request against its deadline and how requests ended; the dashboard's service page
  shows the same. `[SideEffects]` marks a handler for a startup reminder when its request is not
  `[Idempotent]`; an `[Idempotent]` RPC keeps its answer for at least twice its timeout.

- **Duplicate counters** (feature 074): `HW.STATS` reports `callDuplicates`, `sendDuplicates` and
  `publishDuplicates`; the meter `highway.messages.duplicates{verb}`; client meters for cancelled and
  abandoned handlers. Options `AbandonGrace` and `MaxAbandonedHandlers`.

- **Dead-letter contracts & declarative retry policies** (feature 073).
  - Strongly-typed `IDeadLetter<T>` contract handlers to consume dead-letter feed notifications (`HW.CLAIM DEAD`) and resolve them atomically with `Keep()`, `Discard()`, or `Requeue(delay)`.
  - Poison payload resilience: deserialization failures dispatch to `IDeadLetter<T>` with `Message = null` and raw payload preserved.
  - Requeue lineage on queue entries (`[u16 requeues][u32 magic]`), visible to handlers and capped by `MaxRequeues`.
  - Dead letters are for queues and subscriber groups; RPC has none (074).
  - Typed operator API `IHighwayDeadLetters` (`PeekAsync`, `CountAsync`, `RequeueAsync`, `PurgeAsync`, `RequeueAllAsync`, `PurgeAllAsync`) for management across services, queues, and subscriber groups.
  - Declarative retry policies via `[Retry(MaxAttempts, Backoff, MaxRequeues)]` supporting fixed, exponential, and deterministic integer-only FNV-1a jitter rescheduling into delayed space before dead-lettering.
  - Server dashboard Dead Letters panel on entity details page with payload previews and API key authentication.
  - Observability: metrics `highway.deadletters.resolved`, `highway.deadletters.requeued`, gauge `highway.deadletter.feed.depth`, flight recorder events (`DeadLetterResolved`, `DeadLetterRequeueRefused`, `DeadLetterFeedOverflow`), and warning sweeper for unconsumed dead letters older than `DeadLetterAgeWarning` (default 24h).
- **The broker sees every process** (feature 072). Each client process names its connections with a
  random instance id (`CLIENT SETNAME <node>@<instance>`) and describes itself in `HW.SESSION` (host,
  pid, hosting name, start time, version). The broker counts the processes behind each node name, logs
  a Warning and records `NodeNameShared` when more than one live process uses a name — they would share
  one heartbeat and one set of claims — and the dashboard's Nodes view shows a process count, flags a
  shared name, and lists each node's processes. In memory only; nothing is stored or replicated.

### Improved

- A claim, ack, touch or fail costs the same with ten thousand messages in flight as with one: acks
  are point lookups by token, lease recovery reads only expired claims, and every count is a counter.
- A replica catching up from far behind reads the WAL once instead of once per page (300 000 batches:
  1.5 s → 0.2 s) and applies each page with one synced write (feature 066).
- RocksDB 11.8.1 (feature 064; data directories are compatible with 11.1.2).

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
