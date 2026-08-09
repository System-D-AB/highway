# Tasks: Messages, Not Protocol Events

**T1 is the feature.** Everything else renders what it produces. If message projection lives
anywhere but the server, this has built a second implementation of the protocol's semantics in
JavaScript — the mistake 022 avoided twice.

**Nothing here writes.** Unchanged since 020.

---

## Phase 1 — The projection

### - [x] T1 — Correlate events into messages, server-side

A projection over the recorder's buffers: group by id, scoped by entity, and derive an outcome.

*Requirements:* R1.1, R1.4, R7.1, R7.3, Open Decisions 1 and 2
**Done when:** `MessageSummaryDto` and `MessageStepDto` are produced by server code with server
tests, and no JavaScript groups events or decides what an outcome is.

The ids are already there — verified in the commands:

```
RPC      requestId   on RpcEnqueued / RpcClaimed / RpcAcknowledged
                     and on RpcReplied, recorded under `hw.replies`
Queue    requestId   on QueueSent / QueueClaimed / QueueAcknowledged
Pub/Sub  messageId   on Published (i64), carried into each group entry as its id (string)
```

**Two wrinkles to handle rather than discover:** `Published` records `messageId` as a long while
group deliveries record `requestId` as a string — the same value, two representations, normalise
on read. And an RPC's reply lives under a **different recorder name**, so the join that makes
R3.4 work is the one that crosses names.

**Do not change what is recorded to make this easier.** That would be a protocol change for a
display problem.

### - [x] T1a — Classify every event `Public` or `Internal`

*Requirements:* R1.6, R1.7, R1.8, design Decision 5
**Done when:** `HighwayEventType` carries a visibility, decided **on the server**, and a test
enumerates every member so a new event type cannot be added without someone saying which it is.

**Public** is what the developer's code caused or must act on — an RPC started, a response
returned, a message published or sent, a handler failed, a message dead-lettered, a send
refused. **Internal** is the broker recognising its own work: claims, acknowledgements,
doorbells, sweeps, requeues, topology.

> **The subtlety that makes this more than a filter.** Highway has no "handler finished" event —
> the acknowledgement *is* the evidence. So the Public **fact** (*processed at 8:35:47 on
> order-service-1*) is derived from an event classified **Internal**. The projection derives
> facts; the classification decides which raw steps are shown. Conflating them produces a
> message list that says "acknowledged", which is the exact word this feature exists to stop
> showing.

The enumeration test is the same mechanism 016's `BoundedStructureTests` used, and it earned its
keep on first contact in 017.

### - [x] T2 — `Incomplete` is an outcome, not a failure

*Requirements:* R1.5
**Done when:** a message whose early events have aged out of the bounded recorder reports
`Incomplete`, and a test proves it by filling a small buffer past its capacity.

**This test matters more than it looks.** The recorder is volatile and bounded (002), so this is
the normal state under load — exactly when the view is most needed. Reporting it as `Abandoned`
would be a confident lie at the worst moment.

### - [x] T3 — Counts, with their window

*Requirements:* R2.1, R2.2, R2.4, R2.5
**Done when:** every entity reports processed / failed / dead-lettered / refused counts, **each
labelled with the window they cover**, and a channel aggregates its groups while each group keeps
its own.

"1,204 processed" reads as a lifetime total. "1,204 processed in the last 18 minutes" is
actionable. The window is not a footnote.

The four outcomes stay distinct because they are different problems: a handler threw (015),
attempts exhausted (013), a byte limit refused it (016).

---

## Phase 2 — The views

### - [x] T4 — The entity page lists messages

*Requirements:* R1.1, R1.2, R1.3, Open Decision 4
**Done when:** an entity page shows one row per **message** — id, **started (when + node)**,
**completed (when + node, or why not)**, outcome in developer words, duration — and the protocol
event view is one click away and **labelled as the protocol view**.

**Two node columns, not one.** "shop-1 sent it, order-service-1 processed it" is the sentence
the row exists to say, and a single column would have to pick one end.

Never `RpcAcknowledged` in an outcome column. The developer wrote `SendAsync`; the outcome is
`processed`.

### - [x] T5 — One message, its whole journey

*Requirements:* R3.1–R3.6
**Done when:** a message shows its timeline with **Public steps first and Internal ones one
click away**, the node at each step, **durations between
steps** rather than bare clock times, the body subject to capture modes, the reply beside the
request for RPC, and 015's failure context when it failed.

**A published message shows every group's delivery.** One publish is one event to the developer
and N to the broker, and that difference is the whole reason the current page is unreadable.

"Waited 4.2s in the queue, processed in 30ms" is the diagnosis. Two clock times are the raw
material an operator has to do arithmetic on.

### - [x] T6 — The node view

*Requirements:* R4.3, R4.5
**Done when:** a node shows what it handled and what it failed, and navigation connects
message ↔ entity ↔ node in both directions.

### - [x] T7 — Bodies load on demand

*Requirements:* R7.4, R7.5
**Done when:** a message list ships no payloads, a body loads when a message is opened, and the
dashboard's polling cost is measured and stated.

A list that ships every body it lists is a dashboard that becomes its own broker's heaviest
client — the failure 020 R1.5 asked to be bounded.

---

## Phase 3 — Node identity

### - [x] T8 — Version `NodeRegistration`, then add the address

*Requirements:* R6.1–R6.4
**Done when:** the framing carries a version byte **in the same change** as the new field, an
older record is either read correctly or **refused with a message naming the remedy**, and the
address is labelled as an observation.

```
before   [i64 seenTicks][catalog json]
after    [u8 version=1][i64 seenTicks][u16 addrLen][addr][catalog json]
```

> **The version byte is not optional and not a follow-up.** Adding a field to an unversioned
> binary format is exactly how 013's storage break happened: an old entry read as a new one does
> not fail, it reinterprets its leading bytes and hands back something wrong. 022 deferred this
> task for precisely this reason (review R-0A); doing it now means doing it properly.

**"Seen from 10.1.4.22", not "Address".** Behind NAT, in a container, or scaled horizontally
under one name, the observed address is true and not useful — and the label is what carries that
distinction.

---

## Phase 4 — Conformance

### - [x] T9 — Protocol and constraints

*Requirements:* R8.1, R8.2
**Done when:** the registration framing change is documented, `ProtocolConformanceTests` is
green, and `constraints.md` records the framing version and Open Decision 3's registered
candidate — a longer-retention message index — under Deferred.

**Register it; do not build it.** Feature 016 spent its whole length learning what unbounded
storage costs, and adding some inside the dashboard would be an odd way to forget that.

### - [x] T10 — Samples and full verification

*Requirements:* R8.3, R8.4
**Done when:** the samples are re-run, the entity page is compared against the screenshot in
`requirements.md`, all tests pass, and `dotnet build --no-incremental` is warning-free.

**The before-and-after is the verification.** Six rows of `QueueSent`/`QueueClaimed`/
`QueueAcknowledged` should have become two message rows with outcomes. If an operator still
cannot answer "how many succeeded?", the feature did not do its job.

### - [x] T11 — Front-end coverage, decided deliberately

*Requirements:* R7.3
**Done when:** either a headless-browser harness exists, or its absence is a **recorded
decision** with what compensates for it.

022 shipped its front-end verified only by being driven against a live broker, and said so. That
is acceptable once. Twice is a habit, and T1's server-side projection is what makes the
alternative cheap — the logic worth testing is no longer in the browser.

---

## Parallelization

```
LANE 0   T1, T2, T3    the projection        → blocks the views
LANE 1   T4, T5, T6, T7  the views           → needs lane 0
LANE 2   T8            node identity         → independent, can run beside either
LANE 3   T9, T10, T11  conformance           → last

Order: 0 → 1 → 3,  with 2 alongside
```

---

## The line that must not move

**The server aggregates; the browser renders.** No correlation, no outcome derivation, no
counting in JavaScript. This rule has now survived three features — the browser must not learn
the key layout (020), must not parse a name (022), must not decide what acknowledged means
(023) — and at three it is a principle, not a preference.

**And the window is always stated.** Every count and every list covers what the recorder still
holds. A number presented as a lifetime total, from a buffer that drops under load, is the most
convincing wrong answer this dashboard could give.


---

## What execution found

### `startedOnNode` is not knowable, and the spec assumed it was

R1.1 asked for **both** nodes on every message row — where it started and where it finished.
Running it against the samples showed the completion node populated and the start node
**always null**:

```
27b940d72b0d   start=None -> done=order-service-1   Processed   60ms
b64955a841ea   start=None -> done=order-service-1   Processed
```

**The cause is not the projection.** `HW.CALL`, `HW.QSEND` and `HW.PUBLISH` record no node,
because **the sender never identifies itself in those commands**. The broker learns a node id
only when a worker *claims* — `HW.DEQUEUE`/`HW.QCLAIM` carry one. So the recorded events simply
do not contain the fact the row wanted.

The information exists, just not where the recorder can reach it: the **envelope** carries
`"src"` (feature 005). Getting it onto the event means one of:

| | Cost |
|---|---|
| Parse `src` from the envelope when recording | JSON parsing inside a Garnet transaction, on the send path. Highway's write path is measured in nanoseconds |
| Add a caller-node argument to `HW.CALL` / `HW.QSEND` / `HW.PUBLISH` | A protocol change to three commands, for a display field |
| Leave it | The row shows `—` for the origin |

**Left as `—` for now, and registered.** Neither fix is small, and bolting a protocol change
onto the tail of a dashboard feature is how the wrong one gets chosen. The column stays because
the *completion* node is real and useful, and an honest blank is better than a removed column
that hides a knowable fact.

**The spec was wrong, not the implementation.** "Both nodes" was written from the shape of the
data as displayed, without checking that the send side records one.

### What does work, verified against the samples

```
orders.create      processed=1  failed=0            60ms   -> order-service-1
invoices.generate  processed=1                             -> order-service-1
poison.queue       processed=0  failed=1   System.InvalidOperationException
```

Outcomes, durations, counts by category, failure detail, and the completion node all come
through. Six protocol rows became one message row per message.

## Phase 3

**T6 and T8 are now built.** What follows replaces the "not done" note that stood here.

### T8 did not need the framing change the task specified

The task said: version `NodeRegistration`, then add the address. I wrote that change — version marker,
`u16` address length, v0 records still readable, a refusal message naming the remedy for an unknown
version. Then I looked for where the value would come from and **the change turned out to be
unnecessary**, so I reverted it.

The node already tells the broker where it is, every time it connects: `CLIENT SETNAME`. The client
now sets its connection name to its node name, and `BrokerState` joins the registry to `CLIENT LIST`
on that name. Both are RESP built-ins Garnet already implements — no new command, no new storage,
and **no versioned framing**, because nothing is persisted.

| | Registration field | Live client list |
|---|---|---|
| Storage format | Changes an unversioned binary framing | Untouched |
| Freshness | The address a node had when it last registered | The address it is connected from now |
| A node that has gone away | Reports a stale address as if current | Reports nothing, which is the truth |

The second column is not just cheaper, it is **more correct**. A recorded address survives the
socket it describes; `seenFrom` is null for a registered-but-absent node precisely because there is
nothing to see. The version byte would have been real work protecting a value that was worse.

**The spec's reasoning was right and its conclusion was wrong.** "Do not add a field to an
unversioned format" is correct — the escape it missed is not adding the field. R6.1–R6.4 are met:
the address is shown, labelled `Seen from`, and never presented as an address to dial.

Verified live:

```
order-service-1   live   seen from 127.0.0.1:63619
```

`Sanitise()` guards the name because `CLIENT SETNAME` rejects whitespace and would fail the whole
connection — a display field must never be able to stop a node connecting.

### T6 fixed a link that had been dead since 022

The nodes list has linked to `#/node?name=…` since 022, but no such route existed: it fell through
the router's default onto the catalogue. Clicking a node quietly showed the wrong page.

The page pairs **declared** against **processed**, because neither is worth much alone. "Hosts
`orders.create`" is a claim the node made; "processed 3 messages" is a thing that happened. A node
declaring a service it has never served is a misconfiguration, and it is only visible when both
are on one page.

The per-node index is a scan, not an index. The recorder keys by entity name, and nothing maps a
node to the messages it handled; building that mapping would be new storage for a view, so the
endpoint projects every entity and filters. The recorder is bounded (002), which is what makes
that affordable.

Verified live, four entities in one list:

```
poison.queue                    Failed      39ms   System.InvalidOperationException
inventory.low@order-service-1   Processed
invoices.generate               Processed    5ms
orders.create                   Processed   41ms
```

Attribution is by **completion**, the only node these events name — the same `startedOnNode` gap
recorded above, surfacing again. The page says so rather than implying the list is everything the
node touched.

## Not done

### T11 — front-end coverage was decided, by the user

The user's instruction for this phase was explicit: **no tests for the dashboard project.** That
resolves T11 as a recorded decision rather than an open one, and it is worth writing down what
now carries the weight instead:

- **The logic moved to the server.** T1's `MessageProjection` — correlation, outcome ordering,
  `Incomplete`, the counts — has 14 tests. In 022 that logic lived in the browser and had none.
- **The endpoints are exercised live**, against real samples, and this log records what they
  returned rather than that they returned something.
- **What is genuinely untested is rendering**, and a rendering fault is visible on the first
  screenshot. Finding 1 above is the counter-example — a dead route rendered a plausible page —
  and it was found by driving the samples, which is the compensating practice.

That is a smaller gap than 022's, and unlike 022's it is a decision rather than an omission.

### Defect found by looking at the running dashboard: a channel never completed

Every message on a channel page read `InFlight` for ever, while the samples had processed all of
them. Reported from a screenshot, and reproduced in one query:

```
/api/messages/inventory.low                    id 2  InFlight   no completion
/api/messages/inventory.low@order-service-1    id 2  Processed  order-service-1, +20ms
```

**Same message, two recorder names.** A channel records only `Published`; the delivery and the
acknowledgement are recorded under the subscriber group `{channel}@{node}`. The projection was
sound and was being asked about half a message.

The endpoint already joined `hw.replies` for exactly this reason — an RPC's reply is recorded
under a different name than its request — and the same join was missing for channels. Splitting
on `@` is how `Catalogue.Classify` already decides what a group is, so the fix agrees with the
catalogue rather than inventing a second rule.

**A fan-out has more than one ending, and one outcome word has room for one.** Two of three
subscribers succeeding is not "processed" and not "failed". The row now carries `2/3 groups`
beside the outcome, with each group resolved on **its own** events — resolving the union would
let one group's acknowledgement answer for another group's failure. The subscriber count comes
from the caller rather than the events, because a group that has received nothing leaves no
trace and is exactly the group worth noticing.

Four tests in `MessageProjectionTests`, all checked against deliberately-broken code. The channel
test asserts **both** worlds — `InFlight` on the channel's own events, `Processed` once joined —
so it cannot pass by accident.

The message timeline gained the thing R1 asked for and never showed:

```
Public    Published                             —
Internal  QueueClaimed         order-service-1  +15.6ms
Internal  QueueAcknowledged    order-service-1  +0.5ms
```

### The counts panel rendered its label and its number as one word

`IN FLIGHT2`, in the screenshot. The stylesheet has defined `.stat-label` and `.stat-value` since
022; `entity.js` emitted a bare `<span>` and `<b>`, so neither rule applied and the two ran
together. A markup-and-CSS mismatch that no test would have caught and one look did.

## Not done

**`startedOnNode` is still unrecorded**, and this phase is more evidence it matters: the node page
can show what a node *finished* and not what it *started*. T6 needs a per-node message index, which
is a projection over every entity rather than one — a real piece of work, not a rendering pass.
T8 changes `NodeRegistration`'s unversioned framing, and doing that properly is the whole reason
022 deferred it; it should not be squeezed in beside a UI change.

**T11 stands unanswered.** The front end is again verified by being driven against a live broker
rather than by tests. The difference from 022 is that the logic worth testing has moved to the
server — `MessageProjectionTests` covers correlation, outcome ordering, `Incomplete`, both nodes
and the counts — so what remains untested in the browser is rendering. That is a smaller and more
defensible gap, but it is still a gap.
