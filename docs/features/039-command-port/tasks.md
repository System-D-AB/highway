# Feature 039 — Command Port: Tasks

```
T1 (runtime types) ──► T2 (template) ──► T3..T7 (batches 2–6, in order) ──► T8 (determinism) ──► T9 (parity sweep)
```

### - [x] T1 — The command runtime

**Fulfills:** R2.1, R4.2 (foundation)
`Commands/Runtime/`: arg reader, reply-writer abstraction, command context
(`now`, striped lock, `IHighwayStore`), dispatch entry usable with no transport.
**Done when:** a toy command dispatches and replies in-process with no socket.

### - [x] T2 — Template: `HwQSend` *(reviewed as the pattern)*

**Fulfills:** R1
**Done when:** ported per the 037 §3 shape; its existing tests pass (fixture swapped
to in-process); golden reply captured pre-port and matched post-port; pattern review
recorded in this file.

> **Pattern review (2026-09-15).** `Commands/Ported/HwQSendCommand.cs` is the shape every
> other command follows. The three-method split maps the old lifecycle exactly:
> - **`Parse(ctx, input)`** ← the old `PrepareCore` *minus* `AddKey`/`StoreType` (there is
>   no lock-set to declare — the per-name lock in `Run` replaces it). Reads args via
>   `TryReadIdentifier`/`TryReadPayload`, validates, captures errors with `Fail` (never
>   throws — validate-in-Main preserved).
> - **`Run(ctx, writer)`** ← the old `Main`. Takes `ctx.Locks.Lock(name)`, opens one
>   snapshot for reads and one batch for writes, decides against `ctx.NowTicks` (the clock
>   read *once*, before the batch — 037 R5.1), commits once. `IGarnetApi.X` →
>   `ctx.Store.X`; `CreateArgSlice(HighwayKeys.X)` → `HighwayKeyspace`/`HighwayNames`;
>   byte accounting via `StoreCommandExtensions.AdjustByteCounter` (delete-at-zero
>   preserved). Tail-push allocates its seq in-batch via `PushTail` (B1-safe under the lock).
> - **`AfterCommit(ctx)`** ← the old `Finalize`. Recorder + doorbell, guarded by
>   `_refusedReason` and `Failed` exactly as before — a rejected or refused run rings nobody.
>
> Reply bytes are golden-matched (`+OK\r\n`, `-ERR HW_QUEUE_FULL …`, `-ERR HW_INVALID_ARG …`)
> in `HwQSendTests` (10 tests). Ported commands live in the `Highway.Server.Commands.Ported`
> namespace so they coexist with the still-live Garnet versions until 040/041 rewire dispatch.

### - [x] T3 — Batch 2: producers (`HwPublish`, `HwCall`, `HwReply`)

**Fulfills:** R2
**Done when:** ported + green incl. fan-out ordering, reply-slot expiry via 038 R3.4,
per-channel seq monotonicity.

### - [x] T4 — Batch 3: consumers (`HwDequeue`, `HwQClaim`)

**Fulfills:** R2, R6
**Done when:** ported + R6.1–R6.3 all green, including the concurrency test and the
culture-bug regression (R2.3) on the promotion path.

### - [x] T5 — Batch 4: drain family (`HwAck`, `HwQAck`, `HwFail`, `HwTouch`)

**Fulfills:** R2
**Done when:** ported + FIFO-preservation asserts green; attempt counting and
dead-letter thresholds byte-match golden replies.

### - [x] T6 — Batch 5: inspection (`HwDlq`, `HwReplay`, `HwStats`)

**Fulfills:** R2
**Done when:** ported; stats parity test against seeded state green (list length as
range count).

### - [x] T7 — Batch 6: membership + mirrors (`HwJob`, `HwSubscribe`, `HwUnsubscribe`, `HwHeartbeat`, `HwDiscover`, registry/decommission/lease sweep)

**Fulfills:** R2, R3
**Done when:** ported on plain sets; **every former mirror reader's equivalence test
green** (recorded-from-Garnet answers); `DeleteRange` used for retirement paths.

> **Done (2026-08-29).** Ported the five commands plus `Commands/Ported/RegistrySupport.cs`
> — the mirror-collapse machinery (`RequeueNodeWork`, `RemoveNodeFromService`,
> `RemoveRegistration`, `RemoveFromServiceIndex`, `RetireGroup`, `ReadNodeChannels`) rewritten
> on `IHighwayStore`. Every Garnet Main-store mirror (`reg:nodes`, `grplist`, `job:index`,
> `grp:members`, `node:subs`, `node:channels`, `svc:{s}:nodelist`) is now a plain `s`-family
> set read with `SetMembers` (R3.2). Retirement uses one `DeleteRange` per structure instead
> of a per-key DELETE loop (C4.6). The three deferred pieces landed here too: `HwPublish`
> auto-retirement of dead groups (with `NodeSuspect`/`GroupRetired` recording), `HwQClaim`
> `FireDueJobs` (fire + atomic re-arm, catch-up-one, backpressure refusal), and `HwDequeue`
> `SweepDeadNodes` (stale-registration prune + requeue). All clocks are `ctx.NowTicks`
> (037 R5.1). `MembershipTests` — 25 tests — covers each mirror's equivalence, both BYE and
> BYE PURGE (sole-member vs sibling), stale/live discovery filtering, job SET/DEL/LIST +
> firing + re-arm, publish auto-retirement (including the "no record ≠ dead" rule), and the
> dead-node requeue. All 78 `Commands` tests green. 18 ported files cover all 23 HW.* names.

### - [x] T8 — Determinism enforcement + crash test

**Fulfills:** R5
**Done when:** no-clock-in-batch analyzer test green over the command assembly; sweep
persists rows not intent; kill-mid-claim → byte-identical recovery.

> **Done (2026-08-29).** `DeterminismTests` — 6 tests over the three R5 acceptance criteria.
> **R5.1:** `NoPortedCommand_ReadsAWallClock` scans every `.cs` under
> `Commands/Ported/`, strips comments and string/char literals, and asserts no
> `DateTime.UtcNow`/`.Now`, `DateTimeOffset.UtcNow`/`.Now`, `Environment.TickCount` or
> `Stopwatch` token survives in code (the one `DateTime.UtcNow` in HwJob's doc comment is
> stripped — a companion test proves the sanctioned `new DateTime(ctx.NowTicks, …)` is not
> flagged). **R5.2:** `LeaseSweep_PersistsProducedRows_NotAnIntentMarker` decodes the actual
> dead-letter record the sweep wrote (its `deadAtTicks` is the sweep's clock value — a
> persisted fact) and `LeaseSweep_UnderLimit_RequeuesTheActualEntry` re-claims the materialised
> requeued row. **R5.3:** `ClaimIsOneBatch_TwoRuns_YieldByteIdenticalState_InMemory` and
> `…_OnRocksDb` run the identical claim sequence twice from a fixed clock and assert the full
> keyspace dump is byte-identical — because the claim commits exactly one batch, a mid-claim
> crash can only leave the pre- or post-claim state, both deterministic. Added
> `InMemoryStore.DumpData()` mirroring `RocksDbStore.DumpData()`. All 84 `Commands` tests green.

### - [x] T9 — Parity sweep + in-process suite *(039's exit)*

**Fulfills:** R2.2, R4
**Done when:** all existing command tests green with only listed fixture swaps; no
`Garnet.*`/`Tsavorite.*` in any command file (assert); the R4.1 behavior suite
(sweep, attempts, DLQ, promotion, jobs, bytes) runs end-to-end on `InMemoryStore`
with no socket.

> **Done (2026-08-29).** `ParityTests` — 8 tests. **R2.2 (no engine type):**
> `NoPortedCommand_ReferencesGarnetOrTsavorite` strips comments/strings from every
> `Commands/Ported/*.cs` and asserts no `using Garnet`/`using Tsavorite` directive and no
> `Garnet.`/`Tsavorite.` qualified reference survives in code (the "Ported from Garnet" prose
> in doc comments is stripped first — those are the only remaining mentions). Scoped to
> `Ported/`; the still-live Garnet command files are 040/041's to delete.
> `EveryHwCommandName_HasAPortedImplementation` pins the 18-file / 23-name coverage.
> **R4.1 behavior suite, in-process on `InMemoryStore`, no socket:** send→claim→ack round-trip
> with byte accounting, delayed-message promotion, lease-sweep redelivery→dead-letter with
> attempt counting, DLQ peek→requeue, job fire-on-claim + re-arm, and pub/sub fan-out→subscriber
> claim — each driven through the real ported commands. All 92 `Commands` tests green; the full
> `Highway.Server.Tests` suite (399 tests) green.

---

## Feature 039 complete

All 23 `HW.*` commands run on `IHighwayStore` with no Garnet/Tsavorite type in any ported
command. The wire replies are byte-identical (R1), every command commits exactly one batch
from a clock read once (R4/R5), the seven Main-store mirrors collapsed into the sets they
mirrored (R3), and the claim path's contention/promotion/fire/sweep semantics are proven on
real parallelism (R6). The ported commands coexist with the live Garnet versions in the
`Highway.Server.Commands.Ported` namespace; **040 (resp-server)** rewires dispatch onto them
and **041 (garnet-removal)** deletes the originals.

### Addendum (2026-09-15, from the 040 fixture swap)

Three Finalize-contract deviations survived the 039 suite and were caught when 040 put the
full integration suite on the ported commands; all fixed in the ported code:

1. `HighwayCommand.Execute` skipped `AfterCommit` on validation failure — rejected commands
   vanished from the flight recorder (the Garnet Finalize always ran).
2. `HwDequeueCommand` dropped the `RpcClaimed` event.
3. `HwQClaimCommand` dropped the `QueueClaimed` event (its port note wrongly claimed Garnet
   recorded nothing on a claim).

Lesson recorded: replay-visible side effects are wire-invisible, so R1's byte-identical-reply
check cannot catch a missing recorder event — parity for `AfterCommit` needs recorder
assertions, which the integration suite supplied.
