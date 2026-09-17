# Fail-Safe & Observability — Doctrine, Hole Inventory, and Roadmap

*A distributed system is only as trustworthy as what it tells you when something goes wrong. This
document states the principle Highway is held to, inventories every silent-failure hole a senior
review can find, defines exactly how a significant event — a leadership change above all — must be
made loud, and lays out the features that get us there. It is a living roadmap: features link back
here; here links forward to them.*

## The doctrine — "loud, bounded, never total"

Three rules, in priority order:

1. **Never total.** No single failure — a node, a disk, a network path, a bad config, a version
   skew — may take down the whole system. Failure is contained to the smallest blast radius, and the
   rest keeps serving, degraded if necessary.
2. **Never silent.** Every failure and every significant state change is made **loud on every
   channel that matters** — log, metric, health endpoint, dashboard, doorbell, audit trail — so a
   human or a machine learns of it *when it happens*, not from the next failure. A failure with full
   observability is acceptable; a silent degradation is not, even if smaller.
3. **Bounded and stated.** Every degradation has a named, bounded cost (an RPO window, a cold cache,
   a cap) that is documented in `constraints.md` and surfaced at runtime — never an open-ended or
   hidden loss.

The test for any feature: *if this fails at 3 a.m., does the operator find out immediately and from
which channel, and does anything else keep working?* If the answer is "they find out at the next
outage," it is not done.

## A leadership change must be loud on every channel (the "what else?")

A node becoming primary (or a primary demoting) is the highest-consequence event in the cluster. It
must be visible on **all** of these — not one:

| Channel | What it must show | Status today |
|---|---|---|
| **Structured log** | `PROMOTED`/`DEMOTED` with node, epoch, reason, timestamp — on the promoting node, the demoting node, and every observer that adopts the epoch | Partial (047 logs promote/demote locally); not on observers, not the demoted node's rejoin |
| **Metric** | `highway_replication_role` gauge, `highway_epoch` gauge, `highway_promotions_total` / `highway_demotions_total` counters, `highway_seconds_since_promotion` | **Missing — no metrics exist at all** |
| **Health / readiness endpoint** | role reflected; a demoted/fenced/not-yet-rejoined node reports **not-ready** so load balancers and orchestrators stop routing to it | **Missing — no health endpoint** |
| **Dashboard** | a prominent **banner** on the node's tab AND a cluster-wide statement ("Primary is now X, epoch N, since T"), plus a leadership-change entry in an event timeline | Partial (tab shows role; no banner, no timeline, no cluster-wide statement) |
| **Roster (replicated state)** | the new primary registered so every node's view is correct | **Broken** — a *promoted* node never self-registers (050 F1); the roster goes empty exactly when it matters |
| **Topology doorbell** (`hw:door:topology`) | cluster-wide narration so clients and peers react immediately | Exists (GOODBYE/TOPOLOGY); extend to carry promote/demote uniformly |
| **Audit trail** | a **persistent, queryable** record of who promoted, when, why, from which epoch — survives restart, unlike the volatile flight recorder | **Missing** |
| **Client event** | the client raises a role-change/`Converged` event and logs "master changed to X"; the app can react (circuit-break, alert) | `Converged` exists internally; not surfaced to the app or logged with the new master's identity |
| **External notification** | an optional webhook/callback on critical events (promotion, demotion, no-redundancy) → paging / Slack / PagerDuty | **Missing** |
| **Trace span** | an `Activity` span for the failover, for distributed tracing | `Activity` emission exists elsewhere; not on failover |

The gap is stark: of ten channels, three work partially, and the two that a machine (LB, alerting)
depends on — **metrics and health endpoints — do not exist at all.** That is why such a failure stays invisible to everything except a human reading a stack trace.

## The silent-failure inventory

Severity: **S1** = can silently lose data or take the system down; **S2** = silently loses
redundancy/availability; **S3** = silently misleads the operator. "Closes in" names the feature that
fixes it (some new, proposed below).

### Replication & failover
| # | Hole | Sev | Closes in |
|---|---|---|---|
| R‑1 | A promoted node never registers itself in the roster → cluster loses its own membership map exactly at failover | S2 | 050 F1 |
| R‑2 | A demoted ex-primary can't rejoin → HA silently gone after any primary restart | S2 | 050 F2 |
| R‑3 | Stale/phantom slots read as "Active" on a node that no longer serves them | S3 | 050 F3 |
| R‑4 | No "you have no redundancy right now" signal (primary with 0 standbys; demoted-not-rejoined) | S2 | 050 R6 + metrics/health |
| R‑5 | **Replication lag / RPO window grows unbounded with no metric or alert** — you learn your standby is 10 min behind only when you fail over and lose 10 min | S1 | **051 metrics** + 050 |
| R‑6 | A replica dropped past the WAL cap / retention can never catch up; it silently stops being a valid successor | S2 | 050 + metric on slot state |
| R‑7 | Epoch divergence / split-brain risk is not loudly flagged as an alarm | S1 | 050 + metrics/audit |
| R‑8 | Snapshot bootstrap failure or partial leaves a replica permanently invalid, silently | S2 | 050 T2/T3 + health |
| R‑9 | Priority collision (`HW_PRIORITY_TAKEN`): the loser silently stays out of succession → there may be **no valid successor** and nobody said so | S2 | 050 + startup validation + metric |
| R‑10 | A willing-but-unreachable successor stalls herd convergence; clients hang | S2 | 050 R5 + client telemetry |

### Client
| # | Hole | Sev | Closes in |
|---|---|---|---|
| C‑1 | **Client/server version or protocol-capability skew** (the 1.0 client that can't follow `-NOTPRIMARY`) → failover silently doesn't work | S2 | **054 client safety** (capability negotiation + refuse/warn) |
| C‑2 | Single-endpoint client in a replica set → no failover | S2 | 050 R5 (startup warning) |
| C‑3 | A node's heartbeat failing (disconnected from the cluster) is a log line the app never sees | S3 | 054 (client state event/metric) |
| C‑4 | Rising RPC timeouts / error rates have no client-side metric | S3 | 051/054 metrics |
| C‑5 | `-NOTPRIMARY` to an unreachable endpoint throws a bare transport exception, not an actionable message | S3 | 050 R5.3 |

### Storage & durability
| # | Hole | Sev | Closes in |
|---|---|---|---|
| D‑1 | Disk full / write failure — does the broker fail loud, or wedge / corrupt silently? Must be **fail-loud + refuse writes + alarm**, never silent partial writes | S1 | **055 resource guards** |
| D‑2 | RocksDB open/corruption failure behaviour (crash-loud vs hang) is unspecified | S1 | 055 |
| D‑3 | An **ephemeral** broker (no `dataDir`, or a data dir on ephemeral storage) looks identical to a durable one on the dashboard — durability silently absent | S1 | 055 (loud ephemeral banner) + health |
| D‑4 | WAL retention shorter than a replica's downtime → perpetual re-bootstrap loop, silent | S2 | 050 + metric |

### Resource exhaustion
| # | Hole | Sev | Closes in |
|---|---|---|---|
| E‑1 | Queue depth growing toward the cap (backpressure) with no alert until sends are refused | S2 | 051 metrics + 055 |
| E‑2 | Dead-letter queue filling — nobody is told messages are dying | S2 | 051 metrics |
| E‑3 | Connection / file-descriptor / memory pressure on the broker | S2 | 051/055 |
| E‑4 | Flight-recorder / cache memory pressure | S3 | 051 |

### Configuration & environment
| # | Hole | Sev | Closes in |
|---|---|---|---|
| G‑1 | Config mismatch between nodes — different passwords (replica silently can't connect), colliding priorities, mismatched TLS | S2 | 054/055 (cross-node config check + loud auth-fail) |
| G‑2 | Clock skew between nodes skews willingness/fence timers | S2 | 055 (skew detection + warn) |
| G‑3 | TLS disabled with auth configured → credentials in clear text | S3 | Already warned (good — the pattern to copy) |

## The observability pillars (foundational features)

These are the enablers. Without pillars 1 and 2, **nothing can alert** — the failure stays a human-only, after-the-fact discovery.

1. **051 — Metrics (OpenTelemetry `Meter`).** Export the numbers a fail-safe system alerts on:
   replication role/epoch/lag/slots and RPO seconds; queue depth and DLQ count; RPC
   latency/throughput/error rate; connection counts; promotions/demotions/fences counters. No new
   dependency beyond `System.Diagnostics.Metrics` (BCL) — the app wires its own OTel/Prometheus
   exporter, exactly as it does today for `Activity`. **This is the single highest-leverage gap.**
2. **052 — Health & readiness endpoints.** `/health` (liveness), `/ready` (can this node serve
   client traffic — false when replica/fenced/demoted/not-bootstrapped), `/replication` (role, lag,
   roster). So a load balancer routes only to the writable node and pulls a demoted one out of
   rotation automatically — closing R‑4/R‑8/D‑3 at the infrastructure layer.
3. **050 — Failover resilience & full failover observability.** (Enhanced by this document — see the
   leadership-change matrix; every channel above must light up on promote/demote.)
4. **053 — Audit trail.** A small, persistent, queryable log of significant events (promotions,
   demotions, fences, GOODBYE, config-affecting restarts) that survives restart — the record you
   reach for during a after-the-fact review, which the volatile flight recorder can't be.
5. **054 — Client safety & telemetry.** Server↔client **capability/version negotiation** (a client
   that can't do herd failover is warned or refused, not silently broken — closes C‑1, the exact
   footgun hit); a single-endpoint-in-a-replica-set startup warning; a surfaced client role-change /
   disconnect event + client metrics; actionable redirect-failure messages.
6. **055 — Resource & durability guards.** Fail-loud on disk-full / write failure (refuse, alarm,
   never silent-partial); a loud "this broker is EPHEMERAL — no durability" banner and health signal;
   cross-node config sanity check at join (password/priority/TLS mismatch → loud, not a silent
   can't-connect); clock-skew detection; queue/DLQ/connection pressure surfaced through 051.
7. **056 — External notification hooks (optional).** A configurable webhook fired on critical events
   (promotion, no-redundancy, DLQ growth) so paging happens without scraping metrics — the last mile
   to "the operator finds out at 3 a.m."

## Roadmap & sequencing

Ordered by "stops silent S1/S2 loss soonest," with the enablers first:

1. **050-a** — close the acute failover hazard (roster self-register, auto-rejoin, slot aging, loud
   logs + dashboard no-redundancy banner + the leadership-change matrix's log/roster/doorbell/client
   channels), and the safe-upgrade `--drain-and-stop` + runbook. *This is the urgent one — it stops
   a routine restart from silently killing HA.*
2. **051 — Metrics.** Unlocks all machine alerting (lag, role, depth, errors). Highest leverage.
3. **052 — Health & readiness endpoints.** Lets infrastructure route around failures automatically.
4. **053 — Audit trail** and **054 — Client safety** (capability negotiation closes the version
   footgun; client telemetry surfaces disconnects).
5. **055 — Resource & durability guards** (fail-loud on disk/write; ephemeral banner; config sanity).
6. **050-b** — the symmetric member/peer config (root cure for the asymmetric-config stranding).
7. **056 — External notification hooks.**

Each becomes a numbered feature under `docs/features/` with its own requirements/design/tasks; this
document is the index and the doctrine they answer to. `constraints.md` gains a **C11 — Fail-Safe**
block stating, line by line, which failure modes are made loud and bounded, and which remain open.
