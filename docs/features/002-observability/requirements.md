# Feature: Observability & Flight Recorder

## Introduction

Highway provides built-in observability with zero external infrastructure. Every operation (RPC calls, publishes, registrations, heartbeats) is recorded with millisecond timestamps and full payloads in an in-memory flight recorder. Data is simultaneously streamed via OpenTelemetry for integration with external observability stacks. The flight recorder enables production debugging and replay without requiring Jaeger, Datadog, or any external tooling.

## Glossary

- **Flight Recorder (FDR)** — An in-memory ring buffer storing structured events for every Highway operation, queryable via `HW.REPLAY`
- **Ring Buffer** — A fixed-size circular buffer that evicts oldest entries when full
- **OTEL** — OpenTelemetry, the vendor-neutral observability standard for traces, metrics, and logs
- **OTLP** — OpenTelemetry Protocol, the wire format for exporting telemetry data
- **Retention** — How long events are kept before eviction (by time or by buffer pressure)
- **Payload Capture** — The level of request/response body data included in recorded events

## Requirements

### Requirement 1: Flight Recorder Storage

**User Story:** As an operator, I want every Highway operation automatically recorded in an in-memory ring buffer, so that I can debug production issues without external observability infrastructure.

#### Acceptance Criteria

1. Every `HW.CALL`, `HW.REPLY`, `HW.DEQUEUE`, `HW.ACK`, `HW.PUBLISH`, `HW.SUBSCRIBE`, `HW.UNSUBSCRIBE`, `HW.RECEIVE`, `HW.RACK`, and `HW.HEARTBEAT` operation produces a structured event in the flight recorder
2. Each event contains: timestamp (millisecond precision), event type, service/channel name, node ID, request/message ID, payload (per capture mode), status code (if applicable), and duration (for completed operations)
3. The ring buffer has a configurable maximum memory size (default 1 GB)
4. When the buffer reaches capacity, oldest events are evicted (FIFO)
5. The flight recorder is enabled by default with no configuration required
6. Events survive Highway.Server restart via Garnet's AOF persistence (events in RAM at crash time are restored on startup)

### Requirement 2: Configurable Retention

**User Story:** As an operator, I want to configure how long events are retained per service and channel, so that I can keep important data longer and discard noisy data immediately.

#### Acceptance Criteria

1. A global default retention period is configurable (default: 24 hours)
2. Per-service retention overrides can be specified (e.g., `orders.create` → 7 days)
3. Per-channel retention overrides can be specified (e.g., `health.ping` → 0 / disabled)
4. Events exceeding their retention period are evicted regardless of buffer space remaining
5. Retention is enforced by a background cleanup process (not on the hot path)
6. Setting retention to zero for a service/channel disables recording for that name entirely

### Requirement 3: Payload Capture Modes

**User Story:** As an operator, I want to control how much payload data is captured per service/channel, so that sensitive data is not stored in the flight recorder and I can manage memory consumption.

#### Acceptance Criteria

1. Three capture modes are supported: `Full` (default), `HeadersOnly`, `Off`
2. `Full` mode captures the complete serialized request/response payload
3. `HeadersOnly` mode captures metadata (service name, node ID, request ID, timestamp, status code, size in bytes) but not the payload body
4. `Off` mode disables recording entirely for that service/channel
5. Capture mode is configurable globally and per service/channel (per-service overrides global)
6. Changing capture mode at server configuration time takes effect on restart

### Requirement 4: HW.REPLAY Command

**User Story:** As a developer debugging a production issue, I want to query the flight recorder by service/channel and time range, so that I can see exactly what happened in chronological order.

#### Acceptance Criteria

1. `HW.REPLAY <service|channel> [FROM timestamp] [TO timestamp] [LIMIT n] [NODE nodeId]` returns events in chronological order
2. When FROM/TO are omitted, defaults to last 5 minutes
3. LIMIT caps the result set (default: 100, max: 10000)
4. NODE filter restricts results to events involving a specific node
5. Results include all event fields (timestamp, type, nodeId, requestId, payload, statusCode, durationMs)
6. The command completes within 50ms for typical queries (< 1000 results)
7. Relative timestamps are supported (e.g., `FROM -5min`, `FROM -1h`)

### Requirement 5: OpenTelemetry Export

**User Story:** As an operator with an existing observability stack, I want Highway to stream telemetry data via OpenTelemetry, so that I can integrate with Jaeger, Datadog, Grafana, or any OTLP-compatible collector.

#### Acceptance Criteria

1. Every Highway operation emits an OpenTelemetry span with appropriate attributes (service name, node ID, request ID, status code, duration)
2. RPC calls produce a producer span (caller) and consumer span (handler) linked by trace context
3. Pub/Sub publishes produce a producer span, and each subscriber receive produces a consumer span
4. OTEL export is configurable via an OTLP endpoint URL
5. When no OTLP endpoint is configured, OTEL spans are still generated (no-op exporter) so the flight recorder works standalone
6. Payload inclusion in OTEL spans is optional and separately configurable from the flight recorder (default: off for OTEL, on for flight recorder)
7. Standard OTEL semantic conventions are followed where applicable (messaging.system, messaging.operation, etc.)

### Requirement 6: Observability Configuration API

**User Story:** As an operator, I want a clean configuration API for observability settings on both server and client, so that I can tune the behavior without guessing.

#### Acceptance Criteria

1. Server-side configuration via `HighwayServerBuilder.WithObservability(options => { ... })`
2. Flight recorder options: MaxMemory, DefaultRetention, PayloadCapture, per-service/channel overrides
3. OpenTelemetry options: Enabled, Endpoint, IncludePayloads, custom resource attributes
4. Client-side configuration via `HighwayOptions.Observability` for client-side spans (e.g., call duration from caller perspective)
5. Configuration is validated at startup — invalid values throw descriptive exceptions
6. All options have sensible defaults (flight recorder on, 1 GB, 24h retention, full payload, OTEL on with no endpoint)

### Requirement 7: Event Schema

**User Story:** As a developer building replay tooling, I want a well-defined event schema, so that I can deserialize flight recorder events and replay them programmatically.

#### Acceptance Criteria

1. A public `HighwayEvent` type is defined in `Highway.Abstractions` representing the event schema
2. The schema includes: EventId (Guid), Timestamp (DateTimeOffset, ms precision), EventType (enum), ServiceOrChannelName, NodeId, RequestId/MessageId, Direction (Inbound/Outbound), Payload (byte[]?), PayloadType (string), StatusCode (int?), DurationMs (double?), ParentEventId (Guid? for correlation)
3. The EventType enum covers: RpcCallEnqueued, RpcDequeued, RpcReplied, RpcAcknowledged, PublishEmitted, SubscribeRegistered, MessageReceived, MessageAcknowledged, HeartbeatSent, NodeRegistered, NodeExpired
4. Events are serializable to/from JSON using System.Text.Json
5. The schema is versioned (SchemaVersion field) for forward compatibility

### Requirement 8: Flight Recorder Metrics

**User Story:** As an operator monitoring the server, I want to see flight recorder health metrics, so that I know how much memory it's using and whether events are being dropped.

#### Acceptance Criteria

1. The following metrics are exposed: current memory usage, total events stored, events/second write rate, eviction rate (events/second evicted due to capacity), oldest event timestamp
2. Metrics are accessible via `HW.STATS RECORDER` command
3. Metrics are emitted as OTEL metrics (gauges/counters) when OTEL is enabled
4. The dashboard displays flight recorder health alongside service/channel metrics
