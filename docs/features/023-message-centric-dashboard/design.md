# Design: Messages, Not Protocol Events

> **Four decisions, answered.**
>
> | | Decision | Chosen |
> |---|---|---|
> | 1 | Where correlation happens | **Server.** The browser renders; it never computes |
> | 2 | Correlation key | **Message id, scoped by entity.** Trace context is a later enrichment |
> | 3 | Retained window | **Stated, not extended.** A longer index is registered, not built |
> | 4 | Relationship to 022's entity page | **Extends it.** One page answers one question |

## The shape of the change

```
BEFORE — the transport                    AFTER — the traffic

8:35:47  QueueSent                        inv-8821   processed   47ms   ✓
8:35:47  QueueClaimed      order-svc-1    inv-8822   failed      —      ✗ InvalidOperationException
8:35:47  QueueAcknowledged order-svc-1
8:35:52  QueueSent                        ── open one ─────────────────────────
8:35:52  QueueClaimed      order-svc-1     sent      8:35:47.201  shop-1
8:35:52  QueueAcknowledged order-svc-1     claimed   8:35:47.209  order-svc-1  (+8ms queued)
                                           processed 8:35:47.248  order-svc-1  (+39ms)
two messages, six rows,                    body      {"OrderId":"ORD-77", …}
none of them a thing a
developer did
```

## Decision 1 — The server aggregates; the browser renders

**This is the architectural answer to "the dashboard is becoming an application".** It is
becoming one. The question is where its logic lives.

```
browser correlates                     server correlates
──────────────────                     ─────────────────
fetch every event to group them        one scan over buffers it already owns
reimplement "acknowledged = success"   one implementation of protocol semantics
gets slower as traffic grows           bounded by the recorder, which is bounded
untestable without a browser           ordinary server code with ordinary tests
```

The rule, stated so it can be enforced: **the dashboard never computes what the server can.**
It survived 022's queue-layout question (the browser must not learn `HighwayKeys`) and 022's
classification question (the browser must not parse `@`). This is the same rule a third time,
and the third time is when it becomes a principle rather than a preference.

## Decision 2 — Correlation by id, scoped by entity

Every event already carries the key. Verified in the commands themselves:

```
RPC        HW.CALL     → RpcEnqueued      requestId
           HW.DEQUEUE  → RpcClaimed       requestId
                       → RpcAcknowledged  requestId
           HW.REPLY    → RpcReplied       requestId   ← recorded under `hw.replies`

Queue      HW.QSEND    → QueueSent        requestId (= messageId)
           HW.QCLAIM   → QueueClaimed     requestId
           HW.QACK     → QueueAcknowledged requestId

Pub/Sub    HW.PUBLISH  → Published        messageId (i64 channel sequence)
                       → fans out carrying that same number as each group entry's id
           per group   → QueueClaimed / QueueAcknowledged  requestId (= that number)
```

**Three rows appear where one should because nobody grouped them.** The data has been there
since 002.

Two wrinkles the implementation must handle rather than discover:

- **`Published` uses `messageId` (long); group deliveries use `requestId` (string)** — the same
  value in two representations. Normalise on read; do not change what is recorded, because that
  would be a protocol change for a display problem.
- **An RPC's reply lives under a different recorder name** (`hw.replies`, feature 015's fix for
  buffer-per-reply). Joining it is what makes R3.4 work, and it is the one join that crosses
  names.

**Why not the W3C trace context** the envelope already carries: it is globally unique and made
for this — and it lives in the **payload**. Correlation would then stop working the moment an
operator sets `HeadersOnly` for privacy, which is exactly when they still need to know what
happened. An id is present regardless.

## The message projection

```csharp
internal sealed record MessageSummaryDto(
    string Id,
    string Entity,
    MessageOutcome Outcome,     // Processed | Failed | DeadLettered | InFlight | Abandoned | Incomplete
    DateTimeOffset FirstSeen,
    TimeSpan? Duration,
    string? FailureType,
    int NodeCount);

internal sealed record MessageStepDto(
    DateTimeOffset At, string Type, string? Node, TimeSpan? SincePrevious, string? Detail);
```

**Outcome is derived on the server** from the event sequence — the last-writer wins, with
dead-lettering and failure taking precedence over an acknowledgement that preceded them.

**`Incomplete` is a real outcome, not an error state.** The recorder is bounded and volatile
(002), so a long-lived message's early events age out while later ones remain. Reporting that as
`Abandoned` would be a confident lie under exactly the load where the view matters most.

## Decision 3 — The window is stated, not extended

Counts and message lists cover **the recorder's retained window**, and every view says so.

"1,204 processed" is a number an operator reads as a lifetime total. "1,204 processed in the
last 18 minutes" is a number they can act on.

**A longer-retention message index is registered as a candidate, not built.** It is genuinely
useful and it is a new storage decision — and everything C4 says about bounded growth would
apply to it. Feature 016 spent its length learning what unbounded storage costs; adding some
inside the dashboard would be an odd way to forget that.

## The three views (R4)

All three are projections of the same events, computed in one place, so none can disagree with
another.

```
BY ENTITY   orders.create        1,204 processed · 3 failed   (last 18m)
              inv-8821  processed  47ms
              inv-8822  failed     InvalidOperationException

BY MESSAGE  inv-8822
              sent       8:35:47.201  shop-1
              claimed    8:35:47.209  order-service-1   +8ms
              failed     8:35:47.248  order-service-1   +39ms
                         InvalidOperationException: order ORD-77 already shipped
              body       {"OrderId":"ORD-77", …}

BY NODE     order-service-1
              handled 1,204 · failed 3 · in flight 2
```

**A published message's timeline shows every group's delivery**, because one publish is one
event to the developer and N to the broker. That difference is the whole reason the current
page is unreadable.

## Decision 4 — Extends 022's entity page

022's page answers *what is this and what is its state*. This adds *what has happened to it*.
Same question, same page. A second page would split one answer across two places and guarantee
they eventually disagree.

## Requirement 6 — Node address, and the framing that must change first

022 deferred this because `NodeRegistration` is `[i64 seenTicks][catalog json]` with **no version
byte**, and adding a field to an unversioned binary format is precisely how 013's storage break
happened.

```
before   [i64 seenTicks][catalog json]
after    [u8 version=1][i64 seenTicks][u16 addrLen][addr][catalog json]
```

**The version byte lands with the field, not after it.** A record without it is either read
under the old layout or refused with a message naming the remedy — never misparsed, which is
the rule 013 wrote after learning it the hard way.

The address is what the **broker observed**, and it is labelled that way. A node behind NAT, in
a container, or scaled horizontally under one name reports something true and useless; "seen
from" is honest about which it is.

## Error handling

| Case | Behaviour | Why |
|---|---|---|
| Events partly aged out | outcome `Incomplete`, stated | R1.5 — a confident wrong answer is worse than an admitted gap |
| Payload withheld by capture mode | says so, names the mode | The dashboard is not an exemption from 002 |
| Two entities using the same id | correlation is **scoped by entity** | Ids are unique per entity, not globally |
| A message list with thousands of rows | paged, and says it is paging | 020's rule: a view that silently truncates is how "we had no failures" gets said |
| Aggregation fails | that panel reports it; the rest of the page works | C7.1, as 020 established |

## What this design does not do

**No write operations.** Unchanged since 020.

**No metrics backend.** Counts over a window, not a time series. Highway has no metrics story,
and inventing one inside the dashboard is the wrong place to decide it.

**No build step.** ES modules, as 022 established. Server-side aggregation is what keeps that
sustainable — a thin client stays thin.

## Cross-References

- `docs/features/022-dashboard-catalogue/design.md` — the entity model and the module structure
- `docs/features/002-observability/design.md` — the recorder's volatility, capture modes, trace context
- `docs/features/015-recoverability/design.md` — `hw.replies`, and the failure context R3.5 renders
- `docs/features/013-reliable-delivery/design.md` — the versioned-framing precedent R6.3 follows
- `docs/product/constraints.md` — C4, which Open Decision 3 must respect
