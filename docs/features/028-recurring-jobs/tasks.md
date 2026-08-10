# Tasks: Recurring Jobs

> **Phase 0 gates everything.** OD1–OD5 change the API surface, the storage record, and the
> fire semantics; implementing ahead of them is how APIs get retreated from. The design
> carries a recommendation for each — the discussion confirms or overrides.

## Phase 0 — settle the open decisions

### - [ ] T0 — OD1–OD5 resolved with the user

**Done when:** each decision is recorded in `requirements.md` (chosen option + one line of
why), and any design section invalidated by a choice is rewritten before Phase 1 starts.
Design recommendations on the table: **B** (composition-root builder; attribute deferred as
sugar), **cron in** alongside `Daily`/`Every`, **catch-up-one** for calendar schedules /
skip for intervals, **skip-while-outstanding** overlap default, **last-registration-wins**
with a loud event.

## Phase 1 — the schedule store and the fire

### - [ ] T1 — Schedule record, keys, and `HW.JOB SET|DEL|LIST`

*Requirements:* R2.1, R5.1, R5.2, R5.4, R6.1
**Done when:** the versioned record and both keys exist (`hw:job:{queue}:schedules` +
mirror), `HW.JOB` is appended to the command table (A1 guard passes on an existing data
dir), registration is idempotent and last-wins per OD5 with its recorder event, removal is
loud, both keys have `BoundedStructureTests` rows, and the protocol document carries the
command and Key Schema entries **in this task**.

### - [ ] T2 — Fire-and-re-arm inside the promotion sweep

*Requirements:* R2.2, R2.4, R3.1, R3.4
**Done when:** `HW.QCLAIM`'s sweep promotes due schedules — enqueue occurrence + advance
`nextFire` + `JobFired` event in one transaction with all keys declared in `Prepare`; the
adversarial test (N racing pollers at the due instant → exactly one message) passes and was
verified to fail against deliberately broken atomicity; a kill between fire and claim leaves
a consistent store (occurrence enqueued, schedule advanced — proven by restart test).

### - [ ] T3 — Policies: missed fires and overlap

*Requirements:* R3.2, R3.3
**Done when:** OD3's and OD4's resolved policies are implemented with behavioral tests each
(restart-past-N-occurrences; due-while-outstanding), skip paths record their events
(`JobSkippedOverlap` etc.), and a refused enqueue (queue full) leaves `nextFire` unchanged
with `JobFireRefused` recorded.

## Phase 2 — the client API

### - [ ] T4 — The declaration API (per OD1) and expression validation

*Requirements:* R1.1–R1.5
**Done when:** the chosen API declares `Daily`/`Every` (+ `Cron` per OD2) against `ISend`
contracts; invalid expressions fail startup naming the accepted forms; declarations ride
registration to the broker; the engine issues `HW.JOB SET` on start; a node declaring no
jobs sends nothing new.

### - [ ] T5 — Manifest and catalog visibility

*Requirements:* R4.4, R2.5
**Done when:** declared schedules appear in the topology manifest under PROVIDES and in the
node catalog additively (pre-028 catalogs read as no-jobs); a schedule whose queue no node
processes shows a "no hosting node" state server-side.

## Phase 3 — observability

### - [ ] T6 — Dashboard: schedules visible; occurrences are ordinary messages

*Requirements:* R4.1–R4.3
**Done when:** `HW.JOB LIST` feeds a schedules view (next fire, last fire, expression) and
the target queue's entity page links its schedule; fired occurrences appear in the existing
message view with the `JobFired` event opening their timeline; server-side projections
tested; **no dashboard tests**, per standing instruction.

## Phase 4 — the record

### - [ ] T7 — Constraints, protocol invariants

*Requirements:* R6.1, R6.2
**Done when:** `constraints.md` gains the scheduling constraints (exactly-one-fire,
broker-clock authority, honest accuracy, the OD3/OD4 policies as stated guarantees); the
protocol Invariants section carries fire-atomicity with its enforcing test named.

### - [ ] T8 — UserGuide and samples

*Requirements:* R6.3, R6.4, R7
**Done when:** the UserGuide section exists in the house pattern with the *exactly one fire,
at-least-once processing* sentence; a sample job (e.g. a minutely `Every` in the order
service) demonstrably fires across the running samples and is captured in the RUNLOG; full
suite green; zero-warning `--no-incremental` build.

---

## Parallelization

```
PHASE 0  T0            gates all
LANE 1   T1 → T2 → T3  broker (protocol work, sequential)
LANE 2   T4 → T5       client (needs T1's command shape only)
LANE 3   T6            dashboard (needs T1's LIST)
LANE 4   T7, T8        the record (last)

Order: 0 → (1 ∥ 2 head) → 3 → 4
```

## Registered, not built

- **Attribute sugar (`[Job("02:00")]`)** — layers onto OD1-B later if demanded (design D1).
- **Per-job missed-fire/overlap overrides** — v1 ships the defaults; overrides wait for a
  concrete need.
- **029's scope correction** — D2 removes the election dependency; the roadmap note is
  updated when this feature ships.
