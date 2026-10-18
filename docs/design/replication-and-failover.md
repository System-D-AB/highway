# Replication & Failover

> **Status:** current (off by default). 050-a failover resilience is shipped; 050-b (symmetric config) is the follow-on.
> **Protocol:** the *Replication Commands* section of
> [`docs/HIGHWAY-PROTOCOL.md`](../HIGHWAY-PROTOCOL.md) is authoritative for the `HW.REPL.*` family.
> Guarantees are C9 in [`constraints.md`](../product/constraints.md); the observability doctrine is
> [`fail-safe-and-observability.md`](../product/fail-safe-and-observability.md).

*Highway replication is one writable primary with warm standbys that follow its write-ahead log,
and failover with no elections: the master is simply the node the client herd is connected to, chosen by a deterministic
successor function and fenced by a monotonic epoch, so the herd can never split into two masters.*

## Overview

Replication is off by default — a single node stays writable with no standby. Configure
`HighwayReplicationOptions` and a broker becomes one **primary** plus N **warm standbys** that follow
its write-ahead log. Standbys serve no client traffic; they exist to take over. There is no quorum,
no vote, and no plan to add one.

Since 3.0 (feature 076) the work is split. **SystemD.Crystal moves the data**: its own replication
port and protocol, authenticated by the replica set's shared secret (the broker password unless one
is configured), epochs and fencing on the data path, snapshot bootstrap, gap and divergence handling,
format checks, reconciliation reports, rejoin — and it decides nothing. **Highway decides**: who is
primary, when a standby is willing, when to fence, the roster, the graceful drain. Every rule below
under *Mastership* is Highway's, unchanged from 2.x.

## How it works

**The data path is Crystal's.** A node in a replica set listens on a replication port (by default the
RESP port plus one, opened when a standby first says hello or the node is promoted, so a broker outside
a replica set opens no second port). A standby follows its primary there: it bootstraps from a snapshot when it holds nothing or
has diverged, then pulls the primary's committed batches in order, applying each as one atomic write.
A caught-up standby waits in a long poll, so an idle pair costs nothing; a standby too far behind the
retained log is never fed a gapped stream — it re-bootstraps on its own. The primary keeps a slot per
standby with its acknowledged position, its lag in batches and in time, and its state (active,
lagging, dropped). Database `highway` is replicated; the cache and node-local state are not. A broker
that serves TLS replicates over TLS with the same certificate (mutual when RESP requires client
certificates); the shared-secret challenge runs inside it either way.

**Contact is the hello.** A standby sends `HW.REPL.HELLO` to its primary over RESP every 200 ms: its
position, the highest epoch it has seen, its storage format. Each answered hello is its **master-link
contact**, which willingness and the fence backstop read; each arriving one is the primary's contact
from that standby. The reply names the primary's replication endpoint, which the standby follows —
so a standby is configured with the primary's RESP address only, as before. Crystal's own pull is a
long poll that moves nothing while idle, so it cannot say whether a master is alive; the hello can.

**Start order does not matter.** A blank node opens at once and keeps saying hello until its
primary answers; Crystal bootstraps it in the background and an interrupted bootstrap leaves nothing
partial. The shipped systemd unit sets `TimeoutStartSec=infinity`, which 3.0 no longer needs but keeps.

**A node is syncing until it has caught up; only then is it a standby.** While it copies the primary's
data, and until it has applied everything the primary has (lag 0), it is **syncing**: it is never
willing, it has lost no master (its silence clock does not run), and `/ready` answers `bootstrapping`.
It becomes a standby the first time it holds the data and has caught up, and its master-silence clock
starts there — never at process start, so a slow start or a slow copy can never read as a dead master.
A re-bootstrap (a rejoin, a gap, a divergence) makes it syncing again. A node that restarts with its
data already on disk was a complete standby before it stopped and is one again from start, as on 3.0's
RocksDB engine: otherwise a standby restarting while its primary is dead could never take over.

**Mastership is client-defined — the herd, not a timer.** The master is *the node the client herd is
connected to*; a node with no clients is not a master and performs no master-only side effects.
There is one master because every client computes the **same pure successor function** — the
highest-priority reachable *willing* node from the live roster — so the herd moves as one and never
splits. A standby answers *willing* only when its own link to the master is dead past a threshold (or
it saw a GOODBYE), and a priority stagger makes the highest-priority successor turn willing first, so
several standbys losing the master at once still converge on one. The stagger alone is not trusted
(feature 069, 3.0): under load a client's probe of the live successor can be slow enough to look dead,
and before 3.0 that client could walk on to a lower-priority standby that answered willing by its
timer — two masters at one epoch. So a standby that has lost its master also **asks its
higher-priority roster peers** (`HW.REPL.HELLO PEER`, a peer session that is neither a herd client nor
master contact) and answers willing only when none of them is a master, willing, or still following
one; otherwise it answers standby and redirects the client to that peer. A peer it has no recent
answer from counts as alive — it waits rather than guesses. And if two nodes do promote at one epoch,
each hears the other's announce and the **lower-priority one yields**: it demotes, writes a
reconciliation report and auto-rejoins the other as its replica. Promotion happens on the herd's
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

**The session follows the herd** (3.0). A client that adopts a new master re-declares its whole
session there — catalog, subscriber groups, job schedules — before re-driving its pending calls, and
re-subscribes every doorbell, queue doorbells included. A promoted standby normally already holds that
state (it is replicated), so the re-declaration is a no-op; it is what makes a *fresh* or
re-bootstrapped master safe, where subscriptions and schedules would otherwise be silently missing.

**Formats must match** (3.0). A standby holds the primary's data key for key, so both must store the
same layout. Every hello declares the storage format, and a primary refuses a standby with another one
(`HW_STORAGE_FORMAT`). The primary's reply echoes its own format and names its replication endpoint, so
a 3.0 standby refuses a 2.x primary (which echoes nothing) and a 3.0 pre-release (which names no
endpoint) and stays empty-handed until the primary is upgraded. Crystal checks its own storage format
on the data path too, in both directions. A 2.x and a 3.0 node therefore never form a pair.

**Upgrading a pair to 3.0 is a fresh start, one node at a time** (no in-place migration):
1. Stop the standby, install 3.0 on it, run `highways --wipe-data`, and leave it stopped. The 2.x
   primary serves alone.
2. Drain what must not be lost on the 2.x primary (waiting, deferred and dead-lettered work; RPC in
   flight ends within its callers' deadlines).
3. Stop the 2.x primary, then start the 3.0 node as primary. Clients (on the 3.0 client) fail over to
   it and declare their sessions. Downtime is the gap between these two steps.
4. Install 3.0 on the old primary, `--wipe-data`, start it as standby: it re-syncs from the new primary.

Never start the 3.0 node as a standalone writable broker while the 2.x primary still serves: clients
list both endpoints and would split between two masters.

## Failover resilience (050-a, shipped)

A routine primary restart used to leave a two-node set degraded; 050-a closes that class:

- a node that **promotes** (not just one that *starts* primary) self-registers in the roster, so the
  succession view and the successor-priority map survive a failover;
- a demoted ex-primary **auto-rejoins** the new primary — it records the endpoint it learned
  (node-locally, in the store's not-replicated `highway.node` database), and on its next start it is
  that primary's standby: Crystal finds its diverged history and re-bootstraps it, no reconfigure or
  wipe by hand (opt-out via `server.replication.autoRejoin`); its diverged tail is written to a
  reconciliation report first — Crystal's, per box, and Highway's beside it naming each queue and
  service — and there is no auto-failback;
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

- **At most one writable node, without votes** (C9.2/C9.5): mastership is client-defined, a
  standby defers to any live higher-priority peer, an equal-epoch pair settles by priority,
  succession is deterministic config, the epoch is a rule-based tie-break — never a tally.
- **RPO is the measured replication lag window** (C9.1): a primary loss inside that window can lose
  an acked-but-not-yet-replicated message. It is surfaced (`HW.STATS repl.slot.N.lag`), bounded, and
  never silent.
- **A replica serves no client writes** (C9.3) — refused by Highway's gate, and by Crystal, which
  refuses local writes on a standby; a dead replica cannot fill the disk (C9.4, WAL TTL + size +
  slot-lag cap, enforced by Crystal).
- **A pull's cost does not grow with uptime** (C9.9): Crystal serves a caught-up standby from memory
  and a standby further behind through a held log reader.
- **No auto-failback** (C9.6): a higher-priority node waits; failback is a deliberate GOODBYE.
- **Two-to-few-node warm standby**, not cross-datacenter quorum consensus and not active-active —
  Highway stays single-writer.
