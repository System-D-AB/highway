# Design: Pub/Sub Unification

## The shape of the change

```
BEFORE — two engines

  PublishAsync ──► HW.PUBLISH ──► hw:ch:{ch}:grp:{g}:q      ─┐
                                  hw:ch:{ch}:grp:{g}:proc    │  engine A
                                  hw:ch:{ch}:grp:{g}:dlq     │  (1,234 lines)
                                  hw:ch:{ch}:delayed         │
                   HW.RECEIVE ◄── batch + promote + sweep    │
                   HW.RACK    ◄── ack                       ─┘

  SendAsync ─────► HW.QSEND   ──► hw:q:{q}:q                ─┐
                                  hw:q:{q}:proc:{node}       │  engine B
                                  hw:q:{q}:dlq               │  (470 lines)
                                  hw:q:{q}:delayed           │
                   HW.QCLAIM  ◄── claim + promote + sweep    │
                   HW.QACK    ◄── ack                       ─┘


AFTER — one engine, two verbs

  PublishAsync ──► HW.PUBLISH ──┐
                                ├─► hw:q:{name}:q           ─┐
  SendAsync ─────► HW.QSEND   ──┘    hw:q:{name}:proc:{node}  │  one engine
                                     hw:q:{name}:dlq          │  (470 lines)
                                     hw:q:{name}:delayed      │
                   HW.QCLAIM  ◄───── claim + promote + sweep  │
                   HW.QACK    ◄───── ack                     ─┘

  where name = "{queue}"            for SendAsync
        name = "{channel}@{group}"  for PublishAsync
```

**The only thing `HW.PUBLISH` still does that `HW.QSEND` does not is fan out.** Everything after
the fan-out is the queue.

## Decision 1 — A group's queue is named `{channel}@{group}`

Open Decision 1, resolved.

`hw:q:orders.placed@billing:q` is a queue in every respect: same key shape, same framing, same
sweep, same dead-letter list, same `HW.DLQ` target, same `HW.STATS` form. No command learns a
second target grammar.

**`@` becomes reserved** in user-declared queue and channel names. Without that, a queue
literally named `orders.placed@billing` collides with the `billing` group of the
`orders.placed` channel. The rule is enforced in two places, because there are two ways in:

| Where | When | Failure |
|---|---|---|
| `[Queue("...")]` / `[Subscribe("...")]` scanning | application startup | throws, naming the attribute and the character |
| `HW.QSEND` / `HW.PUBLISH` / `HW.SUBSCRIBE` identifier validation | command | `HW_INVALID_ARG`, naming the character |

Client-side alone is not enough — Highway's protocol is open and a non-Highway client can
issue `HW.QSEND` directly. Server-side alone is a startup-time problem discovered at runtime.
Both, or the guarantee is not one.

**Rejected alternative: keep `hw:ch:…:grp:…` keys and parameterise the implementation on key
names.** The lease sweep already works that way, so it would function. But then `HW.QCLAIM`,
`HW.QACK`, `HW.DLQ`, `HW.STATS` and `HW.FAIL` each need a `Q | CH` target grammar — which is
precisely the per-family branching this feature exists to delete. It trades a naming rule for
five commands' worth of surface.

## Decision 2 — Fan-out stays inside one transaction

`HW.PUBLISH` already reads the channel's group list in `Prepare` and locks every group queue
before writing. That machinery does not change; only the key it derives does.

```
Prepare                                  Main
───────                                  ────
read  hw:ch:{ch}:grplist  (main store)   INCR hw:ch:{ch}:seq
      └─ the 004.1 mirror key: a         for each group g:
         main-store string, NOT the        RPUSH hw:q:{ch}@{g}:q  <entry>
         object-store set, because
         reading an object structure     Finalize
         in Prepare registers a watch    ────────
         that later exclusive locks      ring hw:door:q:{ch}@{g}
         fail against (004.1)            record ChannelPublished

lock  hw:ch:{ch}:seq            X main
      hw:ch:{ch}:grplist        X main
      hw:q:{ch}@{g}:q           X object   ← for each group
      hw:q:{ch}@{g}:delayed     X object   ← only when deferred
```

**This is the constraint that shapes the whole design**, and it is already satisfied: every key
touched in `Main` is derivable from the command's arguments plus the mirror key, so every key
can be declared in `Prepare`. Garnet rejects touching an undeclared key — a wall hit in 013,
014 and 015.

**Atomicity is preserved for free.** One transaction, N pushes: a publish reaches every
registered group or none. A partial fan-out would make "at least once per group" false in a way
no consumer could detect.

### The one new risk: N locks

A channel with 50 groups locks 50 queue keys in one transaction. That is true today as well —
today's `HW.PUBLISH` locks 50 group queues — so this feature does not make it worse. It is
recorded here because it is the natural place to look for it, and because 016's byte accounting
will touch the same path.

## Decision 3 — Deferred publish fans out at publish time

`hw:ch:{channel}:delayed` is deleted. `PublishAsync(msg, delay)` writes into each group's
`hw:q:{ch}@{g}:delayed` with the same `AT <ticks>` argument `HW.QSEND` already takes.

```
today                                    after
─────                                    ─────
publish(delay) ─► hw:ch:{ch}:delayed     publish(delay) ─► hw:q:{ch}@{g1}:delayed
                        │                                  hw:q:{ch}@{g2}:delayed
                  (groups resolved                          (groups resolved NOW)
                   at promotion)
```

**Semantic change, called out in R5.3.** A group registering *during* the delay used to be
included when the message was promoted; now it is not. This matches C2.1's own wording —
"every group registered **at publish time**" — and C2.4, which says pub/sub is not a store for
messages nobody has subscribed to. The old behaviour was the odd one out.

## Decision 4 — Group workers default to concurrency 1

`ChannelConsumerLoop` dispatches a batch sequentially and has no concurrency gate.
`QueueWorkerLoop` has one, defaulting to `WorkerConcurrency` (8).

Swapping the loop without swapping the default would silently parallelise every existing
subscriber and reorder messages within a group. So:

```csharp
// A group's worker inherits the channel's ordering expectation, not the queue's throughput one.
var concurrency = descriptor.IsSubscription ? 1 : options.WorkerConcurrency;
```

Raising it is a deliberate act with a documented trade — ordering for throughput — which is the
same explicit trade `PubSubBackoffEnabled` already offers (`constraints.md` C5, "ordering under
backoff").

**Why not preserve batching too?** Because nobody has measured it. Highway has no throughput
benchmark at all, so adding a `COUNT` argument now would optimise against a number that does not
exist. If a benchmark later shows it matters, `COUNT` on `HW.QCLAIM` is additive and improves
**both** verbs.

## The delivery path, end to end

```
Publisher                Server                          Subscriber node
─────────                ──────                          ───────────────
PublishAsync(OrderPlaced)
   │
   ├─ resolve channel from [Publish] attribute
   ▼
HW.PUBLISH orders.placed <envelope>
   │
   ├─ Prepare: read grplist -> [billing, shipping]
   │           lock both group queues
   │
   ├─ Main:    RPUSH hw:q:orders.placed@billing:q
   │           RPUSH hw:q:orders.placed@shipping:q
   │
   └─ Finalize: ring hw:door:q:orders.placed@billing
                ring hw:door:q:orders.placed@shipping
                                                    │
                                                    ▼
                                          QueueWorkerLoop wakes
                                             │
                                             ├─ gate (concurrency 1)
                                             ├─ HW.QCLAIM orders.placed@billing node-1
                                             │     └─ promotes deferred, sweeps leases
                                             ├─ ISubscribe<OrderPlaced>.SubscribeAsync(...)
                                             │     └─ throws? -> HW.FAIL Q ... (015)
                                             └─ HW.QACK orders.placed@billing node-1 <id>
```

Every box after `HW.PUBLISH` is code that already exists and is already tested.

## Deletion inventory

The feature is measured by what is gone.

| Deleted | Lines | Replaced by |
|---|---|---|
| `HwReceiveCommand` | 481 | `HwQClaimCommand` |
| `HwRackCommand` | 124 | `HwQAckCommand` |
| `ChannelConsumerLoop` | 179 | `QueueWorkerLoop` |
| `Envelope.Encode/DecodeChannelEntry` | ~35 | `Encode/DecodeRpcEntry` |
| `Envelope.Encode/DecodeGroupProcessingEntry` | ~40 | `Encode/DecodeRpcProcessingEntry` |
| `HighwayKeys.GroupQueue/GroupProcessing/GroupDeadLetter/GroupRetry/ChannelDelayed` | ~25 | `Queue/QueueProcessing/QueueDeadLetter/QueueDelayed` |
| `CH` branch in `HwDlqCommand` | ~40 | `Q` branch |
| `CH` branch in `HwFailCommand` | ~20 | `Q` branch |
| **Total** | **~944** | |

`HwPublishCommand` shrinks (no channel entry framing, no channel delayed set) rather than
disappearing — fan-out is the verb's remaining reason to exist.

**Commands: 18 → 16. Entry framings: 4 → 2.**

That second number is the one that matters. 015's failure block was lost because it had to be
carried across four framings and was carried across three. Halving the framings halves that
class of bug permanently.

## Error handling and edge cases

| Case | Behaviour | Why |
|---|---|---|
| Publish to a channel with no groups | `:0`, nothing stored | C2.4 unchanged — the current behaviour |
| A group registered mid-publish | Included or not, atomically; never half | The group list is locked in `Prepare` |
| `@` in a declared queue or channel name | Rejected at scan **and** at command | Decision 1; both, or it is not a guarantee |
| Pre-018 channel keys present at startup | **Refuse to start**, naming the keys and the remedy | R6.3 — 013's `HW_STORAGE_FORMAT` precedent: refusing beats misparsing, and both beat silence |
| Subscriber handler throws | Not acknowledged, redelivered, eventually dead-lettered with context | 013 + 015, inherited unchanged |
| Group's queue exceeds its bound | Whatever 016 decides — **one** decision now, not two | The point of sequencing this first |

### The pre-018 data check

A broker that silently serves an empty channel because its data is in keys nobody reads any
more is the worst available outcome: the application looks healthy and loses every event.

```
startup ─► scan for any key matching hw:ch:*:grp:*
             │
             ├─ none  ─► start normally
             │
             └─ found ─► refuse, naming the key count and:
                         "Channel data from before the 018 unification is present.
                          Drain these channels with the previous version, or delete
                          the data directory. Refusing rather than starting with
                          data that would never be delivered."
```

Cost is one `SCAN` at startup against a pattern that matches nothing on a clean broker.

## What this design deliberately does not do

**No `IDeliveryEngine`.** R2.5. If two implementations end up behind one interface, nothing was
deleted — the duplication was merely given a lid, and the next feature still has to be written
twice. The success condition is that `HwReceiveCommand.cs` no longer exists.

**No transient subscription mode.** A second delivery model added in the same change that
removes one is how both get done badly. It is a real feature; it is a later one.

**No change to the three verbs.** `PublishAsync` stays `PublishAsync`. That publish is
implemented as fan-out is a fact about the server, and a developer who has to know it has been
handed the maintainer's problem.

## Cross-References

- `docs/features/004.1-server-remediation/` — the mirror-key rule this design depends on
- `docs/features/014-queue/design.md` — the engine being reused; T2's shared lease sweep
- `docs/features/015-recoverability/tasks.md` — T5, the three-of-four re-encode sites
- `docs/HIGHWAY-PROTOCOL.md` — 3.0's backlog removal, the precedent for deleting a guarantee's machinery cleanly
