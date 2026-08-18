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

### 015 — Recoverability  ✅

**Shipped as reduced by engineering review.** `docs/features/015-recoverability/`. Originally three tiers between "a handler threw" and
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

### 016 — Retention, Storage and Durability  ✅

> **Runs after 018, and was rewritten once 018 shipped.** Requirement 3 halved — group queues
> and queues were two bounding jobs because they were two implementations, and they are one now.
> A fifth open decision appeared that did not exist before: publish fans out in one transaction,
> so a single full group queue would fail the publish for **every** group, letting one stuck
> subscriber block a channel for all the healthy ones.
>
> **Note (post-018):** Group queues no longer exist as a separate structure. They are now
> ordinary queues under `hw:q:{channel}@{group}:*`, bounded by the same mechanism as every
> other queue. C4.4's former "Pub/Sub group queues — no bound at all" row is gone.

**Shipped** — `docs/features/016-retention-and-durability/`. Closed all five
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

### 023 — Messages, Not Protocol Events  ✅

**Shipped** — `docs/features/023-message-centric-dashboard/`. 022 made the dashboard show *what
exists*. Clicking into a queue still shows six rows of `QueueSent` / `QueueClaimed` /
`QueueAcknowledged` for two messages — **not one of them a thing the developer did**. The page
cannot answer the first question anyone asks: how many succeeded, and how many failed?

**The unit is wrong.** A row per protocol event, where the unit of meaning is a *message* — sent
once, processed once, with an outcome.

**The correlation key is already in every event and nothing has ever grouped by it.** RPC and
queue events carry `requestId`; a publish carries the channel sequence and fans that same number
into each group's entry. A message's whole life — including its journey across nodes — is
reconstructible from data the recorder has held since 002.

Three views, all projections of the same events: **by entity** (what is happening to
`orders.create`), **by message** (what happened to this one order, across every node, with the
body and the reply), **by node** (what is this host doing).

**The architectural answer to "the dashboard is becoming an application":** it is, and **the
server aggregates while the browser renders**. That rule has now survived three features — the
browser must not learn the key layout (020), must not parse a name (022), must not decide what
acknowledged means (023) — and at three it is a principle rather than a preference. No build
step; server-side aggregation is what keeps a thin client thin.

Also lands 022's deferred node address, done properly: `NodeRegistration` gains a **version
byte in the same change as the field**, because adding one to an unversioned binary format is
exactly how 013's storage break happened.

### 022 — Dashboard: A Catalogue, Not a List of Names  ✅

**Shipped** — `docs/features/022-dashboard-catalogue/`. Run against the samples, the dashboard's
main page lists ten rows in one column called **Name**, containing **six different kinds of
thing**: nodes (`shop-1`), services (`orders.get`), a queue (`invoices.generate`), channels
(`orders.placed`), group queues (`orders.placed@shop-1`) and an internal bucket (`hw.replies`).
Nothing distinguishes them.

**The dashboard shows the flight recorder's index and calls it the system.** The recorder keys
buffers by an arbitrary name because that is all it needs; rendering that dictionary directly is
a faithful view of the recorder and a useless view of the broker.

This introduces the entity model the product already has — nodes, services, queues, channels and
their groups — and makes the dashboard navigate it: what is running, what serves what, and then
one entity's state and events together.

**The server already knows all of it.** `hw:reg:node:{nodeId}` holds `[lastSeen][catalog json]`
with each node's services, channels and queues, written by every heartbeat since 006 and never
read back for display. This is mostly a rendering problem.

**Supersedes 020's view tasks** (T6–T9) while inheriting its Phase 0 read path unchanged.

### 020 — Dashboard: Operations Console  ✅

**Shipped** (view tasks superseded by 022/023; `HW.STATS` consumer fields T4/T5 remain registered) — `docs/features/020-dashboard-operations/`. Feature 011 built a dashboard for the
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

### 017 — Node Decommissioning  ✅

**Shipped** (retirement liveness generalized to group membership by 025) — `docs/features/017-node-decommissioning/`. A node that is never coming back can
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

### 024 — Hosting Boundaries and Topology  ✅

**Shipped** — `docs/features/024-hosting-boundaries/`. Contracts stay discovered closure-wide;
handlers become hosted by consent (`HostingMode`, `[HighwayHostModule]`, `HostAssembly`), with
`Implicit` as the unbroken default plus a warning that makes reference-equals-hosting announce
itself. Every node logs a topology manifest (PROVIDES / CAN USE) and the dashboard node page
shows both halves. Born from three independent architecture reviews converging on the same gap.

### 025 — Subscription Groups  ✅

**Shipped** — `docs/features/025-subscription-groups/`. `SubscriptionGroup` names the logical
consumer; `NodeName` keeps naming the process. Replicas sharing a group compete for one copy
per event (the claimant IS the group); retirement counts the youngest member; `BYE PURGE`
destroys a queue only for the last member. Protocol 4.4. The default (group = node name) is
the pre-025 behavior exactly.

### 026 — Distributed Cache  ✅

**Status:** Complete

**Shipped** — `docs/features/026-distributed-cache/`. Garnet is
natively a cache-store; this exposes it through `IDistributedCache` / `IBufferDistributedCache`
so `HybridCache` layers on top — standard interfaces, standard Redis commands, no `HW.*`
surface, no new package. Review conditions to land with it met: AOF/memory cohabitation
documented, `constraints.md` C4 row added, and sliding window metadata header implemented.

### 028 — Recurring Jobs  ✅

**Status:** Complete

**Shipped** — `docs/features/028-recurring-jobs/`. Adds recurring job scheduling (`Daily`, `Every`, and cron schedules) to `ISend` contracts without external schedulers like Hangfire or Quartz. Schedule state and promotion ride durable broker storage with `HW.JOB` commands (`HW.JOB SET|DEL|LIST`), exactly-one-fire atomicity inside `HW.QCLAIM`, and client-side options and manifest integration.

---

## The Posture: Core-Complete, Production-Driven

Settled 2026-08-10 (discussion recorded in [`brainstorming.md`](brainstorming.md)):

1. **The verb set is frozen.** *Execute a verb, Publish a fact, Send a job* is the complete
   model. Proposals for a fourth verb are rejected by default.
2. **v1.0 is defined**: everything shipped above (including 026 and 028), plus
   the pre-production hardening set — protocol completion (RESP + `HW.*` declared native,
   per-node reply doorbells), a `Meter` beside the `ActivitySource`, and the assurance rig
   (crash-recovery, disk-full, connection-churn, soak). Then production, for months, on
   purpose.
3. **Post-1.0 work enters through two lanes only**, each with a gate:
   - **Substrate lane** — gated by the 026 scope test: *exposes a native Garnet capability,
     through a standard .NET interface, with zero Highway protocol surface and zero new
     guarantees.* This lane is finite and visibly ends (027, 029, 030 below — then closed).
   - **Connective-tissue lane** — gated by: *closes a recorded gap or strengthens an existing
     guarantee; never adds a paradigm.* (028 shipped; causation ids / hop count; outbox as
     cookbook; retry tiers if production votes them back.)

Not a freeze — a constitution. Freezes break at the first customer ask; a constitution says
which asks to take seriously.

---

## Planned: 027, 029–031

Registered 2026-08-10 as empty feature folders; each gets the full requirements → design →
tasks treatment when its discussion happens. Order within the list is not commitment order.
(031 arrived fully specced on 2026-08-11 — its discussion happened first.)

### 027 — Distributed Rate Limiter

**Lane:** substrate. **Seed:** implement `System.Threading.RateLimiting.RateLimiter` (the BCL
abstraction — never a bespoke `CheckRateLimitAsync`) over `INCR`+`EXPIRE`. The strongest
remaining substrate fit: event-driven apps throttle third-party APIs constantly, and the
interface is Microsoft's, not ours. 026's sibling in every way.

### 029 — Singleton Runner

**Lane:** substrate (borderline — no standard interface exists, so Highway would own the
API). **Seed:** "at most one replica runs this" — `SET NX PX` + heartbeat, the natural
companion to 025's replicas (feature 028 decoupled fire-exactly-once via queue promotion). Must be
named honestly: *at most one*, never *exactly one*. API shape (an attribute? an
`IHostedService` wrapper?) is the discussion.

### 030 — Distributed Lock

**Lane:** substrate. **Status: doubtful, and the doubt is shared** — registered so the
reasoning is in one place when the ask recurs. The pre-existing note in this file's old
"Later" table still holds: `SET NX EX` alone is not a correctness lock (GC pause or clock
skew lets two holders proceed), so this ships **only with a fencing token** — which Highway
*can* mint because it owns the server, and a generic Redis wrapper cannot. The open question
is whether anything needs it that 029 does not already cover; if no concrete need surfaces
during the discussions, this folder closes as a cookbook pattern instead of a feature.

### 031 — Server Distribution

**Lane:** connective tissue — delivers `product.md`'s hosting promise ("run as a single
binary", "deployed as a standalone process in production"); today the broker runs
standalone only by writing your own host. **Fully specced 2026-08-11, simplified
2026-08-12** — `docs/features/031-server-distribution/`: a MongoDB-style zip per RID —
`bin/highways` (exe + DLLs) running broker and embedded dashboard as one process,
`config/highway.json` with full-coverage options incl. the authentication on/off switch,
`data/`, `logs/`, and `scripts/` holding what an operator actually double-clicks: run it
standalone, install or remove it as a Windows service, install or remove it as a systemd
daemon. **No container image** (D8, 2026-08-12): the broker embeds like SQLite, so an
app that hosts it already *is* the image, and a shared broker wraps this zip in the
cluster's own base image. 021's broker-side sibling — exactly the deployment shape 021
carved out as a non-goal, reusing its installer analysis. Decision history recorded in
the spec: OD3 revised to folder publish; OD9 (separate dashboard executable) added and
reverted — the dashboard stays embedded where its flight recorder lives; OD1 revised
2026-08-12 to `highways`. Phase 1 shipped bar T14 (host + configuration layer, 70 tests
green); T14 carries the rename and the fixes a 2026-08-12 review found; Phases 2–6
pending.

### 032 — The Assurance Rig

**Status:** Complete (2026-08-18)

**Lane:** connective tissue — proves existing guarantees rather than adding any. **Fully
delivered 2026-08-18** — `docs/features/032-assurance-rig/`: three applications as real
processes against one `highways` broker under load (50–100 msg/s aggregate), covering every
verb in every direction, with a durable email queue whose consumers deliberately arrive 75
seconds late. Every participant writes an append-only ledger; an independent reconciler proves
by set operations on correlation ids that nothing was lost across 7 correctness invariants,
cross-checked against the broker's own `HW.STATS` and flight recorder. Delivered with a
fast-running CI integration suite (<60s) and a full 4-minute multi-process soak run.
Registers **033** for crash-recovery, disk-full and connection-churn. Claims no throughput
figure (C5) and measures AOF/directory storage. Evidence recorded in `assurance/RUNLOG.md` and
`docs/features/032-assurance-rig/runs.md`.

---

### 033 — Chaos & Fault Injection Assurance Rig

**Status:** Planned

**Lane:** connective tissue — extends Feature 032 harness to destructive broker and environmental failures.

**Scope:**
- Broker abrupt process kill & recovery validation across active traffic
- Disk-full and backpressure handling under sustained load
- Network partition simulation and rapid connection churn / reconnect storms
- Cross-checking storage recovery and AOF replay correctness against ledger invariants

---

### 035 — NuGet Packages ✅

**Status:** Complete (2026-08-18)

**Lane:** connective tissue — closes the gap between `product.md`'s founding claim ("the
client is a NuGet package") and reality, where no package metadata exists anywhere. **Shipped 2026-08-18** — `docs/features/035-nuget-packages/`: four packages
(`Highway.Abstractions`, `Highway.Client`, `Highway.Server`, `Highway.Server.Dashboard`),
with the `highways` executable deliberately excluded because its channel is 031's Release
zip. Two channels, one version property: **Releases** for deploying a broker, **NuGet** for
building against Highway and running one in-process for tests.

**The blocker turned out not to be one.** `Highway.Server` referenced Garnet from source
because `Garnet.host` marks its dependencies `PrivateAssets="All"`. Verified 2026-08-18:
`Garnet.host.csproj` packs those dependencies into `lib/{tfm}` anyway, and the restored
`microsoft.garnet/2.1.3` package carries ten assemblies including `Garnet.server.dll` and
`Tsavorite.core.dll` — everything Highway compiles against. The submodule survives as an
opt-in build mode (`-p:UseGarnetSource=true`) for debugging and for the option of carrying a
patch, which is the only lever available for C4.6 if 034's experiment fails.

---

## Deferred to v2: Traffic Capture and Replay

**Postponed 2026-08-18 by the user, deliberately and with the reasoning kept** — the
priority is finishing v1 and putting it into production, not widening the surface.

The idea: persist the flight recorder to disk asynchronously with 1–2 days of retention;
export a capture; import it into a **staging broker** through the dashboard; re-run it
against nodes that serve those messages, diffing the new replies against the recorded ones.
A regression harness built from real production traffic.

Why it is v2 and not v1:

- **It adds a paradigm.** The connective-tissue gate is *"closes a recorded gap or
  strengthens an existing guarantee; never adds a paradigm."* Capture-and-replay is a new
  one, so it needs to enter as a deliberate product decision rather than as an extension of
  the assurance rig.
- **It is four features, not one**: a durable recorder (server-side, async, bounded); an
  export/import format with redaction; a replay engine; dashboard import. The **durable
  recorder has standalone value** for post-incident diagnosis and is the piece to build
  first if any of it is revived.
- **The constraints it must respect are already written.** C7.1 — a diagnostic write can
  never delay, block or fail a delivery — means the disk drain must drop when it lags, never
  block. C2.5 stays true: the production broker is not becoming a replayable log; a capture
  is re-injected as *new* traffic into a *separate* broker. C5 means "staging broker", not
  a Highway cluster — there is no clustering.
- **Sizing, measured from 032's own run** (~108 recorded events/s): full payloads cost
  ~9.3 GB/day, ~19 GB for two days, against a recorder capped today at 64 MB and one hour.
  The default would have to be `HeadersOnly` with `Full` opted in per name — in a product
  whose C4.6 disk-growth constraint is *already unmet*.
- **The workload makes it a data-protection question.** Password-reset and transactional
  email payloads mean a capture holds reset tokens and email addresses, and moving one to
  staging is a production-PII export. Redaction on export is a first-class step, not a flag.
- **A prerequisite is unmet**: `HW.REPLAY` has never been read successfully by the rig
  (`recorder-replay.jsonl` is 0 bytes across all three 032 runs, from a malformed call), so
  nobody has yet looked at the content a persistence format would have to carry.

Revive by specifying the durable recorder alone. The rest follows only if it earns its way.

---

---

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

### 013 — Reliable Delivery  ✅  (shipped)

| Part | API | Why |
|---|---|---|
| **Dead letters + retry limits** | `HW.DLQ`, `MaxDeliveryAttempts` | Fixes shipped behaviour: a poison message currently loops forever |
| **Delayed delivery** | `PublishAsync(msg, delay: ...)` | One parameter on an API that already exists; also gives retry-with-backoff |
| **Deduplication** | `[Idempotent]` | Closes the gap Highway's own at-least-once redelivery creates |

### Later, and only if wanted — superseded by *Planned: 027–030* above (2026-08-10); kept for the reasoning

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
