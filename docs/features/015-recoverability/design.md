# Design: Recoverability

## Overview

Three tiers between "a handler threw" and "an operator has to look at this", plus the
failure context that makes the last one useful.

```
ProcessAsync throws
   │
   ├─ unrecoverable exception?  ──────────────────────────► dead letter  (reason: UNRECOVERABLE)
   │
   ├─ immediate retry × N        in-process, no delay, lease still held
   │      still failing
   │      ▼
   ├─ delayed retry × M          leaves the live queue → :retry set → returns after a growing delay
   │      still failing
   │      ▼
   └─ attempts exhausted        ──────────────────────────► dead letter  (reason: MAX_ATTEMPTS)
                                                             + exception type, message, stack,
                                                               node, first and last failure time
```

The tiers exist to keep the dead-letter queue **rare**. Transient faults are common and
self-healing; if each produced a dead letter, the queue would fill with messages that would
have succeeded on retry, nobody would watch it, and the one genuinely poisoned message would
be invisible. That is the whole argument, and it is why "just dead-letter immediately and
drain it with another worker" was rejected — see below.

## Decision 1: Exceptions stay the only signal

No result object, no status enum, no boolean. A handler returns → success. It throws →
failure.

The alternative — `Task<HandlerResult>` with `Success` / `Retry` / `Reject` — was rejected.
It looks more expressive and is worse: every handler must now decide a policy question at
the point of failure, most will get it wrong or copy whatever the last one did, and the
compiler cannot help. An exception already carries type, message and stack, which is exactly
what Requirement 2 needs to record. `throw` is also what a handler does when it *does not*
handle a failure, so the honest path and the lazy path agree.

One refinement: **`OperationCanceledException` during shutdown is not a failure.** The
message is abandoned, not rejected, and must not consume an attempt it never really had.
Highway's worker loops already separate the stop token from the work token for exactly this
reason, so the distinction is available.

## Decision 2: Failure context on the dead letter

The highest-value part, and mostly plumbing.

Feature 013's dead-letter framing already anticipated this by carrying a reason code rather
than a message:

```
[i64 deadLetteredTicksUtc][u16 attempts][u16 reasonLen][reason][original entry]
```

It gains a failure block:

```
[i64 deadLetteredTicksUtc][u16 attempts][u16 reasonLen][reason]
[u16 failureLen][failure json][original entry]
```

JSON rather than a fourth binary framing: this field is read by humans and by the dashboard,
it is variable-shaped, and it is written once per dead letter rather than per delivery — none
of the reasons the entry framings are binary apply.

```json
{
  "type": "System.InvalidOperationException",
  "message": "Order ORD-1 has no customer",
  "stack": "   at Orders.InvoiceWorker.ProcessAsync(...)",
  "node": "order-service-1",
  "firstFailedAt": "2026-08-08T10:00:01.2Z",
  "lastFailedAt": "2026-08-08T10:04:33.9Z",
  "firstType": "System.TimeoutException"
}
```

`firstType` is present only when the first failure differed from the last. That single field
answers the question an operator actually asks — *did this fail the same way every time, or
did something change?* — without storing every attempt.

**Bounded**, per Requirement 2 AC6: the stack trace is truncated to a documented size, and
the message likewise. An unbounded string on a dead letter is the same defect class as an
unbounded queue, and this feature would be a poor place to reintroduce it.

**Capture modes apply.** An exception message routinely contains application data — the order
ID above is a mild example, a validation error quoting a payload is not. Feature 002's
per-name capture modes govern this: under `HeadersOnly`, type and timing are kept and message
and stack are dropped. The same switch, not a second one.

## Decision 3: Immediate retry holds the lease

Requirement 3 AC3 is the sharp edge. An in-process retry loop runs **while the lease is
held**. If the loop outlives the lease, the message is redelivered to another worker while
this one is still retrying it — a genuine duplicate, which `[Idempotent]` would suppress but
noisily, and which without it is a double side effect.

Two options were considered:

- **Extend the lease while retrying** — forgiving of slow handlers, and requires a heartbeat-during-processing mechanism Highway does not have. That is a new failure mode (what happens when the heartbeat fails but the handler keeps going?) for a tier that is supposed to cost milliseconds.
- **Hold the lease, and bound the loop so it cannot approach it.**

The second is chosen. The retry budget is bounded by a **deadline**, not just a count: the
loop stops when either the attempt count is exhausted **or** a documented fraction of the
lease has elapsed, whichever comes first. A handler slow enough to threaten the lease gets
zero immediate retries, which is correct — a handler taking minutes is not experiencing a
transient fault.

The deadline is logged when it cuts a retry short, because "why did my three immediate
retries become one?" is otherwise unanswerable.

## Decision 4: Delayed retry leaves the live queue

This is the half of "just move it out of the way" that is right.

A message awaiting a delayed retry is moved into the per-consumer `:retry` sorted set that
feature 013 built, with a score of when it becomes claimable. It is **not** pushed back onto
the live queue, because a message that is going to fail again for the next thirty seconds
should not be at the head of a queue with work behind it that would succeed.

That is head-of-line blocking, and it is the strongest practical argument for the tier.

**Ordering is traded, and this is already Highway's documented position.** A delayed retry
loses its place. `PubSubBackoffEnabled` makes the same trade and defaults to off for exactly
this reason (`constraints.md` C3.4). The delayed tier therefore inherits the same
default-off posture where ordering is the stronger guarantee, and the design must not
quietly flip it.

## Decision 5: Why not "dead-letter immediately, drain with another worker"

Recorded because it is a reasonable proposal and the reasoning is the feature's spine.

The suggestion: skip retries, put every failure in the dead-letter queue, and have a separate
worker retry from there. Its appeal is real — one failure path, no retry policy in the hot
path, and the failing message leaves the live queue immediately.

It was rejected on three grounds:

1. **It destroys the DLQ's signal.** A dead-letter queue is worth watching precisely because entries in it are rare and mean "a human needs to decide". Fill it with deadlocks that would have cleared on the next attempt and it becomes a log nobody reads — at which point the genuinely poisoned message is invisible, which is the failure this whole area exists to prevent.
2. **It does not remove the retry policy, it relocates it.** The draining worker still has to decide how often and how long to retry, and when to give up. The policy now lives further from the handler that failed, and the message has made an extra round trip through storage to get there.
3. **It loses ordering information and doubles the write path** for the common case, which is a fault that would have resolved in milliseconds.

**What was kept from it:** the insight that a failing message should leave the live queue
rather than blocking it. That is Decision 4. The correction is only about *which* structure
it moves to — a retry set means "needs a moment", a dead-letter list means "needs a human",
and conflating them removes the distinction that makes either one useful.

## Decision 6: Classification is opt-in and empty by default

```csharp
[Unrecoverable]
public sealed class ValidationException : Exception { }

// or, for exception types the application does not own
services.AddHighway(o => o.Recoverability.TreatAsUnrecoverable<ArgumentException>());
```

Highway ships **no** default classification. Guessing which of an application's exceptions
are permanent is exactly the kind of helpfulness that produces a message dead-lettered on its
first transient blip because someone's retry wrapper happened to throw `ArgumentException`.

An unrecoverable failure dead-letters immediately with reason `UNRECOVERABLE`, distinguishable
from `MAX_ATTEMPTS` — because "this can never work" and "this did not work six times" call for
different responses from whoever is looking.

## Decision 7: The attempt count stops conflating three things

Today one `u16 attempts` field counts lease-driven redeliveries. It now needs to distinguish
three events that read identically and mean very different things.

`MaxDeliveryAttempts` counts **deliveries**. A message handled by one worker that retried
three times in-process has been delivered **once** — that is what the word means, and it
keeps the option's meaning stable as the tiers are added.

Immediate retries are therefore *not* stored in the entry: they happen entirely within one
delivery and are reported through the recorder and the failure context. Delayed retries
**are** deliveries and do increment.

**The off-by-one is resolved here** (Requirement 6 AC3). `attempts > MaxDeliveryAttempts`
permits N+1 deliveries while the option's name says N — visible in the samples as `attempts 3`
under a limit of 2. It becomes `>=`, so a limit of 5 means five deliveries. This is a
behaviour change for anyone who has tuned the value and is called out in the changelog rather
than slipped in; doing it inside this feature is right because this is the feature that
redefines what an attempt is.

## Options

```csharp
public sealed class RecoverabilityOptions
{
    public int ImmediateRetries { get; set; } = 3;
    public double ImmediateRetryLeaseFraction { get; set; } = 0.25;
    public int DelayedRetries { get; set; } = 3;
    public TimeSpan MaxFailureMessageBytes { get; set; }   // documented cap
    public bool CaptureStackTrace { get; set; } = true;
}
```

Three immediate retries because this tier targets faults already gone by the next attempt;
past three, waiting is more likely to help than trying harder. A quarter of the lease as the
deadline leaves ample margin on the default five-minute lease and makes the tier
self-disabling for genuinely slow handlers.

## Testing

| Test | Proves |
|---|---|
| `HandlerThrows_IsRetriedInProcess_BeforeAnyRedelivery` | R3.1 — the fast path exists |
| `ImmediateRetries_StopAtTheLeaseDeadline` | **R3.3** — the sharp edge; a slow handler cannot outlive its lease |
| `TransientFailure_SucceedsOnImmediateRetry_WithoutRedelivery` | the case the tier is for |
| `ExhaustedImmediate_MovesToRetrySet_NotBackToTheQueue` | R4.1, R4.4 — head-of-line |
| `MessageAwaitingRetry_DoesNotBlockTheQueueBehindIt` | R4.4 stated as an observable property |
| `DeadLetter_CarriesExceptionTypeMessageAndStack` | **R2.1** — the highest-value item |
| `DeadLetter_RecordsFirstTypeWhenFailuresDiffered` | R2.3 — same way every time, or not |
| `FailureContext_IsBounded` | R2.6 |
| `FailureContext_HonoursCaptureModes` | R2.5 — an exception message is application data |
| `UnrecoverableException_DeadLettersImmediately_WithItsOwnReason` | R5 |
| `CancellationDuringShutdown_DoesNotConsumeAnAttempt` | R1.3 |
| `MaxDeliveryAttempts_MeansExactlyThatManyDeliveries` | R6.3 — closes the off-by-one |

`ImmediateRetries_StopAtTheLeaseDeadline` and `DeadLetter_CarriesExceptionTypeMessageAndStack`
are the two that justify the feature. The first prevents a duplicate-delivery bug this feature
would otherwise introduce; the second is the reason to build it at all.

## Risks

**Immediate retry introduces duplicate side effects where none existed.** Today a handler
runs once per delivery. It will now run up to four times, and a handler that is not
idempotent between attempts — one that has already written a row before throwing — behaves
differently. This is real, is why the count is configurable and can be set to zero, and is
called out in the release notes rather than left for someone to discover.

**Stack traces on dead letters are an information-disclosure surface.** They can carry
application data and are served by `HW.DLQ PEEK` and the dashboard. Mitigated by capture
modes and by the bound, and by making the choice visible in options rather than implicit.

**Three tiers is more to explain than one.** The mitigation is that the default configuration
behaves sensibly with no knowledge at all, and the sentence that explains it is short: *retry
quickly, then slowly, then give up and tell someone.*

## Cross-references

- `docs/features/013-reliable-delivery/design.md` — dead-letter framing, `:retry` and `:delayed` sets
- `docs/features/014-queue/design.md` — the worker loop this changes
- `docs/features/002-observability/design.md` — capture modes governing failure context
- `docs/product/constraints.md` — C1.4, C3.4
- `samples/RUNLOG.md` finding 9 — the off-by-one closed here
