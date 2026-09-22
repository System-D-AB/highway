# Replication & Failover

> **Status:** current (off by default). 050-a failover resilience is shipped; 050-b (symmetric config) is the follow-on.
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
resumable checkpoint (`HW.REPL.SNAPSHOT`) rather than being served a gapped stream. A **caught-up**
standby pulls at a low idle cadence (≈20/s) and applies nothing — the primary returns an empty page
for a pull at the watermark, so an idle replica set costs negligible CPU on both sides (feature 058;
the ack keeps refreshing the slot so liveness is unaffected).

**Start order does not matter.** A blank standby whose primary is not yet listening **waits** for it —
retrying the snapshot bootstrap with backoff and logging each wait — instead of exiting; the snapshot
downloads into a temporary directory and is moved into place only when complete, so an interrupted
bootstrap never leaves a partial database (feature 058). The shipped systemd unit sets
`TimeoutStartSec=infinity` for exactly this wait.

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

## Failover resilience (050-a, shipped)

A routine primary restart used to leave a two-node set degraded; 050-a closes that class:

- a node that **promotes** (not just one that *starts* primary) self-registers in the roster, so the
  succession view and the successor-priority map survive a failover;
- a demoted ex-primary **auto-rejoins** the new primary — on its next start it wipes and re-syncs as a
  replica of the endpoint it learned, no reconfigure or wipe by hand (opt-out via
  `server.replication.autoRejoin`); its diverged tail is written to a reconciliation report first, and
  there is no auto-failback;
- stale slots **age out** — cleared on demotion, and a silent slot is dropped past
  `server.replication.slotStaleAfter` — so the dashboard never shows a phantom "Active" replica;
- the leadership change is **loud**: a dashboard leadership banner, a no-redundancy alert, an event
  timeline, a client role-change event, and a single-endpoint-client startup warning;
- **safe upgrades**: a `--drain-and-stop` verb and a rolling-upgrade runbook, proven zero-loss by an
  in-process herd-rig scenario.

Still ahead — **050-b**: a symmetric member/peer config model that dissolves the asymmetric-config
stranding at the root, so rejoin needs no learned-endpoint marker. This work answers the
["loud, bounded, never total" doctrine](../product/fail-safe-and-observability.md); the machine
channels it depends on — metrics (051) and health/readiness endpoints (052) — are separate pillars.

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
