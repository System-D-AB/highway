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

### 015 — Node Decommissioning

A node that is never coming back can say so, and an operator can say it on the node's
behalf. Closes C1.5's unbounded growth.

- `IHighwayClient.CleanAndByeForever()` — stop the loops first (or the next heartbeat resurrects the node), drain in-flight work, then purge
- `HW.HEARTBEAT <node> BYE PURGE` — the operator path, for the far more common case where the node is already gone
- Unacknowledged **RPC** work is requeued, never deleted; queued **messages** are deleted — the subscriber has declared it no longer exists
- Returns what it destroyed, so an irreversible operation appears in a log

### 016 — Retention, Storage and Durability

**Specced** — `docs/features/016-retention-and-durability/requirements.md`. Closes all five
remaining unmet constraints (C4.1–C4.6). One coherent piece of work rather than five
problems.

**C4.5 is the one that makes the others conditional:** `new HighwayServerBuilder().Build()`
is memory-only, so every queue and pub/sub guarantee is false in the configuration a
newcomer meets first. Feature 014 shipped a warning because a silent lie was unacceptable;
this replaces the warning with the fix.

**Four open decisions are recorded in the requirements rather than guessed** — what the byte
budget is measured against, whether a full-queue refusal is permanent or transient, the
`MaxDeliveryAttempts` off-by-one, and where the default data directory lives. Each changes
the shape of the feature, so the design is not written until they are settled.

- Byte-based caps with real accounting; 1 GB default, configurable
- 100-day retention default
- Group queues bounded like every other structure
- **Backpressure instead of silent loss** — refuse the publish rather than drop the oldest
- Durable by default
- `AofSizeLimit` and checkpointing, so the log does not grow forever


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
