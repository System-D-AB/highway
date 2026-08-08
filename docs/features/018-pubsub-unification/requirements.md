# Feature: Pub/Sub Unification — One Delivery Engine, Two Verbs

> **Roadmap position: before 016.** The number is creation order; the ordering that matters is
> that this **deletes the group queues 016 was going to bound**. Running retention first means
> building byte budgets and eviction for a structure that is about to disappear, then deleting
> them. See [Sequencing](#sequencing).

## Introduction

Highway has three verbs and **two** delivery engines. The queue (014) and pub/sub (004) each
implement claim, lease, redelivery, attempt counting, dead-lettering and deferred delivery —
independently.

| | lines |
|---|---|
| Pub/Sub — `HW.PUBLISH`, `HW.SUBSCRIBE`, `HW.UNSUBSCRIBE`, `HW.RECEIVE`, `HW.RACK`, `ChannelConsumerLoop` | **1,234** |
| Queue — `HW.QSEND`, `HW.QCLAIM`, `HW.QACK` | **470** |

`HW.RECEIVE` alone is **481 lines — larger than the entire queue implementation** — because it
does batch receive *plus* delayed promotion *plus* a lease sweep *plus* dead-lettering *plus*
group processing entries. Every one of those is a second copy.

### The cost is not the lines. It is that every feature is built twice.

This is not a prediction. It is the last three features:

| Feature | What the duplication cost |
|---|---|
| **013** | Unbounded redelivery was found living in **three** independently written requeue paths — `HwDequeueCommand`, `HwReceiveCommand`, `RequeueNodeWork`. One bug, three fixes. |
| **014 T2** | The shared lease sweep had to be extracted *before* the queue could be built, specifically to stop a fourth copy appearing. |
| **015** | The failure block was silently dropped at **three** re-encode sites. `HW.RECEIVE` was one, precisely because it re-encodes entries its own way. Caught only by a two-worker test. |
| **016** (unstarted) | C4.4 requires byte budgets on every queue-like structure. Group queues and queues are two jobs because they are two implementations. |

Four consecutive features. That is a pattern, not bad luck.

### What this feature does

**A durable subscription stops resembling a queue and becomes one.** `PublishAsync` fans out
into one queue per registered group; subscribers consume with the same commands and the same
worker loop as `[Queue]`. Pub/sub keeps its identity as a *verb* — publish once, every
subscriber gets a copy — and loses its private *engine*.

### What this feature does not do

**It does not remove durability.** That was considered and rejected. Reliable fan-out across a
restart is what distinguishes a broker from raw Redis pub/sub. Without it, an `OrderPlaced`
event is lost whenever the billing service is mid-deploy, and the only remedy is for the
publisher to `SendAsync` into each consumer's queue by hand — which means **the publisher must
know its subscribers**, destroying the decoupling pub/sub exists to provide. It would also
retract `product.md`'s MSMQ positioning, where reliable publish-subscribe is the core case.

## Requirements

### Requirement 1: Publish Is Fan-Out Into Group Queues

**User Story:** As a developer, I want `PublishAsync` to behave exactly as it does today, while the machinery underneath becomes the queue's.

#### Acceptance Criteria

1. `HW.PUBLISH` resolves the channel's registered groups and enqueues one message into **each group's queue**, in a single transaction, exactly as it writes to each group queue today
2. Each group's queue is a **real queue** — the same keys, framing, lease sweep, attempt count, dead-letter list and deferred-delivery machinery `HW.QSEND` uses. Not a parallel structure that resembles one
3. A subscriber consumes with `HW.QCLAIM` / `HW.QACK`. `HW.RECEIVE` and `HW.RACK` are **removed**
4. Fan-out remains atomic: a publish either reaches every registered group or none. A partial fan-out would make "at least once per group" false in a way no consumer could detect
5. `ChannelResponse` still reports how many groups received the message

### Requirement 2: One Implementation, Not Two

**User Story:** As a maintainer, I want a delivery change to be made once.

#### Acceptance Criteria

1. These are **deleted**, not deprecated:
   - `HwReceiveCommand` (481 lines) and `HwRackCommand` (124)
   - `ChannelConsumerLoop` (179) — `QueueWorkerLoop` replaces it
   - `Envelope.EncodeChannelEntry` / `DecodeChannelEntry` and `EncodeGroupProcessingEntry` / `DecodeGroupProcessingEntry`
   - The group-specific lease sweep, dead-letter and delayed-promotion paths
2. `Envelope` is left with **two** framings — queue entry and processing entry — down from four. Each removed framing is one fewer place for the next feature to forget, which is exactly how 015's failure block was lost
3. `HW.FAIL`'s `CH` target collapses into `Q`, because a group **is** a queue. The `SVC|Q|CH` grammar becomes `SVC|Q`
4. `HW.DLQ`'s `CH` target likewise collapses into `Q`
5. **No new abstraction is introduced to share the code.** The point is deletion. If this feature ends with a `IDeliveryEngine` interface and two implementations behind it, it has failed and should be reverted

### Requirement 3: The Guarantees That Survive Unchanged

**User Story:** As someone who depends on pub/sub today, I want my delivery semantics to be the same afterwards.

#### Acceptance Criteria

1. **C2.1** — at-least-once to every group registered at publish time. Preserved: it becomes at-least-once queue delivery, per group
2. **C2.2** — acknowledged means gone. Preserved as `HW.QACK`
3. **C2.3** — a subscriber that is down receives what it missed. Preserved: its queue holds the work
4. **C2.4** — not a store for messages nobody has subscribed to. Preserved: no group, no queue, no copy
5. **C2.5** — not a replayable log. Preserved
6. **Competing consumers within a group.** Two instances of the same subscriber share the group's work rather than each getting a copy — the same competition the queue already provides, and the same behaviour pub/sub has today
7. `[Idempotent]`, dead-lettering, `HW.DLQ REQUEUE` and deferred publish all keep working, because they are the queue's and the queue is what runs now

### Requirement 4: The Client Surface Does Not Move

**User Story:** As a developer, I want to change nothing in my application.

#### Acceptance Criteria

1. `IPublish`, `ISubscribe<T>`, `PublishAsync(message)` and `PublishAsync(message, delay)` are **unchanged**. This is an engine swap, not an API change
2. `[Subscribe]` group registration is unchanged
3. An application that compiles today compiles afterwards, with no source edit
4. The three verbs stay three verbs. A developer must not have to learn that publish is "really" a send — that is an implementation fact, and leaking it would trade the product's simplicity for the maintainer's convenience

### Requirement 5: Three Semantic Changes, Stated Rather Than Discovered

**User Story:** As someone upgrading, I want the behaviour changes named, not left for me to find in production.

These are the parts a line count does not show. Each is a real difference.

#### Acceptance Criteria

1. **Batch consumption is lost.** `HW.RECEIVE` returns many messages per round trip; `HW.QCLAIM` returns one. For a high-fan-out channel this is more round trips. **No speculative batching is added** — see Open Decisions 2. Whatever is decided must be measured, not assumed
2. **Subscriber ordering must be preserved by default.** `ChannelConsumerLoop` dispatches sequentially; `QueueWorkerLoop` has a concurrency gate. Group workers therefore default to **concurrency 1**, preserving today's per-group ordering. A developer may raise it deliberately and trade ordering away — the same explicit trade `PubSubBackoffEnabled` already makes
3. **Deferred publish resolves its groups at publish time**, not at promotion time. `PublishAsync(msg, delay)` fans out immediately into each group's queue with a deferred delivery time. A group registering *during* the delay does not receive it. This matches C2.1's wording ("registered at publish time") and C2.4, and it is simpler than a channel-level delayed set that must re-resolve groups on promotion
4. Every one of these appears in `docs/HIGHWAY-PROTOCOL.md`'s changelog and in `constraints.md`, not only in this file

### Requirement 6: A Breaking Change, Handled Honestly

**User Story:** As an operator, I want to be told what breaks rather than discovering it after an upgrade.

#### Acceptance Criteria

1. Protocol **4.0**. This removes two commands and two entry framings — the definition of major
2. **Existing channel data becomes unreachable.** Group queues, group processing lists, group dead letters and the channel delayed set are all in key shapes that no longer exist
3. A broker started against a data directory containing pre-018 channel keys **says so explicitly at startup**, naming the keys and the remedy, rather than silently serving an empty channel. Feature 013's `HW_STORAGE_FORMAT` precedent: refuse and explain beats misparse or silence
4. The remedy is documented: drain channels with the previous version, or delete the data directory
5. `product.md`, `roadmap.md` and `constraints.md` are updated in this feature, not after it

### Requirement 7: Conformance

#### Acceptance Criteria

1. `docs/HIGHWAY-PROTOCOL.md`: `HW.RECEIVE` and `HW.RACK` removed from the Command Index; two framings removed; `HW.FAIL` and `HW.DLQ` grammars narrowed to `SVC|Q`; version 4.0 with a changelog entry naming all three semantic changes
2. `ProtocolConformanceTests` green — it parses the Command Index in both directions, so a removed-but-still-registered command fails, as does the reverse
3. `constraints.md`: C2.1–C2.5 restated in queue terms; **C4.4's group-queue row disappears** because the structure does; C7 unaffected
4. Every existing pub/sub integration test passes **unchanged where it tests behaviour**, and is rewritten only where it names `HW.RECEIVE` or `HW.RACK` directly. A behavioural test that needs changing means the behaviour changed and must be justified against Requirement 5
5. Samples re-run across real processes with a `RUNLOG.md` entry; the pub/sub scenarios must behave identically
6. All tests pass; `dotnet build` warning-free

## Sequencing

```
NOW    015 done
       |
018 ──►│  deletes group queues, group DLQ, group delayed set
       │  Envelope: 4 framings -> 2;  commands: 18 -> 16
       ▼
016    Retention & durability
       bounds ONE queue structure instead of two
       C4.4's "pub/sub group queues: no bound at all" row is already gone
       |
       ▼
017    Node decommissioning
       one structure to retire, not two
```

Doing 016 first means building byte accounting, eviction and refusal for group queues, then
deleting all of it weeks later. That is the expensive order, and it is the same reasoning that
put 014 before 016 in the first place.

## Open Decisions

**Answer before the design is final.** Recorded rather than guessed, because each changes the shape.

1. **How is a group's queue named?**
   - *Derived name* — `{channel}@{group}` in the existing `hw:q:` key space. One key space, no new grammar, `HW.QCLAIM`/`HW.QACK`/`HW.DLQ`/`HW.STATS` work untouched. Requires **forbidding `@` in user-declared queue and channel names**, or a channel called `a@b` collides with a queue.
   - *Separate key space* — keep `hw:ch:{channel}:grp:{group}:*`, share the implementation by parameterising it on keys (the lease sweep already works this way). No naming rule, but every queue command needs a target grammar like `HW.DLQ`'s, which adds the surface this feature exists to remove.
   - **Recommendation: derived name.** The naming rule costs nobody anything; the alternative reintroduces per-family branching into commands that are currently simple.

2. **Does anything replace batch receive?**
   - *Nothing* — one claim per round trip, as the queue does today. Simplest; unmeasured cost on high-fan-out channels.
   - *Optional `COUNT` on `HW.QCLAIM`* — restores batching for **both** verbs, which is arguably a queue improvement independent of this feature.
   - **Recommendation: nothing, then measure.** Highway has no throughput benchmark at all (`constraints.md` C5), so adding batching now would be optimising against a number nobody has. If a benchmark shows it matters, `COUNT` is a small additive change afterwards.

3. **What does `HW.STATS CH:name` report now?**
   The channel is no longer a structure — it is a group set plus N queues. Report the aggregate across its groups, or redirect to per-queue stats? Aggregate is friendlier and hides the change; per-queue is honest and makes the dashboard's job obvious.

4. **Does `HW.SUBSCRIBE` still exist?**
   It registers a group. Under unification that is "create a queue and record it against the channel". It probably stays, doing less — but if it becomes a pure alias for queue creation, deleting it is one more command gone. Decide with the design.

## Non-Goals

- **Removing pub/sub durability.** Requirement 3. Considered as the alternative and rejected in the introduction.
- **Transient/ephemeral subscriptions.** A "notify only the connected" mode is a real feature (cache invalidation, presence) but it is a *second* delivery model, and adding one while removing another in the same change is how both get done badly. Revisit once this has shipped.
- **A shared `IDeliveryEngine` abstraction.** Requirement 2.5. The goal is one implementation, not one interface over two.
- **Changing the three verbs.** Requirement 4.4.
- **Exactly-once, ordering guarantees, or priority.** Unchanged non-goals (`constraints.md` C5).

## Cross-References

- `docs/product/constraints.md` — C2 (the guarantees preserved), C4.4 (the row this deletes), C5 (ordering under backoff, the precedent for R5.2)
- `docs/features/014-queue/design.md` — the engine that survives; T2 is the precedent for deleting a second copy rather than maintaining it
- `docs/features/015-recoverability/tasks.md` — T5's finding: the failure block dropped at three re-encode sites, one of them `HW.RECEIVE`
- `docs/features/013-reliable-delivery/` — dead letters and deferred delivery, inherited rather than reimplemented
- `docs/features/016-retention-and-durability/requirements.md` — the feature this unblocks and shrinks
- `docs/HIGHWAY-PROTOCOL.md` — version 3.0's backlog removal, the precedent for a major break that deleted rather than added
