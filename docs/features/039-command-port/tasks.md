# Feature 039 — Command Port: Tasks

```
T1 (runtime types) ──► T2 (template) ──► T3..T7 (batches 2–6, in order) ──► T8 (determinism) ──► T9 (parity sweep)
```

### - [ ] T1 — The command runtime

**Fulfills:** R2.1, R4.2 (foundation)
`Commands/Runtime/`: arg reader, reply-writer abstraction, command context
(`now`, striped lock, `IHighwayStore`), dispatch entry usable with no transport.
**Done when:** a toy command dispatches and replies in-process with no socket.

### - [ ] T2 — Template: `HwQSend` *(reviewed as the pattern)*

**Fulfills:** R1
**Done when:** ported per the 037 §3 shape; its existing tests pass (fixture swapped
to in-process); golden reply captured pre-port and matched post-port; pattern review
recorded in this file.

### - [ ] T3 — Batch 2: producers (`HwPublish`, `HwCall`, `HwReply`)

**Fulfills:** R2
**Done when:** ported + green incl. fan-out ordering, reply-slot expiry via 038 R3.4,
per-channel seq monotonicity.

### - [ ] T4 — Batch 3: consumers (`HwDequeue`, `HwQClaim`)

**Fulfills:** R2, R6
**Done when:** ported + R6.1–R6.3 all green, including the concurrency test and the
culture-bug regression (R2.3) on the promotion path.

### - [ ] T5 — Batch 4: drain family (`HwAck`, `HwQAck`, `HwFail`, `HwTouch`)

**Fulfills:** R2
**Done when:** ported + FIFO-preservation asserts green; attempt counting and
dead-letter thresholds byte-match golden replies.

### - [ ] T6 — Batch 5: inspection (`HwDlq`, `HwReplay`, `HwStats`)

**Fulfills:** R2
**Done when:** ported; stats parity test against seeded state green (list length as
range count).

### - [ ] T7 — Batch 6: membership + mirrors (`HwJob`, `HwSubscribe`, `HwUnsubscribe`, `HwHeartbeat`, `HwDiscover`, registry/decommission/lease sweep)

**Fulfills:** R2, R3
**Done when:** ported on plain sets; **every former mirror reader's equivalence test
green** (recorded-from-Garnet answers); `DeleteRange` used for retirement paths.

### - [ ] T8 — Determinism enforcement + crash test

**Fulfills:** R5
**Done when:** no-clock-in-batch analyzer test green over the command assembly; sweep
persists rows not intent; kill-mid-claim → byte-identical recovery.

### - [ ] T9 — Parity sweep + in-process suite *(039's exit)*

**Fulfills:** R2.2, R4
**Done when:** all existing command tests green with only listed fixture swaps; no
`Garnet.*`/`Tsavorite.*` in any command file (assert); the R4.1 behavior suite
(sweep, attempts, DLQ, promotion, jobs, bytes) runs end-to-end on `InMemoryStore`
with no socket.
