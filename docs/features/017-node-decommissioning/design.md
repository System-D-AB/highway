# Design: Node Decommissioning

> **Four open decisions, answered.** Recorded as this design's premises.
>
> | | Decision | Chosen |
> |---|---|---|
> | 1 | Backlog disposal | **Delete, and record what was destroyed** |
> | 2 | Idle threshold | **24 hours**, configurable |
> | 3 | Scope of automatic retirement | **Subscriber groups only** |
> | 4 | A "suspect" state | **No new state**; a recorder event at half the threshold |

## The shape of the change

```
BEFORE                                    AFTER

heartbeat registry                        heartbeat registry
  knows: shop-3 gone 7 days                 knows: shop-3 gone 7 days
        │                                         │
        │  (never speak)                          │  RetireDeadGroups
        │                                         ▼
subscription registry                     subscription registry
  hw:ch:{ch}:groups                         group retired, queue deleted
  still lists shop-3                        byte budget released
        │                                         │
        ▼                                         ▼
publishes fan in until 1 GB                publishes succeed again
channel blocked for everyone               healthy subscribers unaffected
```

**The whole feature is the arrow in the middle.** Both registries already exist and both are
already correct; nothing has ever connected them.

## Three ways a node retires, in order of how often each is used

```
                                        who acts     when
CleanAndByeForever()                    the node     it is shutting down for good
HW.HEARTBEAT <node> BYE PURGE           an operator  the node is already gone
automatic retirement (this feature)     the broker   nobody noticed
```

The third is the one that matters. The first two require somebody to know and to act, and the
failure this feature exists to fix is precisely the case where nobody did.

## Decision 1 — Retirement deletes, and says what it deleted

A retired group's queue is deleted outright: the list, its byte counter, its delayed set, its
dead-letter list and its processing lists.

**Why not dead-letter the backlog.** Preserving a gigabyte of messages addressed to a subscriber
that has declared it will never exist is preserving them for nobody — and the bytes are only
reclaimed if the dead-letter list is itself trimmed, so the hazard would survive the fix. This
is what RabbitMQ's `x-expires` and Azure's `AutoDeleteOnIdle` do, for the same reason.

**But an operator must be able to answer "what did we lose?"** So retirement records:

```
Retired subscriber group 'billing' on channel 'orders.placed':
  node 'shop-3' last seen 2026-08-02T11:04:19Z (7.2 days ago, threshold 24h)
  discarded 41,203 messages / 1,048,102 bytes
```

Warning level, plus a flight-recorder event, plus a counter in `HW.STATS`. C4.3's rule — a loss
is never silent — applies more strongly here than anywhere else in the product, because this is
the largest single loss Highway can inflict.

## Decision 2 — 24 hours, and the asymmetry that sets it

```
too early                          too late
─────────                          ────────
a restarting subscriber loses      a channel stays blocked longer
its backlog                        an operator can fix by hand
CORRECTNESS failure (C2.3)         AVAILABILITY failure
```

These are not symmetric, so the default is not a midpoint. A subscriber absent for a full day is
not restarting; a deploy that takes a day has other problems. When in doubt, wait longer.

`SubscriberRetirementThreshold`, default `TimeSpan.FromHours(24)`. `TimeSpan.Zero` disables
automatic retirement entirely, for anyone who would rather have the outage than the deletion —
a legitimate position for a system where every subscriber is precious.

## Decision 3 — Evidence, not inference

```
RabbitMQ x-expires / Azure AutoDeleteOnIdle       Highway
─────────────────────────────────────────         ───────
"nobody consumed for N minutes"                    "the node owning this group has
                                                    not heartbeated for N hours"

cannot tell a dead subscriber from a               a nightly batch job heartbeats
nightly batch job                                  all night; it is obviously alive
```

Because 018 made a group **be** a node — `SubscribeGroupAsync(channel, NodeName)` — the group
name *is* the registry key. No new mapping is needed; the join already exists and has never
been used.

**This is the one place Highway can be strictly better than its comparators**, and it costs
nothing to take.

## Where the sweep runs

R3.6 forbids a timer per group. The retirement check rides on the **heartbeat prune** that
feature 006 already performs:

```
HW.HEARTBEAT (any form)
   │
   ├─ existing: prune nodes past the liveness timeout
   │
   └─ NEW: for each pruned-or-long-absent node,
            retire its subscriber groups
```

One pass, on a path that already walks the registry, at a frequency the deployment already
sets by heartbeating. A broker with a thousand idle groups pays for one extra registry walk on
a heartbeat, not a thousand timers.

**The Prepare-phase problem, and why this shape solves it.** Retirement must delete keys named
`hw:q:{channel}@{group}:*`, and the set of channels a node subscribes to is not derivable from
a heartbeat's arguments — it must be *read*. Garnet rejects touching a key not declared in
`Prepare`, and reading an object-store structure in `Prepare` registers a watch that later
exclusive locks fail against (004.1).

The existing mirror-key pattern is the way through: a **main-store, newline-delimited list of
the channels a node subscribes to**, written by `HW.SUBSCRIBE`, read in `Prepare`, from which
every key to be deleted is then derivable and declarable.

```
hw:reg:node:{nodeId}:channels     main store, newline-delimited
                                  written by HW.SUBSCRIBE / HW.UNSUBSCRIBE
                                  read in Prepare -> derive and declare every key
```

This is the same shape `hw:ch:{channel}:grplist` already uses for the fan-out, and for the same
reason. Building it any other way ends at the wall 013, 014 and 015 each hit.

## Decision 4 — No suspect state, but a visible warning

A `NodeSuspect` flight-recorder event at **half** the threshold, carrying the node, its groups
and how long remains. No stored state, no state machine, no new field on any reply — the
recorder already exists to answer "what was happening before this went wrong?"

An operator replaying a channel that later went quiet sees the warning that preceded it. That
is most of the value of a suspect state without any of its maintenance.

## What retirement does per verb (R4)

| | Action | Why |
|---|---|---|
| **Subscriber group** | queue **deleted** | The messages were addressed to this subscriber alone. Nobody else can process them and it has declared it will never exist |
| **Queue worker** | processing list **requeued** | A queue is shared by competing consumers. Claimed work belongs to the queue, not the node; another worker takes it |
| **RPC** | processing list **requeued** | A caller may still be waiting. Destroying its request turns a slow answer into no answer |

The asymmetry is deliberate and is the thing most likely to surprise, so it is stated in the
protocol document and in `CleanAndByeForever`'s own summary rather than only here.

## `CleanAndByeForever()` — the ordering that is not optional

```
1. stop the loops           ← FIRST, always
2. drain in-flight work     (bounded by the existing shutdown timeout)
3. HW.HEARTBEAT BYE PURGE
4. return what was destroyed
```

**Step 1 before step 3 is a correctness requirement, not tidiness.** The heartbeat loop
re-registers the node; a purge issued while it is still running is undone by the next heartbeat
moments later, and the node is resurrected with an empty catalog — which looks like the purge
worked and then silently did not.

## Error handling

| Case | Behaviour | Why |
|---|---|---|
| Retiring an unknown node | returns zero, no error | An operator cleaning up after an incident should not need to know which names still exist |
| Retiring twice | idempotent, second returns zero | R2.2 |
| Node returns after retirement | re-subscribes, starts **empty** | C2.4 working as intended (R3.5); documented so it is not reported as a defect |
| Threshold set to zero | automatic retirement off | A legitimate choice for a deployment that prefers the outage to the deletion |
| A group retired mid-publish | atomic — the publish sees the group or it does not | The group list is locked in `Prepare`, as the fan-out already requires |

## What this design does not do

**It does not implement message retention.** Different timer, different job, needs a framing
change (C4.1).

**It does not retire queues.** A queue is shared; one node leaving is not the queue ending.

**It does not make retirement reversible.** A retired backlog is gone. That is the decision, and
the logging exists so it is a known loss rather than a mystery.

## Cross-References

- `docs/features/016-retention-and-durability/requirements.md` — Open Decision 5, which accepted the blocking cost on the condition this exists
- `docs/features/018-pubsub-unification/design.md` — the atomic fan-out, and the group-is-a-node fact this design depends on
- `docs/features/006-heartbeat-service-registry/` — the liveness evidence and the prune this rides on
- `docs/features/004.1-server-remediation/` — the mirror-key rule that makes the `Prepare` phase possible
