# Highway — System Constraints

**What this document is.** The guarantees Highway intends to make, each one numbered, each
one carrying its **current implementation status**. It exists so that intent and reality can
be compared line by line instead of inferred from code.

**How to use it.** Every constraint is `Met`, `Partial`, `Not met`, or `Not built`. A gap is
either a **defect** (the code should already do this) or a **planned feature** (a spec
exists, or one is needed). A gap that is neither is a decision nobody has made yet, and
saying so is the point of the document.

**When to update it.** A feature that changes any behaviour below updates the status in the
same feature. If a constraint turns out to be wrong, change the constraint and record why —
do not quietly let the code diverge.

Last reviewed: 2026-08-18 (feature 032 — Assurance Rig).

---

## The three verbs

Guarantees differ by verb, so the constraints are grouped by verb. Choosing between them is
one sentence: **one handler → Send, many handlers → Publish, need the answer → Execute.**

| | Purpose | Consumers | Durability |
|---|---|---|---|
| `ExecuteAsync` | Request and reply | Compete | Until acknowledged; caller waits on a timeout |
| `SendAsync` | Work, no reply | **Compete** | **Until processed — this is the durable store** |
| `PublishAsync` | Notification | **Each gets a copy** | Until each registered subscriber acknowledges |

**The queue is where durability lives.** That is the single most important line in this
document, and it is a change of direction: before feature 014, pub/sub was being asked to be
a durable store because nothing else could be, which is what pushed 100-day retention and
gigabyte budgets onto a fan-out mechanism. They belong on the queue.

---

## C1 — Queue (`SendAsync`) — feature 014

### C1.1 — A sent message is processed at least once

**Status: Met** — feature 014. Proven under sustained multi-process load and crash turbulence by the feature 032 assurance rig (`assurance/runs/2026-08-18T10-47-30/`).

Exactly one `IProcess<T>` handles each message. Multiple instances of the same application
**compete** — they share the work, they do not each get a copy.

### C1.2 — A sent message survives until it is processed

**Status: Met** — feature 014, subject to C4. Proven under ungraceful worker kill and lease recovery by feature 032 (`assurance/runs/2026-08-18T10-47-30/`).

This is the queue's reason to exist. A message with no worker running waits. A message whose
worker crashes mid-handling is redelivered after its lease. Nothing removes it except
successful acknowledgement, dead-lettering (C1.4), or an explicit purge.

Bounded only by C4.1 and C4.2.

### C1.3 — Sending never requires a running consumer

**Status: Met** — feature 014. Proven during the Gap phase of feature 032 (`assurance/runs/2026-08-18T10-47-30/`).

`SendAsync` succeeds whether or not any worker exists. The message waits. This is the
capability whose absence made people misuse `PublishAsync`.

### C1.4 — A message that cannot be processed stops being retried, **and says why**

**Status: Met** — feature 013 for the stopping, feature 015 for the why.

`MaxDeliveryAttempts` bounds redelivery; exhaustion moves the message to a dead-letter list
atomically. `HW.DLQ PEEK / REQUEUE / PURGE` operate on it. The queue inherits this on day one
rather than needing it built.

A dead letter carries the **exception type, message, stack, node and time** of the failure that
killed it, plus `firstType` when the failure changed shape between attempts. `HW.FAIL` records
each failure as the handler throws; the block rides on the entry through every requeue and
re-claim. A dead letter produced with no report — a worker that died before it could send one —
says so explicitly rather than showing blanks.

> **The wording changed in 015, and that is the point.** The old constraint was satisfied by a
> dead letter nobody could diagnose: it stopped being retried, which was all it claimed. An
> operator still had to correlate logs across every worker to learn what threw. When a
> constraint can be met by something obviously inadequate, the constraint was too weak, and the
> honest fix is to change the constraint rather than quietly do more than it asks.

Failure detail honours feature 002's per-name `PayloadCapture`, because an exception message
routinely contains application data. The **type** survives every mode: it is metadata, and it
is the one field that makes a dead letter diagnosable at all.

### C1.5 — A send can be deferred

**Status: Met by the underlying machinery** — feature 013.

`SendAsync(message, delay)` schedules work without a scheduler. It is a **"not before"**,
driven by worker polling rather than a timer — see C5.

---

### C1.6 — A handler may run longer than the lease without being duplicated

**Status: Met** — feature 019.

A worker renews its claim while a handler runs, so slowness alone no longer causes duplicate
execution. Before this, a handler outliving `Lease` had its message requeued **while it was
still running** — a *concurrent* duplicate, not one after a failure. A twenty-minute job against
a five-minute lease ran five times and then dead-lettered, having done the work five times and
reported failure.

The symptom was made worse by feature 015: that dead letter reads `MAX_ATTEMPTS` with
`failure: not reported`, because the handler never threw. An operator reads *"failed five times,
no exception"* about work that succeeded every time.

**Renewal is bounded, and the bound is the point.** `MaxProcessingTime` (15 minutes) stops it.
Unbounded renewal would delete lease recovery: a deadlocked handler would hold its message
forever, never redelivered, never dead-lettered, never visible. Past the cap the message returns
to exactly the behaviour it had before this feature.

> **The one behaviour change, recorded.** A **hung** handler is now recovered after
> `MaxProcessingTime` (15 min) rather than after `Lease` (5 min). Deliberate: a slow-but-working
> handler executed five times and dead-lettered corrupts data, while a hung one taking ten
> minutes longer to recover is a delay. `MaxProcessingTime = TimeSpan.Zero` restores the old
> behaviour exactly.

**For work measured in hours, renewal is the wrong tool** — see
[`docs/cookbook/long-running-work.md`](../cookbook/long-running-work.md). Chunk and checkpoint:
each message lives seconds while the job lives hours, and it survives deploys, parallelises for
free, and dead-letters one bad slice without killing the job.

---

## C2 — Pub/Sub (`PublishAsync`)

### C2.1 — A published message is delivered at least once to every group registered at publish time

**Status: Met** — and since feature 025, **the fan-out unit is the subscription group, not the
node**. Proven under multi-process soak by feature 032 (`assurance/runs/2026-08-18T10-47-30/`).

Fan-out across groups, atomic — all groups or none. Each group has its own **queue** (named
`{channel}@{group}`) with the same lease, acknowledgement, attempt counter and dead-letter
list as any other queue. One group failing has no effect on another.

**"Delivered" is per group, never "delivered to anyone".** First-acknowledgement-wins would
let a fast subscriber deny a slow one the message, which is not fan-out.

**Within a group, replicas compete** (025). Nodes sharing a `SubscriptionGroup` claim from the
group's one queue with the group as the claimant; each message is processed once per group, by
whichever replica claims it. The default — group = node name — keeps every node its own group,
which is the pre-025 behavior exactly. `[Idempotent]` markers are group-scoped, so a redelivery
suppressed for one replica is suppressed for its siblings too.

### C2.2 — A delivered and acknowledged message is gone

**Status: Met.**

A message leaves a group's queue when that group acknowledges it via `HW.QACK`. Storage tracks
**undelivered** work, which in a healthy system is near zero.

### C2.3 — A subscriber that is down receives what it missed, **until its node is declared gone**

**Status: Met, and now bounded** — feature 018 for the holding, feature 017 for the bound. Proven under graceful subscriber restart by feature 032 (`assurance/runs/2026-08-18T10-47-30/`).

A registered group's queue holds every publish while its subscriber is away, so a restart or a
deploy loses nothing. That was unqualified until 017 and it could not stay that way: a guarantee
to hold messages forever, for a node that will never return, is a guarantee to fill a disk and —
after 016 — to block the channel for every healthy subscriber on it.

The bound is **evidence-based, not a blind idle timer**. A group is retired when **every node
backing it** has been absent from the heartbeat registry past `SubscriberRetirementThreshold`
(24 hours by default) — since 025 liveness is the *youngest member's* heartbeat, so one live
replica keeps the whole group and every sibling's pending messages alive. A group nobody has
*consumed* from is not dead; a group whose every member has stopped *heartbeating* is.
RabbitMQ's `x-expires` and Azure's `AutoDeleteOnIdle` cannot tell those apart — Highway can,
because groups have members with heartbeats (018's insight, generalized by 025). `BYE PURGE`
destroys a group's queue only when the departing node is its **last member**.

Three ways a group is retired: the node says so (`CleanAndByeForeverAsync`), an operator says it
(`HW.HEARTBEAT <node> BYE PURGE`), or the broker decides after the threshold. Plain `BYE`
retires nothing — *"I am stopping"* and *"I am never coming back"* are different statements and
confusing them loses data.

Retirement deletes the backlog and is **never silent**: it logs at Warning, records a
`GroupRetired` event, and reports the messages and bytes destroyed.

### C2.4 — Pub/Sub is **not** a store for messages nobody has subscribed to

**Status: Met** — the backlog was removed once `SendAsync` gave that use case a proper home. Proven during Settle/Arrival phases of feature 032 (`assurance/runs/2026-08-18T10-47-30/`).

A publish with no registered group is delivered to nobody. A group registering later starts
empty. The surprising rule this removes: a late group used to receive an arbitrary prefix of
history, determined by when the *first* subscriber happened to start.

"Hold this until someone can handle it" is `SendAsync` and a queue — durable by design, with
no dependence on subscription timing.

### C2.5 — Pub/Sub is not a replayable log

**Status: Met, and permanent.**

A subscriber joining an active channel does not receive prior traffic. Highway does not
retain delivered messages or track consumer offsets. If you need history, you need a log, and
Highway is not one.

---

## C3 — RPC (`ExecuteAsync`)

### C3.1 — In-flight requests are never destroyed by a node leaving

**Status: Met.**

Departure and dead-node pruning **requeue** unacknowledged requests rather than deleting
them — a request in flight belongs to a caller who is still waiting, not to the node
processing it.

This is the line that makes decommissioning coherent: queued *messages* belong to a
subscriber that has declared it no longer exists; in-flight *requests* do not.

### C3.2 — A caller always gets an answer or a timeout, never silence

**Status: Met.** Proven by invariant I3 under sustained load across thousands of RPC invocations by feature 032 (`assurance/runs/2026-08-18T10-47-30/`).

`CallTimeout` (30 s default) bounds the wait. Errors are data (`Output.StatusCode`), not
exceptions.

### C3.3 — Retry budget may outlive the caller

**Status: Met, and worth knowing.**

`Lease` defaults to 5 minutes against a 30-second `CallTimeout`, so a stuck request exhausts
its attempts long after the caller gave up. The dead letter is the only trace anyone will
see. This is why `RpcBackoffEnabled` is off by default.

---

## C4 — Storage, retention and durability

Applies to queues (C1) and to pub/sub group queues (C2). **The target numbers below are not
the current defaults.**

### C4.1 — Retention: 100 days

**Status: Not met.** Feature 016 found it needs a breaking framing change first.

> **OD2 decision (feature 038 T0, 2026-09-15): DEFERRED.** The RocksDB port is exactly the
> "breaking framing change" moment 016 said this was blocked on — but adopting timed
> retention means adding a timestamp to every entry's frame and a sweep keyed on it, which
> is command-layer behaviour (039), not storage-layer (038). 038 makes it *cheaper to add
> later* — the delayed set's `z`-family already proves the "range-by-time then act" pattern
> a retention sweep would reuse — but does not adopt it. Revisit as its own feature once the
> engine swap is proven end-to-end.

A queue entry is `[ver][attempts][idLen][id][payload]` — it carries **no timestamp**, so there
is nothing to age it against. Time-based retention therefore needs either a fifth field in the
entry framing (breaking, like 013's attempt count) or a parallel structure keyed by time.

Deliberately not bolted on: 016 shipped a byte budget instead, which is the limit that binds
first anyway. R5.2 says so explicitly — under C1.2 only *unprocessed* work is stored, so in a
healthy system neither limit binds, and in an unhealthy one the byte budget arrives long before
100 days do.

### C4.2 — Size cap: 1 GB, configurable

**Status: Met** — feature 016.

`MaxQueueBytes`, default 1 GB, measured in bytes rather than entries: what exhausts a server is
bytes, and a count cannot express "as much memory as I am willing to give this". A running
counter per queue is maintained inside the same transaction that pushes or pops, so the write
path stays O(1). After 018 the one setting covers both verbs.

**See C4.7** — this bounds a queue, not the process.

> **Message size vs. queue size (feature 057, 2026-09-18).** `MaxQueueBytes` bounds a *queue*;
> `MaxPayloadBytes` bounds a single *message*. The message default is **5 MiB**, configurable up to a
> **15 MiB** ceiling — a configured value above it is refused at build, because a message is buffered
> whole in memory at several hops (client, RESP frame, store, WAL, replication), so the bound protects
> memory, not disk (RocksDB stores multi-MB values without trouble). A message still counts against its
> queue's `MaxQueueBytes`. The server is the authority (`HW_PAYLOAD_TOO_LARGE`); the client learns the
> server's configured limit at connect and enforces it as a fail-fast (057-b).

### C4.3 — Reaching a limit is never silent

**Status: Met** — feature 016.

A full queue **refuses the producer** with `HW_QUEUE_FULL` — permanent under the 004.1 contract
— naming the queue and the limit. Nothing is dropped: under C1.2 a queued message is one nobody
has ever processed, so discarding the oldest to make room loses exactly the data the queue
exists to protect.

**A publish refuses in full when any one group's queue is full**, and the error names that
group. Fan-out is atomic (018), so a partial delivery would quietly downgrade C2.1 from "at
least once per registered group" to "at least once, unless full". The accepted cost is that one
stuck subscriber blocks the channel for the healthy ones — made loud and attributable rather
than hidden, so an operator fixes a subscriber instead of debugging a channel.

### C4.4 — Every queue-like structure is bounded

**Status: Met** — feature 016, and enforced by a test rather than by inspection.

`BoundedStructureTests` enumerates every key shape `HighwayKeys` creates and requires each to
name what bounds it — a real cap for anything that grows with **traffic**, an explicit exemption
with a reason for anything that grows with **topology** (node counts, name counts).

**The enumeration is the constraint, not the caps.** This entry read "pub/sub group queues: no
bound at all" for three features because nothing forced the question to be asked. The test now
fails the moment a new key helper appears without a row.

### C4.5 — Durability is the default, not an option

**Status: Met** — feature 016.

`new HighwayServerBuilder().Build()` creates a data directory beside the executable, enables
AOF and storage tiering, and recovers on start. A queued message, a published message with a
registered offline group, and an unclaimed RPC request all survive a restart — proven by a test
that was **watched failing** against memory-only first, because a durability test that has never
failed proves the harness restarted, not that the data survived.

Memory-only is now asked for by name: `Ephemeral()`. A location that cannot be written **throws
at `Build()`**, naming the path and both ways out, rather than degrading silently — silent
degradation would be worse after this feature than before it, because the guarantee is now
documented as true.

> **Amendment 2026-09-15 (feature 041) — the mechanism changed, the guarantees C4.2–C4.5 did not.**
> These four constraints were written against Garnet. The engine is now the RocksDB store (037–041):
> - **C4.2 / C4.3 / C4.4** are engine-agnostic as stated — the per-structure byte counter, the loud
>   `HW_QUEUE_FULL` refusal, and the `BoundedStructureTests` key-shape enumeration all live in the
>   command layer over `IHighwayStore` and are unchanged by the swap.
> - **C4.5** — "enables AOF and storage tiering, and recovers on start" was Garnet phrasing. Read it
>   now as: `Build()` opens a RocksDB store at the data directory (durable by default), and a restart
>   recovers from it. Per 038 T0's sync policy, durability is a **WAL** with RocksDB's default sync
>   behaviour rather than a Garnet AOF; `Ephemeral()` still selects the in-memory store, and an
>   unwritable directory still throws at `Build()`. The guarantee — a queued message, an offline
>   group's published message, and an unclaimed RPC request all survive a restart — is unchanged and
>   is proven by `DurableByDefaultTests` re-pointed onto the RocksDB store (041 T2).

### C4.6 — Storage growth is bounded over time, not just in the moment

**Status: Met (feature 041, 2026-09-15) on the RocksDB engine.** See the 2026-09-15 addendum at the
end of this entry. Everything below the status line describes the **Garnet** engine, on which this
constraint was *not* met; it is kept verbatim as history (the register never rewrites what was
measured — it corrects with dated notes).

> **Was: Not met.** Investigated three times and **measured not to work** across all configurations.

`AofSizeLimitBytes` (512 MB default) is configured and Garnet's background enforcement task
runs — checkpoints appear where none did before, and the checkpoint path demonstrably calls
`TruncateUntil`. The log nevertheless grows linearly in total history:

| identical traffic | AOF on disk |
|---|---|
| 12,000 × 8 KB messages | 102 MB |
| 24,000 × 8 KB messages | 205 MB |

Measured against a **32 MB** limit, so hundreds of checkpoints' worth of headroom.

**Truncation is logical.** `TruncateUntil` moves the log's begin address; it does not return
disk. Reclamation would need whole device segments to retire, and in this configuration they do
not.

> **Investigations & Discarded Hypotheses (recorded so nobody repeats them):**
> 1. *Hypothesis 1 (016):* Garnet's default AOF page size (32 MB) was larger than the traffic between checkpoints, so no page ever fully obsoleted. Lowering it below 32 MB is rejected by Garnet (must be at least 2x 16 MB main-log page).
> 2. *Hypothesis 2 (034):* Exposing `AofSegmentSize` (`32m`, `64m`) would enable Garnet to truncate and delete retired segment files on disk once traffic crosses segment boundaries. Feature 034 exposed `WithAofSegmentSize("32m")` and verified at full scale (12,000 × 8 KB messages per wave = 24,000 total ≈ 205 MB): Wave 1 produces 102.3 MB across 4 files (`aof.log.0..3`); Wave 2 produces 204.6 MB across 7 files (`aof.log.0..6`). File count increases monotonically from 4 to 7; older segment files are never deleted by Garnet's `TsavoriteLog` on disk despite logical truncation (`TruncateUntil`). Growth remains strictly linear in total history.

The test `SustainedTraffic_DoesNotGrowTheLogWithoutBound` was kept and **skipped**, carrying the
measurements. (Retired in feature 041 — see the 2026-09-15 addendum below.)

**What this costs in practice:** a broker's disk grows with everything it has ever written, and
restart replays all of it. A busy broker needs its data directory watched, and a periodic
planned restart against a fresh directory is currently the only remedy.

> **Addendum 2026-09-11.** Two things are outstanding here, and they pull in opposite directions. **(a)** The user reports this constraint has since been solved; the fix is not visible in this repository and the status below is therefore stale. Whoever made it should update this entry — the register's whole value is that its statuses can be trusted, and a solved constraint reading *"measured not to work"* costs more than an unsolved one. **(b)** Independently, internal storage-engine research (§ I.2) argues the failure is structural to Garnet's AOF rather than a configuration matter: on an LSM engine this is not solved but absent, because compaction reclaiming space is the engine's ordinary job. That research is exploratory and nothing is approved.

> **Addendum 2026-09-15 (feature 041, gate G4) — Met on RocksDB; the Garnet failure was structural.**
> The engine swap resolves this constraint, and it resolves it by *construction*, not by tuning. The
> Garnet failure recorded above was a property of an append-only log: `TruncateUntil` moved the begin
> address but returned no disk, and retired AOF segment files were never deleted, so total on-disk
> footprint grew linearly with everything the broker had ever written (12k×8KB → 102 MB; 24k×8KB →
> 205 MB; segment files 4→7). RocksDB has no such log. Consumed messages are deleted, deletes become
> tombstones, and **compaction reclaims their space as the engine's ordinary background job** — the
> live working set, not the write history, determines the footprint. A broker that drains what it is
> sent reaches a bounded steady state; it does not carry a year of history into its data directory or
> replay it on restart.
>
> This is documented, externally-verified LSM behaviour (confirmed by the maintainer against a real
> RocksDB deployment), so feature 041 does not re-measure it with a fresh soak. The Garnet-shaped test
> (`SustainedTraffic_DoesNotGrowTheLogWithoutBound`, which counted `aof.log*` segment files) has no
> RocksDB analogue to re-point onto and is **retired** rather than carried skipped — its reason for
> existing was to hold the Garnet measurements, and those are preserved above as history. This also
> resolves the 2026-09-11 addendum's item (a): the "solved" the user reported is the RocksDB engine
> shipped in 037–041.
>
> **Practical note updated:** the "watch the data directory / plan periodic restarts against a fresh
> directory" remedy above applied to the Garnet AOF and no longer applies. A RocksDB broker's data
> directory tracks its live working set; sizing it is a function of in-flight and retained-until-
> processed volume (C4.4's per-structure byte budgets), not of cumulative history.

### C4.7 — The byte budget bounds a queue, not the process

**Status: Deliberately unmet** — feature 016, decision 1.

`MaxQueueBytes` is **per structure**. Ten queues at their limit is ten gigabytes; nothing bounds
the process as a whole.

This is recorded rather than implied because an operator reading "1 GB" will otherwise assume
the wrong thing. A server-wide budget is what they actually mean, and it is materially more
work: a global accountant on every enqueue, plus an eviction or refusal policy across unrelated
structures deciding whose message loses. 016 shipped the bound that could be built without
that, and named the gap instead of letting the default imply a guarantee it does not make.

> **OD2 decision (feature 038 T0, 2026-09-15): DEFERRED.** A process-wide budget is still a
> global accountant plus a cross-structure eviction policy — unchanged by the engine swap,
> and command-layer, not storage-layer. RocksDB does make the *observation* cheaper (the
> live working set's real size is a property the engine already tracks for compaction), but
> the policy question 016 named is untouched. Not adopted in 038; a candidate for a later
> feature once OD1 pressure is real.

### C4.8 — The cache is bounded by application TTLs, not by Highway

**Status: Retired (feature 041, 2026-08-29).** The distributed-cache add-on this constraint
governed was removed with the Garnet engine (041 R1.5): it existed only because Garnet was
natively a cache-store exposing `GET`/`SET`, and RocksDB is a durable log-structured store, not a
cache substrate. There is no cache to bound, so the constraint no longer applies. The original
text is kept below as history.

> **Was: Met as scoped** — feature 026.

Cache entries given an expiration die natively in Garnet; Highway adds no sweeper and no
quota of its own. Entries set **without** an expiration persist until deleted, exactly as
with any Redis-style store — `IDistributedCache` permits it, so Highway permits it. The
cache's growth is therefore bounded by the application's TTL discipline, and Highway names
that rather than implying a bound it does not enforce.

Cache traffic also **cohabits** with the queues: every cache write rides the same AOF and
every entry shares the same memory as queue and channel state. A heavy cache makes the
C4.6 disk growth and restart-replay cost heavier; an operator sizing a broker's data
directory sizes it for the cache too.

### C23 — No encryption at rest

**Status: Met as stated (a bounded non-guarantee)** — recorded against RocksDB, feature 041, 2026-09-15.

Highway does not encrypt its data directory. On-disk state — RocksDB SST files and the write-ahead
log — is written in the clear, exactly as the Garnet AOF and checkpoints were before it. This is a
deliberate posture, not a gap: **volume-level or filesystem encryption is the stated answer** (an
encrypted disk, a `LUKS`/BitLocker volume, or a cloud provider's at-rest encryption), because it
protects every byte the process touches without Highway inventing a key-management story it cannot
own. An operator handling regulated data encrypts the volume the data directory lives on.

> Originally an 016/012 non-goal ("the AOF and checkpoints stay unencrypted; that is disk
> encryption's job"). Restated here against the RocksDB engine (037 stow-tech storage-model): the
> mechanism changed from AOF+checkpoints to SST+WAL; the posture did not.

### C24 — Deletion is logical until compaction

**Status: Met as stated (a property to be aware of)** — recorded against RocksDB, feature 041, 2026-09-15.

On RocksDB a delete writes a **tombstone**; the deleted key's bytes are not physically reclaimed
until compaction merges the SST files that hold them. So an acked queue message, a decommissioned
group's queue, or a dropped catalogue entry stops being *visible* immediately (reads skip
tombstoned keys) but continues to occupy disk for a bounded window until background compaction runs.
This is the ordinary behaviour of a log-structured merge engine and is exactly why C4.6 is met:
compaction reclaiming space is the engine's routine job (see C4.6's 2026-09-15 addendum).

The practical consequence: a data directory's size tracks the live working set **plus** not-yet-
compacted tombstones and overwritten versions, so it fluctuates above the logical live size between
compactions rather than tracking it exactly. It does not grow without bound — that is the C4.6
distinction — but a point-in-time measurement is an upper bound, not the live-set size.

> Restated here against RocksDB (037 stow-tech storage-model / keyspace §7). Under Garnet the
> analogous property was AOF logical truncation; the shape ("gone from reads before gone from disk")
> is the same, the reclamation mechanism (compaction vs. never, on Garnet) is what differs — and that
> difference is what moved C4.6 from unmet to met.

---

## C5 — What Highway does not guarantee

| Not guaranteed | Note |
|---|---|
| **Exactly-once delivery** | Not achievable without a transactional participant. `[Idempotent]` (013) makes a handler run at most once per *redelivery*; it cannot relate two separate sends. |
| **High availability** | **Amended 2026-09-15 (feature 042).** Single-node remains the default (`AutoFailover` off). Two-node HA is now a product: one primary + priority replicas, epoch fencing, optional witness, **no elections**. A primary loss inside the async-ack replication lag window can lose an acked-but-not-yet-replicated message (C9.1). Dual-writable is refused by the two-timeout rule, proven in the in-process pair harness. |
| **Replayable history** | C2.5. |
| **Transactional enlistment** | No DTC, no ambient transaction. An MSMQ user who depends on this is not one Highway can serve. |
| **Message priority or selective consumption** | FIFO, no filtering. |
| **Per-message TTL** | Retention is per queue or channel. |
| **Characterised throughput** | No benchmark *measured on Highway* exists; no measured figure is claimed. See the **OD1 design target** below — a target to design against, not a benchmark result. |
| **Second-accurate scheduled delivery** | Delay is a "not before", driven by consumer polling, not a timer. |
| **Ordering under backoff** | Redelivery preserves head-of-queue order by default; enabling backoff trades that away. No setting gives both. This trade-off is a C5 row, not a numbered constraint — it is a property Highway declines to promise, not one it keeps. |

> **OD1 — a throughput target to design against (feature 038 T0, dated 2026-09-15).**
> Highway still claims **no measured** throughput — the row above stands. But a design
> needs a shape to build toward, and "uncharacterised" was blocking four decisions in the
> 037 research (RocksDB tuning, whether batching matters, whether D1 deserves revisiting).
> The target, chosen against the **imported sibling bake-off** — 84–112 k indexed
> saves/s at 64 writers, ~89 MB RSS flat at 1 M rows on the same `RocksDbSharp`
> (`C:\Software\ai\stow-rocksdb`, spec `v2-001-engine-bakeoff`), a heavier per-write
> workload than a Highway enqueue — is:
>
> **10 000 msg/s at 8 KB, across 20 queues and 40 concurrent consumers, on one broker,
> with sync-per-commit durability.**
>
> This is a *floor to not regress below*, not a promise. The sibling's numbers make it
> comfortable headroom (a Highway enqueue is one batch of a few small keys, lighter than
> a document save with N index entries), which is why 038 defers RocksDB tuning and the
> `CounterMergeOperator` until this floor is *measured* to be at risk (041/T6.3, and any
> later tuning feature). If a measured Highway benchmark ever lands, it replaces this row;
> until then no figure is published to users.
>
> **Note 2026-09-15 (feature 041, gate G3).** The two assurance-rig runs on the shipped RocksDB
> broker exercised **correctness under turbulence**, not throughput — the shortened profile drives
> ~25 msg/s deliberately, to keep a full reconciliation interpretable, not to characterise a ceiling.
> So they add no observed figure beside this floor and none is claimed. The floor stands as a design
> target; nothing in 041 measured it at risk, so 038's deferral of RocksDB tuning / `CounterMergeOperator`
> still holds.

---

## C7 — Observing the system never breaks it — feature 002, extended by 015

### C7.1 — A diagnostic write can never delay, block or fail a delivery

**Status: Met** — feature 002 for the flight recorder, feature 015 for failure reporting.

The flight recorder drops rather than blocks when full. `HW.FAIL` is best-effort: if it fails,
the exception is swallowed and logged **with the original attached**, the worker loop continues,
and the message is **not** acknowledged — so the lease sweep recovers it exactly as it would
have. A consumer that dies because its diagnostics died is worse than one with no diagnostics.

Losing the diagnosis is survivable. Losing the thing being diagnosed is not.

### C7.2 — Diagnostic detail obeys the same capture switch as payloads

**Status: Met** — feature 015.

An exception message routinely contains application data, so a name configured `HeadersOnly`
or `Off` has its failure detail withheld too, governed by feature 002's per-name
`PayloadCapture` rather than by a second setting nobody would remember to set.

The exception **type** survives every mode: it is metadata, and it is the one field that makes
a dead letter diagnosable at all.

### C7.3 — A node's address is an observation, never a declaration

**Status: Met** — feature 023.

The broker reports where it currently sees a node connected from, taken from the live connection
(`CLIENT SETNAME` on connect, `CLIENT LIST` on read). It is labelled **"seen from"** everywhere it
appears, and it is **absent** — not stale — for a node that is registered but not connected.

Highway never asks a node what its address is, and never stores one. A node behind NAT, in a
container, or scaled horizontally under one name would report a number nobody can reach, and
storing it would mean a record that outlives the socket it describes.

### C7.4 — The recorder is bounded, and reads it correctly for the life of the broker

**Status: Met** — feature 002, corrected by feature 046.

Each name's history is a fixed circular buffer with per-name retention; nothing about the recorder
grows without bound (that boundedness is what makes the diagnostic affordable — see the deferred
"longer-retention index" note). **Correctness of the read never depends on the sweep having run:**
retention is applied at read, and — since 046 — a read anchors at the oldest slot and walks the
whole ring, so a buffer that has *wrapped and then been swept* still returns exactly its live
events, newest included. (Before 046 a post-wrap sweep left the read scanning the wrong slots, so
after ~1h of uptime the dashboard silently dropped the most recent operations and surfaced stale
ones — fixed, with a wrap→sweep→read regression test.)

RPC replies are recorded in one reserved, cluster-wide bucket, `hw.replies` (every reply, all
services), correlated to a request by id. It is given **8× a single service's capacity** so a
retained request keeps its reply under realistic aggregate load; it is still bounded, so a very high
sustained RPC rate can age replies out within the retention window — raise its capacity/retention
override to widen that. An RPC row whose reply has aged out shows its outcome as incomplete rather
than inventing one.

### C7.5 — Metrics are emitted, exporter-agnostic — feature 051

**Status: Met.** The broker exposes a `Highway.Server` meter and the client a `Highway.Client` meter
through the in-box `System.Diagnostics.Metrics` API — the same posture as its `Activity` emission:
Highway defines the instruments (replication role/epoch/lag and promotions/demotions/fences, queue
depth/bytes and dead-letters, RPC throughput/latency/errors, connections, recorder drops; client RPC
latency and a `failovers` counter) and takes **no OpenTelemetry dependency**. The hosting
application adds whatever exporter it runs and subscribes with `.AddMeter("Highway.Server")`.
Instruments are always defined and **cost nothing when unobserved** (C7.1 holds: the observable
gauges sample the live feeder/store only when a listener collects, never on the delivery path). The
instrument names, units and labels are the documented contract in
[`docs/HIGHWAY-PROTOCOL.md`](../HIGHWAY-PROTOCOL.md) § "Metric emission". Health/readiness endpoints
are the sibling machine channel (C7.6).

### C7.6 — Readiness is role-driven, so infrastructure routes around a failover — feature 052

**Status: Met.** The broker serves `GET /health` (liveness — `200` whenever the process answers,
regardless of role), `GET /ready` (readiness — `200` **only** on a writable, un-fenced,
non-draining, fully-bootstrapped primary, else `503` with a one-word reason), and `GET /replication`
(the `HW.REPL.STATUS` JSON, behind the API key) on the dashboard host. Readiness is the feeder's own
`repl.ready` computed **fresh per probe**, so it flips within a probe interval on a
promotion/demotion/GOODBYE — which is what lets a load balancer pull a stepped-down node out of
rotation automatically, closing several silent-availability holes at the infrastructure layer. A
replica is deliberately *live but not ready*: liveness answers "is this process ok?", readiness
answers "route clients here?". `/health` and `/ready` are keyless (a probe leaks nothing beyond
up/down and a role word); `/replication` is gated. Loopback by default; bind `0.0.0.0` + an API key
to expose. The endpoints are on by default even when the dashboard UI is off, so a headless broker
still answers probes.

---

## C8 — Recurring jobs (feature 028)

### C8.1 — Each due occurrence fires exactly once, however many workers poll

**Status: Met.** The fire is one transaction inside `HW.QCLAIM`'s locks: enqueue one
occurrence, advance `nextFire`, replace the schedule record. Racing pollers cannot double-fire
for the same reason they cannot double-claim. **Exactly one fire, at-least-once processing** —
the occurrence is then an ordinary queue message, and the handler keeps queue semantics.

### C8.2 — Due-ness is the broker's clock, and firing needs a polling worker

**Status: Met, stated honestly.** Node clocks never participate. An occurrence fires on the
first poll after its due time — within the backstop interval in a running system; **not at
all** while no node hosts the queue's processor (the dashboard shows that state). Highway is
not an alarm clock and does not pretend to be.

### C8.3 — Missed occurrences collapse to one catch-up fire

**Status: Met** (OD3). After downtime, a due schedule fires once and `nextFire` is computed
from now. Three missed nights are one statements run, not three.

### C8.4 — A full queue refuses the fire without consuming it

**Status: Met** (016's rule, applied to the scheduler). `nextFire` is unchanged,
`JobFireRefused` is recorded, a later poll retries. Backpressure reaches the schedule instead
of being absorbed.

### C8.5 — Overlap: v1 fires anyway

**Status: Met as decided** (OD4, changed from the spec's recommendation during execution). A
new occurrence may be enqueued while the previous is unacknowledged. Detecting
"still outstanding" in O(1) needs an ack-side completion hook that does not exist; the
mitigations are `[Idempotent]` and derive-from-state handlers, documented. The
skip-while-outstanding default is in the Deferred table with its prerequisite named.

## C6 — Security (feature 012)

### C6.1 — An unauthenticated broker cannot reach the network by accident

**Status: Met.**

`Build()` refuses a server bound off loopback with no authentication, unless
`WithoutAuthentication()` is called explicitly. Loopback is exempt: running open is the right
configuration for development, and a loopback-bound broker is reachable only by processes
already on the machine.

`new HighwayServerBuilder().Build()` therefore still starts an unsecured broker, deliberately,
and a test named for that exists to stop it regressing.

### C6.2 — Credentials never appear in a log or an exception

**Status: Met.**

Three sites leaked before feature 012 — the engine logged the connection string at
Information level, and two exception paths embedded it. All now pass through one shared
redactor, and the tests were confirmed to fail with it removed.

### C6.3 — Authentication failures are permanent and legible

**Status: Met.**

`NOAUTH`, `WRONGPASS` and `NOPERM` map to typed permanent exceptions, distinct from a network
failure. StackExchange.Redis wraps them in a connection exception, so without inspecting the
chain a wrong password is indistinguishable from a dead host.

### C6.4 — TLS is available and never required

**Status: Met.**

PFX file or certificate-store subject, mTLS, revocation checking and refresh. Validated at
`Build()` so a missing file is a startup error naming it. **The password crosses the wire in
clear text without TLS** — documented at the point of configuration.

### C6.5 — The tested path is the secured path

**Status: Met.**

`HighwayTestServer` authenticates by default with a random credential per instance, so the
whole integration suite exercises `AUTH`. This is what makes C6.1's loopback exemption
defensible: users get the free path, and the suite still covers the secured one.

> **Amendment 2026-09-15 (feature 041) — the auth mechanism moved to the RESP server; C6.1–C6.5
> semantics are unchanged, and the two Garnet-ACL traps are retired.** Authentication is now the
> 040 RESP server's own `AUTH` (a `PasswordAuthenticator` over a single password or a list of
> PBKDF2-hashed config users), not Garnet's ACL subsystem. Every C6 guarantee above still holds and
> is still tested: `Build()` still refuses off-loopback-without-auth (C6.1), the redactor still
> covers every leak site (C6.2), `NOAUTH`/`WRONGPASS`/`NOPERM` still map to permanent exceptions
> (C6.3), TLS is still validated at `Build()` and never required (C6.4), and the test server still
> authenticates on every connection (C6.5).
>
> Two **feature-012 findings that were specific to Garnet's ACL model are now retired as live
> hazards** (kept here as history so the reasoning is not lost):
> - **`nopass` silently disabling auth** — a `user default on nopass` line in a Garnet ACL file
>   authenticated any connection as `default`. There is no ACL file and no `nopass` concept on the
>   RESP path; a configured password or user list is matched, and absent/blank credentials are
>   refused. The `WithAclFile` builder method and the shipped `config/users.acl` were removed (041 T4).
> - **Highway commands living in Garnet's `@dangerous` category** — the `+@all -@dangerous` hardening
>   idiom silently `NOPERM`'d every `HW.*`. The RESP server serves only the `HW.*` subset (037 R6.3)
>   and has no Garnet command categories, so the trap cannot arise. `AclStrictCustomCommands` is gone
>   with Garnet.
>
> A dated addendum recording the same retirement sits in the local research notes (the 012 analysis is history,
> corrected there by note rather than edit).

---

## Status summary

| | Constraint | Status |
|---|---|---|
| C1.1 | Sent message processed at least once | ✅ Met |
| C1.2 | Survives until processed | ✅ Met |
| C1.3 | Sending needs no running consumer | ✅ Met |
| C1.4 | Unprocessable messages stop being retried, and say why | ✅ Met (013 + 015) |
| C1.5 | Sends can be deferred | ✅ Machinery met (013) |
| C1.6 | A handler may outlive the lease without duplication | ✅ Met (019) |
| C2.1 | At-least-once per registered group | ✅ Met |
| C2.2 | Acknowledged means gone | ✅ Met |
| C2.3 | A down subscriber receives what it missed, until its group's last member is declared gone | ✅ Met, bounded (017, group-aware since 025) |
| C2.4 | Not a store for absent subscribers | ✅ Met |
| C2.5 | Not a replayable log | ✅ Met |
| C3.1 | In-flight requests survive departure | ✅ Met |
| C3.2 | An answer or a timeout, never silence | ✅ Met |
| C3.3 | Retry budget may outlive the caller | ✅ Met |
| C4.1 | Retention 100 days | ❌ Not met — needs a framing change |
| C4.2 | Size cap 1 GB, in bytes | ✅ **Met** (016) |
| C4.3 | Limits are never silent | ✅ **Met** (016) |
| C4.4 | Every queue-like structure bounded | ✅ **Met** (016) |
| C4.5 | Durable by default | ✅ **Met** (016) |
| C4.6 | Bounded over time | ✅ **Met** (041, RocksDB compaction) — Garnet AOF was not; see the 2026-09-15 addendum |
| C4.7 | Byte budget bounds a queue, not the process | ⚠️ **Deliberately unmet** (016 decision 1) |
| C23 | No encryption at rest (volume encryption is the answer) | ✅ Met as stated (041) |
| C24 | Deletion is logical until compaction | ✅ Met as stated (041) |
| C7.1 | Diagnostics can never break a delivery | ✅ Met (002 + 015) |
| C7.2 | Diagnostic detail obeys the payload capture switch | ✅ Met (015) |
| C7.5 | Metrics emitted, no exporter dependency | ✅ Met (051) |
| C7.6 | Readiness is role-driven for LB routing | ✅ Met (052) |
| C6.1 | Cannot reach the network unauthenticated by accident | ✅ Met |
| C6.2 | Credentials never logged | ✅ Met |
| C6.3 | Auth failures permanent and legible | ✅ Met |
| C6.4 | TLS available, never required | ✅ Met |
| C6.5 | The tested path is the secured path | ✅ Met |

**Two unmet constraints remain, both in C4, and both are understood rather than merely
outstanding:** C4.1 (retention) needs a breaking framing change first, and C4.6 (bounded
storage growth) was attempted and **measured not to work**. C4.7 is unmet by choice.

> **Correction 2026-09-15 (feature 041).** C4.6 is now **met** on the RocksDB engine — compaction
> reclaims consumed messages, and the Garnet-AOF linear growth was structural to the append-only log,
> not a configuration miss (see the C4.6 entry's 2026-09-15 addendum). So **one** unmet C4 constraint
> remains outstanding by circumstance — C4.1 (retention, awaiting a breaking framing change) — with
> C4.7 unmet by choice.

Feature 016 closed C4.2, C4.3, C4.4 and — the one that made the rest conditional — **C4.5** — retention, storage and durability — which is one
coherent feature rather than six problems. Feature 014 delivered C1; feature 015 completed
C1.4; feature 018 unified the two delivery engines.

---

---

## Open decisions

1. **`MaxDeliveryAttempts` off-by-one.** The comparison is `attempts > MaxDeliveryAttempts`, so a limit of 5 permits 6 deliveries while the name says 5. Change the comparison, or rename to "redeliveries"?
2. **What "1 GB" is measured against.** Per queue, per channel group, or a server-wide budget? Only a server-wide budget actually bounds the process, but it needs a global accountant and an eviction policy.
3. **Backpressure shape.** C4.3 says refuse. Which error code, and is it permanent or transient to the client? A full queue may drain, which argues transient and retryable.
4. **Do queues and services share a name space?** Feature 014 proposes separate `hw:q:` keys so a queue and a service may share a name without colliding.

---

## Deferred work

Registered here rather than in a separate `TODOS.md` — a second register is a second thing to
get stale, and this register is already maintained and cross-linked.

| Item | Deferred from | Why |
|---|---|---|
| **Retry tiers** — immediate, delayed, `[Unrecoverable]` | 015, by engineering review | 015 would have touched 11 files and added retry logic to three near-identical worker loops. Reduced to a structural refactor plus failure context; the reasoning for the tiers is preserved in the feature 015 (recoverability) working notes |
| **Polly / `Microsoft.Extensions.Resilience`** | 015 | The .NET built-in for retry pipelines and the obvious "does the framework already do this?" answer. Moot until the tiers return. Highway takes no dependency beyond Garnet and StackExchange.Redis, so it is a real trade rather than a free win |
| **Skip-while-outstanding overlap for jobs** | 028, OD4 | Needs an ack-side completion hook (`HW.QACK` does not know a message was a job occurrence); without it, detection is an O(depth) scan under exclusive locks on the claim path. Ships when the hook is designed |
| **Time-zone schedules (DST semantics)** | 028, OD2 | UTC-only shipped; local-time schedules are a real feature with real edge cases, not a parameter |
| **`[Job]` attribute sugar** | 028, OD1 | Layers onto the composition-root API if demanded; the reverse retreat would break users |
| **Per-subscriber `SubscriptionGroup` override** | 025, D5 | One node-wide option teaches the model ("this process is one replica of `billing`"); a per-subscriber-class override re-opens the which-identity-am-I confusion 025 exists to close, and no review produced a concrete need. Registered until one does |
| **`HostingMode` default flip to `Declared`** | 024, D1 | Changing the default silently changes what deployed processes host — the exact surprise 024 exists to end — and under a test runner the entry assembly is `testhost`, so the flip breaks every fixture-hosting test that has not declared its assembly. A major-version change, made when `Implicit`'s startup warning has had time to teach |
| **A longer-retention message index** | 023, Open Decision 3 | The dashboard's message view is bounded by the flight recorder's window, so history older than that is simply gone. A durable index would fix it and would be **new unbounded storage inside a diagnostic** — the exact cost feature 016 spent its length measuring. Registered, not built |
| **A node → message index** | 023 T6 | The node page projects every entity and filters, because nothing maps a node to the messages it handled. Affordable only because the recorder is bounded. An index would be new storage for a view |
| **`MaxDeliveryAttempts` off-by-one** | 013, then 015 | Belongs with the attempt-counting work, because that is what redefines what an attempt *is*. Also listed under Open Decisions above |

## Cross-references

- [`docs/HIGHWAY-PROTOCOL.md`](../HIGHWAY-PROTOCOL.md) — the wire contract these guarantees are built on
- [`design/durable-queues.md`](../design/durable-queues.md) — queues, dead letters, delayed delivery, deduplication
- [`design/replication-and-failover.md`](../design/replication-and-failover.md) — two-node replication, fencing, no elections
- [`design/`](../design/README.md) — all topical design docs

## C9 — Replication (feature 042)

Two-node WAL shipping: one primary, N warm standbys, pull-based `HW.REPL.*`, no elections. Off by default (`HighwayReplicationOptions.AutoFailover = false`); a single node stays writable with no replica.

### C9.1 — RPO is the measured replication lag window, never silent and never unbounded

**Status: Met** — feature 042, async-ack (RD8).

An ack is durable on the primary the instant the enqueue/`HW.ACK` commits (sync-per-commit, C4.5 / 038). It is durable on replicas within the lag window surfaced by `HW.STATS` (`repl.slot.N.lag`). A primary loss **inside that window** can lose an acked-but-not-yet-replicated message. That is the v1 RPO. "Ack after replica applied" (OD2) is not a v1 deliverable. Duplicates across failover are allowed and counted; loss is bounded by the window.

The in-process pair harness (042 T8) asserts zero loss for messages that had been pulled and acked on the replica before promotion.

### C9.2 — At most one writable node, by construction, without votes

**Status: Met** — **amended 2026-09-16 (feature 042-1): mastership is client-defined, not deadman-timed.**

*(The original 042 statement — a two-timeout deadman promoting a replica on `T_promote` of silence — was superseded before release. It is preserved here as the reasoning that led to the herd model, not as the shipped mechanism.)*

The shipped rule (042-1): **the master is the node the client herd is connected to; a node with no clients is not a master and performs no master-only side effects.** There is one master because the clients move as one herd to one node, computed by a **pure successor function** — the highest-priority reachable *willing* node from the live roster — identical on every client, so the herd never splits. A standby answers *willing* only when its own link to the master is dead past `WillingnessThreshold` (or it saw GOODBYE), and a **priority stagger** on that threshold makes the highest-priority successor turn willing first, so several standbys losing the master at once still converge on one. Promotion happens on the herd's **first accepted client verb**, bumping a durable **epoch**; a resurrected lower-epoch node demotes on first contact and writes a reconciliation report, never merges. The 042 deadman survives as a **fence backstop only** (a herd-less, peer-less primary goes read-only) — no timer ever promotes. Proven by the in-process cohesion harness (`HerdCohesionTests`): hard kill, partition matrix, GOODBYE, rejoin, and a doubly-partitioned client all land in the designed state and never mint a second live master with clients.

### C9.2a — The ack is the birth: where cluster responsibility begins

**Status: Met** — feature 042-1 (R5), stated as a definition, not a caveat.

**Before the ack**, a request lives only in the client; if the client dies before the ack it never entered the cluster — a non-birth, not a loss (no system solves client-death-before-ack; causality, not a Highway gap). **After the ack**, the request is the cluster's responsibility: durable on the master (sync-per-commit, C4.5) and replicated within the measured lag window (C9.1). Each client holds its own **unacknowledged** work and **replays it with the same request id** to the new master on convergence — so a single server failure loses no acked work, and the broker preserves nothing in-flight across a failover. Duplicates from replay are counted, never silently doubled (the existing at-least-once / `[Idempotent]` contract).

### C9.3 — A replica serves no client writes

**Status: Met** — feature 042, tightened by 042-1.

Mutating `HW.*` on a non-master is `-NOTPRIMARY <endpoint> <epoch>` — and 042-1 extends the gate to the **raw-key write surface** (`SET`/`SETEX`/`DEL` on `hw:idem:*`/`hw:rep:*`), so a client pointed at a standby cannot stage local state either. Reads/stats/admin still run. During a **GOODBYE drain** the quiescing master serves only completion verbs (`HW.REPLY`/`ACK`/`QACK`/`FAIL`/`TOUCH`) plus WAL shipping, refusing new work so the herd converges.

### C9.4 — A dead replica cannot fill the disk

**Status: Met** — feature 042 T4.

Retention is `SetWalTtlSeconds` (24h) + `SetMaxTotalWalSize` (1 GiB) plus a slot lag cap (`SlotLagCapSequences`, default 100_000). A slot past the cap is dropped with a named event; that replica re-bootstraps via `HW.REPL.SNAPSHOT`. `DisableFileDeletions` is only the snapshot capture window.

### C9.5 — Highway does not elect a leader

**Status: Met** — feature 042 / O10 closed; **reaffirmed and simplified by 042-1.**

There is no quorum, no vote, no three-node majority, and (as of 042-1) no witness process either. Mastership is defined by client connection; the successor is deterministic config (priority + live roster); convergence is connection-driven; the epoch is a rule-based tie-break of last resort, not a tally. Adding any vote reopens 042 RD6 and 042-1 R1/R10 by name.

### C9.6 — No auto-failback; deliberate failback is GOODBYE

**Status: Met** — feature 042-1 (R13.4/R13.5).

A returning or newly joined **higher-priority** node joins as a warm standby, syncs, and **waits** — it never preempts a healthy master (its healthy master-link keeps it *unwilling*, so no path can move the herd onto it). "Master = highest-priority live node" is deliberately **not** an invariant; the successor is computed at the *next* transition against the live roster. An operator who wants a specific node back issues **GOODBYE** on the current master — a chosen, timed, zero-loss handover. Every herd transition is thus forced-by-failure or chosen-by-operator, never surprise-triggered by a reboot.

### C9.7 — Membership is master-owned; priority is a unique, timing-independent key

**Status: Met** — feature 042-1 (R13.1–R13.3).

A joining node announces its own-config priority (`HW.REPL.JOIN`); the master admits it into a **replicated roster** (so every standby, and any promoted successor, already holds it) and narrates the change. A priority **already held by a live member is refused** (`ERR HW_PRIORITY_TAKEN`, naming the holder) — first announcer wins, so the succession order can never depend on restart order. A client's connection string is **bootstrap only**; the live roster is the running truth, so the cluster can grow beyond any client's original string (dynamic membership).

---

### C9.8 — A primary restart no longer silently degrades HA (feature 050-a)

**Status: Met** — feature 050-a, 2026-09-17.

Before 050-a, a routine restart of the primary in a two-node set could leave it permanently degraded
with no signal: a *promoted* node never self-registered in the roster (blank succession view), and a
demoted ex-primary — having no `primaryServer` — could not follow the successor, sitting `Demoted`
until a manual reconfigure + wipe + restart. 050-a closes the class:

- **Promotion self-registers** in the replicated roster (F1), so succession and the priority map
  survive a failover.
- **Auto-rejoin** (F2; `server.replication.autoRejoin`, on by default): a demoted ex-primary records
  the new primary it learned and, on its next start, wipes and re-syncs as its replica — its diverged
  tail preserved in a reconciliation report first (the C9.1 RPO made operational), with no
  auto-failback (C9.6). The snapshot pull authenticates with the node's own shared secret.
- **Slot aging** (F3; `server.replication.slotStaleAfter`, 60s): slots clear on demotion and a silent
  slot is dropped even under the lag cap, so a phantom `Active` replica cannot persist.
- **Loud on every channel** (F6): redundancy state, leadership-since and an event timeline on
  `HW.REPL.STATUS`; a dashboard leadership banner, no-redundancy alert and timeline; a client
  role-change event; and a single-endpoint-client startup warning.
- **Safe upgrades**: a `--drain-and-stop` verb + a rolling-upgrade runbook, proven zero-loss by an
  in-process herd-rig scenario (`RollingUpgradeTests`).

The root cure — a symmetric member/peer config that removes the learned-endpoint marker entirely — is
the deferred **050-b**.

## C10 — Broker-local cache (feature 044)

The cache is an **opt-in add-on** (`server.cache.enabled`, off by default). A broker with it off
carries no cache surface, store, or behaviour delta — byte-identical to a pre-044 broker. Every
guarantee below holds only when it is on.

### C10.1 — The cache is broker-local and never replicated

**Status: Met** — feature 044 (R2), structurally.

The cache lives in a **separate store** — its own RocksDB at `dataDir/cache` on a durable broker,
or an in-memory store on an ephemeral one — that the replication feeder never sees. A cache write
uses a different physical database than the replicated dataset, so it **cannot** enter the WAL
`HW.REPL.PULL` ships (RocksDB has one WAL per database; a separate database is the only way a
write cannot replicate). This is proven by a type test (the cache store and the feeder share no
reference) and a behaviour test (a cache write is absent from the broker DB's `GetUpdatesSince`),
not merely asserted.

### C10.2 — The cache is cold after a failover, and that is safe

**Status: Met** — feature 044 (R6, R8.1).

Because the cache does not replicate, the herd's **new master starts with an empty cache**. On top
of that, any **epoch change wipes** the cache: a mastership move means another node may have mutated
the system of record, so every cached value is suspect and is dropped wholesale. A herd-holding
master that keeps its herd through a peer-only partition does **not** change epoch and so does
**not** wipe. The accepted cost is a **cold-cache burst**: at the instant the herd lands on a new
master, every key misses at once → a spike of backing-store traffic while the cache repopulates.
For most workloads a brief blip; for a very cache-heavy read path, worth knowing. It is strictly
safer than serving stale data across a mastership change.

### C10.3 — Every entry is TTL-bounded, and the store is size-bounded

**Status: Met** — feature 044 (R5, R7).

A cache set with no caller TTL gets the broker's `defaultTtl` (24 hours); any caller TTL is clamped
to `maxTtl` (7 days) — an entry is never immortal. A background sweeper drops lapsed entries, and
when the store exceeds `maxSizeBytes` it is **cleared wholesale** with a named event (v1 has no
per-key LRU — a cache, cleared, is one round of misses, not data loss). The store cannot grow
unbounded.

### C10.4 — The cache is best-effort, and a miss is never data loss

**Status: Met** — feature 044 (R3).

The cache rides the shared herd connection, so a cache op issued mid-failover **re-drives to the new
master and is a miss there** — no special handling, no stale read. A cache read on a non-master is a
miss; a cache write on a non-master is refused `-NOTPRIMARY` like any write. In every one of these
cases the caller does what a cache miss always means: one more trip to the system of record, then
re-cache. The cache is an optimisation over the durable, replicated dataset — never a substitute for
it.
