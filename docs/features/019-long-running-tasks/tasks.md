# Tasks: Long-Running Tasks

**The deliverable is a lease that can be kept alive, and a bound that keeps lease recovery
alive with it.** A task that makes renewal unlimited has misunderstood the feature: the cap in
T5 is not a safety rail bolted on afterwards, it is the reason the default in T4 is
defensible.

**Order matters.** The server command lands before anything calls it, and the cap lands with
the renewal loop rather than after it — a renewal loop merged without its bound is a window in
which a hung handler cannot be recovered.

**One measure of success is a diff that is not there.** `docs/HIGHWAY-PROTOCOL.md` § Entry
Framing and § Key Schema must be **unchanged** when this feature is done. If either moved,
Decision 1 was abandoned and the change is breaking.

---

## Phase 1 — The server command

### - [x] T1 — `HW.TOUCH SVC|Q` renews a claimed entry

```
HW.TOUCH SVC <service> <node> <requestId>   →   :1 | :0
HW.TOUCH Q   <queue>   <node> <messageId>   →   :1 | :0
```

Arity **5**, exact. Read `HwFailCommand.cs` first and mirror it: same target parsing, same
`procKey` derivation, same pop-find-rewrite-push shape, same `:1`/`:0` reply. The only
difference is what the rewrite does — `Envelope.EncodeRpcProcessingEntry` with
`claimTicks = DateTime.UtcNow.Ticks`, everything else carried through unchanged.

`Q` must use `TryReadDerivedIdentifier` (not `TryReadIdentifier`) so `{channel}@{group}`
is accepted; `SVC` and the node argument keep the strict form.

*Requirements:* R1.1, R1.2, R1.3, R1.4, R1.5

**Done when:** a live entry's claim timestamp moves and the reply is `:1`; an acknowledged
message replies `:0` and writes nothing; an unknown target is rejected with `HW_INVALID_ARG`
**naming the accepted forms**; and two tests assert the properties that make renewal safe to
compose — the entry is **still in the processing list** afterwards (it did not acknowledge) and
its **attempt count is unchanged** (it did not consume a retry).

### - [x] T2 — The failure block survives renewal

`HW.TOUCH` re-encodes a processing entry, which makes it the **fifth** re-encode site. Call
`Envelope.CarryFailureBlock` exactly as the other four do.

*Requirements:* R1.6

**Done when:** a test reports a failure with `HW.FAIL`, renews with `HW.TOUCH`, lets the lease
expire, and asserts the dead letter still carries `firstType`.

> **Lands with T1, not after it.** 015's design records that this exact class of miss "would
> fail **silently**" — the block vanishes and the dead letter merely shows last-failure-only,
> on a path nobody watches. It was found last time because one test was nearly not written.

### - [x] T3 — Protocol document, version 4.3

*Requirements:* R7.1, R7.2

**Done when:** `HW.TOUCH` is in the Command Index with arity 5 and 2 forms; a command section
documents both target forms, the reply, the keys touched, and the three properties it does
*not* have (no acknowledgement, no attempt increment, no byte movement); the changelog carries a
**4.3** entry describing the additive command **and** the behaviour change from R4; and
`ProtocolConformanceTests` is green.

**Same-change rule:** the document moves in the commit that adds the command. That gate has
fired on every feature since 007.

**And the diff that must not exist:** § Entry Framing and § Key Schema unchanged.

---

## Phase 2 — The client renews

> Only after Phase 1. There is nothing to call before then, and T7 cannot pass.

### - [x] T4 — Renewal options

Two options on `HighwayOptions`, validated at startup:

```csharp
public TimeSpan LeaseRenewalInterval { get; set; } = TimeSpan.FromMinutes(1);
public TimeSpan MaxProcessingTime    { get; set; } = TimeSpan.FromMinutes(15);
```

*Requirements:* R2.5, R2.6, R3.1, R3.4

**Done when:** `LeaseRenewalInterval` must be positive; `MaxProcessingTime` must be
non-negative with `TimeSpan.Zero` meaning renewal off; and **both XML doc comments state the
relationship to the server's `Lease`** — the client cannot read it, so the 5× default headroom
and the consequence of lowering `Lease` below roughly 3× the interval exist only in the doc, and
that is the whole mitigation for the risk.

### - [x] T5 — Automatic renewal in `SingleMessageWorkerLoop`, bounded

Renewal goes in `ProcessAndReleaseAsync` as a `using` disposable so **every** exit path stops
it — success, throw, cancellation, and cap — without adding a second `finally`. All three loops
inherit it; `Target` already carries the family and name, so nothing new is plumbed through.

Behaviour on each renewal reply: `:1` continue · `:0` stop quietly (the message is no longer
ours) · throw → log at Debug and continue.

*Requirements:* R2.1, R2.2, R2.3, R2.4, R3.1, R3.2, R3.5

**Done when:** a slow handler is delivered **once** where today it is delivered several times;
no renewal is sent after the handler completes; reaching `MaxProcessingTime` **stops** renewal
so the ordinary sweep requeues, increments attempts and eventually dead-letters; and a renewal
that throws on every attempt leaves the handler, the acknowledgement and the loop untouched.

> **The cap is not a follow-up task.** Merged without it, this is a change that removes lease
> recovery for any handler that hangs. The capability and its bound are one commit.

### - [x] T6 — The cap is loud; renewals are silent

*Requirements:* R3.3, R3.5

**Done when:** each renewal logs at `Debug` only; reaching the cap logs at `Warning` naming the
target, the message id and the elapsed time, **and** emits a new `HighwayEventType`
(`LeaseRenewalExhausted`) so it reaches the dashboard and `HW.REPLAY`.

Recording every renewal was considered and rejected: at one per minute per in-flight message it
would evict the events worth keeping with the least interesting thing Highway does.

### - [x] T7 — Warn when the drain window cannot fit a long handler

*Requirements:* R5.1, R5.2, R5.3

**Done when:** an engine whose `MaxProcessingTime` exceeds its `DrainTimeout` warns **once at
startup**, naming both values and stating that long handlers are cancelled mid-flight on
shutdown and redelivered — and **starts anyway**. Some deployments prefer a fast drain and
accept redelivery; the unacceptable option is the silent surprise. Feature 014's memory-only
queue warning is the precedent.

---

## Phase 3 — The pattern for hours, not minutes

> Independent of Phases 1 and 2 — markdown only, can start immediately.

### - [x] T8 — The long-running work cookbook

*Requirements:* R6.1, R6.2, R6.3, R6.4, R6.5

**Done when** a document under `docs/product/` covers:

1. **Chunk-and-checkpoint** — claim, one slice, checkpoint to *your* database, enqueue the next
   slice, acknowledge. With the five things it buys over one long handler: survives deploys,
   durable and queryable progress, free parallelism via `WorkerConcurrency`, a poison slice that
   dead-letters alone, and a lease that stops mattering.
2. **Guard first** — every handler opens by checking the state it expects. One line that
   delivers idempotency, out-of-order tolerance and stale-timeout safety together.
3. **`[Idempotent(WindowSeconds = n)]`** with `n` above the worst-case duration — including the
   correct shape, because it is `WindowSeconds` (an `int`) and not a `TimeSpan`, and a 12-minute
   handler that leaves the 5-minute default loses the protection exactly when it needs it.
4. **When to renew and when to chunk** — renewal is for the handler that is *slow* (up to
   `MaxProcessingTime`); chunking is for the job that is *long*. Naming the boundary is the
   point of the document.
5. **`MaxPayloadBytes` is 1 MiB** — long work over large data passes a blob reference.

### - [x] T9 — `constraints.md`

*Requirements:* R7.3

**Done when:** C1 carries a new constraint — *a handler may run longer than the lease without
duplicate execution* — with its status; the R4 trade (a hung handler now recovers after
`MaxProcessingTime`, not `Lease`) is recorded as a numbered decision rather than a footnote; and
**per-queue lease is registered under Deferred work** with the reason, so the idea is not
re-proposed from scratch.

### - [x] T10 — `product.md` and `roadmap.md`

*Requirements:* R7.4

**Done when:** 019 appears in the implementation status table; the roadmap entry states what it
fixes and what it deliberately does not (per-queue lease, cancellation, progress reporting); and
the queue row in the three-verbs table notes that a handler is no longer bounded by the lease.

---

## Phase 4 — Verification

### - [x] T11 — The tests this feature needs

*Requirements:* R1 (all), R2 (all), R3 (all), R5

- **`SlowHandler_ExceedsLease_IsDeliveredExactlyOnce`** — `Lease = 2 s`,
  `LeaseRenewalInterval = 500 ms`, handler sleeps 6 s. Asserts **one** execution and an empty
  queue. **Watch it fail first against `MaxProcessingTime = Zero`** — 3–4 executions — because a
  test for duplicate suppression that has never failed proves the harness ran, not that
  duplicates were suppressed. That is 016's C4.5 discipline.
- **`RenewalCap_StopsRenewing_AndMessageIsRecovered`** — `MaxProcessingTime = 2 s`, handler
  never returns: renewal stops, the sweep requeues, attempts increments, the Warning is logged
  and `LeaseRenewalExhausted` is in the recorder.
- **`FailingTouch_DoesNotBreakTheHandler`** — NSubstitute the connection so `HW.TOUCH` always
  throws: the handler completes, the message is acknowledged, the loop is still running.
- `Touch_DerivedGroupQueue_RenewsSubscriberLease` — 018's dividend, via `{channel}@{group}`.
- `Touch_PreservesFailureBlock` (T2), `Touch_DoesNotAcknowledge`,
  `Touch_DoesNotIncrementAttempts`, `Touch_AcknowledgedMessage_ReturnsZero`,
  `Touch_UnknownTarget_NamesAcceptedForms` (T1).
- `MaxProcessingTimeZero_SendsNoTouchCommand`, `Renewal_StopsWhenHandlerCompletes` (T5).
- `DrainTimeoutBelowMaxProcessingTime_WarnsOnce` (T7).

**Done when:** all pass, and the three starred in the design (`SlowHandler…`,
`RenewalCap…`, `FailingTouch…`) each fail when their mechanism is removed.

### - [x] T12 — Samples

*Requirements:* R7.5

**Done when:** a sample demonstrates both shapes across real processes — a slow handler carried
by renewal, and a chunked job that checkpoints — `dlq` still works against both, and
`RUNLOG.md` records the run. The existing samples' output must be **unchanged**: renewal is
invisible to work that never needed it.

### - [x] T13 — Full verification

*Requirements:* R7.6

**Done when:** all tests pass, `dotnet build` is warning-free, and the additive claim is
**checked rather than asserted** — one new command (18 → 19), one new recorder event, two new
client options, and **zero** changes to § Entry Framing and § Key Schema. If either of those
two sections moved, the change is breaking and the version is wrong.

---

## Parallelization

```
LANE 1  T1..T3     server command + protocol      → blocks lane 2
LANE 3  T8..T10    cookbook + docs                → independent, start now
LANE 2  T4..T7     client renewal + cap + warning → needs lane 1
LANE 4  T11..T13   verification                   → last

Order:  1 ∥ 3  →  2  →  4

Lane 3 is markdown only and shares no file with the others, so it is genuinely
parallel. Lanes 1 and 2 are strictly sequential — there is no point wiring a
client to a command that does not exist, and the test that justifies the feature
needs both halves.
```

---

## The line that must not move

**Renewal extends a deadline. It does not remove one.**

Every recovery path Highway has — lease sweep, attempt counting, dead-lettering with failure
context, dead-node pruning — must behave after this feature exactly as it does today, just
later for handlers that are demonstrably still alive. If any task above makes a message
un-recoverable, that task is wrong.

And: **`SendAsync` stays `SendAsync`.** A developer whose handler takes ninety seconds should
need to know nothing, change nothing, and configure nothing. That the broker is being told
"still working" once a minute is a fact about the client, and handing it to the developer would
be handing them the maintainer's problem.


---

## What execution found

**The spec held up.** Every open decision was answered as recommended, and nothing in the design
had to change on contact — unusual enough in this project to be worth saying.

**One pre-existing warning surfaced.** 017's `CleanAndByeForeverAsync` passed a nullable
`_options.Server` to `ConnectAsync`. It only appeared on a `--no-incremental` build, which is
worth knowing: an incremental build had been reporting zero warnings while one existed. Fixed by
throwing with a message rather than a null-forgiving operator, so a real regression later cannot
hide behind it.

**Two tests were wrong before the code was.** `Touch_PreservesTheFailureBlock` slept 120 ms
against an 800 ms lease, so nothing ever expired and the dead letter it read was never written.
A timing test whose interval is shorter than the timeout it is exercising tests nothing — the
same shape of mistake as 017's first suspect test.

## R3.3 was not satisfiable as written

**Corrected after the fact.** T6 was marked done with only the Warning log; the recorder event
R3.3 also asked for was never wired, and it turns out it could not be.

**The cap is a client-side decision.** When renewal stops, *nothing is sent* — so there is no
command for the server to record, and the flight recorder only ever sees what crosses the wire.
Recording it would need new protocol surface: a command whose only purpose is "note that I gave
up", issued at exactly the moment the client has decided to stop talking about this message.

The cap is surfaced client-side instead — Warning log plus an
`highway.processing_cap_exceeded` event on the client's `ActivitySource`, where a tracing
backend can see it alongside the handler span it belongs to.

`HighwayEventType.ProcessingCapExceeded` is kept and documented as never-recorded rather than
deleted, because reusing the number later would make an old replay mean something new.

> **The general fact worth carrying forward:** the flight recorder is a *server-side* facility.
> Any requirement phrased as "record event X" for something the client decides is unsatisfiable
> unless a command carries it. Three other event types are already in this state as 018
> leftovers — `MessagesReceived`, `MessageAcknowledged` and `MessageDeadLettered` — all defined,
> none recorded.

## Not done

**T12 — the sample.** R6.6 asks for a runnable chunk-and-checkpoint demonstration across real
processes. The cookbook (T8) carries the pattern with working code, but a sample needs its own
job table and a progress command in the storefront, which is a sample-harness change rather than
a feature change. Recorded rather than faked: a sample that "demonstrates" chunking with a
three-line loop and no durable checkpoint would teach the wrong shape.
