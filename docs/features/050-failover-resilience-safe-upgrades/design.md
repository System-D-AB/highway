# Feature 050 — Design

*This is the design record. Where a decision is genuinely open, it is flagged and carries a
recommendation; the requirements' Open Decisions (OD1–OD3) are resolved here as the spike lands.*

## The mechanisms we already have (what to build on)

- `ReplicationFeeder.TryPromote` (herd-arrival / explicit) bumps the epoch, sets `Role = Primary`,
  and **announces** to the configured primary and roster peers via `AnnouncePromotionAsync`
  (`HW.REPL.HELLO … SelfEndpoint`). It does **not** touch the roster — the F1 gap.
- `ReplicationFeeder.ObserveHigherEpoch` demotes a primary/fenced node to `Demoted`, writes a
  **reconciliation report** (`WriteReconciliationReport`), persists the epoch, and records
  `_knownPrimaryEndpoint` from the announce/redirect. So a demoted node **already knows where the
  new primary is** — it just does nothing with it (the F2 gap).
- `ReplicaPuller` runs the replica loop (HELLO → PULL → apply → ACK) and, on a WAL gap, writes a
  **resync marker** and re-bootstraps via snapshot on the next `RocksDbStore.Open` (the one
  bootstrap code path). It is created at startup only when `PrimaryServer` is configured.
- `RosterStore.TryUpsert` writes a member into the replicated `repl:roster` KV; `RespServer` calls
  it at startup for a node that *starts* writable.

So the parts exist; F1/F2/F3 are about wiring them to the runtime transitions instead of only to
startup + config.

## R1 — Promoted node self-registers in the roster

On `TryPromote` success, upsert `self` (`ReplicaId`, `Priority`, `SelfEndpoint`) into the roster —
the same `RosterStore.TryUpsert` the startup path uses. Because the roster is a replicated KV, this
ships to every standby. Wiring: the feeder cannot call the higher-level `RosterStore` cleanly from
inside `Storage.Rocks`, so expose it as a hook the way `Narrator`/`OnEpochChanged` already are — a
`Func`/`Action` the `RespServer` sets that performs the roster upsert. Fire it after
`AnnouncePromotionAsync`. Idempotent by construction (`TryUpsert` is an upsert on the node id).

This alone fixes the blank succession view and restores the brokers' priority map after a failover.

## R2 — Auto-rejoin of a demoted ex-primary (the hard part)

The demoted node has `_knownPrimaryEndpoint`; it must become a replica of it. The difficulty is the
**data divergence**: the ex-primary may hold acked-but-unreplicated writes past the failover
watermark, and rejoining means yielding to the new primary's authoritative state. The reconciliation
report already captures the diverged tail; rejoin must re-sync the store from the new primary.

### OD1 resolution — restart-to-rejoin (recommended for v1), runtime later

Wiping and re-bootstrapping a **live, open** RocksDB in-process is risky (open handles, the WAL, the
cache DB, in-flight readers). The existing, proven re-sync path runs at `RocksDbStore.Open` — i.e.,
across a process (re)start, where the directory is quiescent. So v1 rejoin reuses it:

1. On demotion with a known new primary and auto-rejoin enabled, the feeder writes a **rejoin
   marker** (a superset of today's resync marker) into the data dir: the new primary's endpoint, the
   demote watermark, the observed epoch, and a pointer to the reconciliation report.
2. The node then **re-bootstraps against that primary** by going through Open again. Two ways to get
   there, and v1 supports both:
   - **Supervised restart (default):** the broker requests a graceful stop; the Windows service /
     systemd unit (or `run.bat` loop) restarts it. On Open, `RocksDbStore` sees the rejoin marker,
     wipes, downloads a snapshot from the marker's primary endpoint, and the node comes up as a
     `Replica` following the new primary. This is the safest path and reuses the whole existing
     bootstrap machinery; it costs one bounce.
   - **In-process re-sync (opt-in, `Replication.RuntimeRejoin`):** the feeder closes the store,
     runs the same wipe+bootstrap in place, and reopens — no bounce, but it exercises a live store
     teardown and must be gated behind a flag until it has soak time.
3. Guards (R2.4): a node re-syncs at most once per observed epoch (the marker records the epoch — a
   second demote at the same epoch is a no-op); a node never re-syncs from a peer whose epoch is not
   strictly higher; and if the new primary is unreachable, the node holds in `Demoted` with a loud,
   repeated log line rather than looping.

The demoted node needs a `ReplicaPuller` even though it had no configured `PrimaryServer`: after a
successful rejoin bootstrap, start a puller against the **marker's** primary endpoint (carry the
credentials/TLS tail from the announce or the operator's shared secret). This is the one new piece —
a puller sourced from the learned endpoint, not the static config.

**Data-loss framing (R2.2):** rejoin is the herd-wins contract made operational. The demoted node
yields; its diverged tail lives in the reconciliation report for manual replay if the business needs
it. This must be documented as the accepted RPO cost — the same window 042/C9 already names — not
hidden.

### OD2 — symmetric config (the root cure), scoped as a follow-on

The cleanest cure for F2 is to stop encoding role in config. A node would be a **member**: an id, a
priority, and the set of **peer endpoints**; whoever holds the herd is primary, everyone else follows
it, and `PrimaryServer`/`StartAsReplica` collapse into "follow whoever the roster/herd says is
primary." Then a demoted node already knows every peer and rejoins with no learned-endpoint special
case. This is a larger change (config schema, bootstrap, the puller's source-of-truth) and is
recommended as **050-b**, after 050-a (R1 + R2 restart-to-rejoin + R3) ships the urgent fix. 050
documents the direction so the interim marker-based rejoin is built as a stepping stone, not a dead
end.

## R3 — Slot aging on role change

- On demotion (`ObserveHigherEpoch` → `Demoted`), **clear `_slots`** — a node that is no longer
  primary holds no replicas; the phantom "Active" row goes at once.
- Add **time-based staleness** to slots: a slot whose `AckedSeq` has not advanced and whose last
  contact is older than a bound (e.g. `N × NodeExpiry`) is marked `Dropped`, so even on a live
  primary a silent replica stops reading as healthy. `EnforceCap` already drops on lag; add the
  last-contact dimension (the feeder records `NotePeerContact`; extend to per-slot last-contact).

## R4 — Safe rolling upgrade

- **Runbook** (`docs/product/` or the feature dir, and referenced from the distribution README):
  the ordered procedure — replica(s) first; `HW.REPL.GOODBYE` on the primary to hand off gracefully
  (drain + succession, zero loss) **before** swapping its binaries; the old primary rejoins (R2) as
  a replica; optionally GOODBYE again to fail back if a preferred primary is wanted.
- **Tooling (R4.3):** `--goodbye` exists. Add an **`--drain-and-stop`** (a.k.a. upgrade mode): one
  command that runs GOODBYE, waits for the drain to complete, then stops the process cleanly, so the
  operator's sequence is "`--drain-and-stop` → swap bin → start" with the failover made *deliberate*
  and lossless. Decide in build whether this is 050-a or the runbook-only interim.
- **Assurance scenario (R4.4):** extend the herd rig with a "rolling upgrade of a two-node set" run —
  GOODBYE-driven handoff, swap (simulated), rejoin — asserting zero client loss (I1–I7 clean) and a
  healthy attached pair at the end. This is the proof the safe path works, in CI.

## R5 — Client requirements: explicit and warned

- **Docs:** state the rule in the client guide and the distribution README — a replica-set client
  lists **every** endpoint and must reach **every** one; one endpoint = no failover.
- **Startup warning (R5.2):** on connect, the client can read `HW.REPL.STATUS`; if the broker
  reports a roster/replica set larger than one but the client's connection string names a single
  endpoint, emit a `WARN` naming the missing endpoints. This catches the exact footgun from the
  incident before it bites. (The client already handshakes; the roster is already on the wire.)
- **Actionable redirect failure (R5.3):** when a `-NOTPRIMARY` points at an endpoint the client
  cannot reach, log "redirected to X (epoch N) which is unreachable — add it to the connection
  string and open the network path," not a bare `HighwayTransportException`.

## R6 — Make the degraded states loud

- **Logging:** extend 047's replication logging with rejoin milestones ("demoted; rejoining as a
  replica of X", "rejoin bootstrap complete, following X"). Promotion/demotion already log.
- **Dashboard health banner (R6.2):** on the Replication tab, show a prominent warning when the
  local node is **Primary with 0 attached replicas but a roster that expects standbys** ("No
  redundancy: this primary has no live standby"), or is **Demoted and not yet rejoined** ("This node
  is demoted and not following a primary"). These are the two "HA is gone right now" states; they
  must not require the operator to infer them from a stat tile.

## Testing strategy

| Layer | Proof | Req |
|---|---|---|
| Roster | promote registers self; `HW.REPL.STATUS` roster non-empty after a failover | R1 |
| Rejoin (unit) | demotion writes the rejoin marker (new primary, epoch, watermark) + reconciliation | R2 |
| Rejoin (integration/herd rig) | after an ungraceful primary loss, the ex-primary rejoins as a live replica of the successor; guards prevent re-sync loops and equal-epoch re-syncs | R2 |
| Slots | demotion clears slots; a silent slot ages to Dropped | R3 |
| Upgrade | herd-rig rolling-upgrade run is zero-loss and ends with a healthy attached pair | R4 |
| Client | a one-endpoint client against a >1 roster warns at startup | R5 |
| Observability | rejoin logs; the dashboard shows the no-redundancy banner in both degraded states | R6 |

## Decomposition

- **050-a (urgent):** R1 (roster self-register), R2 restart-to-rejoin with the reconciliation
  safety net, R3 (slot aging), R6 (logging + dashboard banner), R5 docs + the client startup
  warning, R4 runbook + `--drain-and-stop`. This closes the production hazard.
- **050-b (follow-on):** OD2 — the symmetric member/peer config model that dissolves F2 at the root,
  and R2's optional in-process runtime rejoin. Larger, sequenced after 050-a soaks.
