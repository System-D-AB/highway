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

Last reviewed: 2026-08-08 (feature 018).

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

## C2 — Pub/Sub (`PublishAsync`)

### C2.1 — A published message is delivered at least once to every group registered at publish time

**Status: Met.**

Fan-out across groups, atomic — all groups or none. Each group has its own **queue** (named
`{channel}@{group}`) with the same lease, acknowledgement, attempt counter and dead-letter
list as any other queue. One group failing has no effect on another.

**"Delivered" is per group, never "delivered to anyone".** First-acknowledgement-wins would
let a fast subscriber deny a slow one the message, which is not fan-out.

### C2.2 — A delivered and acknowledged message is gone

**Status: Met.**

A message leaves a group's queue when that group acknowledges it via `HW.QACK`. Storage tracks
**undelivered** work, which in a healthy system is near zero.

### C2.3 — A subscriber that is down receives what it missed, **until its node is declared gone**

**Status: Met, and now bounded** — feature 018 for the holding, feature 017 for the bound.

A registered group's queue holds every publish while its subscriber is away, so a restart or a
deploy loses nothing. That was unqualified until 017 and it could not stay that way: a guarantee
to hold messages forever, for a node that will never return, is a guarantee to fill a disk and —
after 016 — to block the channel for every healthy subscriber on it.

The bound is **evidence-based, not a blind idle timer**. A group is retired when the node that
owns it has been absent from the heartbeat registry past `SubscriberRetirementThreshold`
(24 hours by default). A group nobody has *consumed* from is not dead; a group whose node has
stopped *heartbeating* is. RabbitMQ's `x-expires` and Azure's `AutoDeleteOnIdle` cannot tell
those apart — Highway can, because a group is a node (018).

Three ways a group is retired: the node says so (`CleanAndByeForeverAsync`), an operator says it
(`HW.HEARTBEAT <node> BYE PURGE`), or the broker decides after the threshold. Plain `BYE`
retires nothing — *"I am stopping"* and *"I am never coming back"* are different statements and
confusing them loses data.

Retirement deletes the backlog and is **never silent**: it logs at Warning, records a
`GroupRetired` event, and reports the messages and bytes destroyed.

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

**Status: Not met.** Feature 016 found it needs a breaking framing change first.

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

### C4.6 — Storage growth is bounded over time, not just in the moment

**Status: Not met.** Feature 016 attempted it and **measured that it does not work**.

`AofSizeLimitBytes` (512 MB default) is wired to Garnet's `AofSizeLimit` and its background
enforcement task does run — checkpoints appear where none did before. The append-only log is
nevertheless **never truncated on disk**:

| identical traffic | AOF on disk |
|---|---|
| 2,000 messages | 8.9 MB |
| 4,000 messages | 17.8 MB |

Linear in total history, which is exactly what this constraint exists to prevent. A broker that
has run for a year still holds a year of log.

The test is kept and **skipped**, carrying the measurement, so the gap stays visible.
`docs/features/016-retention-and-durability/tasks.md` T6 records what was learned — including
that two earlier versions of that test were vacuous, one measuring the wrong directory
entirely. Next step is investigation (Garnet compaction settings), not another attempt at the
same setting.

Recovery *time* may still be bounded by checkpointing even while disk use is not. That is
**unmeasured and not claimed**.

### C4.7 — The byte budget bounds a queue, not the process

**Status: Deliberately unmet** — feature 016, decision 1.

`MaxQueueBytes` is **per structure**. Ten queues at their limit is ten gigabytes; nothing bounds
the process as a whole.

This is recorded rather than implied because an operator reading "1 GB" will otherwise assume
the wrong thing. A server-wide budget is what they actually mean, and it is materially more
work: a global accountant on every enqueue, plus an eviction or refusal policy across unrelated
structures deciding whose message loses. 016 shipped the bound that could be built without
that, and named the gap instead of letting the default imply a guarantee it does not make.

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
| **Ordering under backoff** | Redelivery preserves head-of-queue order by default; enabling backoff trades that away. No setting gives both. This trade-off is a C5 row, not a numbered constraint — it is a property Highway declines to promise, not one it keeps. |

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
| C1.4 | Unprocessable messages stop being retried, and say why | ✅ Met (013 + 015) |
| C1.5 | Sends can be deferred | ✅ Machinery met (013) |
| C2.1 | At-least-once per registered group | ✅ Met |
| C2.2 | Acknowledged means gone | ✅ Met |
| C2.3 | A down subscriber receives what it missed, until its node is declared gone | ✅ Met, bounded (017) |
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
| C4.6 | Bounded over time | ❌ Not met — measured broken (016) |
| C4.7 | Byte budget bounds a queue, not the process | ⚠️ **Deliberately unmet** (016 decision 1) |
| C7.1 | Diagnostics can never break a delivery | ✅ Met (002 + 015) |
| C7.2 | Diagnostic detail obeys the payload capture switch | ✅ Met (015) |
| C6.1 | Cannot reach the network unauthenticated by accident | ✅ Met |
| C6.2 | Credentials never logged | ✅ Met |
| C6.3 | Auth failures permanent and legible | ✅ Met |
| C6.4 | TLS available, never required | ✅ Met |
| C6.5 | The tested path is the secured path | ✅ Met |

**Two unmet constraints remain, both in C4, and both are understood rather than merely
outstanding:** C4.1 (retention) needs a breaking framing change first, and C4.6 (bounded
storage growth) was attempted and **measured not to work**. C4.7 is unmet by choice.

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
get stale, and this one is already linked from `CLAUDE.md`, `product.md` and the roadmap.

| Item | Deferred from | Why |
|---|---|---|
| **Retry tiers** — immediate, delayed, `[Unrecoverable]` | 015, by engineering review | 015 would have touched 11 files and added retry logic to three near-identical worker loops. Reduced to a structural refactor plus failure context; the reasoning for the tiers is preserved in `docs/features/015-recoverability/requirements.md` § Deferred |
| **Polly / `Microsoft.Extensions.Resilience`** | 015 | The .NET built-in for retry pipelines and the obvious "does the framework already do this?" answer. Moot until the tiers return. Highway takes no dependency beyond Garnet and StackExchange.Redis, so it is a real trade rather than a free win |
| **`MaxDeliveryAttempts` off-by-one** | 013, then 015 | Belongs with the attempt-counting work, because that is what redefines what an attempt *is*. Also listed under Open Decisions above |

## Cross-references

- [`docs/HIGHWAY-PROTOCOL.md`](../HIGHWAY-PROTOCOL.md) — the wire contract these guarantees are built on
- [`roadmap.md`](roadmap.md) — what is being built and in what order
- [`product.md`](product.md) — vision and positioning
- `docs/features/013-reliable-delivery/` — dead letters, delayed delivery, deduplication
- `docs/features/014-queue/` — the queue
