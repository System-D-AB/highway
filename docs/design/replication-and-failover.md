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

**Pulls are served from memory, not the WAL (feature 062).** RocksDB's WAL reader
(`GetUpdatesSince`) locates the live WAL file and reads it from its start up to the requested
sequence; the live file rolls only on a memtable flush, which a lightly written broker reaches after
hours. Reading the WAL on every pull therefore cost more the longer the primary ran (≈1.5 ms per pull
after 2 000 writes, ≈54 ms after 120 000, at ≈20 pulls/s per caught-up replica). The primary now keeps
its most recently committed batches in a bounded in-memory tail (16 MiB): a pull at the watermark
answers without touching the WAL, and a pull just behind it gets the tail's contiguous run. The tail
is captured after each commit's write — the batch then carries its assigned sequence and is the same
bytes as its WAL record — holds no lock across the synced write, starts only once a replica first
pulls, and is cleared on any epoch or role change. Anything it cannot serve contiguously (a replica
further behind, a restarted primary, a batch still being added) falls back to the WAL path, gap
checks included.

**Catching up reads the WAL once, not once per page (feature 066, 3.0).** A replica behind the tail
used to pay the from-the-start WAL read on *every* page, so catching up N pages cost O(N × WAL size)
(measured: 300 000 batches, 35 pages, 1.5 s with an 86 ms last page). The primary now keeps a few WAL
tailers open between pulls, keyed by the sequence each will serve next: a pull that continues where the
previous one stopped continues the same tailer, and only a pull from anywhere else opens a fresh one.
The same catch-up takes 0.2 s. A tailer's page is one write batch made of whole WAL records, and the
replica applies it with **one synced write per page** — a crash leaves the whole page or none of it.
A tailer is opened only after the WAL is confirmed to still hold its starting sequence (a range
retention already removed is refused as a gap, as before), and while tailers are open WAL file deletion
is paused so none can be removed under them. That pause is bounded: tailers are dropped on any epoch or
role change, closed after a few idle seconds, and never held for more than 30 s at a stretch, so a
replica that pulls forever without catching up cannot suspend WAL retention (C9.4).

One consequence is handled explicitly: RocksDB's group commit merges concurrent committers into one
WAL record, while the tail holds them separately, so a replica fed from the tail can stand *inside* a
WAL record. When such a replica falls back to the WAL, the record is trimmed to start just after its
watermark; a record that cannot be split with certainty is refused like a gap, and the replica
re-syncs from a snapshot rather than risk diverging.

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

**Formats must match** (3.0). A replica applies the primary's WAL byte for byte, so both must store the
same layout. The replica form of `HW.REPL.HELLO` declares the storage format, and a primary refuses a
replica with another one (`HW_STORAGE_FORMAT`). The primary's reply echoes its own format, so a 3.0
replica also refuses a 2.x primary, which ignores the declaration and echoes nothing; a snapshot of 2.x data is refused when the replica
opens it. A 2.x and a 3.0 node therefore never form a pair.

**Upgrading a pair to 3.0 is a fresh start, one node at a time** (no in-place migration):
1. Stop the standby, install 3.0 on it, run `highways --wipe-data`, and leave it stopped. The 2.x
   primary serves alone.
2. Drain what must not be lost on the 2.x primary (waiting, deferred and dead-lettered work; RPC in
   flight drains within its timeout).
3. Stop the 2.x primary, then start the 3.0 node as primary. Clients (on the 3.0 client) fail over to
   it and declare their sessions. Downtime is the gap between these two steps.
4. Install 3.0 on the old primary, `--wipe-data`, start it as standby: it re-syncs from the new primary.

Never start the 3.0 node as a standalone writable broker while the 2.x primary still serves: clients
list both endpoints and would split between two masters.

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

- **At most one writable node, without votes** (C9.2/C9.5): mastership is client-defined, a
  standby defers to any live higher-priority peer, an equal-epoch pair settles by priority,
  succession is deterministic config, the epoch is a rule-based tie-break — never a tally.
- **RPO is the measured replication lag window** (C9.1): a primary loss inside that window can lose
  an acked-but-not-yet-replicated message. It is surfaced (`HW.STATS repl.slot.N.lag`), bounded, and
  never silent.
- **A replica serves no client writes** (C9.3); a dead replica cannot fill the disk (C9.4, WAL TTL +
  size + slot-lag cap).
- **A pull's cost does not grow with uptime** (C9.9): a caught-up or nearly caught-up replica is
  served from the in-memory tail; a replica further behind catches up through one held WAL reader, one
  synced write per page.
- **No auto-failback** (C9.6): a higher-priority node waits; failback is a deliberate GOODBYE.
- **Two-to-few-node warm standby**, not cross-datacenter quorum consensus and not active-active —
  Highway stays single-writer.
