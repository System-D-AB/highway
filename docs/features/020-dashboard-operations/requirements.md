# Feature: Dashboard — From Recorder Viewer to Operations Console

## Introduction

Feature 011 built a dashboard for the flight recorder, and that is still all it is: recorder
health, a list of recorded names, and an event table per name.

Five features have shipped since, each adding operational state the dashboard cannot show:

| Feature | What it added | Dashboard today |
|---|---|---|
| **015** | Dead letters carry the exception type, message, stack, node and `firstType` | ❌ no dead-letter view at all |
| **016** | Per-queue byte budgets, `HW_QUEUE_FULL` refusals, `sendsRefused` | ❌ no queue view; the counter is unreachable |
| **017** | Automatic retirement, `groupsRetired`, `messagesDiscarded` | ⚠️ the *events* show; the counters do not |
| **018** | Pub/Sub became queues — a group is `hw:q:{channel}@{group}` | ❌ still described as channels |
| **019** | Lease renewal and `ProcessingCapExceeded` | ⚠️ the event shows; nothing says a handler is running long |

The pattern is consistent: **the dashboard sees events, and nothing else.** An event says
something happened; an operator needs to know what is *true right now* — how full is that
queue, what is sitting in the dead-letter list, which subscriber is about to be retired.

### The question this feature has to answer first

**The dashboard has no connection to the broker it is hosted in.** It runs in-process with
`FlightRecorder` injected, and the recorder is a pure in-memory structure. Every piece of state
listed above lives in Garnet — reachable through `HW.DLQ`, `HW.STATS` and ordinary key reads,
none of which the dashboard can currently issue.

**Opening a loopback client connection is the obvious answer and it has already failed once.**
Feature 018's pre-018 data check did exactly that: it built a connection string with the
password and forgot TLS, and **no TLS-enabled server could start** — four failing tests and a
broker that refused to boot. The fix made it transport-aware but still cannot cover mTLS, where
the server demands a client certificate the self-connection has no way to present.

So this feature's first requirement is not a view. It is deciding how the dashboard reads
broker state without reintroducing that class of bug — see [Open Decision 1](#1-how-does-the-dashboard-read-broker-state).

### What this feature does not do

**It does not add write operations.** No requeue button, no purge button, no retire button.
Read-only is a deliberate first step: the dashboard is reachable with an API key over loopback
(011), and an operator destroying a dead-letter list from a browser tab is a different
threat model from one reading it. Write actions get their own feature, with their own
confirmation and audit requirements.

## Requirements

### Requirement 1: The Dashboard Can Read Broker State

**User Story:** As the dashboard, I need to read queue depths and dead letters without becoming the reason a secured broker cannot start.

#### Acceptance Criteria

1. A single mechanism supplies broker state to the dashboard, decided in Open Decision 1
2. **It works unchanged against every supported security configuration**: open, password, TLS, and TLS with `ClientCertificateRequired`. A test covers all four, because the last one is the one that defeats a loopback connection
3. **A failure to read state never takes down the broker and never takes down the dashboard.** A view that cannot load says so and the rest of the page still works. This is C7.1 applied to the dashboard: a mechanism that observes the system must never be able to break it
4. Reading state is **read-only** by construction, not by convention — whatever the mechanism, it must not be able to mutate anything
5. The cost is bounded and stated. A dashboard polling every second must not measurably affect the broker's write path

### Requirement 2: Dead Letters Are Visible, With Their Diagnosis

**User Story:** As an operator, I want to see what died and why, without a terminal.

**The oldest outstanding item.** Feature 015 R3.4 asked for `HW.DLQ PEEK` **and** the dashboard;
PEEK shipped and the dashboard half did not.

#### Acceptance Criteria

1. A dead-letter view lists every queue and channel group that has dead letters, with counts
2. Selecting one shows its entries with the fields 015 added: `failureType`, `failureFirstType` when the failure changed shape, `failureDetail` (message, stack, node, time), `attempts`, `reason` and `deadLetteredAt`
3. **`firstType` is displayed as the answer to a question, not a field.** When it differs from `failureType`, the view says the failure *changed* — that is the question an operator actually asks, and burying it as one more row wastes it
4. A dead letter with no failure context says so explicitly, exactly as `HW.DLQ PEEK` does. Blank fields would read as "nothing went wrong"
5. The stack is collapsed by default and expandable. A stack is the most useful field and the one that destroys a table's readability
6. The payload obeys feature 002's capture modes. A name configured `HeadersOnly` must not have its payload appear here — the dashboard is not an exemption from the setting that exists to keep application data out of the recorder

### Requirement 3: Queues Show Their Real State

**User Story:** As an operator, I want to see how close a queue is to its limit before it starts refusing.

#### Acceptance Criteria

1. A queue view lists every queue with: depth, bytes used against `MaxQueueBytes`, in-flight (claimed) count, dead-letter count, and delayed count
2. **Fullness is shown as a proportion, not a number.** "847 MB" means nothing without the limit beside it; "83% of 1 GB" is actionable at a glance
3. **Subscriber groups appear as queues, because they are** (018). A channel is shown as its groups, each with the same columns, so "billing is at 94%" is visible before it refuses the channel
4. The view distinguishes a queue with **no consumers** from one that is merely busy. An unconsumed queue is the shape that fills up, and it is invisible in a depth number alone
5. `sendsRefused`, `groupsRetired` and `messagesDiscarded` are surfaced. They are counted today (016, 017) and reachable by nobody

### Requirement 4: The Things About To Go Wrong Are Visible

**User Story:** As an operator, I want the dashboard to show me the problem before it becomes an outage.

**This is the requirement that justifies the feature.** A view of what is currently true is
worth building because it makes the failure modes the last five features created *predictable*
rather than merely diagnosable afterwards.

#### Acceptance Criteria

1. A queue above a configurable fullness threshold is highlighted, with the default set so it fires well before refusal
2. **A subscriber group whose node is absent is highlighted, with time remaining before retirement.** 017 emits `NodeSuspect` at half the threshold; the dashboard turns that into "billing retires in 11 hours" — and retirement destroys that subscriber's entire backlog
3. A handler that has been running longer than half `MaxProcessingTime` is visible (019), so "this will be redelivered in seven minutes" is knowable before it is
4. Every one of these states already exists in the broker. **None of them requires new server work** — which is the argument for doing this now rather than another server feature

### Requirement 5: The `ErrorCode` Wart Is Fixed

**User Story:** As an operator, I want a warning to look like a warning.

#### Acceptance Criteria

1. `HighwayEvent` gains a **`Detail`** field for human-readable text, distinct from `ErrorCode`
2. Features 016, 017 and 019 move their prose into it — `"retired 1 group(s), discarded 41 message(s)"` and `"node 'x' has been absent past half the retirement threshold"` are messages, not codes
3. **The dashboard stops styling every event with an `ErrorCode` as a failure.** `NodeSuspect` is a warning and currently renders as an error, which trains an operator to ignore the colour
4. Events gain a severity — informational, warning, failure — and the view distinguishes them
5. This is a small change and it is in this feature because the dashboard is where the defect is visible. Fixing it elsewhere would mean changing the recorder twice

### Requirement 6: Conformance

#### Acceptance Criteria

1. No new `HW.*` command unless Open Decision 1 requires one; if it does, `docs/HIGHWAY-PROTOCOL.md` and `ProtocolConformanceTests` are updated in the same change
2. `constraints.md`: C7 gains the dashboard's read path under the "observing must never break" rule; 015 R3.4 is marked complete
3. `docs/features/015-recoverability/tasks.md` and `016`'s outstanding notes are closed, since this is the feature they were waiting for
4. The dashboard works against a secured broker in the sample, and the samples are re-run with a `RUNLOG.md` entry
5. All tests pass; `dotnet build` warning-free on a `--no-incremental` build — incremental builds have hidden a warning before

## Open Decisions

**Answer before the design is written.** The first one shapes everything else.

### 1. How does the dashboard read broker state?

| | How | Cost |
|---|---|---|
| **A. Loopback client connection** | The dashboard opens a RESP connection to its own broker and issues `HW.DLQ`, `HW.STATS` | Must mirror **every** transport setting. This is what broke TLS in 018, and **mTLS still defeats it** — the server demands a client certificate the self-connection cannot present |
| **B. In-process read API** | `HighwayServer` exposes a narrow read-only interface the dashboard consumes directly | Sidesteps transport entirely and cannot be broken by a security change. Needs in-process reads of Garnet state, which is the wall the 018 startup check hit when it took the connection route instead |
| **C. Server-owned connection** | The server builds the connection at startup from its own options and hands it to the dashboard | Transport matches by construction rather than by mirroring. Still cannot present a client certificate under mTLS |

**Recommendation: B.** The dashboard is *part of the server process*; making it talk to itself
over a network protocol to read state it is sitting next to is the kind of accidental complexity
that produces the 018 bug. A read-only in-process interface also satisfies R1.4 by construction
rather than by discipline.

**The risk to check first:** whether Garnet's state can be read in-process outside a
transaction, and at what cost. If it cannot, C is the fallback and mTLS becomes a documented
limitation rather than a silent failure.

### 2. Polling or streaming?

The dashboard already has SSE for events (011). Queue depths could reuse it, or be polled.
Streaming is nicer and adds a per-client push path over state that changes constantly.
**Recommendation: poll on an interval, with the interval configurable and stated.** Event
streaming exists because events are discrete; depth is a gauge, and a gauge does not need push.

### 3. Does the queue view read Garnet directly, or go through `HW.STATS`?

`HW.STATS` already aggregates some of this and is the documented surface. Reading keys directly
is more flexible and duplicates knowledge of the key layout — the thing `HighwayKeys` exists to
centralise. **Recommendation: extend `HW.STATS` where a field is missing** rather than teaching
the dashboard the key layout, so both surfaces stay honest.

### 4. Is the retirement countdown (R4.2) accurate enough to show?

It is derived from the node's last heartbeat and `SubscriberRetirementThreshold`, both known.
But a node that returns resets it, so a displayed countdown can jump back. **Recommendation:
show it, phrased as "retires in ~11h unless it returns"** — the uncertainty is the point, and
an operator who sees the countdown disappear has learned the node came back.

## Non-Goals

- **Write operations.** No requeue, purge, retire or replay-trigger from the browser. Read-only first; writes get their own feature with confirmation and audit.
- **Authentication beyond feature 011's API key.** The dashboard's exposure model is unchanged.
- **Historical charting or metrics storage.** The flight recorder is explicitly volatile (002); a dashboard that implies retained history would be lying about it.
- **Replacing `HW.STATS` or `HW.DLQ`.** The dashboard is a second surface onto the same data, never a separate source of truth.
- **A cluster or multi-broker view.** Highway is a single broker (`constraints.md` C5).

## Cross-References

- `docs/features/011-dashboard-flight-recorder/` — the dashboard this extends
- `docs/features/015-recoverability/tasks.md` — R3.4's dashboard half, and the `ErrorCode` wart recorded there
- `docs/features/016-retention-and-durability/` — byte budgets and `sendsRefused`
- `docs/features/017-node-decommissioning/` — retirement, `NodeSuspect`, and the countdown R4.2 derives
- `docs/features/018-pubsub-unification/design.md` — why a group is a queue, which is what R3.3 depends on
- `docs/features/019-long-running-tasks/` — `ProcessingCapExceeded` and the long-handler view
- `docs/product/constraints.md` — C7.1, the rule R1.3 applies to the dashboard's own read path
