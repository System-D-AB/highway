# Tasks: Pub/Sub Unification

> **Amended by engineering review, 2026-08-08.** Decision 1A (subscriber failure adopts queue
> semantics) added T2a and three tests to T15; a task-ordering bug was fixed (T5a must precede
> the deletions it unblocks — the review caught T7 deleting framings the double-write still
> used); T0 and T6 gained done-when clauses.

**Deletion is the deliverable.** A task that adds an abstraction to share code between two
delivery engines has misunderstood the feature. The success condition is that
`HwReceiveCommand.cs` does not exist and nothing replaced it.

**Order matters more than usual here.** The naming rule must land before anything derives a
name from it, and the subscriber path must be proven working before the old one is deleted —
otherwise a failure in the new path is indistinguishable from a failure in the deletion.

---

## Phase 0 — The naming rule, alone

### - [x] T0 — Reserve `@` in queue and channel names

Both enforcement points, per design Decision 1: `[Queue]`/`[Subscribe]` scanning throws at
startup naming the attribute and the character; `HW.QSEND`, `HW.PUBLISH` and `HW.SUBSCRIBE`
reject it with `HW_INVALID_ARG`.

*Requirements:* R1.2 (implicitly), design Decision 1
**Done when:** a test proves **both** paths reject it, and a test proves a name *without* `@`
is unaffected. The rejection message names the migration ("rename the queue/channel; `@` is
reserved for group queues as of protocol 4.0") — for an existing deployment this rule is
retroactively breaking, and the error is where the operator will meet it. Client-side alone is insufficient — the protocol is open and a non-Highway
client can issue `HW.QSEND` directly. Server-side alone turns a startup mistake into a runtime
one.

**Lands first and alone.** Every later task derives `{channel}@{group}`; deriving it before the
collision is closed means a window where a queue can shadow a group.

---

## Phase 1 — The new path, beside the old one

> Nothing is deleted in this phase. Both paths exist and both work, so a failure in the new one
> is unambiguous.

### - [x] T1 — `HW.PUBLISH` writes into group queues

Fan out into `hw:q:{channel}@{group}:q` using the queue entry framing, alongside the existing
writes to `hw:ch:{ch}:grp:{g}:q`.

*Requirements:* R1.1, R1.2, R1.4
**Done when:** a publish appears in **both** places with identical payloads, and the existing
pub/sub tests still pass untouched. Double-writing is deliberate and temporary — it makes T2
verifiable without deleting the thing it is being compared against.

The `Prepare` phase gains the derived queue keys and keeps the group-queue keys. Both sets are
derivable from the group list mirror key, so both are declarable.

### - [x] T2 — Subscribers consume through `QueueWorkerLoop`

`ChannelDescriptor` gains whatever `QueueWorkerLoop` needs; the engine starts a queue worker
per subscribed group against the derived name.

*Requirements:* R1.3, R3.6, R4.1
**Done when:** `ISubscribe<T>` handlers are invoked through `HW.QCLAIM`/`HW.QACK` with the
application source unchanged, and the end-to-end pub/sub integration tests pass with
`ChannelConsumerLoop` **not running**.

### - [x] T2a — `ExecuteSubscribersAsync`: attempt all, then fail (Decision 1A)

The executor stops swallowing: every local handler is attempted, failures are collected, and if
any threw the delivery fails — which is what `FailureReporter` then reports and the DLQ shows.

*Requirements:* R5.4, design Decision 5
**Done:** `ServiceExecutor.ExecuteSubscribersAsync` collects failures and throws after every
subscriber has had its attempt. A **single** failure is surfaced as itself rather than wrapped —
an `AggregateException` around one exception buries the type and message the dead letter exists
to show. `OperationCanceledException` during shutdown is rethrown untouched, not collected:
a clean stop must not consume an attempt. `ChannelResponse`'s doc records that `SuccessCalls`
no longer reaches a caller on the delivery path.

> **This task was marked complete while unimplemented.** `ServiceExecutor.cs` was never
> modified — the swallow at line 142 survived with its original comment — and
> `SubscriptionWorkerLoop` acknowledged unconditionally, so a throwing subscriber was still
> invisible. It was found by verification, not by the test that was supposed to cover it.

##### Two further defects, found by writing the tests properly

Both were invisible to the test that shipped with the task, because that test drove `HW.FAIL`
over raw RESP and never invoked a handler at all.

1. **The failure was reported against the wrong key.** `SubscriptionWorkerLoop.Target` named
   the bare **channel**, but `HW.FAIL Q` locks `hw:q:{name}:proc:{node}` and the list a
   subscriber actually claims from belongs to the **derived queue**. Every subscriber report
   therefore returned `:0`, and the dead letter said *"not reported"* while everything looked
   healthy. Fixed to name `_derivedQueueName`.
2. **`[Idempotent]` was still ignored for subscribers.** `SubscriptionWorkerLoop` had no dedup
   gate, so R5.4's promised remedy for the sibling re-run did not exist. The gate is now there,
   keyed on the derived queue so one group's suppression cannot hide another's delivery.

### - [x] T3 — Group workers default to concurrency 1

*Requirements:* R5.2
**Done when:** a test proves a subscriber group processes in order by default, and proves that
raising the setting parallelises it. `QueueWorkerLoop`'s gate makes this a one-line default,
but the default is the whole behaviour: shipping 8 would silently reorder every existing
subscriber.

### - [x] T4 — Deferred publish fans out at publish time

`PublishAsync(msg, delay)` writes into each group's `:delayed` with `AT`.

*Requirements:* R5.3
**Done when:** a deferred publish arrives after its delay for every group registered **at
publish time**, and a test asserts explicitly that a group registered *during* the delay does
**not** receive it. That test documents the semantic change; without it the change is
undiscoverable until someone hits it.

---

## Phase 2 — Delete the old engine

> Only after Phase 1 is green. Each deletion is a separate commit, because a deletion that
> breaks something must be bisectable.

### - [x] T5a — Remove the double-write from `HW.PUBLISH` first

*Requirements:* R2.1
**Done when:** `HW.PUBLISH` writes only to the derived queues, and the pub/sub tests still pass.

**This must open the phase.** The review caught the ordering bug: T7 deletes the channel
framings while T1's old write still encodes with them — deleting in the written order breaks
the build. The scaffolding comes down before the structure it was propping up.

### - [x] T5 — Delete `HwReceiveCommand` and `HwRackCommand`

*Requirements:* R2.1, R7.1
**Done when:** both files are gone, both are removed from the registration table and the
Command Index, and `ProtocolConformanceTests` is green. It checks both directions, so a command
removed from one and not the other fails — which is the point.

### - [x] T6 — Delete `ChannelConsumerLoop`

*Requirements:* R2.1
**Done when:** the file is gone and `FailureReporter`'s channel branch goes with it — a branch
the review found was nearly unreachable anyway, because the executor swallowed exceptions
before the loop's catch could see them. **`SingleMessageWorkerLoop`'s class remarks are updated
in the same change**: they currently explain why `ChannelConsumerLoop` is "deliberately not a
subclass", which becomes a reference to a class that does not exist. Stale doc is the same
defect as a stale diagram.

### - [x] T7 — Delete two entry framings

`EncodeChannelEntry` / `DecodeChannelEntry` and `EncodeGroupProcessingEntry` /
`DecodeGroupProcessingEntry`, plus `GetMessageId`.

*Requirements:* R2.2
**Done when:** `Envelope` has **two** framings, and `Envelope.CarryFailureBlock` has two call
sites instead of four.

> **This is the task that pays for the feature.** 015's failure block had to ride across four
> framings and rode across three; the miss was silent and was caught by one test that nearly
> was not written. Two framings halve that class of bug permanently.

### - [x] T8 — Collapse `CH` into `Q` in `HW.DLQ` and `HW.FAIL`

*Requirements:* R2.3, R2.4, R7.1
**Done when:** the target grammar is `SVC|Q`, the `CH` forms are removed from the protocol
document, and an old `CH` call is rejected with an error naming the accepted forms. A silently
ignored `CH` would look like a working call that recorded nothing.

### - [x] T9 — Delete the group key helpers and the channel delayed set

*Requirements:* R2.1
**Done when:** `HighwayKeys` has no `hw:ch:*:grp:*` helper and no `ChannelDelayed`.
`ChannelGroups`, `ChannelGroupList` and `ChannelSeq` survive — a channel is still a group set
with a sequence.

---

## Phase 3 — The break, handled honestly

### - [x] T10 — Refuse to start against pre-018 channel data

*Requirements:* R6.2, R6.3
**Done when:** a broker started against a data directory containing `hw:ch:*:grp:*` keys
refuses, naming the key count and the remedy; a clean broker starts normally; and the check
costs one `SCAN` that matches nothing.

**The worst outcome this prevents:** a broker that starts happily and serves an empty channel,
so the application looks healthy while every event is lost. Feature 013 established the
precedent — refusing beats misparsing, and both beat silence.

### - [x] T11 — Protocol document, version 4.0

*Requirements:* R6.1, R7.1, R7.2
**Done when:** `HW.RECEIVE` and `HW.RACK` are out of the Command Index, the two framings are
gone, the `SVC|Q` grammar is documented, and the changelog entry names **all three** semantic
changes from R5 — lost batching, default ordering, deferred-publish resolution. A changelog
that lists removed commands but not changed behaviour is the more dangerous half omitted.

Same-change rule: the document is updated in the commit that changes the code. That gate has
fired five times.

### - [x] T12 — `constraints.md`

*Requirements:* R7.3
**Done when:** C2.1–C2.5 are restated in queue terms with their guarantees intact; **C4.4's
"Pub/Sub group queues — no bound at all" row is deleted** because the structure is; and the C4
summary count is corrected to match.

C2.3 deserves a note: "a subscriber that is down receives what it missed" is now true *because
its queue holds the work*, not because pub/sub has its own retention. Same guarantee, one fewer
mechanism.

### - [x] T13 — `product.md` and `roadmap.md`

*Requirements:* R6.5
**Done when:** the three verbs are described with one engine underneath; 018 is placed
**before** 016 with the reason stated; and 016's requirements are annotated where they assume
group queues exist.

---

## Phase 4 — Verification

### - [x] T14 — Every existing pub/sub test, rewritten only where it names a removed command

*Requirements:* R7.4
**Done when:** every behavioural pub/sub test passes. A test that names `HW.RECEIVE` or
`HW.RACK` is rewritten to the queue commands; **a test whose assertions must change means the
behaviour changed**, and it must be justified against R5 or treated as a defect.

Keep a count in the commit message: tests rewritten mechanically vs tests whose expectations
moved. The second number should be small and every entry in it explainable.

### - [x] T15 — The tests this feature needs that did not exist

- `PublishReachesEveryRegisteredGroup_OrNone` — kill a group queue mid-fan-out; assert atomicity
- `GroupRegisteredDuringDelay_DoesNotReceive` — R5.3's semantic change, asserted
- `SubscriberGroupProcessesInOrder_ByDefault` — R5.2
- `QueueNamedLikeAGroup_IsRejected` — T0's collision, from both directions
- `PreUnificationChannelData_RefusesStartup` — T10
- `SubscriberFailure_DeadLettersWithContext` — proves Decision 1A end to end; **this is a new
  capability, not an inheritance** — today's pub/sub has never dead-lettered a handler failure.
  **It must drive a real throwing handler through a real node.** The first version issued
  `HW.FAIL` over raw RESP and never invoked one, so it passed against an executor that was
  still swallowing everything — a green test named for absent behaviour, which is worse than no
  test and is why the gap survived a review
- `SiblingHandlers_ReRunOnRedelivery` — pins R5.4's sharpest edge: handler A succeeded, handler
  B threw, the redelivery runs A again. The test *is* the documentation of the trade
- `IdempotentSubscriber_SuppressesRedeliveredDispatch` — `[Idempotent]` was silently ignored
  for subscribers; this proves it now works, keyed on `{channel}@{group}` + sequence.
  **Assert on the marker in the store, not the invocation count**: a successful handler
  acknowledges, so nothing redelivers and the count is 1 whether or not the gate exists. The
  count-only version passed against a loop that had no gate at all
- `PublishStillReportsGroupCount` — `PublishAsync` still returns how many groups received

*Requirements:* R3 (all), R5, R6.3

### - [x] T16 — Samples

*Requirements:* R7.5
**Done when:** the `low` / `InventoryLow` pub/sub scenario behaves identically across three
real processes, `dlq` works against a channel group, and `RUNLOG.md` records the run. The
sample output should be **unchanged** — that is the evidence the engine swap is invisible.

### - [x] T17 — Full verification

*Requirements:* R7.6
**Done when:** all tests pass, `dotnet build` is warning-free, and the deletion is counted:
lines removed, commands removed, framings removed. If the net line count went *up*, the feature
did not do what it set out to do.

---

## Parallelization

```
LANE 0  T0            naming rule            → blocks everything
LANE 1  T1..T4        new path beside old    → blocks lane 2
LANE 2  T5..T9        deletions              → blocks lane 3
LANE 3  T10..T13      break handling + docs
LANE 4  T14..T17      verification           → last

Order: 0 → 1 → 2 → 3 → 4

Little parallelism here, and that is correct: this is a sequence of
dependent structural changes, not independent workstreams. Lanes 1 and 2
touch the same files in opposite directions and must never overlap.
```

---

## The line that must not move

**Pub/Sub keeps every guarantee it has today.** At-least-once per registered group, acknowledged
means gone, a down subscriber receives what it missed. If any task above weakens one of those,
that task is wrong — the feature is about deleting a *second implementation*, not a *promise*.

And: **`PublishAsync` stays `PublishAsync`.** That it is fan-out underneath is a fact about the
server. A developer who has to know it has been handed the maintainer's problem, and the
product's one real advantage is that they do not have to.
