# Tasks: Recoverability — Part 1, Diagnosable Failures

> **Scope reduced by engineering review, 2026-08-08.** Retry tiers deferred; see
> `requirements.md` § Deferred. What remains is one structural change and one behavioural
> change, in that order and never simultaneously.

**Lane 1 lands alone, first, with zero test edits.** That is not a preference — it is how the
refactor is proven correct.

---

## Phase 0 — Harmonise the claim/gate ordering (behavioural, precedes the refactor)

> **Found while starting T1, 2026-08-08.** The two loops T1 was to unify did not agree on when
> they claim work, and the difference was not cosmetic.

### - [x] T0 — Make RPC wait for a slot before claiming

```
before   RpcWorkerLoop     DequeueAsync (claim)  ->  _gate.WaitAsync  ->  spawn
after    RpcWorkerLoop     _gate.WaitAsync       ->  DequeueAsync     ->  spawn
         QueueWorkerLoop   _gate.WaitAsync       ->  QClaimAsync      ->  spawn   (unchanged)
```

`HW.DEQUEUE` starts the lease. A message claimed while the gate is full therefore has its
clock running on a node that cannot begin it, and if the wait outlives `Lease` the server
redelivers it elsewhere while this node still intends to process it — a duplicate produced by
load alone, with no failure involved.

**Measured, not inferred.** `WorkerSaturationTests` parks handlers on a `SemaphoreSlim` so the
gate is saturated deliberately, then reads the processing list — the claim ledger — directly.
With `WorkerConcurrency = 1` and four messages queued, RPC held **2** claims before the fix and
**1** after. The queue loop held 1 throughout.

*Requirements:* none — a pre-existing defect, not a 015 requirement.
**Done:** the gate precedes the claim; every early return and every exception path releases the
slot, or the loop starves itself. The test was seen failing at 2 first.

#### The test lied twice before it was isolated

Worth recording, because it nearly buried a real defect.

Both handlers park on **static** semaphores — they are reached through DI, so per-test
instances are not available — and `Dispose` releases 64 permits on both so a parked handler
cannot wedge shutdown. Whichever test ran first therefore handed the other one a handler that
**never parked**, and a handler that never parks cannot saturate a gate.

That produced two false readings in sequence:

| assertion | reading | what it meant |
|---|---|---|
| `<= 1` | both pass | vacuous — a mistyped key also reads 0 |
| `== 1` | RPC 1, queue 0 | contaminated — read as "the defect does not exist" |
| `== 1`, permits drained | RPC **2**, queue 1 | the defect, measured |

The middle row was written up as a disproof and committed as one. It was wrong. The lesson is
narrower than "be careful": **shared mutable test state must be reset in the constructor, not
only cleaned up in `Dispose`**, and an upper-bound assertion needs a companion assertion that
something actually happened, or zero passes it.

---

## Phase 1 — Structural (no behaviour change)

> **T1 depends on T0, which is done.** The loops now agree on claim/gate ordering, so a shared
> base can adopt it without changing either one's behaviour. `WorkerSaturationTests` is the
> guard that T1 keeps them agreeing.

### - [x] T1 — Extract `SingleMessageWorkerLoop`

Base for `RpcWorkerLoop` and `QueueWorkerLoop`: `RunAsync`, `DrainAsync`, the semaphore gate,
the in-flight list, `LoopWake`, the idempotency gate. Removes `QueueWorkerLoop`'s reach into
`RpcWorkerLoop.DefaultIdempotencyWindow`.

*Requirements:* R2.1, R2.4
**Done:** `SingleMessageWorkerLoop` holds `RunAsync`, `DrainAsync`, the gate, the in-flight
list, `LoopWake` and the idempotency window; the two loops supply `ClaimAsync`, `ProcessAsync`,
a target name and kind, and their own failure wording. 313 + 220 lines became 228 + 174 + 117.
`DefaultIdempotencyWindow` moved to the base, so `QueueWorkerLoop` no longer reaches into
`RpcWorkerLoop` for it. **All 630 tests pass, none edited.** Build clean, zero warnings.

#### Four behaviour changes, all to the queue loop, all deliberate

"No behaviour change" held for RPC. It did not hold exactly for the queue, because unifying
two implementations means choosing one of each pair — and where they differed, the RPC one was
the considered version. Named here rather than left to be discovered:

1. **Claim errors are now typed.** The queue had a bare `catch { release; throw }`, which sent
   a *permanent* transport error to `RunAsync`'s catch-all — logged as "unexpected", drain
   retried on the next wake. That is the tight-loop-on-poison shape 004.1's classification
   exists to prevent. It now takes the RPC path: transient backs off, permanent ends the pass.
2. **Handlers run on the thread pool.** The queue called `ProcessClaimedAsync` directly, so a
   synchronous-heavy handler stalled the drain until its first `await`. Now `Task.Run`, as RPC.
3. **In-flight bookkeeping** is prune-at-64 rather than a `ContinueWith` removal per message —
   one less continuation allocation per delivery, same observable drain behaviour.
4. **Log wording** is now `Worker loop started for queue 'x'` rather than `Queue worker started
   for 'x'`, so one message template covers both loops.

None is covered by an existing test, which is precisely why they are written down. (1) is the
only one with real consequence, and it is a strict improvement.

**`ChannelConsumerLoop` is not touched by this task.** It is batch-shaped, has no gate and no
in-flight list, and forcing it into the base means either losing batching or filling the base
with `if (batch)` branches — the wrong shape for one of three callers.

### - [x] T2 — `FailureReporter`, wired but inert

The shared helper, used by all three loops, with no server command behind it yet.

*Requirements:* R2.3
**Done:** `FailureReporter` plus `FailureTarget(Family, Name, Scope)` — the `SVC|Q|CH` grammar
`HW.DLQ` already parses, so T3 needs no second vocabulary. All three loops report through it:
services and queues via the base's `ProcessAndReleaseAsync`, `ChannelConsumerLoop` from its own
dispatch catch. It logs and nothing more.

Each loop supplies a **disposition** — what happens to the message now — because that is the
part the three genuinely disagree on and the part an operator needs: RPC *"it was not
acknowledged, so lease recovery will redeliver it"*, queue *"it will be redelivered"*, channel
*"it is acknowledged anyway, so the group queue never blocks"*. Folding those into one sentence
would have made the log uniform and wrong.

Reports pass `CancellationToken.None`: a handler that fails during shutdown is exactly the one
worth recording, so the report must not be cancelled along with the work.

Deriving `TargetName`/`TargetKind` from `FailureTarget` removed two abstract members from T1's
base — the reporter's vocabulary turned out to be the base's vocabulary too.

630 tests pass, none edited. Build clean.

**Phase 1 complete.** T0, T1 and T2 are in.

> **Ship Phase 1 separately.** It removes duplication that would otherwise be triplicated by
> everything below, and it is provable by tests that already exist.

---

## Phase 2 — The wire path

### - [x] T3 — `HW.FAIL`, one generic command

```
HW.FAIL SVC <service> <node> <requestId>  <json>
HW.FAIL Q   <queue>   <node> <messageId>  <json>
HW.FAIL CH  <channel> <group> <messageId> <json>
```

*Requirements:* R4.1, R4.2, R4.6
**Done:** arity 7 — `<kind> <name> <scope> <id> <type> <detail>`, the same shape for all three
families, which is what lets one command serve them. It rewrites the matching processing entry
in place, does **not** acknowledge, and returns `:0` for a message that is no longer there.
`WriteInteger` moved to `HighwayCommandBase`; `HW.FAIL` would have been its third copy.

`<type>` travels as its own argument rather than inside `<detail>` because the server needs it
to maintain `firstType`, and reading it out of a JSON blob would mean parsing JSON inside a
Garnet transaction on the failure path.

10 integration tests against a real embedded server — not unit tests, because the two things
most likely to be wrong are both invisible to one: whether the processing key is declared in
`Prepare`, and whether the pop-and-restore preserves the list. Both are covered, including
that a *miss* does not eat the entries it walked past.

**`HW.FAIL` is documented in `docs/HIGHWAY-PROTOCOL.md` in this same change**, because
`ProtocolConformanceTests` refused the commit otherwise. That gate has now fired five times.
Protocol version 3.1 — additive, since the failure block is a trailer.

##### Three wrong assumptions, all mine, all caught by running it

- **Arity counts the command name.** Registered 6 for six arguments; every call answered
  *wrong number of arguments*. `HW.QACK` is 4 for three arguments and says so.
- **`HW.ACK` returns `+OK`, not an integer** — it is idempotent by design and does not
  distinguish found from not-found. `HW.QACK` *does* return `:1`/`:0`. Four tests asserted the
  wrong shape. The asymmetry is real and pre-existing; the list length is what actually proves
  a match, so that is what the tests assert now.
- **`HW.RECEIVE` returns an array of `[id, payload]` pairs**, not a flat array.

None was a defect in the command. Worth recording anyway: the first debugging pass mis-paired
test names with failure messages through a sloppy regex over the TRX file, which sent me
looking for a server bug that was never there. A five-line probe printing the actual replies
settled it immediately — measuring beats inferring, again.

### - [x] T4 (framing) — The failure block on **both** framings

Optional trailing block on the **processing** entry and the **queue** entry.

*Requirements:* R4.3, R4.4
**Done (framing half):** the block is a trailer read from the end, so an entry without one
decodes byte-for-byte as before; all four decoders strip it, so it rides on every framing.
13 unit tests, including the collision guard — verified by removing the bounds check and
watching exactly that test fail. The sweep half is T5.

Original acceptance: an entry without the block decodes exactly as today — the block is **additive**,
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
LANE 0  T0    DONE  RPC claim/gate ordering
LANE 1  T1, T2      worker loops                → blocks lane 3
LANE 2  T3, T4, T5  server: command, framings, sweep   → independent
LANE 3  T6-T11      client: context, capture, wiring   → needs 1 and 2
LANE 4  T16-T18     docs and samples                   → needs 2

Order:  0  →  1 ∥ 2  →  3  →  4
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
