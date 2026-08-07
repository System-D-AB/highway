# Feature: Reliable Delivery — Dead Letters, Delayed Delivery, Deduplication

## Introduction

Highway promises durable delivery. In three specific ways it does not currently keep that
promise, and all three are visible in shipped code:

1. **A permanently failing message is redelivered forever.** `HW.DEQUEUE`'s lease sweep requeues abandoned work with no attempt limit, no dead-letter destination, and no way to see or drain what is stuck. One poison message poisons a service queue for the life of the deployment.
2. **Delivery is at-least-once with nothing to deduplicate against.** Lease recovery can deliver the same request twice *by design* — correct behaviour for a durable queue — but Highway hands the resulting duplicate-handling problem entirely to the application and offers no help with it.
3. **There is no way to defer work.** Retry-with-backoff, scheduled sends, and delayed retries all require Hangfire, Quartz, or a database today — an infrastructure dependency for something the broker is already positioned to do.

This feature closes all three. They are one feature rather than three because they are one
mechanism: an attempt counter is what makes dead-lettering possible, delayed delivery is
what turns "retry" into "retry with backoff", and deduplication is what makes accepting a
redelivery safe.

### Why these, and not the other nine

An earlier `runtime-vision.md` proposed nine primitives — cache, lock, rate limiting,
counters, delayed messages, leader election, shared dictionary, deduplication,
content-based routing — under the banner "a distributed application runtime". That
document has been **withdrawn**; `docs/product/roadmap.md` records why in full.

The short version: most of the nine wrapped commands an application can already issue on
the connection it already holds. These three are different. **They fix things Highway
itself breaks.** A feature that closes a gap the product creates is worth more than a
feature that wraps a command the user could have called.

### The constraint that governs every decision here

Highway's advantage is that it is easy, and the samples are the proof. Every requirement
below is written so the common case costs the developer **nothing new to learn**: delayed
delivery is one optional parameter on `PublishAsync`, deduplication is one attribute, and
dead-lettering is automatic and needs no API at all until you want to look at what landed
there.

## Glossary

- **Delivery attempt** — one claim of a request or message by a consumer. Incremented when work is requeued after a lease expiry, not when it is first enqueued.
- **Dead letter** — an entry that has exhausted its delivery attempts and has been moved out of the live queue, so it stops blocking and stops looping.
- **DLQ** — the dead-letter list for one service, or for one channel group.
- **Promotion** — moving a delayed message from the delayed set into a live group queue once its delivery time has passed.
- **Deduplication window** — how long a completed delivery's identity is remembered, so a redelivery of it can be recognised.

## Requirements

### Requirement 1: Delivery Attempts Are Counted

**User Story:** As an operator, I want to know how many times a message has been tried, because "forever" is not a number.

#### Acceptance Criteria

1. Every RPC queue entry and every channel group entry carries a **delivery attempt count**
2. The count increments when an entry is **requeued after a lease expiry**, not when it is first enqueued — a message delivered once and acknowledged has one attempt, not two
3. The count survives requeue, broker restart, and AOF replay, because it lives in the entry rather than beside it
4. The count is visible through `HW.STATS` and the flight recorder, so "this message has been tried 47 times" is answerable *before* it becomes a dead letter
5. Counting is **not** optional and has no configuration. A count that can be turned off is a count you cannot rely on

### Requirement 2: Dead Letters

**User Story:** As an operator, I want a message that cannot be processed to get out of the way, and to still exist so I can find out why.

#### Acceptance Criteria

1. A configurable **maximum delivery attempt count** applies to RPC requests and to channel group messages, with a documented default
2. On exceeding it, the entry is moved to a **dead-letter list** rather than requeued — it leaves the live queue exactly once and never loops again
3. Dead-lettering is **atomic with removal from the live queue**: an entry can never be in both places, and can never be in neither
4. The dead-lettered entry retains its payload, its original identifiers, its attempt count, and the time it was dead-lettered. An entry that reaches the DLQ stripped of what is needed to diagnose it has been thrown away with extra steps
5. Dead letters are **inspectable without consuming them** — an operator can look before deciding
6. Dead letters can be **requeued** for another attempt, individually or in bulk, with the attempt count reset, so "fix the bug, replay the traffic" is a supported workflow rather than a manual `redis-cli` exercise
7. Dead letters can be **purged**
8. Dead-lettering is recorded by the flight recorder, so it appears in `HW.REPLAY` and the dashboard alongside the rest of that message's lifecycle
9. The dead-letter list is **bounded**, with the same retention discipline as the backlog, so an unattended DLQ cannot exhaust the server it exists to protect
10. A documented sentinel restores today's unlimited-retry behaviour for anyone who genuinely wants it

### Requirement 3: Delayed Delivery

**User Story:** As a developer, I want to publish a message that arrives in five minutes, without adding a scheduler to my stack.

#### Acceptance Criteria

1. A message can be published with a **delay** or an **absolute delivery time** — one optional argument on the publish path, no new verb and no new concept
2. The client API is one optional parameter on `PublishAsync`, so the common case gains nothing to learn and the delayed case is a one-token change
3. A delayed message becomes visible to subscribers **no earlier** than its delivery time. Arriving late is acceptable; arriving early is not
4. **The timing guarantee and its granularity are documented honestly.** Promotion is driven by consumer activity rather than a server-side timer, so practical resolution is bounded by the client's backstop interval, and a channel with no consumers promotes nothing until one appears. A five-minute delay is not a five-minute alarm clock and the documentation must not imply it is
5. The design states why a **background server timer was not chosen**, so the trade-off is a recorded decision rather than an omission
6. Delayed messages survive broker restart and AOF replay
7. A delayed message is delivered to the groups registered **at delivery time**, not at publish time — a group that subscribes during the delay receives it, matching how the backlog already treats late subscribers
8. Delayed and immediate messages preserve **message-ID ordering among themselves**; a delayed message does not jump ahead of an immediate one published after its delivery time
9. Delayed delivery applies to **Pub/Sub only**. Delayed RPC is out of scope and the reason is documented: the caller is synchronously awaiting a reply, so a deliberately delayed request is a timeout waiting to happen
10. Cancelling or listing pending delayed messages is **not** in scope, and the omission is recorded rather than silently absent

### Requirement 4: Retry With Backoff

**User Story:** As an operator, I want a failing message retried on a schedule rather than hammered in a tight loop.

#### Acceptance Criteria

1. Requeue after a lease expiry can apply a **delay before the entry becomes claimable again**, reusing Requirement 3's mechanism rather than inventing a second one
2. The delay can grow with the attempt count, with a documented default schedule and a configurable cap
3. Backoff is **off by default for RPC** unless the design justifies otherwise, because a caller is waiting on a timeout and a delayed retry may simply guarantee that caller times out. The interaction with `CallTimeout` is analysed in the design rather than left to be discovered
4. The default schedule is chosen and documented with reasoning — a number without a rationale is a number nobody can safely change

### Requirement 5: Deduplication

**User Story:** As a developer, I want a redelivered request to not run my handler twice, without writing my own dedup table.

#### Acceptance Criteria

1. An `[Idempotent]` attribute on a request or message contract makes its handler run **at most once** per delivery identity within a window
2. The scope of the guarantee is stated **precisely and prominently**, including what it does *not* cover:
   - It deduplicates **Highway's own redelivery** — the same request ID or message ID arriving again after a lease expiry
   - It does **not** deduplicate a caller that issues a semantically identical request twice, because that is a different request with a different identity
   - Anything vaguer would be a claim the mechanism cannot keep
3. A duplicate RPC delivery returns the **original response** rather than re-running the handler or failing, so the caller is unaffected by the duplication
4. The window is configurable per contract, with a documented default
5. Deduplication state is bounded and expires. A dedup table that grows forever is the same class of defect as the recorder-name leak that feature 011's first run exposed
6. The handler-ran marker and the cached response are written so that a crash between them cannot produce a request treated as complete but with no response. The failure mode is analysed explicitly in the design
7. A contract without the attribute behaves exactly as it does today
8. ~~Suppressed duplicates are recorded by the flight recorder~~ — **dropped, and here is why.** The flight recorder is a *server* component and deduplication is necessarily a *client* one (Requirement 5's mechanism only works because the consumer knows the handler ran; the server cannot know). Reaching the recorder from a client would need a new `HW.*` command whose only purpose is to record, which is a worse trade than the gap it closes. Suppressions are logged by the client at debug level instead, and the limitation is stated here rather than quietly unmet

### Requirement 6: Configuration and Ease of Use

#### Acceptance Criteria

1. Every part of this feature has a working default and needs no configuration to be useful
2. Dead-lettering requires **no application code at all** — it is behaviour, not API
3. `new HighwayServerBuilder().Build()` still starts a working broker with no configuration, and the samples still run with none
4. Limits are configurable per server, and per service or channel where that is meaningful
5. Options are validated at build time with messages naming the offending value, consistent with `HighwayServerOptions.Validate`

### Requirement 7: Protocol, Storage, and Migration

#### Acceptance Criteria

1. `docs/HIGHWAY-PROTOCOL.md` is updated in this feature: new commands, key schema, entry framing, and the new invariants
2. **The entry framing change is a breaking storage-format change.** An attempt count cannot be added to an entry without changing how entries parse. This is stated explicitly, the upgrade path is documented (drain, or discard the data directory), and a **detection strategy** ensures a broker meeting an old entry says so rather than misparsing it into a corrupt payload
3. `ProtocolConformanceTests` stays green
4. New keys live under the existing `hw:` namespace and follow the established naming
5. Every new command that reads object-store state in `Prepare` is checked against the **watch-conflict rule** from 004.1 — a set or sorted-set read in `Prepare` registers a watch that the later exclusive lock fails. The design states, per command, where each read happens and why it is safe

### Requirement 8: Testing and Living Conformance

#### Acceptance Criteria

1. A test proves a permanently failing message **stops being redelivered** and lands in the DLQ — the defect that motivates this feature
2. A test proves dead-lettering is atomic: the entry is never in both the queue and the DLQ, and never in neither
3. A test proves a delayed message is **not** delivered before its time, and **is** delivered after it
4. A test proves delayed messages survive a broker restart with AOF
5. A test proves a duplicate delivery does not re-run an `[Idempotent]` handler and does return the original response
6. A test proves dedup state expires
7. Tests require no external infrastructure
8. All existing tests pass; `dotnet build` produces zero warnings
9. The samples demonstrate all three parts — a poison message reaching the DLQ, a delayed publish, and a duplicate suppressed — and are re-run within this feature with a `samples/RUNLOG.md` entry
10. `docs/product/product.md` and the roadmap are updated

## Non-Goals

- **Delayed RPC.** Requirement 3 AC9. The caller is awaiting a reply against a timeout.
- **Cancelling or listing pending delayed messages.** Recorded in Requirement 3 AC10; a later feature if wanted.
- **A cron or recurring-schedule engine.** One-shot delays only. Recurrence is Quartz's job, and pretending otherwise invites a scheduler into a broker.
- **Exactly-once delivery.** Not achievable across a network boundary without a transactional participant on the far side. Requirement 5 is explicit that it deduplicates Highway's redelivery and nothing more.
- **Automatic DLQ replay.** Requeue is operator-initiated. A queue that automatically re-feeds its own failures is a loop with extra steps.
- **Poison-message heuristics.** The attempt count is the signal. Guessing which payloads are "bad" is not Highway's business.
- **Per-message retry policy carried in the payload.** Configuration lives on the server and the contract, not in the data.

## Cross-References

- `docs/HIGHWAY-PROTOCOL.md` § Key Schema, § Entry Framing, § Cross-Command Invariants
- `src/Highway.Server/Commands/HwDequeueCommand.cs` — the lease sweep this feature bounds
- `docs/features/004.1-server-remediation/design.md` — the `Prepare`-phase watch-conflict rule
- `docs/features/002-observability/design.md` — the recorder these events must reach
- `docs/product/roadmap.md` § Beyond v1 — why these three and not the withdrawn nine
