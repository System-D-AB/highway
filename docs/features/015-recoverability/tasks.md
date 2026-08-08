# Tasks: Recoverability — Part 1, Diagnosable Failures

> **Scope reduced by engineering review, 2026-08-08.** Retry tiers deferred; see
> `requirements.md` § Deferred. What remains is one structural change and one behavioural
> change, in that order and never simultaneously.

**Lane 1 lands alone, first, with zero test edits.** That is not a preference — it is how the
refactor is proven correct.

---

## Phase 1 — Structural (no behaviour change)

### - [ ] T1 — Extract `SingleMessageWorkerLoop`

Base for `RpcWorkerLoop` and `QueueWorkerLoop`: `RunAsync`, `DrainAsync`, the semaphore gate,
the in-flight list, `LoopWake`, the idempotency gate. Removes `QueueWorkerLoop`'s reach into
`RpcWorkerLoop.DefaultIdempotencyWindow`.

*Requirements:* R2.1, R2.4
**Done when:** the existing **626 tests pass with no test edited**. If a test needs changing,
the refactor changed behaviour and is wrong — revert and re-do it smaller.

**`ChannelConsumerLoop` is not touched by this task.** It is batch-shaped, has no gate and no
in-flight list, and forcing it into the base means either losing batching or filling the base
with `if (batch)` branches — the wrong shape for one of three callers.

### - [ ] T2 — `FailureReporter`, wired but inert

The shared helper, used by all three loops, with no server command behind it yet.

*Requirements:* R2.3
**Done when:** all three loops route handler exceptions through one place. Still purely
structural: the reporter does nothing but log, so behaviour is unchanged and Phase 1 remains
provable by the existing suite.

> **Ship Phase 1 separately.** It removes duplication that would otherwise be triplicated by
> everything below, and it is provable by tests that already exist.

---

## Phase 2 — The wire path

### - [ ] T3 — `HW.FAIL`, one generic command

```
HW.FAIL SVC <service> <node> <requestId>  <json>
HW.FAIL Q   <queue>   <node> <messageId>  <json>
HW.FAIL CH  <channel> <group> <messageId> <json>
```

*Requirements:* R4.1, R4.2, R4.6
**Done when:** it rewrites the matching processing entry, **does not acknowledge**, and
returns `0` for a message that is no longer there. One parser reusing `HW.DLQ`'s `SVC|Q|CH`
grammar — three per-family commands would triplicate parsing and contradict the command that
set the precedent.

Move `WriteInteger` to `HighwayCommandBase` while here; it is already copy-pasted in
`HwDlqCommand.cs:349` and `HwQAckCommand.cs:95`, and this would be a third copy.

### - [ ] T4 — The failure block on **both** framings

Optional trailing block on the **processing** entry and the **queue** entry.

*Requirements:* R4.3, R4.4
**Done when:** an entry without the block decodes exactly as today — the block is **additive**,
not a breaking change like 013's attempt count.

**Both framings, not just the processing one.** The sweep re-encodes a processing entry as a
queue entry on requeue; if the queue framing cannot carry the block, `firstType` is lost on the
first redelivery **and nothing reports it**. This is the one silent failure mode in the
feature.

**Why the entry and not a side key:** the sweep discovers which messages exhaust their attempts
only in `Main`, so it cannot declare `hw:fail:{id}` keys in `Prepare` — and Garnet rejects
touching an undeclared key. That wall was hit twice already, in 013 and 014.

### - [ ] T5 — Sweep attaches the block

*Requirements:* R3.1, R3.2
**Done when:** a dead letter carries type, message, stack, node and timing, read from the entry
the sweep is already decoding — no extra read, no N+1 inside the transaction.

---

## Phase 3 — Capture, bounds and surfacing

### - [ ] T6 — Build the context, truncated client-side

*Requirements:* R3.6
**Done when:** an oversized stack is capped **before the wire**, so bytes that will be
discarded are never transmitted.

### - [ ] T7 — Capture modes

*Requirements:* R3.5
**Done when:** `HeadersOnly` keeps type and timing and drops message and stack. An exception
message routinely contains application data, so feature 002's switch governs it — not a second
one.

### - [ ] T8 — Merge, and `firstType`

*Requirements:* R3.3, R4.5
**Done when:** a second report replaces the last failure and preserves `firstType` when the
first differed.

### - [ ] T9 — `HW.DLQ PEEK` and the recorder

*Requirements:* R3.4, R3.7, R4.7
**Done when:** the fields are surfaced; a dead letter with no context says so **explicitly**
rather than showing blanks; and `HW.FAIL` records a `DeliveryFailed` event so "failed five
times then recovered" is visible.

### - [ ] T10 — Best-effort reporting

*Requirements:* R5 (all)
**Done when:** a failing `HW.FAIL` is swallowed and logged **with the original exception
attached**, the loop survives, and the message is still recovered by the sweep. Diagnostic
writes must never outrank delivery — the same rule feature 002 states for the recorder.

### - [ ] T11 — Cancellation is not failure

*Requirements:* R1.3
**Done when:** shutdown mid-handler does not report a failure or consume an attempt. The loops
already separate the stop token from the work token; the distinction is simply unused.

---

## Phase 4 — Tests

Seven of these are ordinary. Two guard things that would otherwise fail silently.

### - [ ] T12 — The end-to-end test

`DeadLetter_CarriesExceptionTypeMessageAndStack` — handler → wire → sweep → DLQ against a real
embedded server.

*Requirements:* R3.1, R6
**Done when:** it passes without mocking any hop. Mocking one would hide exactly the failure
this feature exists to surface.

### - [ ] T13 — **Two workers, different exceptions**

`FirstType_SurvivesRequeue_AcrossTwoWorkers`: `node-a` throws `TimeoutException`, the lease
expires, `node-b` throws `InvalidOperationException`, attempts exhaust.

*Requirements:* R3.3, R4.4
**Done when:** the dead letter reports `type = InvalidOperationException` and
`firstType = TimeoutException`.

**This must land with T4, not after it.** It is the only test that proves the block survives
both a requeue and a change of worker — the single silent failure mode in the feature. A
one-worker version would pass even if the context were cached client-side, which would defeat
the whole reason the state is server-side.

### - [ ] T14 — **Reporting cannot break delivery**

`FailingReport_DoesNotMaskOrKill`: NSubstitute `IHighwayConnection` so `HW.FAIL` throws.

*Requirements:* R5
**Done when:** the original exception is logged, the message is **not** acknowledged, the sweep
still recovers it, and the loop is still running. `IHighwayConnection` is already an interface
and NSubstitute is already referenced, so this costs almost nothing — and it is the entire
content of the rule in T10, which would otherwise be unverified.

### - [ ] T15 — The remaining coverage

`FailureContext_IsTruncated_ClientSide`, `FailureContext_HonoursCaptureModes`,
`UnknownTarget_IsRejected_NamingTheExpectedForms`, `ReportingAnAcknowledgedMessage_ReturnsZero`,
`DeadLetterWithoutContext_SaysSo`.

*Requirements:* R3.5, R3.6, R3.7, R4.6

---

## Phase 5 — Conformance

### - [ ] T16 — Protocol document

`HW.FAIL` in the Command Index, the failure block on two framings, the `DeliveryFailed` event,
the new dead-letter fields.

*Requirements:* R6.1, R6.2
**Done when:** `ProtocolConformanceTests` is green. It must be updated in the same change that
registers the command — that gate has fired four times now.

Note the framing change is **additive**, unlike 013's, so it is a minor version bump.

### - [ ] T17 — Constraints

*Requirements:* R6.3
**Done when:** C1.4 gains the diagnosis property. A dead letter nobody can diagnose satisfied
the old wording, which is a sign the wording was too weak. Register the deferred retry tiers
and the Polly evaluation in § Open Decisions — **not** in a new `TODOS.md`, because a second
register is a second thing to get stale.

### - [ ] T18 — Samples

*Requirements:* R6.4, R6.5
**Done when:** `dlq poison.queue` shows the exception that caused the dead letter. The `poison`
command already dead-letters; what changes is that it now says **why** — which is the entire
point. Re-run and append to `samples/RUNLOG.md`.

### - [ ] T19 — Full verification

*Requirements:* R6.6

---

## Parallelization

```
LANE 1  T1, T2      worker loops                → blocks lane 3
LANE 2  T3, T4, T5  server: command, framings, sweep   → independent
LANE 3  T6-T11      client: context, capture, wiring   → needs 1 and 2
LANE 4  T16-T18     docs and samples                   → needs 2

Order:  1 ∥ 2  →  3  →  4
Conflict: lanes 1 and 3 both touch the worker loops.
          Land lane 1 first, alone, with zero test edits.
```

---

## The line that must not move

A handler signals failure by **throwing**, and nothing else. No result object, no status enum,
no boolean. If any task above introduces one, that task is wrong — the exception already
carries the type, message and stack that this whole feature exists to record, and a return
value would force every handler to answer a policy question at the point it knows least about
the answer.

And: **diagnostic writes never outrank delivery.** If reporting a failure can delay, block or
break the recovery of a message, the design has inverted its priorities.
