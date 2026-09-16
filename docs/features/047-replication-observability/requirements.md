# Feature 047 — Replication Observability

*Field-reported: an operator set up a two-node replica set, both brokers started, and there was
**no way to tell whether the replica had joined** — the replication code writes nothing to the
logs, and the operator can't (and shouldn't have to) install `redis-cli` on production to run
`HW.REPL.STATUS`. A distributed system that is silent about its own replication is not operable.*

## Requirement 1: Replication writes to the log

**User Story:** As an operator, I want the broker's log (`logs/highway-*.log`, feature 045) to show
what replication is doing, so I can confirm a replica joined, see it fall behind or re-sync, and
see a failover happen — without any extra tooling.

**Root cause.** `ReplicaPuller` and `ReplicationFeeder` hold no `ILogger` and emit nothing to it;
their only trace is an in-memory transition tail read by `HW.REPL.STATUS`. So `logs/` shows the
startup line and nothing else about replication, ever.

#### Acceptance Criteria

1. **Replica side** logs, at Information unless noted: it is following a primary and bootstrapping a
   snapshot when its data directory is blank; it connected to the primary and is streaming; it lost
   the connection and is retrying (Warning); and it hit a WAL gap and must re-bootstrap (Warning).
2. **Primary side** logs: a replica attached (a new slot) with its watermark; a slot dropped for
   exceeding the lag cap (Warning); a promotion (with epoch and reason); a demotion on a higher
   observed epoch (Warning); fence / unfence; and GOODBYE begin / complete.
3. Logs are **milestone-level, not per-message** — a healthy streaming replica does not log on every
   pull. Frequency is bounded by connection and state transitions, not by traffic.
4. The messages are greppable and name the peer, so an operator can filter (`[replication]` /
   `Replica …` / `Promoted …`).
5. No behaviour change: logging is additive, and a broker with replication disabled logs nothing new.

## Requirement 2: The dashboard shows replication state on both nodes

**User Story:** As an operator, I want the dashboard's Replication tab to be informative on the
primary *and* on a replica, so it is not a blackhole when I open it.

#### Acceptance Criteria

1. The Replication tab shows `role` and `epoch` on any node (already true on the primary), and on a
   **replica** shows the primary it follows and its lag — not an empty slot table.
2. It shows the **roster** (both nodes, their priorities and endpoints) so the succession order is
   visible from either node.
3. Documentation notes that the dashboard binds to **loopback by default** (`dashboard.bindAddress`)
   — to reach it from another machine, set `0.0.0.0` and an `apiKey`. (That, not a code bug, is why
   a remote browser saw nothing.)

## Non-goals

Changing the replication protocol or failover behaviour. This feature only makes the existing
behaviour observable.
