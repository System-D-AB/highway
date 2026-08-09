# Feature: Node Decommissioning — Retiring a Subscriber That Is Not Coming Back

## Introduction

### The hazard this closes

A node that disappears without unsubscribing leaves its subscriber group registered. Publishes
keep fanning into that group's queue and nobody ever claims them.

Before feature 016 this grew without limit — bad, but only for memory. **Feature 016 turned it
into an outage:**

```
node crashes, group stays registered
        │
publishes keep fanning in  ──►  hw:q:{channel}@{group}:q
        │
        ▼
grows to MaxQueueBytes (1 GB)
        │
        ▼
every publish to the CHANNEL is now refused   ← 016 decision 5:
        │                                       a fan-out reaches every
        ▼                                       registered group or none
healthy subscribers stop receiving anything
```

**One dead subscriber takes down a live channel for everyone.** 016's Open Decision 5 named
this cost and accepted it, on the condition that something would retire dead groups. This is
that something.

### The fact that makes it embarrassing

**The broker already knows the node is dead.** Feature 006's heartbeat registry tracks node
liveness and prunes nodes that stop heartbeating. Feature 018 made a subscriber group *be* a
node — `SubscribeGroupAsync(channel, NodeName)`.

So the broker can simultaneously hold "node `shop-3` has missed heartbeats for a week" and keep
faithfully filling `shop-3`'s queue until it blocks the channel. Two facts, same process, never
introduced to each other. **Connecting them is the core of this feature.**

### Why a grace period, and not immediate eviction

The tempting alternative — *stop saving for a subscriber the moment it looks dead, keep what it
already has* — was considered and rejected.

**A crash and a slow restart are indistinguishable in the moment.** A GC pause, a rolling
deploy, a network partition and a genuine crash all look identical for the first seconds. Evict
on suspicion and a 30-second restart loses messages, which breaks C2.3 — the guarantee that
makes durable pub/sub worth having at all.

Every comparable system uses **time** as the discriminator for exactly this reason. So does
this feature.

### How the field solves it

Highway is in the **fan-out-to-per-subscriber-queue** family — NServiceBus/MSMQ, RabbitMQ,
Azure Service Bus, SNS→SQS — rather than the **shared-log** family (Kafka, NATS JetStream,
Pulsar) where a dead consumer costs one stale offset and can never block a producer.

That was the right choice for per-subscriber isolation, and it is the MSMQ lineage the product
positions against. But it obliges Highway to have an eviction story, and every member of the
family has one:

| | Message-level | Subscription-level |
|---|---|---|
| MSMQ / NServiceBus | message TTL | — (operational monitoring) |
| RabbitMQ | `x-message-ttl` | **`x-expires`** — queue deleted after N ms of disuse |
| Azure Service Bus | TTL | **`AutoDeleteOnIdle`** (minimum 5 min) |
| SQS | 14-day maximum retention | — |
| **Highway today** | **none** (C4.1, blocked) | **none** ← this feature |

**Highway's advantage over the two closest analogues.** `x-expires` and `AutoDeleteOnIdle` are
blind timers: they know only "nobody consumed for N minutes", so they cannot distinguish a dead
subscriber from a nightly batch job that consumes once a day. Because a Highway group *is* a
node with a heartbeat, Highway can retire on **evidence** — "the node owning this group has been
gone for N hours" — rather than on inference from a consumption gap.

### What this feature is not

**It is not message retention.** That is C4.1, and it is a different timer doing a different
job: how long an individual message waits. Retention alone does not close the hazard — at 100
days, a dead subscriber's queue sits at its limit blocking a channel for three months. Retiring
the **group** is what unblocks it.

## Requirements

### Requirement 1: A Node Can Say It Is Not Coming Back

**User Story:** As an application, I want to retire cleanly so the broker stops holding messages nobody will ever read.

#### Acceptance Criteria

1. `IHighwayClient.CleanAndByeForever()` retires this node: its subscriber groups, its queues' worker registrations, and its service registrations
2. **The loops stop first.** Stopping the engine before purging is not an optimisation — a running heartbeat re-registers the node moments after the purge and quietly resurrects it
3. In-flight work is drained first, within the existing shutdown timeout, so a message being processed at the moment of retirement is finished rather than abandoned
4. It returns **what it destroyed** — groups retired, queues purged, messages discarded — so an irreversible operation leaves a record
5. The name says what it means. This is not `Dispose`, not `StopAsync`, and it is not idempotent bookkeeping: it destroys data on purpose

### Requirement 2: An Operator Can Say It On The Node's Behalf

**User Story:** As an operator, I want to retire a node that is already gone — which is the common case.

#### Acceptance Criteria

1. `HW.HEARTBEAT <node> BYE PURGE` retires a node that cannot speak for itself
2. It works whether or not the node ever comes back, and is idempotent: retiring a node twice is not an error
3. It returns what it destroyed, like R1.4
4. Retiring an unknown node returns zero rather than an error — an operator cleaning up after an incident should not have to know which names still exist

### Requirement 3: The Broker Retires Dead Groups By Itself

**User Story:** As an operator, I want a node that crashed and never returned to stop blocking my channel, without me noticing first.

**The core of the feature.** R1 and R2 need someone to act. This one does not.

#### Acceptance Criteria

1. A subscriber group whose owning node has been **absent from the heartbeat registry longer than a configurable threshold** is retired automatically: the group is unregistered and its queue deleted
2. The default threshold is **generous** — comfortably longer than any deploy, restart or maintenance window — because the cost of retiring too early (a restart loses its backlog, breaking C2.3) is far worse than the cost of retiring too late (a channel stays blocked a while longer)
3. Retirement is driven by **liveness evidence, not consumption gaps**. A group that has not been consumed from is not dead; a group whose node has not heartbeated is. Highway can tell the difference and must use it
4. Retirement is **counted, logged at Warning, and recorded by the flight recorder**. C4.3's rule — reaching a limit is never silent — applies at least as strongly to discarding a whole subscriber's backlog
5. A node that returns after retirement re-subscribes and **starts empty**. That is C2.4 working as intended, not a defect, and it is documented so nobody reports it as one
6. The sweep must not be a timer per group. Whatever drives it, the cost of a broker with a thousand idle groups must be bounded and stated

### Requirement 4: The Three Verbs Are Retired Differently

**User Story:** As an operator, I want retirement to destroy what is genuinely mine to destroy, and nothing else.

#### Acceptance Criteria

1. **Subscriber groups (pub/sub): the queue is deleted.** Those messages were addressed to this subscriber alone; nobody else can process them and the subscriber has declared it will never exist again
2. **Queues (`SendAsync`): the node's *processing list* is requeued, not deleted.** A competing consumer shares its queue with others — work it had claimed belongs to the queue, not to the node, and another worker will take it
3. **RPC: unacknowledged requests are requeued, never deleted.** A caller may still be waiting on a reply; destroying its request converts a slow answer into no answer
4. The asymmetry is documented where an operator will meet it, because "retire" destroying data in one verb and preserving it in another is surprising unless it is explained

### Requirement 5: Retirement Unblocks The Channel

**User Story:** As an operator whose channel is refusing publishes, I want retiring the dead subscriber to fix it.

#### Acceptance Criteria

1. Deleting a retired group's queue releases its byte budget, so publishes to that channel succeed again
2. A test proves the whole loop: fill a group's queue until publishes are refused, retire the group, assert publishes succeed and the surviving groups are unaffected
3. That test is the feature's reason to exist. Without it, R1–R3 are plumbing whose purpose is unverified

### Requirement 6: Conformance

#### Acceptance Criteria

1. `docs/HIGHWAY-PROTOCOL.md` updated: the `BYE PURGE` form, any new keys, the retirement recorder event
2. `ProtocolConformanceTests` green
3. `constraints.md`: C2.3 gains its limit — "a subscriber that is down receives what it missed, **until its node is declared gone**" — and C4.7's hazard note records that automatic retirement is now its mitigation
4. Samples demonstrate a node retiring and a channel recovering, re-run with a `samples/RUNLOG.md` entry
5. All tests pass; `dotnet build` warning-free

## Open Decisions

**Answer before the design is final.**

### 1. Does retirement delete the backlog, or dead-letter it?

- *Delete* — what RabbitMQ (`x-expires`) and Azure (`AutoDeleteOnIdle`) do. Reclaims the budget, which is the entire point. Irreversible.
- *Dead-letter it* — the messages survive for inspection, but the bytes are only reclaimed if the dead-letter list is itself bounded and eventually trimmed, so **the hazard is not actually fixed**.
- *Delete, but record the count and the channel* — irreversible, with enough left behind to know what happened.

**Recommendation: the third.** Preserving a gigabyte of messages addressed to a subscriber that has declared it will never exist is preserving them for nobody. But an operator must be able to answer "what did we lose?" afterwards.

### 2. What is the default idle threshold?

Long enough to survive any deploy, restart or maintenance window; short enough that a channel is not blocked for a quarter. **Recommendation: 24 hours**, on the grounds that a subscriber absent for a full day is not restarting.

The asymmetry matters: retiring too early **loses messages a live subscriber would have processed** — a correctness failure. Retiring too late leaves a channel blocked — an availability failure that an operator can also fix by hand. When in doubt, wait longer.

### 3. Does automatic retirement apply to service registrations too, or only subscriber groups?

Only groups cause the hazard: a dead RPC service registration wastes a little memory and is already pruned by feature 006's heartbeat timeout. Extending automatic retirement to services adds blast radius for no problem. **Recommendation: groups only**, with R1/R2's explicit paths covering everything.

### 4. Is there a grace state between "absent" and "retired"?

A group could be marked *suspect* — visible in `HW.STATS` and the dashboard — before being retired, so an operator sees it coming. More surface and more state; more warning. **Recommendation: no new state, but the flight recorder gets a `NodeSuspect` event** when a group's node passes half the threshold, so it is visible in a replay without a state machine to maintain.

## Non-Goals

- **Message retention (C4.1).** A different timer for a different job; it needs a timestamp in the entry framing and belongs with the next breaking change.
- **Reviving a retired group's backlog.** Retirement is irreversible by design; a node that returns starts empty (R3.5).
- **Consumption-based idle detection.** Highway has liveness evidence and should use it (R3.3); a blind idle timer would retire the nightly batch job that RabbitMQ and Azure cannot distinguish.
- **High availability.** Retiring a node is not failover (`constraints.md` C5).
- **Automatic retirement of queues (`SendAsync`).** A queue is shared by competing consumers, so one node leaving is not the queue ending.

## Cross-References

- `docs/product/constraints.md` — C2.3 (the guarantee this bounds), C4.3 (losses are never silent), C4.7 (the byte budget whose hazard this mitigates)
- `docs/features/016-retention-and-durability/` — Open Decision 5, which accepted the blocking cost on the condition this feature exists
- `docs/features/018-pubsub-unification/design.md` — the atomic fan-out that makes one full group queue a channel-wide problem
- `docs/features/006-heartbeat-service-registry/` — the liveness evidence this feature finally connects to subscriptions
