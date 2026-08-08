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

### - [x] T5 — Sweep attaches the block

*Requirements:* R3.1, R3.2
**Done:** read from the entry the sweep is already decoding — no extra read, no N+1 inside the
transaction. `HW.DLQ PEEK` surfaces `failureType`, `failureFirstType` (only when the failure
changed shape) and `failureDetail`; a dead letter with no report says so explicitly instead of
showing blanks (R3.7, so T9's PEEK half landed here).

#### The sweep was not the only place the block was dropped

T4 said "both framings" and I wired the two the task named — the sweep's requeue and the dead
letter. The two-worker test still failed, with the dead letter reporting *no failure recorded*.

An entry is rebuilt from its decoded parts in **four** places, and the trailer is not one of
the parts, so every one of them drops it:

| site | rebuild |
|---|---|
| `HW.DEQUEUE` | queue entry → RPC processing entry |
| `HW.QCLAIM` | queue entry → RPC processing entry |
| `HW.RECEIVE` | channel entry → group processing entry |
| lease sweep | processing entry → queue entry, or → dead letter |

The block survived the requeue and then vanished at the very next claim. Fixed with one
`Envelope.CarryFailureBlock(source, rebuilt)` used at all four sites rather than four
hand-written copies — the same reasoning as T1 and 014's T2, and the same failure mode 013
found living in three independently written requeue paths.

**This is the silent failure R4.4 warned about, and the warning still was not enough.** The
requirement named the requeue specifically, so the requeue is what I wired. What caught it was
the two-worker test, which is exactly the test T13 argues for: a one-worker version would have
passed.

---

## Phase 3 — Capture, bounds and surfacing

### - [x] T6 — Build the context, truncated client-side

*Requirements:* R3.6
**Done:** `FailureReporter.BuildDetail` writes `{message, node, at, stack?, inner?}`. Message
capped at 2,000 chars, stack at 8,000, each marked `… [truncated]` so a cut field never reads
as a complete one. Truncation keeps the **front** — the top frames say where it threw.

`inner` carries the inner exception's **type only**. "TimeoutException wrapping an
IOException" is the sentence an operator needs; the full chain is the application's own
logging's job, and serialising it would put unbounded nesting on the failure path.

### - [x] T7 — Capture modes

*Requirements:* R3.5
**Done — server-side, which is where the switch already lives.** `PayloadCapture` is a
*per-name server* setting; the client has no view of it, so a client-side implementation would
have needed a second copy of the configuration. `HW.FAIL` calls `FlightRecorder.CaptureFor`
and drops the detail unless the mode is `Full`.

The **type always survives**, under every mode. It is metadata rather than application data,
and it is the single field that makes a dead letter diagnosable at all — withholding it would
defeat the feature to protect something the type does not contain.

### - [x] T8 — Merge, and `firstType` *(landed inside T3)*

*Requirements:* R3.3, R4.5
**Done:** `HwFailCommand.BuildBlock` — `firstType` is set once, never overwritten, and only
when the type actually changed. Covered end to end by T13.

### - [x] T9 — `HW.DLQ PEEK` and the recorder *(PEEK landed with T5, recorder with T3)*

*Requirements:* R3.4, R3.7, R4.7
**Done:** `failureType`, `failureFirstType` (only when it changed) and `failureDetail`;
an explicit `failure: not reported…` when there is no block; `DeliveryFailed = 17` recorded by
`HW.FAIL` with the exception type as `ErrorCode`. **Dashboard display is not done** — see
below.

### - [x] T10 — Best-effort reporting

*Requirements:* R5 (all)
**Done:** the reporting exception is swallowed and logged at warning inside an
`AggregateException` with the original attached — losing the diagnosis is survivable, losing
the thing being diagnosed is not. The message is never acknowledged either way, so the sweep
recovers it exactly as before. Covered by `AFailingReport_IsSwallowedAndDoesNotMaskTheOriginal`
(T14), which is the whole content of R5 and would otherwise be unverified prose.

### - [x] T11 — Cancellation is not failure

*Requirements:* R1.3
**Done structurally:** `SingleMessageWorkerLoop.ProcessAndReleaseAsync` catches
`OperationCanceledException` **before** the reporter, so a shutdown mid-handler cannot reach
`HW.FAIL`. Same in `ChannelConsumerLoop`. Reports themselves pass `CancellationToken.None`, so
a *genuine* failure during shutdown is still recorded — the distinction runs both ways.

---

## Phase 4 — Tests

Seven of these are ordinary. Two guard things that would otherwise fail silently.

### - [x] T12 — The end-to-end test

`DeadLetter_CarriesExceptionTypeMessageAndStack` — handler → wire → sweep → DLQ against a real
embedded server.

*Requirements:* R3.1, R6
**Done when:** it passes without mocking any hop. Mocking one would hide exactly the failure
this feature exists to surface.

### - [x] T13 — **Two workers, different exceptions** (landed with T5, as required)

`FirstType_SurvivesRequeue_AcrossTwoWorkers`: `node-a` throws `TimeoutException`, the lease
expires, `node-b` throws `InvalidOperationException`, attempts exhaust.

*Requirements:* R3.3, R4.4
**Done when:** the dead letter reports `type = InvalidOperationException` and
`firstType = TimeoutException`.

**This must land with T4, not after it.** It is the only test that proves the block survives
both a requeue and a change of worker — the single silent failure mode in the feature. A
one-worker version would pass even if the context were cached client-side, which would defeat
the whole reason the state is server-side.

### - [x] T14 — **Reporting cannot break delivery**

`FailingReport_DoesNotMaskOrKill`: NSubstitute `IHighwayConnection` so `HW.FAIL` throws.

*Requirements:* R5
**Done when:** the original exception is logged, the message is **not** acknowledged, the sweep
still recovers it, and the loop is still running. `IHighwayConnection` is already an interface
and NSubstitute is already referenced, so this costs almost nothing — and it is the entire
content of the rule in T10, which would otherwise be unverified.

### - [x] T15 — The remaining coverage *(spread across T3, T5 and Phase 3)*

`FailureContext_IsTruncated_ClientSide`, `FailureContext_HonoursCaptureModes`,
`UnknownTarget_IsRejected_NamingTheExpectedForms`, `ReportingAnAcknowledgedMessage_ReturnsZero`,
`DeadLetterWithoutContext_SaysSo`.

*Requirements:* R3.5, R3.6, R3.7, R4.6

---

## Phase 5 — Conformance

### - [x] T16 — Protocol document

`HW.FAIL` in the Command Index, the failure block on two framings, the `DeliveryFailed` event,
the new dead-letter fields.

*Requirements:* R6.1, R6.2
**Done when:** `ProtocolConformanceTests` is green. It must be updated in the same change that
registers the command — that gate has fired four times now.

Note the framing change is **additive**, unlike 013's, so it is a minor version bump.

### - [x] T17 — Constraints

*Requirements:* R6.3
**Done when:** C1.4 gains the diagnosis property. A dead letter nobody can diagnose satisfied
the old wording, which is a sign the wording was too weak. Register the deferred retry tiers
and the Polly evaluation in § Open Decisions — **not** in a new `TODOS.md`, because a second
register is a second thing to get stale.

### - [x] T18 — Samples

*Requirements:* R6.4, R6.5
**Done when:** `dlq poison.queue` shows the exception that caused the dead letter. The `poison`
command already dead-letters; what changes is that it now says **why** — which is the entire
point. Re-run and append to `samples/RUNLOG.md`.

### - [x] T19 — Full verification

*Requirements:* R6.6
**Done:** 668 tests green across four projects, `dotnet build` clean with zero warnings,
samples re-run across three real processes with a `RUNLOG.md` entry.

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


---

## Not done, and named rather than left to be discovered

**The dashboard does not display the new fields.** R3.4 asks for `HW.DLQ PEEK` **and** the
dashboard; PEEK is done and the CLI sample proves it end to end, but the dashboard's dead-letter
view still shows the pre-015 columns. It is a separate package (`Highway.Server.Dashboard`,
feature 011) with its own view code, and bolting a UI change onto the tail of a protocol feature
is how both get done badly. **Registered as outstanding for the next dashboard change** — not
silently dropped.

**Two reply-shape warts, both pre-existing, both left alone:**

- `HW.DLQ PEEK` labels a queued message `requestId`, because a queue reuses the RPC entry
  framing and the command branches on *framing* rather than on *family*. Misleading exactly
  where an operator is reading carefully — but renaming a reply field is a protocol change and
  does not belong bolted onto this feature.
- `HW.ACK` answers `+OK` while `HW.QACK` answers `:1`/`:0`. Both are defensible alone; together
  they are an inconsistency that cost time during T3 and will cost someone else time again.

**The `MaxDeliveryAttempts` off-by-one survives** — `attempts 3` under a limit of 2, visible in
the samples. Deferred with the attempt-counting work, which is what redefines what an attempt
*is*. Registered in `constraints.md` § Deferred.
