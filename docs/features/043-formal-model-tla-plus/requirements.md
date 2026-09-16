# Feature 043 — Formal Model of the Replication/Herd Protocol (TLA+)

*Model-checks the failover protocol shipped by features [042](../042-replication/)
and [042-1](../042-1-replication-improvements/). This feature writes a **TLA+
specification** of that protocol and runs the **TLC model checker** to exhaustively explore every
crash / partition / rejoin / timing interleaving a bounded cluster can produce, proving the safety and
liveness invariants those features assert — or handing back the exact step sequence that violates one.*

*Starts only after Claude's 042 implementation settles (the protocol must be stable enough to model).
This feature touches **no product code** in its core scope; it produces a `.tla` model, a TLC config,
a findings record, and — for any confirmed protocol defect — dated addenda back into 042/042-1.*

## Why TLA+, and why now

Highway's failover is a **novel, hand-rolled, no-elections protocol** (client-herd mastership,
priority-ordered succession, two-observer willingness, epoch tie-break, GOODBYE drain). It was designed
in conversation and refined across several iterations. Three facts make it the textbook case for formal
model-checking:

1. **It is bespoke, not a cited algorithm.** A Raft or Paxos implementation can lean on a peer-reviewed
   proof. Highway's herd-mastership protocol has no such proof — "we thought hard and wrote tests" is
   the entire assurance today, and that is exactly the situation where a subtle interleaving bug hides.
2. **The invariants are already written out in prose.** 042 R8.3 and 042-1 R1/R8/R11 enumerate the
   safety properties (no two masters with clients, no dual-writable, epoch monotonicity, no
   acked-and-replicated loss) and the partition matrix almost as a ready-made property list. Translating
   prose invariants into TLA+ is the cheap part; the value is TLC checking them against *every*
   interleaving.
3. **The existing tests sample; they do not exhaust.** The assurance rig and `HerdCohesionTests`
   simulate failure by killing a socket or overriding reachability at a *scripted* instant. They cannot
   cover "master froze without dropping TCP at the exact moment two willingness clocks fire," or "a
   stale island rejoins at an epoch boundary while a client is mid-converge." A model checker enumerates
   those; a soak hits them only by luck.

Industry precedent (recorded, not decorative): AWS applied TLA+ to S3, DynamoDB and EBS and reported
finding subtle design bugs that *"cannot be found through any other technique we know of"* — including a
DynamoDB fault-tolerance bug requiring a 35-step interleaving no human writes as a test. ZooKeeper,
MongoDB and Cosmos DB all maintain public TLA+ specs of their replication cores for the same reason.

## Expected value (the honest estimate)

For Highway's Tier-1 protocol specifically:

> **Expect TLC to surface on the order of 2–5 genuine safety/liveness defects** that the current
> soak + scripted-partition tests structurally cannot find — concentrated in the herd-convergence and
> epoch-fencing logic (the "two willingness clocks fire together," "island rejoins mid-promotion,"
> "client converges before the original write replicated," "partition timing at the `x`/`W`/`T_fence`
> boundary" class). These are the **highest-severity** bugs Highway can have — silent split-brain, a
> lost acked message, a split herd — and the hardest to reproduce from a field failure.

A "finding" is any TLC counterexample against a declared invariant. Each must be triaged: a real
protocol defect (fix the protocol + spec + code), a modeling error (fix the model), or an acceptable
behaviour the invariant was too strict about (refine the invariant, record why). **Zero findings is
also a valid, valuable outcome** — it upgrades the protocol's assurance from "tested" to "model-checked
over a bounded state space," which is a materially stronger claim to make in `constraints.md`.

## Scope

### In scope — Tier 1: the replication / herd protocol

The multi-actor, crash/partition-exposed state machine — the only part of Highway with true concurrent
distributed behaviour and hand-designed invariants:

1. **The node role/epoch state machine** — `Primary`, `Replica`, `Fenced`, `Demoted`; monotonic epoch;
   promote / fence / unfence / demote / observe-higher-epoch transitions
   (`src/Highway.Server/Storage/Rocks/ReplicationFeeder.cs`).
2. **Herd convergence + willingness** — the client-herd mastership model: clients converge on one node,
   the two-observer willingness rule (`Willingness.Decide`), herd-arrival promotion
   (`CommandDispatcher.Dispatch`), and the client-side walk/converge
   (`HighwayConnectionSource.TryFailoverAsync`).
3. **GOODBYE graceful drain** — `Primary → draining (quiesce master-only verbs) → Demoted`
   (`ReplicationFeeder.BeginGoodbye/TickGoodbye`).
4. **Retention/watermark** only to the extent it bears on the *no-acked-loss* invariant (the replicated
   watermark vs. the async RPO window) — not the full slot-cap/snapshot mechanics.

### Explicitly out of scope — Tier 2

The single-writer-per-lock, atomic-per-batch state machines whose aggregate safety the **assurance rig
already reconciles** (I1–I7):

- The queue/lease lifecycle (claim/ack/fail/sweep/dead-letter/promote).
- Pub/sub fan-out atomicity and group retirement.
- The node registry heartbeat lifecycle.

These are modelable but low-payoff: per-name locks + per-batch commit eliminate most interleavings by
construction, and the rig covers what remains. They may be modelled in a later feature if a Tier-1 bug
suggests a Tier-2 interaction, but they are **not** part of this feature's gate.

## Requirements

### Requirement 1: A TLA+ model of the Tier-1 protocol

**User Story:** As a maintainer of a bespoke failover protocol, I want its states, transitions and
invariants expressed in TLA+, so that a model checker — not just my judgement — decides whether the
protocol is correct.

#### Acceptance Criteria

1. A single TLA+ specification models the node role/epoch state machine (R-scope 1), the herd/client
   connection state and convergence (R-scope 2), and the GOODBYE drain (R-scope 3).
2. Time is modelled **logically, not by wall clock**: the `x ≤ W < T_fence` ordering (042-1 OD2) is
   expressed as a constraint on the *order* of timeout events, never as comparison of real clocks (the
   protocol itself has no absolute clock comparison — the model must not smuggle one in).
3. The model is parameterised by a small, bounded cluster: **2–4 nodes with configured priorities and
   N clients** (N small, e.g. 1–3). The config is stated so TLC's state space is finite and tractable.
4. Each modelled action maps to a named piece of the implementation (a comment on every action citing
   the `ReplicationFeeder` / `Willingness` / `CommandDispatcher` / `HighwayConnectionSource` member it
   represents) so a reviewer can check the model against the code.

### Requirement 2: The safety invariants, checked exhaustively

**User Story:** As a maintainer, I want TLC to prove the failover safety properties hold under every
interleaving in the bounded model, so that "no split-brain" is a checked fact, not a hope.

#### Acceptance Criteria — each is a TLA+ invariant TLC checks

1. **At most one master with clients** (042-1 R1.4): there is never a reachable state where two distinct
   nodes are both in the master role **and** both have ≥1 connected client.
2. **No dual-writable** (042 R5.1): two nodes are never both writable at the same logical instant; the
   `T_promote > T_fence + margin` ordering that guarantees this is encoded and its necessity is shown
   (remove it → TLC finds a dual-writable trace).
3. **Epoch monotonicity + higher-epoch-wins** (042 RD7 / 042-1 R8.4): epochs never decrease at a node;
   on observing a higher epoch a Primary/Fenced node demotes; a Replica adopts; ties never both act.
4. **No acked-and-replicated loss** (042 R8.1 / 042-1 R11.1): a message acked to a client **and**
   replicated (below the min acked watermark) is present on whatever node the herd converges to.
   (Loss *inside* the async RPO window is permitted and is asserted as bounded, not as zero — R4.)
5. **A herd-less node performs no master-only side effects** (042-1 R1.3 / R6): a node with zero clients
   never performs QACK / DLQ / reply-slot-write / job-fire.
6. **GOODBYE quiesce** (042-1 R12.2): while a node is draining, no *new* master-only work is admitted,
   and the drain always terminates (deadline or herd-left).

### Requirement 3: The liveness properties, checked

**User Story:** As a maintainer, I want TLC to prove the herd *always eventually converges*, so that
"the cluster heals" is checked, not assumed — this is the property tests are worst at.

#### Acceptance Criteria — temporal properties TLC checks under fair scheduling

1. **Herd convergence (no permanent split):** from any state where a master has failed or departed and
   at least one willing successor is reachable, the model *eventually* reaches a state where all
   connected clients are on the same single master.
2. **A willing successor eventually serves:** a reachable, willing, top-priority standby, once the herd
   arrives, eventually becomes the master (herd-arrival promotion completes).
3. **No livelock:** the model does not admit an infinite sequence of promote/demote/fence with no node
   ever stably serving the herd (e.g. two nodes never ping-pong mastership forever).

### Requirement 4: The RPO window is modelled honestly

**User Story:** As a maintainer, I want the async-replication loss window represented so the model's
"no loss" claim is precisely scoped, not overstated.

#### Acceptance Criteria

1. The model distinguishes **acked-and-replicated** (must survive — R2.4) from
   **acked-but-not-yet-replicated** (may be lost on hard master death — the RD8 RPO window).
2. TLC confirms loss is **confined** to that window (no reachable trace loses a message that was already
   below the min acked watermark) and is never unbounded or silent — matching 042 R9 / 042-1 R5.

### Requirement 5: The scenario matrix the model must cover

**User Story:** As a maintainer, I want the specific crash/partition/rejoin/timing scenarios the specs
name to be *reachable* in the model, so I know TLC actually exercised them and did not trivially pass.

#### Acceptance Criteria — each must be a reachable state/trace in the model

1. **Hard master crash** (no last words) → herd converges on the successor (042-1 R11.1).
2. **Master frozen but TCP-alive** → client health timeout `x` fires and the herd walks (042-1 R3.3).
3. **Partition matrix** (042 R8.3 / 042-1 R11.2): (a) master isolated from peers but not clients → keeps
   its herd; (b) master islanded from everyone → herd moves, island stands down on heal via epoch;
   (c) client-side reachability difference → "no master for me yet," never a second master.
4. **Rejoin without preemption** (042-1 R11.5): a higher-priority node returns while a lower serves →
   herd does not move.
5. **Priority collision** admission rule (042-1 R11.6) — modelled if it bears on succession
   determinism; otherwise recorded as out-of-model with a reason.
6. **Stale island rejoins at an epoch boundary** → resolved by higher-epoch-wins, no dual-writable.
7. **GOODBYE mid-traffic** → herd moves, near-zero replay, drain terminates (042-1 R11.4).

### Requirement 6: Findings are triaged and fed back

**User Story:** As a maintainer, I want every TLC counterexample resolved and any real protocol defect
corrected at the source, so the model earns its keep instead of producing an unread report.

#### Acceptance Criteria

1. Every invariant/property either **passes** on the bounded model or produces a **counterexample
   trace** that is triaged into one of: (a) real protocol defect, (b) modeling error, (c) invariant too
   strict.
2. A real protocol defect is corrected as a **dated addendum** to 042 and/or 042-1 (house rule — never
   rewrite the existing analysis) **and** a code fix is filed against the implementation, with the TLC
   trace attached as the reproducer.
3. The feature records, in a `FINDINGS.md` under this feature directory, each property, its verdict, and (for
   findings) the trace and its disposition.

### Requirement 7: The record

**User Story:** As a maintainer, I want the fact that the protocol was model-checked recorded where the
guarantees live, so the assurance level is legible.

#### Acceptance Criteria

1. `constraints.md` gains a dated note (extending the 042/042-1 failover constraints): the protocol's
   safety and liveness invariants were model-checked with TLA+/TLC over a bounded cluster; the config
   bound and the checked properties are named; any residual (unmodeled) risk is stated.
2. The `.tla` model + `.cfg` config live in a repo `formal/` directory (the design decides the exact
   path) and are runnable — a maintainer can re-run TLC after a protocol change.

## Non-Goals

- **Verifying the C# code.** The core feature proves the **protocol/design** is correct, not that
  `ReplicationFeeder.cs` faithfully implements it. Closing that gap is the optional **trace-validation
  stretch** (design + tasks), not a core deliverable.
- **Modeling Tier-2** (queue/pub-sub/registry) — the assurance rig covers their aggregate safety.
- **Unbounded verification.** TLC checks a *bounded* model (small node/client counts, bounded epochs
  and message sets); it does not prove correctness for arbitrary cluster sizes. The bound is stated and
  justified (distributed bugs almost always surface at 2–3 nodes; larger N rarely adds new bug shapes).
- **Replacing the assurance rig or `HerdCohesionTests`.** TLA+ complements them — it exhausts the
  interleavings they sample; they exercise the real code the model abstracts. Both stay.
- **A production runtime dependency.** TLA+/TLC is a development-time verification tool; nothing in the
  shipped broker depends on it.

## Cost and limits (stated plainly)

- **Effort:** roughly 1–3 weeks, most of it the TLA+ learning curve if no one on the team has used it.
  The precondition is ideal (invariants already enumerated, protocol at the right abstraction level), so
  the modelling itself is direct.
- **It checks the design, not the deployment.** A green TLC run means the protocol is sound under the
  modelled failures; it says nothing about a bug in the socket layer, a RocksDB durability edge, or a C#
  concurrency mistake below the protocol abstraction. The trace-validation stretch narrows this gap; the
  assurance rig and unit tests remain the code-level assurance.
- **State-space blowup is the practical risk.** Careless modelling explodes TLC's state count. The
  design constrains the model (small bounds, symmetry reduction, view abstractions) to keep runs
  tractable; this is called out as the main modelling risk.

## Dependencies and sequencing

- **Blocked on:** Claude's 042 implementation settling — the protocol must be stable enough that the
  model tracks shipped behaviour rather than a moving target. (042-1's herd model is the version to
  model, with RD6's deadman as the backstop it became.)
- **Reads:** `docs/features/042-replication/`, `docs/features/042-1-replication-improvements/`,
  `src/Highway.Server/Storage/Rocks/ReplicationFeeder.cs`, `Willingness.cs`,
  `src/Highway.Server/Resp/CommandDispatcher.cs`, `src/Highway.Client/Engine/HighwayConnectionSource.cs`.
- **Feeds back into:** 042 / 042-1 (dated addenda for confirmed defects) and `constraints.md` (R7).
