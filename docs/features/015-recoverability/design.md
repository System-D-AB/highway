# Design: Recoverability — Part 1, Diagnosable Failures

> **Revised after engineering review, 2026-08-08.** Scope reduced to a structural refactor
> plus failure context; retry tiers deferred. The review also found a **feasibility defect**
> in the first draft of this design — see [Decision 4](#decision-4-the-failure-block-lives-in-the-entry-not-a-side-key),
> which supersedes an earlier side-key approach that Garnet's locking model cannot support.

## Overview

Two changes, in this order and not simultaneously.

```
STEP A — structural, zero behaviour change
    SingleMessageWorkerLoop (abstract)
      ├─ RpcWorkerLoop      claim → handle → reply → ack
      └─ QueueWorkerLoop    claim → handle →         ack
         shares: RunAsync, DrainAsync, gate, in-flight, idempotency gate

    ChannelConsumerLoop  — batch-shaped, keeps its own shape
    FailureReporter      — used by all three

    PROOF: the existing 626 tests pass with no test edited.


STEP B — behavioural
    handler throws
      └─ FailureReporter → HW.FAIL <kind> <target> <node> <id> <json>
             server: rewrite the processing entry, attaching a failure block
             (does NOT ack — the lease sweep still owns recovery)
      └─ lease expires → sweep decodes the entry, block is already there
             ├─ requeue     → block rides the queue framing too
             └─ dead letter → block is attached to the dead letter
      └─ HW.DLQ PEEK shows type, message, stack, node, firstType
```

## Decision 1: Exceptions stay the only signal

No result object, no status enum, no boolean. Return → success. Throw → failure.

`Task<HandlerResult>` with `Success`/`Retry`/`Reject` was rejected: it looks more expressive
and is worse. Every handler must then answer a policy question at the point it knows least
about the answer, most will copy whatever the last one did, and the compiler cannot help. An
exception already carries the type, message and stack that Step B exists to record — and
`throw` is what a handler does when it *does not* handle a failure, so the honest path and the
lazy path agree.

**`OperationCanceledException` during shutdown is not a failure.** The attempt was abandoned.
The loops already thread a stop token separately from a work token for exactly this reason;
the distinction exists and is simply unused today.

## Decision 2: Refactor first, and only where the shapes actually match

`RpcWorkerLoop` and `QueueWorkerLoop` share `RunAsync`, `DrainAsync`, a semaphore gate, an
in-flight list, `LoopWake` and the idempotency gate. `QueueWorkerLoop.cs:59` already reaches
into `RpcWorkerLoop.DefaultIdempotencyWindow`, which is what a missing shared home looks like.

`ChannelConsumerLoop` is **batch**-shaped: `HW.RECEIVE` returns many messages, and it has no
gate and no in-flight list. Forcing it into the same base means either losing batching or
filling the base with `if (batch)` branches — the wrong shape for a third of its callers,
which is how bad base classes are born.

So: **a base for the two that match, and a narrow helper for the concern all three share.**

```
SingleMessageWorkerLoop (abstract)      FailureReporter
  ├─ RpcWorkerLoop                        used by all three
  └─ QueueWorkerLoop                      catch → build context → HW.FAIL
```

**Structural and behavioural change are not mixed.** Step A lands alone and is proven by the
existing suite. If a test needs editing to make it pass, the refactor changed behaviour and is
wrong. This is 014's T2 discipline, which prevented a fourth copy of a bug that had already
appeared three times.

## Decision 3: Failure context on the dead letter

Feature 013's dead-letter framing already anticipated this by carrying a reason *code* rather
than a message. It gains a failure block:

```
[i64 deadLetteredTicksUtc][u16 attempts][u16 reasonLen][reason]
[u16 failureLen][failure json][original entry]
```

JSON, not a fifth binary framing: this field is read by humans and by the dashboard, it is
variable-shaped, and it is written once per dead letter rather than per delivery — none of the
reasons the entry framings are binary apply. Plain `System.Text.Json`, matching the house
style; no source-generated context exists anywhere in `src/` and one type does not justify
introducing the pattern.

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

`firstType` appears only when the first failure differed from the last, which answers the
operator's real question without storing every attempt.

**Bounded, and truncated client-side.** Message and stack are capped before transmission, so
bytes destined to be discarded never cross the wire. An unbounded string on a dead letter is
the same defect class as an unbounded queue, and this feature would be a poor place to
reintroduce it.

**Capture modes apply.** An exception message routinely contains application data — the order
ID above is mild; a validation error quoting a payload is not. Feature 002's per-name modes
govern it: under `HeadersOnly`, type and timing survive and message and stack are dropped. The
same switch, not a second one.

## Decision 4: The failure block lives in the entry, not a side key

**The review's critical finding, and it invalidated the first draft.**

The obvious design is a side key — the client writes `hw:fail:{queue}:{messageId}` and the
sweep reads it when dead-lettering. **Garnet cannot do that.** Every key touched in `Main`
must be declared and locked in `Prepare`, and the sweep only discovers *which* messages have
exhausted their attempts in `Main` — it pops the processing list there. It cannot name the
keys in advance.

This is not theoretical: the same wall was hit twice during 013 and 014
(`Attempting to use a non-XLocked key in a Transactional context`). Both times the fix was
that the key was derivable from the command's arguments. **A per-message key is not.**

Secondarily, even if it were possible, it would be an N+1 read inside a transaction holding
exclusive locks on the queue and every processing list.

**So `HW.FAIL` rewrites the processing entry.** `hw:q:{queue}:proc:{node}` derives from the
command's own arguments, so it is lockable in `Prepare` by the ordinary rules. The command
scans that list for the message ID and rewrites the entry with the failure block attached.

The consequences are all favourable:

| | |
|---|---|
| No side keys | nothing to declare that is not already declared |
| No N+1 in the sweep | the block is already in the entry it is decoding |
| No orphans, no TTL | context dies with the entry it belongs to |
| Merge is free | read-modify-write on one entry the command already holds |

**The catch, and it would fail silently.** When the lease expires the sweep re-encodes the
processing entry as a **queue** entry. If the queue framing does not carry the failure block,
`firstType` is lost on the first redelivery and nobody notices — the dead letter simply shows
last-failure-only. So the optional failure block rides on **both** framings, and the
two-worker test in the test plan is the guard.

## Decision 5: One generic `HW.FAIL`, not one per family

```
HW.FAIL SVC <service> <node> <requestId>  <failureJson>
HW.FAIL Q   <queue>   <node> <messageId>  <failureJson>
HW.FAIL CH  <channel> <group> <messageId> <failureJson>
```

`HW.DLQ` already solved this exact problem with a `SVC | Q | CH` target grammar
(`HwDlqCommand.cs:41`). Three commands would mean three near-identical parsers and validators,
a protocol growing by three names instead of one, and an inconsistency with the command that
set the precedent.

**It does not acknowledge.** Reporting is orthogonal to delivery: the message stays in the
processing list and the lease sweep still owns recovery. Reporting a failure for a message
that is no longer there — already acked, or moved — returns `0` and does nothing, because a
worker reporting late is not an error worth failing.

`WriteInteger` is currently copy-pasted in `HwDlqCommand.cs:349` and `HwQAckCommand.cs:95`.
`HW.FAIL` would be a third caller, so it moves to `HighwayCommandBase` alongside
`WriteNullArray`, which was moved there for the same reason in 014.

## Decision 6: Reporting is best-effort and can never break delivery

```csharp
catch (Exception original)
{
    try
    {
        await _failureReporter.ReportAsync(original, ct).ConfigureAwait(false);
    }
    catch (Exception reporting)
    {
        // Diagnostic writes must never outrank delivery. The message is still not
        // acknowledged, so the lease sweep recovers it exactly as before — just
        // without context.
        _logger.LogWarning(reporting,
            "Could not report the failure of '{Id}' on '{Name}'; it will be recovered " +
            "without context. Original failure: {Original}", id, name, original);
    }
    // deliberately no ack
}
```

The original exception is **never masked**, the loop is **never terminated**, and delivery
semantics are unchanged when reporting fails. This is feature 002's rule for the flight
recorder applied to the same class of concern: *a mechanism that observes the system must
never be able to break it.*

Retrying the report was rejected — it holds the lease longer for a diagnostic write, and a
retry loop inside the error path is exactly where 3am debugging goes wrong.

## Testing

```
STEP A  refactor            existing 626 tests, unedited          ★★★
STEP B  ────────────────────────────────────────────────────────────
handler throws
  ├─ build context ──┬─ has stack                    T-1  ★★★ E2E
  │                  ├─ over cap → truncate          T-2  ★★
  │                  └─ HeadersOnly / Off            T-3  ★★  (PII)
  ├─ HW.FAIL ────────┬─ valid target                 T-1
  │                  ├─ unknown target               T-4  ★★
  │                  ├─ message already acked → :0   T-5  ★★
  │                  └─ second report → merge        T-6  ★★★ two workers
  └─ report itself fails                             T-7  ★★★ NSubstitute
lease sweep ─┬─ requeue → block survives             T-6
             ├─ dead letter → block attached         T-1
             └─ no block → "no context", not blanks  T-8  ★★
HW.DLQ PEEK  → fields surfaced                       T-1
```

| Test | Proves |
|---|---|
| `T-1 DeadLetter_CarriesExceptionTypeMessageAndStack` | **the reason to build this.** End-to-end: handler → wire → sweep → DLQ. Mocking any hop hides the failure this feature exists to surface |
| `T-2 FailureContext_IsTruncated_ClientSide` | R3.6 — oversized stack never crosses the wire |
| `T-3 FailureContext_HonoursCaptureModes` | R3.5 — an exception message is application data |
| `T-4 UnknownTarget_IsRejected_NamingTheExpectedForms` | consistent with `HW.DLQ`'s error |
| `T-5 ReportingAnAcknowledgedMessage_ReturnsZero` | R4.6 — a late report is not an error |
| **`T-6 FirstType_SurvivesRequeue_AcrossTwoWorkers`** | **R3.3 + R4.4.** `node-a` throws `TimeoutException`, `node-b` throws `InvalidOperationException`; the dead letter keeps both. This is the only version that proves the block survives requeue *and* a different worker — which is why the state is server-side at all. Guards a gap that would otherwise fail **silently** |
| **`T-7 FailingReport_DoesNotMaskOrKill`** | **R5.** NSubstitute the connection so `HW.FAIL` throws; assert the original exception is logged, the message is not acked, the sweep still recovers it, and the loop is still running |
| `T-8 DeadLetterWithoutContext_SaysSo` | R3.7 — a crashed worker produces an explicit "no context", not blank fields |

`T-6` and `T-7` are the two that justify the design. `T-6` guards the only silent failure mode
in the feature; `T-7` verifies the rule that decision 6 exists to state.

## Failure modes

| Codepath | Realistic failure | Test | Handled | Silent? |
|---|---|---|---|---|
| `FailureReporter` → `HW.FAIL` | broker blip mid-report | T-7 | best-effort | no — logged |
| `HW.FAIL` entry rewrite | message already gone | T-5 | returns `0` | no |
| **Block dropped on requeue** | **`firstType` lost** | **T-6** | both framings carry it | **would be silent** |
| Truncation | oversized stack | T-2 | client-side cap | no |
| Capture modes | PII in message | T-3 | 002 modes | no |
| Step A refactor | behaviour regression | existing 626 | n/a | no |

**One critical gap**, and it is why T-6 must land *with* the framing change rather than after.

## Risks

**A fifth entry-framing change, soon after 013's breaking one.** Mitigated by the block being
**optional and trailing**: an entry without it decodes exactly as it does today, so this is
additive rather than breaking, unlike 013's attempt count.

**Stack traces are an information-disclosure surface.** They can carry application data and
are served by `HW.DLQ PEEK` and the dashboard. Mitigated by capture modes, the truncation cap,
and making the choice visible in options rather than implicit.

**The refactor touches three loops at once.** Mitigated by it being purely structural and by
the existing suite being the acceptance criterion — with the rule that no test may be edited
to accommodate it.

## Parallelization

```
LANE 1  Step A refactor    SingleMessageWorkerLoop, FailureReporter   → blocks lane 3
LANE 2  Server side        HW.FAIL, framings, sweep, HW.DLQ           → independent
LANE 3  Client side        reporter wiring in three loops             → needs 1 and 2
LANE 4  Docs               protocol, constraints, samples             → needs 2

Order:  1 ∥ 2  →  3  →  4
Conflict: lanes 1 and 3 both touch the worker loops. Land lane 1 first, alone,
          with zero test edits.
```

## Cross-references

- `docs/features/013-reliable-delivery/design.md` — dead-letter framing; the `Prepare`-phase locking wall hit twice
- `docs/features/014-queue/design.md` — T2, the precedent for refactoring before building
- `docs/features/002-observability/design.md` — capture modes; "must never break the system it observes"
- `docs/features/004.1-server-remediation/design.md` — the watch-conflict rule governing `Prepare`
- `docs/product/constraints.md` — C1.4, and § Open Decisions where deferred items are registered
