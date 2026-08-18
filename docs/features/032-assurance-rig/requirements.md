# Feature: The Assurance Rig — Proving Nothing Is Lost Under Load

## Introduction

The roadmap's v1.0 gate names this and nothing of it is built:

> the assurance rig (crash-recovery, disk-full, connection-churn, soak)

930 tests pass today. Not one of them runs three applications against one broker for four
minutes under load and then **counts**. Every delivery guarantee in
[`constraints.md`](../../product/constraints.md) is proven by a test that sends a handful of
messages and asserts on them immediately. That is the right shape for a unit test and the
wrong shape for the question an operator actually asks: *over a sustained run, with real
processes, real sockets and real concurrency, did we lose anything?*

The intended first production use is a **durable queue for transactional email** — password
resets and sign-up confirmations. That is a workload where one lost message is a locked-out
user opening a support ticket, and where "at least once" has to be **demonstrated** rather
than asserted. It is also a workload with a specific shape worth proving: the mailer is the
part most likely to be down, and the messages must simply wait.

This feature builds the rig: three applications, a scripted load profile, an append-only
ledger written independently by every participant, and a reconciler that proves from those
records — cross-checked against the broker's own — that nothing was lost.

**Lane:** connective tissue. It strengthens existing guarantees by proving them; it adds no
verb, no command, no protocol surface, and no product capability.

### What this feature is

- **Three applications** as real OS processes over real TCP against one `highways` broker,
  exercising every verb in every direction: RPC with replies, pub/sub fan-out, durable
  queues with two producers and one consumer, and a subscription cycle back to the frontend.
- **A scripted timeline** of roughly four minutes with a stated load target, including a
  window in which a queue has **no consumer at all** and must simply accumulate.
- **A ledger per process** — append-only, flushed per line, written by producers *and*
  consumers independently, so no single component's bookkeeping can hide its own loss.
- **A reconciler** that reads every ledger plus the broker's `HW.STATS` and flight recorder
  and reports a verdict per invariant, exiting non-zero if any fails.
- **A run record**: every artefact of a run kept under one timestamped directory, with a
  summary appended to `assurance/RUNLOG.md` in the house pattern.

### What this feature is not

- **Not a benchmark.** C5 states plainly that Highway claims no characterised throughput.
  This rig reports the rate it *achieved* as context for a verdict; it publishes no
  performance figure and no comparison.
- **Not chaos engineering.** Ungraceful process kills, broker restart mid-flight, disk-full
  and connection-churn are the *rest* of the roadmap's assurance rig. This feature builds
  the harness and the **soak** scenario; those reuse it, registered as 033.
- **Not a replacement for the test suite.** The existing 930 tests keep proving mechanisms
  in isolation. This proves them together, under duration and concurrency.
- **Not new product capability.** If the rig needs a library change to work, that change is
  a defect this feature found — triaged as `RUNLOG.md` requires, fixed in the library, and
  given a regression test. Bending the rig around a broken path is not an acceptable fix.

## Decisions — RESOLVED (2026-08-13)

All settled. OD3, OD4, OD9 and OD10 were decided by the user on 2026-08-13; the rest were
decisions this spec made, and are recorded here with their reasoning in `design.md`
§ Decisions rather than left as questions.

| # | Decision | Resolution |
|---|---|---|
| **OD1** | Where the rig lives | **`assurance/`**, beside `samples/` — these are real processes, not fixtures. Plus one shortened xUnit test in `Highway.Integration.Tests` so the rig cannot rot unnoticed |
| **OD2** | How the late consumer is expressed | **A second process started late** — the honest model of "the mailer was down", and it demonstrates the competing-processor property the docs advertise |
| **OD3** | Load target | **50–100 messages/second aggregate** *(user, 2026-08-13)*. The profile runs at 100/s by default and the run reports achieved rate per verb; no figure is published as throughput (C5) |
| **OD4** | Broker restart | **Out of scope** *(user, 2026-08-13)* — deferred to 033. **Node restarts are in scope** and are the subject of R3.6–R3.9 |
| **OD5** | Ledger transport | **JSONL files on disk.** A ledger carried by the system under test cannot be trusted to record that system losing messages |
| **OD6** | Duplicate policy | **Counted and reported, never failed.** At-least-once means redelivery is correct behaviour (C1.1); an unexplained *rise* between runs is the signal |
| **OD7** | Broker under test | **`highways`** (031) — the artefact that will ship, and the rig becomes its first sustained workout |
| **OD8** | Security profile | **Open on loopback, no TLS, no password** — the stated production posture. A password profile joins once the open 026 defect is fixed, turning C6.5 into evidence |
| **OD9** | Node restarts | **In scope** *(user, 2026-08-13)*: messages must stay durable across them. One **graceful** restart of a subscriber (proves C2.3 catch-up) and one **ungraceful kill** of a processor mid-work (proves lease-based redelivery). The kill is the one that actually tests durability — a graceful stop drains, and a drain proves nothing about crash safety |
| **OD10** | Slow handlers | **In scope** *(user, 2026-08-13)*: selected handlers `await Task.Delay(500)` so the run reflects real work rather than a no-op loop. Which handlers, and the concurrency this forces, are derived in `design.md` § Capacity — this is not a free parameter |
| **OD11** | Where run artefacts live | Full artefacts under `assurance/runs/{timestamp}/`; the **committed** summary of every accepted run goes in `docs/features/032-assurance-rig/runs.md`, so the feature folder carries its own evidence |

## Requirements

### Requirement 1: Three Applications, One Broker, Every Verb

**User Story:** As a developer about to put Highway in production, I want a realistic
multi-application topology exercised end to end, so that trust comes from a run rather than
from a diagram.

#### Acceptance Criteria

1. Three applications run as **separate OS processes** against one broker over TCP. No
   in-process shortcut, no shared object graph, no test double. Contracts live in one shared
   contracts assembly that references only `Highway.Abstractions`
2. **`Edge`** — the frontend. Simulates user requests and originates load: calls RPC
   (`ValidateAccount`, `GetProfile`), publishes `UserSignedUp` and `PasswordResetRequested`,
   sends `SendEmail` for sign-up confirmations, and subscribes to `EmailDispatched`
3. **`Accounts`** — offers RPC methods **and** subscribes to another app's topic: hosts
   `ValidateAccount` and `GetProfile`, subscribes to `PasswordResetRequested`, sends
   `SendEmail` for reset mail, and publishes `AccountAudited`
4. **`Notifications`** — publishes **and** subscribes: subscribes to `UserSignedUp` and
   `AccountAudited`, publishes `EmailDispatched`, and hosts the single
   `IProcess<SendEmail>` — the late consumer of Requirement 3
5. The topology therefore closes a **cycle** (Edge → Notifications → Edge), has **two
   producers into one queue** (Edge and Accounts), and covers `ExecuteAsync`, `SendAsync`,
   `PublishAsync`, `IProcess<T>` and `ISubscribe<T>` in one run
6. At least one RPC path returns a **failure as data** (`Output.StatusCode` 404), so the
   run proves the error path is not silence (C3.2) rather than only the happy path

### Requirement 2: A Scripted Timeline Under Load

**User Story:** As the person reading the result, I want the run to follow a written script
with named phases, so that a verdict can be attributed to a moment rather than to "the run".

#### Acceptance Criteria

1. The run lasts **3–4 minutes** and is divided into named phases with fixed boundaries.
   Every ledger line carries its phase, so any anomaly is locatable in time
2. The **target aggregate rate is 50–100 messages/second** (OD3), configured in the run
   profile and defaulting to 100/s. The run reports the rate it **achieved**, per verb. A
   run that falls materially short of target is reported as such — it is context for the
   verdict, never a failure on its own, and never published as a throughput figure (C5)
2a. **Selected handlers do real-shaped work**: they `await Task.Delay(500)` rather than
   returning immediately (OD10), so the run measures a system doing something rather than a
   no-op loop. Which handlers are slow, and the `WorkerConcurrency` that choice forces, are
   **derived** in `design.md` § Capacity — at the default concurrency of 8 a 500 ms mailer
   cannot keep up with the target rate at all, so this number is load-bearing and is stated
   with its arithmetic rather than guessed
3. Phases: **settle** (processes start, catalogue and registry converge), **gap** (steady
   load, the email queue has no consumer), **arrival** (the consumer starts, the backlog
   drains), **steady** (full load, everything live), **drain** (load stops, consumers finish),
   **shutdown** (graceful stop in a defined order), **reconcile**
4. Shutdown is **graceful and ordered** — producers first, then consumers, each through the
   host's normal `Ctrl+C`/`StopAsync` path with its `DrainTimeout` honoured. An in-flight
   message at shutdown is a message the run still expects to see processed
5. The whole run is driven by **one command** and needs no interactive input

### Requirement 3: Consumers That Are Absent, and Consumers That Die

**User Story:** As someone whose mailer will certainly be down at some point — sometimes
cleanly, sometimes not — I want proof that queued email waits, survives, and is fully
delivered, so that "durable" is a demonstrated property rather than a documented one.

#### Acceptance Criteria

1. Throughout the **gap** phase, `SendEmail` messages are produced continuously by two
   applications and **zero** are processed — there is no `IProcess<SendEmail>` running
   anywhere. The run asserts zero processing during the gap, not merely a low count
2. The queue's growth during the gap is observed **independently of the applications**, via
   `HW.STATS` against the broker, and recorded per sample
3. When the consumer starts, **every message produced during the gap is processed** —
   proven by set reconciliation on message ids, not by a count that could coincide
4. The **time to drain** the accumulated backlog is recorded. No threshold is asserted; the
   number exists so a regression is visible between runs
5. The run also proves the *complementary* behaviour so nobody mistakes it for loss: a
   subscription group that has never registered receives nothing published before it
   existed (C2.4), and this is recorded as an **expected miss**, not a failure. The
   difference between "a queue holds work for an absent consumer" and "pub/sub does not" is
   the single most misunderstood thing in the product, and this run states it in evidence
6. **A processor is killed ungracefully mid-work** (OD9) — `Kill()`, no drain, no
   acknowledgement — while it holds claimed messages with a `Task.Delay(500)` in flight.
   Every message it held is redelivered after its lease expires and is processed by another
   instance or by its own restart. Not one is lost. This is the criterion that makes the
   feature worth building: a graceful stop drains, and a drain proves nothing about crash
   safety
7. **The lease is shortened for the run** so redelivery is observable inside it. The
   server's default lease is 5 minutes — longer than the whole run — so a kill under
   default settings would prove nothing. The run's `highway.json` sets a lease of seconds,
   the value is recorded in the run's configuration artefacts, and the recovery time is
   reported
8. **A subscriber is restarted gracefully** with the **same node and group identity**, and
   receives what was published while it was down (C2.3). A publish during its downtime that
   never arrives after it returns is a failure
9. Restarts are **scheduled by the runner at fixed points in the timeline**, recorded as
   phase events in every ledger, and the reconciler attributes any duplicate or delay to the
   restart that caused it. A restart whose effects cannot be located in time is not evidence

### Requirement 4: Every Participant Keeps Its Own Ledger

**User Story:** As the person who has to believe the result, I want the records written
independently by producers, consumers and the broker, so that no single component's
bookkeeping can conceal its own failure.

#### Acceptance Criteria

1. Every process writes an **append-only JSONL ledger**, one line per event, **flushed per
   line**, so a process that dies still leaves everything it had recorded
2. Every message carries a **producer-minted correlation id**, unique across the run and
   traceable to its origin (`edge-000123`), present in the ledger of every process that
   touches it
3. Recorded event kinds cover both sides of every verb: `sent`, `processed`, `published`,
   `received`, `executed`, `replied`, `timed-out`, `failed`, `expected-miss`, plus phase
   markers. Each line carries timestamp, application, node name, phase, message type and id
4. Ledgers are written to a **run directory**, never to the message system under test
   (OD5). A ledger that travelled through Highway could not be trusted to record Highway
   losing it
5. Ledger writing must not perturb what it measures: it is local, buffered per line, and its
   cost is measured once and stated. If ledger overhead materially shapes the achieved rate,
   that is reported

### Requirement 5: The Reconciler Proves the Invariants

**User Story:** As the developer deciding whether to ship, I want a single verdict derived
from the records by an independent tool, so that "no messages lost" is a computation and not
an impression.

#### Acceptance Criteria

1. A reconciler reads every ledger and reports **per invariant**: verdict, counts, and the
   first few offending ids where it fails. It exits non-zero if any invariant fails
2. **I1 — Queue completeness.** Every `SendEmail` id sent by either producer has at least
   one `processed` (C1.1, C1.2). A single missing id fails the run
3. **I2 — No phantoms.** Every `processed` id was `sent` by someone. An id that appears
   from nowhere is corruption and fails the run
4. **I3 — RPC never silent.** Every `executed` has a matching `replied` or a recorded
   `timed-out` (C3.2). A call with neither is a failure
5. **I4 — Pub/sub reaches every live group.** Every `published` id has at least one
   `received` in **each subscription group registered at publish time**. Groups not yet
   registered are reported as expected misses (R3.5), never as failures
6. **I5 — Duplicates counted, not failed.** Redeliveries are correct under at-least-once;
   the reconciler reports the duplicate count and rate per message type, and the run records
   it so a rise is visible between runs (OD6)
7. **I6 — Dead letters.** Zero, or each one listed with its reason from `HW.DLQ PEEK`. A
   dead letter is not automatically a failure, but it is never silent
8. **I7 — Nothing left behind.** After the drain phase, every queue's depth is zero and no
   message is in flight

### Requirement 6: The Broker's Own Record Corroborates

**User Story:** As a sceptic, I want the applications' story checked against the broker's,
so that a bug in the harness cannot produce a passing run.

#### Acceptance Criteria

1. `HW.STATS` is sampled throughout the run and at the end, per queue and per channel, and
   stored in the run directory. Final depths and dead-letter counts are compared against the
   reconciler's conclusions — a disagreement fails the run and is reported as a
   harness-versus-broker conflict, naming both numbers
2. The **flight recorder** is enabled for the run and its contents replayed into the run
   directory, giving a third independent record of what the broker saw
3. The broker's **data directory size and AOF bytes** are recorded before and after. This
   run is the first sustained measurement Highway has under a realistic mixed workload, and
   C4.6 is currently unmet — the number belongs in the record whether or not this feature
   acts on it
4. Process **peak working set** for each application and the broker is recorded, so a leak
   under duration is visible without a separate tool

### Requirement 7: Reproducible and Recorded

**User Story:** As someone who will run this repeatedly, I want each run's evidence kept
whole, so that two runs can be compared rather than remembered.

#### Acceptance Criteria

1. **One documented command** runs the whole rig from a clean checkout: builds, starts the
   broker on an isolated port with a fresh data directory, starts the applications, drives
   the timeline, shuts down, reconciles, and prints the verdict
2. Every artefact of a run lands in one timestamped directory: ledgers, broker stats
   samples, flight-recorder replay, the reconciler's report, the effective configuration,
   and the versions of everything involved
3. `assurance/RUNLOG.md` gains an entry per run in the house pattern (date, what ran, what
   it found, what was done), newest first, following `samples/RUNLOG.md`'s discipline — an
   invariant failure is triaged as a product defect, fixed in the library, and given a
   regression test
4. Runs are isolated: an ephemeral port and a fresh data directory per run, so a previous
   run cannot influence the next and two runs can proceed concurrently on one machine
5. The rig cleans up after itself on success and **leaves everything in place on failure** —
   the state at the moment of a lost message is the most valuable artefact it can produce

### Requirement 8: It Runs in CI, Shortened

**User Story:** As the maintainer, I want an abbreviated version in the normal test suite,
so that the rig cannot quietly stop working between the times anyone runs it fully.

#### Acceptance Criteria

1. One xUnit test in `Highway.Integration.Tests` runs the entire rig at reduced duration and
   rate (target: under 60 seconds), asserting the same invariants
2. The short run uses the **same** applications, ledger format and reconciler as the full
   run — only duration and rate differ. A separate CI-only code path would be a second thing
   to get wrong
3. The short run is deterministic enough to be a normal test: no flake-by-timing, no
   sleep-and-hope. Where it must wait, it waits on an observable condition with a generous
   bound
4. A failing short run fails the build

### Requirement 9: The Record

**User Story:** As a Highway maintainer, I want the rig held to the project's own standards,
so that it does not become an unowned artefact.

#### Acceptance Criteria

1. `docs/HIGHWAY-PROTOCOL.md` is **not modified** — this feature adds no protocol surface
2. `constraints.md` gains, for each constraint this rig now exercises (C1.1, C1.2, C1.3,
   C2.1, C2.3, C2.4, C3.2), a note that the guarantee is proven under sustained multi-process
   load and where the evidence lives. A constraint's *status* changes only if the run proves
   it wrong — in which case the constraint is corrected and the reason recorded
3. `roadmap.md` records 032 as the harness plus the soak scenario, and registers 033
   (crash-recovery, disk-full, connection-churn) as reusing it
4. `assurance/README.md` explains what the rig proves, what it deliberately does not, and
   how to read a run directory
5. All tests pass; `dotnet build --no-incremental` warning-free

## Non-Goals

- **Throughput or latency figures.** C5 declines to characterise throughput; a rig that
  produced a headline number would create the claim by accident.
- **Chaos: kills, broker restart, disk-full, connection churn.** The rest of the roadmap's
  assurance rig, registered as **033**, built on this harness.
- **Multi-broker or failover scenarios.** C5 — single broker by constitution.
- **Testing the dashboard.** It may be watched during a run; it is not under assertion here.
- **A general-purpose load tool.** This rig exercises Highway's guarantees with a fixed
  topology. It is not a framework for arbitrary load scenarios, and generalising it before
  a second scenario exists would be inventing requirements.

## Cross-References

- [`docs/product/constraints.md`](../../product/constraints.md) — C1 (queues), C2 (pub/sub,
  including C2.3 and C2.4 which R3.5 turns into evidence), C3 (RPC), C4.6 (unmet; R6.3
  measures it), C5 (no characterised throughput — the reason R2.2 is worded as it is)
- [`docs/product/roadmap.md`](../../product/roadmap.md) § The Posture — v1.0's assurance rig,
  of which this is the harness and the soak scenario
- `docs/features/014-queue/`, `018-pubsub-unification/`, `025-subscription-groups/` — the
  mechanisms under test; 025 defines the group identity R5.5 reconciles against
- `docs/features/024-hosting-boundaries/` — `HostingMode`, the alternative to OD2's
  recommendation
- `docs/features/031-server-distribution/` — the `highways` host this rig runs against (OD7)
- `samples/RUNLOG.md` — the triage discipline R7.3 adopts wholesale
- `docs/features/026-distributed-cache/tasks.md` — the open credentials defect that OD8's
  password profile is blocked on
