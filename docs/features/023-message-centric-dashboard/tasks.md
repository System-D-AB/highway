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

### - [ ] T4 — The entity page lists messages

*Requirements:* R1.1, R1.2, R1.3, Open Decision 4
**Done when:** an entity page shows one row per **message** — id, **started (when + node)**,
**completed (when + node, or why not)**, outcome in developer words, duration — and the protocol
event view is one click away and **labelled as the protocol view**.

**Two node columns, not one.** "shop-1 sent it, order-service-1 processed it" is the sentence
the row exists to say, and a single column would have to pick one end.

Never `RpcAcknowledged` in an outcome column. The developer wrote `SendAsync`; the outcome is
`processed`.

### - [ ] T5 — One message, its whole journey

*Requirements:* R3.1–R3.6
**Done when:** a message shows its timeline with **Public steps first and Internal ones one
click away**, the node at each step, **durations between
steps** rather than bare clock times, the body subject to capture modes, the reply beside the
request for RPC, and 015's failure context when it failed.

**A published message shows every group's delivery.** One publish is one event to the developer
and N to the broker, and that difference is the whole reason the current page is unreadable.

"Waited 4.2s in the queue, processed in 30ms" is the diagnosis. Two clock times are the raw
material an operator has to do arithmetic on.

### - [ ] T6 — The node view

*Requirements:* R4.3, R4.5
**Done when:** a node shows what it handled and what it failed, and navigation connects
message ↔ entity ↔ node in both directions.

### - [ ] T7 — Bodies load on demand

*Requirements:* R7.4, R7.5
**Done when:** a message list ships no payloads, a body loads when a message is opened, and the
dashboard's polling cost is measured and stated.

A list that ships every body it lists is a dashboard that becomes its own broker's heaviest
client — the failure 020 R1.5 asked to be bounded.

---

## Phase 3 — Node identity

### - [ ] T8 — Version `NodeRegistration`, then add the address

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

### - [ ] T9 — Protocol and constraints

*Requirements:* R8.1, R8.2
**Done when:** the registration framing change is documented, `ProtocolConformanceTests` is
green, and `constraints.md` records the framing version and Open Decision 3's registered
candidate — a longer-retention message index — under Deferred.

**Register it; do not build it.** Feature 016 spent its whole length learning what unbounded
storage costs, and adding some inside the dashboard would be an odd way to forget that.

### - [ ] T10 — Samples and full verification

*Requirements:* R8.3, R8.4
**Done when:** the samples are re-run, the entity page is compared against the screenshot in
`requirements.md`, all tests pass, and `dotnet build --no-incremental` is warning-free.

**The before-and-after is the verification.** Six rows of `QueueSent`/`QueueClaimed`/
`QueueAcknowledged` should have become two message rows with outcomes. If an operator still
cannot answer "how many succeeded?", the feature did not do its job.

### - [ ] T11 — Front-end coverage, decided deliberately

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
