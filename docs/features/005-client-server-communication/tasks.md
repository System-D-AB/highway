# Tasks: Client-Server Communication

> **Gating note:** implementation starts only after feature 004 is merged (needs `HighwayTestServer` and the registered `HW.*` commands). The spec is written against 004's design contract, so nothing here needs re-deriving when 004 lands — re-read `004/design.md` § Command Designs first.

## Task Dependency Graph

```
T1  (Spikes: SE.Redis resubscribe + HW.RECEIVE result parsing)
T2  (HighwayOptions extensions + stable NodeName)
T3  (Response-type parameterless-ctor scan validation)          [independent]
T4  (HighwayConnection)                    → depends on T2
T5  (PendingCallRegistry)                  → depends on T4
T6  (DoorbellWatcher)                      → depends on T1, T4, T5
T7  (RpcWorkerLoop)                        → depends on T1, T4
T8  (ChannelConsumerLoop)                  → depends on T1, T4
T9  (BackstopSweeper)                      → depends on T5, T7, T8
T10 (HighwayClient — ExecuteAsync/PublishAsync) → depends on T4, T5
T11 (HighwayEngine + hosted service + AddHighway wiring) → depends on T2–T10
T12 (Integration tests — RPC end-to-end)   → depends on T11, 004 merged
T13 (Integration tests — Pub/Sub end-to-end) → depends on T11, 004 merged
T14 (Integration tests — resilience & lifecycle) → depends on T11, 004 merged
```

## Tasks

- [x] ### Task 1: Spike — SE.Redis Resubscribe-on-Reconnect

**Fulfills:** de-risks Requirements 3, 6, 8 (design § "Spikes")

> **Amended by 004.1:** the `HW.RECEIVE` nested-pair shape and all three doorbell deliveries are now permanent tests in 004.1's `DoorbellTests` (Req 7 AC5/AC7) — both former spikes are closed. Only the reconnect question remains open.

**Steps:**
1. Throwaway test against `HighwayTestServer`: subscribe via `ISubscriber`, restart the server via `HighwayTestServer.Restart()` (stable port), verify resubscription behavior with SE.Redis 2.8.24 defaults; note whether the `ConnectionRestored` event is needed for `DoorbellWatcher`
2. Record the outcome in `design.md` § "Spikes" (replace the open item with confirmed behavior + chosen approach)

**Done criteria:**
- Reconnect/resubscribe behavior empirically confirmed; design updated; throwaway test either promoted to the integration suite or deleted

---

- [x] ### Task 2: HighwayOptions Extensions and Stable NodeName

**Fulfills:** Requirement 2 (size check inputs), 9, 12, 13

**Steps:**
1. Add to `HighwayOptions`: `WorkerConcurrency` (8), `ReceiveBatchSize` (10), `BackstopInterval` (500ms), `DrainTimeout` (10s), `DoorbellsEnabled` (true) — XML docs per convention
2. Change `NodeName` default from random to stable: `{entry-assembly-name}-{machine-name}` (entry assembly null-safe fallback); document uniqueness-per-live-process requirement in XML docs. **Amended by 004.1:** `NodeName` becomes the pub/sub group name and the processing-list owner on the server — its validation is the client half of server identifier safety (004.1 Req 3), not a cosmetic rule: the server rejects identifiers containing control characters or exceeding 256 bytes with `ERR HW_INVALID_ARG`, so an invalid `NodeName` must fail locally at startup, never at first traffic
3. Create `HighwayOptionsValidator` (internal): validates all options per design § "Options" table — descriptive messages naming the offending value; `NodeName` rules mirror the server's `Identifier` rules (non-empty, ≤ 256 bytes, no character below U+0020, no U+007F)
4. Unit tests: defaults, stable NodeName shape, every validation rule (positive/negative)

**Done criteria:**
- All new options present with defaults; validation throws descriptive errors; existing tests unaffected (NodeName default change may require fixing tests that assumed randomness)

---

- [x] ### Task 3: Response-Type Parameterless-Constructor Validation

**Fulfills:** Requirement 12 (AC1)

**Steps:**
1. Add `ResponseTypeRequiresParameterlessConstructorException` to `Highway.Abstractions.Exceptions` (match existing exception style/constructors)
2. Extend `DefaultTypeScanner.DiscoverServices`: after existing validations, verify `responseType` has a public parameterless constructor; throw the new exception otherwise
3. Unit tests in `TypeScannerTests`: response type without parameterless ctor throws the typed exception; with one passes

**Done criteria:**
- Fail-fast at `AddHighway` for response types that could not be constructed for timeout/error responses; full test coverage of both paths

---

- [x] ### Task 4: HighwayConnection

**Fulfills:** Requirement 1

**Steps:**
1. Add `StackExchange.Redis` package reference to `Highway.Client.csproj` (version centrally managed)
2. Create `src/Highway.Client/Wire/HighwayTransportException.cs` and `Engine/HighwayServerUnreachableException.cs`
3. Create `src/Highway.Client/Engine/HighwayConnection.cs`: wraps one `ConnectionMultiplexer`; `ConnectAsync` (parse config first → descriptive error for invalid strings; bounded connect timeout → `HighwayServerUnreachableException` naming the endpoint); typed helpers for every wire operation: `CallAsync`, `DequeueAsync`, `ReplyAsync`, `AckAsync`, `PublishCommandAsync`, `SubscribeGroupAsync`, `ReceiveAsync`, `RackAsync`, `GetReplySlotAsync`, `DeleteReplySlotAsync`, `SubscribeDoorbellAsync(channel, handler)`; `Dispose`/`DisposeAsync`. **Amended by 004.1:** helpers classify RESP errors by the server contract — message starting with `ERR HW_` ⇒ permanent failure (never retry); bare `ERR Transaction failed.` ⇒ transient (bounded retry with backoff); anything else ⇒ permanent. Expose this as a typed distinction (e.g. `HighwayTransientException` vs `HighwayTransportException`) so loops and callers can branch on it
4. Unit tests (NSubstitute where possible; real `HighwayTestServer` for connect/invalid-config paths — allowed since 004 provides it): invalid config string, unreachable endpoint, each helper's command name/args match the 004 contract exactly, error classification for both classes

**Done criteria:**
- All wire shapes live in exactly one class; command names/argument orders verified against `004/design.md`; fail-fast startup behavior tested

---

- [x] ### Task 5: PendingCallRegistry

**Fulfills:** Requirement 3 (AC3–AC9), 4 (AC1–AC4), 5 (timeout rows)

**Steps:**
1. Create `src/Highway.Client/Engine/PendingCallRegistry.cs`: `Register(requestId, responseType, timeout, callerToken)` → returns awaitable task; `TryCompleteFromSlot(requestId)` (GET → deserialize envelope → complete → DEL; nil → leave to sweep); `SweepExpiredSlots(grace)` for the backstop; internal timeout via linked CTS (timer + caller token); dictionary-remove-before-complete race rule
2. Timeout completion constructs the response via `Activator.CreateInstance(responseType)` + StatusCode 504 + `ErrorDetail{ Code = "CALL_TIMEOUT" }`; caller cancellation completes with `OperationCanceledException`
3. Late completion (no entry): still `DEL` the slot, no throw
4. Unit tests with mocked `HighwayConnection`: concurrent registration/completion, timeout → 504, cancellation → OCE, late-reply cleanup, double-complete safety, response construction for a sample Output type

**Done criteria:**
- Every Requirement 3/4 acceptance criterion exercised in unit tests; no path throws for service-level outcomes

---

- [x] ### Task 6: DoorbellWatcher

**Fulfills:** Requirement 3 (AC4), 6 (AC2 wake), 8 (AC2 wake), 13 (`DoorbellsEnabled` seam)

**Steps:**
1. Create `src/Highway.Client/Engine/DoorbellWatcher.cs`: subscribes `hw:door:rep` → `PendingCallRegistry.TryCompleteFromSlot`; per-service `hw:door:svc:{service}` → registered wake callbacks; per-group `hw:door:ch:{channel}:grp:{group}` → registered wake callbacks; honors `DoorbellsEnabled == false` by subscribing nothing
2. Handlers do minimal work (lookup + signal); completion I/O stays in the registry/loops
3. If Task 1 spike showed reconnect needs it: re-issue subscriptions on `ConnectionRestored`
4. Unit tests: wake routing to the right callback per channel shape; disabled mode subscribes nothing; handler exceptions don't kill the watcher

**Done criteria:**
- One reply-doorbell subscription per node regardless of pending-call count; wake signals delivered to registered loops; test seam verified

---

- [x] ### Task 7: RpcWorkerLoop

**Fulfills:** Requirement 6

**Steps:**
1. Create `src/Highway.Client/Engine/RpcWorkerLoop.cs` per design § "Worker Loop": wait (wake signal | backstop tick | stop) → drain `HW.DEQUEUE` until nil → per item: semaphore gate → `Task.Run` process
2. Process path: parse envelope (fail → `HW.REPLY` 400 `BAD_ENVELOPE` + `HW.ACK`) → deserialize body via catalog `RequestType` → `ServiceExecutor.ExecuteServiceAsync` → `HW.REPLY` **then** `HW.ACK`. **Amended by 004.1 (retry policy):** retry ONLY the transient class (bare `ERR Transaction failed.` — watch-conflict aborts) with jittered backoff 100ms→5s and a bounded attempt count; permanent failures (`ERR HW_*`) are logged and dropped — never retried, so the loop cannot spin forever on a poisoned request. The loop itself never dies from either class
3. Wire wake input: `Channel`/`SemaphoreSlim` signal from `DoorbellWatcher` + backstop + stop token
4. Unit tests (mocked connection + real catalog/executor fixtures): REPLY-before-ACK order assertion, poison envelope → 400+ACK, exception isolation between requests, concurrency bound respected, drain-until-nil behavior, transient error retried up to the bound then dropped, permanent error never retried

**Done criteria:**
- Requirement 6 acceptance criteria covered; no test can kill the loop; ordering invariant asserted

---

- [x] ### Task 8: ChannelConsumerLoop

**Fulfills:** Requirement 8

**Steps:**
1. Create `src/Highway.Client/Engine/ChannelConsumerLoop.cs` per design § "Channel Consumer Loop": startup `HW.SUBSCRIBE channel NodeName` happens at engine level (Task 11); loop: wait → drain `HW.RECEIVE COUNT ReceiveBatchSize` until short batch → per message: parse/dispatch via `ServiceExecutor.ExecuteSubscribersAsync` → `HW.RACK` after dispatch completes; poison message → log + RACK. **Amended by 004.1 (retry policy):** same classification as Task 7 — bounded retry on the transient class only; permanent errors logged and dropped
2. Unit tests: RACK-only-after-dispatch ordering, poison message acked without dispatch, short-batch drain termination, transient transport error → bounded backoff + continue, permanent error → logged and dropped, subscriber failure doesn't block siblings or RACK

**Done criteria:**
- Requirement 8 acceptance criteria covered in unit tests

---

- [x] ### Task 9: BackstopSweeper

**Fulfills:** Requirement 10

**Steps:**
1. Create `src/Highway.Client/Engine/BackstopSweeper.cs`: single loop at `BackstopInterval`; sweep pending calls older than grace via registry; signal all registered loops' drain passes; zero network I/O when idle; catches all internal errors
2. Register loops' signal delegates during engine composition (Task 11)
3. Unit tests (fake time where practical): idle sweep performs no connection calls; aged pending call triggers slot GET; loops signaled each pass; internal exception doesn't stop the sweeper

**Done criteria:**
- Requirement 10 acceptance criteria covered; sweeper proven harmless when idle

---

- [x] ### Task 10: HighwayClient — ExecuteAsync and PublishAsync

**Fulfills:** Requirement 2 (AC5), 3, 4, 5, 7

**Steps:**
1. Replace the `NotImplementedException` stubs in `HighwayClient.cs`: `ExecuteAsync` per design § "Caller Flow" (catalog reverse lookup → 404 data on miss; envelope + size check → 413 data; `HW.CALL`; registry await); `PublishAsync` per design § "Publish Flow" (lookup miss → typed exception; oversize → typed exception; transport failure → `HighwayTransportException`). **Amended by 004.1:** `PublishAsync` **must** retry the transient class (bounded) — a watch-conflicted `HW.PUBLISH` delivered nothing, and silently surfacing that as failure would lose a message the caller believes was sent; permanent failures throw immediately
2. Use catalog's `GetServiceNameForRequestType` / `GetChannelNameForMessageType` (no attribute reflection)
3. Transport failure on send for `ExecuteAsync` → 503 data with `SERVER_UNAVAILABLE`
4. Unit tests (mocked connection/registry): every Requirement 5 mapping row; oversize paths; catalog-miss paths; requestId uniqueness across concurrent calls

**Done criteria:**
- No stub remains; full error-mapping table (design § "Error Handling Strategy") green in unit tests

---

- [x] ### Task 11: HighwayEngine, Hosted Service, and AddHighway Wiring

**Fulfills:** Requirement 11, 12 (AC2–AC4), 13 (AC4)

**Steps:**
1. Create `src/Highway.Client/Engine/IHighwayEngine.cs` (`StartAsync`, `StopAsync`, `State`) and `HighwayEngine.cs`: start order connect → doorbells → `HW.SUBSCRIBE` channels → loops → sweeper; stop order per design § "Lifecycle" (drain with linked token, no `HW.UNSUBSCRIBE` ever); state machine `NotStarted | Running | Draining | Stopped`; double-start throws, double-stop no-ops; snapshot options at start
2. Create `src/Highway.Client/Hosting/HighwayEngineHostedService.cs` (`IHostedService` delegating to `IHighwayEngine`)
3. Extend `ServiceCollectionExtensions.AddHighway`: register `ServiceExecutor` (if not already), `IHighwayEngine` singleton, `IHostedService` wrapper; run option validation (Task 2 validator) after the existing Server-required check
4. Update/extend unit tests: start/stop ordering (mocked collaborators, verify call sequence), state transitions, hosted service delegation, double-start/stop semantics
5. Verify full solution build + all existing tests still pass

**Done criteria:**
- Engine composes all components in the documented order; `AddHighway` yields a host-ready engine; Requirement 11/12/13 acceptance criteria covered

---

- [x] ### Task 12: Integration Tests — RPC End-to-End

**Fulfills:** Requirement 14 (AC2 RPC items), 3–6 end-to-end

**Steps:**
1. Test fixture: `HighwayTestServer` + two DI-built engines (caller node with no services; service node hosting test services) via real `AddHighway` → `IHostedService` start
2. Tests: full round trip returns typed response data; unknown service on caller side → 404 data without network; slow service + short `CallTimeout` → 504 data; two service engines hosting the same service: 100 concurrent calls partition with zero duplicates, all correct responses; caller-cancel mid-flight → `OperationCanceledException`; 100 concurrent calls correlate correctly
3. Naming `Method_Scenario_ExpectedBehavior`; no external infrastructure

**Done criteria:**
- All RPC integration items of Requirement 14 green against the real 004 server

---

- [x] ### Task 13: Integration Tests — Pub/Sub End-to-End

**Fulfills:** Requirement 14 (AC2 pub/sub items), 7–9 end-to-end

**Steps:**
1. Tests: publisher engine → subscriber engine delivers to all local subscribers; two subscriber nodes each receive their own copy (per-node groups); publish before subscriber engine starts → message delivered after it starts (product success criterion 2 through the client API); subscriber exception doesn't block siblings or later messages
2. Restart-resume test: subscriber engine stops (graceful), publish while down, restart with same `NodeName` → pending message drains (group never unsubscribed)

**Done criteria:**
- All pub/sub integration items of Requirement 14 green; late-subscriber and restart-resume scenarios have dedicated named tests

---

- [x] ### Task 14: Integration Tests — Resilience and Lifecycle

**Fulfills:** Requirement 14 (AC2 resilience items), 10, 11 end-to-end

**Steps:**
1. Doorbells-disabled test: both engines with `DoorbellsEnabled = false` → RPC round trip and pub/sub delivery still complete (backstop-only path)
2. Graceful shutdown drain: slow service in flight → `StopAsync` waits and the caller receives the response within `DrainTimeout`
3. Engine state observability assertions across start/drain/stop
4. Server-restart tolerance (per Task 1 spike outcome): call `HighwayTestServer.Restart()` mid-session — **amended by 004.1:** the port is stable across `Restart()`, so the engine's connection string stays valid and this test is expressible as specified (dispose + rebuild on same port/data dir) → engine recovers (reconnect + resubscribe) and completes new calls

**Done criteria:**
- Backstop-only correctness proven; drain behavior proven; full 005 suite green with `dotnet test` and zero external infrastructure

---

## Completion Record

### Defect found and fixed during completion: foreign reply-slot deletion

`hw:door:rep` is a **node-global** channel — every engine connected to the server
receives every reply doorbell, not just its own. `PendingCallRegistry.TryCompleteFromSlotAsync`
read the slot for any request id it was handed and, finding no matching pending
call, deleted the slot as "orphan cleanup". A node that was not the caller could
therefore win the race, delete another node's reply, and leave the real caller
hanging until its 30s `CallTimeout` expired.

Symptoms: `ConcurrentCalls_CorrelateCorrectly` and
`CompetingConsumers_TwoHosts_AllCallsSucceedWithZeroLoss` failed with 504s;
`GracefulShutdown_DrainsInFlightWork` found an empty reply slot;
`Publish_BeforeAnyEngineStarts_DeliveredWhenSubscriberComesOnline` was flaky.
Single-call tests passed, which is why the defect survived into the integration
suite — it only surfaces when more than one engine is connected.

Fix: the registry now ignores any request id it did not itself register. Slot
cleanup for the node's own timed-out calls is preserved; foreign slots are never
touched. This also drops reply-slot `GET` traffic from O(nodes) to O(1) per reply.
Regression test: `PendingCallRegistryTests.TryCompleteFromSlot_ForForeignRequestId_TouchesNothing`.

### Engine unit tests added

Tasks 4–11 each required unit tests with a mocked connection; none existed. They
were blocked by a toolchain detail: NSubstitute cannot proxy `internal` interfaces
without `[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]`, which is now
declared in `Highway.Client`'s `AssemblyInfo.cs`. Added:

| File | Covers |
|---|---|
| `Engine/PendingCallRegistryTests.cs` | Task 5 — correlation, 504/OCE mapping, late-reply and foreign-slot rules, sweep grace window |
| `Engine/HighwayConnectionTests.cs` | Task 4 — 004.1 transient/permanent classification, fail-fast connect |
| `Engine/DoorbellWatcherTests.cs` | Task 6 — subscription set, wake routing, disabled mode |
| `Engine/RpcWorkerLoopTests.cs` | Task 7 — REPLY-before-ACK ordering, poison envelope, drain-to-nil, loop survival |
| `Engine/ChannelConsumerLoopTests.cs` | Task 8 — RACK-after-dispatch ordering, poison ack, batch termination, sibling isolation |
| `Engine/BackstopSweeperTests.cs` | Task 9 — idle costs no I/O, wake signalling, never dies |
| `Engine/HighwayClientTests.cs` | Task 10 — error-mapping table, request-id uniqueness |
| `Hosting/HighwayEngineHostedServiceTests.cs` | Task 11 — host delegation and fail-fast propagation |

`HighwayEngine` start/stop ordering is asserted end-to-end in
`ResilienceIntegrationTests` rather than by unit test: `StartAsync` calls the
static `HighwayConnection.ConnectAsync`, so the engine cannot be constructed
without a live server. Noted here so the gap is a recorded decision, not an oversight.

Task 13's restart-resume scenario was also missing and is now
`PubSubIntegrationTests.Subscriber_StopsAndRestartsWithSameNodeName_DrainsMessagesPublishedWhileDown`.

### Latent test-parallelism defect fixed

`SubscriberRecorder` is process-global and both `PubSubIntegrationTests` and
`ResilienceIntegrationTests` reset it in their constructors. xUnit parallelizes
across test classes, so one class could wipe the other's recorded entries
mid-test. The existing tests used `>= N` waits and mostly tolerated it; the new
restart-resume test asserts an exact count and exposed it. Both classes now share
the `SubscriberRecorderCollection` xUnit collection, which serializes them.
The same root cause applied in the unit suite, where `ChannelConsumerLoopTests`
initially shared `TestSubscriber`'s static counters with `ServiceExecutorTests`;
the loop tests now use dedicated `LoopSubscriberA`/`LoopSubscriberB` fixtures.

### Final verification

`dotnet build Highway.slnx` — 0 warnings, 0 errors.

| Project | Tests |
|---|---|
| Highway.Abstractions.Tests | 2 |
| Highway.Client.Tests | 132 |
| Highway.Server.Tests | 83 |
| Highway.Integration.Tests | 131 |
| **Total** | **348** |

No external infrastructure required.
