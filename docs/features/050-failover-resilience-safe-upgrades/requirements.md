# Feature 050 — Failover Resilience & Safe Upgrades

*This feature exists because of a real field incident: a routine binary upgrade of a healthy
two-node replica set left the cluster **permanently degraded** — one primary running solo, one
ex-primary stranded and unable to rejoin, an empty roster on both, and clients erroring — with no
error that told the operator what had happened or how to fix it. This is not a corner case. **Any
restart of the primary** (an upgrade, a reboot, a crash, a deploy) triggers it, and most operators
will not realise their HA is gone until the next failure finds them with no standby.*

## The incident (the motivating record — do not delete)

Setup: `.20` primary (priority 1, no `startAsReplica`, no `primaryServer`); `.31` replica
(priority 2, `startAsReplica: true`, `primaryServer: .20`). Clients pointed at `.20`. Healthy:
`.31` attached, streaming, lag 0.

The operator did a rolling binary upgrade — replica first (`.31`), then primary (`.20`):

1. Upgraded `.31` (the replica): fine, zero client impact.
2. Restarted `.20` (the primary) to swap its binaries. **This is the fault line.** A bare restart
   of the primary looks like a primary failure to the herd.
3. `.31` promoted itself (herd-arrival, epoch 6) — correct behaviour on paper.
4. `.20` came back up, its client herd had moved, so it saw `.31`'s higher epoch and **demoted**
   (epoch 6). Correct fencing — no split-brain.

But the aftermath was silently broken:

- **`.31` is a lonely primary:** `SLOTS 0`, no replica. `.20` never came back as its standby.
- **`.20` is stranded in `Demoted`:** it has **no `primaryServer` pointing at `.31`**, so it cannot
  follow the new primary. It sits Demoted forever, replicating from nobody, until a human
  reconfigures it, wipes its data, and restarts it.
- **The roster is empty on both nodes** — so the dashboard succession view is blank *and* the
  brokers have lost the priority map they use to choose a successor. A promoted node never
  registered itself.
- **`.20`'s dashboard shows a phantom replica:** `broker-b · Active · lag 0` — a stale slot
  frozen from before `.31` promoted, which reads as healthy replication that is not happening.
- **Clients erred** (`NOTPRIMARY 203.0.113.31:6500`) and their nodes went stale, because the master
  had moved and the situation was, from the operator's seat, invisible.

Net: one primary restart → HA gone, no standby, no signal, and a multi-step manual recovery
(reconfigure + wipe + restart) that an operator can only perform if they already understand the
internals. **That is a production outage waiting for the next incident.**

## Failure modes to fix

| # | Failure | Consequence |
|---|---|---|
| F1 | A node that **promotes** (vs starts) as primary never registers itself in the roster | Empty/partial roster after every failover; blank succession view; weakened successor selection |
| F2 | A **demoted ex-primary cannot rejoin** the new primary (asymmetric config: no `primaryServer`) | HA silently lost after any primary restart until manual reconfigure + wipe + restart |
| F3 | **Stale slots persist** on a node that lost its primary role | Dashboard shows phantom "Active" replicas; operator misreads the cluster as healthy |
| F4 | **No safe upgrade path is enforced or documented** — a bare primary restart forces an unplanned failover | Every routine upgrade/reboot degrades the cluster |
| F5 | **Client connection-string / reachability requirements are implicit** | A client listing one endpoint, or unable to reach the promoted node, breaks with a bare `NOTPRIMARY` |
| F6 | **Nothing tells the operator any of this happened** | The whole incident is invisible until the next failure |

## Requirement 1: A promoted node registers itself in the roster (F1)

**User Story:** As an operator, I want the roster (and thus the succession view and the brokers'
own successor selection) to stay complete after a failover, so the cluster does not go blind about
its own membership the moment it needs it most.

#### Acceptance Criteria

1. When a node promotes (herd-arrival, GOODBYE succession, or explicit `--promote`), it upserts
   itself into the replicated roster with its id, priority, and advertised endpoint — the same
   record a node that *starts* primary already writes.
2. After a failover, `HW.REPL.STATUS` on the new primary lists a non-empty roster including itself,
   and the dashboard succession view is populated.
3. Idempotent: re-promotion or re-announcement does not duplicate or corrupt the roster.

## Requirement 2: A demoted ex-primary rejoins the new primary automatically (F2 — the critical one)

**User Story:** As an operator, I want a former primary that has been superseded to **become a
standby of the new primary on its own**, so that a single primary restart does not permanently cost
me my redundancy and my manual intervention.

#### Acceptance Criteria

1. When a node demotes on observing a higher epoch, and it has learned the new primary's endpoint
   (from the promotion announce / `-NOTPRIMARY` redirect), it **begins following that primary as a
   replica** — without requiring a pre-configured `primaryServer` and without an operator step.
2. Rejoin preserves the no-split, herd-wins contract: the demoted node **yields to the new
   primary's data**. Its own diverged tail (any acked-but-unreplicated writes past the failover
   watermark) is written to the reconciliation report **before** its store is re-synced, so nothing
   is destroyed silently — the RPO window is surfaced, not lost.
3. After rejoin, the ex-primary appears as a live attached slot on the new primary (lag converging
   to 0), and its role reads `Replica`.
4. It is **safe and bounded**: rejoin cannot loop (repeated demote/rejoin), cannot race two nodes
   into both re-syncing from each other, and cannot proceed on ambiguous epoch state — an
   unresolvable case stops in a clearly-logged `Demoted` hold rather than guessing.
5. There is no auto-**failback** (unchanged from 042-1): rejoining as a replica is not the same as
   taking mastership back. The former primary stays a replica until a deliberate GOODBYE hands it
   mastership again.

## Requirement 3: Stale slots age out on role change (F3)

**User Story:** As an operator, I want the dashboard to stop showing replicas that are no longer
streaming, so a demoted or superseded node does not look healthy when it is not.

#### Acceptance Criteria

1. When a node demotes (or otherwise stops being the primary), the replica slots it held are
   dropped/aged so they no longer render as `Active`.
2. A slot whose replica has not acked within a bounded window is marked stale/dropped rather than
   frozen at its last value, on the primary as well.

## Requirement 4: A safe, tested rolling-upgrade path (F4)

**User Story:** As an operator, I want a documented, tested procedure (and, ideally, tooling) to
upgrade or restart a primary **without** an unplanned failover and without degrading the cluster.

#### Acceptance Criteria

1. A `docs/` runbook specifies the rolling upgrade: replica first; then hand the primary off with
   `HW.REPL.GOODBYE` (graceful drain + succession) **before** restarting it; then the old primary
   rejoins (R2) as a replica.
2. The distribution ships the procedure (its README references it), so it travels with the binaries.
3. Ideally, a single verb/flag makes the graceful primary handoff-then-restart a one-command
   operation (an "upgrade mode"), so the safe path is the easy path. (Design decides whether this is
   in-scope for v1 or a documented manual sequence.)
4. An assurance-rig scenario exercises "rolling upgrade of a two-node set" and asserts zero client
   loss and a healthy pair afterwards — so the safe path is proven, not just written down.

## Requirement 5: Client requirements are explicit and enforced (F5)

**User Story:** As a developer, I want the client's endpoint requirements to be obvious and, where
possible, checked, so I don't discover them during an outage.

#### Acceptance Criteria

1. Documentation states plainly: a client of a replica set **must** list every broker endpoint in
   its connection string and **must** be able to reach every one of them, because the master can be
   any of them. A one-endpoint client has no failover; a client that cannot reach the promoted node
   cannot follow a `-NOTPRIMARY`.
2. The client emits a **startup warning** when it is configured against a broker that reports it is
   part of a replica set (roster size > 1) but the client's connection string names only one
   endpoint — the single most common footgun, caught before it bites.
3. (Design decides) the client's `-NOTPRIMARY`-to-unreachable-endpoint failure is logged with a
   clear, actionable message ("redirected to X, which is unreachable — open the path / add it to the
   connection string"), not a bare transport exception.

## Requirement 6: The operator can see it happened (F6)

**User Story:** As an operator, I want a failover, a demotion, a rejoin, and a degraded "primary
with no standby" state to be **loud** — in the log and on the dashboard — so I never again learn
about it from the next incident.

#### Acceptance Criteria

1. Promotion, demotion, rejoin-start, and rejoin-complete are logged (047 already logs
   promote/demote; this extends it to rejoin) and narrated on the topology doorbell.
2. The dashboard surfaces a **health banner** when the local node is a primary with **zero attached
   replicas** while the roster expects standbys, or is `Demoted` and not yet rejoined — the two
   "you have no redundancy right now" states — rather than leaving the operator to infer it from a
   `SLOTS 0` tile.

## Open decisions (resolve in design)

1. **OD1 — Runtime rejoin vs. restart-to-rejoin.** Does R2 re-sync the demoted node's store at
   runtime (wipe + snapshot while the process runs), or write a rejoin marker and re-bootstrap on a
   (possibly self-triggered) restart? Runtime is seamless but wipes a live DB; restart is safer but
   needs a bounce.
2. **OD2 — Symmetric configuration.** Should the config model change so a node is not statically
   "primary" or "replica" but simply a member with a priority and a set of peer endpoints, its role
   decided at runtime? That would dissolve F2 at the root (every node already knows how to follow
   any peer) but is a larger change. Decide whether 050 moves toward it or defers it.
3. **OD3 — Auto-rejoin default.** Is automatic rejoin on by default, or opt-in? Default-on removes
   the footgun; opt-in is conservative. Recommendation leans default-on with the reconciliation
   safety net, since the current default (stranded) is strictly worse.

## Non-goals

- Multi-primary / active-active. Highway stays single-writer.
- Automatic failback (taking mastership *back* to a preferred node without a deliberate GOODBYE) —
  explicitly out, per 042-1.
- Cross-datacenter / quorum consensus. This is the two-to-few-node warm-standby model, made robust.
