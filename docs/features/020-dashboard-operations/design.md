# Design: Dashboard — Operations Console

> **Four open decisions, answered.**
>
> | | Decision | Chosen |
> |---|---|---|
> | 1 | How the dashboard reads state | **In-process read API**, with a server-owned connection as the documented fallback |
> | 2 | Polling or streaming | **Poll** for gauges; events keep their existing SSE |
> | 3 | Direct key reads or `HW.STATS` | **Extend `HW.STATS`**; the dashboard never learns the key layout |
> | 4 | Retirement countdown | **Show it**, phrased as an estimate that can reset |

## The shape of the change

```
BEFORE                                AFTER

Dashboard                             Dashboard
   │                                     │
   └─► FlightRecorder                    ├─► FlightRecorder        (events, unchanged)
       (in-memory, in-process)           │
                                         └─► IBrokerState          (gauges, NEW)
   nothing else reachable                        │
                                                 └─► HW.STATS / HW.DLQ, in-process
```

**One new seam, and it is deliberately narrow.** `IBrokerState` is read-only, returns DTOs, and
is implemented by the server rather than reached for by the dashboard.

## Decision 1 — In-process, not a loopback connection

```
A. loopback connection            B. in-process read API
──────────────────────            ──────────────────────
dashboard ──RESP──► itself        dashboard ──► IBrokerState ──► store

must mirror: port, password,      mirrors nothing. There is no
TLS, SNI, cert validation,        transport to get wrong.
client certificate

018 shipped this shape in the     cannot be broken by a security
pre-018 startup check. It         change, because it does not
mirrored the password and not     participate in security.
TLS, and NO TLS-ENABLED SERVER
COULD START.

mTLS defeats it even when         satisfies "read-only" by
correct: the server demands a     construction rather than by
client certificate the self-      discipline.
connection has none of.
```

The dashboard is *inside the server process*. Making it talk to itself over a network protocol
to read state it is sitting next to is accidental complexity, and 018 already paid for it once.

### The risk, and the fallback

Garnet's state is reached through sessions, and Highway's commands are transactional
procedures. **Whether a read-only in-process path exists at acceptable cost is the first thing
T1 must establish** — it is a spike, not an assumption.

If it does not: fall back to **C**, a connection the *server* builds from its own options at
startup and hands to the dashboard. Transport then matches by construction rather than by
mirroring, which removes the 018 failure mode. **mTLS remains uncoverable** and becomes a
documented limitation — the dashboard's state views degrade with a clear message rather than
the broker failing to start.

**Either way, R1.3 holds:** a view that cannot load says so, and the rest of the page works.

## Decision 3 — `HW.STATS` is the source, not the key layout

The dashboard must not learn that a queue's bytes live at `hw:q:{name}:bytes`. `HighwayKeys`
exists to centralise that, and a second reader of the layout is a second thing to update when it
changes — the drift this project keeps finding.

So `HW.STATS` gains what is missing:

```
HW.STATS Q:<queue>          depth, bytes, maxBytes, inFlight, deadLettered,
                            delayed, consumers, oldestClaimAge

HW.STATS QUEUES             one row per queue, for the list view — a single
                            call rather than N, because the list is the page
                            an operator lands on
```

`sendsRefused`, `groupsRetired` and `messagesDiscarded` already exist on the server form (016,
017) and only need surfacing.

**`consumers` and `oldestClaimAge` are the two new pieces**, and they are what R3.4 and R4.3
need: a queue with depth and no consumers is the shape that fills up, and the oldest claim age
is how long the longest-running handler has been going.

## Decision 5 — What the views are

Four, and no more. Each answers a question an operator actually asks.

```
┌─ Overview ────────────────────────────────────────────────┐
│  broker, uptime, durability, data directory               │
│  sendsRefused · groupsRetired · messagesDiscarded         │
│  ⚠ 2 queues above 80%   ⚠ 1 group retires in ~11h         │  ← R4
└───────────────────────────────────────────────────────────┘

┌─ Queues ──────────────────────────────────────────────────┐
│  name              depth   bytes        in-flight  dlq    │
│  orders.process    1,204   83% of 1 GB      8        0    │  ← highlighted
│  orders@billing    41,203  94% of 1 GB      0       12    │  ← no consumers
│  orders@shipping       2   <1%              1        0    │
└───────────────────────────────────────────────────────────┘

┌─ Dead letters ────────────────────────────────────────────┐
│  queue           count   newest                           │
│  orders@billing     12   2026-08-09T14:02Z                │
│    ▸ msg-8821  InvalidOperationException                   │
│      "order ORD-77 is already shipped"                     │
│      the failure CHANGED — started as TimeoutException     │  ← R2.3
│      ▸ stack (collapsed)                                   │
└───────────────────────────────────────────────────────────┘

┌─ Events ──────────────────────────────────────────────────┐
│  the existing recorder view, with severity colouring       │  ← R5
└───────────────────────────────────────────────────────────┘
```

**`firstType` is rendered as a sentence, not a row.** "The failure changed — started as
`TimeoutException`" is the answer to the question an operator asks. A `failureFirstType:` label
beside eleven other labels wastes the one field 015 added specifically to answer it.

## Decision 4 — The retirement countdown, and its honesty

Derived from the node's last heartbeat and `SubscriberRetirementThreshold`, both already known.
A node that returns resets it, so the number can vanish.

That is why it is phrased **"retires in ~11h unless it returns"**. The uncertainty is the
information: an operator who watches the countdown disappear has learned the node came back,
which is exactly what they wanted to know.

**This is the highest-value thing on the page.** Retirement destroys a subscriber's entire
backlog, and 017 made it automatic. A countdown turns the largest single loss Highway can
inflict from a surprise into a decision.

## Decision 6 — `Detail` and severity (R5)

```csharp
// before: prose smuggled through a field named for codes
_recorder.Record(GroupRetired, channel,
    errorCode: "retired 1 group(s), discarded 41 message(s) / 1,048,102 byte(s)");

// after
_recorder.Record(GroupRetired, channel,
    severity: EventSeverity.Warning,
    detail: "retired 1 group(s), discarded 41 message(s) / 1,048,102 byte(s)");
```

The dashboard styles on **severity**, not on "does this have an `ErrorCode`". Today
`NodeSuspect` — a warning that nothing has gone wrong yet — renders identically to a failure,
which teaches an operator to ignore the colour. A signal that cries wolf is worse than no
signal.

Severity is derived from the event type, not passed at every call site: the type already knows
whether it is informational. One mapping, one place to change.

## Error handling

| Case | Behaviour | Why |
|---|---|---|
| State read fails | that view shows an error; events and the rest of the page still work | R1.3 — C7.1 applied to the dashboard itself |
| mTLS with the fallback path | state views degrade with a clear message | Better a named limitation than a broker that will not start (018) |
| A queue disappears mid-poll | the row vanishes; no error | Retirement (017) does exactly this, legitimately |
| Payload under `HeadersOnly` | not shown, and says why | The dashboard is not an exemption from 002's capture modes |
| 10,000 queues | the list pages and says it is paging | A view that silently truncates is how "we had no dead letters" gets said |

## What this design does not do

**No write operations.** No requeue, purge or retire buttons. An operator destroying a
dead-letter list from a browser tab is a different threat model from one reading it, and it
needs confirmation and audit that belong in their own feature.

**No new source of truth.** Every number comes from `HW.STATS` or `HW.DLQ`. The dashboard is a
second *surface*, never a second *answer*.

**No history.** The flight recorder is explicitly volatile (002). A chart implying retained
history would misrepresent what the product keeps.

## Cross-References

- `docs/features/011-dashboard-flight-recorder/design.md` — the SSE and API-key model this keeps
- `docs/features/015-recoverability/design.md` — the failure block R2 renders
- `docs/features/017-node-decommissioning/design.md` — the threshold R4.2's countdown derives from
- `docs/features/018-pubsub-unification/design.md` — why a group is a queue, which is what lets one view cover both
- `docs/product/constraints.md` — C7.1
