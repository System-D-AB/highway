# Feature 043 — Formal Model: Design

*Implements [`requirements.md`](requirements.md). Designs the TLA+ specification of Highway's Tier-1
replication/herd failover protocol and the TLC model-checking setup. The model abstracts the protocol
in [042](../042-replication/) + [042-1](../042-1-replication-improvements/);
every action cites the implementation member it represents so a reviewer can check model against code.*

## Modelling philosophy

**Model the protocol, abstract everything else.** TLC's job is to explore *control-flow interleavings
under failure* — who acts, in what order, with which messages lost — not to reproduce Highway's bytes.
So the model keeps: node roles, epochs, the client→node connection map, willingness state, and a tiny
abstract message log (just enough to state "no acked-and-replicated loss"). It discards: RESP framing,
RocksDB, payloads, the reply-slot bytes, real timers. A message is an opaque id; a WAL is a sequence of
ids; "replicated" is "id ≤ a node's watermark." This keeps the state space finite and the invariants
legible.

**No wall clocks.** The protocol itself makes no absolute-clock comparison (042-1 D1, `Willingness`),
and the model must not either. Time is **logical** — see §"Modelling time without clocks."

## State variables

```tla
VARIABLES
    role,          \* [Node -> {"Primary","Replica","Fenced","Demoted","Down"}]  (ReplicationFeeder.Role + crash)
    epoch,         \* [Node -> Nat]   monotonic per node                          (ReplicationFeeder.Epoch, repl-epoch.txt)
    watermark,     \* [Node -> Nat]   highest replicated seq this node holds       (BatchApplier.Watermark / MinAckedWatermark)
    conn,          \* [Client -> Node \union {NONE}]   who each client is on       (the herd; HighwayConnectionSource.ActiveServer)
    silence,       \* [Node -> Nat]   logical "time since last peer contact"       (ReplicationFeeder.LastPeerContact age)
    seenMaster,    \* [Node -> BOOLEAN]  has this standby ever reached a master     (ReplicationFeeder.HasSeenMaster)
    goodbye,       \* [Node -> BOOLEAN]  master announced departure                 (ReplicationFeeder.GoodbyeSeen / IsDraining)
    link,          \* [Node -> [Node -> BOOLEAN]] symmetric reachability            (the partition; test EndpointReachableOverride)
    clientLink,    \* [Client -> [Node -> BOOLEAN]] client↔node reachability        (client-side partition, R11.2c)
    log,           \* [Node -> Seq(Msg)]  the abstract per-node message history      (WAL tail; for the loss invariant)
    acked          \* SUBSET Msg      messages the cluster has acked to a client     (the "must not lose if replicated" set)
```

Derived (definitions, not variables):

```tla
Writable(n)     == role[n] = "Primary" /\ ~goodbye[n]                 \* CommandDispatcher.IsWritable
Herd(n)         == { c \in Client : conn[c] = n }                     \* connected clients of n
IsMasterWithClients(n) == Writable(n) /\ Herd(n) # {}                 \* the thing R2.1 forbids two of
Willing(n)      == /\ role[n] = "Replica" /\ Priority(n) # 0          \* Willingness.Decide
                  /\ (goodbye[MasterOf(n)] \/ silence[n] >= (IF seenMaster[n] THEN W ELSE FenceT))
Replicated(m,n) == \E i \in DOMAIN log[n] : log[n][i] = m /\ i <= watermark[n]
```

## Modelling time without clocks

The protocol's only timing content is an **ordering** of timeout thresholds: `x ≤ W < T_fence < T_promote`
(042-1 OD2 + 042 OD1). We model these as **abstract nat constants with the ordering asserted**, and
"time" as a monotonic per-node `silence` counter that a `Tick` action may advance nondeterministically.
A node's willingness/fence decision compares `silence[n]` to a threshold constant — never to another
node's clock. This captures exactly what the code does (each node reasons about *its own* silence) and
lets TLC explore every relative ordering of "which node's threshold trips first" without a global clock.

The key modelled invariant of the timing design — **a standby cannot become willing before the primary
would have fenced** — falls out of `W < T_fence` plus the fact that both read the *same* `silence`
semantics. TLC checks that removing the ordering (`W >= T_fence`) produces a dual-writable trace (R2.2),
which is the proof the constant ordering is load-bearing, not decorative.

## Actions (the transition relation)

Each action is a TLA+ `Next` disjunct. Every one cites its implementation counterpart.

| Action | Meaning | Implementation |
|---|---|---|
| `Tick(n)` | advance `silence[n]` (logical time passes for n) | the deadman/health clocks |
| `PeerContact(n,m)` | n hears m over a live link → `silence[n] := 0`, `seenMaster` | `ReplicationFeeder.NotePeerContact` |
| `HerdArrivalPromote(n)` | a `Willing` n gets a client verb → epoch++, role:=Primary | `CommandDispatcher.Dispatch` herd-arrival branch + `TryPromote` |
| `Fence(n)` | Primary, no client & no peer past FenceT → Fenced | `ReplicationFeeder.TickDeadman` |
| `Unfence(n)` | Fenced regains contact → Primary | `UnfenceIfContact` |
| `ObserveHigherEpoch(n,e,src)` | n sees epoch e>epoch[n]: Primary/Fenced→Demoted(+report); Replica→adopt | `ObserveHigherEpoch` |
| `BeginGoodbye(n)` | Primary starts drain: goodbye:=TRUE, quiesce new master-only work | `BeginGoodbye` |
| `CompleteGoodbye(n)` | drain deadline or herd-left → Demoted | `TickGoodbye` |
| `ClientConnect(c,n)` | client c walks to reachable willing/primary n → conn[c]:=n | `HighwayConnectionSource.TryFailoverAsync/HandshakeAsync` |
| `ClientDrop(c)` | c's link to its node lost → conn[c]:=NONE (then re-walks) | TCP drop → client walk |
| `ClientReplay(c,n)` | c re-drives its unacked msg to its new node n | `PendingCallRegistry.ReplayPendingAsync` |
| `Replicate(m,n)` | standby n pulls a batch → append to log[n], advance watermark | `ReplicaPuller` / `BatchApplier.ApplyPage` |
| `Ack(m,n)` | primary n acks m to a client → acked := acked ∪ {m} | the QSEND/ack path |
| `Crash(n)` | hard failure → role:=Down, in-flight (unreplicated) log tail lost | process kill |
| `Recover(n)` | a Down node restarts as Replica, seenMaster:=FALSE | node restart |
| `Partition(n,m)` / `Heal(n,m)` | flip `link[n][m]` | the partition matrix |
| `ClientPartition(c,n)` / `ClientHeal(c,n)` | flip `clientLink[c][n]` | R11.2c client reachability |

`HerdArrivalPromote` is the heart: it fires **only** when `Willing(n)` (the no-split guard) and a client
is trying to send to n. This is where "the herd connecting IS the promotion" (042-1) lives, and where a
protocol bug would most likely hide — so it gets the most careful modelling and the tightest guards.

## The invariants, formalized

```tla
\* R2.1 — at most one master WITH clients (the central safety property)
AtMostOneMasterWithClients ==
    \A n1, n2 \in Node : (IsMasterWithClients(n1) /\ IsMasterWithClients(n2)) => n1 = n2

\* R2.2 — no dual-writable
NoDualWritable ==
    \A n1, n2 \in Node : (Writable(n1) /\ Writable(n2)) => n1 = n2

\* R2.3 — epoch monotonicity (checked as an action property: epoch[n] never decreases)
\* + higher-epoch-wins: a Primary/Fenced that has seen a higher epoch is not still Writable
EpochSafety ==
    \A n \in Node : (role[n] \in {"Primary","Fenced"}) => (\A m \in Node : epoch[m] <= epoch[n] \/ ~Writable(n))

\* R2.4 — no acked-and-replicated loss: an acked, replicated msg is on the herd's master
NoAckedReplicatedLoss ==
    \A m \in acked :
        (\E n \in Node : Replicated(m, n) /\ role[n] # "Down")
        => (\A master \in Node : IsMasterWithClients(master) => Replicated(m, master))

\* R2.5 — a herd-less node performs no master-only side effect
\* (modelled as: MasterOnlyAction(n) is enabled only when Herd(n) # {} /\ Writable(n))

\* R2.6 — GOODBYE quiesce: while draining, no new master-only work admitted (guard on the action)
```

Liveness (temporal, under weak fairness on the convergence actions):

```tla
\* R3.1 — the herd eventually converges on one master (no permanent split)
HerdConverges == <>[] (\E n \in Node : \A c \in Client : conn[c] \in {n, NONE})
                                 \* eventually-always: all connected clients share one node

\* R3.2 — a willing successor eventually serves
WillingEventuallyServes == \A n \in Node : []( (Willing(n) /\ \E c \in Client : ClientWants(c,n)) => <> (role[n] = "Primary") )

\* R3.3 — no livelock (no infinite promote/demote with nobody stably serving)
NoLivelock == <>[] (\E n \in Node : IsMasterWithClients(n))
```

TLC checks safety invariants on every reachable state and temporal properties under fairness
constraints (weak fairness on `Tick`, `ClientConnect`, `HerdArrivalPromote`, `Heal`) so liveness is
checked against *progress*, not against an adversary who simply never lets anything happen.

## Bounded configuration

The model config (`.cfg`) fixes small constants so TLC's state space is finite:

- **Nodes:** start with **2**, then **3** (the size at which almost all distributed bugs surface),
  optionally **4** if runs stay tractable. Priorities distinct (e.g. `1,2,3`), with one `0`
  (never-promote) variant run.
- **Clients:** **2** (enough to express "herd split" and "clients disagree on reachability"), a **1**
  variant for the base case.
- **Epochs / messages:** bounded (e.g. epoch ≤ 4, |Msg| ≤ 3) — enough to reach every scenario in R5
  without unbounded growth. A `StateConstraint` caps epoch/log growth so TLC terminates.
- **Symmetry:** clients are symmetric; declare a TLC symmetry set over `Client` to cut the state space.

Justification (per requirements Non-Goals): distributed protocol bugs are overwhelmingly 2–3-node,
few-message phenomena; a bound this size is where TLA+ historically finds the DynamoDB/ZooKeeper-class
bugs. Larger N rarely adds new *shapes* of bug, only more instances.

## Reachability of the scenario matrix (R5)

The model must be able to *reach* each named scenario, else a green run is vacuous. Design ensures:

- **Hard crash** → `Crash(master)` then `ClientDrop`+`ClientConnect` walk. (R5.1)
- **Frozen-but-TCP-alive** → `Tick(master)` advances `silence` past `x` with no `Crash` and links intact;
  clients still walk on health timeout. (R5.2)
- **Partition matrix** → `Partition`/`ClientPartition` flips: (a) peer link down, client links up →
  master keeps herd, `Fence` disabled by `Herd(master) # {}`; (b) all links to master down → islanded,
  `Herd(master)` empties, successor promotes, island `ObserveHigherEpoch` on `Heal`; (c) `clientLink`
  asymmetry → a client cannot mint a second master because `Willing` requires the *standby's* dead link,
  not the client's belief. (R5.3)
- **Rejoin without preemption** → `Recover(highPriorityNode)` while a lower Primary has a herd → no
  action promotes it (herd is not on it; it is `Replica`). (R5.4)
- **Stale island rejoins at epoch boundary** → `Heal` after the island missed an epoch bump →
  `ObserveHigherEpoch` demotes it; `NoDualWritable` holds across the window. (R5.6)
- **GOODBYE mid-traffic** → `BeginGoodbye` with clients present and unreplicated `log` tail →
  `CompleteGoodbye`, herd re-walks. (R5.7)

A TLC "coverage" run (or explicit `\E`-reachability lemmas run as negated invariants that *should* fail)
confirms each scenario state is reachable — so a passing safety run is known to have exercised them.

## Mapping model ↔ implementation

| Model element | Implementation | Notes |
|---|---|---|
| `role`, `epoch`, `Fence/Unfence/Promote/Demote/ObserveHigherEpoch` | `ReplicationFeeder.cs` | the role/epoch state machine 1:1 |
| `Willing(n)` | `Willingness.Decide` + `IsWillingForHerd` | the two-observer no-split rule; `seenMaster` refinement modelled |
| `HerdArrivalPromote` | `CommandDispatcher.Dispatch` (herd-arrival branch) | "the verb arriving IS the promotion" |
| `Writable`, master-only gate, GOODBYE quiesce set | `CommandDispatcher` `IsWritable`/`AllowedWhenNotWritable`/`AllowedWhileDraining` | R2.5/R2.6 guards |
| `conn`, `ClientConnect/Drop/Replay` | `HighwayConnectionSource.TryFailoverAsync`, `PendingCallRegistry.ReplayPendingAsync` | the herd + in-flight replay |
| `watermark`, `Replicate` | `BatchApplier` / `ReplicaPuller` / `MinAckedWatermark` | abstracted to seq ≤ watermark |
| `silence`, `Tick`, `FenceT`, `W` | `LastPeerContact`, `TickDeadman`, `FenceTimeout`, `WillingnessThreshold` | logical time |

This table is the review artifact: a maintainer changing `ReplicationFeeder` checks whether the
corresponding action still matches.

## Trace validation — the optional stretch (non-goal for the core)

The core feature verifies the **protocol**. To narrow the "does the code match the protocol" gap, the
stretch instruments the C# implementation to emit a **trace** of specification-variable updates (role,
epoch, conn, watermark) at each transition, and uses the published **TLA+ trace-validation** technique
(TLC constrained model-checking against a recorded trace — arXiv 2404.16075) to confirm a real
execution is a behaviour the spec allows. Scope note: this is a meaningful extra effort (instrumentation
+ trace plumbing) and is explicitly *optional* — it is filed as a stretch task, run only if the core
model lands with time to spare. It does not gate the feature.

## Tooling

- **TLA+ / TLC** (the model checker), via the TLA+ Toolbox or the `tla2tools.jar` CLI so runs are
  scriptable/CI-able.
- **Community Modules** for `Sequences`, `Bags`, and (if used) fairness helpers.
- Model + config live under `formal/replication/` (repo-level, so a maintainer re-runs TLC after a
  protocol change) with a `run.ps1` invoking `tla2tools.jar`. The design prefers a repo `formal/`
  directory over burying the `.tla` in the feature folder, since it is a living artifact re-run on
  protocol changes; the feature folder holds the findings and the write-up.

## Risks and limits

| Risk | Mitigation |
|---|---|
| **State-space blowup** — the #1 practical failure of a TLA+ effort | Small bounds (2–3 nodes, 2 clients, epoch≤4, |Msg|≤3); `StateConstraint` caps growth; symmetry set over clients; abstract messages to opaque ids. Grow bounds only if runs stay fast. |
| Model drifts from shipped code (verifies a protocol nobody runs) | Every action cites its impl member (mapping table); feature blocked until 042 settles; a protocol change reopens the model. Trace-validation stretch closes it further. |
| Modelling error masquerades as a "proof" (false green) | R5 reachability check — confirm each scenario state is actually reached, so a green run is known non-vacuous; negated-invariant lemmas prove the interesting states exist. |
| Invariant too strict → noisy false counterexamples | Triage protocol (R6): each counterexample is defect / model-error / invariant-too-strict, recorded in FINDINGS.md. |
| Liveness needs fairness; wrong fairness hides or invents bugs | Fairness stated explicitly (weak fairness on progress actions only, never on `Crash`/`Partition`); documented so a reviewer can judge it. |
| Effort underestimated (TLA+ learning curve) | Sequence tasks safety-first (T2) — the highest-value invariants (no dual-writable, epoch) land before liveness/GOODBYE; the feature delivers value even if it stops after safety. |
| Read as replacing the rig/tests | Non-goal stated; TLA+ complements — exhausts interleavings the rig samples; the rig + `HerdCohesionTests` remain the code-level assurance. |
