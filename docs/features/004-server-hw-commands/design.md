# Design: Server HW.* Commands

> **Protocol reference.** The authoritative definition of the wire protocol —
> commands, replies, errors, keys, framing, invariants — is
> [`docs/HIGHWAY-PROTOCOL.md`](../../HIGHWAY-PROTOCOL.md) (feature 007).
> This document keeps the *reasoning* behind the decisions; that file is the
> reference for *what* the protocol is. Where they differ, that file governs.
## Overview

Highway.Server hosts a Garnet server in-process (via a project reference to `Garnet.host`) and registers nine custom `HW.*` commands as Garnet **custom transaction procedures**. All queue/subscription state lives in the Garnet store (lists, sets, strings), so Garnet's AOF gives durability for free. Doorbell notifications are emitted from the host layer through Garnet's public `SubscribeBroker`, reached by subclassing `GarnetServer`.

Everything in this design is verified against the pinned Garnet checkout (`libs/garnet`, v2.1.2 + 2 commits). Where older documentation describes APIs (`RegisterCmd`, `CustomCommandRegistry`, `ArgSlice`, `MainAsync`), those no longer exist — this design uses the current API surface exclusively.

## Architecture

```
Highway.Server
├── HighwayGarnetServer : GarnetServer          # exposes storeWrapper.subscribeBroker
├── DoorbellBridge                              # pins bytes, calls SubscribeBroker.PublishNow
├── HighwayServerBuilder                        # options → GarnetServerOptions → build → run
├── HighwayTestServer                           # embedded, memory-only, ephemeral port
├── Commands/                                   # one CustomTransactionProcedure per HW.* command
│   ├── HwCallCommand          (HW.CALL)
│   ├── HwReplyCommand         (HW.REPLY)
│   ├── HwDequeueCommand       (HW.DEQUEUE)
│   ├── HwAckCommand           (HW.ACK)
│   ├── HwPublishCommand       (HW.PUBLISH)
│   ├── HwSubscribeCommand     (HW.SUBSCRIBE)
│   ├── HwUnsubscribeCommand   (HW.UNSUBSCRIBE)
│   ├── HwReceiveCommand       (HW.RECEIVE)
│   └── HwRackCommand          (HW.RACK)
└── Internal/
    ├── HighwayKeys                             # key & doorbell-channel schema
    ├── Envelope                                # binary framing for queue entries
    └── HighwayServerOptions                    # server configuration model
```

### Startup flow

```
HighwayServerBuilder.Build()
    │
    ▼
1. Map HighwayServerOptions → GarnetServerOptions
    │
    ▼
2. new HighwayGarnetServer(opts)          ← constructor completes full init, no Start yet
    │
    ▼
3. Register all nine HW.* transactions via server.Register.NewTransactionProc(...)
   ⚠ MUST happen BEFORE Start() — Start() runs RecoverAsync(), and AOF replay
   re-executes stored-procedure entries through the registered procedures.
    │
    ▼
4. Start()                                 ← listeners up, AOF recovered
    │
    ▼
5. RunAsync(ct) holds until cancellation → Dispose()
```

### Why custom transaction procedures

- **Atomicity:** `HW.PUBLISH` touches N group lists + a sequence counter; `HW.DEQUEUE` sweeps leases across node lists then claims. A `CustomTransactionProcedure` locks all keys in `Prepare` and runs `Main` atomically — no partial fan-out, no double-claim (Requirement 5, 9).
- **Durability:** Garnet logs a transaction to AOF as **one stored-procedure entry** (proc id + arguments) and replays it atomically (Requirement 13). Individual ops inside `Main` are not double-logged (`StoredProcMode` suppresses per-write records).
- **Finalize semantics:** `Finalize` runs post-commit and is **skipped during AOF replay** — the exact right place for doorbells: rung on live traffic, never re-rung during recovery.

## Garnet API Surface Used (verified)

| Need | API | Location in Garnet |
|---|---|---|
| Registration | `RegisterApi.NewTransactionProc(string name, Func<CustomTransactionProcedure> proc, RespCommandsInfo commandInfo)` | `libs/server/Servers/RegisterApi.cs` |
| Transaction shape | `CustomTransactionProcedure` — `Prepare<TGarnetReadApi>`, `Main<TGarnetApi>`, `Finalize<TGarnetApi>` | `libs/server/Custom/CustomTransactionProcedure.cs` |
| Key locking | `AddKey(PinnedSpanByte key, LockType type, StoreType storeType)` in `Prepare` | same |
| Argument parsing | `GetNextArg(ref CustomProcedureInput procInput, ref int idx)` → `PinnedSpanByte` | `libs/server/Custom/CustomProcedureBase.cs` |
| Lists | `ListRightPush(key, element, out count)`, `ListLeftPop(key, out element)`, `ListMove`, `ListRemove`, `ListRange`, `ListLength` (simple `PinnedSpanByte` overloads) | `libs/server/API/IGarnetApi.cs` |
| Sets | `SetAdd`, `SetRemove`, `SetMembers` (used in `Prepare` read phase for group membership) | same |
| Strings/TTL | `SET`, `SETEX(key, value, TimeSpan)`, `EXPIRE`, `DELETE`, `INCR` (sequence counter) | same |
| Reply writing | `WriteSimpleString / WriteError / WriteBulkString / WriteBulkStringArray` helpers on `CustomProcedureBase` writing into `ref MemoryResult<byte> output` | `libs/server/Custom/CustomProcedureBase.cs` |
| Doorbell | `SubscribeBroker.PublishNow(PinnedSpanByte key, PinnedSpanByte value)` — public, thread-safe | `libs/server/PubSub/SubscribeBroker.cs:297` |
| Broker access | `protected StoreWrapper storeWrapper` on `GarnetServer` → `public readonly SubscribeBroker subscribeBroker` | `libs/host/GarnetServer.cs:57`, `libs/server/StoreWrapper.cs:90` |

> **Amendment (004.1) to the Sets row:** `SetMembers` is NOT used in `Prepare`. Reading an object-store set through the transaction read API (`GarnetWatchApi`) registers a WATCH, and the subsequent exclusive lock on the same key aborts the transaction. The implementation instead reads **mirror keys** — main-store strings holding newline-delimited copies: `hw:svc:{service}:nodelist` (mirrors the nodes set, maintained by `HW.DEQUEUE`) and `hw:ch:{channel}:grplist` (mirrors the groups set, maintained by `HW.SUBSCRIBE`/`HW.UNSUBSCRIBE`). Invariant: mirror and set are updated together inside the same transaction; the set remains authoritative. Consequence: mirror-reading commands (`HW.DEQUEUE`, `HW.PUBLISH`, `HW.SUBSCRIBE`, `HW.UNSUBSCRIBE`) can abort transiently on watch conflicts — see the error-contract amendment below.

**Verified absences that shape this design:**
- Custom commands/procedures have **no publish surface** (`IGarnetApi` contains zero pub/sub members; `RespServerSession.subscribeBroker` is private; `InternalsVisibleTo` excludes Highway). → doorbells must be rung from the host layer.
- No public API reads back an OS-assigned port after `Port = 0`. → ephemeral-port strategy in "Embedded Test Server" below.
- `SubscribeBroker` is lazily initialized on first SUBSCRIBE; `PublishNow` before that safely returns 0. `DisablePubSub = true` makes the broker null → Highway must never set it.

## Key Schema

All keys are namespaced under `hw:`. Lists/sets live in the object store; reply slots in the main store.

| Key | Type | Purpose | TTL |
|---|---|---|---|
| `hw:svc:{service}:q` | List | Pending RPC requests (FIFO) | none |
| `hw:svc:{service}:proc:{nodeId}` | List | Requests claimed by node, not yet ACKed | none |
| `hw:svc:{service}:nodes` | Set | Node IDs that hold a processing list (lets `HW.DEQUEUE` sweep all of them lazily) | none |
| `hw:rep:{requestId}` | String | RPC reply slot | configurable (default 300s) |
| `hw:ch:{channel}:groups` | Set | Registered subscriber group names | none |
| `hw:ch:{channel}:seq` | String (counter) | Channel message-ID sequence (`INCR`) | none |
| `hw:ch:{channel}:backlog` | List | Messages published while zero groups registered | bounded by retention policy |
| `hw:ch:{channel}:grp:{group}:q` | List | Pending messages for one group | none |
| `hw:ch:{channel}:grp:{group}:proc` | List | Messages received by group, not yet RACKed | none |

Doorbell channels (RESP pub/sub, rung via `PublishNow`):

| Channel | Rung by | Payload |
|---|---|---|
| `hw:door:svc:{service}` | `HW.CALL` Finalize | `requestId` |
| `hw:door:rep` | `HW.REPLY` Finalize | `requestId` |
| `hw:door:ch:{channel}:grp:{group}` | `HW.PUBLISH` Finalize | `messageId` |

Doorbells are a latency optimization only. Feature 005 pairs them with a slow poll backstop, so a dropped doorbell costs latency, never a message.

## Entry Framing (`Envelope`)

The server stores binary-framed elements; payloads are opaque bytes (server never deserializes user data).

```
RPC queue entry       := [u16 BE requestIdLen][requestId][payload]
RPC processing entry  := [i64 BE claimTicksUtc] + RPC queue entry
Channel entry         := [i64 BE messageId][payload]
Backlog entry         := [i64 BE publishTicksUtc] + Channel entry
Group processing entry:= [i64 BE receiveTicksUtc] + Channel entry
```

- `messageId` is a monotonically increasing `long` from `hw:ch:{channel}:seq` (per-channel unique, Requirement 9 AC3). Wire representation in replies is its decimal string.
- The claim/receive timestamp prefix is what lazy lease-requeue reads (Requirement 7, 12).
- `HW.ACK`/`HW.RACK` match by ID: scan the processing list (`ListRange`), locate the entry whose ID segment matches, remove by exact bytes (`ListRemove`). Processing lists are bounded by worker concurrency, so the scan is cheap.

> **Amendment (004.1):** the implementation does not use `ListRange`+`ListRemove`. It pops the entire processing list (`ListLeftPop(key, int.MaxValue)`), re-pushes every entry except the matched one, and replies `+OK` either way. Cost characteristics: O(n) list rewrites per ack where n = in-flight entries for that node/group (bounded by worker concurrency); each ack performs n+1 list operations rather than one targeted remove. This is the hot-path cost deferred by 004.1's Non-Goals (no optimization before 005 benchmarks exist).

## Command Designs

Every command is a `CustomTransactionProcedure`. `RespCommandsInfo.Arity` (argument count including command name; negative = minimum) gives coarse arity enforcement; `Prepare` performs fine validation and writes RESP errors before any lock is taken (Requirement 14 AC5).

Common rules:
- Blank/empty identifiers (service, channel, group, nodeId, requestId, messageId) → `-ERR ...`, transaction aborted in `Prepare`.
- Payload larger than `MaxPayloadBytes` (default 1 MiB) → `-ERR payload too large`.
- Lock acquisition failure (timeout) surfaces as Garnet's transaction failure reply; clients treat it as retryable (feature 005). No handler ever throws out of the procedure.

> **Amendment (004.1) — validation and locking reality:**
> 1. `Prepare` **cannot write RESP output** — its signature has no output parameter, and returning `false` surfaces only the literal `ERR Transaction failed.` (indistinguishable from a transient abort). Validation therefore never fails `Prepare`: `HighwayCommandBase` captures the error there (adding no keys) and `Main` renders it via `TryWriteError` as its first statement. Verified viable for zero-key transactions by the 004.1 Task 1 spike.
> 2. Validation errors carry stable machine-readable codes — `ERR HW_INVALID_ARG`, `ERR HW_PAYLOAD_TOO_LARGE {actual} > {limit}`, `ERR HW_INVALID_COUNT`, `ERR HW_INTERNAL` — so clients classify from the message alone: **`ERR HW_*` prefix = permanent; bare `ERR Transaction failed.` = transient (retry); anything else = permanent.** Identifiers are additionally rejected when they contain any character below U+0020 or U+007F, or exceed `MaxIdentifierBytes` (default 256) — payloads remain byte-opaque.
> 3. `FailFastOnKeyLockFailure` is left at its default `false`: key locking **blocks** rather than timing out (`LockAllKeys`; `KeyLockTimeout` is never consulted). The only path by which a Highway transaction aborts is **watch-version validation failing** — caused by the mirror-key reads in `Prepare` being modified concurrently (see mirror-keys amendment above).
> 4. Commands whose `Finalize` rings doorbells (`HW.CALL`, `HW.REPLY`, `HW.PUBLISH`) guard it with `if (Failed) return;` — a rejected command must never wake workers.

### HW.CALL `<service> <requestId> <payload>` → `+OK`

| Phase | Action | Keys locked |
|---|---|---|
| Prepare | validate args | — |
| Main | `ListRightPush(hw:svc:{service}:q, RpcEntry(requestId, payload))` | `hw:svc:{service}:q` (Exclusive) |
| Finalize | `doorbell.Ring("hw:door:svc:{service}", requestId)` | — |

AOF: the push is replayed on recovery; the doorbell is not (Finalize skipped) — correct, since no live workers exist mid-recovery.

### HW.REPLY `<requestId> <payload>` → `+OK`

| Phase | Action | Keys locked |
|---|---|---|
| Prepare | validate args | — |
| Main | `SETEX(hw:rep:{requestId}, payload, ReplySlotTtl)` — **last-writer-wins** (deterministic, Requirement 4 AC4); TTL refreshed | `hw:rep:{requestId}` (Exclusive) |
| Finalize | `doorbell.Ring("hw:door:rep", requestId)` | — |

**Retrieval surface for the caller (feature 005):** the reply slot is a plain main-store string — the caller uses stock `GET hw:rep:{requestId}` (then `DEL`) after the doorbell. No extra custom command needed.

### HW.DEQUEUE `<service> <nodeId>` → `[requestId, payload]` | nil

| Phase | Action | Keys locked |
|---|---|---|
| Prepare | read `hw:svc:{service}:nodes` (read API); declare locks for queue, caller's proc list, nodes set, and every discovered proc list | `hw:svc:{service}:q`, `hw:svc:{service}:proc:{nodeId}`, `hw:svc:{service}:nodes`, `hw:svc:{service}:proc:{*}` (all Exclusive) |
| Main | 1. **Lazy lease sweep** (if leases enabled): for each node proc list, pop entries whose `claimTicks` are older than the lease and `ListRightPush` the unwrapped RPC entry back to the queue tail (Requirement 7 AC3). 2. `ListLeftPop(queue)`; if empty → `WriteNull`. 3. Wrap with current `claimTicks`, `ListRightPush` to caller's proc list, `SetAdd(nodes, nodeId)`. 4. Reply `[requestId, payload]`. | as locked |
| Finalize | none | — |

> **Amendment (004.1):** `Prepare` reads the **mirror key** `hw:svc:{service}:nodelist` (main-store string), not the object-store set — see the mirror-keys amendment in "Garnet API Surface Used". The lock set additionally includes the mirror key itself; `Main` appends the node ID to both the set and the mirror together.

Competing consumers (Requirement 5 AC4): exclusive key locks make two concurrent dequeues on the same service serialize; each pops a distinct head element.

### HW.ACK `<service> <nodeId> <requestId>` → `+OK`

Main: `ListRange` the proc list → find entry whose requestId matches → `ListRemove(proc, 1, exactEntryBytes)`. Not found → still `+OK` (idempotent, Requirement 6 AC2).

> **Amendment (004.1):** implemented as pop-all-and-re-push (pop the entire processing list, re-push every entry except the match), not `ListRange`+`ListRemove` — see the framing-section amendment for cost characteristics. Same shape applies to `HW.RACK`.

### HW.SUBSCRIBE `<channel> <group>` → `+OK`

| Phase | Action | Keys locked |
|---|---|---|
| Prepare | validate | — |
| Main | 1. `SetAdd(hw:ch:{channel}:groups, group)` (idempotent). 2. If `hw:ch:{channel}:backlog` is non-empty: purge retention-expired head entries, then **copy** remaining backlog entries (as Channel entries, IDs preserved) to the group queue in order. Backlog is *copied, not drained* — a second late group registering within retention gets the same backlog (Requirement 10 AC3); retention bounds it (AC4). | groups set, backlog, group queue (Exclusive) |
| Finalize | none (nothing new to wake for) | — |

> **Amendment (004.1):** step 2 runs **only when `SetAdd` reports the group was newly added** (its `out saddCount` is 1). The original implementation copied the backlog unconditionally, so a re-subscribe by an already-registered group re-delivered the entire backlog — directly triggered by feature 005's engine, which sends `HW.SUBSCRIBE` on every start. The mirror-key repair (`hw:ch:{channel}:grplist`) stays unconditional so an inconsistent mirror self-heals. A group that unsubscribed and re-subscribes IS new (`HW.UNSUBSCRIBE` removed it from the set and deleted its queue) and legitimately receives the backlog again. `Prepare` also locks the mirror key, and reads it nowhere — the command maintains it in `Main`.

### HW.UNSUBSCRIBE `<channel> <group>` → `+OK`

Main: `SetRemove(groups, group)`; `DELETE` group queue and group proc list. Unknown group → `+OK` (idempotent, Requirement 8 AC4).

### HW.PUBLISH `<channel> <payload>` → `:groupCount`

| Phase | Action | Keys locked |
|---|---|---|
| Prepare | read `hw:ch:{channel}:groups` membership (read API) | then lock: seq, groups set, backlog, and every group queue discovered (Exclusive) |
| Main | 1. `INCR(hw:ch:{channel}:seq)` → messageId. 2. If zero groups: append `BacklogEntry(now, messageId, payload)` (purge expired head first; enforce `MaxBacklogEntries` dropping oldest with a logged warning), reply `0`. 3. Else `ListRightPush` `ChannelEntry(messageId, payload)` into **every** group queue, reply group count. | as locked |
| Finalize | ring `hw:door:ch:{channel}:grp:{group}` with messageId for each group | — |

> **Amendment (004.1):** `Prepare` reads the **mirror key** `hw:ch:{channel}:grplist` (main-store string), not the object-store set — see the mirror-keys amendment. The lock set additionally includes the mirror key. Because of the mirror read, concurrent subscribes/unsubscribes can force a watch-conflict abort of `HW.PUBLISH` — the caller observes the bare `ERR Transaction failed.` and must retry (005's transient class); the message was NOT delivered on that attempt.

Atomic fan-out (Requirement 9 AC1): all pushes happen under one locked transaction — all groups get the message or none do.

### HW.RECEIVE `<channel> <group> [COUNT n]` → array of `[messageId, payload]`

Defaults: `COUNT` = 10, max 500 (invalid values → RESP error, Requirement 11 AC5).

Main: 1. **Lazy lease sweep** of the group proc list: entries with `receiveTicks` older than the lease are re-queued at the group queue **head** (`ListLeftPush`, reversed to preserve order) so they are redelivered first (Requirement 12 AC4). 2. Pop up to `COUNT` from the group queue head; wrap each with `receiveTicks`, push to proc list. 3. Reply array of `[messageId, payload]` pairs (empty array when nothing available, Requirement 11 AC4).

### HW.RACK `<channel> <group> <messageId>` → `+OK`

Main: scan group proc list for the entry with matching messageId → remove by exact bytes. Unknown/already-acked → `+OK` (idempotent, Requirement 12 AC2). One group's ack never touches other groups' copies (AC5) — keys are per-group.

## DoorbellBridge

```csharp
internal sealed class DoorbellBridge(HighwayGarnetServer server)
{
    // PublishNow requires PinnedSpanByte — memory must stay pinned for the call.
    public int Ring(string channel, ReadOnlySpan<byte> payload)
    {
        var broker = server.SubscribeBroker;          // public via subclass
        if (broker is null) return 0;                 // never (DisablePubSub stays false)
        byte[] ch = Encoding.UTF8.GetBytes(channel);
        byte[] body = payload.ToArray();
        fixed (byte* c = ch) fixed (byte* b = body)
            return broker.PublishNow(
                PinnedSpanByte.FromPinnedPointer(c, ch.Length),
                PinnedSpanByte.FromPinnedPointer(b, body.Length));
    }
}
```

Cross-thread broadcast is a supported pattern (Garnet's own background broker consumer does exactly this). If the broker hasn't been initialized yet (no subscriber ever connected), `PublishNow` safely returns 0 — doorbells are best-effort by contract.

## Hosting

### HighwayServerBuilder

```csharp
var server = new HighwayServerBuilder()
    .WithPort(6500)                                    // default 6500
    .WithDataDir("./data")                             // omit → memory-only
    .WithLease(TimeSpan.FromMinutes(5))                // default 5m; TimeSpan.Zero disables lazy requeue
    .WithReplySlotTtl(TimeSpan.FromMinutes(5))         // default 5m
    .WithMaxPayloadBytes(1 << 20)                      // default 1 MiB
    .WithBacklogRetention(TimeSpan.FromDays(1), 10_000)// duration + entry cap
    .WithReceiveDefaults(count: 10, maxCount: 500)
    .Build();                                          // returns IHighwayServer

await server.RunAsync(ct);        // Start + hold until cancellation, then Dispose
string endpoint = server.Endpoint; // "host:port", valid after Build/Start
```

`IHighwayServer` (new public surface in Highway.Server): `Endpoint`, `Start()`, `RunAsync(CancellationToken)`, `IDisposable`/`IAsyncDisposable`.

### GarnetServerOptions mapping

| Highway option | Garnet mapping |
|---|---|
| `WithPort(p)` | `EndPoints = [IPEndPoint(IPAddress.Loopback, p)]` |
| `WithDataDir(dir)` | `EnableStorageTier = true`, `LogDir`/`CheckpointDir` under `dir`, `EnableAOF = true`, `CommitFrequencyMs = 0` (commit per op), `Recover = true` |
| no data dir | `EnableStorageTier = false`, `EnableAOF = false` (Requirement 13 AC3) |
| always | `DisablePubSub = false` (doorbells need it) |

Garnet validation quirks respected (verified): `CommitFrequencyMs`/`WaitForCommit` without `EnableAOF` throws at construction — only set them when a data dir is configured. `WaitForCommit` (strict durability, G2's honest latency tradeoff per research §2.7) is exposed as `WithWaitForCommit(bool)` but defaults off.

### Shutdown

`RunAsync` returns when the token fires; `Dispose` closes listeners (frees the port), drains handlers, disposes the provider and the subscribe broker (verified Garnet dispose phases). Logging (`ILogger`): startup config summary, registration count, ready-with-endpoint, shutdown.

## Embedded Test Server

```csharp
using var server = new HighwayTestServer();          // starts on construction (or Start())
services.AddHighway(o => o.Server = server.ConnectionString);
```

- Memory-only: `EnableStorageTier = false`, `EnableAOF = false`, no disk writes (Requirement 2 AC2).
- Full HW.* command set registered (same registration path as production — one code path).
- `IDisposable` + `IAsyncDisposable`; safe for concurrent instances (Requirement 2 AC5) — each owns its `HighwayGarnetServer` + port.
- Startup budget: `HighwayGarnetServer` ctor + `Start()` without storage tier is well under the 2s target (Garnet's own per-test fixtures do this constantly).

### Ephemeral port strategy

**Decision: Approach B (port probe with retry) — implemented in `EphemeralPort` helper.**

Rationale:
- **Approach A (custom IGarnetServer wrapper)** was inspected: `GarnetServer` accepts `IGarnetServer[] servers` in its `GarnetServerOptions` constructor, and `GarnetServerBase` exposes `public EndPoint EndPoint`. However, the Garnet TCP server (`GarnetServerTcp`) binds the socket in `Start()`, not in the constructor, so there is no way for a wrapper to intercept the OS-assigned port before passing the endpoint array to the `GarnetServer` constructor — the constructor uses its own `EndPoints` to create `GarnetServerTcp` internally if no `servers` array is supplied. Providing a custom wrapper that fully re-implements TCP accept and session management just to read a port was deemed unnecessary complexity.

- **Approach B** is used: `EphemeralPort.Probe()` creates a `TcpListener(IPAddress.Loopback, 0)`, starts it, reads `LocalEndpoint.Port`, stops it, and returns the port. Garnet is then started on that fixed port. The reuse-race window (between `TcpListener.Stop()` and Garnet's `listenSocket.Bind()`) is extremely small on the loopback interface. The helper retries up to 5 times on collision.

Implementation: `src/Highway.Server/Internal/EphemeralPort.cs`

## Sequence Diagrams

### RPC round trip (wire-level, feature 005 consumes this)

```
Caller                    Highway.Server                        Worker node
──────                    ──────────────                        ───────────
HW.CALL svc req-1 {json} ─▶ HwCallCommand
                            Main: RPUSH hw:svc:svc:q
                            Finalize: PublishNow hw:door:svc:svc ◀─ SUBSCRIBE (waiting)
   ◀── +OK                                                  wake ─▶
                                                          HW.DEQUEUE svc node-1 ─▶
                                                             HwDequeueCommand
                                                             Main: lease sweep, LPOP, wrap,
                                                                   RPUSH proc, SADD nodes
                                                          ◀── [req-1, {json}]
                                                          execute AsyncService
HW.REPLY req-1 {result}  ─▶ HwReplyCommand
                            Main: SETEX hw:rep:req-1 (TTL)
                            Finalize: PublishNow hw:door:rep ◀─ SUBSCRIBE (caller waiting)
   ◀── +OK                                                  wake ─▶
GET hw:rep:req-1 (stock) ─▶ {result}
DEL hw:rep:req-1 (stock)
                                                          HW.ACK svc node-1 req-1 ─▶
                                                             LREM from proc list
                                                          ◀── +OK
```

### Durable pub/sub with late subscriber

```
Publisher                 Highway.Server                     Subscriber node
─────────                 ──────────────                     ───────────────
                          (no groups registered yet)
HW.PUBLISH ch {m} ──────▶ HwPublishCommand
                          Main: INCR seq=1, backlog += [ts,1,{m}]
   ◀── :0
                          ... time passes ...
                                                     HW.SUBSCRIBE ch grp-a ─▶
                                                        HwSubscribeCommand
                                                        Main: SADD grp-a;
                                                              backlog → grp-a:q (copy)
                                                     ◀── +OK
HW.PUBLISH ch {m2} ─────▶ HwPublishCommand
                          Main: seq=2, RPUSH grp-a:q
                          Finalize: PublishNow
                            hw:door:ch:ch:grp:grp-a  ◀─ SUBSCRIBE (consumer waiting)
                                                                  wake ─▶
                                                     HW.RECEIVE ch grp-a COUNT 10 ─▶
                                                        Main: sweep, LPOP×2, wrap, proc
                                                     ◀── [[1,{m}],[2,{m2}]]
                                                     process both
                                                     HW.RACK ch grp-a 1 ─▶ +OK
                                                     HW.RACK ch grp-a 2 ─▶ +OK
```

## Error Handling Strategy

| Situation | Behavior |
|---|---|
| Wrong arity | RESP error (RespCommandsInfo + explicit check in `Prepare`) |
| Blank identifier / bad COUNT / payload too large | `-ERR <specific message>`, no state touched |
| Dequeue/Receive on empty/unknown queue | nil / empty array (never an error) |
| ACK/RACK/UNSUBSCRIBE on unknown id | `+OK` (idempotent) |
| Double REPLY same requestId | last-writer-wins overwrite, TTL refreshed |
| Key-lock timeout (contended transaction) | Garnet transaction failure reply; client retries (005) |
| Any unexpected exception inside a procedure | caught, mapped to `-ERR internal`, logged; never escapes the handler (Requirement 14 AC6) |

> **Amendment (004.1) — error contract as implemented:**
>
> | Message | Meaning | Class |
> |---|---|---|
> | `ERR HW_INVALID_ARG <detail>` | identifier blank, control character, over-length, or malformed (e.g. non-numeric messageId) | permanent |
> | `ERR HW_PAYLOAD_TOO_LARGE <actual> > <limit>` | payload above `MaxPayloadBytes` | permanent |
> | `ERR HW_INVALID_COUNT <detail>` | COUNT missing/non-numeric/zero/negative/overflowing/above `ReceiveMaxCount` (distinct detail per case) | permanent |
> | `ERR HW_INTERNAL <detail>` | unexpected exception inside `Main` (was `-ERR internal:`) | permanent — indicates a server bug |
> | `ERR Transaction failed.` | Garnet aborted the transaction — only reachable via watch conflict (locking blocks, never times out) | **transient — retry** |
> | Garnet arity error | wrong argument count, rejected before `Prepare` | permanent |
>
> Client classification rule (005): `ERR HW_*` prefix → permanent; bare `ERR Transaction failed.` → transient; anything else → permanent. No validation failure mutates state (error written in `Main` before any operation; `Prepare` adds no keys on failure). Covered by `ErrorContractTests`.

## Threading & Safety Notes

- `PinnedSpanByte` arguments point to pinned memory; procedures build owned slices via `CreateArgSlice(...)` and never retain spans beyond the call.
- `Main` runs on Garnet's session thread under key locks — keep it allocation-lean and non-blocking (no I/O, no awaits; the API is synchronous).
- Doorbells ring in `Finalize` (post-commit, possibly different scheduling context) — `PublishNow` is verified thread-safe.
- Wall-clock reads inside `Main` (claim/receive ticks, backlog timestamps) are not replay-deterministic: on AOF replay they take the recovery-time clock. This is harmless — timestamps only feed lease comparisons, which reset naturally.
- `hw:svc:{service}:nodes` grows monotonically in this feature; pruning stale nodes is feature 006's heartbeat concern. Lazy sweep cost is bounded by the number of known nodes.

## Dependencies & Constraints

- **No new package dependencies.** Highway.Server already references `Highway.Abstractions` + `Garnet.host` (source project). Integration tests add `StackExchange.Redis` (centrally managed, v2.8.24 — RESP2, sidesteps the SE.Redis 3.x subscription issue flagged in research §2.7).
- Garnet pinned at the submodule commit (v2.1.2 + 2). A Garnet bump can move the APIs used here; re-verify `RegisterApi`, `SubscribeBroker`, `IGarnetApi` list/set signatures on upgrade.
- `DisablePubSub` must remain `false`; `EnableCluster` stays `false` in v1 (key schema is already single-slot-friendly but cluster mode is a non-goal).
- Client-side polling backstop for dropped doorbells is feature 005's responsibility — this feature only guarantees state correctness, doorbell best-effort.

## Risks

| Risk | Mitigation |
|---|---|
| `IGarnetServer` custom wrapper not implementable for port readback | Fallback port-probe with retry (Task 1 spike decides before any dependent work) |
| Backlog copy-on-subscribe duplicates memory for many late groups | Entry cap + retention with logged warnings; metrics hook for 002 |
| Lock-timeout failures under hot-key contention | Clients retry (005); per-service queue keys are only contended by that service's own traffic |
| Garnet internal API drift on submodule bump | All Garnet touchpoints are listed above with file locations; upgrade = re-verify table |
| Lazy lease sweep adds work to DEQUEUE | Sweep reads only processing lists (bounded by concurrency); disabled-able via `WithLease(TimeSpan.Zero)` |

## Cross-References

- Requirements: `docs/features/004-server-hw-commands/requirements.md`
- Garnet extensibility verification (v2.1.2 source-verified): `docs/features/004-server-hw-commands/research.md`
- Successor: 005 (client wire engine: doorbell SUBSCRIBE + poll backstop, GET/DEL reply retrieval, worker loops), 006 (heartbeat/registry, node pruning), 002 (flight recorder hooks per command)
- Research basis: `docs/product/research.md` §2.3–2.5 (Garnet capabilities, doorbell pattern, why not BLPOP)
