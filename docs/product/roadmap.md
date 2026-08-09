# Highway — Product Roadmap

## Build Order

Features are ordered by dependency — each builds on the one before it. You can't observe traffic that doesn't exist, and you can't send messages to a server that hasn't been built.

```
001 ─▶ 003 ─▶ 004 ─▶ 005 ─▶ 006 ─▶ 002
 │       │       │       │       │       │
 ▼       ▼       ▼       ▼       ▼       ▼
Skeleton Scan   Server  Wire   Registry Observe
```

**Design decision:** There is no local-only dispatch. Every call goes through the server — even when caller and handler are in the same process. This gives one code path, consistent behavior (timeouts, retry, observability), and the server as single source of truth. For testing, `HighwayTestServer` embeds the server in-process with zero external infrastructure.

---

## Feature List

### 001 — Project Skeleton ✅

**Status:** Complete

Foundation: .NET 10 solution with three packages (Abstractions, Client, Server), shared build config, Garnet as a git submodule, test projects, and .slnx solution file.

**Unlocks:** Everything else.

---

### 003 — Assembly Scanning & Service Catalog

**Status:** Complete

At startup, Highway.Client scans loaded assemblies to discover `AsyncService<T,TRes>` implementations and `ISubscribe<T>` subscribers. Builds an immutable catalog with pre-compiled dispatch delegates. Registers everything in DI with proper lifetimes.

**Unlocks:** Local-only dispatch (services can be discovered and called).

---

### 004 — Server HW.* Commands

**Status:** Complete (amended by 004.1)

Highway.Server registers custom `HW.*` commands in Garnet. This is the broker brain — it manages queues, subscriber groups, routing, and acknowledgment. All atomicity guarantees live here.

**Unlocks:** A running server that can accept commands.

**Key deliverables:**
- `HW.CALL` — enqueue RPC request
- `HW.REPLY` — send RPC response
- `HW.DEQUEUE` — pop next request for processing
- `HW.ACK` — acknowledge processing complete
- `HW.PUBLISH` — durable publish to all subscriber groups
- `HW.SUBSCRIBE` / `HW.UNSUBSCRIBE` — subscriber group management
- `HW.RECEIVE` — consume messages from subscriber group
- `HW.RACK` — acknowledge pub/sub message
- Server embeddable in-process for integration tests (`HighwayTestServer`)

---

### 004.1 — Server Remediation

**Status:** Complete

Amendment to 004, not a new capability: fixed the re-subscribe backlog-duplication defect, made validation errors classifiable (`ERR HW_*` permanent vs bare `ERR Transaction failed.` transient), hardened identifier validation against control characters, made `HighwayTestServer` fully configurable (including `Restart()` on a stable port for durability tests), added the missing durability/lease/doorbell/retention coverage, corrected the bind address gap, and synced 004's spec docs with the implementation. See `docs/features/004.1-server-remediation/`.

**Unlocks:** A server whose behavior is both correct and knowable — the foundation 005's retry policy and engine lifecycle are built on.

---

### 005 — Client-Server Communication

**Status:** Complete

Highway.Client sends `HW.*` commands to Highway.Server via StackExchange.Redis over RESP. This is where location transparency happens — the same `ExecuteAsync` that dispatched locally now routes to the server when `options.Server` is configured.

**Unlocks:** Distributed communication between processes.

**Key deliverables:**
- Client sends `HW.CALL` and waits for reply (with timeout → 504)
- Client worker loop: `HW.DEQUEUE` → execute service → `HW.REPLY` → `HW.ACK`
- Client sends `HW.PUBLISH` for pub/sub
- Client subscribes to channels: `HW.SUBSCRIBE` → poll/doorbell → `HW.RECEIVE` → dispatch to local subscribers
- Doorbell pattern (RESP `SUBSCRIBE` as latency optimization + polling as safety net)
- Competing consumers (multiple nodes dequeue from same service queue)
- Call timeout with CancellationToken (default 30s)
- Connection management via SE.Redis `ConnectionMultiplexer`

---

### 006 — Heartbeat & Service Registry

**Status:** Complete

Nodes register their catalog once and then prove liveness cheaply. The server maintains a registry of which nodes host which services, enabling fast-fail (404 before timeout) and operator visibility.

**Unlocks:** The server knows what's online; operators can see their topology.

**Key deliverables:**
- `HW.HEARTBEAT <nodeId> <catalogJson>` — registration, once per node lifetime
- `HW.HEARTBEAT <nodeId>` — liveness only; replies `+REGISTER` when the server holds no record, making a wiped registry self-healing
- `HW.HEARTBEAT <nodeId> BYE` — graceful departure, runs the full teardown immediately
- `HW.DISCOVER <service>` — live nodes hosting a service, with the age of each one's last beat
- `HW.STATS [service|channel]` — queue depth, hosts, in-flight, subscriber groups
- Stale-node pruning: unacknowledged RPC work requeued, worker sets and registry cleaned — **subscriber groups deliberately untouched**
- Optional fast-fail (off by default) with a short-TTL discovery cache

**Design note:** the catalog rides the wire once per node lifetime, not once per beat. Registration and liveness are different operations sharing one command, so a node hosting 200 services beats with the same payload as one hosting none.

**Also discharges:** the `hw:svc:{service}:nodes` unbounded-growth deferral from 004.1.

---

### 002 — Observability & Flight Recorder

**Status:** Complete

Built-in observability with zero external infrastructure. Every operation is recorded in an in-memory flight recorder (1 GB ring buffer) with millisecond timestamps and full payloads. Simultaneously exports via OpenTelemetry for integration with external stacks.

**Unlocks:** Production debugging, traffic replay, audit trails.

**Why last:** Observability hooks into every HW.* command handler. Those handlers (005-007) must exist first. Building observability before there's traffic to observe produces dead code.

**Key deliverables:**
- Flight recorder ring buffer in Garnet (configurable retention, payload capture modes)
- `HW.REPLAY <name> [FROM ts] [TO ts]` — query recorded events
- OpenTelemetry span export (OTLP gRPC/HTTP)
- Configurable per service/channel (retention, payload capture, disable)
- `HW.STATS RECORDER` — flight recorder health metrics
- Event schema in Abstractions for replay tooling

---

### 010 — Runnable Samples

**Status:** Complete

Three console apps under `samples/` — a broker, a service host, and a storefront
— plus a shared contracts library. The first time Highway ran as a deployed
system rather than inside a test host.

**Found on the first run:** a caller-only node could address nothing, because
the catalog derived addressing from locally hosted implementations. Every
`ExecuteAsync` from a pure caller returned `SERVICE_NOT_FOUND` for services
running in another process — the product's headline use case. 440 tests missed
it because every integration node scans the same assembly and hosts everything.

**Now proven end to end:** standalone broker process, RESP over a real socket
between OS processes, generic-host lifecycle, cross-assembly scanning, durable
delivery across subscriber downtime and across a broker restart, competing
consumers, and RPC plus pub/sub over a non-loopback interface.

Findings live in `samples/RUNLOG.md`. Running the samples is a recurring test:
any feature changing the protocol or public API must update and re-run them
(`.kiro/steering/spec-workflow.md` § Living Conformance).

---

### 011 — Dashboard: Flight Recorder View

**Status:** Complete

An embedded web dashboard served from `Highway.Server.Dashboard` on a separate
port. Reads the flight recorder in-process and presents a hand-written HTML/CSS/JS
page from embedded resources — no external dependencies, no build step.

**Key deliverables:**
- Recorder health overview: stat grid and name table
- Event query per name with time window, node, and limit filters
- Server-Sent Events for live tailing with backpressure and drop counting
- Shared projection enforcing capture modes and `ReplayEnabled`
- Concurrency-capped streaming (default 4) that cannot delay the recording path
- API key authentication, loopback-bound by default, exposure warnings

**Design note:** The recording thread's entire streaming cost is one non-blocking
`TryWrite` per subscriber per event. All serialization and I/O happens on the
reader side. A stalled browser drops events and counts them rather than blocking
the broker.

---

## Beyond v1: Delivery You Can Trust

> **Measured against [`constraints.md`](constraints.md)**, which numbers every guarantee and
> records whether the code keeps it.

### 014 — The Queue

**The missing third verb.** Highway can address work with a reply (`ExecuteAsync`, competing
consumers) and events without one (`PublishAsync`, fan-out). It has no way to say *"do this
work, exactly one worker, I am not waiting for an answer"* — so people reach for
`PublishAsync` and then need it to behave like a queue, which is what pushed dead letters,
retention and durability onto pub/sub in the first place.

| | Contract | Attribute | Handler | Verb |
|---|---|---|---|---|
| RPC | `IReturn<TResponse>` | `[Service]` | `AsyncService<TReq,TRes>` | `ExecuteAsync` |
| **Queue** | **`ISend`** | **`[Queue]`** | **`IProcess<T>`** | **`SendAsync`** |
| Pub/Sub | `IPublish` | `[Channel]` | `ISubscribe<T>` | `PublishAsync` |

One handler → Send. Many handlers → Publish. Need the answer → Execute.

Cheap to build, because the machinery exists: `hw:svc:{name}:q` is already a
competing-consumer queue with leases, acknowledgement, attempt counting and dead letters. A
queue is RPC minus the reply. It also inherits feature 013 wholesale — delayed sends,
`[Idempotent]`, and `HW.DLQ` all work on day one.

**Build this before 016.** Several retention constraints move from pub/sub to queues, and
building gigabyte budgets into pub/sub first would be the expensive order.

### 015 — Recoverability

**Specced, then reduced by engineering review.** `docs/features/015-recoverability/`. Originally three tiers between "a handler threw" and
"an operator has to look at this", plus the failure context that makes the last one useful.

A handler signals failure by throwing, and that stays. What is thin is everything after:
Highway has no in-process retry (so a deadlock costs a five-minute lease), no delayed tier,
no way to declare an exception unrecoverable — and, most importantly, **a dead letter does
not say why it died.** The exception is discarded where it is caught, so an operator running
`HW.DLQ PEEK` learns something failed six times and must correlate logs across workers to
find out what.

**The review cut it to that Phase 1 plus a refactor**, and corrected the spec: failure context
is *not* "mostly plumbing". The client holds the exception and the **server** writes the dead
letter, so it needs a new `HW.FAIL` command and a failure block on two entry framings. A
side-key design was drafted and found unimplementable — the lease sweep discovers which
messages exhaust their attempts only in `Main`, so it cannot declare per-message keys in
`Prepare`, and Garnet rejects touching an undeclared key.

The refactor lands first and alone: `RpcWorkerLoop` and `QueueWorkerLoop` are near-identical
and would otherwise have had retry logic added to them separately — the same shape as the
defect 013 found in three independently written requeue paths.

*Why retry at all rather than dead-lettering immediately?* Because the dead-letter queue's
value is that it is **rare**. Transient faults are common and self-healing; if each produced
a dead letter the queue fills with noise, nobody watches it, and the genuinely poisoned
message becomes invisible. The related instinct — get the failure out of the live queue so it
cannot block what is behind it — is right and is kept, as the delayed tier. It belongs in a
**retry** structure, not the DLQ: a retry set means "needs a moment", a dead-letter list
means "needs a human", and conflating them removes the distinction that makes either useful.

### 018 — Pub/Sub Unification  ✅

**Status:** Complete

A durable subscription stops *resembling* a queue and becomes one: `PublishAsync` fans out
into one queue per registered group (`{channel}@{group}`), and subscribers consume with the
same commands and the same worker loop as `[Queue]`. Roughly **944 lines deleted**;
commands 18 → 16; entry framings **4 → 2**.

**Placed before 016** because it deletes the group queues 016 was going to bound. Retention
first would have meant building byte budgets and eviction for a structure about to disappear.

Protocol 4.0: two commands removed (`HW.RECEIVE`, `HW.RACK`), two entry framings removed,
`HW.DLQ` and `HW.FAIL` narrowed to `SVC|Q`. Existing channel data becomes unreachable;
the broker refuses to start against it rather than serving an empty channel.

**Three semantic changes:** batch consumption lost (one claim per round trip), subscriber
ordering preserved by default (concurrency 1), deferred publish resolves groups at publish
time (not promotion time).

---

### 016 — Retention, Storage and Durability

> **Runs after 018, and was rewritten once 018 shipped.** Requirement 3 halved — group queues
> and queues were two bounding jobs because they were two implementations, and they are one now.
> A fifth open decision appeared that did not exist before: publish fans out in one transaction,
> so a single full group queue would fail the publish for **every** group, letting one stuck
> subscriber block a channel for all the healthy ones.
>
> **Note (post-018):** Group queues no longer exist as a separate structure. They are now
> ordinary queues under `hw:q:{channel}@{group}:*`, bounded by the same mechanism as every
> other queue. C4.4's former "Pub/Sub group queues — no bound at all" row is gone.

**Specced** — `docs/features/016-retention-and-durability/requirements.md`. Closes all five
remaining unmet constraints (C4.1–C4.6). One coherent piece of work rather than five
problems.

**018 delivered no durability in the sense this feature means.** Two things share the word:
*retention until processed* (a consumer is down — built, 013/014/018) and *durability across a
restart* (the broker process dies — not built). None of what 013–018 built survives `kill -9`.

**C4.5 is the one that makes the others conditional:** `new HighwayServerBuilder().Build()`
is memory-only, so every queue and pub/sub guarantee is false in the configuration a
newcomer meets first. Feature 014 shipped a warning because a silent lie was unacceptable;
this replaces the warning with the fix.

**Five open decisions are recorded in the requirements rather than guessed** — what the byte
budget is measured against, whether a full-queue refusal is permanent or transient, the
`MaxDeliveryAttempts` off-by-one, where the default data directory lives, and what a fan-out
does when one group's queue is full. Each changes
the shape of the feature, so the design is not written until they are settled.

- Byte-based caps with real accounting; 1 GB default, configurable
- 100-day retention default
- Group queues bounded like every other structure
- **Backpressure instead of silent loss** — refuse the publish rather than drop the oldest
- Durable by default
- `AofSizeLimit` and checkpointing, so the log does not grow forever

### 020 — Dashboard: Operations Console  ← **next**

**Specced** — `docs/features/020-dashboard-operations/`. Feature 011 built a dashboard for the
flight recorder, and that is still all it is. Five features have shipped since, each adding
operational state it cannot show: dead letters with their diagnosis (015), byte budgets and
refusals (016), retirement countdowns (017), groups-as-queues (018), long-running handlers (019).

**The dashboard sees events, and nothing else.** An event says something happened; an operator
needs to know what is true *right now* — how full that queue is, what is in the dead-letter
list, which subscriber is about to be retired and take its backlog with it.

**The first requirement is not a view.** The dashboard runs in-process with only
`FlightRecorder` injected and has **no connection to its own broker**. Opening a loopback one is
the obvious answer and it has already failed: 018's pre-018 check did exactly that, mirrored the
password but not TLS, and no TLS-enabled server could start. mTLS defeats it even when correct.
So T1 is a spike deciding between an in-process read API and a server-owned connection, and T2
proves the answer against all four security configurations before a single view is built.

**Read-only.** No requeue, purge or retire buttons — an operator destroying a dead-letter list
from a browser tab is a different threat model, and writes get their own feature.

The highest-value item is the **retirement countdown**: 017 made retirement automatic and it
destroys a subscriber's whole backlog. A countdown turns the largest single loss Highway can
inflict from a surprise into a decision.

### 019 — Long-Running Tasks  ✅ **shipped**

`HighwayServerOptions.Lease` defaults to 5 minutes and could not be extended, so a handler that
outlived it had its message requeued **while it was still running** — a concurrent duplicate
caused by nothing but slowness. A twenty-minute job ran five times and then dead-lettered.

`HW.TOUCH` moves a claimed entry's timestamp forward; the client renews automatically while a
handler runs. **Bounded by `MaxProcessingTime` (15 min)** — unbounded renewal would delete lease
recovery, letting a deadlocked handler hold its message forever.

For work measured in hours, renewal is the wrong tool:
[`docs/cookbook/long-running-work.md`](../cookbook/long-running-work.md) documents chunk-and-checkpoint.

### 017 — Node Decommissioning  ← **next**

**Specced** — `docs/features/017-node-decommissioning/`. A node that is never coming back can
say so, an operator can say it on the node's behalf, and — the part that matters — **the broker
works it out by itself**.

**Feature 016 turned a memory leak into an outage.** A crashed subscriber's group queue fills to
`MaxQueueBytes`, and because a fan-out reaches every registered group or none (018), **every
publish to that channel is then refused**. One dead subscriber takes down a live channel. 016's
Open Decision 5 accepted that cost on the condition this feature would exist.

**The embarrassing part:** the broker already knows. Feature 006's heartbeat registry tracks
node liveness; 018 made a subscriber group *be* a node. The two facts sit in the same process
and have never been introduced. Connecting them is the feature.

- `CleanAndByeForever()` — the node says so. Stop the loops **first**, or the next heartbeat resurrects it
- `HW.HEARTBEAT <node> BYE PURGE` — the operator says it, for the commoner case where the node is already gone
- **Automatic retirement** after a configurable absence (24h default), riding on the heartbeat prune
- Subscriber queues are **deleted**; queue and RPC work is **requeued** — it belongs to the queue, not the node
- Every retirement is loud: node, absence, threshold, and how many messages and bytes were discarded

**Driven by liveness evidence, not consumption gaps.** RabbitMQ's `x-expires` and Azure's
`AutoDeleteOnIdle` only know "nobody consumed for N minutes" and cannot tell a dead subscriber
from a nightly batch job. Because a Highway group is a node with a heartbeat, Highway can. It is
the one place the product can be strictly better than its closest analogues, and it costs
nothing to take.


The next theme is **not** breadth. It is making the delivery Highway already
promises actually trustworthy, because there are gaps in it today.

`docs/product/runtime-vision.md` — which framed the next phase as becoming "a
distributed application runtime" and listed nine primitives — has been
**withdrawn**. Three reasons, recorded so the idea is not re-proposed from
scratch:

1. "Distributed Application Runtime" is literally what Dapr's name stands for. Adopting it invited comparison on breadth — actors, workflows, pluggable state stores, bindings, eight language SDKs — against a product whose advantage is that a developer is productive in five minutes.
2. Several of the nine were wrappers over commands an application can already issue on the connection it already has (counters), or invited misuse of a broker as a database (shared dictionary), or required inventing an expression language on the hot path (content-based routing).
3. The document's central claim — "none of these require new server commands" — is false for at least delayed delivery and deduplication, both of which need multi-step atomicity that stock `GET`/`SET`/`ZADD` cannot provide across a failure.

**What the review found instead**, and what feature 013 addresses: Highway has
real reliability gaps in shipped code, and they matter more than any of the nine.

| Gap | Status |
|---|---|
| **A permanently failing message is redelivered forever.** Lease recovery requeues abandoned RPC work with no attempt limit, no dead-letter destination, and no way to see or drain it. One poison message poisons a queue indefinitely | Feature 013 |
| **At-least-once delivery with nothing to deduplicate against.** Highway's own lease recovery can deliver the same request twice by design, and hands the duplicate-handling problem entirely to the application | Feature 013 |
| **No way to defer work.** Retry-with-backoff, scheduled sends, and delayed retries all currently require Hangfire, Quartz, or a database | Feature 013 |

Cache and locking remain reasonable *small* additions later — a lock in
particular is only worth shipping with a fencing token, which Highway can offer
because it owns the server and a naive Redis wrapper cannot.

### Next: 013 — Reliable Delivery

| Part | API | Why |
|---|---|---|
| **Dead letters + retry limits** | `HW.DLQ`, `MaxDeliveryAttempts` | Fixes shipped behaviour: a poison message currently loops forever |
| **Delayed delivery** | `PublishAsync(msg, delay: ...)` | One parameter on an API that already exists; also gives retry-with-backoff |
| **Deduplication** | `[Idempotent]` | Closes the gap Highway's own at-least-once redelivery creates |

### Later, and only if wanted

| Feature | API | Note |
|---|---|---|
| **Distributed Cache** | `IDistributedCache` (standard .NET) | Strongest of the wrappers precisely because the interface is not ours — it plugs straight into ASP.NET output caching and session state |
| **Distributed Locking** | `AcquireLockAsync(key, ttl)` → token | **Only with a fencing token.** `SET NX EX` alone is not a correctness lock: a GC pause or clock skew lets two holders proceed. Highway owns the server, so it can issue a monotonic fence a generic Redis wrapper cannot |
| **Rate Limiting** | `System.Threading.RateLimiting.RateLimiter` | Worth doing only as the BCL abstraction, not a bespoke `CheckRateLimitAsync` |
| **Leader Election** | `[Singleton]` on a service | Depends on the lock. Must be named honestly — "at most one", never "exactly one" |

### Rejected

| Proposal | Why not |
|---|---|
| Shared Dictionary | Invites treating a broker as a database: no transactions, no consistency story, no answer for concurrent writers. The cache covers the real need |
| Content-Based Routing `[Filter("...")]` | A server-side expression language — new syntax to learn, a security surface, and a cost on the hot path. Filter in the subscriber |
| Atomic Counters | It is `INCR`. Three lines on the multiplexer the application already holds |

### Phase 3: Advanced (v2.0)

| Feature | Description |
|---|---|
| Sagas / Process Managers | Long-running workflows with compensation |
| Transactional Outbox | Atomic DB write + message publish |
| Clustering | Multi-server Highway.Server deployment |
