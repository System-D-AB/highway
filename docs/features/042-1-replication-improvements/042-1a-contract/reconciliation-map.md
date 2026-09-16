# 042-1a — Reconciliation Map (A-T1)

*Dated 2026-09-15. The reuse/change/delete map of the as-built 042 tree (uncommitted on
`rocksdb`, including the G1–G11 gap fixes) under the 042-1 herd model. Referenced by
042-1c C-T7 for the deletions.*

## Reused unchanged — the machinery layer

| Code | Role under 042-1 |
|---|---|
| `src/Highway.Server/Storage/Rocks/ReplicationFeeder.cs` — `Pull`/`Hello`/`Ack`, slots + `EnforceCap`, `BeginSnapshot`/`ReadSnapshotChunk`/`EndSnapshot`, `ReplicationGapException` (both shapes), epoch persistence (`repl-epoch.txt`), `ObserveHigherEpoch` demote/adopt, `WriteReconciliationReport` | WAL-ship, retention, snapshot, gap refusal, heal — parent RD2/RD3/RD4/R8, verbatim |
| `src/Highway.Server/Storage/Rocks/ReplicaPuller.cs` — pump loop, `DownloadSnapshot`, resync marker + restart-time wipe | Standby sync path, verbatim; gains the JOIN announcement in 042-1c |
| `src/Highway.Server/Storage/Rocks/BatchApplier.cs`, `ReplicationApply.cs`, `WalTailSummary.cs` | Apply atomicity + reconciliation content, verbatim |
| `TryPromote` + `AnnouncePromotionAsync` (epoch+endpoint HELLO gossip) | The promotion *mechanics* — 042-1 changes only what *triggers* them (herd arrival, C-T4) |
| `CommandDispatcher` `-NOTPRIMARY` gate + `RedirectEndpoint()`; `RespSession` raw-write gate | Parent R6's teeth, verbatim (OD3 stays refuse-and-redirect) |
| Client: `HostsOf`/`OptionsOf` bootstrap parsing; the serialized `TryFailoverAsync` gate + `AdoptCurrentMultiplexer`/delayed-dispose | 042-1b B-R1 done; the walk's skeleton — only the candidate rule changes |

## Changed — the control layer

| Code | Change | Owner |
|---|---|---|
| `ReplicationFeeder.TickDeadman` | loses its Replica-promote branch; fence half kept as backstop (herd-count joins the fence condition in 042-1c C-T1/C-T4) | 042-1a (delete) / 042-1c (refine) |
| `HighwayConnectionSource.TryFailoverAsync` | candidate rule: probe-`STATUS`-for-Primary → roster priority order + `HELLO CLIENT` willingness | 042-1b B-T2 |
| `HwReplCommands.HwReplHelloCommand` | gains the `CLIENT` form (willingness handshake) | 042-1a (this feature) |
| `HW.REPL.STATUS` | gains `roster.*` fields | 042-1a (this feature) |

## Deleted — the second failover model (executed here, 2026-09-15)

Removed with this contract feature (pulled forward from C-T7 so protocol doc, code and
conformance land coherently; C-T7 keeps the guard-test half):

- `ReplicaPuller.WitnessLoopAsync` and the witness half of its constructor condition
- `ReplicationFeeder`: `WitnessObserve`, `ReportWitnessAnswer`, `WitnessAllowsPromotion`,
  `NoteWitnessContact`/`LastWitnessContact`, `_witnessSeenByRole`, `PromoteStaggerPerPriority`,
  `EffectivePromoteTimeout`, and `TickDeadman`'s Replica-promote branch
- `HwReplWitnessCommand`: the `<nodeId> <role>` peer-question form (reverts to the bare `+OK`
  probe, arity 1)
- `HighwayReplicationOptions.WitnessServer` (+ host schema/applicator/printer rows)
- Tests: the witness fake-clock pair (`Deadman_WitnessConfigured…`, `Deadman_PrimaryWithWitnessContact…`),
  the timer-promotion test (`Deadman_ReplicaPromotesAfterPromoteTimeout` — replaced by
  `Deadman_SilentReplica_NeverSelfPromotes`), and the pair test `Witness_DefersFence…`

Why deletable: the witness and the stagger existed solely to stop *timer-driven* promotion
from going dual-writable (042 gaps G1/G3). Herd-driven promotion removes the timer;
the standby's own dead master-link inside the willingness predicate absorbs the witness's
tiebreaker role with no third process.

**Superseded 042 gap-fix work** (recorded in
[`implementation-gaps.md`](../../042-replication/implementation-gaps.md), closure notes added
there): the G1 witness protocol amendment and the G3 stagger. G2 (epoch persistence),
G4 (gap refusal), G5 (redirects), G6 (client walk skeleton), G7 (raw gate), G9 (report
content) all carry forward.

## The structural insight (A-R1.2)

Highway's server performs **no autonomous side effects**: acks, dead-letters, reply-slot
writes, sweeps and recurring-job fires all ride client verbs (job firing lives inside
`HW.QCLAIM`). Therefore "no herd ⇒ no master-only effects" (parent R1.3/R6) holds **by
construction** — a node nobody talks to does nothing. The dispatcher gate exists for exactly
one case: a stale client reaching an out-epoched ex-master, answered by `-NOTPRIMARY`.

## Backstop semantics retained (until 042-1c refines)

`T_fence`/`T_promote`/`Margin` and their validation stay; `T_promote` is backstop-legacy
(parent OD4 revisits after harness data). A single node running with `AutoFailover = true`
and no peers still fences itself after `T_fence` — unchanged 042 behaviour, refined by
herd-count in 042-1c.
