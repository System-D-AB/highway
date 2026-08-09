# Design: Long-Running Tasks

## Overview

One additive server command, one client-side renewal loop, and a bound that keeps lease
recovery alive.

```
BEFORE — the lease is a hard ceiling

  claim ──────────── handler still running ──────────────────────►
        │
        ├─ 5 min: sweep sees claimTicks < (now - Lease)
        │         → requeue, attempts++
        │
        └─ another worker claims the SAME message while the first runs
                    → concurrent duplicate, then dead letter at attempt 5


AFTER — the ceiling moves while work is genuinely progressing

  claim ──┬─ 1 min ─ HW.TOUCH ─┬─ 1 min ─ HW.TOUCH ─┬─── handler completes ── QACK
          │  claimTicks = now   │  claimTicks = now  │
          │                     │                    │
          └─────────────────────┴────────────────────┘
                     sweep never sees an expired entry

  … but only up to MaxProcessingTime (15 min default):

  claim ──┬─ touch ─┬─ touch ─┬─ … ─┤ cap reached: STOP renewing, warn, record
          │         │         │     │
          └─────────┴─────────┴─────┴──► lease expires → sweep → requeue
                                          → attempts++ → eventually dead letter
```

**The whole feature is that middle diagram, plus the fact that the bottom one still
happens.** Renewal without a bound is not a smaller version of this feature, it is a
different and worse one: it removes the only mechanism that recovers a hung consumer.

## Decision 1 — Renewal rewrites `claimTicks`; it does not add a field

The shared sweep already decides expiry like this (`HighwayCommandBase.LeaseSweep.cs`):

```csharp
decode(span, out var claimTicks, out var id, out var payload, out var attempts);

if (claimTicks >= leaseExpiry)   // leaseExpiry = now - opts.Lease.Ticks
{
    keep.Add(span.ToArray());
    continue;
}
```

So moving `claimTicks` forward to *now* **is** restarting the lease. There is nothing else to
change: no new field, no new framing, no new key, no change to how the sweep reads an entry.

| Consequence | |
|---|---|
| Protocol stays **additive** | 4.3, a minor bump. No existing command, reply, key or framing changes |
| The sweep is untouched | Zero risk to the path that recovers everything else |
| Works for all three families at once | RPC, queue and derived group queues share the framing and the sweep |
| Old entries need no migration | An entry written before 019 is renewable the moment it is claimed |

**Rejected: store an absolute `leaseUntilTicks` in the entry.** More explicit to read, and it
changes the processing-entry framing — a breaking change requiring 5.0, for a field the sweep
can already infer. 015 also put an optional trailer on every entry; a *second* trailer type
would mean strip-and-parse logic that must handle both in either order, which is precisely the
shape of bug 015's `CarryFailureBlock` was written to prevent.

**Rejected: a per-call lease on `HW.QCLAIM` (`HW.QCLAIM <queue> <node> LEASE <ticks>`).** This
one is not merely inelegant, it is **unsafe**, and the reason is worth recording because it is
not obvious. `HwQClaimCommand` sweeps *every known node's* processing list using **one**
`leaseExpiry` computed from the caller's own value:

```csharp
var leaseExpiry = DateTime.UtcNow.Ticks - _opts.Lease.Ticks;
var allNodes = _knownNodes.Contains(_nodeId) ? _knownNodes : [.. _knownNodes, _nodeId];
foreach (var node in allNodes)
    SweepExpiredEntries(api, procKey: QueueProcessing(_queue, node), …, leaseExpiry, …);
```

Node A claiming with a two-hour lease and node B claiming with five minutes means B's sweep
evaluates A's entries against B's threshold — and requeues work that is still legitimately
running. A per-call lease would have to be stored per entry to be correct, which is Decision 1
again by a longer route.

## Decision 2 — `HW.TOUCH`, with `HW.FAIL`'s grammar and shape

```
HW.TOUCH SVC <service> <node> <requestId>   →   :1 | :0
HW.TOUCH Q   <queue>   <node> <messageId>   →   :1 | :0
```

Arity **5** (exact), **2** forms. Named `TOUCH` rather than `QTOUCH` because it serves both
families, matching `HW.FAIL` and `HW.DLQ`.

`HwFailCommand` is a structural template, not merely a stylistic one. Both commands do the
same four things:

```
Prepare                                  Main
───────                                  ────
parse SVC|Q → derive procKey             pop the whole processing list
read <name> <scope> <id>                 find the entry whose id matches
lock procKey  X object                   rewrite THAT entry
  (derivable from arguments alone,       push everything back in order
   so declarable — 015 Decision 4)       reply :1 if found, else :0
```

`HW.FAIL` rewrites to attach a failure block; `HW.TOUCH` rewrites to move `claimTicks`. The
matching, the list rewrite, the not-found reply and the "does not acknowledge" property are
identical, so the implementation should share whatever it can and the two should be read
side by side in review.

**Preserving the failure block is not optional.** A message can report a failure (015) and
then be renewed on a later attempt. `HW.TOUCH` re-encodes the processing entry, so it is a
**fifth re-encode site** and must call `Envelope.CarryFailureBlock` like the other four. 015's
own design says this exact class of miss "would fail **silently**" — the block simply
disappears and the dead letter shows last-failure-only. The test in T8 is the guard.

**`Q` reads the name with `TryReadDerivedIdentifier`**, not `TryReadIdentifier`, because after
018 a subscriber's queue is named `{channel}@{group}` and `@` is otherwise reserved. That is
the same distinction `HW.QCLAIM`, `HW.QACK`, `HW.DLQ` and `HW.FAIL` already make.

## Decision 3 — The client renews where in-flight work is already tracked

`SingleMessageWorkerLoop.ProcessAndReleaseAsync` is the one place that knows a handler is
running and when it stops. Renewal belongs there, so all three loops inherit it and a fourth
loop cannot forget it.

```csharp
private async Task ProcessAndReleaseAsync(string id, byte[] payload, CancellationToken workToken)
{
    using var renewal = StartRenewal(id);          // no-op when disabled
    try
    {
        await ProcessAsync(id, payload, workToken).ConfigureAwait(false);
    }
    catch (OperationCanceledException) { /* unchanged */ }
    catch (Exception ex)
    {
        await Reporter.ReportAsync(Target, id, ex, FailureDisposition, CancellationToken.None)
            .ConfigureAwait(false);
    }
    finally
    {
        _gate.Release();
    }
}
```

`StartRenewal` returns a disposable that cancels its own timer, so **every** exit path —
success, throw, cancellation, cap — stops renewing without a fifth `finally`. `RpcWorkerLoop`
supplies `SVC`, `QueueWorkerLoop` and `SubscriptionWorkerLoop` supply `Q`; the existing
`Target` property already carries the family and the name, so nothing new needs plumbing.

```
StartRenewal(id)
   │
   └─► loop:  await Task.Delay(LeaseRenewalInterval, linkedToken)
              │
              ├─ elapsed > MaxProcessingTime ?
              │     └─ log Warning, record LeaseRenewalExhausted, STOP
              │
              └─ HW.TOUCH <family> <name> <scope> <id>
                    ├─ :1 → continue
                    ├─ :0 → message is gone (acked or swept); STOP quietly
                    └─ throw → log Debug, continue (best effort, C7.1)
```

**A `:0` stops renewal quietly.** The message is no longer ours — acknowledged by us a moment
ago, or swept and now someone else's. Continuing to renew would be harmless but would log
noise forever, and stopping is the honest reading of the reply.

**A failed renewal never propagates.** It is logged and the loop continues; if renewals keep
failing the lease expires and the message is recovered exactly as it is today. This is C7.1
and 015 Decision 6 applied to a second mechanism: *losing the protection is survivable, losing
the thing being protected is not.*

## Decision 4 — On by default, bounded at 15 minutes

Two defaults, and the second is what makes the first defensible.

```csharp
/// Interval between lease renewals while a handler runs. Default: 1 minute.
public TimeSpan LeaseRenewalInterval { get; set; } = TimeSpan.FromMinutes(1);

/// Longest a single message may be renewed for. Zero disables renewal.
/// Default: 15 minutes.
public TimeSpan MaxProcessingTime { get; set; } = TimeSpan.FromMinutes(15);
```

**Why on by default.** The failure it removes is silent duplicate execution of a handler that
is working correctly. The failure it introduces is a hung handler recovering in 15 minutes
instead of 5. The first corrupts data and is invisible until someone reads the dead-letter
queue; the second is a delay an operator can see. 018 set this precedent when it chose
concurrency 1 for subscriber groups — pick the default that preserves correctness, not the one
that preserves the previous number.

**Why the interval is 1 minute against a 5-minute lease.** Five renewals' worth of headroom,
the same ratio the heartbeat deliberately keeps against `NodeExpiry` (6×), and for the same
reason: several consecutive failures should be survivable before the thing being protected
expires. The client cannot read the server's `Lease`, so this relationship is documented at the
point of configuration rather than validated — an operator who lowers `Lease` to 90 seconds has
made renewal unreliable and only the XML doc will tell them.

**Why 15 minutes and not an hour.** Anything measured in hours should be chunked (Decision 6),
not renewed. Fifteen minutes is three times the current effective ceiling — enough to fix the
handler that is merely slow — and short enough that a hung one is recovered inside a coffee
break.

## Decision 5 — Hitting the cap is loud; individual renewals are silent

| Event | Where it goes |
|---|---|
| Each successful renewal | `LogDebug` only |
| Renewal returned `:0` | `LogDebug`, stop |
| Renewal threw | `LogDebug`, continue |
| **Cap reached** | `LogWarning` naming queue, id, elapsed **+ recorder event `LeaseRenewalExhausted`** |

Recording every renewal would put the least interesting thing Highway does into the flight
recorder once a minute per in-flight message, evicting things that matter. The cap is the
event: it means a handler is either mis-sized or stuck, and both are worth a dashboard row and
an `HW.REPLAY` hit.

`HighwayEventType` gains one member. Additive — 002's schema is a self-describing enum and
readers ignore members they do not know.

## Decision 6 — For work measured in hours, do not hold the message

Renewal fixes the handler that is *slow*. It is the wrong tool for the job that is *long*,
and the cookbook says so plainly.

```
ONE LONG HANDLER (what renewal enables — use up to ~15 min)

  claim ─── 12 minutes of work, renewed ─── ack
  ✗ deploy mid-job → cancelled at DrainTimeout → restarts from zero
  ✗ progress invisible
  ✗ no parallelism


CHUNK AND CHECKPOINT (what hours-long work should do)

  page 0 ─┬─ 2 s ─ checkpoint ─ ack ─ SendAsync(page 1)
          │
  page 1 ─┼─ 2 s ─ checkpoint ─ ack ─ SendAsync(page 2)
          │
   …      ┴─ … until a page comes back empty → PublishAsync(Finished)

  ✓ survives deploys — at most one page is redone
  ✓ progress is a column in YOUR table; queryable, joinable, displayable
  ✓ WorkerConcurrency parallelises pages for free
  ✓ a poison page dead-letters alone; the job continues
  ✓ the lease never matters
```

The state lives in the application's database because that is where it belongs — the same
conclusion the saga analysis reached. Highway supplies durable delivery and durable timers;
a second copy of "how far did we get" inside the broker would be a second source of truth with
no transaction spanning the two.

Three rules the cookbook must state, because they are what makes it work:

1. **Guard first.** Every handler opens by checking the state it expects and returning if it
   does not hold. One line, and it delivers idempotency, out-of-order tolerance and
   stale-timeout safety together.
2. **`[Idempotent(WindowSeconds = n)]` with `n` above the worst-case duration.** The gate uses
   `SET NX EX` with an in-progress marker, so a redelivery arriving while the original still
   runs is neither run nor acknowledged. The window defaults to 5 minutes; a 12-minute handler
   that leaves it alone loses the protection precisely when it needs it. Note the shape —
   `WindowSeconds`, an `int`, because attribute arguments must be compile-time constants.
3. **`MaxPayloadBytes` is 1 MiB.** Long-running work over large data passes a blob reference,
   not the blob.

## The renewal path, end to end

```
Worker node                          Broker
───────────                          ──────
QueueWorkerLoop
  ├─ gate slot acquired
  ├─ HW.QCLAIM invoices node-1  ───► pop, stamp claimTicks, push to proc list
  │                              ◄── [messageId, payload]
  │
  ├─ StartRenewal("m-42")
  │     │
  │     ├─ t+60s  HW.TOUCH Q invoices node-1 m-42 ───► find m-42 in
  │     │                                              hw:q:invoices:proc:node-1
  │     │                                              rewrite claimTicks = now
  │     │                                              CarryFailureBlock(…)
  │     │                                        ◄──── :1
  │     ├─ t+120s HW.TOUCH … ─────────────────────────► :1
  │     └─ t+900s cap reached → Warning + LeaseRenewalExhausted, stop
  │
  ├─ IProcess<GenerateInvoice>.ProcessAsync(…)     (finishes at t+180s)
  ├─ renewal disposed → timer cancelled
  └─ HW.QACK invoices node-1 m-42 ──────────────► remove from proc list
```

Every box on the broker side except the `HW.TOUCH` handler already exists and is already
tested.

## Error handling and edge cases

| Case | Behaviour | Why |
|---|---|---|
| `HW.TOUCH` on an acknowledged message | `:0`, nothing happens | A late renewal is a race, not an error — `HW.FAIL`'s precedent |
| `HW.TOUCH` on a swept message | `:0`; the entry now belongs to whoever claimed it | Renewing someone else's claim would be worse than losing ours |
| `HW.TOUCH` with an unknown target kind | `HW_INVALID_ARG` naming the accepted forms | Consistent with `HW.FAIL`; a silently ignored target looks like a working call |
| `@` in the `Q` name | Accepted — derived group queues need it | 018; `SVC` and node names still reject it |
| Renewal command fails (broker blip) | Logged at Debug, loop continues | C7.1 — protection must never break delivery |
| Renewal races the handler's own ack | Ack wins; the next renewal gets `:0` and stops | The entry is gone; there is nothing to keep alive |
| Cap reached while handler still runs | Stop renewing; handler continues to completion | The handler is not cancelled — it may still succeed, and its ack will then get `:0`, which is harmless |
| Handler finishes *after* the sweep requeued it | Its `QACK` returns `:0`; a duplicate is possible | Unchanged from today, and why R6 mandates `[Idempotent]` |
| Renewal enabled, `Lease = TimeSpan.Zero` | Renewal is pointless but harmless | Lease sweeping is disabled entirely; documented, not special-cased |
| Pre-013 entry in the processing list | `HW_STORAGE_FORMAT`, as everywhere else | Refuse over misparse |

## Testing

```
HW.TOUCH ─┬─ Q, live entry ─────────── claimTicks moves, :1        T-1  ★★★
          ├─ SVC, live entry ────────── claimTicks moves, :1        T-2  ★★
          ├─ derived {ch}@{grp} name ── accepted, renewed           T-3  ★★★
          ├─ already acked ──────────── :0, no write                T-4  ★★
          ├─ unknown target ─────────── HW_INVALID_ARG names forms  T-5  ★★
          ├─ does not ack ───────────── entry still in proc list    T-6  ★★★
          ├─ does not bump attempts ─── count unchanged             T-7  ★★
          └─ failure block survives ─── firstType preserved         T-8  ★★★ silent-gap guard

renewal ──┬─ slow handler completes ─── ONE delivery, no duplicate  T-9  ★★★ E2E
          ├─ stops on completion ────── no touch after ack          T-10 ★★
          ├─ cap → recovery ─────────── requeued, attempts++, warn  T-11 ★★★
          ├─ MaxProcessingTime=0 ────── no HW.TOUCH ever sent       T-12 ★★
          ├─ touch throws ───────────── handler unaffected, loop up T-13 ★★★ NSubstitute
          └─ subscriber path ────────── renewal works via {ch}@{grp} T-14 ★★★

startup ──── DrainTimeout < MaxProcessingTime → warn once           T-15 ★★
```

| Test | Proves |
|---|---|
| **`T-9 SlowHandler_ExceedsLease_IsDeliveredExactlyOnce`** | **The reason to build this.** Server with `Lease = 2 s`, `LeaseRenewalInterval = 500 ms`; a handler that sleeps 6 s. Asserts the handler ran **once** and the queue is empty. Fails on today's code with 3–4 executions, which is the point |
| **`T-11 RenewalCap_StopsRenewing_AndMessageIsRecovered`** | **R3.2, the bound that keeps recovery alive.** `MaxProcessingTime = 2 s` against a handler that never returns: renewal stops, the sweep requeues, attempts increments, the warning is logged and the recorder event is present. Without this test, "bounded" is an unverified claim |
| **`T-8 TouchPreservesFailureBlock`** | 015's silent-failure class, at a fifth re-encode site. Report a failure, renew, let the lease expire, dead-letter: `firstType` must still be there |
| **`T-13 FailingTouch_DoesNotBreakTheHandler`** | R2.4 + C7.1. NSubstitute the connection so `HW.TOUCH` throws every time; the handler still completes, the message is still acknowledged, the loop is still running |
| `T-3 / T-14` | 018's dividend — one command and one loop cover subscribers because a group **is** a queue |
| `T-6 / T-7` | The two properties that make renewal safe to compose with everything else: it moves a deadline and nothing else |

`T-9`, `T-11` and `T-13` are the three that justify the design. `T-9` is the capability;
`T-11` is the constraint that stops it becoming a foot-gun; `T-13` is the rule that stops it
becoming a liability.

## Failure modes

| Codepath | Realistic failure | Test | Handled | Silent? |
|---|---|---|---|---|
| `HW.TOUCH` entry rewrite | message already acked | T-4 | `:0` | no |
| Renewal timer → broker | broker blip mid-job | T-13 | best-effort, logged | no |
| **Renewal cap reached** | handler hung | T-11 | stop, warn, record | no — Warning + event |
| **Failure block on renewal** | `firstType` dropped | **T-8** | `CarryFailureBlock` | **would be silent** |
| Handler outlives cap and finishes | duplicate execution | T-11 | `[Idempotent]` + R6 guidance | no — documented as the trade |
| Shutdown mid-long-handler | work cancelled, redelivered | T-15 | warned at startup | no — warned |

**One would-be-silent gap**, and it is the same one 015 found: the failure block at a
re-encode site. That is why T-8 lands with the command rather than after it.

## Risks

**Renewal makes a hung consumer look healthy for longer.** Mitigated by the cap, by the
Warning, and by the recorder event — and stated as a deliberate trade in R4 rather than left
to be discovered. `MaxProcessingTime = TimeSpan.Zero` is the exact opt-out.

**Renewal traffic on a busy queue.** One small command per in-flight message per minute. At
`WorkerConcurrency = 8` that is eight commands a minute per queue — negligible against the
claim/ack traffic already flowing. Worth measuring, not worth pre-optimising: Highway has no
throughput benchmark at all (C5), so a batching `HW.TOUCH` now would optimise against a number
nobody has. Same reasoning that deferred batch claims in 018.

**A fifth re-encode site for the failure block.** Structurally the riskiest thing here, and
the reason `CarryFailureBlock` exists as a named helper with a comment naming every call site.
T-8 guards it.

**The client cannot validate `LeaseRenewalInterval` against the server's `Lease`.** An
operator who lowers `Lease` below ~3× the interval silently loses renewal. Documented at both
options; a `HW.STATS` field exposing the effective lease would close it and is deliberately
out of scope.

## Parallelization

```
LANE 1  Server        HW.TOUCH, registration, protocol doc        → blocks lane 2
LANE 2  Client        options, renewal loop, cap, startup warning → needs 1
LANE 3  Docs          cookbook, constraints, product, roadmap     → independent
LANE 4  Verification  tests, sample, full build                   → last

Order:  1 ∥ 3  →  2  →  4

Lane 3 touches only markdown and can start immediately. Lanes 1 and 2 are strictly
sequential: there is no point wiring a client to a command that does not exist,
and T-9 cannot pass until both halves are in.
```

## What this design deliberately does not do

**No per-queue lease.** Deferred with its reasoning in the requirements' Non-Goals. Renewal
covers the case that motivated it, and adding a second mechanism for the same problem is how
018's duplication started.

**No new field, framing, or key.** Decision 1. The measure of success is that
`docs/HIGHWAY-PROTOCOL.md` § Entry Framing and § Key Schema are **unchanged** by this feature.

**No cancellation, no progress reporting, no job abstraction.** `HW.TOUCH` answers exactly one
question — "is this consumer still alive?" — and answers it in one round trip.

## Cross-References

- `docs/features/015-recoverability/design.md` — Decision 4 (entry rewrite over side keys, and *why* a per-message key cannot be declared in `Prepare`), Decision 6 (best-effort diagnostics)
- `docs/features/013-reliable-delivery/design.md` — attempt counting and dead-lettering: the path the cap hands back to
- `docs/features/014-queue/design.md` — the shared lease sweep, and T2's rule that a second copy is the defect
- `docs/features/018-pubsub-unification/design.md` — a group is a queue, which is why `Q` alone covers subscribers
- `docs/features/004.1-server-remediation/design.md` — the `Prepare`-phase watch rule that makes `procKey` declarable
- `docs/product/constraints.md` — C1.2, C3.3, C5, C7.1
