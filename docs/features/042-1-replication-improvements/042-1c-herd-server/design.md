# Feature 042-1c — The Herd Server: Design

*Implements the server half of [042-1](../design.md) per the
[contract](../042-1a-contract/design.md). The theme is reuse-and-delete: 042's machinery
carries the mechanics; this feature adds herd state, roster, narration, GOODBYE — and removes
the second failover model.*

## Where it lives

```
Highway.Server/
├── Storage/Rocks/ReplicationFeeder.cs   ← herd state joins role/epoch here; willingness answer;
│     TickDeadman loses its promote branch; witness members deleted
├── Storage/Rocks/ReplicaPuller.cs       ← gains the JOIN announcement on startup; witness loop deleted
├── Resp/RespServer.cs / RespSession.cs  ← authenticated-client session counting (herd size)
├── Resp/CommandDispatcher.cs            ← herd-arrival promotion hook (first accepted verb);
│     gate unchanged otherwise (-NOTPRIMARY, raw-write gate — 042 G5/G7 kept)
├── Commands/Ported/HwReplCommands.cs    ← HELLO CLIENT form; HW.REPL.JOIN; STATUS roster.*;
│     WITNESS reverts to bare probe
├── Storage/Rocks/RosterRecord.cs        ← NEW: versioned roster encode/decode (k repl:roster)
├── Internal/DoorbellBridge / SubscriptionRegistry ← narration publish (hw:door:topology)
└── HighwayReplicationOptions.cs         ← + WillingnessThreshold, GoodbyeDrainTimeout;
      − WitnessServer (obsolete), stagger const
Highway.Server.Host/                      ← `highways --goodbye` verb; schema for new/removed options
```

## Herd state (C-R1)

`RespSession` marks a connection *client* once authenticated and not self-identified as a peer
(a connection whose first post-auth command is `HW.REPL.HELLO` non-CLIENT / `PULL` / `JOIN` is
a peer). `RespServer` keeps the live count; the feeder reads it (`ConnectedClients`) for
stats and for GOODBYE completion. Mastership remains role+epoch (042) — the herd count is an
*input* to promotion and observability, deliberately not a second authority.

Willingness (contract D1) is answered from feeder state: `Options.Priority`, the puller's
`lastMasterExchange` clock (exposed to the feeder as `LastMasterContact` — the standby-side
mirror of `LastPeerContact`), and a `GoodbyeSeen` flag set by a GOODBYE narration/peer notice.

## Herd-arrival promotion (C-R2)

Dispatcher hook, exactly one:

```
if (!feeder.IsWritable && session.WillingAnswered && IsClientVerb(name))
    feeder.TryPromote("herd-arrival");   // 042 path: epoch++ persisted + announcement
```

`session.WillingAnswered` is set by the HELLO CLIENT handler when it answers willing, cleared
on unwilling. The verb then proceeds normally on the now-master (no refusal round-trip for the
first client). Races (two clients' first verbs) collapse inside `TryPromote`'s existing lock —
second caller finds Role already Primary.

## Roster (C-R3)

- `RosterRecord`: `version u64` + members `(nodeId, priority, endpoint)`; encode/decode with
  the 038 record discipline; stored at KV `repl:roster` through the ordinary batch path —
  **WAL-ships to standbys for free**, which is the whole point (parent R13.6 structural).
- `HW.REPL.JOIN`: master-only (non-master refuses `-NOTPRIMARY` → the joiner walks to the
  master like any client would); collision → `ERR HW_PRIORITY_TAKEN priority=<p>
  holder=<nodeId>`; success bumps the version, writes the record, narrates `ROSTER-UPDATE`.
- `ReplicaPuller` startup: after snapshot/tail readiness, announce
  `HW.REPL.JOIN <ReplicaId> <Priority> <SelfEndpoint>` against `PrimaryServer` (retry with
  backoff; a refusal is logged loudly and halts join, not the process — the node still pulls,
  it just is not in the succession).
- Departure pruning (a member gone long enough) is v1-manual (operator edits via a future
  admin verb) — recorded as an OD, not silently invented.

## Narration + GOODBYE (C-R4)

- Publish via the existing `SubscriptionRegistry` on `hw:door:topology`; message grammar from
  the contract (D4). Emission points: roster change (`ROSTER-UPDATE`), master observing peer
  loss (`TOPOLOGY`), GOODBYE.
- **GOODBYE state machine** (feeder-owned):

```
Serving ──goodbye()──► Draining(deadline = now + GoodbyeDrainTimeout)
  Draining: narrate GOODBYE (clients + peers); refuse NEW master-only work
            (-NOTPRIMARY with redirect = successor-unknown → self? no: redirect stays
             computed as today; clients converge by rule, not by the redirect);
            in-flight completes (pending replies, acks)
  Draining ──herd==0 ∨ deadline──► StoodDown (role Demoted-equivalent, same epoch;
            reconciliation report only if an unreplicated tail exists)
```

- Carrier: `HW.REPL.GOODBYE` admin command + `highways --goodbye` host verb wrapping it.
- The successor's epoch+1 happens on *its* herd arrival (C-R2), not here — GOODBYE never
  names or contacts a successor (parent R12.1).

## Deletion (C-R6) — citing the 042-1a map

Removed: `WitnessLoopAsync`, `WitnessObserve`, `ReportWitnessAnswer`,
`WitnessAllowsPromotion`, `PromoteStaggerPerPriority`, the `WITNESS <id> <role>` form
(command reverts to bare `+OK`), `TickDeadman`'s Replica-promote branch, and
`HighwayReplicationOptions.WitnessServer`. Kept: the fence branch, unfence-on-contact,
`T_fence`/`T_promote`/`Margin` validation (`T_promote` documented as backstop-legacy until
parent OD4 revisits). Host schema + schema-completeness test updated in the same commit.

## Testing strategy

| Proof | Requirement |
|---|---|
| Willingness truth table on real feeder state (healthy link → unwilling+redirect; lost ≥ W → willing; priority 0 → never; GOODBYE-seen → willing) | C-R1 |
| Herd-arrival: handshake alone/probe/STATUS do not promote; first client verb does; concurrent first-verbs promote once | C-R2 |
| Roster: record round-trip; JOIN admits + versions + narrates; collision refused naming holder; promoted standby reads roster from own store | C-R3 |
| GOODBYE: drain completes → StoodDown; stuck in-flight → deadline degrade; narration emitted; ignoring client gets -NOTPRIMARY | C-R4 |
| Heal/no-preemption: higher-epoch stand-down + report (042 tests re-run); healthy-link standby stays unwilling when a higher-priority node rejoins | C-R5 |
| One-model assertion: no code path promotes on a timer (reflection/grep-style guard test); fence backstop still fences on fake clock | C-R6 |
