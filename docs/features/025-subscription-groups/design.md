# Design: Subscription Groups

## Overview

```
                        PUBLISH inventory.low
                                 |
             fan-out: one copy per SUBSCRIPTION GROUP
                 |                            |
     queue inventory.low@billing    queue inventory.low@shipping
                 |                            |
      +----------+----------+                 |
      |          |          |                 |
  billing-1  billing-2  billing-3        shipping-1
      claim → process → ack                (sole member)
      (replicas COMPETE via the
       existing queue machinery)

  membership   hw:grp:members:inventory.low@billing = billing-1␊billing-2␊billing-3
  liveness     group absent ⇔ youngest member's heartbeat older than threshold
```

The delivery mechanics barely change — a group's queue already *is* a queue, and Highway's
queues already support competing consumers. What changes is **who the claimant is** and **how
the server decides a group is dead**.

## Key decisions

### D1 — The claim identity is the group, not the node

**This is the decision everything else follows from.** The server derives every key a group
owns from `{channel}@{group}` — including the processing list — because 018 could assume the
only claimant was the same-named node. Garnet's `Prepare` must declare every key `Main`
touches (the wall of 013/014/015/017), so per-*node* processing lists under a shared group
queue would make retirement and `HW.UNSUBSCRIBE` unable to declare their keys without first
reading membership — the exact read-in-Prepare trap the mirror keys exist to avoid.

Therefore: replicas claim with the **group** as claimant id. One shared processing list per
group (`QueueProcessing("{channel}@{group}", group)`), exactly as today — three replicas pop
from one queue into one processing list, each entry leased per-message. Claim exclusivity is
per message (the pop is atomic); acknowledgement removes by id; the lease sweep recovers
unacked entries to the queue for *any* replica. `GroupQueueKeys` does not change shape.

Consequences, stated honestly:
- `HW.QCLAIM`/`HW.QACK`/`HW.TOUCH`/`HW.FAIL` for subscription work carry the group where
  they carried the node (R2.5) — client-side change, no protocol shape change.
- Per-replica in-flight attribution inside a group is not observable server-side (the
  processing list is shared). The recorder still knows the physical node — worker loops
  record `NodeId` — so the dashboard's message timeline keeps naming the actual replica.
- 018's "a group IS a node" comments are updated everywhere they stand: the sentence becomes
  "**the claimant IS the group**", which is the invariant that actually kept the keys
  derivable all along.

### D2 — Membership is a new mirror key, never a reinterpretation

`hw:grp:members:{channel}@{group}` — newline-delimited node ids, main store, derivable from
channel+group (Prepare-declarable), maintained by:

- `HW.SUBSCRIBE channel group` → adds the subscribing node id (new argument: the node —
  see D3).
- `HW.HEARTBEAT BYE PURGE` → removes the node; destroys the group iff it removed the last
  member (R3.3).
- Retirement sweep → group's `lastSeen` = max over members' registration timestamps;
  a member with no registration record contributes nothing (it is gone).

**Legacy compatibility (R4.2):** a pre-025 group has no membership key. The sweep falls back
to 017's rule — treat the group name as a node name and use that node's heartbeat. Default
deployments (group = NodeName) therefore behave identically whether or not the membership
key exists yet.

### D3 — `HW.SUBSCRIBE` gains the subscriber's node id

Today: `HW.SUBSCRIBE <channel> <group>` (arity 3). The server cannot maintain membership
without knowing *who* subscribes, so the command becomes
`HW.SUBSCRIBE <channel> <group> <node>` (arity 4). Protocol change, same-feature protocol
doc update, and — per the A1 manifest guard — the command table entry is *modified in
place*, not reordered; arity changes do not move positions. Old clients sending arity 3
receive the standard arity error; Highway.Client and Highway.Server ship together (the
protocol doc's compatibility stance since 004).

### D4 — Idempotency scope falls out correctly

Channel `[Idempotent]` markers key on the derived queue name `{channel}@{group}` plus message
id. Sharing the group shares the marker — which is exactly R2.4's requirement: a redelivery
after replica-1's crash finds replica-1's completed marker and is suppressed for replica-2
too. No change needed; a test proves it rather than assumes it.

### D5 — One option, node-wide

`SubscriptionGroup` applies to every subscription the node hosts. Per-subscriber overrides
(attribute on the subscriber class) are registered as deferred: they re-open the "which
identity am I?" confusion this feature exists to close, and no review produced a concrete
need. The option validates as an identifier (rejecting `@`) in `HighwayOptionsValidator`,
fail-fast at startup like every other misconfiguration (005 R12).

## Client-side sequence

```
StartAsync
  group = options.SubscriptionGroup ?? options.NodeName
  for each channel:  HW.SUBSCRIBE channel group node     (D3)
  SubscriptionWorkerLoop(channel):
      derived = "{channel}@{group}"
      HW.QCLAIM derived group          ← group, not node (D1)
      run subscribers
      HW.QACK   derived group id
      on failure: HW.FAIL … CH derived group …
      long handler: HW.TOUCH derived group id
```

## Error handling

| Condition | Behavior |
|---|---|
| `SubscriptionGroup` fails identifier rules | Startup validation error naming the rule (fail-fast, 005) |
| `BYE PURGE` from a non-last member | Membership shrinks; queue and backlog untouched; reply reflects zero destroyed groups for shared ones |
| Membership key missing for a legacy group | Sweep falls back to group-name-as-node (D2); no error |
| Publish while all members briefly absent | Unchanged from 017: backlog accrues until retirement threshold, bounded by `MaxQueueBytes` |

## What already exists (reuse, not rebuild)

- The group-queue claim/ack/lease machinery — untouched; replicas compete through it.
- `HW.SUBSCRIBE`'s group parameter — the wire already models groups; only the client stopped
  conflating them, plus one new argument.
- 017's retirement sweep — gains a membership-aware liveness function, keeps its threshold,
  bounds and recorder event.
- The dashboard catalogue's `Group` kind and `ParentChannel` — display works today; the node
  page's "Hosts" and the group's member list come from registration + membership.

## Test strategy

The heart of the suite is behavioral, on embedded Garnet:

1. **Compete:** two nodes, same group — N publishes yield N total deliveries, split between
   them, none duplicated (asserted by a per-message recorder, the 018 pattern).
2. **Replicate:** two nodes, distinct groups — N publishes yield 2N deliveries.
3. **Recover:** replica crashes mid-claim → lease sweep hands the message to the sibling.
4. **Retire:** group with one dead and one live member is NOT retired; both dead past
   threshold → retired, with 017's recorder event.
5. **Purge:** `BYE PURGE` from first member shrinks membership; from last member destroys
   the queue — 017's tests unmodified prove the default path.
6. **Dedup:** `[Idempotent]` marker shared across replicas (D4).
7. **Legacy:** a group with no membership key retires by the 017 rule.

Every new test is checked against deliberately-broken logic before being trusted (the
project's standing practice since 015 T0).
