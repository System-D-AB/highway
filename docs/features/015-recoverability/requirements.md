# Feature: Recoverability — Retry Tiers, Failure Context and Classification

## Introduction

Highway signals handler failure the right way already: `IProcess<T>.ProcessAsync` throws, the
message is not acknowledged, and it is redelivered. That is the same contract NServiceBus,
MassTransit and MediatR use, and it is deliberate — a result object that says "partially
succeeded" is ambiguous about whether to retry, and every codebase that invents one invents a
different convention.

What happens *after* the exception is where Highway is thin.

| | Established practice | Highway today |
|---|---|---|
| Failure signal | exception | exception ✅ |
| Immediate retry | in-process, milliseconds | **none** |
| Delayed retry | seconds, growing | via lease expiry — **5 minutes** by default |
| Failure detail preserved | exception type, message, stack trace | **attempt count and `MAX_ATTEMPTS`** |
| Unrecoverable classification | declare exceptions that skip retries | **none** |

Three consequences, in the order they hurt:

1. **A dead letter does not say why it died.** An operator running `HW.DLQ PEEK` learns that something failed six times, and must go correlate logs across every worker to find out what threw. The exception is discarded at the point it is caught.
2. **There is no fast path for a transient fault.** Highway's minimum retry latency is the lease. A deadlock that would clear in twenty milliseconds costs five minutes, because redelivery is a *crash-recovery* mechanism being asked to do a *retry* job. Those are different jobs and Highway currently has only the first.
3. **Every failure costs the full budget.** A `ValidationException` that can never succeed still consumes five attempts × a five-minute lease — roughly 25 minutes — before it stops blocking. That arithmetic is why dead-lettering was undemonstrable in the samples until the broker gained a lease flag.

### Why retry at all, rather than dead-lettering immediately

Recorded because it is a reasonable question and the answer shapes the whole feature.

**The dead-letter queue's value is that it is rare.** Transient faults — deadlocks,
connection resets, concurrency conflicts, momentary timeouts — are common, self-healing, and
high-volume. If each became a dead letter, a busy system would produce thousands an hour,
nearly all of which would have succeeded on a retry. The queue then fills with noise, nobody
watches it, and the one genuinely poisoned message becomes invisible.

Retry tiers exist to keep the dead-letter queue meaning **"a human needs to look at this"**
rather than **"something twitched"**.

The related proposal — *move failures out of the live queue immediately and let a separate
worker retry them* — is half right, and the right half is preserved here. A failing message
**should** leave the hot path so it cannot block what is behind it. But it belongs in a
**retry** structure, not the dead-letter queue:

| | Means | Who acts |
|---|---|---|
| Retry structure | "needs a moment" | the system, automatically |
| Dead-letter queue | "needs a human" | an operator, deliberately |

Conflating them removes the distinction that makes either useful, and does not even remove
the retry policy — a DLQ-draining worker still has to decide when to give up, so the policy
just moves somewhere less convenient. Feature 013 already built the `:retry` and `:delayed`
sorted sets, so the delayed tier is mostly wiring.

## Requirements

### Requirement 1: The Failure Signal Stays As It Is

**User Story:** As a developer, I want failure to mean "my handler threw", with nothing else to learn.

#### Acceptance Criteria

1. A handler that returns normally has succeeded; a handler that throws has failed. There is **no** result object, status enum, or boolean return
2. This applies to `IProcess<T>` and to `ISubscribe<T>` alike
3. `OperationCanceledException` during shutdown is **not** a failure — it is an abandoned attempt, and the message is redelivered without consuming an attempt it never really had
4. The contract is documented on `IProcess<T>` where a developer will read it

### Requirement 2: A Dead Letter Explains Itself

**User Story:** As an operator, I want the dead-letter entry to tell me why it died, without correlating logs across workers.

**The highest-value part of this feature.** Everything else changes how often messages fail;
this changes whether a failure can be diagnosed at all.

#### Acceptance Criteria

1. A dead letter carries the **exception type**, **message**, and **stack trace** of the final failure
2. It carries the **worker node** that last attempted it and the **time** of that attempt
3. It carries the retry history in enough detail to distinguish "failed the same way six times" from "failed differently each time" — at minimum, the first and last failure
4. `HW.DLQ PEEK` returns these fields, and the dashboard displays them
5. Capturing failure detail is subject to the same payload-visibility rules as the flight recorder: an exception message can contain application data, so it honours the same capture modes
6. Failure detail is **bounded** — a stack trace is capped at a documented size, because an unbounded string on a dead letter is the same class of defect as an unbounded queue
7. A dead letter produced with no failure context — a message dead-lettered by a mechanism other than a handler exception — says so explicitly rather than showing blanks

### Requirement 3: Immediate Retry

**User Story:** As a developer, I want a deadlock to cost milliseconds, not a lease.

#### Acceptance Criteria

1. A failed handler is retried **in-process, without delay**, a small configurable number of times before the message is released
2. The default is documented with reasoning, and is small — this tier exists for faults that are already gone
3. **Immediate retries must complete well inside the lease.** A retry loop that outlives the lease causes the message to be redelivered to *another* worker while this one is still retrying it — a duplicate that `[Idempotent]` would suppress, but noisily. The design states how this is bounded and what happens if a handler is slow enough to threaten it
4. Immediate retries are visible: counted, logged, and distinguishable from lease-driven redeliveries
5. Setting the count to zero restores today's behaviour exactly

### Requirement 4: Delayed Retry

**User Story:** As an operator, I want a message that failed for a reason needing time to be tried again in seconds, not on the lease clock, and without blocking the queue behind it.

#### Acceptance Criteria

1. After immediate retries are exhausted, the message is moved out of the live queue into a **retry structure** with a delay, and returns when the delay elapses
2. It reuses feature 013's delayed/retry sorted sets rather than introducing a third mechanism
3. The delay grows across rounds, with a documented schedule and a cap
4. **A message awaiting retry does not block messages behind it.** This is the head-of-line property, and it is the reason the message leaves the live queue rather than being pushed back onto it
5. The interaction with ordering is stated: a delayed retry loses its place in the queue, which is the same trade `PubSubBackoffEnabled` already makes explicit
6. Delayed retries count toward `MaxDeliveryAttempts`

### Requirement 5: Exception Classification

**User Story:** As a developer, I want a validation failure to stop immediately instead of burning twenty-five minutes proving it is still invalid.

#### Acceptance Criteria

1. An exception type can be declared **unrecoverable**, so a message that fails with it skips all retry tiers and dead-letters at once
2. Declaration is possible both by attribute on the exception or contract, and programmatically for exception types the application does not own
3. An unrecoverable failure is dead-lettered with its reason recorded as such, distinguishable from an exhausted-attempts dead letter
4. The default classification list is empty. Highway does not guess which of an application's exceptions are permanent

### Requirement 6: The Attempt Count Means Something

**User Story:** As an operator, I want "attempted 6 times" to be a number I can reason about.

#### Acceptance Criteria

1. The recorded attempt count distinguishes **immediate retries**, **delayed retries**, and **lease-driven redeliveries** — three in-process retries and three crash recoveries are not the same event and must not read the same
2. `MaxDeliveryAttempts` counts **deliveries**, not in-process retries: a message handled by one worker that retries three times in-process has been *delivered* once
3. The existing off-by-one is resolved as part of this: `attempts > MaxDeliveryAttempts` currently permits N+1 deliveries while the name says N. Either the comparison changes or the option is renamed, and the decision is recorded
4. `HW.STATS` and the flight recorder report the tiers separately

### Requirement 7: Pub/Sub Gets This Too

#### Acceptance Criteria

1. Every requirement above applies to `ISubscribe<T>` as well as `IProcess<T>` — a subscriber that throws deserves the same treatment
2. Where behaviour must differ between the two, the difference is stated and justified rather than incidental

### Requirement 8: Conformance

#### Acceptance Criteria

1. `docs/HIGHWAY-PROTOCOL.md` updated: dead-letter framing gains failure context, new reason codes, any new keys or options
2. `ProtocolConformanceTests` green
3. `constraints.md` updated — C1.4 gains the diagnosis property, and a new constraint covers "a failure is explicable"
4. Samples demonstrate a transient failure recovering on immediate retry, and a poison message dead-lettering **with its exception visible**
5. Samples re-run, `samples/RUNLOG.md` entry
6. All existing tests pass; `dotnet build` warning-free

## Open Decisions

1. **Does immediate retry hold the lease, or extend it?** Holding is simpler and bounds the retry budget by the lease. Extending is more forgiving of slow handlers and adds a heartbeat-during-processing mechanism Highway does not have.
2. **Is the stack trace stored by default?** It is the most useful field and the most likely to contain application data. Default on, with the recorder's capture modes governing it, or default off and opt in?
3. **Does an unrecoverable exception still get a delayed retry round?** No is the obvious answer; the argument for yes is that "unrecoverable" is sometimes a misdiagnosis and one delayed attempt is cheap insurance.

## Non-Goals

- **A result object or status return.** Requirement 1. Exceptions are the signal.
- **Automatic dead-letter replay.** Requeue stays operator-initiated (feature 013). A queue that re-feeds its own failures is the loop this whole area exists to bound.
- **A centralised error queue across all queues.** Highway's per-queue dead letters isolate failures, which is the better default. "One place to look" is the dashboard's job — aggregation in the view, not in the storage.
- **Retry policies expressed per message in the payload.** Policy lives on the server and the contract, not in the data.
- **Circuit breaking or bulkheading.** Different problem.

## Cross-References

- `docs/features/013-reliable-delivery/design.md` — the dead-letter framing this extends, and the `:retry` / `:delayed` sets this reuses
- `docs/features/014-queue/design.md` — `IProcess<T>` and the worker loop this changes
- `docs/product/constraints.md` — C1.4, C3.4 (ordering under backoff), C4
- `samples/RUNLOG.md` finding 9 — the off-by-one Requirement 6 resolves
