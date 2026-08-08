# Feature: Recoverability — Part 1, Diagnosable Failures

> **Scope reduced by engineering review, 2026-08-08.** This feature originally specified
> three retry tiers plus failure context. The review found that it would touch 11 existing
> files and add retry logic to three near-identical worker loops that have never been
> unified — the same shape as the defect feature 013 found living in three independently
> written requeue paths.
>
> **In scope:** a structural refactor with no behaviour change, then failure context on dead
> letters. **Deferred:** all three retry tiers — see [Deferred](#deferred-to-a-later-feature),
> where the thinking is preserved rather than discarded.
>
> The review also corrected this document. It previously called failure context "mostly
> plumbing". It is not: the client holds the exception and the **server** writes the dead
> letter, so it needs a new command and an entry-framing change.

## Introduction

Highway signals handler failure the right way already: `IProcess<T>.ProcessAsync` throws, the
message is not acknowledged, and it is redelivered. That contract stays.

What is missing is that **a dead letter does not say why it died.** The exception is discarded
where it is caught, so an operator running `HW.DLQ PEEK` learns that something failed six
times and must correlate logs across every worker to find out what threw.

That is what this feature fixes, and it is the highest-value part of the original three-tier
design — everything else changes how *often* messages fail; this changes whether a failure
can be diagnosed at all.

### The problem the reduced scope also solves

`RpcWorkerLoop` (313 lines) and `QueueWorkerLoop` (220) are structurally near-identical, and
`QueueWorkerLoop.cs:59` already reaches into `RpcWorkerLoop.DefaultIdempotencyWindow` — a
shared concept with no shared home. `ChannelConsumerLoop` (170) is batch-shaped and genuinely
different.

Adding anything to the failure path means touching all three. Doing the structural change
**first**, separately, and proving it with the existing suite is the lesson from 014's T2,
where extracting the shared lease sweep before building on it prevented a fourth copy of a
bug that had already appeared three times.

## Requirements

### Requirement 1: The Failure Signal Stays As It Is

**User Story:** As a developer, I want failure to mean "my handler threw", with nothing else to learn.

#### Acceptance Criteria

1. A handler that returns normally has succeeded; one that throws has failed. There is **no** result object, status enum, or boolean return
2. This applies to `IProcess<T>` and `ISubscribe<T>` alike
3. `OperationCanceledException` during shutdown is **not** a failure — the attempt was abandoned, not rejected, and must not consume an attempt it never really had. The worker loops already separate the stop token from the work token, so the distinction is available and merely unused
4. The contract is documented on `IProcess<T>` where a developer will read it

### Requirement 2: A Shared Worker-Loop Core (Structural, No Behaviour Change)

**User Story:** As a maintainer, I want one place to change when the failure path changes, not three.

**Done first and separately.** Structural and behavioural changes are not made simultaneously.

#### Acceptance Criteria

1. `RpcWorkerLoop` and `QueueWorkerLoop` share a base — they are genuinely alike: single message, semaphore gate, in-flight tracking, `LoopWake`, idempotency gate
2. **`ChannelConsumerLoop` is not forced into that base.** It is batch-shaped — `HW.RECEIVE` returns many messages and it has no gate or in-flight list. A three-way abstraction would be worse than the duplication it removes
3. A narrow `FailureReporter` is shared by **all three**, because failure reporting is the one concern they genuinely have in common
4. `QueueWorkerLoop`'s reach into `RpcWorkerLoop.DefaultIdempotencyWindow` is removed by the base
5. **The existing test suite passes unchanged.** No test may be edited to accommodate this refactor — if one needs changing, the refactor changed behaviour and is wrong

### Requirement 3: A Dead Letter Explains Itself

**User Story:** As an operator, I want the dead-letter entry to tell me why it died, without correlating logs across workers.

#### Acceptance Criteria

1. A dead letter carries the **exception type**, **message**, and **stack trace** of the final failure
2. It carries the **worker node** that last attempted it and the **time** of that attempt
3. It carries `firstType` **when the first failure differed from the last** — that single field answers the question an operator actually asks (*did this fail the same way every time, or did something change?*) without storing every attempt
4. `HW.DLQ PEEK` returns these fields, and the dashboard displays them
5. Failure detail honours feature 002's per-name **capture modes**. An exception message routinely contains application data, so the same switch governs it — not a second one
6. Failure detail is **bounded**: message and stack are truncated to a documented size, **client-side, before the wire**, so bytes that will be discarded are never transmitted
7. A dead letter produced without failure context — a worker that crashed before reporting — says so **explicitly** rather than showing blank fields

### Requirement 4: The Wire Path

**User Story:** As the system, I need the exception to reach the component that writes the dead letter.

**The review's central finding.** The client catches the exception; the **server** dead-letters
the message from its lease sweep. Nothing connects the two today.

#### Acceptance Criteria

1. A single generic command reports failure, using the same target grammar `HW.DLQ` already parses:
   ```
   HW.FAIL SVC <service> <node> <requestId> <failureJson>
   HW.FAIL Q   <queue>   <node> <messageId> <failureJson>
   HW.FAIL CH  <channel> <group> <messageId> <failureJson>
   ```
   **One command, not three per family** — three would triplicate parsing and validation, and would be inconsistent with `HW.DLQ`, which solved the same problem with a generic target
2. **`HW.FAIL` does not acknowledge.** The message stays in the processing list and the lease sweep still recovers it. Reporting is orthogonal to delivery
3. The failure block is written **into the processing-list entry**, not a side key. The sweep discovers which messages exhaust their attempts only in `Main`, so it cannot declare per-message keys in `Prepare` — and Garnet rejects touching an undeclared key. `hw:q:{queue}:proc:{node}` **is** derivable from the command's own arguments and is therefore lockable
4. **The failure block survives requeue.** When the sweep returns an entry to the queue, the block must ride on the queue framing too, or `firstType` is lost on the first redelivery. This would fail silently, so it is called out here
5. A second `HW.FAIL` for the same message merges: last failure replaces, `firstType` is preserved
6. Reporting a failure for a message that is no longer in the processing list — already acknowledged, or moved — is **not an error**. It returns zero and does nothing
7. `HW.FAIL` is recorded by the flight recorder as a `DeliveryFailed` event, so "failed five times then recovered" is visible rather than invisible

### Requirement 5: Reporting Never Breaks Delivery

**User Story:** As an operator, I want a diagnostic write to be unable to take down a consumer.

#### Acceptance Criteria

1. If `HW.FAIL` itself fails, the reporting exception is **swallowed and logged at warning with the original exception attached**
2. The original handler exception is **never masked** by a reporting failure
3. The worker loop continues; a failed report never terminates it
4. The message is still not acknowledged, so the lease sweep recovers it exactly as before — just without context
5. This is the same rule feature 002 states for the flight recorder: *a mechanism that observes the system must never be able to break it*

### Requirement 6: Conformance

#### Acceptance Criteria

1. `docs/HIGHWAY-PROTOCOL.md` updated: `HW.FAIL`, the failure block on two entry framings, the `DeliveryFailed` event, and the new dead-letter fields
2. `ProtocolConformanceTests` green
3. `constraints.md` C1.4 gains the diagnosis property — a dead letter nobody can diagnose satisfied the old wording, which is a sign the wording was too weak
4. Samples: `dlq` output shows the exception that caused the dead letter. The existing `poison` command already dead-letters; what changes is that it now says **why**
5. Samples re-run, `samples/RUNLOG.md` entry
6. All existing tests pass; `dotnet build` warning-free

## Deferred to a later feature

Cut by the review to keep this feature to one structural change plus one behavioural one. The
reasoning is preserved here so the follow-up does not start from scratch.

### Immediate retry

In-process retry with no delay, before the message is released. Targets faults already gone by
the next attempt — a deadlock, a connection reset. Highway's minimum retry latency is
currently the **lease**, five minutes by default, because redelivery is a crash-recovery
mechanism being asked to do a retry job.

**The sharp edge, recorded for whoever builds it:** an in-process retry loop runs *while the
lease is held*. If it outlives the lease, the message is redelivered to another worker while
this one is still retrying — a genuine duplicate. Bound the loop by a **deadline** (a fraction
of the lease) as well as a count, so a handler slow enough to threaten the lease gets zero
immediate retries. That is correct: a handler taking minutes is not experiencing a transient
fault.

### Delayed retry

After immediate retries, move the message out of the live queue into feature 013's `:retry`
sorted set with a growing delay. **The message leaves the live queue rather than being pushed
back onto it**, so it cannot block work behind it that would succeed — head-of-line blocking
is the strongest practical argument for this tier. Ordering is traded, which is the same trade
`PubSubBackoffEnabled` already makes explicit (`constraints.md` C3.4).

### Exception classification

`[Unrecoverable]` plus programmatic registration, so a `ValidationException` dead-letters at
once instead of burning five attempts × a five-minute lease proving it is still invalid. The
default list is **empty**: guessing which of an application's exceptions are permanent is how
a message gets dead-lettered on its first transient blip.

### Attempt counting across tiers

`MaxDeliveryAttempts` counts **deliveries**. Immediate retries happen within one delivery and
are not stored in the entry; delayed retries are deliveries and increment. The existing
off-by-one (`attempts > MaxDeliveryAttempts` permits N+1, visible in the samples as
`attempts 3` under a limit of 2) belongs with this work, because this is what redefines what
an attempt *is*.

### Why retry at all, rather than dead-lettering immediately

Recorded because it was asked, and the answer is the spine of the deferred work.

**The dead-letter queue's value is that it is rare.** Transient faults are common,
self-healing and high-volume. If each became a dead letter, a busy system would produce
thousands an hour, nearly all of which would have succeeded on a retry — the queue fills with
noise, nobody watches it, and the one genuinely poisoned message becomes invisible.

The related proposal — *dead-letter immediately and drain with a separate worker* — is half
right, and the right half is preserved above. A failing message **should** leave the hot path.
But it belongs in a **retry** structure, not the dead-letter queue:

| | Means | Who acts |
|---|---|---|
| Retry set | "needs a moment" | the system, automatically |
| Dead-letter list | "needs a human" | an operator, deliberately |

Conflating them removes the distinction that makes either useful, and does not even remove the
retry policy — a draining worker still has to decide when to give up, so the policy just moves
somewhere less convenient, after an extra round trip through storage.

### Also deferred

- **Polly / `Microsoft.Extensions.Resilience`.** The .NET built-in for retry pipelines, and the obvious "does the framework already do this?" answer. Moot until the tiers return; Highway currently takes no dependency beyond Garnet and StackExchange.Redis, so it is a real trade rather than a free win.
- **`ChannelConsumerLoop` structural unification.** It gets `FailureReporter` only. If a later feature makes the batch loop genuinely resemble the single-message ones, revisit.

## Non-Goals

- **A result object or status return.** Requirement 1. Exceptions are the signal.
- **Automatic dead-letter replay.** Requeue stays operator-initiated (feature 013).
- **A centralised error queue.** Per-queue dead letters isolate failures, which is the better default; "one place to look" is the dashboard's job — aggregation in the view, not the storage.
- **Retry policy carried in the message payload.** Policy lives on the server and the contract.
- **Circuit breaking or bulkheading.** Different problem.

## Cross-References

- `docs/features/013-reliable-delivery/design.md` — the dead-letter framing this extends
- `docs/features/014-queue/design.md` — `IProcess<T>` and the loops this refactors; T2 is the precedent for refactoring first
- `docs/features/002-observability/design.md` — capture modes, and the "must never break the system it observes" rule
- `docs/product/constraints.md` — C1.4, C3.4, and § Open Decisions where the deferred items are registered
- `samples/RUNLOG.md` finding 9 — the off-by-one, deferred with the attempt-counting work
