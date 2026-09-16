# Feature 050 — Tasks

*Spec only — nothing here is implemented yet. This is the record of the work the field incident
demands, decomposed so the urgent hazard (050-a) can ship before the deeper config change (050-b).
Present to the owner and pick the order before writing code.*

```
050-a  (closes the production hazard — a primary restart no longer silently kills HA)
  T1  roster: self-register on promote
  T2  rejoin: demotion writes a rejoin marker + reconciliation; Open re-bootstraps from it
  T3  rejoin: a demoted node starts a puller against the learned primary (restart-to-rejoin)
  T4  slots: clear on demotion + time-based staleness
  T5  observability: rejoin logging + dashboard "no redundancy" banner
  T6  client: startup warning when roster > 1 but the connection string names one endpoint
  T7  upgrade: --drain-and-stop verb + the rolling-upgrade runbook + dist README pointer
  T8  assurance: a rolling-upgrade herd-rig scenario, zero-loss, healthy pair at the end

050-b  (the root cure — sequenced after 050-a soaks)
  T9  symmetric member/peer config model (dissolves the asymmetric-config stranding at the root)
  T10 optional in-process runtime rejoin (no bounce), gated behind a flag
```

## 050-a — close the hazard

### - [ ] T1 — Roster self-register on promote (F1 / R1)

Expose a promote hook from `ReplicationFeeder` (like `Narrator`/`OnEpochChanged`) that `RespServer`
wires to `RosterStore.TryUpsert(self)`; fire it in `TryPromote` after the announce. Idempotent.
**Done when:** after a herd-arrival promotion, `HW.REPL.STATUS` on the new primary lists a non-empty
roster including itself; the dashboard succession view is populated; re-promotion doesn't duplicate.

### - [ ] T2 — Rejoin marker + one bootstrap path (F2 / R2.1, R2.2, R2.4)

On demotion with a known new-primary endpoint and auto-rejoin enabled, write a **rejoin marker**
(new-primary endpoint, observed epoch, demote watermark, reconciliation path) alongside the existing
reconciliation report. Extend `RocksDbStore.Open` to honour the marker: wipe + snapshot-bootstrap
from the marker's primary (reusing the resync path). Guard: at most one re-sync per observed epoch;
never from a non-higher epoch.
**Done when:** unit — demotion writes the marker with the right fields; Open with the marker
re-bootstraps from the named primary; equal-epoch/duplicate demotion is a no-op.

### - [ ] T3 — Demoted node follows the learned primary (F2 / R2.3, R2.5)

After a rejoin bootstrap, start a `ReplicaPuller` against the marker's primary endpoint (endpoint
learned at runtime, not from static `PrimaryServer`), carrying the shared secret / TLS tail. The
node comes up `Replica`, attaches as a live slot on the new primary, and does **not** auto-failback.
**Done when:** integration/herd-rig — after an ungraceful primary loss, the ex-primary rejoins as a
live replica (lag → 0) with no operator step; it stays a replica until a deliberate GOODBYE.

### - [ ] T4 — Slot aging (F3 / R3)

Clear `_slots` on demotion. Add per-slot last-contact and mark a slot `Dropped` when it has not
acked within `N × NodeExpiry`, so a silent replica stops reading as `Active` on a live primary too.
**Done when:** unit — demotion empties the slot list; a slot with no ack past the bound ages to
Dropped; the dashboard stops showing the phantom.

### - [ ] T5 — Make it loud (F6 / R6)

Rejoin logging milestones (extends 047). Dashboard health banner on the Replication tab for the two
"HA is gone now" states: Primary-with-zero-standbys-but-roster-expects-them, and
Demoted-not-yet-rejoined.
**Done when:** the log narrates a rejoin; the banner shows in both degraded states and clears when
healthy.

### - [ ] T6 — Client footgun warning (F5 / R5.2)

On connect, if the broker reports a roster/replica set > 1 and the client's connection string names
a single endpoint, emit a `WARN` naming the endpoints it is missing. Docs (client guide + dist
README) state the "list and reach every endpoint" rule; the `-NOTPRIMARY`-to-unreachable failure
logs an actionable message (R5.3).
**Done when:** a one-endpoint client against a multi-node set warns at startup; the docs carry the
rule.

### - [ ] T7 — Safe-upgrade tooling + runbook (F4 / R4.1–R4.3)

Add `--drain-and-stop` (GOODBYE → await drain → clean stop) so a deliberate, lossless primary
handoff-then-restart is one command. Write the rolling-upgrade runbook; the distribution README
references it.
**Done when:** `--drain-and-stop` hands off and stops with zero loss; the runbook ships with the
distribution.

### - [ ] T8 — Prove it in the assurance rig (R4.4)

A herd-rig scenario: rolling upgrade of a two-node set (replica first; GOODBYE handoff; swap; rejoin)
asserting I1–I7 clean (zero client loss) and a healthy attached pair afterwards.
**Done when:** the scenario passes in CI and is recorded in `assurance/RUNLOG.md`.

## 050-b — the root cure (follow-on)

### - [ ] T9 — Symmetric member/peer config (OD2 / R2 root)

Replace static `StartAsReplica`/`PrimaryServer` with a member model: id, priority, peer endpoints;
role decided at runtime by who holds the herd. A demoted node already knows every peer, so rejoin
needs no learned-endpoint special case. Migration path from the current schema.
**Done when:** a two-node set configured symmetrically survives repeated failovers with no manual
reconfiguration and no marker special-casing.

### - [ ] T10 — Optional in-process runtime rejoin (OD1)

Behind `Replication.RuntimeRejoin`, re-sync the demoted store in place (close + wipe + bootstrap +
reopen) so rejoin needs no bounce. Gated until it has soak time.
**Done when:** with the flag on, a demoted node rejoins without a process restart; off, T3's
restart-to-rejoin is unchanged.

---

## Gate (050-a)

A **bare restart of the primary no longer degrades the cluster silently**: the successor promotes
and registers, the ex-primary rejoins as a live standby on its own, the dashboard and log say so at
every step, a one-endpoint client is warned, and the documented `--drain-and-stop` upgrade path is
proven zero-loss in the rig.
