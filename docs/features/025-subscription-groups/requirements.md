# Feature: Subscription Groups — Logical Consumers, Physical Nodes

## Introduction

Highway conflates two identities in one option. `NodeName` is simultaneously the **physical
process identity** (heartbeats, leases, processing lists, decommissioning) and the **pub/sub
subscriber identity** (the group that receives one copy of every publish). The conflation was
deliberate in 018 — "a group IS a node" — and it is correct for exactly one topology: one
process per logical subscriber.

Scale a subscriber application to three replicas and the model breaks in the worst way,
*silently and semantically*: three unique NodeNames (required — sharing one corrupts claim
state) means three groups means **three copies of every event**. Every email sent three
times. There is no configuration that makes replicas compete today; fan-out is the only
behavior a subscriber can have.

The evidence trail: `docs/product/concerns.md` §4.1 (the finding), and the third review of
2026-08-10, whose author — writing *about* Highway's identity model — stated the rule as
"same name = compete", which is not and has never been how Highway works. A model that a
careful reviewer mis-learns while studying it needs the split, not better prose.

The fix separates the identities: **`SubscriptionGroup` names the logical consumer;
`NodeName` keeps naming the process.** Replicas that share a group compete for one copy;
distinct groups each receive their own. The default — group = NodeName — preserves today's
behavior bit-for-bit.

### The design constraint that shapes everything

The server derives every key a group's queue owns from `{channel}@{group}` — including the
processing list — *because* group == node meant the only claimant was knowable without
reading. Garnet requires every key declared in `Prepare` (the wall hit in 013, 014, 015,
017). The split must preserve that derivability, which forces the central decision: **the
claim identity for a subscription is the group, not the node** (see design D1).

## Requirements

### Requirement 1: The Option

**User Story:** As a developer deploying replicas of a subscriber application, I want to name
the logical consumer once, so that scaling out does not multiply event deliveries.

#### Acceptance Criteria

1. `HighwayOptions` SHALL gain `SubscriptionGroup` (string, default `null`). When null, the
   effective group SHALL be `NodeName` — today's behavior, unchanged.
2. The value SHALL be validated at startup by the same identifier rules as every Highway
   name, including the `@` rejection (the group is embedded in `{channel}@{group}` keys).
3. The group SHALL apply to all of the node's subscriptions. A per-subscriber override is
   registered as deferred, not built (one option teaches the model; twelve teach confusion).

### Requirement 2: Replicas Compete; Groups Replicate

**User Story:** As an operator, I want three replicas of `billing` to process each event
once between them, while `shipping` still receives its own copy.

#### Acceptance Criteria

1. Nodes sharing a `SubscriptionGroup` SHALL compete for that group's single copy of each
   published message, via the existing group-queue claim machinery.
2. Nodes with distinct groups SHALL each receive their own copy — fan-out semantics
   unchanged.
3. A message claimed by one replica and not acknowledged (crash, timeout) SHALL be
   redelivered to the group — any replica — by the existing lease sweep. At-least-once is
   preserved across the group.
4. `[Idempotent]` channel dedup SHALL be scoped per group delivery (one marker per group per
   message), so replicas do not re-run what a sibling completed within the window.
5. The failure-reporting identity (`HW.FAIL`, `HW.TOUCH`) for subscription work SHALL be
   consistent with the claim identity, so dead letters and lease renewal name the same party
   that claimed.

### Requirement 3: Liveness and Retirement Become Group-Aware

**User Story:** As an operator, I want a group retired only when *every* replica is gone, and
never while one is alive.

#### Acceptance Criteria

1. The server SHALL track group membership: which node ids currently back each group. The
   membership key SHALL be derivable from `{channel}@{group}` (Prepare-declarable).
2. A group SHALL be considered absent only when its **youngest member's** last heartbeat is
   older than `SubscriberRetirementThreshold`. One live member keeps the group alive.
3. `HW.HEARTBEAT BYE PURGE` SHALL remove the departing node's membership; it SHALL destroy a
   group's queue only when that node was the **last member**. Plain `BYE` remains
   backlog-preserving and membership-preserving.
4. With the default (group = NodeName, membership = {the node}), retirement and purge
   behavior SHALL be observably identical to feature 017.

### Requirement 4: The Protocol File Tells the Truth

#### Acceptance Criteria

1. `docs/HIGHWAY-PROTOCOL.md` SHALL be updated **in this feature**: `HW.SUBSCRIBE` semantics
   (group as logical consumer), the membership key in the Key Schema, the group-aware
   retirement invariant, and the `BYE PURGE` last-member rule.
2. Any storage format this feature adds SHALL be new keys, never a reinterpretation of an
   existing key's value. Existing registrations, backlogs and mirrors from a pre-025 broker
   SHALL keep working (membership for a legacy group defaults to the node of the same name).
3. `constraints.md` C2 (pub/sub) SHALL be updated in this feature: the fan-out unit is the
   subscription group; replicas within a group compete.

### Requirement 5: The Model Is Taught Where It Was Mis-Learned

#### Acceptance Criteria

1. The UserGuide's pub/sub section and rule 3 SHALL state: *the verb decides sharing; the
   subscription group decides who counts as one subscriber.* The deployment sentence — "run
   three replicas with the same `SubscriptionGroup` and they share; give them distinct
   groups and each gets a copy" — SHALL appear verbatim-simple.
2. The samples SHALL demonstrate it: a second order-service instance sharing
   `SubscriptionGroup = "order-service"` competes for `inventory.low` instead of
   duplicating it, observable in the RUNLOG.

### Requirement 6: Nothing Breaks

#### Acceptance Criteria

1. With `SubscriptionGroup` unset, the full suite SHALL pass unmodified, and 017's
   decommissioning tests SHALL pass unmodified.
2. The dashboard's catalogue and message views SHALL keep working; a group backed by
   several nodes SHALL display its members. No dashboard tests, per standing instruction.
3. `dotnet build --no-incremental` SHALL report zero warnings.
