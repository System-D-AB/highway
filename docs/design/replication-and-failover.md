# Replication & Failover

> **Status:** current (off by default); active hardening — see [Roadmap](#roadmap)
> **Protocol:** the *Replication Commands* section of
> [`docs/HIGHWAY-PROTOCOL.md`](../HIGHWAY-PROTOCOL.md) is authoritative for the `HW.REPL.*` family.
> Guarantees are C9 in [`constraints.md`](../product/constraints.md); the observability doctrine is
> [`fail-safe-and-observability.md`](../product/fail-safe-and-observability.md).

*Highway replication is one writable primary with warm WAL-shipping standbys, and failover with no
elections: the master is simply the node the client herd is connected to, chosen by a deterministic
successor function and fenced by a monotonic epoch, so the herd can never split into two masters.*

## Overview

Replication is off by default — a single node stays writable with no standby. Configure
`HighwayReplicationOptions` and a broker becomes one **primary** plus N **warm standbys** that pull
its write-ahead log. Standbys serve no client traffic; they exist to take over. There is no quorum,
no vote, and no plan to add one.

## How it works

**WAL shipping, pull-based.** A standby registers a slot and pulls paged WAL batches (`HW.REPL.PULL`)
from a sequence, applies them, and acks a watermark (`HW.REPL.ACK`); every reply is stamped with the
primary's **epoch**. A standby too far behind the retained WAL re-bootstraps from a chunked,
resumable checkpoint (`HW.REPL.SNAPSHOT`) rather than being served a gapped stream.

**Mastership is client-defined — the herd, not a timer.** The master is *the node the client herd is
connected to*; a node with no clients is not a master and performs no master-only side effects.
There is one master because every client computes the **same pure successor function** — the
highest-priority reachable *willing* node from the live roster — so the herd moves as one and never
splits. A standby answers *willing* only when its own link to the master is dead past a threshold (or
it saw a GOODBYE), and a priority stagger makes the highest-priority successor turn willing first, so
several standbys losing the master at once still converge on one. Promotion happens on the herd's
**first accepted client verb** and bumps the durable epoch; a resurrected lower-epoch node demotes on
first contact, writes a reconciliation report, and never merges. The old two-timeout deadman survives
only as a **fence backstop** (a herd-less, peer-less primary goes read-only) — no timer ever
promotes.

**Standbys never take writes.** A mutating command on a non-master is refused
`-NOTPRIMARY <endpoint> <epoch>`, redirecting the client to the current master — extended to the raw
key-write surface too, so a client pointed at a standby cannot stage state either. Reads, stats and
admin still work.

**The roster is master-owned, and priority is a unique key.** A joining node announces its own
priority (`HW.REPL.JOIN`); the master admits it into a **replicated roster** every standby holds, and
a priority already held by a live member is refused (`ERR HW_PRIORITY_TAKEN`, naming the holder) —
first announcer wins, so succession order never depends on restart order. The client connection
string is bootstrap only; the live roster is the running truth, so the cluster can grow beyond any
client's original string.

**Handover is deliberate; failback is not automatic.** `HW.REPL.GOODBYE` drains the master
gracefully — narrate, refuse new work, let in-flight complete, stand down — for a zero-loss,
chosen handover (the basis of a safe rolling upgrade). A returning higher-priority node joins as a
warm standby and *waits*; it never preempts a healthy master. An operator who wants a specific node
back issues GOODBYE. Every transition is thus failure-forced or operator-chosen, never surprise on a
reboot.

**Where responsibility begins.** The **ack is the birth**: before it, a request lives only in the
client (client death before ack is a non-birth, not a loss); after it, the request is the cluster's —
durable on the master and replicated within the lag window. Each client replays its own
*unacknowledged* work to the new master with the same request id on convergence, so a single server
failure loses no acked work.

## Roadmap

A routine primary restart currently exposes real gaps that are being closed: a node that *promotes*
doesn't self-register in the roster; a demoted ex-primary can't rejoin automatically under asymmetric
config; stale slots and degraded states aren't loud enough. Feature 050 addresses these — roster
self-register on promote, automatic rejoin of a demoted node, slot aging, a cluster-wide loud
leadership change on every channel, and a proven `--drain-and-stop` rolling-upgrade path — followed
by a symmetric member/peer config model. This work answers the
["loud, bounded, never total" doctrine](../product/fail-safe-and-observability.md), whose
leadership-change channel matrix depends on the metrics (051) and health/readiness endpoints (052)
that expose replication state to machines.

## Guarantees & limits

- **At most one writable node, without votes** (C9.2/C9.5): mastership is client-defined,
  succession is deterministic config, the epoch is a rule-based tie-break — never a tally.
- **RPO is the measured replication lag window** (C9.1): a primary loss inside that window can lose
  an acked-but-not-yet-replicated message. It is surfaced (`HW.STATS repl.slot.N.lag`), bounded, and
  never silent.
- **A replica serves no client writes** (C9.3); a dead replica cannot fill the disk (C9.4, WAL TTL +
  size + slot-lag cap).
- **No auto-failback** (C9.6): a higher-priority node waits; failback is a deliberate GOODBYE.
- **Two-to-few-node warm standby**, not cross-datacenter quorum consensus and not active-active —
  Highway stays single-writer.
