# Feature 032 — The Assurance Rig: Tasks

Phase 0 settles the decisions. **Phase 1 builds the judge before anything it will judge** —
the reconciler and its ledger format, proven against fixtures containing planted loss. Phase
2 builds the three applications, Phase 3 the runner that drives them, Phase 4 proves it,
Phase 5 records it.

**Why the judge comes first.** If the applications are built first, the only way to learn
whether the reconciler works is to hope a run fails. A reconciler that has never been shown
a lost message will report `PASSED` on a run that lost half of them, and that verdict is
worse than no rig at all — it converts an unknown into false confidence. So the reconciler
is written against hand-made ledgers with deliberate loss, duplicates, phantoms, an
expected-miss and a dead letter, and every invariant has a fixture that **fails** it before
any application exists.

**Phase 0 is closed — OD1–OD11 were resolved on 2026-08-13 and implementation is unblocked.**

## Phase 0 — the decisions

### - [x] T0 — OD1–OD11 resolved

*Requirements:* all — the decisions shape everything downstream
**Done when:** each decision in `requirements.md` carries its resolution and one line of
why; any design section a choice invalidates is rewritten before implementation starts.
**Resolved 2026-08-13.** The user set OD3 (50–100 msg/s), OD4 (no broker restart), OD9
(node restarts in scope, messages durable across them) and OD10 (slow handlers via
`Task.Delay(500)`); the remaining decisions were made by this spec and recorded with their
reasoning in `design.md` rather than left open. OD9 and OD10 both changed the design:
restarts added the turbulence phase and D11–D12, and the slow handler forced the
`WorkerConcurrency` arithmetic in § Capacity.

## Phase 1 — the judge

### - [x] T1 — `Highway.Assurance.Contracts`

*Requirements:* R1.2–R1.6
**Done when:** one assembly referencing **only** `Highway.Abstractions` carries every
contract in the design — `ValidateAccount`/`AccountResult`, `GetProfile`/`ProfileResult`
(with the known-absent user id that returns 404 as data), `UserSignedUp`,
`PasswordResetRequested`, `AccountAudited`, `EmailDispatched`, and `SendEmail` with its
`Kind` discriminator. Every contract carries `Cid`. A test asserts the assembly's only
Highway reference is `Highway.Abstractions` — the property the samples' contracts project
exists to demonstrate, now enforced rather than described.

### - [x] T2 — The ledger writer

*Requirements:* R4.1–R4.5
*Depends on:* T1
**Done when:** a small writer appends one JSON object per line and flushes per line;
the schema is the design's (`ts`, `app`, `node`, `phase`, `kind`, `type`, `cid`, and the
optional `msgId`, `group`, `attempt`, `status`, `ms`); a test kills a writer mid-run and
proves the file still parses to its last complete line; the per-event cost is measured once
and recorded in the design so R4.5's claim is a number rather than an assurance.

### - [x] T3 — `Highway.Assurance.Reconciler` and the fixtures that fail

*Requirements:* R5.1–R5.8
*Depends on:* T2
**Done when:** the reconciler reads a run directory, computes I1–I7 as set operations over
`cid` (never counts — D6), and exits non-zero if any fails; it references **no Highway
client code** (D5) and reaches the broker only over RESP; and **every invariant has a unit
test with a hand-written ledger fixture that makes it fail** — one planted lost message, one
duplicate, one phantom, one RPC with neither reply nor timeout, one publish missed by a live
group, one expected-miss that must *not* fail, one dead letter. A failing invariant names
the offending ids and every ledger line mentioning them. `report.md` and `report.json` are
written.

## Phase 2 — the three applications

### - [x] T4 — `Edge`, the frontend and load origin

*Requirements:* R1.2, R1.6, R2.2
*Depends on:* T2
**Done when:** Edge runs as a generic-host process; mints monotonic `edge-{seq}` cids; calls
`ValidateAccount` and `GetProfile` (including the id that returns 404 as data), publishes
`UserSignedUp` and `PasswordResetRequested`, sends `SendEmail` with `Kind="signup"`, and
subscribes to `EmailDispatched` — closing the cycle; every action writes both sides to the
ledger (`executed`/`replied`/`timed-out`, `published`, `sent`, `received`); it obeys a rate
target and a phase schedule from the runner, and stops cleanly on `Ctrl+C`/SIGTERM.

### - [x] T5 — `Accounts`, RPC host and subscriber

*Requirements:* R1.3, R1.6
*Depends on:* T2
**Done when:** Accounts hosts `ValidateAccount` and `GetProfile` (the latter returning
`StatusCode` 404 as data for the known-absent id), subscribes to `PasswordResetRequested`,
sends `SendEmail` with `Kind="reset"` — making it the **second producer into one queue** —
and publishes `AccountAudited`; every receipt and every send is recorded with the originating
cid preserved, so a reset email is traceable to the publish that caused it.

### - [x] T6 — `Notifications`, in two roles, doing real work

*Requirements:* R1.4, R2.2a, R3.1, R3.6
*Depends on:* T2
**Done when:** one application supports two roles selected at start: `subs` subscribes to
`UserSignedUp` and `AccountAudited` and publishes `EmailDispatched`; `mailer` hosts the only
`IProcess<SendEmail>` and nothing else. Three processes, one binary (OD2/D2) — `subs` plus
**two** mailer instances, so D11's kill leaves a survivor. The mailer's `ProcessAsync`
awaits `Task.Delay(500)` (OD10) and the mailer runs at **`WorkerConcurrency = 64`**, the
value derived in `design.md` § Capacity — at the default of 8 the queue never drains at
100/s, so this is not a tuning preference. The `AccountAudited` subscriber is slowed the
same way. The mailer records `processed` with its delivery `attempt`, so redeliveries after
the kill are attributable. A test proves the `subs` role hosts no processor — the gap phase
is meaningless if it does.

## Phase 3 — the runner

### - [x] T7 — Runner: broker lifecycle and an observable settle

*Requirements:* R7.1, R7.4, R2.3
*Depends on:* T4, T5, T6
**Done when:** the runner probes an ephemeral port, generates `highway.json` with a fresh
data directory, the recorder on, and **a lease of seconds rather than the 5-minute default**
(D12 — without it the kill in T8 cannot be observed inside a 4-minute run, and the run would
report a false failure); the generated config is kept in the run artefacts because it shapes
what the run can see. It starts `highways --config …` (D9) and waits for a RESP
`PING` rather than sleeping; starts the three processes and leaves the settle phase **on an
observable condition** — all nodes visible in `HW.STATS` and every subscription group
registered — with a bounded timeout that reports `HARNESS` if it expires.

### - [x] T8 — Runner: the timeline, the load, the achieved rate

*Requirements:* R2.1–R2.5, R3.1, R3.3
*Depends on:* T7
**Done when:** phases run to wall-clock boundaries (D8) — settle, gap, arrival, steady,
turbulence, drain, shutdown — with every ledger line carrying its phase; both mailers start
at the arrival boundary and not before; the arrival window is sized from § Capacity rather
than guessed; the load mix and target rate (50–100/s, default 100) come from
`profile.json` and the
**achieved** rate per verb is reported without any figure being published as throughput
(C5); shutdown stops producers first and then consumers, each through the host's normal stop
path with `DrainTimeout` honoured; the whole run is one command with no interactive input.

### - [x] T15 — Runner: the turbulence phase — a graceful restart and a kill

*Requirements:* R3.6–R3.9
*Depends on:* T8
**Done when:** at the scheduled points the runner restarts `notifications-subs`
**gracefully** with the same node and group identity, and **kills `mailer-2` ungracefully**
(`Process.Kill`, no drain, no acknowledgement) while it is mid-`ProcessAsync` holding
claimed messages — verified as mid-work, not merely killed at a convenient moment. Both
events are stamped into every ledger as phase markers with their wall-clock time (R3.9). The
reconciler then proves: every message `mailer-2` held is redelivered after lease expiry and
completed by `mailer-1` with **zero lost** (R3.6); the restarted subscriber receives
everything published during its downtime (C2.3, R3.8); and the duplicates the kill produces
are counted and attributed to it rather than failing the run (OD6). Recovery time from kill
to last redelivered message is reported.

**This is the task the feature exists for.** The gap phase proves a queue waits; only the
kill proves the queue survives something going wrong.

### - [x] T9 — Broker observation: stats, dead letters, recorder, storage, memory

*Requirements:* R6.1–R6.4, R3.2
*Depends on:* T7
**Done when:** `HW.STATS` is sampled on an interval per queue and channel into
`stats-samples.jsonl` — this is what proves the gap phase's depth rise **independently of the
applications** (R3.2) and what tells the reconciler which groups were live at a given moment
(D7); `HW.DLQ PEEK` is captured if anything is there; the flight recorder is replayed to
`recorder-replay.jsonl`; data-directory and AOF bytes are recorded before and after (R6.3 —
the first sustained measurement under a mixed workload, and material to C4.6); peak working
set per process is recorded.

### - [x] T10 — The run directory, the report, and what happens on failure

*Requirements:* R7.2, R7.3, R7.5
*Depends on:* T3, T8, T15, T9
**Done when:** every artefact lands under `assurance/runs/{timestamp}/` in the design's
layout, including `versions.json` (assembly versions, `highways --version`, git sha) and the
effective `profile.json`; the verdict prints and `assurance/RUNLOG.md` gains an entry in the
house pattern, newest first, **and the accepted run's summary is committed to
`docs/features/032-assurance-rig/runs.md` so the feature folder carries its own evidence
(OD11)**; the three failure classes are distinguished in output and exit
code — `HARNESS`, `FAILED`, `CONFLICT` (design § Error Handling); **on success the broker
data directory is cleaned, on failure nothing is cleaned** and the path is printed (D10).

## Phase 4 — proof

### - [x] T11 — The shortened run, in the normal test suite

*Requirements:* R8.1–R8.4
*Depends on:* T10
**Done when:** one xUnit test in `Highway.Integration.Tests` runs the entire rig under 60
seconds using the **same** applications, ledger format and reconciler — only duration and
rate differ; it waits on observable conditions rather than sleeps; it fails the build when
an invariant fails.

### - [x] T12 — The first full run, and triaging what it finds

*Requirements:* R2.1, R3.1–R3.5, R5.2–R5.8, R7.3
*Depends on:* T11
**Done when:** a full 3–4 minute run at target rate completes and its verdict is recorded in
`assurance/RUNLOG.md` with the evidence named: zero `SendEmail` processed during the gap,
the queue depth curve from the broker's own samples, the complete drain after arrival with
its duration, the duplicate count, the expected-misses, and the AOF measurement.
**Anything the run finds is triaged as `RUNLOG.md` requires** — symptom, root cause, fix in
the library, regression test. If the rig has to be changed to make a run pass, that change
needs a stated reason that is not "the run failed".

## Phase 5 — the record

### - [x] T13 — `constraints.md`, `roadmap.md`, and the rig's own README

*Requirements:* R9.2–R9.4
*Depends on:* T12
**Done when:** each constraint this rig exercises (C1.1, C1.2, C1.3, C2.1, C2.3, C2.4, C3.2)
carries a note that it is now proven under sustained multi-process load, naming where the
evidence lives — a status changes **only** if a run proved it wrong, in which case the
constraint is corrected and the reason recorded; `roadmap.md` records 032 as the harness plus
the soak scenario and registers 033; `assurance/README.md` explains what the rig proves, what
it deliberately does not (C5: no throughput claim), and how to read a run directory.

### - [x] T14 — Everything green

*Requirements:* R9.1, R9.5
*Depends on:* all above
**Done when:** full suite green; `dotnet build --no-incremental` warning-free;
`docs/HIGHWAY-PROTOCOL.md` byte-identical to before the feature (the check, not the
promise); the shortened run passes from a clean checkout.

---

**Order:** 0 → 1 (T1 → T2 → T3) → 2 (T4 ∥ T5 ∥ T6) → 3 (T7 → T8 → T15 ∥ T9 → T10) → 4 (T11 → T12) → 5.

**Deferred (registered, not built):**

- **033 — the rest of the assurance rig.** **Broker** crash-recovery (killed mid-flight,
  restarted, backlog intact), disk-full, connection-churn, and a long soak. All reuse this
  harness: the applications, the ledgers and the reconciler are the same; only the scenario
  differs. Broker restart is deliberately out of 032 (OD4, user decision) so that a failure
  here indicts one thing. **Node** restarts and kills are *in* 032 — T15.
- **The password/TLS profile (OD8)** — blocked on the open 026 defect, which makes
  `AddHighway` fail against a protected broker. Once fixed, one run under a password turns
  C6.5 ("the tested path is the secured path") from a claim into evidence.
- **A generalised load framework** — this rig has one topology on purpose. Generalising
  before a second scenario needs it would be inventing requirements.
