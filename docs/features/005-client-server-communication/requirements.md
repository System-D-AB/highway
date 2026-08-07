# Feature: Client-Server Communication

## Introduction

This feature implements the Highway.Client wire engine — the piece that makes location transparency real. `HighwayClient.ExecuteAsync` sends `HW.CALL` over RESP (via StackExchange.Redis) and awaits the reply; worker loops dequeue and execute requests hosted by this node; `PublishAsync` sends `HW.PUBLISH`; channel consumer loops receive durable messages and dispatch them to local subscribers. Every call goes through the server — there is no local dispatch.

The wire contract (command shapes, key schema, doorbell channels, reply slots, framing guarantees) is defined by feature 004 and is **not restated here** — see `docs/features/004-server-hw-commands/design.md` and `research.md`. This feature specifies client-side behavior against that contract.

Implementation of this feature gates on feature 004 being merged (it consumes `HighwayTestServer` for integration tests), but the spec stands independently against the 004 contract.

## Glossary

- **Engine** — The client-side runtime that owns the connection, doorbell subscriptions, worker loops, and consumer loops
- **Caller flow** — `ExecuteAsync`: serialize → `HW.CALL` → await reply slot → deserialize response
- **Worker loop** — Per-service loop: doorbell/poll → `HW.DEQUEUE` drain → execute → `HW.REPLY` → `HW.ACK`
- **Consumer loop** — Per-channel loop: doorbell/poll → `HW.RECEIVE` drain → dispatch to subscribers → `HW.RACK`
- **Doorbell** — Server-emitted RESP pub/sub notification (`hw:door:*` channels); a latency optimization only
- **Backstop** — Client-side periodic sweep that drives progress when doorbells are missed
- **Envelope** — Versioned JSON wrapper around every user payload on the wire
- **Group** — Pub/sub consumer identity registered with `HW.SUBSCRIBE`; in Highway, group = NodeName

## Requirements

### Requirement 1: Connection Management

**User Story:** As a developer, I want the engine to own one connection to the server, so that all Highway traffic is multiplexed efficiently and reconnection is handled for me.

#### Acceptance Criteria

1. The engine opens exactly one StackExchange.Redis `ConnectionMultiplexer` per node, built from `HighwayOptions.Server`, shared by all commands and subscriptions
2. At startup, failure to connect fails fast with a descriptive exception naming the configured endpoint (no silent retry loop at startup)
3. After startup, transient connection loss is tolerated: SE.Redis built-in reconnect is relied upon, and engine loops back off and retry instead of crashing
4. The multiplexer is disposed during shutdown
5. `HighwayOptions.Server` values that are not valid SE.Redis configuration strings are rejected at startup with a descriptive error

### Requirement 2: Envelope and Serialization

**User Story:** As a framework author, I want every payload wrapped in a versioned envelope, so that future headers (tracing, correlation, audit for feature 002) can be added without a wire break.

#### Acceptance Criteria

1. Every `HW.CALL`, `HW.REPLY`, and `HW.PUBLISH` payload is a JSON envelope: `{ "v": 1, "src": "<nodeId>", "ts": "<ISO-8601 UTC>", "body": <serialized DTO> }`
2. Envelope `body` carries the DTO serialized with System.Text.Json; no CLR type names appear anywhere on the wire (no polymorphic type metadata)
3. Envelope deserialization validates `v` and rejects unknown versions with a clear error
4. Serialization uses a single shared `JsonSerializerOptions` (case-insensitive property matching), configured once
5. Envelopes exceeding the server's max payload size (004 default 1 MiB) are detected client-side before sending: `ExecuteAsync` returns a response with StatusCode 413; `PublishAsync` throws a typed exception
6. The requesting/publishing node's `NodeName` is always present as `src`

### Requirement 3: ExecuteAsync Caller Flow

**User Story:** As a developer, I want `ExecuteAsync` to route my request through the server and return the typed response, so that remote calls look identical to the programming model I already know.

#### Acceptance Criteria

1. The service name is resolved from the request's runtime type via the catalog's reverse lookup (no per-call attribute reflection)
2. A request whose type is not in the catalog completes with StatusCode 404 and `ErrorDetail` — it never reaches the network and never throws
3. The engine generates a unique `requestId` (GUID string), sends `HW.CALL <service> <requestId> <envelope>`, and awaits the reply
4. The node subscribes to the reply doorbell channel (`hw:door:rep`) exactly once, shared by all pending calls
5. On a reply doorbell for a pending `requestId`: the engine `GET`s the reply slot (`hw:rep:{requestId}`), completes the waiting call with the deserialized response, and `DEL`s the slot
6. A doorbell or slot arriving for an already-completed/timed-out call is ignored and its slot is cleaned up (`DEL`) — no exception, no leak
7. The response JSON `body` is deserialized into `TResponse` and returned; a malformed response envelope completes the call with StatusCode 502 and `ErrorDetail`
8. Concurrent `ExecuteAsync` calls correlate independently (at least 100 concurrent calls complete with the correct responses)
9. `ExecuteAsync` completes only via: response data, timeout, or caller cancellation — it never hangs silently and never throws for service-level failures

### Requirement 4: Timeouts and Cancellation

**User Story:** As a developer, I want real timeouts and cancellation on remote calls, so that a dead consumer costs me one timeout instead of a hung thread.

#### Acceptance Criteria

1. A call not answered within `HighwayOptions.CallTimeout` (default 30s) completes with StatusCode 504 and `ErrorDetail` describing the timeout
2. A per-call `CancellationToken` can shorten or extend the wait relative to `CallTimeout` (whichever fires first wins)
3. Caller cancellation throws `OperationCanceledException` honoring the token (the one intentional exception path); the pending entry is removed and any late reply is cleaned up per Requirement 3 AC6
4. A timed-out or cancelled request is **not** withdrawn from the server queue — at-least-once semantics stand; the handler may still run and its late reply is discarded per Requirement 3 AC6
5. Timeout values are validated at startup (`> 0`)

### Requirement 5: Error-as-Data Mapping

**User Story:** As a developer, I want transport and protocol failures returned as status codes in my response object, so that I handle one failure shape for local and remote problems alike.

#### Acceptance Criteria

1. Service failures map to status codes in the returned `TResponse`, never exceptions (product principle: errors are data):
   - unknown service / request type not in catalog → 404
   - envelope too large → 413
   - transport failure while sending/receiving → 503
   - malformed response envelope → 502
   - call timeout → 504
   - unhandled engine error → 500
2. Every mapped failure carries an `ErrorDetail` with a stable machine-readable `Code` (e.g. `SERVICE_NOT_FOUND`, `CALL_TIMEOUT`, `SERVER_UNAVAILABLE`) and a human-readable message
3. Timeout/error responses are constructed via the response type's public parameterless constructor; response types lacking one are rejected at startup (Requirement 12)
4. The mapping table is documented in the design and covered by unit tests for every row

### Requirement 6: Worker Loop (RPC Hosting)

**User Story:** As a service host, I want my node to automatically dequeue and execute every service in its catalog, so that hosting services is just `AddHighway` plus running the app.

#### Acceptance Criteria

1. For each service in the catalog, the engine runs a worker loop that claims work via `HW.DEQUEUE <service> <NodeName>`
2. Workers wait via the service doorbell channel (`hw:door:svc:{service}`) plus the backstop (Requirement 10) — idle workers do not hot-poll
3. On wake, the loop drains `HW.DEQUEUE` until nil (one doorbell can cover many enqueued requests)
4. Each dequeued request: deserialize envelope → deserialize `body` into the catalog's `RequestType` → execute via `ServiceExecutor` (DI scope per invocation) → serialize response envelope → `HW.REPLY` → then `HW.ACK` (reply is sent before ack, so a crash between the two still delivers the response)
5. A dequeued envelope that cannot be parsed still produces an `HW.REPLY` with StatusCode 400 and `ErrorDetail`, followed by `HW.ACK` — callers never wait out a timeout for a poisoned request
6. An exception in one request's pipeline is logged and mapped to a 500 reply; the loop continues to the next request — no worker loop ever dies from a single bad request
7. Per-service concurrency is bounded by a configurable limit (`HighwayOptions.WorkerConcurrency`, default 8); dequeuing pauses while the limit is reached
8. Multiple engines hosting the same service partition dequeued work with zero duplicates (competing consumers, guaranteed by the server — the client must simply dequeue concurrently)
9. Services never block the doorbell/subscription thread; execution runs on the thread pool

### Requirement 7: PublishAsync Flow

**User Story:** As a developer, I want `PublishAsync` to durably deliver my message to every subscriber group, so that I can emit events without knowing who consumes them.

#### Acceptance Criteria

1. The channel name is resolved from the message's runtime type via the catalog's reverse lookup
2. A message whose type is not in the catalog throws a typed exception (publishing is fire-and-confirm; there is no response object to carry 404 — the error is immediate and local)
3. The engine sends `HW.PUBLISH <channel> <envelope>` and completes when the server acknowledges (the message is durable at that point, including the zero-group backlog case)
4. The returned group count is not surfaced in v1 (`PublishAsync` returns `Task`), but is logged at debug level
5. Transport failure throws a typed `HighwayTransportException` (the documented exception path for publish)
6. Caller cancellation throws `OperationCanceledException` and does not send a partial command

### Requirement 8: Channel Consumer Loop (Pub/Sub Hosting)

**User Story:** As a subscriber host, I want my node to automatically receive and dispatch channel messages, so that durable pub/sub works with zero manual wiring.

#### Acceptance Criteria

1. At startup, for each channel in the catalog that has local subscribers, the engine sends `HW.SUBSCRIBE <channel> <NodeName>` (group = NodeName, idempotent)
2. The consumer loop waits on the group doorbell channel (`hw:door:ch:{channel}:grp:{NodeName}`) plus the backstop; on wake it drains `HW.RECEIVE <channel> <NodeName> COUNT <batch>` until fewer than batch entries return
3. Each received message: deserialize envelope → deserialize `body` into the catalog's `MessageType` → fan out to all local subscribers via `ServiceExecutor` (scope per subscriber invocation)
4. `HW.RACK` is sent only after all subscribers for that message have completed (success or failure) — a crash mid-dispatch causes redelivery, not loss
5. Subscriber failures are logged but do not prevent sibling dispatch or acknowledgment (v0.8-compatible semantics: failures are observable via logs/metrics, not redelivered forever)
6. An unparseable message envelope is logged and acknowledged (`HW.RACK`) — poison messages do not block the group queue
7. Each node hosting subscribers receives its own copy of every published message (fan-out between nodes via per-node groups); within a node, all local subscribers receive the message
8. Consumer loops never die from a single bad message or a transient server error

### Requirement 9: Group Naming and Node Identity

**User Story:** As an operator, I want node identity to be stable across restarts, so that my node's subscriber group and its pending messages survive a restart instead of being orphaned.

#### Acceptance Criteria

1. The subscriber group name is always `HighwayOptions.NodeName` — one group per node, every node gets a copy of published messages
2. `NodeName` defaults to a **stable** value derived from the application (`{entry-assembly-name}-{machine-name}`), not a random value — restarting the same app on the same machine resumes the same group with its pending messages intact
3. The engine never sends `HW.UNSUBSCRIBE` during graceful shutdown — group state (including undelivered messages) persists for the restarted node
4. `NodeName` is validated: non-empty, reasonable length/charset (documented in design); invalid values fail fast at startup
5. Documentation and XML comments state that `NodeName` must be unique per live process instance — two live processes sharing a name share one group and one processing identity (competing with each other)

### Requirement 10: Backstop Sweep

**User Story:** As the engine, I want a periodic sweep independent of doorbells, so that a dropped doorbell costs latency and never a lost message or hung call.

#### Acceptance Criteria

1. A single sweep loop runs at a configurable interval (`HighwayOptions.BackstopInterval`, default 500ms; validated > 0)
2. Each sweep: (a) for pending calls older than the doorbell grace window, `GET` their reply slots directly and complete any that arrived; (b) trigger a drain pass on every worker loop and consumer loop
3. The sweep is cheap when idle: it inspects in-memory pending sets and performs no network I/O when there is nothing pending and no loops signaled
4. With doorbells disabled entirely (test seam), the full feature set — RPC round trips, pub/sub delivery — still works, at backstop-interval latency
5. The sweep loop never throws; internal errors are logged and the next sweep proceeds

### Requirement 11: Engine Lifecycle and Hosting Integration

**User Story:** As a developer using the .NET Generic Host, I want `AddHighway` to wire the engine into the host lifecycle, so that my services start and stop with the application and no manual start call exists.

#### Acceptance Criteria

1. `AddHighway` registers the engine as a singleton (`IHighwayEngine`) and an `IHostedService` wrapper that starts it with the host
2. Engine start order: connect (fail fast per Requirement 1 AC2) → subscribe doorbell channels → `HW.SUBSCRIBE` all catalog channels → start worker/consumer loops → start backstop
3. Engine stop order: stop accepting new work (loops stop dequeuing/receiving) → await in-flight executions up to `HighwayOptions.DrainTimeout` (default 10s) → stop backstop → dispose connection; anything still in flight after drain is logged and left to server lease recovery (feature 004)
4. Start is idempotent-safe (starting twice throws a clear exception rather than double-subscribing); stop is safe to call once and only once has effect
5. In non-Generic-Host applications, resolving `IHighwayEngine` and calling `StartAsync`/`StopAsync(CancellationToken)` manually provides identical behavior
6. The engine surfaces its state (not started / running / draining / stopped) for diagnostics and tests

### Requirement 12: Startup Validation

**User Story:** As a developer, I want all engine-related misconfiguration to fail fast at startup, so that I never discover a broken node at first traffic.

#### Acceptance Criteria

1. Assembly scanning additionally validates that every service's response type has a public parameterless constructor (required to construct timeout/error responses); violations throw a typed exception at `AddHighway` time
2. `CallTimeout`, `BackstopInterval`, `WorkerConcurrency`, `DrainTimeout`, and `ReceiveBatchSize` are validated at startup with descriptive errors (positive values; batch within server bounds 1..500)
3. `HighwayOptions.Server` required-check (from feature 003) remains the first failure, before any new validations
4. All validation failures name the offending value/option in the exception message

### Requirement 13: Options Surface

**User Story:** As a developer, I want the engine's tunables available on `HighwayOptions` with safe defaults, so that a zero-configuration node behaves sensibly.

#### Acceptance Criteria

1. New options with defaults: `WorkerConcurrency` (8), `ReceiveBatchSize` (10), `BackstopInterval` (500ms), `DrainTimeout` (10s), `DoorbellsEnabled` (true — test seam for Requirement 10 AC4)
2. Existing options unchanged in meaning: `NodeName` (default now stable per Requirement 9 AC2), `Server`, `CallTimeout`
3. All options carry XML doc comments (project convention)
4. Options are read once at engine start; mutation after start has no effect (documented)

### Requirement 14: Testing

**User Story:** As a Highway contributor, I want the wire engine covered by unit and integration tests, so that caller, worker, and consumer behavior is provably correct against a real server.

#### Acceptance Criteria

1. Unit tests (mocked transport via NSubstitute): envelope round-trip + version rejection, correlation of concurrent calls, timeout → 504, cancellation → `OperationCanceledException`, every error-mapping row (Requirement 5), REPLY-before-ACK ordering, bad-envelope → 400 reply path, publish confirmation and exception paths
2. Integration tests against `HighwayTestServer` (feature 004), each engine built through real `AddHighway` + DI:
   - full RPC round trip between two engines (caller node ↔ service node)
   - 404 for a call no node hosts is surfaced as data (timeout only when the service exists but is slow)
   - timeout → 504 verified against a deliberately slow service with a short `CallTimeout`
   - competing consumers: two service-node engines share one service's load with zero duplicates
   - pub/sub: publisher engine → subscriber engine, all local subscribers invoked; two subscriber nodes each receive a copy
   - late subscriber: publish before the subscriber engine starts → message delivered after it starts (product success criterion via the client API)
   - doorbells disabled (`DoorbellsEnabled = false`): RPC and pub/sub both still complete via backstop
   - graceful shutdown drains in-flight work (slow service completes before stop returns, within drain timeout)
3. No test requires external infrastructure; tests follow `Method_Scenario_ExpectedBehavior` naming

## Cross-References

- Wire contract, key schema, doorbell channels, reply slots, framing: `docs/features/004-server-hw-commands/design.md` (authoritative — do not re-derive here)
- Garnet/SE.Redis constraints (RESP2, multiplexing, no blocking commands): `docs/features/004-server-hw-commands/research.md`, `docs/product/research.md` §2.5 (doorbell pattern, why not BLPOP)
- Predecessor features: 003 (catalog, `ServiceExecutor`, `AddHighway` — all extended here), 004 (server commands, `HighwayTestServer`)
- Successor features: 006 (heartbeat: engine announces catalog, `HW.DISCOVER` fast-fail 404 replaces timeout for unhosted services), 002 (flight recorder consumes envelope `src`/`ts` headers)
- Product basis: `docs/product/product.md` § G2 (at-least-once), G4 (location transparency), G6 (protocol), G7 (timeouts, cancellation, errors as data)
