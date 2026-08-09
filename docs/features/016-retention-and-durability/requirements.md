# Feature: Retention, Storage and Durability

> **Rewritten 2026-08-09, after feature 018 shipped.** The original was written against a
> two-engine Highway and treated pub/sub group queues as a separate bounding job. 018 made a
> group's queue *be* a queue, so that job merged into another. It also created a question this
> document did not previously have to answer — Open Decision 5, now the sharpest one here.
>
> **What did *not* change: 018 delivered no durability in the sense this feature means.** That
> distinction opens the introduction, because conflating the two is easy and worth writing down.

## Introduction

### Two different things have been sharing one word

| | Survives | Delivered by | Status |
|---|---|---|---|
| **Retention until processed** | a *consumer* being down, slow, or failing | 013, 014, 018 | ✅ Built |
| **Durability across a restart** | the *broker process* dying | **this feature** | ❌ Not built |

Features 013, 014 and 018 built the first thoroughly: a message is held until a consumer
acknowledges it, redelivered if that consumer dies, and dead-lettered with a diagnosis if it can
never succeed. Pub/sub gained all of it in 018.

None of it survives `kill -9` on the broker. `new HighwayServerBuilder().Build()` logs
`dataDir=(memory-only)`: no data directory, no AOF, no checkpoints. Every queue, every group
queue, every dead letter is in memory and gone on exit.

**That is what this feature is for**, and four features' worth of guarantees now rest on it.

### The constraints this closes

`docs/product/constraints.md` reports six unmet constraints and all six are here:

| | Constraint | Today |
|---|---|---|
| C4.1 | Retention: 100 days | 1 day |
| C4.2 | Size cap: 1 GB, configurable | a count of 10,000 entries |
| C4.3 | Reaching a limit is never silent | drops the oldest, silently |
| C4.4 | Every queue-like structure is bounded | queues have no byte cap |
| C4.5 | Durability is the default | **memory-only** |
| C4.6 | Storage growth is bounded over time | AOF grows forever, never compacted |

### The one that makes the others conditional

**C4.5.** A byte budget on a structure that does not survive a restart is a limit on how much
data you can lose at once. Retention measured in days is meaningless when the data does not
outlive the process. **C4.5 is not first among equals; it is the precondition for the rest
meaning anything**, and it is worth shipping even if everything else here is descoped.

Feature 014 shipped a warning because a silent lie was the one unacceptable option. This feature
replaces the warning with the fix.

### What 018 changed about this feature

**It halved Requirement 3.** Group queues and queues were two bounding jobs because they were
two implementations. A group's queue is now a queue — `hw:q:{channel}@{group}:*`, same keys,
same framing, same sweep. One bound covers both verbs.

**It strengthened the case for a server-wide budget** (Open Decision 1). Every subscriber group
is now a full queue — `:q`, `:proc:{node}`, `:nodes`, `:nodelist`, `:delayed`, `:dlq` — so a
broker has more, smaller structures than before. "N structures × 1 GB" was already a weak bound
on the process; it is weaker now.

**It created Open Decision 5.** Publish fans out to every group in **one transaction**. If one
group's queue is full and C4.3 says refuse rather than drop, the whole publish fails and *no
group* receives the message — one stuck subscriber takes down the channel for everyone. This was
not a question before 018, and it has no comfortable answer.

## Requirements

### Requirement 1: Durable by Default

**User Story:** As someone evaluating Highway, I want the broker I start with no configuration to keep my messages.

#### Acceptance Criteria

1. A server built with no configuration is **durable**: a data directory is created, AOF is enabled, and queued work survives a restart
2. **The zero-configuration start still costs nothing to use.** No path to type, no directory to create by hand, no error if the location is unwritable-but-recoverable. This must not be paid for out of feature 012's Requirement 1 (`new HighwayServerBuilder().Build()` just works)
3. The default location is documented, predictable, and stated in the startup log — an operator must be able to answer "where is my data?" without reading source
4. An explicitly **ephemeral** server remains available in one call, for tests and genuinely disposable brokers. `HighwayTestServer` uses it
5. The 014 durability warning is removed, because it is no longer true by default
6. A server that cannot create or write its data directory **fails at `Build()`** naming the path, rather than silently degrading to memory-only. Silent degradation would reintroduce exactly the problem this requirement removes
7. **A restart test proves it for all three verbs**: a queued message, a published message with a registered offline group, and an unacknowledged RPC request all survive a broker restart. 018 unified the storage, so one test shape covers all three — that is the dividend

### Requirement 2: Byte Budgets, Not Entry Counts

**User Story:** As an operator, I want to say how much memory Highway may use, because that is the resource I actually have.

#### Acceptance Criteria

1. Limits are expressed in **bytes**, with a documented default. A count cannot express "as much memory as I am willing to give this", and what exhausts a server is bytes
2. Accounting is real: the size of a structure is tracked or measured, not estimated from entry counts
3. **What the budget is measured against is decided and documented** — Open Decision 1
4. Byte accounting must not add measurable cost to the enqueue path. Highway's write path is measured in nanoseconds and feature 002's flight recorder shows what is acceptable
5. Entry-count limits may remain as a secondary guard, but the byte budget is the primary one

### Requirement 3: Every Queue-Like Structure Is Bounded

**User Story:** As an operator, I want no structure in Highway that can grow without limit.

> **Halved by 018.** This was two requirements — one for queues, one for pub/sub group queues —
> because they were two implementations. They are one now.

#### Acceptance Criteria

1. **Queues are bounded**, and that single statement covers both verbs: `SendAsync` queues and the per-group queues `PublishAsync` fans out into are the same structure under the same keys
2. Dead-letter lists remain bounded (feature 013), and a group's dead-letter list is covered by the same rule for the same reason
3. Delayed and retry sorted sets are bounded
4. **A test enumerates every list, set and sorted set Highway creates and asserts each has a cap**, so a structure added later cannot quietly join without one. This test *is* the requirement — the enumeration is what stops the next feature reintroducing the gap
5. The node registry and catalog structures are either bounded or explicitly exempted with a reason. They grow with the number of nodes rather than with traffic, which is a different risk, not an absent one

### Requirement 4: Limits Are Never Silent

**User Story:** As a producer, I want to be told when the broker cannot accept my message, not to discover months later that history has a hole in it.

#### Acceptance Criteria

1. When a limit is reached, the **send or publish is refused** rather than the oldest entry being dropped. Under C1.2 a queued message is one nobody has ever processed; discarding it loses the data the queue exists to protect
2. The refusal is a distinct error code, and its class (permanent or transient) is decided and documented — Open Decision 2
3. The client surfaces it as a typed exception naming the queue or channel and the limit that was hit
4. **A refused publish names the group whose queue was full** — Open Decision 5. "The publish failed" is not actionable; "the publish failed because `billing` is full" tells an operator which subscriber to go and fix
5. Where dropping is genuinely correct — the flight recorder, which is diagnostic and explicitly volatile — it stays dropping, and the difference is documented rather than inconsistent
6. Every drop or refusal is **counted and visible** in `HW.STATS`, and recorded by the flight recorder

### Requirement 5: Retention

#### Acceptance Criteria

1. Retention defaults to **100 days**, per C4.1
2. Retention is a *secondary* limit: under C1.2 only unprocessed work is stored, so the byte budget is expected to bind first in an unhealthy system and neither to bind in a healthy one. The documentation says so, so nobody sizes a disk for 100 days of throughput
3. Retention applies per queue — which after 018 means per subscriber group as well — and is configurable per structure where meaningful
4. An entry removed by retention is counted and visible, like any other loss

### Requirement 6: Storage Growth Is Bounded Over Time

**User Story:** As an operator, I want a broker that has been running for a year to still start in a reasonable time.

#### Acceptance Criteria

1. `AofSizeLimit` is configured, so Garnet's checkpoint-on-AOF-size actually runs. Highway sets a checkpoint directory today but never turns this on, so the log grows without limit
2. Compaction is configured, or its absence is a documented decision
3. A test proves the AOF does not grow without bound under sustained traffic
4. Restart recovery time is bounded by the checkpoint interval rather than by total history
5. This requirement is **independent of every other decision here** and is worth doing even if the rest is descoped

### Requirement 7: Conformance

#### Acceptance Criteria

1. `docs/HIGHWAY-PROTOCOL.md` updated: new error code, changed option defaults, any new keys
2. `ProtocolConformanceTests` green
3. `constraints.md` C4.1–C4.6 move to **Met**, and the C4 section stops being the reason the summary reads badly
4. Samples demonstrate a refused send at a limit, and are re-run with a `samples/RUNLOG.md` entry
5. All existing tests pass; `dotnet build` warning-free
6. `new HighwayServerBuilder().Build()` still starts a working broker with no configuration — now a durable one

## Open Decisions

**All five need answering before the design is written.** Recorded rather than guessed, because
each changes the shape of the feature.

### 1. What is the byte budget measured against?

- *Per structure* — simple, no global state, but N queues × 1 GB does not bound the process
- *Server-wide* — actually bounds the process; needs a global accountant and an eviction or refusal policy across unrelated structures

The second is what an operator means by "1 GB of RAM". It is also materially more work.
**018 tilted this further toward server-wide**: every subscriber group is now its own queue with
its own five keys, so the per-structure bound is looser than when this was first written.

### 2. Is a full-queue refusal permanent or transient?

A full queue may drain, which argues transient and retryable — and a client that retries into a
full queue in a tight loop makes it worse. A permanent error forces the application to decide,
which is more honest and less convenient.

### 3. `MaxDeliveryAttempts` off-by-one

`attempts > MaxDeliveryAttempts` permits N+1 deliveries while the name says N; the samples made
this concrete (`attempts 3` under a limit of 2). Change the comparison, or rename to
"redeliveries"? Changing it alters behaviour for anyone who has already tuned the value. Not
strictly part of this feature, but in the same area and it should be settled with it.

### 4. Where does the default data directory live?

Beside the executable is predictable and breaks in read-only deployments. A platform
application-data path is correct and less obvious. A required explicit path is clearest and
violates Requirement 1 AC2.

### 5. What happens to a fan-out when one group's queue is full? **(new — created by 018)**

Publish writes to every registered group **in one transaction** (018 design Decision 2), and
that atomicity is a guarantee: a publish reaches every group or none. Requirement 4 says a full
queue refuses rather than drops. Together, one full group queue fails the publish for **every**
group — a single stuck subscriber takes down the channel for all the healthy ones.

| Option | Cost |
|---|---|
| Refuse the whole publish | One dead consumer blocks a channel for everyone. Honest, and probably intolerable |
| Deliver to the groups that fit | Breaks the atomicity 018 guarantees; "at least once per registered group" becomes conditional |
| Drop the oldest in the full group | Violates C4.3, and loses exactly the unprocessed work the queue exists to protect |
| Refuse, but name the offending group | Same blocking behaviour, but the operator learns *which* subscriber to fix. R4.4 assumes this |

**This is the hardest question in the feature and it has no comfortable answer.** It may need a
per-group circuit breaker — a group repeatedly refusing gets marked unhealthy and skipped, which
trades C2.1 for availability and would need its own constraint. Do not let it be settled by
whichever behaviour happens to fall out of the implementation.

## Non-Goals

- **Encryption at rest.** The AOF and checkpoints stay unencrypted; that is disk encryption's job.
- **Tiered or external storage.** Garnet's storage tier is used as configured; Highway does not add its own spill.
- **Per-message TTL.** Retention stays per structure.
- **Quotas per tenant or per client.** Highway has no tenancy model.
- **Changing the flight recorder.** It is volatile and drop-on-full by design (feature 002), and that stays — R4.5 documents the difference rather than removing it.
- **High availability.** Durability is not failover. `EnableCluster` stays false (`constraints.md` C5).

## Cross-References

- `docs/product/constraints.md` — C4 (the six this closes), C1.2 and C4.3 for why refusal beats dropping, C5 for what durability is not
- `docs/features/018-pubsub-unification/` — the feature that halved Requirement 3 and created Open Decision 5
- `docs/features/014-queue/` — the durability warning this replaces
- `docs/features/013-reliable-delivery/design.md` — dead-letter bounding, the pattern to follow
- `docs/features/002-observability/design.md` — byte accounting that does not cost the write path
