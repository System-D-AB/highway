# Feature: Server HW.* Commands

## Introduction

Highway.Server is a Garnet process extended with Highway's custom `HW.*` RESP commands. This feature implements the broker brain: the RPC command set (`HW.CALL`, `HW.REPLY`, `HW.DEQUEUE`, `HW.ACK`) and the Pub/Sub command set (`HW.PUBLISH`, `HW.SUBSCRIBE`, `HW.UNSUBSCRIBE`, `HW.RECEIVE`, `HW.RACK`), plus the hosting layer that runs Garnet with these commands registered — both as a standalone server (`HighwayServerBuilder`) and embedded in-process for tests (`HighwayTestServer`).

All atomicity guarantees live here. Every operation is one command, one round-trip; the client (feature 005) never orchestrates multi-step workflows. The server speaks standard RESP framing, so `redis-cli`, RESP analyzers, and StackExchange.Redis `Execute()` all work against it.

Out of scope for this feature: registry commands (`HW.HEARTBEAT`, `HW.DISCOVER`, `HW.STATS` — feature 006), the client wire engine (feature 005), observability/flight recorder (feature 002), and the web dashboard.

## Glossary

- **Service queue** — The durable FIFO queue of pending RPC requests for one service name
- **Processing list** — Per-node list holding RPC requests that have been dequeued but not yet acknowledged (in-flight)
- **Reply slot** — Server-side slot holding an RPC response until the caller retrieves it
- **Doorbell** — A low-latency notification that wakes waiting callers/workers; correctness never depends on it
- **Subscriber group** — A named consumer of a channel; each group gets its own independent copy of every published message (fan-out between groups, competing consumers within a group)
- **Group list** — The durable per-group queue of published messages awaiting consumption
- **Backlog** — Messages published to a channel while no subscriber groups are registered, held for groups that register later

## Requirements

### Requirement 1: Server Hosting and Command Registration

**User Story:** As an operator, I want to start a Highway server with a small builder API, so that I get a working broker with all `HW.*` commands registered and no manual configuration.

#### Acceptance Criteria

1. `HighwayServerBuilder` configures at minimum: port (`WithPort`), data directory (`WithDataDir`), and produces a running server via `Build()` + `RunAsync(CancellationToken)`
2. Starting the server registers all nine `HW.*` commands (`HW.CALL`, `HW.REPLY`, `HW.DEQUEUE`, `HW.ACK`, `HW.PUBLISH`, `HW.SUBSCRIBE`, `HW.UNSUBSCRIBE`, `HW.RECEIVE`, `HW.RACK`) automatically — no registration step by the caller
3. All commands are reachable through standard RESP: `redis-cli` and StackExchange.Redis `Execute("HW.CALL", ...)` both work
4. `RunAsync` honors the `CancellationToken` and shuts down cleanly (listeners stopped, resources disposed)
5. The server exposes its connection endpoint (host + port) programmatically after start
6. Defaults are sensible: port defaults to 6500; omitting the data directory runs memory-only
7. Structured logging via `ILogger` reports server start, command registration, and shutdown

### Requirement 2: Embedded Test Server

**User Story:** As a developer writing integration tests, I want an in-process Highway server with an ephemeral port and no disk usage, so that `dotnet test` requires zero external infrastructure.

#### Acceptance Criteria

1. `HighwayTestServer` starts an embedded server in-process on an ephemeral (OS-assigned) port
2. `HighwayTestServer` runs memory-only — no files are written to disk
3. `HighwayTestServer.ConnectionString` returns a connection string usable by StackExchange.Redis
4. `HighwayTestServer` implements `IDisposable`/`IAsyncDisposable`; disposal stops the server and releases the port
5. Multiple `HighwayTestServer` instances can run concurrently in the same process (isolated state, distinct ports)
6. Startup is fast enough for per-test-fixture usage (target: < 2 seconds to accepting commands)

### Requirement 3: HW.CALL — Enqueue RPC Request

**User Story:** As a client engine, I want to enqueue an RPC request with a single command, so that the request is durably queued and workers are notified in one round-trip.

Command shape: `HW.CALL <service> <requestId> <payload>`

#### Acceptance Criteria

1. The request is appended to the service's queue as one server-side atomic operation — no partial state is ever observable
2. The command returns an OK-style simple reply on success
3. The `requestId` is preserved exactly as supplied; the server does not interpret or modify it or the payload
4. Requests are enqueued in the order received from a single connection and dequeued FIFO
5. Enqueuing to a service with no registered/online workers succeeds — the request is held durably (there is no registry in this feature; unknown service names are valid)
6. After the append commits, the server emits a doorbell notification for the service so idle workers wake without polling; the doorbell is a latency optimization — correctness never depends on it being delivered
7. The enqueued request survives a server restart when AOF persistence is enabled

### Requirement 4: HW.REPLY — Send RPC Response

**User Story:** As a client engine completing a service invocation, I want to deliver the response to the waiting caller with a single command, so that the caller wakes immediately.

Command shape: `HW.REPLY <requestId> <payload>`

#### Acceptance Criteria

1. The response is written to a reply slot keyed by `requestId` as one server-side atomic operation; after the write commits, the reply doorbell is rung (best-effort, as in Requirement 3)
2. The command returns an OK-style simple reply on success
3. The caller can retrieve the response by `requestId` (retrieval surface defined in the design; used by feature 005)
4. Replying twice for the same `requestId` overwrites or is rejected per a documented, deterministic rule (design decides; last-writer-wins or first-writer-wins, but never undefined)
5. Unretrieved reply slots expire after a configurable TTL (default suitable for the client's default 30s call timeout) so leaked slots are garbage-collected
6. The reply payload is stored and returned byte-for-byte unmodified

### Requirement 5: HW.DEQUEUE — Pop Next Request

**User Story:** As a client worker, I want to atomically claim the next pending request for a service, so that multiple competing workers never receive the same request.

Command shape: `HW.DEQUEUE <service> <nodeId>`

#### Acceptance Criteria

1. Dequeue atomically moves the oldest pending request from the service queue into the node's processing list — no other command can observe or claim the same request
2. The response contains the `requestId` and the payload of the claimed request
3. Dequeue on an empty (or unknown) service queue returns a nil/empty reply — never an error
4. Multiple nodes dequeuing the same service concurrently partition the work: every request is claimed exactly once (competing consumers)
5. FIFO order is preserved: requests are claimed in enqueue order
6. Claimed-but-unacknowledged requests remain visible in the node's processing list until `HW.ACK` removes them

### Requirement 6: HW.ACK — Acknowledge RPC Processing

**User Story:** As a client worker that finished processing a request, I want to acknowledge completion, so that the request leaves the processing list and is never redelivered.

Command shape: `HW.ACK <service> <nodeId> <requestId>`

#### Acceptance Criteria

1. The acknowledged `requestId` is removed from the node's processing list
2. ACK is idempotent: acknowledging an unknown or already-acknowledged `requestId` returns success, not an error
3. After ACK, the request cannot be returned by any future `HW.DEQUEUE` or redelivery mechanism
4. A node's processing list is empty when all dequeued requests have been acknowledged

### Requirement 7: RPC Lease Expiry and Requeue

**User Story:** As the broker, I want to requeue requests whose worker died before acknowledging, so that RPC honors at-least-once delivery even across worker crashes.

#### Acceptance Criteria

1. Each dequeued request carries the time it was claimed
2. A request in a processing list longer than the configurable lease duration (default: 5 minutes) becomes eligible for requeue
3. Eligible requests are requeued lazily: the next `HW.DEQUEUE` for that service first sweeps expired entries from all nodes' processing lists (returning them to the tail of the service queue), then claims the next request — so another worker picks the work up without any background infrastructure
4. Requeue is safe against a slow-but-alive worker double-completing: `HW.ACK` after requeue still succeeds and removes any remaining tracking state (at-least-once permits duplicate delivery; callers/handlers own idempotency)
5. The lease duration is server-configurable; lazy requeue can be disabled (expired entries then stay in their processing lists) with the consequence documented
6. Recovery works when a node disconnects without acknowledging any in-flight requests

### Requirement 8: HW.SUBSCRIBE / HW.UNSUBSCRIBE — Subscriber Group Management

**User Story:** As a client node, I want to register and unregister subscriber groups on channels with a single command each, so that the server owns routing state and I stay stateless.

Command shapes: `HW.SUBSCRIBE <channel> <group>` / `HW.UNSUBSCRIBE <channel> <group>`

#### Acceptance Criteria

1. `HW.SUBSCRIBE` registers the group as a subscriber of the channel and returns OK
2. Subscribing the same group to the same channel twice is idempotent — second call returns OK and does not duplicate state
3. `HW.UNSUBSCRIBE` removes the group from the channel and returns OK
4. Unsubscribing a group that is not subscribed is idempotent — returns OK, not an error
5. On unsubscribe, the group's unconsumed message list and processing state are removed
6. Group registration is durable — subscriptions survive server restart when AOF is enabled
7. Multiple distinct groups can subscribe to the same channel

### Requirement 9: HW.PUBLISH — Durable Fan-Out

**User Story:** As a publisher, I want one command to durably deliver my message to every subscriber group, so that no group misses the message and delivery is atomic.

Command shape: `HW.PUBLISH <channel> <payload>`

#### Acceptance Criteria

1. The message is appended to the group list of every registered subscriber group for the channel, atomically — either all groups receive it or none do (no partial fan-out on failure)
2. The reply reports the number of groups the message was delivered to
3. The server assigns each published message a channel-unique message ID, returned or derivable per the design, used for acknowledgment (`HW.RACK`)
4. Publishing to a channel with zero registered groups succeeds and returns group count 0; the message is retained in the channel backlog (see Requirement 10)
5. Messages published while a group exists but has no online consumers accumulate in that group's list and are delivered when the consumer next receives (no loss for offline groups)
6. The payload is stored byte-for-byte unmodified
7. Publishing is durable — published messages survive server restart when AOF is enabled

### Requirement 10: Channel Backlog for Late Subscribers

**User Story:** As a publisher, I want messages published before any subscriber registers to be held, so that a subscriber that starts later still receives them (product success criterion: "a published message with no online subscriber is delivered when the subscriber eventually starts").

#### Acceptance Criteria

1. Messages published to a channel with zero registered groups are appended to a per-channel backlog
2. When a group subscribes to a channel that has a backlog, the backlog messages are delivered to that group (in publish order) before newer messages
3. Multiple groups registering at different times each receive the backlog messages that still fall within retention
4. Backlog entries expire after a configurable retention (duration and/or maximum entry count) so an never-subscribed channel cannot grow without bound
5. Once at least one group is registered, new publishes go to group lists directly (backlog is only for the zero-group state)
6. Backlog retention settings are server-configurable with safe defaults

### Requirement 11: HW.RECEIVE — Consume Messages

**User Story:** As a client consumer, I want to pull a batch of messages for my subscriber group in one command, so that I can process them with bounded round-trips.

Command shape: `HW.RECEIVE <channel> <group> [COUNT n]`

#### Acceptance Criteria

1. Returns up to `COUNT` messages (default when omitted defined in design) from the group's list in FIFO publish order
2. Each returned entry contains the server-assigned message ID and the payload
3. Received messages move into the group's in-flight/processing state; they are not returned again by subsequent `HW.RECEIVE` calls
4. Receiving from an unknown or empty channel/group returns an empty array — never an error
5. `COUNT` validates bounds (positive, sane maximum per design); invalid values return a RESP error
6. Receiving triggers a doorbell/notification surface consistent with the RPC path (design decides mechanism) so consumers are woken promptly when new messages arrive

### Requirement 12: HW.RACK — Acknowledge Pub/Sub Message

**User Story:** As a client consumer that finished processing a message, I want to acknowledge it, so that it leaves in-flight state and is never redelivered to my group.

Command shape: `HW.RACK <channel> <group> <messageId>`

#### Acceptance Criteria

1. The acknowledged message is removed from the group's in-flight/processing state
2. RACK is idempotent: acknowledging an unknown or already-acknowledged message returns success
3. After RACK, the message cannot be returned by any future `HW.RECEIVE` or redelivery for that group
4. Unacknowledged received messages are recoverable per the pub/sub redelivery rule defined in the design (mirrors Requirement 7's lease semantics)
5. Acknowledgment in one group has no effect on other groups' copies of the same message

### Requirement 13: Durability and Restart Survival

**User Story:** As an operator, I want queued requests, published messages, reply slots, and subscriptions to survive a server restart, so that a crash or upgrade does not lose in-transit work.

#### Acceptance Criteria

1. With AOF persistence enabled, all Highway state (service queues, processing lists, reply slots, subscriptions, group lists, backlog) survives a controlled server restart
2. After restart, `HW.DEQUEUE` returns requests enqueued before the restart; `HW.RECEIVE` returns messages published before the restart
3. Memory-only mode (no data directory) is explicitly supported for tests and accepts that state is lost on shutdown
4. State keys coexist with stock Garnet data without collision (namespaced key schema per design)

### Requirement 14: Input Validation and RESP Errors

**User Story:** As any RESP client, I want malformed commands rejected with clear RESP errors, so that misuse is diagnosed immediately instead of corrupting state.

#### Acceptance Criteria

1. Wrong argument counts for every `HW.*` command return a RESP error, never a crash or silent misbehavior
2. Empty/blank service names, channel names, group names, node IDs, request IDs, and message IDs are rejected with a RESP error
3. Unknown subcommand-style misuse (e.g., negative or non-numeric `COUNT`) returns a RESP error
4. Payload size above a configurable maximum is rejected with a RESP error (protects memory); default limit is documented
5. No malformed input can leave partially-applied state (validation happens before mutation)
6. Errors are returned as RESP error replies; the server process never throws unhandled exceptions out of a command handler

### Requirement 15: Testing

**User Story:** As a Highway contributor, I want the broker covered by unit and integration tests, so that command semantics are provably correct and regression-free.

#### Acceptance Criteria

1. Unit tests cover each command's argument parsing, validation, and state transitions against the server's internal state (no network required)
2. Integration tests run a real embedded `HighwayTestServer` and drive it via StackExchange.Redis `Execute()` — no external infrastructure
3. Integration tests cover the RPC round-trip flow: `HW.CALL` → `HW.DEQUEUE` → `HW.REPLY` → reply retrieval → `HW.ACK`
4. Integration tests cover the pub/sub flow: `HW.SUBSCRIBE` → `HW.PUBLISH` → `HW.RECEIVE` → `HW.RACK`, including multi-group fan-out and per-group independence
5. Integration tests cover competing consumers: multiple dequeue clients partition a set of requests with zero duplicates and zero losses
6. Integration tests verify product success criterion 2: publish with no subscriber → subscriber starts later → message is delivered
7. A restart-survival integration test verifies Requirement 13 with AOF enabled (stop server, restart on same data dir, state intact)
8. Test naming follows `[Method]_[Scenario]_[ExpectedBehavior]`; tests use xUnit + FluentAssertions (+ NSubstitute where mocks are needed)

## Cross-References

- Protocol command table: `docs/product/product.md` § "Highway Protocol (HW.* Commands)"
- Hosting model background: `docs/product/product.md` § "Highway.Server — Hosting & Control Panel"
- Garnet capability analysis (no Streams, pub/sub semantics, custom-command limits): `docs/product/research.md` § 2.3
- Predecessor feature: `docs/features/003-assembly-scanning/` (catalog that clients will announce over these commands in 005/006)
- Successor features: 005 (client wire engine consuming these commands), 006 (`HW.HEARTBEAT`, `HW.DISCOVER`, `HW.STATS`), 002 (flight recorder hooks into every command handler)
