# Tasks: Node Decommissioning

**T1 is the enabling task and nothing works without it.** The set of channels a node subscribes
to must be readable in `Prepare`, or no key can be declared and every later task hits the wall
013, 014 and 015 each hit.

**T8 is the feature's reason to exist.** Everything before it is plumbing whose purpose is
unverified until a blocked channel is seen recovering.

---

## Phase 1 — The join that has never existed

### - [ ] T1 — A node's channel list, as a mirror key

`hw:reg:node:{nodeId}:channels` — main store, newline-delimited, written by `HW.SUBSCRIBE` and
`HW.UNSUBSCRIBE`.

*Requirements:* R3.1, design "Where the sweep runs"
**Done when:** subscribing and unsubscribing keep it accurate, and a test proves it after a
mixed sequence including duplicate subscribes and unsubscribes of names that were never there.

**Main store, not object store.** It is read in `Prepare` so that every
`hw:q:{channel}@{group}:*` key can be *derived and declared* — and reading an object-store
structure in `Prepare` registers a watch that later exclusive locks fail against (004.1). This
is the same shape `hw:ch:{channel}:grplist` already uses, for the same reason.

### - [ ] T2 — Retire one node's groups, as a shared operation

The single implementation both the explicit paths and the automatic sweep call.

*Requirements:* R4.1, R4.2, R4.3
**Done when:** retiring a node deletes each subscriber group's queue, byte counter, delayed set,
dead-letter list and processing lists; **requeues** its queue and RPC processing lists; and
returns counts of what it destroyed.

**One implementation, three callers.** 013 found one bug living in three independently written
requeue paths and 015 found a block dropped at three of four re-encode sites. A retirement
written once in T2 and called by T3, T4 and T6 cannot diverge the way those did.

---

## Phase 2 — The two explicit paths

### - [ ] T3 — `HW.HEARTBEAT <node> BYE PURGE`

*Requirements:* R2.1–R2.4
**Done when:** it retires a node that cannot speak for itself, is idempotent, returns what it
destroyed, and answers zero — not an error — for a node nobody has heard of. An operator
cleaning up after an incident should not have to know which names still exist.

### - [ ] T4 — `IHighwayClient.CleanAndByeForever()`

*Requirements:* R1.1–R1.5
**Done when:** it stops the loops, drains in-flight work within the existing shutdown timeout,
purges, and returns what it destroyed.

> **Stop the loops FIRST.** This is a correctness requirement, not tidiness: the heartbeat loop
> re-registers the node, so a purge issued while it still runs is undone moments later and the
> node reappears with an empty catalog. That looks exactly like a purge that worked — and then
> silently did not. **A test must assert the node is still gone a full heartbeat interval
> later**, or this defect ships undetected.

---

## Phase 3 — The part nobody has to remember

### - [ ] T5 — `SubscriberRetirementThreshold`

*Requirements:* R3.2
**Done when:** the option exists, defaults to **24 hours**, and `TimeSpan.Zero` disables
automatic retirement.

The default is not a midpoint, and the reasoning belongs next to it in the summary: retiring too
early loses messages a live subscriber would have processed (a **correctness** failure against
C2.3); retiring too late leaves a channel blocked (an **availability** failure an operator can
also fix by hand). When in doubt, wait longer.

### - [ ] T6 — Retirement rides on the heartbeat prune

*Requirements:* R3.1, R3.3, R3.6
**Done when:** a group whose node has been absent beyond the threshold is retired automatically,
driven by **liveness evidence** rather than a consumption gap — and a test proves the
difference by leaving a group unconsumed but its node heartbeating, then asserting it survives.

That test is what separates Highway from `x-expires` and `AutoDeleteOnIdle`, which cannot tell a
dead subscriber from a nightly batch job. If it does not exist, the advantage is only claimed.

**No timer per group** (R3.6). One extra registry walk on a path that already walks the
registry, at a frequency the deployment already sets by heartbeating.

### - [ ] T7 — Retirement is loud

*Requirements:* R3.4, and R3.5's documentation
**Done when:** every retirement logs at **Warning** naming the node, how long it was absent, the
threshold, and **how many messages and bytes were discarded**; a flight-recorder event carries
the same; and `HW.STATS` counts retirements.

Plus a `NodeSuspect` recorder event at **half** the threshold, so an operator replaying a
channel that later went quiet sees the warning that preceded it — most of the value of a
suspect state with none of its maintenance.

**This is the largest single loss Highway can inflict.** C4.3's rule — a loss is never silent —
applies here more than anywhere else in the product.

---

## Phase 4 — Proving it

### - [ ] T8 — **A blocked channel recovers**

*Requirements:* R5.1, R5.2, R5.3

```
1. two groups on a channel; fill one until publishes are refused
2. assert HW_QUEUE_FULL naming the full group          (016 behaviour, still true)
3. retire that group
4. assert publishes succeed again
5. assert the surviving group's queue is untouched
```

**This is the feature.** T1–T7 are plumbing whose purpose is unverified without it, and it is
the only test that proves 016's Open Decision 5 was safe to accept.

### - [ ] T9 — The rest of the coverage

- `RetiredNodeStaysGone_AcrossAHeartbeatInterval` — T4's resurrection defect
- `UnconsumedButLiveGroup_IsNotRetired` — evidence, not inference (T6)
- `RetiringAnUnknownNode_ReturnsZero` — R2.4
- `RetirementIsIdempotent` — R2.2
- `AReturningNode_StartsEmpty` — R3.5, C2.4 working as intended
- `RetiringANode_RequeuesItsQueueAndRpcWork_ButDeletesItsSubscriptions` — R4's asymmetry, in one test so the difference is impossible to miss
- `ThresholdZero_DisablesAutomaticRetirement` — the opt-out

---

## Phase 5 — Conformance

### - [ ] T10 — Protocol document

*Requirements:* R6.1, R6.2
**Done when:** `BYE PURGE`, the `hw:reg:node:{nodeId}:channels` key and the retirement events are
documented, and `ProtocolConformanceTests` is green. Same change as the code — that gate has
fired six times now.

### - [ ] T11 — Constraints

*Requirements:* R6.3
**Done when:** **C2.3 gains its limit** — "a subscriber that is down receives what it missed,
*until its node is declared gone*" — and C4.7's hazard note records that automatic retirement is
its mitigation.

C2.3 has been unqualified since it was written, and it is no longer true without a bound. A
guarantee with an unstated expiry is the kind of drift `constraints.md` exists to catch.

### - [ ] T12 — Samples and full verification

*Requirements:* R6.4, R6.5
**Done when:** a sample shows a node retiring and a channel recovering, the samples are re-run
across real processes with a `RUNLOG.md` entry, all tests pass, and the build is warning-free.

---

## The line that must not move

**A restart must never lose a backlog.** Retirement is for nodes that are gone, not for nodes
that are slow. A crash and a slow restart are indistinguishable in the moment, which is why the
discriminator is *time* and why the default is generous. Any change that shortens the threshold
for convenience is trading C2.3 for tidiness.

**And: retirement is never silent.** It destroys more data in one act than anything else in
Highway. An operator who cannot answer "what did we lose?" afterwards has been failed by the
feature even if the channel recovered.
