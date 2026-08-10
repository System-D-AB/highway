# Feature: Recurring Jobs

## Introduction

Highway can say *"not before T"* (delayed delivery, 013) but not *"every T"*. The missing half
of time-based messaging is why every event-driven system on Highway would, within months,
deploy Hangfire, Quartz, or a cron container beside the broker — a second infrastructure with
its own storage, dashboard and failure modes, which is precisely the thing Highway exists to
delete.

A recurring job in Highway is **a schedule that sends a message**. The fired occurrence is an
ordinary queue message: it inherits durability, at-least-once delivery, competing consumers,
`[Idempotent]`, dead-lettering, lease renewal and the dashboard's message view — all of it
already built. The feature's new machinery is deliberately small: durable schedule records,
an atomic *fire-and-re-arm* step, and the API that declares a schedule.

**Lane:** connective tissue (closes the recorded delay-without-recurrence gap; adds no new
verb — a fired job is `SendAsync` with a clock).

### What this feature is not

- **Not a workflow engine.** No steps, no compensation, no state machine. One schedule, one
  message, one processor. Sagas remain rejected.
- **Not an alarm clock.** Firing follows Highway's existing philosophy: delivery is driven by
  polling with a doorbell accelerator, so an occurrence fires on the first poll after it is
  due — within the backstop interval in a running system, and *not at all* until a hosting
  node runs. Second-accurate wall-clock execution with nothing running is a scheduler's
  promise, and Highway makes the promises it can keep.
- **Not 029.** Exactly-one-node-fires does not need a separate election primitive: an
  occurrence is a single queue entry, and the existing claim machinery already guarantees one
  claimant. (This revises the earlier assumption that 029 was a prerequisite — recorded in
  the design, §D2.)

## Open Decisions — RESOLVED (2026-08-10, at execution approval)

The user approved execution on the design's recommendations. One resolution changed during
implementation analysis (OD4) and one was narrowed (OD2's time zones); both changes are
reasoned below, not silent.

| # | Decision | Resolution |
|---|---|---|
| **OD1** | Declaration API | **Composition-root builder** (`o.Jobs.Daily<T>(...)` — design D1-B). The attribute remains registered as possible later sugar |
| **OD2** | Expression format | **`Daily(TimeOnly)` + `Every(TimeSpan)` + `Cron(string, 5-field)`**, all **UTC-only in v1** — time-zone/DST support is registered as deferred, not half-shipped. Client-side floor: `Every` ≥ 1 minute (the wire accepts ≥ 1 second, for tests and tooling) |
| **OD3** | Missed occurrences | **Catch-up-one, uniformly**: a due schedule fires exactly one occurrence and `nextFire` is computed **from now**, collapsing any missed backlog. Uniform rather than split-by-kind (the design's Daily/Every split), because firing one stale interval tick after downtime is at worst harmless and usually wanted ("sync now, then resume"), and one rule is teachable |
| **OD4** | Overlap | **Fire-anyway in v1** — a change from the design's skip-while-outstanding recommendation, forced by implementation: detecting "previous occurrence still outstanding" in O(1) inside the fire transaction requires an ack-side completion hook (`HW.QACK` does not know a message was a job occurrence), and the alternative — scanning the queue and every processing list under exclusive locks — puts O(depth) work on the claim path. Skip-while-outstanding is **registered as a follow-up** with the ack-hook design named as its prerequisite. v1 documents the mitigation: `[Idempotent]` plus derive-from-state handlers |
| **OD5** | Declaration conflict | **Last registration wins, loudly** — a `JobScheduleChanged` recorder event names old and new expressions (design D7; the catalog's existing rule) |

## Requirements

### Requirement 1: Declaring a Recurring Job

**User Story:** As a developer, I want to declare "send this message on this schedule" with
the same near-zero ceremony as the rest of Highway.

#### Acceptance Criteria

1. A recurring job SHALL be declared against a `[Queue]` message contract (an `ISend` type):
   the schedule names *when*, the existing contract and its `IProcess<T>` name *what*. The
   `[Queue]` attribute is required exactly as for any `ISend` — the address half of the
   declaration (design D8.4).
2. The fired payload SHALL be a **template provided by the developer at declaration**: a
   registered instance, defaulting to `new T()` (design D8). Occurrences carry identical
   bytes — per-occurrence data is derived by the handler, never patched by the broker — and
   manual triggering via `SendAsync(new T())` works by construction.
3. Multiple schedules MAY target the same contract (distinct job names, each with its own
   template and expression) — the EU-sync/US-sync case (design D8.2).
4. The declaration API SHALL be the one settled by **OD1**; the requirement is
   shape-agnostic beyond: declaring a job MUST NOT require touching the broker, and the
   declaration MUST be discoverable by the topology manifest (024) and the dashboard.
5. The schedule expression SHALL support at minimum: a daily time (`"02:00"`) and a fixed
   interval (`every 15 minutes`); whether full cron ships in v1 is **OD2**.
6. All schedule times SHALL be **UTC by default**, with time-zone support (and its DST
   semantics) explicitly in or out per **OD2**'s resolution — never implicit local time,
   which differs per node.
7. An invalid schedule expression SHALL fail at startup with an error naming the expression
   and the accepted forms (fail-fast, 005 R12).

### Requirement 2: Durable Schedules, Fired Exactly Once Per Occurrence

**User Story:** As an operator, I want schedules to survive restarts, and each occurrence to
produce exactly one message no matter how many replicas host the processor.

#### Acceptance Criteria

1. Schedule records SHALL be stored on the broker, durable under the same AOF guarantees as
   queues. A broker restart loses no schedule.
2. Each due occurrence SHALL enqueue **exactly one** message onto the job's queue, however
   many nodes host the processor and however many poll concurrently. The fire step
   (enqueue + advance `nextFire`) SHALL be atomic — one transaction, so a crash between the
   two is impossible.
3. The fired message then has ordinary at-least-once queue semantics: competing replicas,
   lease recovery, `[Idempotent]` if the contract opts in, dead-lettering after
   `MaxDeliveryAttempts`. *Exactly one fire, at-least-once processing* — stated in exactly
   those words in the docs, because conflating the two is how schedulers get trusted wrongly.
4. Firing SHALL be poll-driven by the nodes hosting the job's processor, riding the existing
   claim path — no timer thread in the broker, consistent with delayed delivery's design.
5. A job whose processor no node hosts SHALL NOT fire (nothing polls it) and SHALL be
   visible in that state on the dashboard rather than silently accumulating.

### Requirement 3: Time Discipline

#### Acceptance Criteria

1. Due-ness SHALL be decided by the **broker's clock** exclusively. Node clocks never
   participate — a single-broker system has a single clock, and the feature SHALL keep that
   advantage rather than importing distributed-clock problems.
2. Missed occurrences (broker down, or no hosting node polling, past one or more fire
   times) SHALL follow **OD3**'s resolution, and the behavior SHALL be documented with the
   restart scenario spelled out.
3. Overlap (next fire due while a previous occurrence is unacknowledged) SHALL follow
   **OD4**'s resolution.
4. Schedule accuracy SHALL be documented honestly: an occurrence fires on the first poll
   after its due time — typically within the backstop interval, never before the due time.

### Requirement 4: Observability

**User Story:** As an operator, I want to see every schedule, when it fires next, and what
happened to past occurrences.

#### Acceptance Criteria

1. A wire command SHALL list schedules with: job name, target queue, expression, next fire
   time, last fire time, and last outcome linkage (the fired message's id).
2. The dashboard SHALL show schedules — on the target queue's entity page and/or a schedules
   view — with next-fire countdown and the fired occurrences appearing as ordinary messages
   in the existing message view. No dashboard tests, per standing instruction.
3. The flight recorder SHALL gain a `JobFired` event (name, occurrence time, message id), so
   a fired occurrence's timeline starts at the schedule, not at the enqueue.
4. The topology manifest (024) SHALL list declared schedules under PROVIDES.

### Requirement 5: Lifecycle

#### Acceptance Criteria

1. Re-declaring a job (deploy with a changed expression) SHALL update the schedule per
   **OD5**'s conflict rule, and the change SHALL be visible (recorder event + log).
2. A schedule SHALL be removable: explicitly by API/command, and its records SHALL be
   destroyed with the same loudness as group retirement (what was removed, said out loud).
3. Whether an *undeclared* schedule (no deploy mentions it anymore) is retired automatically
   or kept until explicit removal SHALL be decided in design — with 017's retirement
   machinery as the precedent either way.
4. Schedule storage SHALL be bounded and SHALL appear in `BoundedStructureTests`' table with
   what bounds it (one record per declared job — topology-driven, not traffic-driven).

### Requirement 6: The Record

#### Acceptance Criteria

1. `docs/HIGHWAY-PROTOCOL.md` SHALL gain the new command(s), key schema entries, and the
   fire-atomicity invariant **in this feature**. The command table remains append-only (A1
   manifest guard).
2. `constraints.md` SHALL gain the scheduling guarantees (exactly-one-fire, broker-clock
   authority, the honest accuracy statement) as numbered constraints.
3. The UserGuide SHALL gain a Recurring Jobs section in its established pattern, including
   the *exactly one fire, at-least-once processing* sentence and the missed-fire policy.
4. The samples SHALL demonstrate a recurring job end to end, captured in the RUNLOG.

### Requirement 7: Nothing Breaks

#### Acceptance Criteria

1. The full suite SHALL pass; applications declaring no jobs SHALL see zero behavioral
   difference and zero new broker work.
2. `dotnet build --no-incremental` SHALL report zero warnings.
