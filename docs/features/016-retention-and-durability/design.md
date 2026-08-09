# Design: Retention, Storage and Durability

> **Five open decisions, answered 2026-08-09.** Recorded here as the design's premises.
>
> | | Decision | Chosen |
> |---|---|---|
> | 1 | Byte budget scope | **Per structure now**, server-wide registered as an open constraint |
> | 2 | Refusal class | **Permanent** (`HW_QUEUE_FULL`) |
> | 3 | `MaxDeliveryAttempts` off-by-one | **Not in this feature**; stays registered |
> | 4 | Default data directory | **Beside the executable** |
> | 5 | Fan-out when one group is full | **Refuse the whole publish, name the group** |

## The shape of the change

```
BEFORE                                  AFTER

Build()                                 Build()
  DataDir = null                          DataDir = <exe dir>/highway-data
  EnableAOF = false                       EnableAOF = true
  EnableStorageTier = false               EnableStorageTier = true
  -> everything lost on exit              Recover = true
                                          -> queued work survives a restart

enqueue                                 enqueue
  RPUSH, no size check                    RPUSH if under MaxQueueBytes
  trim to 10,000 entries, silently        else refuse: HW_QUEUE_FULL, named

AOF                                     AOF
  grows forever                           AofSizeLimit -> checkpoint -> truncate
```

## Decision 4 — The data directory lives beside the executable

`AppContext.BaseDirectory/highway-data`, with the port appended when it is not the default, so
two brokers on one machine do not collide.

**The trade this accepts.** A read-only deployment — a scratch container, a locked-down install
directory — cannot create it. R1 AC6 governs that case: **fail at `Build()` naming the path**,
never degrade silently to memory-only. A broker that quietly becomes non-durable is the exact
problem this feature exists to remove, and it would be worse after this change than before,
because the guarantee would now be documented as true.

```
Build()
  |
  +- DataDir explicitly set?  -> use it
  |
  +- Ephemeral() requested?   -> memory-only, deliberately
  |
  +- otherwise                -> <AppContext.BaseDirectory>/highway-data[-{port}]
        |
        +- creatable and writable? -> durable, path logged at Information
        |
        +- not writable?           -> throw at Build(), naming the path AND
                                      naming WithDataDir() / Ephemeral() as the
                                      two ways out
```

The startup log states the path unconditionally (R1.3), because "where is my data?" must not
require reading source.

### `Ephemeral()` is the escape hatch, and it is one call

`HighwayTestServer` uses it, and so does anyone who genuinely wants a disposable broker. Making
durability the default only works if opting out is trivial — otherwise the tests fight the
default and someone eventually flips it back.

## Decision 1 — Per-structure byte budgets

`MaxQueueBytes`, default 1 GB, applied per queue. After 018 that one setting covers both verbs:
a `SendAsync` queue and a `PublishAsync` group queue are the same structure.

**What this does not do, stated plainly:** it does not bound the process. Ten queues at their
limit is ten gigabytes. That gap is registered as a **new constraint (C4.7)** rather than left
implied, because an operator reading "1 GB" will otherwise assume the wrong thing.

### Accounting without paying for it on the write path

R2.4 forbids measurable cost on enqueue. A running byte counter per queue, kept in the main
store beside the queue and updated in the same transaction:

```
hw:q:{name}:bytes     main-store integer, maintained by the same transaction
                      that pushes or pops the entry

enqueue:  read counter (already locked) -> compare -> INCRBY entrySize
claim:    DECRBY entrySize
sweep:    DECRBY on dead-letter move, INCRBY on the dead-letter list's own counter
```

**Why a counter and not a measurement.** Asking Garnet for a structure's size on every enqueue
is O(n); a counter is O(1) and is updated inside a transaction that already holds the lock. The
cost is that the counter can drift if a code path forgets it — which is why the *test* in R3.4
enumerates structures rather than trusting the implementation, and why a drift-detection check
recomputes and compares in the test suite.

## Decision 2 — A full queue is a permanent error

`HW_QUEUE_FULL`, carrying the `ERR HW_` prefix, which Highway's error contract already defines
as **permanent** (004.1). The connection does not retry it.

A full queue may well drain, which is the argument for transient. It is the wrong argument: the
client's bounded retry would hold a connection and hammer a broker that is already over budget,
and if the queue does not drain the caller learns nothing until the retries are exhausted.
Backpressure is information the application has to act on — shed load, buffer, alert — and only
the application knows which.

## Decision 5 — A fan-out refuses as a whole, and says which group

018 guarantees a publish reaches every registered group or none, inside one transaction. That
guarantee is kept.

```
HW.PUBLISH orders.placed <payload>

Prepare: lock every group queue and its byte counter
Main:    for each group -> would this push exceed MaxQueueBytes?
              |
              +- any group over? -> write NOTHING, return
              |                     ERR HW_QUEUE_FULL group 'billing' ...
              |
              +- all fit         -> push to every group
```

**The cost, accepted with eyes open:** one stuck subscriber blocks the channel for the healthy
ones. The mitigation is not to hide it but to make it *loud and attributable* — the error names
the offending group, so an operator fixes a subscriber rather than debugging a channel.

The alternative — deliver to the groups that fit — was rejected because it retracts C2.1 from
"at least once per registered group" to "at least once, unless full", and a guarantee with a
silent exception is not a guarantee. A per-group circuit breaker is the real answer if this
proves intolerable in practice; it is a feature, not a footnote, and it needs its own constraint
because it trades C2.1 for availability.

## Decision 6 — AOF growth is bounded (R6, independent of everything above)

```
AofSizeLimit = 512 MB   ->  Garnet checkpoints when the log reaches it
                            and truncates what the checkpoint covers
```

Highway already sets `CheckpointDir` and never turns this on, so today the log grows until the
disk does not. This requirement is independent of every decision above and ships even if the
rest is descoped — a broker that cannot restart in bounded time is a broker that cannot be
operated.

## Error handling

| Case | Behaviour | Why |
|---|---|---|
| Data dir not creatable | throw at `Build()`, naming the path and both escapes | R1.6 — silent degradation is the problem being removed |
| Queue at its byte limit | `HW_QUEUE_FULL`, permanent, naming the queue and limit | R4.1, R4.2 |
| One group full on publish | nothing written, error names the group | Decision 5 |
| Byte counter drifts | test recomputes and compares | a counter is O(1) but trusts every writer |
| AOF at its size limit | checkpoint and truncate, not refuse | log growth is the broker's problem, not the producer's |
| Flight recorder full | still drops, deliberately | R4.5 — diagnostic and explicitly volatile (002) |

## What this design does not do

**It does not bound the process.** Decision 1, registered as C4.7. Anyone reading `MaxQueueBytes`
as a server-wide limit is reading it wrong, and the constraint says so.

**It does not fix the `MaxDeliveryAttempts` off-by-one.** Decision 3. It belongs with the
attempt-counting work that redefines what an attempt is, and bolting it on here would change
behaviour for anyone who has tuned the value, for reasons unrelated to storage.

**It does not add high availability.** Durability is not failover (`constraints.md` C5).

## Cross-References

- `docs/product/constraints.md` — C4.1–C4.6 closed here, C4.7 added by Decision 1
- `docs/features/018-pubsub-unification/design.md` — the atomic fan-out Decision 5 preserves
- `docs/features/013-reliable-delivery/design.md` — dead-letter bounding, the pattern followed
- `docs/features/002-observability/design.md` — byte accounting that does not cost the write path
