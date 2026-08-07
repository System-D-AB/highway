# Design: Client-Server Communication

> **Protocol reference.** The authoritative definition of the wire protocol —
> commands, replies, errors, keys, framing, invariants — is
> [`docs/HIGHWAY-PROTOCOL.md`](../../HIGHWAY-PROTOCOL.md) (feature 007).
> This document keeps the *reasoning* behind the decisions; that file is the
> reference for *what* the protocol is. Where they differ, that file governs.
## Overview

Feature 005 turns `Highway.Client` into a live wire engine. `HighwayClient` sends `HW.CALL` / `HW.PUBLISH` through a shared StackExchange.Redis connection; per-service worker loops and per-channel consumer loops host this node's catalog; a single backstop sweep makes doorbells a pure latency optimization. Everything speaks the wire contract defined by feature 004 — this design consumes that contract and specifies client-side mechanics only.

**Pinned contract inputs (from 004 + 004.1, authoritative there):**
- `HW.CALL <service> <requestId> <payload>` → `+OK`
- `HW.DEQUEUE <service> <nodeId>` → `[requestId, payload]` | nil — the empty case is a RESP **nil array** (`*-1\r\n`), which SE.Redis surfaces as `RedisResult.IsNull == true` (pinned by 004.1; assert on `IsNull`, not on array length)
- `HW.REPLY <requestId> <payload>` → `+OK`
- `HW.ACK <service> <nodeId> <requestId>` → `+OK` (idempotent)
- `HW.PUBLISH <channel> <payload>` → `:groupCount`
- `HW.SUBSCRIBE <channel> <group>` → `+OK` (idempotent)
- `HW.RECEIVE <channel> <group> [COUNT n]` → array of `[messageId, payload]` pairs (default 10, max 500)
- `HW.RACK <channel> <group> <messageId>` → `+OK` (idempotent)
- Reply slot: plain string key `hw:rep:{requestId}`, TTL 300s, read with stock `GET` / `DEL`
- Doorbells: `hw:door:svc:{service}` (payload = requestId), `hw:door:rep` (payload = requestId), `hw:door:ch:{channel}:grp:{group}` (payload = messageId); best-effort by contract — all three shapes are regression-tested by 004.1's `DoorbellTests`
- Server lease default 5 min; max payload default 1 MiB
- Error contract (004.1): `ERR HW_*` prefix = permanent failure; bare `ERR Transaction failed.` = transient abort (watch conflict) — safe to retry; anything else = permanent

## Architecture

```
src/Highway.Client/
├── HighwayClient.cs                     # IHighwayClient — caller flows (ExecuteAsync, PublishAsync)
├── ServiceCollectionExtensions.cs       # AddHighway — extended: engine + hosted service registration
├── HighwayOptions.cs                    # extended: engine tunables (Req 13)
├── Wire/
│   ├── HighwayEnvelope.cs               # { v, src, ts, body } record + versioning
│   ├── HighwayJson.cs                   # shared JsonSerializerOptions, envelope (de)serialize
│   └── HighwayTransportException.cs     # typed transport failure (publish path)
├── Engine/
│   ├── IHighwayEngine.cs                # StartAsync / StopAsync / State
│   ├── HighwayEngine.cs                 # orchestration: connect → subscribe → loops → backstop
│   ├── HighwayConnection.cs             # owns ConnectionMultiplexer; Execute/Get/Del helpers
│   ├── PendingCallRegistry.cs           # requestId → PendingCall; doorbell completion; sweep surface
│   ├── RpcWorkerLoop.cs                 # one per catalog service
│   ├── ChannelConsumerLoop.cs           # one per catalog channel (with subscribers)
│   ├── DoorbellWatcher.cs               # SUBSCRIBE management for all hw:door:* channels
│   └── BackstopSweeper.cs               # single periodic sweep loop
├── Hosting/
│   └── HighwayEngineHostedService.cs    # IHostedService wrapper over IHighwayEngine
└── Execution/
    └── ServiceExecutor.cs               # (from 003 — unchanged, consumed by worker/consumer loops)
```

## Envelope Design

```json
{ "v": 1, "src": "orders-1", "ts": "2026-08-06T12:34:56.789Z", "body": { "customerId": 42 } }
```

- `v` — envelope schema version; deserializer rejects unknown versions (error path per Req 3 AC7 / Req 6 AC5)
- `src` — sending node's `NodeName`; the audit/tracing hook feature 002 builds on
- `ts` — send timestamp, ISO-8601 UTC
- `body` — the DTO's own JSON, embedded as a nested object (implemented via `JsonNode`: serialize DTO → attach as `body`)

`HighwayJson` owns one `JsonSerializerOptions` instance (`PropertyNameCaseInsensitive = true`, no polymorphic type info — ever). Envelope overhead is ~70 bytes + timestamp; negligible against the 1 MiB payload cap. Client-side size check (Req 2 AC5) measures the final envelope UTF-8 length before `HW.CALL`/`HW.PUBLISH`.

## Caller Flow (ExecuteAsync)

```
App code                HighwayClient        PendingCallRegistry      Server (004)
────────                ─────────────        ───────────────────      ────────────
ExecuteAsync(req, ct) ─▶
                        catalog.GetServiceNameForRequestType(req.GetType())
                        (miss → return 404 response data immediately)
                        env = envelope(req)
                        (oversize → return 413 response data)
                        requestId = Guid "N"
                        Register(requestId, TResponse, timeout, ct) ─▶ pending[requestId] = PendingCall
                        HW.CALL svc requestId env ──────────────────▶ enqueue + doorbell (004)
   ◀── await ──────────  (async wait on PendingCall.Task)
                                              ◀─ hw:door:rep message: requestId
                                              GET hw:rep:{requestId}
                                              complete TCS(response)
                                              DEL hw:rep:{requestId}
   ◀── TResponse ──────
```

### PendingCallRegistry

- `ConcurrentDictionary<string, PendingCall>`; `PendingCall` = { `TaskCompletionSource<Output>` (created with `RunContinuationsAsynchronously`), response `Type`, deadline, registration time }
- **Doorbell path:** `DoorbellWatcher` handler for `hw:door:rep` → registry.TryComplete(requestId): `GET` slot → nil means raced/already-gone (leave to sweep) → else deserialize envelope → complete TCS → `DEL` slot
- **Timeout:** each `PendingCall` arms one `CancellationTokenSource` linked from (a) the caller's token, (b) a timer at `CallTimeout`. On fire: remove from dictionary first (wins races against late doorbells), then complete — timeout → 504 data, caller-cancel → throw `OperationCanceledException` from the awaiting task
- **Late replies** (after timeout/cancel): doorbell/sweep finds no dictionary entry → `DEL` the slot anyway (cleanup), done (Req 3 AC6)
- One reply doorbell subscription per node (registered by `DoorbellWatcher` at engine start), regardless of pending-call count — O(1) channels, not O(calls)

### Concurrency & race rules

- Dictionary-remove-before-complete makes timeout-vs-reply deterministic: whichever removes the entry completes it; the loser no-ops
- `GET`→complete→`DEL` is not atomic with the TCS completion; a crash between GET and DEL leaks the slot until its 300s TTL — acceptable, documented
- Response construction for error paths: `Activator.CreateInstance(TResponse)` + set `StatusCode`/`Error`; response types are validated at scan time to have a public parameterless ctor (Req 12 AC1)

## Worker Loop (RPC Hosting)

One `RpcWorkerLoop` per catalog service, all sharing the one multiplexer.

```
loop(service):
  subscribe hw:door:svc:{service}            (DoorbellWatcher routes wakes)
  while running:
    wait for (doorbell | backstop tick | shutdown)
    drain:
      while HW.DEQUEUE service NodeName returns [requestId, payload]:
        await semaphore (WorkerConcurrency)          # bounds in-flight per service
        _ = Task.Run(process(requestId, payload))    # never blocks the drain
      until nil
    (loop returns to wait)

process(requestId, payload):
  try:
    env = parse envelope(payload)                    # fail → reply 400 + ACK, return
    req = deserialize env.body → catalog[service].RequestType
    result = ServiceExecutor.ExecuteServiceAsync(service, req, ct)   # scope + delegate + 404/500 mapping
    HW.REPLY requestId envelope(result)              # REPLY before ACK — crash between them still delivers
    HW.ACK service NodeName requestId
  catch transport error: back off, retry REPLY/ACK briefly, then log + abandon (server lease recovers)
  finally release semaphore
```

- **Ordering invariant:** `HW.REPLY` strictly before `HW.ACK` (Req 6 AC4). If REPLY succeeds and ACK fails/is lost, the server lease eventually returns the request to the queue → duplicate execution with the reply slot overwritten (last-writer-wins, 004) → at-least-once, consistent with product G2
- **Poison request:** unparseable envelope → `HW.REPLY` with StatusCode 400 envelope (constructed without a request object — envelope carries `ErrorDetail` only, `GenericOutput` semantics) → `HW.ACK`. The caller gets data, never a timeout (Req 6 AC5)
- **Unknown service dequeued** (catalog drift): `ServiceExecutor` already returns 404 data — flows through the normal REPLY path
- **Transport errors mid-drain:** the loop catches, backs off (jittered 100ms → 5s ceiling), retries — loops never die (Req 6 AC6, Req 1 AC3)
- Cancellation of executing services: engine stop passes a linked token; in-flight handlers get `DrainTimeout` to finish (Req 11 AC3)

## Publish Flow

```
PublishAsync(msg, ct)
  channel = catalog.GetChannelNameForMessageType(msg.GetType())
             (miss → throw ChannelNotRegisteredException — local, immediate)
  env = envelope(msg)   (oversize → throw PayloadTooLargeException)
  result = HW.PUBLISH channel env            (transport failure → HighwayTransportException)
  log group count at debug
  return                                     (message durable at this point)
```

Publish has no response object, so its failure paths are the documented exceptions (Req 7 AC2/AC5) — the single intentional asymmetry versus `ExecuteAsync`.

**Amended by 004.1:** `HW.PUBLISH` reads the channel's group mirror in `Prepare`, so it can abort with the bare `ERR Transaction failed.` (watch conflict) under concurrent subscribe/unsubscribe — and an aborted publish delivered **nothing**. `PublishAsync` must therefore retry the transient class (bounded attempts + jittered backoff) before surfacing `HighwayTransportException`. Permanent failures (`ERR HW_*`) throw immediately, never retried.

## Channel Consumer Loop (Pub/Sub Hosting)

At engine start, for each catalog channel with local subscribers: `HW.SUBSCRIBE channel NodeName` (group = NodeName; idempotent per 004).

```
loop(channel):
  subscribe hw:door:ch:{channel}:grp:{NodeName}
  while running:
    wait for (doorbell | backstop tick | shutdown)
    drain:
      repeat:
        entries = HW.RECEIVE channel NodeName COUNT ReceiveBatchSize
        for each [messageId, payload]:
          try: env = parse; msg = deserialize → catalog[channel].MessageType
               await ServiceExecutor.ExecuteSubscribersAsync(channel, msg, ct)   # all subscribers, scopes per invocation
          catch parse failure: log (poison message)
          HW.RACK channel NodeName messageId        # only after dispatch completes (Req 8 AC4)
      until entries.Count < ReceiveBatchSize
```

- **Fan-out model:** group = NodeName → every node gets a copy; within the node all subscribers run (Req 8 AC7). Competing-consumer channels are a future feature, not v1
- **Restart survival:** the group is never unsubscribed (Req 9 AC3); messages published while the node is down sit in the group list and drain on restart — this is exactly product success criterion 2, expressed through the client API
- **Sequential per-message dispatch** within a message (subscribers run one after another inside `ExecuteSubscribersAsync`, v0.8-compatible), messages processed in order; a slow subscriber delays later messages in the batch — documented, matches v0.8 semantics

## DoorbellWatcher

Owns all `ISubscriber.SubscribeAsync` calls against the shared multiplexer:
- `hw:door:rep` → `PendingCallRegistry.TryComplete`
- `hw:door:svc:{service}` per catalog service → signals that service's loop (`Channel`/`SemaphoreSlim` wake)
- `hw:door:ch:{channel}:grp:{NodeName}` per catalog channel → signals that consumer loop
- SE.Redis auto-resubscribes on reconnect (default behavior); the watcher logs subscription confirmations
- `DoorbellsEnabled = false` (test seam) skips all subscriptions — correctness then rides entirely on the backstop (Req 10 AC4)

## Backstop Sweep

One `BackstopSweeper` task, `BackstopInterval` (default 500ms):

```
while running:
  await delay(interval, stopToken)
  1. Pending calls older than grace (1 × interval): GET hw:rep:{id} for each → complete any hits (doorbell-miss recovery)
  2. Signal every worker loop and consumer loop to run a drain pass
```

- Idle cost: dictionary-empty checks + loop signals only — zero network I/O when nothing is pending (Req 10 AC3)
- Doorbells and sweeps may both trigger the same drain — drains are idempotent (`HW.DEQUEUE`/`HW.RECEIVE` return nil/empty when there's nothing)
- The sweeper catches everything internally; it is the engine's heartbeat and must never die (Req 10 AC5)

## Connection Management

- `HighwayConnection` wraps one `ConnectionMultiplexer` built from `HighwayOptions.Server` (`ConfigurationOptions.Parse` first → invalid strings fail fast with a descriptive error, Req 1 AC5)
- Startup: `ConnectAsync` with bounded timeout → failure throws `HighwayServerUnreachableException` naming the endpoint (fail fast, Req 1 AC2)
- Runtime: rely on SE.Redis reconnect; command failures surface to loops as transient → back off/retry; `ExecuteAsync` calls in flight map transport failure to 503 data (Req 5)
- All server access goes through `HighwayConnection` (typed helpers: `CallAsync`, `DequeueAsync`, `ReplyAsync`, `AckAsync`, `PublishCommandAsync`, `SubscribeGroupAsync`, `ReceiveAsync`, `RackAsync`, `GetReplySlotAsync`, `DeleteReplySlotAsync`) so wire shapes live in exactly one class
- `ReceiveBatchSize` maps to `COUNT` (validated 1..500 against the 004 server bound)

## Lifecycle

```
AddHighway(configure)
  ├── (003 pipeline: scan → catalog → DI registration)
  ├── NEW: response-type parameterless-ctor validation during scan (Req 12 AC1)
  └── register: HighwayOptions, ICatalog, ServiceExecutor,
                IHighwayEngine → HighwayEngine (singleton),
                IHostedService → HighwayEngineHostedService,
                IHighwayClient → HighwayClient

Engine start (hosted StartAsync):
  1. HighwayConnection.ConnectAsync            ← fail fast
  2. DoorbellWatcher.SubscribeAll              ← hw:door:rep + per-service + per-group
  3. HW.SUBSCRIBE per catalog channel          ← group = NodeName
  4. start RpcWorkerLoop × services, ChannelConsumerLoop × channels
  5. start BackstopSweeper
  State: Running

Engine stop (hosted StopAsync):
  1. State: Draining — loops stop taking new work
  2. await in-flight up to DrainTimeout (linked CancellationToken cancels handlers at deadline)
  3. stop sweeper, unsubscribe (local only — NO HW.UNSUBSCRIBE sent)
  4. dispose multiplexer — State: Stopped
  Anything still in flight is logged; server lease recovery (004) handles redelivery
```

`IHighwayEngine.State` enum: `NotStarted | Running | Draining | Stopped` (Req 11 AC6). Starting twice throws `InvalidOperationException`; double stop is a no-op (Req 11 AC4).

## Options (HighwayOptions additions)

| Option | Type | Default | Validation |
|---|---|---|---|
| `NodeName` | string | `{entry-assembly-name}-{machine-name}` | non-empty, ≤ 200 chars, no whitespace/control chars |
| `Server` | string | — (required) | parseable by `ConfigurationOptions.Parse` |
| `CallTimeout` | TimeSpan | 30s | > 0 |
| `WorkerConcurrency` | int | 8 | ≥ 1 |
| `ReceiveBatchSize` | int | 10 | 1..500 (004 server bound) |
| `BackstopInterval` | TimeSpan | 500ms | ≥ 50ms |
| `DrainTimeout` | TimeSpan | 10s | > 0 |
| `DoorbellsEnabled` | bool | true | — (test seam) |

All options are snapshotted into the engine at start; later mutation is ignored (documented, Req 13 AC4).

## Error Handling Strategy

| Failure | ExecuteAsync result | PublishAsync | Worker/Consumer loop |
|---|---|---|---|
| Request type not in catalog | 404 data (`SERVICE_NOT_FOUND`) | throws `ChannelNotRegisteredException` | n/a |
| Envelope > max payload | 413 data (`PAYLOAD_TOO_LARGE`) | throws `PayloadTooLargeException` | n/a |
| Transport failure | 503 data (`SERVER_UNAVAILABLE`) | throws `HighwayTransportException` | back off + retry, loop survives |
| Transient server abort — bare `ERR Transaction failed.` (004.1) | retry in-flight send (bounded), else 503 data | bounded retry, then throws | bounded retry with backoff, then log + drop the item |
| Permanent server error — `ERR HW_*` prefix (004.1) | map per code (e.g. payload 413), never retry | throws immediately, never retry | log + drop, never retry |
| Call timeout | 504 data (`CALL_TIMEOUT`) | n/a | n/a |
| Caller cancellation | throws `OperationCanceledException` | throws `OperationCanceledException` | engine stop cancels handlers |
| Malformed response envelope | 502 data (`BAD_REPLY`) | n/a | n/a |
| Malformed request envelope (worker) | n/a | n/a | reply 400 (`BAD_ENVELOPE`) + ACK |
| Poison pub/sub message | n/a | n/a | log + RACK (queue never blocks) |
| Service throws | executor maps to 500 data (003 behavior) | n/a | REPLY the 500, ACK, continue |
| Unexpected engine error | 500 data (`INTERNAL_ERROR`) | throws `HighwayTransportException` | log, continue |

`ErrorDetail.Code` values are stable strings (documented in Abstractions XML docs) so callers can switch on them.

## Threading Model

- All SE.Redis commands are async; no blocking calls anywhere in the engine
- Worker drains run on dedicated async loops; per-request processing hops to the thread pool (`Task.Run`) so a synchronous-heavy handler can't stall draining
- `PendingCallRegistry` completions use `RunContinuationsAsynchronously` — caller continuations never run inside the subscription handler
- Doorbell handlers do minimal work (dictionary lookup + signal); the GET/DEL happens in the registry's completion path
- Per-service `SemaphoreSlim` bounds concurrency; one shared multiplexer means connection pressure is naturally global

## Spikes (small, in-design — no separate research doc needed)

1. ~~**SE.Redis resubscribe-on-reconnect**~~ — **CLOSED (verified 2026-08-06, Task 1 spike):** with default 2.8.24 options, `ConnectionMultiplexer` auto-reconnects after `HighwayTestServer.Restart()` (same port) and automatically re-establishes `SubscribeAsync` handlers — a doorbell published after the restart reached the original subscription with no re-issue logic. `DoorbellWatcher` therefore does NOT need `ConnectionRestored` handling; the reconnect proof lives in `ClientReconnectTests`.
2. ~~**`RedisResult` array parsing for `HW.RECEIVE` pairs**~~ — **CLOSED by 004.1:** `DoorbellTests.HwReceive_ReplyShape_ArrayOfTwoElementPairs` pins the nested-pair shape as a permanent regression test; doorbell delivery for all three channel shapes is likewise pinned there. Only spike 1 remains open.

## Dependencies & Constraints

- New package reference: `StackExchange.Redis` (centrally managed, 2.8.24) added to `Highway.Client` — per coding standards this is the sanctioned RESP client
- `Highway.Abstractions` gains one exception type (parameterless-ctor validation) and stable `ErrorDetail.Code` constants; no other contract changes — 003's public surface is extended, not broken
- Server lease (004, default 5 min) bounds redelivery after engine death; `DrainTimeout` ≪ lease is the intended operating envelope
- 006 will add heartbeat/`HW.DISCOVER` fast-fail: 005's 404-for-unknown-local-type stays local-only; the engine's structure (connection + loops + registry) is exactly where 006 plugs in
- Client never sends `HW.UNSUBSCRIBE` (004 deletes group state on unsubscribe — see Req 9 AC3 rationale)

## Risks

| Risk | Mitigation |
|---|---|
| Duplicate execution after REPLY-but-lost-ACK (lease requeue) | Inherent to at-least-once; documented; handlers own idempotency (product G2 contract) |
| Two live processes sharing a `NodeName` silently share a group | Default is stable-but-unique-per-machine+app; docs + XML comments demand uniqueness per instance (Req 9 AC5); 006 heartbeat will surface the collision |
| Backstop latency noticeable when doorbells drop | Interval configurable; doorbell path is primary — drops are rare (in-process broker) |
| Slow subscriber stalls channel ordering | Documented v0.8-compatible semantics; per-channel loops are independent |
| Multiplexer saturation under heavy mixed traffic | SE.Redis multiplexing is the sanctioned pattern (research §2.5); `WorkerConcurrency` bounds fan-in; dedicated-connection optimization deferred to post-benchmarking |
| Envelope overhead on tiny payloads | ~70 bytes fixed; negligible vs. JSON DTOs; versioning value judged worth it |

## Cross-References

- Requirements: `docs/features/005-client-server-communication/requirements.md`
- Wire contract (authoritative): `docs/features/004-server-hw-commands/design.md`; Garnet verification: `docs/features/004-server-hw-commands/research.md`
- Foundation consumed: `docs/features/003-assembly-scanning/design.md` (catalog, `ServiceExecutor`, `AddHighway`)
- Successors: 006 (heartbeat, `HW.DISCOVER` fast-fail, node pruning), 002 (flight recorder on envelope headers)
