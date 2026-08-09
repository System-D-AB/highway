# Feature: Messages, Not Protocol Events

## Introduction

### The evidence

Feature 022 made the dashboard show *what exists*. Clicking into an entity still shows this:

```
invoices.generate  [QUEUE]     Hosted by order-service-1

Time         Type                 Node              Detail
8:35:47 PM   QueueSent
8:35:47 PM   QueueClaimed         order-service-1
8:35:47 PM   QueueAcknowledged    order-service-1
8:35:52 PM   QueueSent
8:35:52 PM   QueueClaimed         order-service-1
8:35:52 PM   QueueAcknowledged    order-service-1
```

**Two messages. Six rows. Not one of them is a thing the developer did.**

`QueueSent`, `QueueClaimed`, `QueueAcknowledged` are the broker's internal mechanics. The
developer wrote `SendAsync(new GenerateInvoice { ... })` and a handler processed it. Neither the
message nor its outcome appears anywhere on this page.

The same page cannot answer the first question anyone asks: **how many invoices were generated,
and how many failed?**

### The diagnosis

**The dashboard shows the transport, and the operator wants the traffic.**

022 fixed *what things are*. This fixes *what happened to them*. The unit is wrong: a row per
protocol event, where the unit of meaning is a **message** — sent once, processed once, with an
outcome.

### The fact that makes this cheap

**The correlation key is already in every event, and nothing has ever grouped by it.**

| Verb | Events | Key |
|---|---|---|
| RPC | `RpcEnqueued`, `RpcClaimed`, `RpcAcknowledged`, `RpcReplied` | `requestId` |
| Queue | `QueueSent`, `QueueClaimed`, `QueueAcknowledged`, `QueueDeadLettered` | `requestId` |
| Pub/Sub | `Published` on the channel; `QueueClaimed`/`QueueAcknowledged` on each group queue | the channel sequence, carried into each group's entry |

Every one of those already carries the id. **A message's whole life is reconstructible from data
the recorder holds today** — including the cross-node journey, because each event carries the
node that produced it.

Three rows appear where one should because nobody grouped them, not because the data is missing.

### The architectural question this raises

The user's observation is correct: **the dashboard is becoming an application.** 011 was a
recorder viewer, 020 added state, 022 added an entity model, and this adds correlation and
aggregation. That trajectory needs a decision rather than a drift.

The decision this feature makes: **the server aggregates, the browser renders.** See
[Open Decision 1](#1-where-does-correlation-happen). A browser that correlates thousands of
events is a browser downloading thousands of events, and it becomes a second implementation of
the message model.

## Requirements

### Requirement 1: The Unit Is a Message

**User Story:** As a developer, I want one row per message I sent, not three rows per message the broker moved.

#### Acceptance Criteria

1. An entity's page lists **messages**, one row each, carrying the whole story a summary can hold:

   | | |
   |---|---|
   | identifier | which message |
   | **started** | when, and **on which node** |
   | **completed** | when, **on which node**, or why it did not |
   | outcome | in developer words |
   | duration | end to end |

   **Both nodes, not one.** A message is usually produced on one node and processed on another,
   and "shop-1 sent it, order-service-1 processed it" is the sentence the row exists to say. A
   single "node" column would have to pick one and would pick the wrong one half the time.
2. Protocol events are **not** the default view. They remain available — see R5 — because they are the truth underneath, but they are not what an operator is shown first
3. A message row states its outcome in words a developer recognises: **processed**, **failed**, **dead-lettered**, **in flight**, or **abandoned** — never `RpcAcknowledged`
4. Outcome is derived **on the server** from the event sequence. A browser inferring "acknowledged means success" is a second implementation of the protocol's semantics
5. A message whose events have partly aged out of the recorder is shown as **incomplete**, explicitly, rather than as a wrong outcome. The recorder is bounded and volatile (002); pretending otherwise would make the view lie under exactly the load that matters
6. **Every event type is classified `Public` or `Internal`, on the server.** *Public* is something the developer's code caused or needs to act on — an RPC was started, a response came back, a message was published or sent, a handler processed it, a handler failed, a message dead-lettered. *Internal* is the broker recognising its own work — a claim, an acknowledgement, a doorbell, a sweep, a requeue
7. **A summary row is built from Public events only.** The list answers "what did my code do and what happened to it", and an internal step has never been the answer to that
8. The classification lives with the event type, not with the view. A second implementation in JavaScript would be the same mistake this project has now refused three times (020: the key layout; 022: name parsing; 023: outcome derivation)
9. **A Public fact may be evidenced by an Internal event, and the view shows the fact.** Highway has no "handler finished" event — an acknowledgement *is* that evidence. So the row says *processed at 8:35:47 on order-service-1*, derived from `QueueAcknowledged`, and never shows the word "acknowledged"

### Requirement 2: Counts That Answer The First Question

**User Story:** As an operator, I want to see at a glance how much work succeeded and how much did not.

#### Acceptance Criteria

1. Every service, queue and channel in the catalogue shows **processed** and **failed** counts
2. The counts are over the recorder's retained window, and **the window is stated**. "1,204 processed" without "in the last 20 minutes" is a number an operator will misread as a lifetime total
3. A non-zero failure count is visually distinct and links to those messages, not to a filtered event log
4. Counts distinguish **failed** (a handler threw — 015) from **dead-lettered** (attempts exhausted — 013) from **refused** (a byte limit — 016). They are different problems with different fixes
5. A channel's counts aggregate its groups, and each group's own counts remain visible. "The channel is fine but billing is failing" is the sentence this must be able to produce

### Requirement 3: One Message, Its Whole Journey

**User Story:** As a developer debugging one order, I want to see everything that happened to it, across every node.

#### Acceptance Criteria

1. Selecting a message shows its **timeline**: what happened, when, and **on which node** — **Public steps first and by default, with the Internal ones one click away**. The whole lifecycle is there; the mechanics do not crowd out the meaning
2. The timeline crosses nodes and entities. A published message shows the publisher and then **each subscriber group's** delivery, because that is one event from the developer's point of view and N from the broker's
3. The **message body** is shown — the payload the developer sent — subject to feature 002's capture modes, saying so explicitly when withheld rather than showing blank
4. For RPC, the **reply** is shown beside the request. The reply is recorded under `hw.replies`, so this requires joining two recorder names by `requestId` — the join that makes the whole feature work
5. Failures show the exception type, message and stack that 015 already records
6. Timings are shown as **durations between steps**, not just timestamps. "Waited 4.2s in the queue, processed in 30ms" is the diagnosis; two clock times are the raw material for it

### Requirement 4: The Same Data, Three Points Of View

**User Story:** As different people with different questions, we want views shaped for our question, not one view shaped for the storage.

#### Acceptance Criteria

1. **By entity** — "what is happening to `orders.create`?" (R1, R2)
2. **By message** — "what happened to this one order?" (R3)
3. **By node** — "what is `order-service-1` doing, and what has it failed?"
4. All three are **projections of the same recorded events**, computed server-side. No view has its own source of truth, and none can disagree with another
5. Navigation connects them: a message links to its entity and its nodes; a node links to the messages it handled

### Requirement 5: The Protocol View Survives

**User Story:** As someone debugging Highway itself, I still need the raw events.

#### Acceptance Criteria

1. The per-event view remains reachable from any entity and any message
2. It is labelled as the protocol view, so its audience is obvious
3. It keeps the existing filters and live stream — they work, and this feature does not touch them

### Requirement 6: Nodes Carry Identity

**User Story:** As an operator, I want to know which host a node actually is.

**Deferred from 022 (review R-0A)** because it changes an unversioned binary framing. It is in
scope here, done properly.

#### Acceptance Criteria

1. A node's registration records the address the **broker observes** it connecting from
2. It is labelled as an observation — "seen from 10.1.4.22" — not as a property of the node. Behind NAT, in a container, or scaled horizontally under one name, the address is true and not useful, and the label carries that
3. **`NodeRegistration`'s framing gains a version byte** in the same change. It has none, and adding a field to an unversioned binary format is how 013's storage break happened
4. A registration written by an older client is still readable, or is refused with a message naming the remedy — never misparsed

### Requirement 7: The Dashboard Is An Application, And Is Structured Like One

**User Story:** As a maintainer, I want the dashboard's growth to be a decision rather than an accident.

#### Acceptance Criteria

1. **The server aggregates; the browser renders.** No correlation, outcome derivation or counting happens in JavaScript — Open Decision 1
2. **No build step.** ES modules, as 022 established. Introducing npm and a bundler into a .NET repository is a dependency decision that needs its own justification, and "the dashboard got bigger" is not one
3. The aggregation layer is **testable without a browser**: message projection is server code with server tests. Today the dashboard's logic is verifiable only by looking at it
4. Payload size is bounded on the wire. A message list must not ship every body it lists — bodies load when a message is opened
5. The dashboard's cost to the broker is **stated and bounded**, as 020 R1.5 required

### Requirement 8: Conformance

#### Acceptance Criteria

1. Any new server surface documented in `docs/HIGHWAY-PROTOCOL.md` in the same change; `ProtocolConformanceTests` green
2. `constraints.md` updated if a guarantee changes — R6's framing change is the likely candidate
3. Samples re-run with a `RUNLOG.md` entry, and the entity page compared against the screenshot in this document
4. All tests pass; `dotnet build --no-incremental` warning-free

## Open Decisions

### 1. Where does correlation happen?

- *Browser.* No server work. But it must fetch every event to group them, re-implement outcome rules, and it gets slower exactly as traffic grows.
- *Server.* The recorder is in-process and already holds the events; aggregation is a scan over buffers it owns. One implementation of "what does acknowledged mean".

**Recommendation: server.** It is the only option that keeps the browser thin as the dashboard
grows, and R7.3's testability follows from it for free.

### 2. How are messages correlated across recorder names?

A message's events are spread across names: an RPC's reply is under `hw.replies`; a publish is
under the channel while its deliveries are under each group queue.

- *By id alone* — `requestId`, and the channel sequence for pub/sub. Available today, needs no protocol change. Ambiguous if two entities coincidentally use the same id.
- *By the W3C trace context* the envelope already carries (`tp`, feature 002) — globally unique and designed for this, but it lives in the **payload**, so correlation would depend on payload capture being on.

**Recommendation: id, scoped by entity**, with trace context as a later enrichment. An id is
present whether or not payloads are captured, and correlation must not stop working because an
operator turned capture off for privacy.

### 3. What is the retained window, and can it be extended?

The recorder is volatile and bounded (002). Message history is only as deep as its buffers, and
a busy broker's window may be minutes.

- *State the window and accept it.* Honest, cheap, and possibly too short to be useful.
- *A separate, longer-retention message index.* Genuinely useful and a new storage decision, with everything C4 says about bounded growth applying to it.

**Recommendation: state the window in this feature; register the index as a candidate.** Do not
quietly add unbounded storage to a product that spent 016 learning what that costs.

### 4. Does this replace 022's entity page or extend it?

**Recommendation: extend.** 022's page shows what an entity *is* and its live state. This adds
what has *happened* to it. They are the same page, and a second page would split the answer to
one question.

## Non-Goals

- **Write operations.** Unchanged since 020: no requeue, purge or retire from the browser.
- **A metrics backend or charting.** Counts over the recorder's window, not a time series. Highway has no metrics story yet, and inventing one inside the dashboard is the wrong place for that decision.
- **Distributed tracing.** The envelope carries W3C trace context (002) and a real tracing backend is the right home for cross-service traces. This correlates *within* Highway.
- **Retaining messages the recorder has dropped.** Open Decision 3.
- **A build step.** R7.2.

## Cross-References

- `docs/features/022-dashboard-catalogue/` — the entity model this adds behaviour to
- `docs/features/020-dashboard-operations/` — the read path and `IBrokerState`
- `docs/features/002-observability/design.md` — the recorder's volatility, capture modes and trace context
- `docs/features/015-recoverability/design.md` — the failure context R3.5 renders
- `docs/product/constraints.md` — C4 on bounded storage, which Open Decision 3 must respect
