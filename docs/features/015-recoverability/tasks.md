# Tasks: Recoverability

Failure context first — it is the highest-value item, it is mostly plumbing, and it is
useful on its own even if the retry tiers are never built.

---

## Phase 1 — A dead letter that explains itself

### - [ ] T1 — Failure context capture

Capture exception type, message, stack trace, node and timing where the handler exception is
caught, and thread it to the dead-letter path.

*Requirements:* R2.1, R2.2, R2.3
**Done when:** the type, message and stack of the **final** failure reach the dead letter,
plus `firstType` when the first failure differed. That single field answers the question an
operator actually asks — did this fail the same way every time, or did something change —
without storing every attempt.

### - [ ] T2 — Bounds and capture modes

Cap the message and stack at a documented size; honour feature 002's per-name capture modes.

*Requirements:* R2.5, R2.6
**Done when:** an unbounded stack trace cannot reach storage, and `HeadersOnly` yields type
and timing with no message or stack. An exception message routinely contains application
data — the same switch governs it, not a second one.

### - [ ] T3 — Dead-letter framing and `HW.DLQ`

Extend the framing with the failure block; surface the fields through `HW.DLQ PEEK`.

*Requirements:* R2.4, R2.7
**Done when:** a dead letter produced by a mechanism other than a handler exception says so
explicitly rather than showing blank fields.

> **Shippable here.** Everything above turns the DLQ from a tombstone into a diagnosis.
> Everything below changes how often messages reach it.

---

## Phase 2 — Retry tiers

### - [ ] T4 — Immediate retry, bounded by a deadline

In-process retry with no delay, stopping at the attempt count **or** a fraction of the lease,
whichever comes first.

*Requirements:* R3.1, R3.2, R3.4, R3.5
**Done when:** a transient failure succeeds without a redelivery, and the count can be set to
zero to restore today's behaviour exactly.

### - [ ] T5 — Prove the lease deadline holds

*Requirements:* **R3.3**
**Done when:** a handler slow enough to threaten the lease gets its retries cut short, and a
test proves the loop **cannot** outlive the lease. Without this the feature introduces a
duplicate-delivery bug: the message would be redelivered to another worker while this one is
still retrying it. Write the test first and watch it fail with the deadline removed.

Log when the deadline cuts a retry short — "why did my three retries become one?" is
otherwise unanswerable.

### - [ ] T6 — Delayed retry into the retry set

On exhausting immediate retries, move the message into the per-consumer `:retry` sorted set
with a growing delay. Reuse feature 013's structures; do not add a third.

*Requirements:* R4.1, R4.2, R4.3, R4.6
**Done when:** the message leaves the **live queue** rather than being pushed back onto it.

### - [ ] T7 — Head-of-line, as an observable property

*Requirements:* R4.4, R4.5
**Done when:** a test shows a message awaiting retry does not block work behind it that would
succeed. That is the strongest practical argument for this tier, so it is asserted rather
than assumed — and the ordering trade is documented where `PubSubBackoffEnabled` already
documents the same one.

---

## Phase 3 — Classification and counting

### - [ ] T8 — `[Unrecoverable]` and programmatic classification

*Requirements:* R5 (all)
**Done when:** a classified exception dead-letters on its first failure with its own reason
code, distinguishable from an exhausted-attempts dead letter. **The default list is empty** —
guessing which of an application's exceptions are permanent is how a message gets
dead-lettered on its first transient blip.

### - [ ] T9 — Attempt counting across tiers, and the off-by-one

*Requirements:* R6 (all)
**Done when:** `MaxDeliveryAttempts = 5` means exactly five deliveries. Immediate retries
happen within one delivery and are reported through the recorder, not stored in the entry;
delayed retries are deliveries and increment.

This closes `samples/RUNLOG.md` finding 9. It is a **behaviour change** for anyone who has
tuned the value — call it out in the protocol changelog rather than slipping it in. Doing it
here is right, because this is the feature that redefines what an attempt is.

### - [ ] T10 — Cancellation is not failure

*Requirements:* R1.3
**Done when:** a shutdown mid-handler does not consume an attempt. The worker loops already
separate the stop token from the work token, so the distinction is available — it just is not
used yet.

---

## Phase 4 — Reach and conformance

### - [ ] T11 — Pub/Sub gets the same treatment

*Requirements:* R7
**Done when:** `ISubscribe<T>` behaves as `IProcess<T>` does, and any deliberate difference is
stated rather than incidental.

### - [ ] T12 — Protocol document

Dead-letter framing, new reason codes, options, and the `MaxDeliveryAttempts` change.

*Requirements:* R8.1, R8.2
**Done when:** `ProtocolConformanceTests` is green and the changelog records the attempt-count
change as behaviour-affecting.

### - [ ] T13 — Constraints and product docs

*Requirements:* R8.3
**Done when:** C1.4 gains the diagnosis property and a constraint covers "a failure is
explicable". A dead letter nobody can diagnose satisfied the old wording, which is a sign the
wording was too weak.

### - [ ] T14 — Samples

A transient failure recovering on immediate retry, and a poison message dead-lettering **with
its exception visible in `dlq`**.

*Requirements:* R8.4, R8.5
**Done when:** the samples are re-run and `samples/RUNLOG.md` records what was found. The
existing `poison` command already dead-letters; what changes is that the output now says
*why*, which is the whole point of Phase 1.

### - [ ] T15 — Full verification

*Requirements:* R8.6

---

## The line that must not move

A handler signals failure by **throwing**, and nothing else. No result object, no status
enum, no boolean. If any task above introduces one, that task is wrong — the exception
already carries the type, message and stack that Phase 1 exists to record, and a return value
would force every handler to answer a policy question at the point it knows least about the
answer.
