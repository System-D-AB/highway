# Feature 039 — Command Port: Design

The port's shape is 037 design §3 (the before/after code) and is not restated. This
file adds the porting order, the substitution table's landing spots, and what "same
behavior" is measured against.

## Porting order — by coupling, not alphabet

| Batch | Commands | Why grouped |
|---|---|---|
| 1 | `HwQSend` | the template (R1) |
| 2 | `HwPublish`, `HwCall`, `HwReply` | producer side: seq allocation, fan-out to group queues, reply slot (`SetEx`) |
| 3 | `HwDequeue`, `HwQClaim` | consumer side: pop, lease rows, promotion, job firing — R6 lives here |
| 4 | `HwAck`, `HwQAck`, `HwFail`, `HwTouch` | drain-filter-restore family (the `ListDrain` idiom) |
| 5 | `HwDlq`, `HwReplay`, `HwStats` | inspection + DLQ restore |
| 6 | `HwJob`, `HwSubscribe`, `HwUnsubscribe`, `HwHeartbeat`, `HwDiscover` + registry/decommission/lease-sweep infrastructure | membership sets, mirrors collapse (R3) here |

Each batch lands with its tests before the next starts — a half-ported command set is
the worst intermediate state, so the intermediate states are whole per family.

## Where the substitutions land

One small `Highway.Server/Commands/Runtime/` set replaces the Garnet types 037 T3.2
lists: an arg-slice reader over the dispatch input, a reply writer over the vendored
`RespWriteUtils` *interface shape* (actual socket wiring is 040's — commands write to
an abstraction, per 037 R10.1), and the clock/lock/store context handed to every
command (`now`, striped lock, `IHighwayStore`). Commands never see Kestrel, RocksDB,
or a pipe.

## What "unchanged behavior" is measured against

Three nets, in order of authority:
1. **Existing command/integration tests** — pass unmodified (except swapping the
   embedded-Garnet fixture for `InMemoryStore`/dispatch harness where the test was
   engine-hosted; those swaps are mechanical and listed in the tasks).
2. **Golden replies** captured from current Garnet behavior *before* porting, for any
   command lacking a reply-shape test.
3. **Mirror equivalence tests** (R3) — seeded, recorded, compared.

The protocol conformance suite stays pointed at a running server and therefore runs in
040/041, not here.

## Risks specific to this feature

| Risk | Mitigation |
|---|---|
| Behavior drift hidden by rewriting a test "to match" | tests are never edited in the same change as the command they cover; fixture swaps are separate commits |
| The drain-filter-restore family reorders survivors | FIFO-preservation asserts in batch 4's tests, seeded with interleaved claims |
| `HwStats` counts drift (list length now a range count) | stats parity test against seeded known state |
