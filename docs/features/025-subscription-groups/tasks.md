# Tasks: Subscription Groups

## Phase 0 — the option and the claim identity

### - [x] T1 — `SubscriptionGroup` option, validated

*Requirements:* R1.1–R1.3
**Done when:** the option exists, defaults to null → effective group = `NodeName`, validates
by the identifier rules (including `@` rejection) in `HighwayOptionsValidator` with a
fail-fast startup error; the per-subscriber override is recorded as deferred in
`constraints.md`.

### - [x] T2 — The claimant becomes the group (client)

*Requirements:* R2.1–R2.3, R2.5
**Done when:** `SubscriptionWorkerLoop` claims, acks, touches and fails with the group as
claimant id (D1); with the option unset, every wire value is byte-identical to today
(asserted, not assumed); the 018 "a group IS a node" comments at each touched site now read
"the claimant IS the group".

### - [x] T3 — Compete and replicate, proven

*Requirements:* R2.1–R2.3
**Done when:** the four behavioral tests exist and pass — compete (no duplicates, full
coverage), replicate (2N deliveries), crash-recovery to a sibling, and each was verified to
FAIL against deliberately-broken logic (e.g. claimant left as node) before being trusted.

## Phase 1 — membership on the server

### - [x] T4 — `HW.SUBSCRIBE` carries the node; membership mirror maintained

*Requirements:* R3.1, R4.1, R4.2
**Done when:** the command takes `<channel> <group> <node>` (arity 4, table position
unchanged — the A1 manifest guard passes on an existing data directory); the
`hw:grp:members:{channel}@{group}` mirror is created/extended idempotently; the protocol
document's command entry and Key Schema are updated **in this task**;
`ProtocolConformanceTests` is green.

### - [x] T5 — Group-aware retirement

*Requirements:* R3.2, R3.4, R4.2
**Done when:** the sweep computes group liveness as the youngest member's heartbeat; a group
with one live member is never retired (tested); a group with all members past threshold
retires with 017's recorder event (tested); a legacy group without a membership key falls
back to the 017 rule (tested); 017's existing tests pass unmodified.

### - [x] T6 — `BYE PURGE` respects shared groups

*Requirements:* R3.3, R3.4
**Done when:** purge removes membership always and destroys the queue only for the last
member (tested both ways); the reply's `[groups, messages, bytes]` counts only what was
destroyed; the protocol document's `BYE PURGE` semantics updated **in this task**; 017's
purge tests pass unmodified.

## Phase 2 — semantics that must be true, proven

### - [x] T7 — Idempotency dedups across replicas

*Requirements:* R2.4
**Done when:** a test proves an `[Idempotent]` channel delivery completed by replica-1 is
suppressed on redelivery to replica-2 within the window — checked against broken code
(distinct markers) first.

### - [x] T8 — Dashboard shows members; no dashboard tests

*Requirements:* R6.2
**Done when:** a group entity page lists its member nodes (from the membership mirror via
`BrokerState`); verified against running samples; the projection addition is tested
server-side; no tests in the dashboard project, per standing instruction.

## Phase 3 — the record

### - [x] T9 — Constraints, protocol invariants, deferred register

*Requirements:* R4.1, R4.3
**Done when:** `constraints.md` C2 states the fan-out unit is the group and replicas compete
within it; the protocol Invariants section carries the group-aware retirement rule; the
per-subscriber override sits in the Deferred table with its reasoning.

### - [x] T10 — UserGuide rule 3 and the samples

*Requirements:* R5.1, R5.2, R6.1, R6.3
**Done when:** the UserGuide states the deployment sentence ("same `SubscriptionGroup` →
share; distinct → each gets a copy"); a second order-service instance with a shared group
demonstrably competes for `inventory.low` in the RUNLOG; full suite green; zero-warning
`--no-incremental` build.

---

## Parallelization

```
LANE 0   T1, T2, T3     client claim identity     → provable against today's server
LANE 1   T4, T5, T6     server membership          → protocol work, sequential within lane
LANE 2   T7, T8         cross-cutting proofs       → needs both lanes
LANE 3   T9, T10        the record                 → last

Order: 0 ∥ 1 → 2 → 3   (T3's compete test needs only lane 0 — the queue machinery
                         already supports multiple claimants; retirement correctness
                         is what needs lane 1)
```

## Registered, not built

- **Per-subscriber group override** — no concrete need surfaced in three reviews (T1
  records it).
- **Per-replica in-flight attribution within a group** — the shared processing list makes it
  unobservable server-side (D1); the recorder's `NodeId` covers the diagnostic need.


---

## What execution found

- **The node argument became optional (arity -3), not required (arity 4).** The spec said
  required; execution found ~30 existing wire-level callers and the negative-arity convention
  (`HW.PUBLISH -3`, `HW.HEARTBEAT -2`) already meaning "optional trailing argument". Absent
  node = the pre-025 identity (the group is the node), so an old client's subscribe keeps its
  exact meaning and membership degrades to `{group}` — which makes the 017 retirement rule the
  natural default rather than a special case. Strictly more compatible than the spec; recorded
  as the better decision.
- **`BoundedStructureTests` fired on first contact, again.** The two new mirror keys could not
  land without stating what bounds them — the 016 guard doing precisely what it was built for,
  third feature running.
- **The 024 unattributed flake is now attributed**: `TestSubscriber` statics shared across
  parallel test classes (`ServiceExecutorTests` × `DelegateCompilerTests`). Fixed with a
  dedicated fixture; the 024 note said "if it reappears, the name comes first" — it did.
- **`HW.REPLAY` cannot see derived queues** (`@` rejected by its identifier rules). The T7
  evidence read comes from the in-process recorder via a new internal `HighwayTestServer`
  accessor. Registered as a wire-observability gap, not fixed here.
- **The compete test was verified against a broken world** by making
  `EffectiveSubscriptionGroup` ignore the option: replicas fell back to per-node groups and
  the test failed with "each replica received a copy" — exactly the defect the feature ends.
- **UserGuide integration happened twice**: the user rewrote the guide mid-feature; the 025
  content was re-integrated into the new structure (Pub/Sub behavior bullets + Choosing
  Between Them) rather than restoring the old shape over their edit.
