# Design: Recurring Jobs

*Written with the Open Decisions unresolved, deliberately: §D1–§D7 analyse every option with a
recommendation, and the implementation phases in `tasks.md` do not start until OD1–OD5 are
settled in discussion (016's precedent). The architecture (§A) is option-independent.*

## A — The architecture: a schedule is a standing delayed entry that re-arms

The whole feature stands on one observation: **Highway already fires messages at future
times.** Delayed delivery (013) stores entries in a per-queue sorted set keyed by due-ticks,
and `HW.QCLAIM` *promotes* due entries into the live queue as part of the ordinary claim
path — poll-driven, broker-clocked, no timer. A recurring job is a delayed entry that,
instead of being consumed by promotion, **re-enqueues itself with a new due time**:

```
                        hw:job:{queue}:schedules        (sorted set: nextFireTicks → record)
                                   │
        node hosting IProcess<X> polls: HW.QCLAIM x  ──────────────┐
                                   │                               │ one transaction
                     due?  ────────┤                               │ (fire + re-arm atomic)
                                   ▼                               │
                     1. enqueue occurrence message ──► hw:q:x:q    │
                     2. nextFire = Next(expression) ──► re-insert ─┘
                     3. record JobFired
                                   │
                                   ▼
              ordinary queue machinery from here: competing claims,
              leases, [Idempotent], DLQ, dashboard message view
```

What this buys, each for free:

- **Exactly one fire per occurrence** — the fire happens inside the claim transaction's
  exclusive locks; two racing pollers cannot both promote the same due schedule. *No 029
  election needed*: the claim IS the election (§D2).
- **Broker-clock authority** — due-ness is evaluated where delayed promotion already
  evaluates it. Node clocks never matter.
- **No broker timer** — the no-alarm-clock philosophy holds; accuracy is "first poll after
  due", exactly as delayed delivery documents today.
- **Durability** — the schedule sorted set rides the same AOF as everything else.
- **A dead system fires nothing and loses nothing** — due schedules simply wait; what
  happens on wake-up is OD3, a *policy*, not a mechanism.

New machinery, exhaustively: one key shape (`hw:job:{queue}:schedules` + a small record
framing, versioned from byte one), fire-and-re-arm logic inside the existing promotion sweep,
one registration/inspection command (`HW.JOB SET|DEL|LIST`, appended to the command table),
one recorder event, and the client-side declaration API — which is OD1, below.

## D1 — Where a schedule is declared (OD1): the options, in full

The question that shapes the developer experience. F3's lesson is the tension: *names belong
on contracts; tuning does not* — and a schedule sits uncomfortably between the two.

### Option A — Attribute on the contract: `[Job("02:00")]`

```csharp
[Queue("statements.generate")]
[Job("02:00")]                          // fires daily at 02:00 UTC
public sealed record GenerateStatements : ISend;
```

| For | Against |
|---|---|
| Maximum discoverability — the contract says *what and when* in one glance | **F3 in full force**: the schedule is tuning frozen into the contract assembly. Moving a job by an hour = recompile and redeploy every node that references the contract, including senders that never fire it |
| Zero ceremony; house style (matches `[Queue]`, `[Idempotent]`) | Attribute arguments are compile-time constants: `Time.DailyAt(23,59,0)` **cannot compile** — only strings/numerics can (`"23:59"`, `EveryMinutes = 15`) |
| Scanner/manifest/analyzer see it for free | Per-environment schedules (staging hourly, prod nightly) are impossible without another mechanism anyway |

### Option B — Composition root, typed builder: the code owns the schedule

```csharp
services.AddHighway(o =>
{
    o.Jobs.Daily<GenerateStatements>(new TimeOnly(2, 0));
    o.Jobs.Every<ReconcileLedger>(TimeSpan.FromMinutes(15));
    o.Jobs.Cron<PruneAudit>("0 3 * * SUN");          // if OD2 admits cron
});
```

| For | Against |
|---|---|
| Schedule lives where tuning lives — the deployable, not the contract. Change it without touching contract packages | Not visible on the contract; a reader of `GenerateStatements` doesn't know it recurs (the manifest and dashboard compensate) |
| Full language available: `TimeOnly`, `TimeSpan`, computed values, per-environment `if` | Declaration is split from the message type it schedules |
| **Industry consensus**: Hangfire (`RecurringJob.AddOrUpdate`), Quartz, NServeBus scheduler — every .NET scheduler is code-at-startup. There is no attribute-declared scheduler in wide use, and that is evidence, not accident | Slightly more ceremony than A |
| Composes with configuration naturally: `o.Jobs.Daily<X>(TimeOnly.Parse(cfg["Jobs:X"]))` — *without Highway building a config system* | |

### Option C — Attribute as default, composition root overrides

`[Job("02:00")]` declares; `o.Jobs.Override<X>(...)` wins when present.

| For | Against |
|---|---|
| A's discoverability plus B's tunability | Two sources of truth for one fact — the "which one is live?" question F4 warned about, now with a precedence rule to memorize |
| | Ships both APIs on day one; twice the surface for a v1 |

### Option D — External configuration primary (`appsettings.json`)

Rejected as *primary*: stringly-typed, no compile-time link between the config key and the
contract, invents a Highway config schema. B already reaches configuration through ordinary
code without Highway owning a format. (Kept as a pattern in the UserGuide, not an API.)

### Option E — A handler interface: `IJob`

```csharp
public sealed class NightlyStatements : IJob
{
    public JobSchedule Schedule => JobSchedule.DailyAt(new TimeOnly(2, 0));   // runtime value!
    public Task RunAsync(CancellationToken ct) { ... }
}
```

| For | Against |
|---|---|
| Dodges the attribute-constant limit — the schedule is a runtime expression | **Adds a fourth handler shape** to a model whose whole pitch is three — the verb-freeze pressure point |
| Self-contained: one class is the entire job | Breaks the composition that makes 028 cheap: an `IJob` is not a message, so it does not inherit queues, DLQ, `[Idempotent]`, the dashboard message view — unless it secretly compiles down to a hidden contract + processor, at which point it is sugar over A/B with extra magic |
| | The schedule still lives in the implementation assembly — F3's problem, relocated but not solved |

### Recommendation: **B**, with A registered as possible later sugar

Option B is the recommendation, for three reasons that outrank discoverability: the schedule
is tuning and B puts it where tuning lives (F3, applied rather than re-litigated); attribute
constants would force the expression into strings anyway, surrendering A's main charm; and
the entire .NET ecosystem's convergence on code-declared schedules is a decade of other
people's A/B testing. The discoverability loss is real and is compensated where operators
actually look: the topology manifest lists schedules under PROVIDES, and the dashboard shows
them with next-fire times. If demand for the attribute emerges, A layers onto B later as
sugar (compiling to the same registration) without breaking anything — whereas shipping A
first and discovering F3's pain would mean an API retreat.

## D2 — Exactly-one-fire needs no election (and 029 is not a prerequisite)

Earlier planning assumed 028 needed 029's singleton primitive. The architecture dissolves
that: the fire step runs inside the claim transaction on the schedule set, under the same
exclusive locks that already make two nodes unable to claim one message. "Which node fires?"
is answered the way "which node claims?" always has been — first poller wins, and it does
not matter which, because firing only *enqueues*. The expensive work (the handler) is then
distributed by ordinary competing consumers. **029's scope shrinks accordingly** to
"long-lived singleton *processes*" (if it survives at all) — recorded here so the roadmap's
dependency note gets corrected when this ships.

## D3 — Schedule expression (OD2)

Ship a small typed core; cron as an explicit decision:

- `Daily(TimeOnly, TimeZoneInfo? tz = null)` — the 80% case. UTC default; if a tz is given,
  DST is resolved as *local wall-clock time, skipped hour fires at the next valid instant,
  repeated hour fires once* (the standard cron answer, stated in docs).
- `Every(TimeSpan)` — anchored to last fire, minimum 1 minute (below that, use a queue and a
  loop; a scheduler is the wrong tool and the floor says so).
- `Cron(string)` — recommendation: **in**, because "weekly on Sunday 03:00" falls off the
  typed cliff immediately and cron is the lingua franca operators already read. Standard
  5-field syntax, validated at startup (R1.5). If OD2 resolves to defer it, `Weekly(...)` and
  `Monthly(...)` builders must ship instead — the gap is real either way.

## D4 — Storage and framing

`hw:job:{queue}:schedules` — object-store sorted set, score = `nextFireTicks`, member = a
versioned record `[u8 version][job name][expression][created][lastFire]`. Plus the mirror
main-store key the Prepare rule will demand (`hw:job:{queue}:list`, newline job names) — the
004.1 pattern, applied at design time rather than discovered at the wall. One record per
declared job: topology-bounded, and `BoundedStructureTests` gets both rows (R5.4).

Key derivability: everything derives from the *queue name*, which `HW.QCLAIM` already holds —
so the promotion sweep can declare the schedule keys in `Prepare` without reading anything.
The wall (013/014/015/017) is designed around, not hit.

## D5 — Missed occurrences (OD3)

Broker restarts after 3 days; a daily job has 3 missed fires. Options:

| Policy | Behavior | Precedent |
|---|---|---|
| **Catch-up-one** ★ | Fire once immediately (the work is "run the statements job", not "run it three times"), then resume schedule | Hangfire's default instinct; almost always what the operator meant |
| Fire-all-missed | 3 messages, in order | Correct only for interval jobs whose payloads differ per occurrence — which Highway's (payload-free contract) jobs do not have |
| Skip-to-next | Nothing until tomorrow 02:00 | Surprising data gap; defensible for high-frequency `Every` jobs |

Recommendation: **catch-up-one for `Daily`/`Cron`, skip-to-next for `Every`** (a 15-minute
tick that is 3 days stale is noise, not backlog) — per-job override later if demanded, not in
v1.

## D6 — Overlap (OD4)

Next fire due while the previous occurrence is still unacknowledged. Default queue semantics
say *fire anyway* — two entries, possibly two replicas working concurrently. For many jobs
that is wrong (the nightly reconciliation must not run twice at once). Options: fire-anyway
(document `[Idempotent]` + handler guards), or **skip-while-outstanding** — at fire time, if
the *previous occurrence's message* is still unacknowledged (in queue or in processing),
advance `nextFire` without enqueueing, and record `JobSkippedOverlap`.

Recommendation: **skip-while-outstanding as the default**, fire-anyway as the opt-out flag.
A scheduler's users assume non-overlap (cron's one-at-a-time-per-slot mental model); the
surprising default is the dangerous one. Detection is cheap: the fire transaction already
holds the queue's keys, and the previous message id is in the schedule record.

## D7 — Ownership conflict (OD5)

Two nodes deploy with different schedules for the same job (mid-rolling-deploy, or a
misconfiguration). Options: last-registration-wins (catalog precedent — re-registering
updates), refuse-and-log, first-wins. Recommendation: **last-wins with a loud recorder event
naming both expressions** — it is the catalog's existing rule, it makes rolling deploys
converge on the new schedule (which is what a deploy *means*), and the event makes a genuine
conflict visible instead of silent. Refuse-and-log would leave rolling deploys permanently
half-updated.

## D8 — The payload template: the message is a signal, not a datum

The fire is server-side, but the payload is a client envelope of type `T`, and the server
cannot construct .NET objects. So registration stores a **template**: the client serializes
`new T()` once, `HW.JOB SET` carries those bytes, and every fire enqueues the same template.

Three consequences, stated rather than discovered:

1. **Every occurrence carries identical bytes.** A handler wanting occurrence-specific data
   ("which night is this run for?") derives it (`DateTime.UtcNow.Date`) — the server will not
   patch JSON inside a transaction (the same refusal as `startedOnNode`, for the same
   reason). The UserGuide documents the one trap: after a catch-up fire (OD3), *now* is later
   than the occurrence was scheduled.
2. **Scheduled contracts should be parameterless records.** The analyzer-someday and the docs
   say so; a contract with meaningful properties scheduled as a job is a smell (whose values
   would they be?).
3. **Manual trigger is free by construction**: `client.SendAsync(new GenerateStatements())`
   is the same contract, processor, and observability as a scheduled fire — "run it now"
   needs no API.

If demand for occurrence metadata in the payload materialises, it is an envelope-framing
addition later (versioned, per house rule) — not a v1 blocker.

## Error handling

| Condition | Behavior |
|---|---|
| Invalid expression | Startup validation error naming expression and accepted forms |
| Job declared, no processor hosted anywhere | Schedule exists, never fires (nothing polls); dashboard shows it with "no hosting node" state — loud, not silent (R2.5) |
| Fire enqueue refused (queue full, 016) | The occurrence is **not** consumed: `nextFire` unchanged, `JobFireRefused` recorded; retries on next poll — backpressure reaches the scheduler |
| Broker clock moves backwards | Fires only when due-ticks pass again; never double-fires (due-ness is monotonic against the stored `nextFire`) |
| `HW.JOB DEL` for unknown job | Idempotent `+OK`, like every removal |

## What already exists (reuse, not rebuild)

- Delayed-delivery promotion inside `HW.QCLAIM` — the fire step extends this sweep.
- The queue machinery end to end — occurrences are ordinary messages.
- Catalog registration — schedule declarations ride to the broker the same way.
- The dashboard's entity/message views — occurrences appear with zero new rendering; only
  the schedule list is new.
- 024's manifest — one new PROVIDES line kind.

## Test strategy (sketch — full matrix in tasks once ODs settle)

Fire-exactly-once under racing pollers (the D2 claim, adversarially: N nodes hammering claim
at the due instant, one message); re-arm atomicity across a kill between fire and ack;
durability across restart; each OD3/OD4 policy behaviorally; expression validation;
`BoundedStructureTests` rows; protocol conformance; the samples' RUNLOG run. Every guard
verified against deliberately broken logic, per standing practice.
