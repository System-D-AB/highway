# Design: Reliable Delivery

## Overview

Three changes that share one mechanism:

```
attempt count in the entry  ──┬──►  dead letter after N attempts        (Req 2)
                              │
delayed set + lazy promotion ─┼──►  PublishAsync(msg, delay: ...)       (Req 3)
                              └──►  requeue-with-backoff                (Req 4)

dedup key + cached response  ─────►  [Idempotent]                        (Req 5)
```

The first two are server-side and touch entry framing. The third is almost entirely
client-side and adds no `HW.*` command.

## The bug this starts from

`HwDequeueCommand` performs a lazy lease sweep: entries in a node's processing list whose
claim timestamp is older than `Lease` go back on the queue. There is no counter anywhere
in that loop, and nothing bounds it. A request whose handler throws every time is claimed,
abandoned, requeued, claimed… for the life of the deployment. It also blocks nothing else
— the queue is FIFO, so a poison message at the head is retried ahead of everything behind
it, forever.

The framing shows why there is no counter to increment:

| Entry | Layout today |
|---|---|
| RPC queue entry | `[u16 requestIdLen][requestId][payload]` |
| RPC processing entry | `[i64 claimTicksUtc][u16 requestIdLen][requestId][payload]` |
| Channel entry | `[i64 messageId][payload]` |
| Group processing entry | `[i64 receiveTicksUtc][i64 messageId][payload]` |

There is nowhere to put an attempt count without changing the format.

## Decision 1: The count lives in the entry, and that is a breaking change

Two alternatives were considered:

- **A side key per in-flight entry** (`hw:svc:{service}:att:{requestId}`). No framing change, but it adds a key per in-flight request, needs its own TTL and cleanup, and — worse — makes the count non-atomic with the requeue. A crash between requeue and increment loses the count, which is exactly the case the count exists for.
- **In the entry.** Atomic by construction: the count is written by the same list operation that moves the entry.

The entry wins. The cost is honest and must be stated: **this is a breaking storage-format
change.**

```
RPC queue entry        [u16 attempts][u16 requestIdLen][requestId][payload]
RPC processing entry   [i64 claimTicksUtc][u16 attempts][u16 requestIdLen][requestId][payload]
Channel/group entry    [u16 attempts][i64 messageId][payload]
Group processing entry [i64 receiveTicksUtc][u16 attempts][i64 messageId][payload]
```

`u16` because 65,535 attempts is far beyond any sane limit and two bytes is cheaper than
four; the count saturates rather than wrapping.

### Detecting an old entry rather than misparsing it

A v1 entry read as a v2 entry does not fail — it silently reinterprets the first two bytes
of the request-ID length as an attempt count and then reads a wrong length. That produces
a corrupt payload delivered to an application, which is far worse than an error.

**Implemented as a per-entry version byte, not the per-queue format key this document
originally specified.** The change was made during implementation because the version byte
is strictly better: it is self-describing, costs no extra key read on every queue command,
and works on entries already sitting in an AOF rather than only on queues touched after the
upgrade.

Every versioned entry begins with `0xFF`, which is unambiguous against every legacy leading
byte:

| Legacy entry | Leading byte | Can it be 0xFF? |
|---|---|---|
| RPC queue | high half of a u16 identifier length | only above 65,280 — rejected by options validation |
| Channel | high byte of a message-ID counter starting at 1 | no |
| RPC / group processing | high byte of a .NET tick count (currently `0x08`) | no |

A mismatch is refused with `HW_STORAGE_FORMAT`, naming the key:

```
ERR HW_STORAGE_FORMAT 'hw:svc:orders.create:q' holds entries in the pre-013 storage
    format. Drain it with the previous version, or delete the data directory.
    Refusing rather than misparsing, which would deliver a corrupt payload.
```

`HighwayServerBuilder` rejects a `MaxIdentifierBytes` above 65,279, so the reasoning in the
table cannot be quietly invalidated by a configuration change. A unit test asserts the
default stays below that bound.

Migrating entries in place on first touch was rejected: it is a write on a read path, it
must be atomic with everything else the command does, and the migration code would live
forever to serve a pre-1.0 product with no deployed users.

**Backlog entries are deliberately unversioned and unchanged.** A backlog entry has never
been delivered, so it carries no attempt count; it gains one (at zero) when promoted into a
group queue. Leaving that format alone also means existing backlog data survives the
upgrade and promotes correctly — a smaller blast radius for no loss.

**Highway has not shipped.** The realistic cost of this break is a `rm -rf ./data` in the
samples. It is documented anyway, because a storage break discovered in production is not
something to leave implicit.

### Three requeue paths, not one

Implementation found that the unbounded redelivery lived in **three** places, not the one
the review identified:

| Path | Where |
|---|---|
| RPC lease sweep | `HwDequeueCommand` — the one the code review found |
| Group lease sweep | `HwReceiveCommand` — the identical defect in pub/sub |
| Dead-node prune | `RequeueNodeWork` — would otherwise let a request escape the limit forever by always being recovered through this path rather than the lease sweep |

All three now increment. Missing the third would have produced a limit that is real for a
crashed consumer and unreachable for a pruned node, which is worse than no limit because it
looks like it works.

## Decision 2: Dead-lettering happens where the requeue already happens

The sweep in `HwDequeueCommand` already holds an exclusive lock on the queue and the
processing list. Dead-lettering is a third list in the same transaction:

```
hw:svc:{service}:dlq                  Object  List
hw:ch:{channel}:grp:{group}:dlq       Object  List
```

```csharp
// inside the existing lease sweep, per expired entry
var attempts = entry.Attempts + 1;
if (attempts > _opts.MaxDeliveryAttempts)
    RPUSH(dlqKey, DeadLetter.Frame(entry, attempts, DateTime.UtcNow.Ticks));
else
    RPUSH(queueKey, entry.WithAttempts(attempts));
// the processing-list removal is in the same transaction either way
```

Atomicity (Requirement 2 AC3) is free: it is one Garnet transaction, and the entry's
removal from the processing list is already part of it. There is no window in which the
entry is in both places or neither.

Dead-letter entry framing keeps everything needed to diagnose (Requirement 2 AC4):

```
[i64 deadLetteredTicksUtc][u16 attempts][u16 reasonLen][reason][original entry]
```

`reason` is a short code — `MAX_ATTEMPTS` initially — so a future cause (oversize,
unroutable) does not need another framing change.

### The DLQ command

One command, three forms, matching the shape `HW.STATS` and `HW.HEARTBEAT` already use:

```
HW.DLQ PEEK    <target> [COUNT n]   →  array of dead-letter entries, non-destructive
HW.DLQ REQUEUE <target> [COUNT n]   →  integer moved back, attempts reset to 0
HW.DLQ PURGE   <target> [COUNT n]   →  integer removed
```

`<target>` is `SVC <service>` or `CH <channel> <group>`. `PEEK` before `REQUEUE` in the
docs and in this list is deliberate: look, then decide.

`REQUEUE` resets the attempt count, because the operator requeues *after fixing something*
and a message that immediately re-dead-letters has wasted the round trip.

**Bounded (Requirement 2 AC9).** `MaxDeadLetterEntries` per list, oldest dropped, with the
drop counted and logged. A DLQ that can exhaust the server is a denial-of-service with
good intentions.

## Decision 3: Delayed delivery promotes lazily, not on a timer

```
hw:ch:{channel}:delayed    Object  Sorted Set   score = deliverAtTicksUtc
```

`HW.PUBLISH` gains an optional trailing argument (arity `3` → `-3`):

```
HW.PUBLISH <channel> <payload> [AT <ticksUtc>]
```

Absolute ticks rather than a relative delay, deliberately: the client computes
`UtcNow + delay` and the server stores what it was told, so a slow round trip does not
silently extend the delay, and the value is idempotent under AOF replay. A relative delay
replayed from the AOF would re-delay from the replay time — fabricating the future the
same way recording replay timestamps would fabricate the past (feature 002's reasoning for
keeping the recorder out of the keyspace).

### Why lazy promotion and not a server timer

A background timer would give tighter delivery resolution. It was rejected:

- Highway's server has exactly one background timer today (`RecorderSweeper`), and it touches only process memory. A timer that **writes to the keyspace** is a new class of thing: it needs its own transaction, its own failure handling, its own interaction with AOF replay, and it runs whether or not anyone is listening.
- Highway already has a lazy-recovery pattern that works and is understood — the lease sweep in `HW.DEQUEUE`. Promotion on `HW.RECEIVE` is the same shape, reviewed the same way, with no new failure mode.
- The client already polls on `BackstopInterval` (500 ms default) precisely because doorbells are an optimisation and correctness must not depend on them. That poll is the promotion driver, at no extra cost.

The honest cost, and Requirement 3 AC4 exists to make sure it is stated where developers
read it rather than buried here:

> **A delay is a "not before", not an alarm clock.** Resolution is bounded by the
> consumer's backstop interval, and a channel whose group has no running consumer promotes
> nothing until one starts. If you need second-accurate scheduled execution with no
> consumer running, you need a scheduler, and Highway is not one.

### Where the promotion happens, and the watch-conflict rule

Requirement 7 AC5 exists because this is exactly where 004.1's trap lives: reading an
object-store structure in `Prepare` registers a watch, and the exclusive lock the command
later takes on the same key fails watch-version validation, aborting the transaction.

| Command | Reads the delayed set | Phase | Safe because |
|---|---|---|---|
| `HW.PUBLISH ... AT` | no — only `ZADD`s | `Main` | write only |
| `HW.RECEIVE` | yes — range query, then move | **`Main`** | the key is declared and exclusively locked in `Prepare` and never *read* there |

`HW.RECEIVE`'s `Prepare` already declares the group queue and processing list. It adds
`hw:ch:{channel}:delayed` to the lock set — a key name derivable from the channel argument
alone, with no read required. All range-querying and moving happens in `Main`, under the
lock. This is the same discipline the mirror keys exist to enforce.

Ordering (Requirement 3 AC8) falls out: promotion happens before the receive is served, in
the same transaction, and promoted entries keep their original message IDs.

## Decision 4: Backoff reuses promotion, and is off for RPC

Requeue-with-backoff is requeue into the delayed set instead of the queue, with
`deliverAt = now + Backoff(attempts)`. No second mechanism.

Default schedule, and the reasoning Requirement 4 AC4 asks for:

```
attempt 1 → 1s      attempt 4 → 30s
attempt 2 → 5s      attempt 5+ → 60s (cap)
```

Exponential-ish, capped at a minute. The cap matters more than the curve: an uncapped
exponential reaches hours by attempt 12, at which point the message is functionally dead
but is still occupying a live queue and still counting toward nothing.

**Off by default for RPC** (Requirement 4 AC3), and this is the analysis behind it. The
default `CallTimeout` is 30 seconds. A caller issues `ExecuteAsync`, the handling node
dies, the lease expires after `Lease` (default 5 minutes) — the caller has *already* timed
out long before the first retry, backoff or not. Backoff on RPC therefore changes nothing
for the caller and only delays the eventual dead-letter. It is available for the case
where `Lease` has been tuned well below `CallTimeout`, and off otherwise.

For pub/sub there is no waiting caller, so backoff is on by default and is the more useful
half of this requirement.

## Decision 5: Deduplication is client-side, and its promise is narrow

The tempting design is server-side: have `HW.DEQUEUE` skip request IDs it has already
seen. It does not work. The server learns a request is complete when `HW.ACK` arrives —
and the duplicate exists *precisely because the ACK never arrived*. A server-side check
would deduplicate only the case that cannot happen.

The duplicate that actually occurs is: **handler ran, ACK lost, lease expired, redelivered.**
Only the handler side knows the handler ran. So the marker is written by the consumer,
before the handler runs, using keys on the server:

```
hw:idem:{name}:{id}       Main  String  SETEX  →  cached response (RPC) or a tombstone (pub/sub)
```

```csharp
// consumer loop, when the contract carries [Idempotent]
var claimed = await db.StringSetAsync(key, InProgress, window, When.NotExists);
if (!claimed)
{
    var prior = await db.StringGetAsync(key);
    if (prior == InProgress) return Outcome.Retry;   // concurrent duplicate, still running
    await ReplyWithCachedAsync(prior);               // RPC: caller gets the original response
    return Outcome.Suppressed;
}

var response = await handler(request);
await db.StringSetAsync(key, response, window);      // overwrite the marker with the answer
```

`SET NX EX` is atomic, so two concurrent redeliveries cannot both claim.

### The crash window, stated rather than hidden

Requirement 5 AC6 asks for this explicitly. Between claiming the key and writing the
response, the process can die. The key then holds `InProgress` until the window expires,
and a redelivery in that period sees `InProgress` and returns `Retry` — it does **not**
run the handler and does **not** reply.

That is the correct trade for an `[Idempotent]` contract: the developer has declared that
running twice is worse than running late. The message is redelivered after the window, or
eventually dead-letters. Choosing the other way — treat `InProgress` as "probably crashed,
run it again" — would silently break the one promise the attribute makes.

**The window therefore has a real meaning** and must be documented as such: it is how long
a crashed in-flight request stays blocked, not just how long duplicates are remembered.
Default 5 minutes, matching `ReplySlotTtl`.

### What it does not do

Stated in the XML docs on the attribute, verbatim, because a vaguer claim is a claim the
mechanism cannot keep:

> `[Idempotent]` deduplicates **Highway's own redelivery** — the same request or message
> arriving again after a lease expiry. It does **not** deduplicate a caller that issues the
> same logical request twice: that is a different request with a different ID, and Highway
> cannot know the two are related. If you need that, supply your own key.

An explicit key selector (`[Idempotent(nameof(Order.ExternalId))]`) is the natural
extension and is left out of this feature deliberately — it changes the guarantee from
"Highway's redelivery" to "whatever the developer's key means", and that deserves its own
design rather than being smuggled in.

## Options

```csharp
public int MaxDeliveryAttempts { get; set; } = 5;      // 0 = unlimited (today's behaviour)
public int MaxDeadLetterEntries { get; set; } = 10_000;
public TimeSpan DeadLetterRetention { get; set; } = TimeSpan.FromDays(7);
public bool RpcBackoffEnabled { get; set; }            // default false — see Decision 4
public bool PubSubBackoffEnabled { get; set; } = true;
public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromMinutes(1);
public TimeSpan DefaultIdempotencyWindow { get; set; } = TimeSpan.FromMinutes(5);
```

Five attempts because the failure this bounds is usually either transient (one retry
fixes it) or permanent (no number of retries fixes it). Five is comfortably past the first
and cheaply short of infinity.

## Protocol changes

| Change | Kind |
|---|---|
| `HW.PUBLISH` arity `3` → `-3`, optional `AT <ticks>` | additive |
| `HW.DLQ` — new command, arity `-3` | additive |
| Attempt count in four entry framings | **breaking storage format** |
| `hw:svc:{service}:dlq`, `hw:ch:{channel}:grp:{group}:dlq`, `hw:ch:{channel}:delayed`, `hw:*:fmt` | additive keys |
| `hw:idem:{name}:{id}` | additive key, written by clients |
| `HW_STORAGE_FORMAT` error code | additive |
| `DeadLettered`, `DeliveryDeduplicated` event types | additive |

`ProtocolConformanceTests` parses the Command Index, so `HW.DLQ` must be documented in the
same change that registers it — the gate has already fired twice on exactly this
(features 007 and 002).

## Testing

| Test | Proves |
|---|---|
| `PoisonMessage_StopsBeingRedelivered_AndLandsInDlq` | **the defect this feature exists for** |
| `DeadLettering_IsAtomic` | never in both lists, never in neither |
| `DlqPeek_DoesNotConsume` | Req 2 AC5 |
| `DlqRequeue_ResetsAttempts_AndRedelivers` | Req 2 AC6 |
| `DlqIsBounded_AndCountsDrops` | Req 2 AC9 |
| `DelayedMessage_NotDeliveredEarly_DeliveredAfter` | Req 3 AC3 |
| `DelayedMessage_SurvivesRestart` | Req 3 AC6, with AOF |
| `DelayedMessage_ReachesGroupThatSubscribedDuringTheDelay` | Req 3 AC7 |
| `OldFormatEntry_IsRefused_NotMisparsed` | Req 7 AC2 — write a v1 entry directly, assert the error |
| `Idempotent_DuplicateDoesNotRerunHandler_AndReturnsOriginalResponse` | Req 5 AC1, AC3 |
| `Idempotent_InProgressMarker_BlocksRatherThanRerunning` | the crash window, asserted as designed behaviour |
| `Idempotent_StateExpires` | Req 5 AC5 |

`OldFormatEntry_IsRefused_NotMisparsed` is the one most likely to be skipped and the one
whose absence would hurt most: without it, the migration guard is untested code that runs
only in the situation nobody can reproduce on purpose.

## Known gap: HW.STATS on an unhosted service

`HW.STATS <name>` resolves a name as a service only when the discovery index knows it, so a
service whose hosts have all departed or been pruned reports as a **channel**, with
`deadLettered` zero — because the channel form reads group dead-letter lists, not the
service one.

That is exactly the state an operator investigating a poison message may be in: the host
crashed, its registration expired, and the dead letters it left behind are invisible to the
command they would reach for first. `HW.DLQ PEEK SVC <service>` still finds them, because it
is told the target kind explicitly.

Not fixed here. The resolution would have to read the service queue or dead-letter list, and
both are object-store structures whose `Prepare`-phase read registers a watch that the
command's own locks would then fail (004.1). Fixing it properly means deciding the kind in
`Main`, which changes `HW.STATS`'s shape rather than adding to it. Asserted by
`Stats_OnAServiceWithNoRegisteredHost_ReportsAsAChannel` so it stays a recorded decision.

## Risks

**The storage break.** Real, and mitigated only by Highway being pre-release. The format
marker turns silent corruption into a loud refusal, which is the best available outcome.

**Lazy promotion under-delivers expectations.** Someone will read "delayed messages" and
expect a scheduler. Mitigated by naming the guarantee "not before" everywhere it appears —
API docs, protocol file, samples — rather than only in this document.

**Dead-lettering hides a bug instead of surfacing it.** A message that used to loop
noisily now disappears quietly into a list nobody watches. Mitigated by recording every
dead-letter in the flight recorder and surfacing DLQ depth in `HW.STATS`; a non-zero DLQ
should read like a non-zero drop counter — visibly not-normal.

**Scope.** Three parts is a lot for one feature, and the tasks are ordered so Part 1 (dead
letters) is independently shippable. If it needs to be cut, cut after Part 1 — that alone
fixes the only item here that is actively broken.

## Cross-references

- `src/Highway.Server/Commands/HwDequeueCommand.cs` — the lease sweep this bounds
- `docs/HIGHWAY-PROTOCOL.md` § Entry Framing, § Key Schema
- `docs/features/004.1-server-remediation/design.md` — the `Prepare` watch-conflict rule
- `docs/features/002-observability/design.md` — why volatile state stays out of the keyspace
