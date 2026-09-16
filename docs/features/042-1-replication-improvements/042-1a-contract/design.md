# Feature 042-1a — The Herd Contract: Design

*Settles the cross-cutting decisions of [042-1](../design.md) and pins the wire shapes. The
sub-features implement; this document decides. Where a decision here closes a parent OD, it
says so.*

## D1 — Willingness (the no-split hinge)

```
willing(standby) :=
      priority != 0
  ∧ ( now - lastMasterExchange >= W          // its own puller lost the master
      ∨ goodbyeReceived )                     // or the master promised to leave
```

- `lastMasterExchange` is the standby's own pull-loop success clock (`ReplicaPuller` already
  tracks contact; this reuses it).
- **W (willingness threshold)** default **3s**, config `Replication.WillingnessThreshold`,
  with the ordering `x (client health timeout, 3s) ≤ W (3s) < T_fence (5s)` validated at
  startup alongside the existing timeout triple (closes parent OD2's composition question and
  A-OD2; the equality x = W is fine — both sides observing the same silence is the point).
- An **unwilling** standby answers with a redirect: the master it still sees
  (`endpoint`, `epoch`) — the same information as `-NOTPRIMARY`, so the client walks on or
  goes where it is pointed.

Why this closes split-brain without a witness: a client alone cannot move mastership (the
standby's healthy master-link vetoes it); a standby alone cannot move it (no herd arrives
without client-side loss). Both observers must see the loss — the 042 witness, absorbed.

**Amendment 2026-09-16 (found by the herd-arrival integration test):** a standby that has
**never** reached its master is not one that *lost* it — during its startup window,
"not found yet" must not read as "dead", or a slow-starting standby promotes under a
living master. The predicate gains `hasSeenMaster` (set on the pump's first real
exchange): never-seen silence becomes willing only at the outer **`T_fence`** bound, so a
genuinely dead-master bootstrap still converges, just on the backstop's clock. GOODBYE
trumps never-seen (the master itself said go).

**Amendment 2026-09-16 (found by the multi-standby cohesion harness):** when several
standbys lose the master at once they must not all turn willing in the same instant, or
clients walking the roster can land on different successors (a split). The threshold gains
a **priority stagger**: a node adds `priority × StaggerPerPriority` (400ms/unit, capped at
priority 20) to its effective willing window, so the highest-priority (lowest-number)
successor turns willing first and the whole herd converges on it; a lower-priority node
turns willing only if the higher one never does. This is 042's deleted *promotion* stagger,
reincarnated in its correct home — the willingness gate, not a promote timer. A transient
split under adversarial timing still self-heals: the higher-priority node, once a client
reaches it, promotes to a **higher epoch** and the promotion broadcast (to every roster
peer) demotes the lower-epoch node, so the herd reconverges on the highest-priority master.

## D2 — The handshake carrier (closes A-OD1)

**Fold into the HELLO family**: `HW.REPL.HELLO` already carries identity/epoch/endpoint and is
served in every role. Add a client form:

```
HW.REPL.HELLO CLIENT <clientId> <lastSeenEpoch>
   → willing:   [+role master|willing, epoch, rosterVersion]
   → unwilling: [+role standby, masterEndpoint, masterEpoch]
```

Arity stays `-4`-compatible (design detail: the literal `CLIENT` disambiguates from the
replica form's identifier — spelled exactly in the protocol doc). No new command name; the
conformance surface grows one form, not one entry.

## D3 — Promotion by herd arrival

State on the server: `HerdState { connectedClients: int, lastWillingHandshakeAt }` maintained
by the RESP session layer (authenticated client sessions only; replica pullers and probes do
not count — they identify via the `HW.REPL.*` preamble).

Transition: on the **first non-`HW.REPL.*` client verb** after answering willing, the node
runs 042's `TryPromote("herd-arrival")` — epoch++ (persisted), promote announcement to the
old master (reused, unchanged), transition logged. Probes, STATUS reads, and handshakes never
promote.

## D4 — Narration (closes parent OD1)

Channel: **`hw:door:topology`** on the existing doorbell surface (`SubscriptionRegistry`
publish — the machinery 040 built; clients already hold a subscribed connection). Messages are
single-line, space-delimited, versioned by roster version:

```
TOPOLOGY <rosterVersion>                      # advisory: re-read the roster, re-run your rule
GOODBYE <epoch>                               # I am leaving now; converge (no successor named)
ROSTER-UPDATE <rosterVersion> <nodeId> <priority> <endpoint> JOINED|LEFT
```

All advisory (parent R7.2). Delivery is doorbell-lossy by design; correctness never depends on
narration — the TCP-drop walk and `-NOTPRIMARY` teeth remain the guarantee.

## D5 — The roster lives in the store (closes A-OD4 shape)

- Key: KV family, name `repl:roster` (`k repl:roster` under the 038 encoding).
- Value: versioned record — `version u64`, then members as `(nodeId, priority, endpoint)`.
- **Only the master writes it** (join admission, departure), through the ordinary batch path —
  so it WAL-ships to every standby for free, and a promoted node reads its own store for the
  current roster (parent R13.6 by construction).
- Clients read it via **`HW.REPL.STATUS`** gaining `roster.*` fields (closes A-OD3: no new
  command; STATUS is already the client-probe surface from 042 G6).
- Join-announce: `HW.REPL.JOIN <nodeId> <priority> <endpoint>` → `:rosterVersion` (integer —
  amended 2026-09-15 from the `+OK <v>` sketch; an integer is the machine-readable version) or
  `ERR HW_PRIORITY_TAKEN priority=<p> holder=<nodeId>` (permanent class, parent R13.3).

## D6 — What the deadman becomes

- **Fence half kept** as the backstop (former master with no herd and no peer contact moves to
  read-only) — unchanged constants, unchanged validation.
- **Promote half deleted**: no timer promotes anybody. `PromoteTimeout` remains in config for
  the validation triple but the standby-side self-promotion path, the witness loops, the
  witness peer-question (`WITNESS <id> <role>`), and the priority stagger are removed in
  042-1c. `HW.REPL.WITNESS` reverts to the bare liveness probe (`+OK`) for compatibility.

## D7 — The reconciliation map (A-R1)

Written as `reconciliation-map.md` beside this file when A-T1 executes, with file:line
references into the tree at that commit. Summary table to be produced there:

| Disposition | Code |
|---|---|
| Reuse unchanged | `ReplicationFeeder` (pull/slots/snapshot/gap/epoch/reconciliation), `ReplicaPuller` (pump, snapshot bootstrap, resync marker), `BatchApplier`, `ReplicationApply`, `WalTailSummary`, `HighwayReplicationOptions` (minus witness/stagger), dispatcher `-NOTPRIMARY` gate + `RedirectEndpoint`, RespSession raw-write gate, promote announcement |
| Change | client failover walk (`HighwayConnectionSource.TryFailoverAsync` → roster-ordered, willingness-aware), promotion trigger (`TickDeadman` promote branch → herd arrival) |
| Delete | `WitnessLoopAsync`, `WitnessObserve`/`ReportWitnessAnswer`, witness-gated `WitnessAllowsPromotion`, `PromoteStaggerPerPriority`, `WITNESS <id> <role>` form |

## Testing strategy

| Proof | Requirement |
|---|---|
| Protocol doc round-trip: conformance sees the new forms; changelog entry present | A-R7 |
| Willingness truth-table unit-tested against the predicate (healthy link → unwilling+redirect; lost link → willing; priority 0 → never) | A-R2 |
| Handshake shape golden test (both replies) at the command layer | A-R2/D2 |
| Roster record encode/decode round-trip; only-master-writes asserted | A-R5/D5 |
| Startup validation: `x ≤ W < T_fence` refused when violated | A-R2.3 |
