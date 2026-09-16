# Feature 042-1d — Herd Proof & Record: Design

*The gate for [042-1](../design.md). Design here means test architecture — the mechanics live
in 042-1b/c.*

## Harness architecture

```
HerdHarness (tests/Highway.Integration.Tests/Herd/)
├── Cluster: 3× HighwayTestServer on shared temp dirs
│     node1(prio 1) master-at-start · node2(prio 2) · node3(prio 3, or 0 for skip tests)
│     wired via Replication options (PrimaryServer chains, JOIN announcements)
├── Herd: N EngineNodes on the same multi-endpoint bootstrap string
│     each with a service host + a subscriber + a sending loop
├── Ledger: per-client record of every issued requestId + every observed
│     delivery/reply/ack — the duplicate counter and the loss detector
└── Chaos: KillMaster() (dispose), IsolateFromPeers(node) (stop its puller targets),
      Island(node) (dispose listeners, keep process), Goodbye(node), Rejoin(node, prio)
```

Partition simulation notes (in-process, no firewall): *peer* isolation = point the standbys'
pullers at a dead endpoint / pause the master's feeder answers; *client* isolation = per-client
candidate filtering injected into the walk (test seam on `HighwayConnectionSource`,
internal-visible). Each simulation is documented next to the test so nobody mistakes it for
packet-level fault injection; the assurance rig (D-R5) covers the coarser real-process case.

## The two core assertions, made mechanical

- **No split:** after any chaos step, collect each client's `ActiveServer`; the set of live
  masters serving ≥1 client must have size ≤ 1 (by construction of the assertion, not
  eyeballing logs).
- **Zero acked loss / counted duplicates:** the Ledger diffs *acked-by-broker* ids against
  *read-back-on-new-master* ids after convergence: missing ⇒ fail (unless within the measured
  RPO window for the hard-kill case — asserted against `repl.slot.*.lag` captured at kill
  time); duplicates ⇒ counted and reported, fail only if unaccounted.

## Observability as load-bearing

Harness steps read state exclusively through `HW.REPL.STATUS`/`HW.STATS` and the transition
log (no reflection into feeders except where a test seam is explicitly declared) — proving
D-R4 by use.

## Testing strategy

The requirement list *is* the test list (D-R1…D-R5 rows map 1:1 to named tests). The record
task (D-R6) follows the house rules: dated amendments, no history rewrites, protocol doc
untouched here (042-1a owned it).
