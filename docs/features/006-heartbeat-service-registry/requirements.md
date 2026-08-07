# Feature: Heartbeat & Service Registry

## Introduction

Highway.Server currently knows what work exists but not who can do it. Queues accept requests for any service name, groups accumulate messages, and processing lists grow — but nothing tracks which nodes are alive or what they host. This feature adds the registry: nodes announce their catalog on a periodic heartbeat, the server maintains a live view of the topology with TTL-based expiry, and both operators and clients can query it.

Three commands complete the protocol table in `docs/product/product.md`: `HW.HEARTBEAT`, `HW.DISCOVER`, and `HW.STATS`. Together they unlock fast-fail (a caller learns in one round trip that nobody hosts a service, instead of waiting out a 30-second timeout), operator visibility (queue depth, subscriber counts, connected nodes), and the liveness signal that lets the server prune state which currently grows without bound.

The client-side contract already exists and is currently unused: `CatalogInfo`, `CatalogServiceEntry` and `CatalogChannelEntry` live in `Highway.Abstractions`, and `ICatalog.ToCatalogInfo()` has been building the payload since feature 003. This feature is what consumes it.

### Debt this feature discharges

Feature 004.1 deferred one item here explicitly: `hw:svc:{service}:nodes` accumulates every node that has ever dequeued a service and is never pruned, so `HW.DEQUEUE` locks one key per historical node and sweeps its processing list on every call. Pruning needs a liveness signal, which is precisely what heartbeat provides. See `004.1/requirements.md` § "Non-Goals".

## Glossary

Terms carry their 004/005 meanings. Additional terms:

- **Node** — One running Highway engine, identified by its `NodeName` (which is also its subscriber-group name).
- **Registration** — The server-side record of one node: its catalog, its last heartbeat time, and its derived health.
- **Registry** — The set of all current registrations.
- **Stale node** — A node whose last heartbeat is older than the expiry window; it is treated as offline and its registration is eligible for pruning.
- **Fast-fail** — Returning 404 to a caller because the registry shows zero live nodes hosting the requested service, without enqueuing anything.

## Requirements

### Requirement 1: HW.HEARTBEAT — Registration and Liveness as Separate Forms

**User Story:** As a client engine, I want to announce my catalog once and then prove liveness cheaply, so that a node's steady-state cost is a few bytes rather than its entire catalog on every beat.

**Design rationale:** A node's catalog is static for its lifetime — assembly scanning happens at startup and there is no runtime register/deregister (see Non-Goals). Re-sending it on every beat would put up to `MaxCatalogBytes` on the wire per node per interval and force a server-side JSON parse to rebuild an index that never changes. Registration ("here is what I serve") and liveness ("I am still here") are different operations and are separated accordingly, while remaining a single command so the protocol table in `docs/product/product.md` is unchanged.

Command shapes:
- `HW.HEARTBEAT <nodeId> <catalogJson>` — **registration form**: records the catalog, builds the service index, refreshes liveness
- `HW.HEARTBEAT <nodeId>` — **liveness form**: refreshes the timestamp only

#### Acceptance Criteria

1. The registration form records the node's catalog, updates the service index, and refreshes its liveness timestamp as one atomic operation, returning an OK-style simple reply
2. The liveness form refreshes the timestamp only: it performs no catalog parse, no index write, and no allocation proportional to catalog size
3. The liveness form for a node the server has **no registration record for** returns a distinct re-register signal rather than `+OK`, and refreshes nothing
4. A client receiving the re-register signal must send the registration form promptly, before its next scheduled beat
5. AC3 is a correctness requirement, not an optimization: pruning removes a node's service-index entries, so a liveness beat that merely refreshed a timestamp would leave the node alive but undiscoverable
6. The registration form is idempotent: re-registering an unchanged catalog does not duplicate or grow registry state
7. Re-registering a **changed** catalog replaces the index — services no longer present are removed, new ones added — so a redeployed node under the same name never leaves stale index entries
8. `catalogJson` is stored byte-for-byte as supplied; the server parses it only to derive the service index and never rewrites the stored copy
9. An empty catalog (`{"services":[],"channels":[]}`) is valid — it registers a node that hosts nothing (a pure caller)
10. Registrations carry a server-configurable expiry window with a documented default; a node whose last beat is older than the window is stale
11. Blank node identifiers, control characters, and oversized catalogs are rejected with the 004.1 error codes (`HW_INVALID_ARG`, `HW_PAYLOAD_TOO_LARGE`)
12. Registrations survive a server restart when AOF is enabled; a node stale at recovery time stays stale until it beats again. Without AOF, surviving nodes recover through the AC3 re-register signal rather than silently disappearing from discovery

### Requirement 2: HW.DISCOVER — Find Nodes Hosting a Service

**User Story:** As a caller, I want to ask which live nodes host a service, so that I can fail fast instead of waiting out a timeout when nobody is listening.

Command shape: `HW.DISCOVER <service>`

#### Acceptance Criteria

1. Returns the node IDs of all live (non-stale) nodes whose most recent catalog lists the service
2. Returns an empty array for an unknown service or one whose hosts are all stale — never an error
3. Stale nodes are excluded from results even when their registration has not yet been pruned
4. A node that heartbeats after going stale reappears in results without any explicit re-registration step
5. Each returned entry carries enough information for the caller to reason about freshness (node ID plus a liveness indicator defined in the design)
6. Results are consistent with `HW.HEARTBEAT`: a service present in a live node's most recent catalog is always discoverable
7. Discovery is a read-only operation — it never mutates registry state

### Requirement 3: HW.STATS — Operational Visibility

**User Story:** As an operator, I want queue depths, subscriber counts, and node health in one command, so that I can see backpressure and topology without attaching a debugger.

Command shape: `HW.STATS [<service>|<channel>]`

#### Acceptance Criteria

1. With no argument, returns server-wide totals: connected (live) node count, registered service count, registered channel count, and total pending work
2. With a service name, returns that service's pending queue depth, the number of live nodes hosting it, and the count of in-flight (dequeued, unacknowledged) requests across all nodes
3. With a channel name, returns that channel's registered subscriber-group count, per-group pending message count, and backlog depth
4. An unknown service or channel returns zeroed counters, not an error — an operator querying a name that has seen no traffic gets a meaningful answer
5. The reply shape is self-describing (field-name/value pairs) so it stays readable in `redis-cli` and parseable by tooling as fields are added
6. `HW.STATS` is read-only and safe to poll on an interval without affecting queue behavior
7. Counters reflect committed state at the time of the call; no cross-key snapshot consistency is promised, and this is documented

### Requirement 4: Stale Node Expiry and State Pruning

**User Story:** As the broker, I want to drop the state of nodes that have stopped heartbeating, so that a long-lived server does not accumulate dead nodes' keys and lock targets forever.

#### Acceptance Criteria

1. A node whose last heartbeat exceeds the expiry window is pruned: its registration is removed and it no longer appears in `HW.DISCOVER` or `HW.STATS` node counts
2. Pruning is lazy — triggered by commands that already touch the relevant keys, with no background timer or additional infrastructure (matching the lease-sweep approach in 004)
3. Pruning a node removes it from every `hw:svc:{service}:nodes` set and its mirror list, so `HW.DEQUEUE` stops locking and sweeping that node's processing list — discharging the 004.1 deferral
4. A pruned node's **unacknowledged RPC requests are not lost**: they are returned to their service queue for redelivery, exactly as lease expiry does today
5. A pruned node's **subscriber groups are NOT deleted**. Pub/sub groups deliberately outlive the process (005 Requirement 9 AC3) so a restarting node resumes its pending messages; pruning liveness must not silently change that contract
6. Pruning is idempotent and safe under concurrency: two commands pruning the same node produce one outcome and no lost requests
7. The expiry window is server-configurable, and pruning can be disabled entirely with the consequence documented

### Requirement 5: Client Heartbeat Loop

**User Story:** As a developer, I want my node to register itself automatically, so that discovery and operator visibility work without any code I have to write.

#### Acceptance Criteria

1. The engine sends the **registration form once** at start, then the **liveness form** on a configurable interval with a default comfortably shorter than the server's expiry window
2. The catalog payload is built from `ICatalog.ToCatalogInfo()` and serialized **once** at start; it is retained for re-registration but never rebuilt or re-serialized per beat
3. On receiving the re-register signal (Requirement 1 AC3), the loop immediately sends the registration form and then resumes the liveness cadence — recovering from server restart, pruning, or a partition without operator action
4. Heartbeat failures never crash the engine or interrupt RPC/pub-sub traffic: transient failures are retried per the 004.1 classification, permanent ones are logged
5. The heartbeat loop participates in engine lifecycle: it starts with the engine and stops during drain, following the same two-token pattern as the worker and consumer loops
6. On graceful shutdown the node signals departure so operators see it leave promptly rather than after the expiry window; the mechanism is defined in the design
7. Heartbeat can be disabled by configuration for tests and for callers that do not want to appear in the registry, with `HW.DISCOVER` behavior in that state documented
8. The interval and enabled flag are validated with the other options at `AddHighway`
9. Steady-state beat cost is bounded and independent of catalog size — a node hosting 200 services beats with the same payload as one hosting none

### Requirement 6: Fast-Fail on Unknown Service

**User Story:** As a caller, I want an immediate 404 when no node hosts the service I am calling, so that a misconfiguration surfaces in milliseconds instead of after a 30-second timeout.

#### Acceptance Criteria

1. When enabled, `ExecuteAsync` consults discovery before enqueuing and returns 404 data (`SERVICE_NOT_FOUND`) when zero live nodes host the service — nothing is enqueued
2. The 404 is data on the response, consistent with 005's error contract; `ExecuteAsync` still never throws for service-level outcomes
3. Fast-fail is **off by default**: it trades a round trip on every call for a faster failure, and that trade belongs to the application
4. Discovery results are cached for a short configurable window so fast-fail does not add a round trip to every call in a hot loop
5. A stale-cache false negative cannot lose a request: if discovery says zero but the service is in fact hosted, the call is still enqueued (the cache is an optimization, never an authority) — or the design documents the alternative and its consequence explicitly
6. With fast-fail disabled, `ExecuteAsync` behaves exactly as it does today — enqueue, then wait for reply or timeout
7. The existing local-catalog 404 (005) still short-circuits first and costs no network round trip

### Requirement 7: Registry Observability Through Existing Surfaces

**User Story:** As an operator debugging a live system, I want the registry reachable with ordinary tools, so that I can inspect topology from `redis-cli`.

#### Acceptance Criteria

1. All three commands are reachable over standard RESP via `redis-cli` and StackExchange.Redis `Execute()`
2. Registry keys live under the documented `hw:` namespace and never collide with stock Garnet data or with 004's queue/group keys
3. Reply shapes are documented in the design and stable — feature 002's flight recorder and any future dashboard build on them
4. Structured logging records node registration, staleness detection, and pruning at appropriate levels

### Requirement 8: Testing

**User Story:** As a Highway contributor, I want the registry covered at both levels, so that liveness semantics are provably correct and the pruning path cannot silently lose work.

#### Acceptance Criteria

1. Unit tests cover heartbeat payload construction, the client loop's lifecycle and failure handling, discovery caching, and fast-fail decision logic with a mocked connection
2. Integration tests drive all three commands against a real `HighwayTestServer` over RESP with no external infrastructure
3. A test proves expiry: a node that stops heartbeating disappears from `HW.DISCOVER` after the configured window
4. A test proves Requirement 4 AC4 — a pruned node's unacknowledged requests are redelivered, not lost
5. A test proves Requirement 4 AC5 — a pruned node's subscriber group and its pending messages survive, and drain when the node returns
6. A test proves fast-fail returns 404 without enqueuing, and that disabling it restores timeout behavior
7. An end-to-end test through real engines asserts that a started node becomes discoverable and a gracefully stopped node stops being discoverable
8. Test naming follows `[Method]_[Scenario]_[ExpectedBehavior]`; xUnit + FluentAssertions + NSubstitute per coding standards

## Non-Goals

- **Load-balancing decisions based on the registry.** Work distribution stays server-side via competing consumers on `HW.DEQUEUE`. Discovery informs fast-fail and observability, not routing.
- **Runtime register/deregister of individual services.** A node's catalog is fixed for its lifetime (assembly scanning at startup, 003). Changing it means restarting the node.
- **The web dashboard.** `HW.STATS` is the data source a dashboard would consume; the UI itself remains out of scope for v1 per `product.md`.
- **Cross-server clustering.** One Highway.Server is the registry authority. Multi-server is post-v1.
- **Health checks beyond liveness.** Heartbeat proves a process is running and connected, not that its dependencies are healthy.
- **Hot-path performance work.** The `HW.ACK`/`HW.RACK` pop-and-re-push cost recorded in 004.1 stays open; this feature removes the *unbounded node growth* half of that problem (Requirement 4 AC3) but does not restructure the list operations.

## Cross-References

- Protocol table: `docs/product/product.md` § "Highway Protocol (HW.* Commands)" — Registry Commands (read-only)
- Roadmap entry: `docs/product/roadmap.md` § "006 — Heartbeat & Service Registry"
- Catalog contract already built: `Highway.Abstractions.CatalogInfo`, `ICatalog.ToCatalogInfo()` (feature 003)
- Command implementation patterns and error contract: `docs/features/004-server-hw-commands/design.md`, `docs/features/004.1-server-remediation/design.md`
- Client engine this extends: `docs/features/005-client-server-communication/design.md` — loops, lifecycle, `HighwayConnection`
- Deferred debt discharged here: `docs/features/004.1-server-remediation/requirements.md` § "Non-Goals" (node-set pruning)
- Successor: feature 002 (flight recorder) consumes `HW.STATS` shapes
