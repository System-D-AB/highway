# Feature: Long-Running Tasks — A Lease That Can Be Kept Alive

## Introduction

`SendAsync` and `IProcess<T>` already are durable background work. What they are not is
**long-running** work, and the reason is a single number.

`HighwayServerOptions.Lease` defaults to 5 minutes. There is no way to extend it. Once a
worker claims a message the clock runs, and when it runs out the sweep in `HW.QCLAIM`
requeues the entry while the original handler is **still executing**:

```
worker A claims ──────────── still working ───────────────────────►
                │
      5 min ────┤ lease expires
                │
worker B claims ┤ sweep requeues A's entry; B starts the same work
                │
                └─ attempts++ … at MaxDeliveryAttempts (5) → dead letter
```

That is not a duplicate *after* a failure. It is a **concurrent** duplicate while the first
attempt is still running. A handler that reliably takes twenty minutes against a five-minute
lease runs five times and then dead-letters — having done the work five times and reported
failure.

The remedies available today are both bad. Raising `Lease` is **server-wide**, so a
two-hour import also makes RPC recovery take two hours — against a `CallTimeout` of thirty
seconds (C3.3). Chunking the work is the right answer for genuinely long jobs and this
feature documents it, but it cannot be the answer for a handler that is simply slow.

### What this feature does

**A claimed message's lease can be renewed.** One additive command, `HW.TOUCH`, moves a
processing entry's claim timestamp forward. The client renews automatically while a handler
is running, so a slow handler is safe without configuration — and renewal is **bounded**, so
a hung handler is still recovered.

### What this feature does not do

**It does not make the lease unlimited.** A renewed lease still expires. Unbounded renewal
would delete lease recovery: a handler stuck in a deadlock or an infinite loop would hold its
message forever, and the message would never be redelivered, never dead-lettered, and never
visible as a problem. The cap is the feature, not a limitation of it.

**It does not add a job, batch, or workflow abstraction.** For work measured in hours the
correct shape is chunk-and-checkpoint using the three verbs and the application's own
database, and Requirement 6 ships that as documentation rather than code. Highway supplies
durable delivery and durable timers; the application supplies state.

## Requirements

### Requirement 1: A Claimed Message's Lease Can Be Renewed

**User Story:** As a developer with a handler that takes longer than the lease, I want to say "still working" so the message is not redelivered underneath me.

#### Acceptance Criteria

1. A new command renews the lease on one claimed entry:
   ```
   HW.TOUCH SVC <service> <node> <requestId>   →  :1 renewed | :0 not found
   HW.TOUCH Q   <queue>   <node> <messageId>   →  :1 renewed | :0 not found
   ```
2. Renewal **rewrites the entry's claim timestamp to now**. No new field, no new framing, no new key — the sweep already decides expiry by comparing the claim timestamp, so moving it forward *is* restarting the lease
3. The target grammar is `SVC | Q`, identical to `HW.FAIL` and `HW.DLQ`. `Q` accepts a derived group-queue name (`{channel}@{group}`), so a subscriber's lease is renewable by the same command
4. Renewing an entry that is no longer in the processing list — acknowledged, or already swept — returns `:0` and does nothing. A late renewal is a race the client cannot avoid, not an error to investigate
5. It **does not acknowledge**, does not change the attempt count, and does not move bytes between counters. The entry stays exactly where it is, with exactly the state it had, and only its deadline moves
6. Renewal preserves the failure block (015). An entry that has already reported a failure and is then renewed must not lose `firstType`

### Requirement 2: The Client Renews Automatically

**User Story:** As a developer, I want a slow handler to be safe without reading the source to discover that it is not.

#### Acceptance Criteria

1. While a handler is executing, the worker loop renews that message's lease on an interval. This applies to RPC, queue and subscription loops — all three claim leases, so all three renew
2. Renewal is **on by default**. A handler that outlives the lease is a correctness failure with silent duplicate execution; a developer should not have to opt in to not having it
3. Renewal stops the moment the handler completes, throws, or is cancelled. A completed message is never renewed
4. A failed renewal is logged and **never propagated**. The handler continues, and the message is recovered by the ordinary sweep as it is today — C7.1 applies unchanged: a mechanism that protects delivery must never be able to break it
5. `LeaseRenewalInterval` is configurable, default **1 minute**, and must be positive. Against the server's 5-minute default lease that is 5× headroom, the same ratio the heartbeat keeps against `NodeExpiry`
6. The client cannot read the server's `Lease`, so the relationship between the two is documented rather than validated. Lowering `Lease` below roughly 3× `LeaseRenewalInterval` makes renewal unreliable and is called out at the point of configuration

### Requirement 3: Renewal Is Bounded, and the Bound Is the Point

**User Story:** As an operator, I want a hung handler to still be recovered, not to hold its message forever because it keeps claiming to be alive.

#### Acceptance Criteria

1. `MaxProcessingTime` caps how long one message may be renewed for. Default **15 minutes**. Once exceeded, renewal stops
2. After the cap, the lease expires normally: the sweep requeues the entry, increments the attempt count, and eventually dead-letters it. **Every existing recovery path applies unchanged** — the cap returns the message to the behaviour it has today, it does not invent a new outcome
3. Hitting the cap is **loud**. It is logged at `Warning` naming the queue, the message id and the elapsed time, and it emits a recorder event so it is visible in the dashboard and in `HW.REPLAY`. A handler that routinely exceeds its cap is either mis-sized or hung, and both are worth knowing
4. `MaxProcessingTime = TimeSpan.Zero` disables renewal entirely, restoring exactly today's behaviour for callers who want it
5. Individual renewals are **not** recorded as events. A one-minute interval across many in-flight messages would flood the recorder with the least interesting thing it could hold; only the cap-exhaustion is an event

### Requirement 4: The Default Change Is Stated, Not Discovered

**User Story:** As someone upgrading, I want the one behaviour that changes named, not left for me to find in production.

#### Acceptance Criteria

1. **A hung handler is now recovered after `MaxProcessingTime` rather than after `Lease`.** With the defaults that is 15 minutes instead of 5. This is the only behaviour change in the feature and it is a deliberate trade
2. The trade is recorded with its reasoning: a slow-but-working handler being executed five times and then dead-lettered corrupts data, while a hung handler taking fifteen minutes to recover instead of five is a delay. The first is the failure worth eliminating
3. It appears in the protocol changelog, in `constraints.md`, and in the release notes — not only in this file
4. `MaxProcessingTime = TimeSpan.Zero` is documented as the exact opt-out for anyone who disagrees

### Requirement 5: Shutdown Does Not Silently Kill Long Work

**User Story:** As an operator deploying a service, I want to know when my drain window cannot possibly let a long handler finish.

#### Acceptance Criteria

1. `DrainTimeout` (default 10 s) bounds how long graceful shutdown waits for in-flight work. When a node's `MaxProcessingTime` exceeds its `DrainTimeout`, the engine **warns once at startup**, naming both values and stating that long handlers will be cancelled mid-flight on shutdown and redelivered
2. The warning is a reminder, not a diagnosis — some deployments genuinely prefer a fast drain and accept redelivery. It says what will happen, and does not refuse to start
3. This is feature 014's memory-only-queue precedent applied to the same class of problem: the one unacceptable option is a silent surprise

### Requirement 6: The Pattern for Work Measured in Hours

**User Story:** As a developer with a two-hour reindex, I want the recommended shape written down, because renewal is not the right answer for it.

#### Acceptance Criteria

1. A cookbook documents **chunk-and-checkpoint**: claim, process one slice, checkpoint progress to the application's own database, enqueue the next slice, acknowledge. Each message lives seconds; the job lives hours
2. It states what that buys over one long handler: it survives deploys mid-job, progress is durable and visible in the application's own tables, `WorkerConcurrency` parallelises slices for free, and a poison slice dead-letters without killing the job
3. It documents the guard-first rule — every handler opens by checking the state it expects — and why that single line delivers idempotency, out-of-order tolerance and stale-timeout safety together
4. It documents `[Idempotent(WindowSeconds = ...)]` as the protection against concurrent duplicate execution, with the correct syntax and the rule that the window must exceed the worst-case handler duration
5. It notes that `MaxPayloadBytes` is 1 MiB, so long-running work over large data passes a reference to blob storage rather than the data
6. A runnable sample demonstrates it end to end across real processes

### Requirement 7: Conformance

#### Acceptance Criteria

1. `docs/HIGHWAY-PROTOCOL.md`: `HW.TOUCH` in the Command Index with arity 5 and 2 forms; a command section documenting both target forms, the reply shape, idempotency and the keys touched; a **4.3** changelog entry naming the additive command and the one behaviour change from Requirement 4
2. `ProtocolConformanceTests` green — it parses the Command Index against a running server in both directions, so a command registered but undocumented, or documented but unregistered, fails
3. `constraints.md`: C1 gains a constraint stating that a handler may run longer than the lease without duplicate execution, carrying its status; the Requirement 4 trade is recorded; the deferred per-queue lease is registered under Deferred work
4. `product.md` and `roadmap.md` updated in this feature, not after it
5. Samples re-run across real processes with a `RUNLOG.md` entry
6. All tests pass; `dotnet build` warning-free

## Open Decisions

**Answer before the design is final.** Recorded rather than guessed, because each changes the shape.

1. **Is `MaxProcessingTime = 15 minutes` the right default?**
   - Longer is friendlier to slow handlers and slower to recover a hung one.
   - Shorter recovers faster and dead-letters legitimate slow work.
   - **Recommendation: 15 minutes.** Three times the current effective ceiling, so it fixes the common case, and small enough that a hung handler is still recovered inside a coffee break. Anyone who needs hours should be chunking (R6), not renewing.

2. **Should renewal really be on by default?**
   - On: slow handlers are safe with no configuration; a hung handler takes `MaxProcessingTime` to recover instead of `Lease`.
   - Off: nothing changes for anyone until they opt in.
   - **Recommendation: on.** 018 set the precedent of choosing the default that preserves correctness (concurrency 1 for subscribers) over the one that preserves the previous number. Silent duplicate execution of working handlers is the worse failure.

3. **Does `HW.TOUCH` need the `SVC` form at all?**
   - An RPC caller times out at 30 s against a 5-minute lease (C3.3), so renewing an RPC lease usually serves nobody.
   - But a caller with a deliberately raised `CallTimeout` is legal, the parsing is shared with `HW.FAIL`, and adding the form later would be a grammar change.
   - **Recommendation: include it.** Consistency with the `SVC|Q` grammar 018 settled on costs about ten lines here and avoids a breaking addition later.

## Non-Goals

- **Per-queue lease configuration.** Considered and deferred. With automatic renewal a slow handler is already safe, so a per-queue lease would buy only reduced command traffic for very long jobs — and Highway has no throughput benchmark to justify optimising against (C5). This is 018's Open Decision 2 reasoning applied again: measure first, then add. Registered under Deferred work in `constraints.md`.
- **Unbounded renewal.** Requirement 3. It would delete lease recovery.
- **A job / batch / workflow abstraction.** Requirement 6 is documentation deliberately. Highway's advantage is three verbs, and a fourth concept for "long work" would be the withdrawn `runtime-vision.md` mistake in a new costume.
- **Progress reporting as a protocol feature.** Progress belongs in the application's own tables, where it can be queried, joined and displayed. `HW.TOUCH` says "alive", not "62% done".
- **Cancellation of in-flight work.** A separate feature with its own semantics; renewal is about keeping a message, not stopping one.
- **Changing `Lease`, `MaxDeliveryAttempts` or `DrainTimeout` defaults.** Renewal makes the existing defaults workable; moving them would change behaviour for people who are not affected by this problem.

## Cross-References

- `docs/product/constraints.md` — C1.2 (survives until processed), C3.3 (retry budget outliving the caller), C5 (no characterised throughput), C7.1 (diagnostics must never break delivery)
- `docs/features/013-reliable-delivery/design.md` — attempt counting and dead-lettering, the recovery path the cap returns to
- `docs/features/014-queue/design.md` — the queue engine and the shared lease sweep this renews
- `docs/features/015-recoverability/design.md` — Decision 4, the entry-rewrite pattern `HW.TOUCH` copies, and Decision 6, the best-effort rule R2.4 inherits
- `docs/features/018-pubsub-unification/design.md` — why a subscriber group is a queue, which is what makes one command cover both verbs
- `docs/HIGHWAY-PROTOCOL.md` — the `SVC|Q` target grammar, and § Entry Framing, which this feature deliberately does not change
