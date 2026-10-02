# Pub/Sub

> **Status:** current
> **Protocol:** the *Pub/Sub Commands* section of [`docs/HIGHWAY-PROTOCOL.md`](../HIGHWAY-PROTOCOL.md)
> is authoritative for `HW.PUBLISH`, the subscriptions `HW.SESSION` declares, and `HW.UNSUBSCRIBE`. Guarantees are C2 in
> [`constraints.md`](../product/constraints.md). This doc explains how pub/sub works and why.

*Highway pub/sub is durable fan-out: a publish reaches every subscriber group registered at publish
time, atomically — all groups or none — and each group holds its copy until it acknowledges, so no
subscriber silently misses a message.*

## Overview

You declare a message POCO implementing `IPublish` with a `[Channel("name")]` attribute and a
subscriber implementing `ISubscribe<T>`; publishers call
`await client.PublishAsync(new OrderCreated { ... })`. The contrast with a queue is the delivery
unit: run three instances of a **subscriber group** and each *group* gets its own copy of every
message, whereas three instances of a queue handler *share* the work.

The subscription **group** — not the node — is the unit of fan-out. Its default is one group per
node, so absent any configuration each node is its own subscriber; a shared `SubscriptionGroup`
name makes several replicas one logical subscriber.

## How it works

**Every group is a queue.** A channel's fan-out is built directly on the queue engine: each
subscriber group has its own derived queue named `{channel}@{group}`, with the same lease,
acknowledgement, attempt counter and dead-letter list as any other queue (`@` is reserved in
identifiers for this). This is why pub/sub inherits durability, competing-replica consumption and
dead-lettering for free — there is one delivery engine underneath, two verbs on top. Dead letters for a subscriber group live in the group's dead-letter space, addressed as `Q {channel}@{group}` on `HW.DLQ` and `q:{channel}@{group}` for dead-letter feeds (`IDeadLetter<T>`). Retry policies declared via `[Retry]` apply per group queue, and requeuing a dead letter restores it to that specific group's queue.

**Fan-out is atomic and per-group.** `HW.PUBLISH` enqueues into *every* registered group's queue in
one commit, holding every group's lock — all groups or none, so a crash never leaves a partial
delivery, and a publish can never race a claim on the same group (3.0; before it, a publish and a
claim took different locks and could, in the worst case, overwrite a message). "Delivered" means
delivered to every group, never "delivered to whoever grabbed it first": first-acknowledgement-wins
would let a fast subscriber starve a slow one, which is not fan-out. Within a group, replicas
**compete** through that group's single queue, so each message is processed once per group by
whichever replica claims it, and `[Idempotent]` markers are group-scoped.

**Absence is held, not lost — within a bound.** A registered group's queue holds every publish while
its subscriber is away, so a restart or deploy loses nothing. The holding is bounded by the byte
budget and by **retirement**: a group whose every backing node has been absent from the heartbeat
registry past a threshold (24 h default) is retired, and retirement is never silent — it logs,
records an event, and reports what it destroyed. See
[registry-and-heartbeat.md](registry-and-heartbeat.md).

**Deferred publish** uses an absolute future time, so a restart can't re-delay it; a group
registering during the delay is resolved at publish time and does not receive a message aimed before
it existed. A deferred copy counts against its group's byte budget like any other (3.0).

**Subscriptions are part of the session** (3.0). A node declares the groups it subscribes through in
`HW.SESSION`, with its catalog and job schedules, and re-declares them whenever the broker answers a
heartbeat with `+SESSION` or the client follows a failover. Before 3.0 a subscription was sent once at
start, so a broker that lost it (a wipe, a fresh node) kept answering while every publish reached
nobody, silently, until the subscriber restarted.

**A session's subscriptions are the node's complete set** (3.0, feature 070). A group the node no longer
declares loses it as a member. When that node was the group's last member the group **drains**: publishes
skip it, so it receives nothing new and — however full its queue — can never refuse a publish to the
channel; its backlog is kept and it is retired by the first publish after the retirement threshold (never, if retirement is disabled), unless a node declares it
again first, which resumes it with that backlog. Messages published while it drains are **not** kept for
it: a resumed group has its old backlog and everything published after the re-declare, never the gap. This closes a 2.x trap: subscriptions were add-only, so
a node redeployed without a subscriber stayed a member while it heartbeated, its group filled, and the
whole channel was then refused. Draining is logged at Warning and visible as `draining` in `HW.STATS`.

## Wire surface

`HW.PUBLISH` (immediate or deferred), `HW.SESSION` (whose `subscriptions` register a group and the
node backing it) and `HW.UNSUBSCRIBE` are defined in [`docs/HIGHWAY-PROTOCOL.md`](../HIGHWAY-PROTOCOL.md).
Subscribers *consume* through the work commands (`HW.CLAIM Q`, `HW.ACK <token>`) on the derived
`{channel}@{group}` queue — there is no separate receive command.

## Guarantees & limits

- **At-least-once per group registered at publish time** (C2.1); fan-out is atomic across groups.
- **Acknowledged means gone** (C2.2); storage tracks only undelivered work.
- **A down subscriber receives what it missed, until its group is retired** (C2.3) — the bound is
  evidence-based (heartbeat absence), not a blind idle timer.
- **Not a store for absent subscribers** (C2.4): a publish with no registered group reaches nobody,
  and a group registering later starts empty. "Hold until someone can handle it" is a queue.
- **Not a replayable log** (C2.5): joining an active channel does not replay prior traffic; Highway
  keeps no consumer offsets.
