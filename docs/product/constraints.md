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

Last reviewed: 2026-08-08 (feature 014).

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

**Status: Met** — feature 014.

Exactly one `IProcess<T>` handles each message. Multiple instances of the same application
**compete** — they share the work, they do not each get a copy.

### C1.2 — A sent message survives until it is processed

**Status: Met** — feature 014, subject to C4.

This is the queue's reason to exist. A message with no worker running waits. A message whose
worker crashes mid-handling is redelivered after its lease. Nothing removes it except
successful acknowledgement, dead-lettering (C1.4), or an explicit purge.

Bounded only by C4.1 and C4.2.

### C1.3 — Sending never requires a running consumer

**Status: Met** — feature 014.

`SendAsync` succeeds whether or not any worker exists. The message waits. This is the
capability whose absence made people misuse `PublishAsync`.

### C1.4 — A message that cannot be processed stops being retried

**Status: Met by the underlying machinery** — feature 013.

`MaxDeliveryAttempts` bounds redelivery; exhaustion moves the message to a dead-letter list
atomically. `HW.DLQ PEEK / REQUEUE / PURGE` operate on it. The queue inherits this on day one
rather than needing it built.

### C1.5 — A send can be deferred

**Status: Met by the underlying machinery** — feature 013.

`SendAsync(message, delay)` schedules work without a scheduler. It is a **"not before"**,
driven by worker polling rather than a timer — see C5.

---

## C2 — Pub/Sub (`PublishAsync`)

### C2.1 — A published message is delivered at least once to every group registered at publish time

**Status: Met.**

Fan-out across groups, atomic — all groups or none. Each group has its own queue, lease,
acknowledgement, attempt counter and dead-letter list. One group failing has no effect on
another.

**"Delivered" is per group, never "delivered to anyone".** First-acknowledgement-wins would
let a fast subscriber deny a slow one the message, which is not fan-out.

### C2.2 — A delivered and acknowledged message is gone

**Status: Met.**

A message leaves a group's queue when that group acknowledges it. Storage tracks
**undelivered** work, which in a healthy system is near zero.

### C2.3 — A subscriber that is down receives what it missed

**Status: Met.**

A subscriber group outlives the process that created it. `HW.HEARTBEAT BYE` — sent
automatically on graceful shutdown — deliberately does not remove it. A node down for a week
returns to find its messages waiting. Verified across real processes in the sample run.

### C2.4 — Pub/Sub is **not** a store for messages nobody has subscribed to

**Status: Met** — the backlog was removed once `SendAsync` gave that use case a proper home.

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

**Status: Met.**

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

**Status: Not met — default is 1 day.** Feature 016.

### C4.2 — Size cap: 1 GB, configurable

**Status: Not met — the cap is a count of 10,000 entries, not a size.** Feature 016.

What exhausts a server is bytes, not entries, and a count cannot express "as much memory as I
am willing to give this".

### C4.3 — Reaching a limit is never silent

**Status: Not met — the backlog drops its oldest entry.** Feature 016.

**Intended: refuse the send or publish.** Under C1.2 a queued message is one nobody has ever
processed; discarding it is losing data the queue exists to protect. A producer that receives
an error can retry or shed load. One that receives silent success cannot.

### C4.4 — Every queue-like structure is bounded

**Status: Not met.** Feature 016.

| Structure | Bounded? |
|---|---|
| Channel backlog | **Removed** (C2.4) |
| Dead-letter queues | Yes — `MaxDeadLetterEntries` (feature 013) |
| **Pub/Sub group queues** | **No — nothing at all** |
| **Queues (C1)** | Not built yet; must be bounded from the start |

An orphaned group queue — a node decommissioned without unsubscribing — receives a copy of
every subsequent publish forever. This, not the backlog, is what will actually consume a
gigabyte. Its remedy is feature 015.

### C4.5 — Durability is the default, not an option

**Status: Not met.** Feature 016.

`new HighwayServerBuilder().Build()` is memory-only: no data directory, no AOF, everything
lost on restart. Every guarantee in C1 and C2 is false in that configuration.

### C4.6 — Storage growth is bounded over time, not just in the moment

**Status: Not met.** Feature 016.

Highway enables AOF and sets a checkpoint directory but sets no `AofSizeLimit` and leaves
`CompactionFrequencySecs` at `0`. The log grows without limit and restart recovery replays all
of it. At the retention target in C4.1 this becomes the binding operational constraint, and it
is independent of every other decision here.

---

## C5 — What Highway does not guarantee

| Not guaranteed | Note |
|---|---|
| **Exactly-once delivery** | Not achievable without a transactional participant. `[Idempotent]` (013) makes a handler run at most once per *redelivery*; it cannot relate two separate sends. |
| **High availability** | Single broker, `EnableCluster = false`. Durability yes, failover no. |
| **Replayable history** | C2.5. |
| **Transactional enlistment** | No DTC, no ambient transaction. An MSMQ user who depends on this is not one Highway can serve. |
| **Message priority or selective consumption** | FIFO, no filtering. |
| **Per-message TTL** | Retention is per queue or channel. |
| **Characterised throughput** | No benchmark exists; no figure is claimed anywhere. |
| **Second-accurate scheduled delivery** | Delay is a "not before", driven by consumer polling, not a timer. |
| **Ordering under backoff** | Redelivery preserves head-of-queue order by default; enabling backoff trades that away. No setting gives both. |

---

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

---

## Status summary

| | Constraint | Status |
|---|---|---|
| C1.1 | Sent message processed at least once | ✅ Met |
| C1.2 | Survives until processed | ✅ Met |
| C1.3 | Sending needs no running consumer | ✅ Met |
| C1.4 | Unprocessable messages stop being retried | ✅ Machinery met (013) |
| C1.5 | Sends can be deferred | ✅ Machinery met (013) |
| C2.1 | At-least-once per registered group | ✅ Met |
| C2.2 | Acknowledged means gone | ✅ Met |
| C2.3 | A down subscriber receives what it missed | ✅ Met |
| C2.4 | Not a store for absent subscribers | ✅ Met |
| C2.5 | Not a replayable log | ✅ Met |
| C3.1 | In-flight requests survive departure | ✅ Met |
| C3.2 | An answer or a timeout, never silence | ✅ Met |
| C3.3 | Retry budget may outlive the caller | ✅ Met |
| C4.1 | Retention 100 days | ❌ Not met — 016 |
| C4.2 | Size cap 1 GB | ❌ Not met — 016 |
| C4.3 | Limits are never silent | ❌ Not met — 016 |
| C4.4 | Every queue-like structure bounded | ❌ Not met — 015, 016 |
| C4.5 | Durable by default | ❌ Not met — 016 |
| C4.6 | Bounded over time | ❌ Not met — 016 |
| C6.1 | Cannot reach the network unauthenticated by accident | ✅ Met |
| C6.2 | Credentials never logged | ✅ Met |
| C6.3 | Auth failures permanent and legible | ✅ Met |
| C6.4 | TLS available, never required | ✅ Met |
| C6.5 | The tested path is the secured path | ✅ Met |

**All five unmet constraints are in C4** — retention, storage and durability — which is one
coherent feature rather than six. Feature 014 delivered C1.

---

---

## Open decisions

1. **`MaxDeliveryAttempts` off-by-one.** The comparison is `attempts > MaxDeliveryAttempts`, so a limit of 5 permits 6 deliveries while the name says 5. Change the comparison, or rename to "redeliveries"?
2. **What "1 GB" is measured against.** Per queue, per channel group, or a server-wide budget? Only a server-wide budget actually bounds the process, but it needs a global accountant and an eviction policy.
3. **Backpressure shape.** C4.3 says refuse. Which error code, and is it permanent or transient to the client? A full queue may drain, which argues transient and retryable.
4. **Do queues and services share a name space?** Feature 014 proposes separate `hw:q:` keys so a queue and a service may share a name without colliding.

---

## Cross-references

- [`docs/HIGHWAY-PROTOCOL.md`](../HIGHWAY-PROTOCOL.md) — the wire contract these guarantees are built on
- [`roadmap.md`](roadmap.md) — what is being built and in what order
- [`product.md`](product.md) — vision and positioning
- `docs/features/013-reliable-delivery/` — dead letters, delayed delivery, deduplication
- `docs/features/014-queue/` — the queue
