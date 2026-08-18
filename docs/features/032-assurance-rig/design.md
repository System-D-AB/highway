# Feature 032 — The Assurance Rig: Design

## Architecture Overview

Five processes, one broker, one run directory. Nothing under test knows it is being
measured.

```
                       ┌──────────────────────────────────────┐
                       │  highways (031)  :ephemeral port     │
                       │  fresh data dir, flight recorder on  │
                       └──────────────────────────────────────┘
                            ▲          ▲            ▲
        ┌───────────────────┘          │            └────────────────┐
        │                              │                             │
┌───────────────┐           ┌──────────────────┐          ┌────────────────────┐
│     Edge      │           │     Accounts     │          │   Notifications    │
│  (frontend)   │           │  (RPC + subs)    │          │  (subs + publish)  │
└───────────────┘           └──────────────────┘          └────────────────────┘
        │                             │                             │
        └───── ledger.jsonl ──────────┴──── ledger.jsonl ───────────┘
                                      │
                          ┌───────────────────────┐
                          │  Runner + Reconciler  │  ← drives the timeline,
                          │  (assurance/Runner)   │    samples HW.STATS, judges
                          └───────────────────────┘
```

`Notifications` runs as **three processes**: `notifications-subs` from the start, and
`mailer-1` + `mailer-2` — the only hosts of `IProcess<SendEmail>` — both started at the
arrival phase (D2). Same application, two roles, so "three applications" stays literally true
while the late consumer is a genuinely separate process. Two mailer instances exist so that
D11's ungraceful kill leaves a survivor to prove the killed instance's work was redelivered
rather than merely delayed until its own restart.

### Message flow

```
Edge  ──publish UserSignedUp──────────────► Notifications (subs)
Edge  ──publish PasswordResetRequested────► Accounts
Edge  ──execute ValidateAccount ──────────► Accounts ──reply──► Edge
Edge  ──execute GetProfile (miss) ────────► Accounts ──404 as data──► Edge
Edge  ──send SendEmail (signup) ──────────► [email.send] ─┐
Accounts ──send SendEmail (reset) ────────► [email.send] ─┼──► Notifications (mailer, LATE)
Accounts ──publish AccountAudited ────────► Notifications (subs)
Notifications ──publish EmailDispatched ──► Edge
```

Every verb, both directions, a closed cycle, two producers into one queue, one late
consumer. Requirement 1 in one picture.

---

## Decisions

**D1 — The rig lives in `assurance/`, beside `samples/`, not in `tests/`.**
These are applications, not fixtures: they have `Program.cs`, a generic host, a shutdown
path and a deployment identity. Putting them under `tests/` would invite a helpful
refactor into an in-process fixture, which destroys the only thing they exist to prove.
`samples/` set this precedent — its RUNLOG opens by saying running the samples *is* a test
because it exercises what no unit test reaches. This is the same argument with counting
attached. R8's shortened xUnit test then guards the rig itself from rotting.

**D2 — The late consumer is a second process, started late — not a restart, not a flag.**
*Options considered:* restart one app with `--mailer on`; use `HostingMode.Declared` and omit
the handler's assembly; start a separate process. A restart conflates two behaviours in one
event — the queue draining *and* a subscriber catching up on what it missed while down
(C2.3) — and when a run fails you want one variable, not two. `HostingMode` would prove a
hosting-boundary property rather than a durability one. A second process that simply starts
later is the honest model of "the mailer was down", needs no restart machinery, and
demonstrates the property the docs advertise: multiple `IProcess` instances compete for one
queue. C2.3's catch-up is proven separately by D11's scheduled subscriber restart, so the
two behaviours stay attributable to two different events.

**D3 — Correlation ids are minted by producers and are the only identity that matters.**
`SendAsync` returns a broker-assigned message id, which is what `HW.DLQ PEEK` reports, so it
is recorded. But reconciliation runs on the **producer's** id carried inside the payload,
because that id exists *before* the send and therefore survives the one failure mode that
matters most: a send that never reached the broker at all. An identity assigned by the
system under test cannot detect that system dropping the message.

**D4 — Ledgers are local JSONL files, flushed per line.**
A ledger that travelled through Highway could not be trusted to record Highway losing
messages, and a ledger in a database adds a dependency and a second failure mode to a run
whose whole purpose is attributing failure. One file per process, one JSON object per line,
`FlushAsync` per write. The cost is a syscall per event, which is measured once (R4.5) and
stated rather than assumed away. If it turns out to shape the achieved rate, the honest fix
is to report both numbers, not to buffer and risk losing the tail.

**D5 — The reconciler is a separate program with no reference to the client engine.**
It reads files and speaks RESP to the broker for `HW.STATS`/`HW.DLQ`. It does not use
`Highway.Client`, because a reconciler built on the machinery under test shares its bugs.
This is the same reasoning as D4, applied to the judge instead of the record.

**D6 — Set reconciliation, not counting.**
`sent == processed` can be true while a specific message is lost and another duplicated.
Every invariant in R5 is a set operation over correlation ids, and every failure names
offending ids. A count that matches is not evidence; a set that matches is.

**D7 — Expected misses are first-class data, not an exception in the code.**
Pub/sub does not hold messages for a group that has never registered (C2.4), and R3.5 makes
that visible rather than filtering it out. The producer records the groups it believes are
live at publish time — from `HW.STATS` sampling, not from its own assumptions — and the
reconciler treats a publish before a group's first registration as `expected-miss`. Silently
excluding those would mean the rig cannot distinguish "correctly not delivered" from
"lost", which is precisely the distinction it exists to make.

**D8 — The run is driven by wall-clock phases from the runner, not by message counts.**
Phase boundaries are times, published to each application over a simple local control file
or command-line schedule, so that a slow phase produces a *lower rate*, never a longer run.
A count-driven timeline would let a stall extend the run indefinitely and hide itself.

**D9 — The broker is `highways` from feature 031.**
It is built, it runs, and it is the artefact that will ship. The rig becomes its first
sustained workout, which is worth more than the convenience of an embedded server. It is
started with `--config` against a generated `highway.json` on an ephemeral port with a fresh
data directory, open on loopback (OD8), flight recorder on.

**D11 — Two node restarts, deliberately different in kind.**
*(User decision, 2026-08-13: the broker stays up; nodes restart and messages must survive.)*
A **graceful** restart of `notifications-subs`, same node name and therefore same
subscription group, proves C2.3 — a down subscriber receives what it missed. An
**ungraceful kill** of a mailer instance mid-`ProcessAsync`, with claimed messages and a
500 ms delay in flight, proves the thing that actually matters: lease expiry returns the
work and another instance completes it. A graceful stop drains, and a drain proves nothing
about crash safety, so the kill is not optional — it is the criterion. Both are scheduled by
the runner at fixed timeline points and stamped into every ledger, so any duplicate or gap
is attributable to the event that caused it rather than to "somewhere in the run".

**D12 — The lease must be shortened, or the kill proves nothing.**
The server's default `lease` is **5 minutes** — longer than the entire run. A killed
processor's messages would still be leased when the run ended, the queue would not drain,
and the rig would report a false failure. The run's generated `highway.json` therefore sets
a lease of seconds (starting point: 15 s), and the value is recorded in the run's config
artefacts because it materially shapes what the run can observe. This is a *test-harness*
setting, not a recommended production default, and the README says so.

**D13 — Slow handlers are real, and they set the concurrency.**
*(User decision, 2026-08-13.)* The mailer's `ProcessAsync` awaits `Task.Delay(500)` — email
is genuinely slow, so it is the honest place to put the delay. That single choice fixes the
mailer's throughput at `WorkerConcurrency / 0.5s`, which at the default concurrency of 8 is
16 msg/s — **less than the message rate the run produces**. See § Capacity: the concurrency
is derived from the target rate, not chosen. One slow subscriber (`AccountAudited`) is also
slowed, to prove the pub/sub path is doing work too; subscribers each get their own copy, so
a slow subscriber creates lag rather than a shared backlog and does not need the same
treatment.

**D10 — On failure, nothing is cleaned up.**
A run that loses a message has produced the most valuable artefact the project can own: the
ledgers, the AOF, the checkpoint directory and the recorder contents at the moment it
happened. Success cleans the data directory; failure leaves everything and prints the path.

---

## Contracts

One shared assembly, `Highway.Assurance.Contracts`, referencing only
`Highway.Abstractions` — the same discipline `Highway.Samples.Contracts` documents.

```csharp
// ── RPC ───────────────────────────────────────────────────────────────
[Service("accounts.validate")]
public sealed class ValidateAccount : IReturn<AccountResult>
{
    public string Cid { get; set; } = "";       // producer-minted correlation id (D3)
    public int UserId { get; set; }
}

public sealed class AccountResult : Output    // Output carries StatusCode + Error
{
    public string Cid { get; set; } = "";
    public bool Valid { get; set; }
}

[Service("accounts.profile")]
public sealed class GetProfile : IReturn<ProfileResult> { public string Cid { get; set; } = ""; public int UserId { get; set; } }

// A known-absent user id makes this return 404 as data — R1.6, proving the
// error path is answered rather than silent (C3.2).

// ── Pub/Sub ───────────────────────────────────────────────────────────
[Channel("users.signedup")]        public sealed class UserSignedUp : IPublish { … Cid, UserId }
[Channel("users.passwordreset")]   public sealed class PasswordResetRequested : IPublish { … Cid, UserId }
[Channel("accounts.audited")]      public sealed class AccountAudited : IPublish { … Cid, UserId }
[Channel("email.dispatched")]      public sealed class EmailDispatched : IPublish { … Cid, EmailCid }

// ── Queue ─────────────────────────────────────────────────────────────
[Queue("email.send")]
public sealed class SendEmail : ISend
{
    public string Cid { get; set; } = "";
    public string Kind { get; set; } = "";      // "signup" | "reset" — proves both producers
    public int UserId { get; set; }
    public string Body { get; set; } = "";      // sized to give the run a realistic payload
}
```

`Cid` format is `{app}-{seq:000000}`, monotonic per process, so an id names its origin and
its ordinal without a lookup.

---

## Capacity — why `WorkerConcurrency` is derived, not chosen

A 500 ms handler (D13) caps one mailer instance at `WorkerConcurrency / 0.5s` messages per
second. With the mix at 30 % queue sends, the target rate produces 15–30 `SendEmail`/s, and
the 90-second gap phase banks a backlog before the mailer even starts. The two numbers have
to be solved together:

| Aggregate target | `SendEmail` rate | Gap backlog | `WorkerConcurrency` | Handled | Net drain | Time to clear |
|---|---|---|---|---|---|---|
| 50/s | 15/s | 1,350 | **8** (default) | 16/s | 1/s | **1,350 s** ✗ |
| 50/s | 15/s | 1,350 | 32 | 64/s | 49/s | 28 s ✓ |
| 100/s | 30/s | 2,700 | **8** (default) | 16/s | **−14/s** | **never drains** ✗ |
| 100/s | 30/s | 2,700 | 32 | 64/s | 34/s | 79 s ⚠ |
| 100/s | 30/s | 2,700 | **64** | 128/s | 98/s | **28 s** ✓ |

**At the default concurrency the run cannot succeed at either end of the target range** — at
100/s the queue grows without bound, and at 50/s the backlog needs 22 minutes to clear inside
a 4-minute run. Both would be reported as a Highway failure when they are an arithmetic one.

The mailer therefore runs at **`WorkerConcurrency = 64`**, which clears the worst-case
backlog in ~28 s and fits inside the arrival phase with margin. The cost is nothing real:
64 concurrent handlers awaiting `Task.Delay` occupy no threads.

Two consequences the runner must respect: the **arrival phase is sized from this table**
(28 s of drain, not 15 s), and after the ungraceful kill (D11) the surviving instance briefly
carries the whole load alone — which is exactly the condition worth observing, and the reason
the second mailer instance exists at all.

The full run writes this table's *actual* values into the report, so the model is checked
against the machine rather than trusted.

## The Timeline

| Phase | Window | What happens | What is asserted |
|---|---|---|---|
| **settle** | 0–15 s | Broker up; Edge, Accounts, Notifications-subs start; catalogue and registry converge; groups register | All nodes visible in `HW.STATS`; every subscription group registered before load begins |
| **gap** | 15–90 s | Full load. **No `IProcess<SendEmail>` exists anywhere.** | Zero `processed` for `SendEmail`. `email.send` depth rises monotonically in `HW.STATS` |
| **arrival** | 90–125 s | `mailer-1` **and** `mailer-2` start | Backlog (~2,250 at 100/s) drains completely; duration recorded and compared against § Capacity (R3.3, R3.4) |
| **steady** | 125–165 s | Full load, everything live | Depth stays bounded; no dead letters |
| **turbulence** | 165–215 s | **t+170: `notifications-subs` restarted gracefully**, same node and group identity. **t+185: `mailer-2` killed ungracefully** mid-`ProcessAsync`, holding claimed messages | Subscriber receives everything published while down (C2.3, R3.8). Every message `mailer-2` held is redelivered after lease expiry and completed by `mailer-1` — zero lost (R3.6). Redelivery visible within the run because the lease is seconds, not the 5-minute default (D12) |
| **drain** | 215–230 s | Producers stop; consumers keep running | All queues reach depth 0 |
| **shutdown** | 230–240 s | Producers stopped first, then consumers, each via `StopAsync` with `DrainTimeout` honoured | Clean exit codes; nothing in flight |
| **reconcile** | after | Runner invokes the reconciler | R5's invariants |

Both restarts are stamped into every ledger as phase events, so the reconciler can attribute
each duplicate and each latency spike to the event that caused it (R3.9). Duplicates *are*
expected around the kill — that is at-least-once working correctly (C1.1) — and the run
records how many rather than failing on them (OD6).

Roughly four minutes of process lifetime, ~3.5 minutes of load. The shortened CI profile
(R8) scales every window by a factor and lowers the rate; the phase *structure* is identical
because a second code path would be a second thing to get wrong.

### Load target

Configured as an aggregate target per second across verbs, split by a fixed mix (e.g. 40 %
publish, 30 % queue send, 30 % RPC). The runner reports **achieved** rate per verb. At a
target of 200/s the run produces roughly 40,000 messages — enough for set reconciliation to
mean something, small enough that the AOF measurement (R6.3) stays interpretable.

---

## Ledger Format

One file per process: `{run}/ledgers/{node}.jsonl`. One object per line, no array, no
trailing state — a truncated file is still readable up to its last complete line.

```jsonc
{"ts":"2026-08-13T09:14:02.1187Z","app":"edge","node":"edge-1","phase":"gap",
 "kind":"sent","type":"SendEmail","cid":"edge-000123","msgId":"01J...","ms":2.4}

{"ts":"...","app":"notifications","node":"mailer-1","phase":"arrival",
 "kind":"processed","type":"SendEmail","cid":"edge-000123","attempt":1}

{"ts":"...","app":"edge","node":"edge-1","phase":"steady","kind":"executed",
 "type":"ValidateAccount","cid":"edge-000456"}
{"ts":"...","app":"edge","node":"edge-1","phase":"steady","kind":"replied",
 "type":"ValidateAccount","cid":"edge-000456","status":200,"ms":6.1}
```

| Field | Meaning |
|---|---|
| `kind` | `sent`, `processed`, `published`, `received`, `executed`, `replied`, `timed-out`, `failed`, `expected-miss`, `phase` |
| `cid` | producer-minted correlation id — the reconciliation key (D3) |
| `msgId` | broker-assigned id from `SendAsync`, recorded for `HW.DLQ PEEK` lookups |
| `group` | on `received`: the subscription group that received it (025) |
| `attempt` | on `processed`: delivery attempt, so duplicates are attributable |
| `ms` | latency where meaningful — context, never a published figure (C5) |

---

## The Reconciler

A console program, `Highway.Assurance.Reconciler`, referencing **no Highway client code**
(D5). Input: a run directory. Output: a human report plus `report.json`, and an exit code.

```
load every ledger  →  index by (kind, type, cid)
sample HW.STATS / HW.DLQ over RESP for the broker's own view
for each invariant I1..I7:
    compute set difference
    verdict = pass | fail | pass-with-notes
    on fail: name up to N offending cids, with every ledger line mentioning them
compare broker view against reconciler conclusions  →  conflict is its own failure (R6.1)
write report.json + report.md into the run directory
exit 0 only if every invariant passed
```

Invariants, restated as set operations:

| | Invariant | Computation |
|---|---|---|
| I1 | Queue completeness | `sent(SendEmail).cids ⊆ processed(SendEmail).cids` |
| I2 | No phantoms | `processed.cids ⊆ sent.cids` |
| I3 | RPC never silent | `executed.cids ⊆ (replied ∪ timed-out).cids` |
| I4 | Pub/sub per group | ∀ published `c`, ∀ group `g` live at `c`'s publish time: `(c,g) ∈ received` |
| I5 | Duplicates | `count(processed) − count(distinct processed.cid)`, reported per type |
| I6 | Dead letters | `HW.DLQ` count = 0, or each entry listed with its reason |
| I7 | Nothing left | final `HW.STATS` depth = 0 for every queue |

"Live at publish time" (I4) comes from the `HW.STATS` samples, not from any application's
belief about its peers — D7.

---

## Run Directory

```
assurance/runs/2026-08-13T09-14-02/
├── config/
│   ├── highway.json              the generated broker config (ephemeral port, fresh data dir)
│   └── profile.json              phases, target rate, mix — the run's inputs, kept
├── ledgers/
│   ├── edge-1.jsonl
│   ├── accounts-1.jsonl
│   ├── notifications-1.jsonl
│   └── mailer-1.jsonl
├── broker/
│   ├── stats-samples.jsonl       HW.STATS over time, per queue and channel
│   ├── recorder-replay.jsonl     HW.REPLAY dump — the broker's third record
│   ├── dlq.json                  HW.DLQ PEEK, if anything is there
│   └── storage.json              data dir + AOF bytes before/after (R6.3, feeds C4.6)
├── processes/
│   ├── *.stdout.log              each process's console output
│   └── resources.json            peak working set per process
├── report.md                     the verdict, human-readable
├── report.json                   the verdict, machine-readable
└── versions.json                 assembly versions, `highways --version`, git sha
```

---

## Orchestration

`Highway.Assurance.Runner` — a console program that owns the run:

1. Probe an ephemeral port; create the run directory; generate `highway.json`.
2. Start `highways --config …`; wait for a RESP `PING` rather than a sleep.
3. Start Edge, Accounts, Notifications-subs; wait until all three appear in `HW.STATS` and
   every group is registered — the settle phase's exit condition is observable, not timed.
4. Broadcast the phase schedule; sample `HW.STATS` on an interval throughout.
5. At the arrival boundary, start `notifications-mailer`.
6. At drain, signal producers to stop; wait for depth 0 with a bounded timeout.
7. Stop consumers via `Ctrl+C`-equivalent; collect exit codes.
8. Collect broker artefacts; stop the broker gracefully; record storage sizes.
9. Invoke the reconciler; print the verdict; append to `assurance/RUNLOG.md`.
10. On success clean the broker data directory; **on failure clean nothing** (D10).

Process control is `Process.Start` with redirected stdout, and graceful stop via
`CTRL_BREAK`/`SIGTERM` — the same path an operator uses, so the run also exercises the
shutdown story R2.4 depends on.

---

## Error Handling Strategy

Failures come from three places and must never be confused with each other:

1. **A harness failure** — a process won't start, a port is taken, a ledger can't be
   written. Reported as `HARNESS`, exit code distinct from an invariant failure. A harness
   failure never reports "messages lost", because it does not know.
2. **An invariant failure** — the run worked and the counting says something is wrong.
   Reported as `FAILED`, with the offending ids and every ledger line that mentions them.
   Triaged under `RUNLOG.md`'s rule: symptom, root cause, fix **in the library**, regression
   test. Adjusting the rig to make it pass is not an acceptable fix.
3. **A conflict** — the applications' ledgers and the broker's own record disagree.
   Reported as `CONFLICT`, naming both numbers. This is its own class because it most likely
   indicts the rig, and a rig that can be wrong must say so rather than blaming the product.

---

## Testing Strategy

| Layer | What | How |
|---|---|---|
| Reconciler | Every invariant, against hand-written ledger fixtures containing planted loss, planted duplicates, planted phantoms, an expected-miss and a dead letter | Unit tests — pure function over files, no processes |
| Ledger writer | Line-per-flush durability: kill mid-write, file still parses to the last complete line | Unit test |
| Applications | Each starts, registers, and handles one message of each kind | Existing integration patterns |
| The rig | Full timeline at reduced duration, asserting the same invariants (R8) | One xUnit test in `Highway.Integration.Tests` |
| The rig, full | 3–4 minutes at target rate | Manual/scheduled, recorded in `assurance/RUNLOG.md` |

The reconciler tests matter most: a judge nobody has tested against known-bad input is a
judge that will report `PASSED` on a run that lost half its messages. Every invariant gets a
fixture that *fails* it, and that fixture is what proves the invariant is wired at all.
