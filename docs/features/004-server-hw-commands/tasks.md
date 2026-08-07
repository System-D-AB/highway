# Tasks: Server HW.* Commands

## Task Dependency Graph

```
T1  (Spike: ephemeral port mechanism)                        ───────────────┐
T2  (HighwayServerOptions config model)                       ─┐            │
T3  (HighwayKeys + Envelope framing)                          ─┤            │
T4  (HighwayGarnetServer + DoorbellBridge) → depends on T3    ─┤            │
T5  (HW.CALL)        → depends on T3, T4                      │            │
T6  (HW.REPLY)       → depends on T3, T4                      │            │
T7  (HW.DEQUEUE + lazy lease sweep) → depends on T3, T4       │            │
T8  (HW.ACK)         → depends on T3, T4                      │            │
T9  (HW.SUBSCRIBE + HW.UNSUBSCRIBE) → depends on T3, T4       │            │
T10 (HW.PUBLISH + backlog) → depends on T3, T4, T9            │            │
T11 (HW.RECEIVE + lazy reap) → depends on T3, T4, T9, T10     │            │
T12 (HW.RACK)        → depends on T3, T4, T11                 │            │
T13 (HighwayServerBuilder + hosting) → depends on T2, T4–T12  │            │
T14 (HighwayTestServer) → depends on T1, T13                  ◀────────────┘
T15 (Validation & error-path tests) → depends on T5–T12
T16 (Integration tests — RPC flow) → depends on T13, T14
T17 (Integration tests — Pub/Sub + backlog + late subscriber) → depends on T13, T14
T18 (Integration tests — competing consumers, restart durability, concurrency) → depends on T13, T14
```

## Tasks

- [x] ### Task 1: Spike — Ephemeral Port Mechanism for Embedded Servers

**Fulfills:** Requirement 2 (informs `HighwayTestServer` design)

**Steps:**
1. Inspect `IGarnetServer` (used by `GarnetServer(GarnetServerOptions, ILoggerFactory, IGarnetServer[], bool)`) and `GarnetServerTcp.Start()` bind path in `libs/garnet`
2. Prototype approach A: Highway-implemented `IGarnetServer` wrapper that binds port 0 and exposes the OS-assigned endpoint
3. Prototype approach B (fallback): `TcpListener(IPAddress.Loopback, 0)` probe → read port → close → pass to Garnet, with a retry loop for reuse races
4. Record the decision + rationale in `design.md` § "Ephemeral port strategy" (replace the spike note with the chosen approach)

**Done criteria:**
- One approach proven to start a Garnet server on an OS-assigned port and yield the actual port programmatically
- Decision documented in design.md with the verified code path

---

- [x] ### Task 2: Highway.Server Configuration Model

**Fulfills:** Requirement 1, 4 (TTL), 7 (lease), 10 (retention), 14 (payload cap)

**Steps:**
1. Create `src/Highway.Server/Internal/HighwayServerOptions.cs`: `Port` (default 6500), `DataDir`, `Lease` (default 5m, `TimeSpan.Zero` = disabled), `ReplySlotTtl` (default 5m), `MaxPayloadBytes` (default 1 MiB), `BacklogRetention` (default 1 day), `MaxBacklogEntries` (default 10,000), `ReceiveDefaultCount` (10), `ReceiveMaxCount` (500), `WaitForCommit` (default false)
2. XML doc comments on every property (project convention)
3. Unit test: defaults are as specified

**Done criteria:**
- Options class compiles, defaults tested, no other code references it yet

---

- [x] ### Task 3: Key Schema and Envelope Framing

**Fulfills:** foundation for Requirements 3–12, 13 (AC4 namespacing)

**Steps:**
1. Create `src/Highway.Server/Internal/HighwayKeys.cs`: static builders for every key and doorbell channel in design § "Key Schema" (`ServiceQueue(service)`, `ServiceProcessing(service, nodeId)`, `ServiceNodes(service)`, `ReplySlot(requestId)`, `ChannelGroups(channel)`, `ChannelSeq(channel)`, `ChannelBacklog(channel)`, `GroupQueue(channel, group)`, `GroupProcessing(channel, group)`, `ServiceDoorbell(service)`, `ReplyDoorbell`, `GroupDoorbell(channel, group)`)
2. Create `src/Highway.Server/Internal/Envelope.cs`: encode/decode for RPC entry, RPC processing entry, channel entry, backlog entry, group processing entry per design § "Entry Framing" (big-endian length/ID/ticks prefixes)
3. Unit tests: round-trip each entry shape; decode rejects truncated/corrupt buffers

**Done criteria:**
- Key builders produce exactly the documented key strings (assertion-locked in tests)
- Every envelope shape round-trips byte-for-byte; corrupt input throws typed exceptions

---

- [x] ### Task 4: HighwayGarnetServer Subclass and DoorbellBridge

**Fulfills:** Requirement 1, 3 (AC6), 4 (AC1), 9 (AC fan-out wake), 11 (doorbell)

**Steps:**
1. Create `src/Highway.Server/HighwayGarnetServer.cs`: `internal sealed class HighwayGarnetServer : GarnetServer` using the `GarnetServer(GarnetServerOptions, ILoggerFactory)` ctor; expose `public SubscribeBroker? SubscribeBroker => storeWrapper.subscribeBroker;`
2. Create `src/Highway.Server/Internal/DoorbellBridge.cs` per design § "DoorbellBridge" (pinned `PinnedSpanByte` via `fixed`, `PublishNow`, null-broker guard)
3. Unit test: server constructs with memory-only options, `SubscribeBroker` non-null, `Dispose` releases without error (no port binding needed for this test — construct only, no `Start`)

**Done criteria:**
- Broker reachable through the subclass using only public/protected Garnet surface (no reflection)
- `DoorbellBridge.Ring` before any subscriber returns 0 without throwing

---

- [x] ### Task 5: HW.CALL Transaction

**Fulfills:** Requirement 3

**Steps:**
1. Create `src/Highway.Server/Commands/HwCallCommand.cs` : `CustomTransactionProcedure`
2. `Prepare`: parse `<service> <requestId> <payload>` via `GetNextArg`, validate (blank checks, payload cap) — write `-ERR` and return false on failure; `AddKey(HighwayKeys.ServiceQueue(service), LockType.Exclusive, StoreType.Object)`
3. `Main`: `ListRightPush(queue, Envelope.RpcEntry(requestId, payload))`, `WriteSimpleString(ref output, "OK")`
4. `Finalize`: `doorbell.Ring(HighwayKeys.ServiceDoorbell(service), requestId)`
5. Command registered in Task 13; until then exercise via test-only registration helper (see Task 13 step 2 note)
6. Unit test of argument validation paths using the test registration helper against an embedded server

**Done criteria:**
- Enqueue appends FIFO; reply `+OK`
- Doorbell observed by a test subscriber after the command
- Malformed inputs produce `-ERR` and leave no state

---

- [x] ### Task 6: HW.REPLY Transaction

**Fulfills:** Requirement 4

**Steps:**
1. Create `src/Highway.Server/Commands/HwReplyCommand.cs`
2. `Prepare`: parse `<requestId> <payload>`, validate; `AddKey(ReplySlot(requestId), LockType.Exclusive, StoreType.Main)`
3. `Main`: `SETEX(slot, payload, options.ReplySlotTtl)` (last-writer-wins), `WriteSimpleString "OK"`
4. `Finalize`: `doorbell.Ring(HighwayKeys.ReplyDoorbell, requestId)`
5. Unit tests: write → stock `GET` returns payload byte-for-byte; double reply overwrites; TTL set (verify via `TTL` command > 0)

**Done criteria:**
- Reply retrievable via stock GET; TTL applied; doorbell rung; overwrite rule deterministic and tested

---

- [x] ### Task 7: HW.DEQUEUE Transaction with Lazy Lease Sweep

**Fulfills:** Requirement 5, 7

**Steps:**
1. Create `src/Highway.Server/Commands/HwDequeueCommand.cs`
2. `Prepare`: parse `<service> <nodeId>`; read `SetMembers(ServiceNodes(service))` via read API; `AddKey` queue, caller's proc list, nodes set, and every discovered proc list (Exclusive, object store)
3. `Main`: lease sweep (skip when `options.Lease == TimeSpan.Zero`) → pop expired entries from each proc list, unwrap, `ListRightPush` to queue tail; then `ListLeftPop(queue)` → nil reply (`WriteNullBulkString`) when empty; else wrap with claim ticks, push to caller proc list, `SetAdd(nodes, nodeId)`, reply `[requestId, payload]` bulk-string array
4. Unit tests: empty queue → nil; FIFO claim order; claim timestamp embedded; sweep requeues only entries older than the lease; sweep disabled leaves entries

**Done criteria:**
- Concurrent dequeues never return the same request (lock serialization verified by test)
- Expired claims return to the queue tail via a subsequent DEQUEUE
- Requirement 5 and 7 acceptance criteria covered by tests

---

- [x] ### Task 8: HW.ACK Transaction

**Fulfills:** Requirement 6

**Steps:**
1. Create `src/Highway.Server/Commands/HwAckCommand.cs`
2. `Prepare`: parse `<service> <nodeId> <requestId>`; `AddKey(ServiceProcessing(service, nodeId), Exclusive, Object)`
3. `Main`: `ListRange` proc list → locate entry with matching requestId → `ListRemove(proc, 1, exactBytes)`; reply `+OK` in all cases (idempotent)
4. Unit tests: ack removes the entry; unknown requestId → `+OK` with no state change; after ack, entry never returned by DEQUEUE-related reads

**Done criteria:**
- Idempotency verified; processing list empty after all dequeued requests acked

---

- [x] ### Task 9: HW.SUBSCRIBE / HW.UNSUBSCRIBE Transactions

**Fulfills:** Requirement 8, 10 (AC2–AC4 copy semantics)

**Steps:**
1. Create `src/Highway.Server/Commands/HwSubscribeCommand.cs`: `Prepare` validates + locks groups set, backlog, group queue; `Main`: `SetAdd(groups, group)`; purge retention-expired backlog head entries; copy remaining backlog entries (as channel entries, IDs preserved) to group queue in order; `+OK`
2. Create `src/Highway.Server/Commands/HwUnsubscribeCommand.cs`: `Main`: `SetRemove(groups, group)`; `DELETE` group queue + group proc; `+OK` (idempotent)
3. Unit tests: double subscribe idempotent; unsubscribe removes queue + proc state; unsubscribe unknown → `+OK`
4. Unit test: late subscriber receives backlog copy; second late subscriber within retention receives the same backlog; expired backlog entries are purged, not copied

**Done criteria:**
- Group membership durable (set state), backlog copy semantics exactly per design, all idempotency paths tested

---

- [x] ### Task 10: HW.PUBLISH Transaction with Backlog

**Fulfills:** Requirement 9, 10 (AC1, AC4, AC5)

**Steps:**
1. Create `src/Highway.Server/Commands/HwPublishCommand.cs`
2. `Prepare`: parse `<channel> <payload>`; read group membership (read API); `AddKey` seq, groups set, backlog, and every group queue (Exclusive)
3. `Main`: `INCR(ChannelSeq(channel))` → messageId; zero groups → append backlog entry (purge expired head, enforce `MaxBacklogEntries` dropping oldest + log warning), reply `:0`; else `ListRightPush` channel entry to every group queue, reply `:count`
4. `Finalize`: ring each group's doorbell with the messageId
5. Unit tests: fan-out reaches all group queues atomically (single transaction observed); reply count correct; zero-group publish lands in backlog; message IDs strictly increasing per channel; payload byte-for-byte

**Done criteria:**
- Requirement 9 fully covered; doorbell per group observed in test; backlog entry cap behavior tested (oldest dropped, warning logged)

---

- [x] ### Task 11: HW.RECEIVE Transaction with Lazy Reap

**Fulfills:** Requirement 11, 12 (AC4)

**Steps:**
1. Create `src/Highway.Server/Commands/HwReceiveCommand.cs`
2. `Prepare`: parse `<channel> <group> [COUNT n]` (default 10, validate 1..`ReceiveMaxCount`); `AddKey` group queue + group proc (Exclusive)
3. `Main`: lazy lease sweep of group proc (expired → re-queue at queue head via `ListLeftPush` reversed to preserve order); pop up to COUNT (`ListLeftPop` loop), wrap with receive ticks, push to proc; reply array of `[messageId, payload]` pairs (empty array when none)
4. Unit tests: batching honors COUNT; FIFO order; received entries move to proc (not returned twice); expired in-flight redelivered head-first; invalid COUNT → `-ERR`

**Done criteria:**
- Requirement 11 covered; redelivery-after-lease tested via manipulated timestamps

---

- [x] ### Task 12: HW.RACK Transaction

**Fulfills:** Requirement 12

**Steps:**
1. Create `src/Highway.Server/Commands/HwRackCommand.cs`: parse `<channel> <group> <messageId>`; `Main` mirrors HW.ACK scan-and-remove on the group proc list; `+OK` idempotently
2. Unit tests: ack removes; unknown → `+OK`; one group's ack leaves other groups' copies untouched (two-group fixture)

**Done criteria:**
- Requirement 12 acceptance criteria all covered

---

- [x] ### Task 13: HighwayServerBuilder, IHighwayServer, and Command Registration

**Fulfills:** Requirement 1, 13 (AC1–AC3)

**Steps:**
1. Create `src/Highway.Server/IHighwayServer.cs` (`Endpoint`, `Start()`, `RunAsync(CancellationToken)`, `IDisposable`, `IAsyncDisposable`) and `src/Highway.Server/HighwayServer.cs` implementing it around `HighwayGarnetServer`
2. Implement `HighwayServerBuilder` fluent API per design § "HighwayServerBuilder", mapping to `GarnetServerOptions` per design table (data-dir ⇒ AOF + storage tier + `Recover`; memory-only otherwise; `DisablePubSub` never set)
3. Registration order guarantee: construct server → `server.Register.NewTransactionProc(...)` for all nine commands (with `RespCommandsInfo` arities: HW.CALL 4, HW.REPLY 3, HW.DEQUEUE 3, HW.ACK 4, HW.PUBLISH 3, HW.SUBSCRIBE 3, HW.UNSUBSCRIBE 3, HW.RECEIVE -3, HW.RACK 4) → only then `Start()` (AOF-replay requirement verified in research)
4. `RunAsync`: `Start()` if not started, then `TaskCompletionSource` on the cancellation token → `Dispose`; structured `ILogger` events: startup config, commands registered, ready (endpoint), shutdown
5. Expose an internal test hook `HighwayServer.RegisterCommands(HighwayGarnetServer, DoorbellBridge, HighwayServerOptions)` reused by `HighwayTestServer` (single registration code path)
6. Update `HighwayServerBuilderTests` to cover builder defaults, memory-only vs data-dir option mapping, start/stop lifecycle on a real port

**Done criteria:**
- Standalone server starts on default port 6500 (or configured), all nine commands answer via raw RESP (e.g., SE.Redis `Execute`)
- Clean shutdown frees the port (restart-on-same-port test passes)
- Requirement 1 acceptance criteria covered

---

- [x] ### Task 14: HighwayTestServer

**Fulfills:** Requirement 2

**Steps:**
1. Create `src/Highway.Server/HighwayTestServer.cs`: builds memory-only `HighwayServerOptions`, applies the Task 1 ephemeral-port mechanism, registers commands via the shared hook, starts on construction (or explicit `Start()` — pick one, document)
2. `ConnectionString` property (`"localhost:{port}"`), `IDisposable` + `IAsyncDisposable`
3. Tests: two concurrent instances get distinct ports and isolated state (a key written via one is invisible to the other); startup under 2 seconds; dispose frees the port for reuse

**Done criteria:**
- Requirement 2 acceptance criteria covered; zero files written to disk (assert temp dir unchanged or Garnet dirs null)

---

- [x] ### Task 15: Validation and Error-Path Tests

**Fulfills:** Requirement 14

**Steps:**
1. Per-command matrix tests via embedded server + SE.Redis `Execute`: wrong arity, blank identifiers, oversized payload, bad COUNT
2. Assert: RESP error replies (not exceptions), no partial state after any rejected command (verify relevant keys absent/unchanged)
3. Test server process never dies on malformed input (all commands survive a full malformed-input barrage)

**Done criteria:**
- Every Requirement 14 acceptance criterion has at least one test; all pass

---

- [ ] ### Task 16: Integration Tests — RPC Flow

> **Un-checked by 004.1:** delivered the round-trip/empty-dequeue/idempotent-ACK/competing-consumer tests, but NOT the lease-redelivery, reply-TTL, or doorbell coverage its steps implied. Completed by 004.1 Requirements 6 and 7 (LeaseRecoveryTests, RetentionTests, DoorbellTests).

**Fulfills:** Requirement 15 (AC3), 3–7 end-to-end

**Steps:**
1. Add `StackExchange.Redis` reference to `Highway.Integration.Tests` (version centrally managed)
2. Fixture: `HighwayTestServer` per test class (IClassFixture)
3. Tests: full round trip `HW.CALL` → `HW.DEQUEUE` → `HW.REPLY` → GET reply slot → `HW.ACK`; timeout-orphan reply slot expires (short-TTL server config); crash-simulation: dequeue without ack → second dequeue after lease (short-lease server config) redelivers; doorbell wakes a subscribed test client (SE.Redis `ISubscriber`)
4. Test naming `Method_Scenario_ExpectedBehavior`

**Done criteria:**
- RPC lifecycle proven end-to-end over real RESP against embedded server; lease recovery path verified

---

- [ ] ### Task 17: Integration Tests — Pub/Sub Flow and Backlog

> **Un-checked by 004.1:** delivered fan-out and late-subscriber tests, but NOT the backlog-retention-window or entry-cap coverage its steps implied. Completed by 004.1 Requirement 7 (RetentionTests).

**Fulfills:** Requirement 15 (AC4, AC6), 8–12 end-to-end

**Steps:**
1. Tests: `HW.SUBSCRIBE` → `HW.PUBLISH` → doorbell observed → `HW.RECEIVE` → `HW.RACK` with multi-group fan-out (two groups get independent copies; one group's RACK does not affect the other)
2. Product success criterion 2 test: publish with zero groups → start subscriber (HW.SUBSCRIBE) → `HW.RECEIVE` returns the earlier message
3. Retention test: backlog entries beyond configured retention/entry cap are not delivered; unsubscribe removes pending state
4. Offline-consumer test: group subscribed but never receiving → messages accumulate → later RECEIVE returns all in order

**Done criteria:**
- Requirement 9–12 behaviors proven end-to-end; product success criterion 2 has a dedicated named test

---

- [ ] ### Task 18: Integration Tests — Competing Consumers, Restart Durability, Concurrency

> **Un-checked by 004.1:** delivered competing-consumers and server-isolation tests, but NOT restart durability (the file named DurabilityTests contained only an isolation test) or the memory-only-loss expectation. Completed by 004.1 Requirement 5 (DurabilityTests rewritten, isolation moved to ServerIsolationTests).

**Fulfills:** Requirement 15 (AC5, AC7), 5 (AC4), 13, 2 (AC5)

**Steps:**
1. Competing consumers: enqueue N (e.g., 1,000) requests; 3+ concurrent dequeue clients; assert union of claims == N with zero duplicates and zero losses; all ACKed → queue + proc lists empty
2. Restart durability: `HighwayServerBuilder` with a temp data dir + AOF → enqueue + publish + subscribe → dispose → rebuild on same dir → `HW.DEQUEUE`/`HW.RECEIVE` return the pre-restart items; subscriptions intact
3. Memory-only mode restart loses state (explicit expectation test, Requirement 13 AC3)
4. Concurrent test servers: two `HighwayTestServer` instances in one process fully isolated (Requirement 2 AC5)

**Done criteria:**
- All durability and load-balancing guarantees of the feature verified by green integration tests; `dotnet test` requires no external infrastructure
