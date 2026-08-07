# Highway — Product Roadmap

## Build Order

Features are ordered by dependency — each builds on the one before it. You can't observe traffic that doesn't exist, and you can't send messages to a server that hasn't been built.

```
001 ─▶ 003 ─▶ 004 ─▶ 005 ─▶ 006 ─▶ 002
 │       │       │       │       │       │
 ▼       ▼       ▼       ▼       ▼       ▼
Skeleton Scan   Server  Wire   Registry Observe
```

**Design decision:** There is no local-only dispatch. Every call goes through the server — even when caller and handler are in the same process. This gives one code path, consistent behavior (timeouts, retry, observability), and the server as single source of truth. For testing, `HighwayTestServer` embeds the server in-process with zero external infrastructure.

---

## Feature List

### 001 — Project Skeleton ✅

**Status:** Complete

Foundation: .NET 10 solution with three packages (Abstractions, Client, Server), shared build config, Garnet as a git submodule, test projects, and .slnx solution file.

**Unlocks:** Everything else.

---

### 003 — Assembly Scanning & Service Catalog

**Status:** Complete

At startup, Highway.Client scans loaded assemblies to discover `AsyncService<T,TRes>` implementations and `ISubscribe<T>` subscribers. Builds an immutable catalog with pre-compiled dispatch delegates. Registers everything in DI with proper lifetimes.

**Unlocks:** Local-only dispatch (services can be discovered and called).

---

### 004 — Server HW.* Commands

**Status:** Complete (amended by 004.1)

Highway.Server registers custom `HW.*` commands in Garnet. This is the broker brain — it manages queues, subscriber groups, routing, and acknowledgment. All atomicity guarantees live here.

**Unlocks:** A running server that can accept commands.

**Key deliverables:**
- `HW.CALL` — enqueue RPC request
- `HW.REPLY` — send RPC response
- `HW.DEQUEUE` — pop next request for processing
- `HW.ACK` — acknowledge processing complete
- `HW.PUBLISH` — durable publish to all subscriber groups
- `HW.SUBSCRIBE` / `HW.UNSUBSCRIBE` — subscriber group management
- `HW.RECEIVE` — consume messages from subscriber group
- `HW.RACK` — acknowledge pub/sub message
- Server embeddable in-process for integration tests (`HighwayTestServer`)

---

### 004.1 — Server Remediation

**Status:** Complete

Amendment to 004, not a new capability: fixed the re-subscribe backlog-duplication defect, made validation errors classifiable (`ERR HW_*` permanent vs bare `ERR Transaction failed.` transient), hardened identifier validation against control characters, made `HighwayTestServer` fully configurable (including `Restart()` on a stable port for durability tests), added the missing durability/lease/doorbell/retention coverage, corrected the bind address gap, and synced 004's spec docs with the implementation. See `docs/features/004.1-server-remediation/`.

**Unlocks:** A server whose behavior is both correct and knowable — the foundation 005's retry policy and engine lifecycle are built on.

---

### 005 — Client-Server Communication

**Status:** Complete

Highway.Client sends `HW.*` commands to Highway.Server via StackExchange.Redis over RESP. This is where location transparency happens — the same `ExecuteAsync` that dispatched locally now routes to the server when `options.Server` is configured.

**Unlocks:** Distributed communication between processes.

**Key deliverables:**
- Client sends `HW.CALL` and waits for reply (with timeout → 504)
- Client worker loop: `HW.DEQUEUE` → execute service → `HW.REPLY` → `HW.ACK`
- Client sends `HW.PUBLISH` for pub/sub
- Client subscribes to channels: `HW.SUBSCRIBE` → poll/doorbell → `HW.RECEIVE` → dispatch to local subscribers
- Doorbell pattern (RESP `SUBSCRIBE` as latency optimization + polling as safety net)
- Competing consumers (multiple nodes dequeue from same service queue)
- Call timeout with CancellationToken (default 30s)
- Connection management via SE.Redis `ConnectionMultiplexer`

---

### 006 — Heartbeat & Service Registry

**Status:** Complete

Nodes register their catalog once and then prove liveness cheaply. The server maintains a registry of which nodes host which services, enabling fast-fail (404 before timeout) and operator visibility.

**Unlocks:** The server knows what's online; operators can see their topology.

**Key deliverables:**
- `HW.HEARTBEAT <nodeId> <catalogJson>` — registration, once per node lifetime
- `HW.HEARTBEAT <nodeId>` — liveness only; replies `+REGISTER` when the server holds no record, making a wiped registry self-healing
- `HW.HEARTBEAT <nodeId> BYE` — graceful departure, runs the full teardown immediately
- `HW.DISCOVER <service>` — live nodes hosting a service, with the age of each one's last beat
- `HW.STATS [service|channel]` — queue depth, hosts, in-flight, subscriber groups, backlog
- Stale-node pruning: unacknowledged RPC work requeued, worker sets and registry cleaned — **subscriber groups deliberately untouched**
- Optional fast-fail (off by default) with a short-TTL discovery cache

**Design note:** the catalog rides the wire once per node lifetime, not once per beat. Registration and liveness are different operations sharing one command, so a node hosting 200 services beats with the same payload as one hosting none.

**Also discharges:** the `hw:svc:{service}:nodes` unbounded-growth deferral from 004.1.

---

### 002 — Observability & Flight Recorder

**Status:** Spec complete, implementation deferred

Built-in observability with zero external infrastructure. Every operation is recorded in an in-memory flight recorder (1 GB ring buffer) with millisecond timestamps and full payloads. Simultaneously exports via OpenTelemetry for integration with external stacks.

**Unlocks:** Production debugging, traffic replay, audit trails.

**Why last:** Observability hooks into every HW.* command handler. Those handlers (005-007) must exist first. Building observability before there's traffic to observe produces dead code.

**Key deliverables:**
- Flight recorder ring buffer in Garnet (configurable retention, payload capture modes)
- `HW.REPLAY <name> [FROM ts] [TO ts]` — query recorded events
- OpenTelemetry span export (OTLP gRPC/HTTP)
- Configurable per service/channel (retention, payload capture, disable)
- `HW.STATS RECORDER` — flight recorder health metrics
- Event schema in Abstractions for replay tooling

---

### 010 — Runnable Samples

**Status:** Complete

Three console apps under `samples/` — a broker, a service host, and a storefront
— plus a shared contracts library. The first time Highway ran as a deployed
system rather than inside a test host.

**Found on the first run:** a caller-only node could address nothing, because
the catalog derived addressing from locally hosted implementations. Every
`ExecuteAsync` from a pure caller returned `SERVICE_NOT_FOUND` for services
running in another process — the product's headline use case. 440 tests missed
it because every integration node scans the same assembly and hosts everything.

**Now proven end to end:** standalone broker process, RESP over a real socket
between OS processes, generic-host lifecycle, cross-assembly scanning, durable
delivery across subscriber downtime and across a broker restart, competing
consumers, and RPC plus pub/sub over a non-loopback interface.

Findings live in `samples/RUNLOG.md`. Running the samples is a recurring test:
any feature changing the protocol or public API must update and re-run them
(`.kiro/steering/spec-workflow.md` § Living Conformance).

---

## Beyond v1 (Future)

These are explicitly out of scope for the initial release but documented for future planning:

| Feature | Description |
|---|---|
| Sagas / Process Managers | Long-running workflows with compensation |
| Transactional Outbox | Atomic DB write + message publish |
| Full Dashboard | Rich web UI beyond the embedded control panel |
| Clustering | Multi-server Highway.Server deployment |
| Dead Letter Queues | Failed messages with retry policies |
| Message Scheduling | Delayed delivery (publish at future time) |
| Request Batching | Batch multiple RPC calls in one round-trip |
