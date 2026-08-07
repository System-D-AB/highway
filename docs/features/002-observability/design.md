# Design: Observability & Flight Recorder

## Overview

The observability system has two independent layers that work in parallel:

1. **Flight Recorder** — in-memory ring buffer stored in Garnet, queryable via `HW.REPLAY`
2. **OpenTelemetry Export** — real-time streaming of spans/metrics to external collectors

Both layers consume the same event stream. The flight recorder stores full events in Garnet's memory. OTEL exports lightweight spans continuously. Either can be disabled independently.

## Architecture

```
┌────────────────────────────────────────────────────────────────────────────┐
│  Highway.Server (every HW.* command handler)                                │
│                                                                              │
│  HW.CALL ──┐                                                                │
│  HW.REPLY ─┤                                                                │
│  HW.PUBLISH┼──▶ EventEmitter ──┬──▶ FlightRecorderWriter ──▶ Garnet RAM    │
│  HW.DEQUEUE┤                   │                              (ring buffer) │
│  etc.  ────┘                   │                                            │
│                                 └──▶ OtelSpanExporter ──▶ OTLP endpoint     │
│                                                                              │
│  HW.REPLAY ──▶ FlightRecorderReader ──▶ Garnet RAM (query)                 │
│  HW.STATS RECORDER ──▶ FlightRecorderMetrics                               │
└────────────────────────────────────────────────────────────────────────────┘
```

## Flight Recorder Storage Design

### Key Schema in Garnet

```
hw:fdr:events:{bucket}          # List — events stored as serialized JSON
hw:fdr:idx:{name}               # Sorted set — score=timestamp, member=eventId
hw:fdr:meta                     # Hash — recorder metadata (size, count, oldest)
```

**Bucketing strategy:** Events are stored in time-bucketed lists (1-minute buckets) to enable efficient range queries and bulk eviction:

```
hw:fdr:events:20260806-1432     # All events from 14:32:xx on 2026-08-06
hw:fdr:events:20260806-1433     # All events from 14:33:xx
```

Benefits:
- Eviction is O(1): delete entire old bucket keys
- Range queries: find relevant buckets by time range, scan within
- No single hot key: distributes writes across bucket keys

### Event Structure

```csharp
public sealed class HighwayEvent
{
    public required Guid EventId { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required HighwayEventType EventType { get; init; }
    public required string Name { get; init; }          // service or channel name
    public required string NodeId { get; init; }
    public Guid? RequestId { get; init; }               // for RPC correlation
    public Guid? MessageId { get; init; }               // for pub/sub correlation
    public EventDirection Direction { get; init; }       // Inbound | Outbound
    public byte[]? Payload { get; init; }               // null if capture mode is Off/HeadersOnly
    public string? PayloadTypeName { get; init; }       // CLR type name of payload
    public int? PayloadSize { get; init; }              // size in bytes (always present)
    public int? StatusCode { get; init; }
    public double? DurationMs { get; init; }            // only on completion events
    public Guid? ParentEventId { get; init; }           // correlation with related event
    public int SchemaVersion { get; init; } = 1;
}

public enum HighwayEventType
{
    RpcCallEnqueued,
    RpcDequeued,
    RpcReplied,
    RpcAcknowledged,
    PublishEmitted,
    SubscribeRegistered,
    MessageReceived,
    MessageAcknowledged,
    HeartbeatSent,
    NodeRegistered,
    NodeExpired
}

public enum EventDirection
{
    Inbound,
    Outbound
}
```

### Memory Management

```
┌─────────────────────────────────────────────────────────────────┐
│  Ring Buffer (1 GB default)                                      │
│                                                                   │
│  [bucket-1432][bucket-1433][bucket-1434]...[bucket-current]      │
│       ↑ evict oldest                              write here ↑   │
│                                                                   │
│  Eviction triggers:                                              │
│  1. Total size > MaxMemory                                       │
│  2. Bucket age > retention for its service/channel               │
│  3. Periodic cleanup (every 10 seconds)                          │
└─────────────────────────────────────────────────────────────────┘
```

**Size tracking:** Each event write updates a running total in `hw:fdr:meta`. When total exceeds threshold (90% of MaxMemory), the evictor wakes and removes oldest buckets until under 80%.

### Write Path (hot path — must be fast)

```
1. Command handler executes HW.CALL (normal business logic)
2. After response sent to client, fire-and-forget event emission:
   a. Serialize event to JSON (pooled buffers)
   b. LPUSH hw:fdr:events:{currentBucket} {eventJson}
   c. ZADD hw:fdr:idx:{serviceName} {timestamp} {eventId}
   d. HINCRBY hw:fdr:meta totalSize {eventSize}
   e. HINCRBY hw:fdr:meta totalCount 1
3. In parallel: emit OTEL span (async, non-blocking)
```

**Critical:** Steps 2a-2e happen AFTER the response is sent to the client. Recording must never add latency to the business operation. If recording fails, the business operation still succeeds (recording is best-effort).

### Read Path (HW.REPLAY)

```
1. Parse time range → determine bucket keys to scan
2. For each bucket in range:
   a. LRANGE hw:fdr:events:{bucket} 0 -1
   b. Deserialize, filter by NODE if specified
3. Sort by timestamp, apply LIMIT
4. Return as RESP array
```

For indexed queries (specific service/channel):
```
1. ZRANGEBYSCORE hw:fdr:idx:{name} {fromTs} {toTs} LIMIT 0 {n}
2. Fetch events by ID from bucket lists
3. Return as RESP array
```

## OpenTelemetry Integration

### Span Structure

```
Trace: highway.rpc (distributed trace across caller → server → handler)
├── Span: highway.rpc.call (producer, on caller node)
│   Attributes: highway.service, highway.request_id, highway.node.caller
├── Span: highway.rpc.process (consumer, on handler node)  
│   Attributes: highway.service, highway.request_id, highway.node.handler, highway.status_code, highway.duration_ms
│
Trace: highway.pubsub
├── Span: highway.publish (producer)
│   Attributes: highway.channel, highway.message_id, highway.node.publisher, highway.subscriber_count
├── Span: highway.subscribe.receive (consumer, one per subscriber group)
│   Attributes: highway.channel, highway.message_id, highway.node.subscriber, highway.group
```

### Semantic Conventions

Following [OTEL Messaging Semantic Conventions](https://opentelemetry.io/docs/specs/semconv/messaging/):

| Attribute | Value |
|---|---|
| `messaging.system` | `highway` |
| `messaging.operation` | `publish` / `receive` / `process` |
| `messaging.destination.name` | service or channel name |
| `messaging.message.id` | request/message ID |
| `messaging.client.id` | node ID |

### Export Configuration

```csharp
.WithObservability(obs => {
    obs.OpenTelemetry.Enabled = true;
    obs.OpenTelemetry.Endpoint = "http://localhost:4317";     // gRPC OTLP
    obs.OpenTelemetry.Protocol = OtlpProtocol.Grpc;          // or HttpProtobuf
    obs.OpenTelemetry.IncludePayloads = false;                // don't put payloads in spans
    obs.OpenTelemetry.ResourceAttributes = new Dictionary<string, string>
    {
        ["service.name"] = "highway-server",
        ["deployment.environment"] = "production"
    };
})
```

## Configuration API Design

### Server-Side

```csharp
var server = new HighwayServerBuilder()
    .WithPort(6500)
    .WithObservability(obs =>
    {
        // Flight Recorder
        obs.FlightRecorder.Enabled = true;                          // default: true
        obs.FlightRecorder.MaxMemoryBytes = 1L * 1024 * 1024 * 1024; // default: 1 GB
        obs.FlightRecorder.DefaultRetention = TimeSpan.FromHours(24);
        obs.FlightRecorder.DefaultPayloadCapture = PayloadCapture.Full;
        obs.FlightRecorder.Overrides["health.ping"] = new ServiceObservabilityOptions
        {
            PayloadCapture = PayloadCapture.Off,
            Retention = TimeSpan.Zero       // don't record at all
        };
        obs.FlightRecorder.Overrides["orders.create"] = new ServiceObservabilityOptions
        {
            Retention = TimeSpan.FromDays(7) // keep longer
        };

        // OpenTelemetry
        obs.OpenTelemetry.Enabled = true;                           // default: true
        obs.OpenTelemetry.Endpoint = "http://otel-collector:4317";  // null = no export
        obs.OpenTelemetry.IncludePayloads = false;
    })
    .Build();
```

### Client-Side

```csharp
services.AddHighway(o =>
{
    o.Server = "localhost:6500";
    o.Observability.EmitClientSpans = true;   // default: true
    // Client-side spans (call duration from caller perspective)
    // These propagate trace context to the server
});
```

## Protocol Extension

### New Commands

| Command | Purpose |
|---|---|
| `HW.REPLAY <name> [FROM ts] [TO ts] [LIMIT n] [NODE nodeId]` | Query flight recorder |
| `HW.STATS RECORDER` | Flight recorder health metrics |

### HW.REPLAY Response Format

```
*3                          # array of 3 events
*10                         # each event is array of 10 fields
$36 eventId
$24 timestamp (ISO 8601)
$16 eventType
$14 serviceName
$8  nodeId
$36 requestId
$4  direction
$n  payload (or $-1 if null)
$3  statusCode
$6  durationMs
```

### HW.STATS RECORDER Response

```
*10
$12 memory_used
:838860800                  # bytes
$11 memory_max
:1073741824                 # bytes
$11 total_events
:1523847
$10 write_rate
:4523                       # events/sec (last 10s avg)
$13 eviction_rate
:12                         # events/sec evicted
```

## Performance Considerations

1. **Write amplification:** Each business operation produces 1-2 extra Garnet writes (event + index). At 50K RPC/sec that's 100K extra writes/sec. Garnet handles this easily (millions of ops/sec).

2. **Serialization cost:** Events serialized with System.Text.Json using source generators for zero-allocation paths. Pooled byte buffers for payload capture.

3. **Non-blocking:** Event recording happens after the response is sent. A slow recorder never delays business operations.

4. **Memory pressure:** The 1 GB cap is enforced by a background evictor. Writes are never rejected — oldest data is evicted to make room.

5. **OTEL overhead:** Spans are created with the OTEL SDK's sampler. Under extreme load, sampling can reduce overhead while still maintaining the flight recorder at full fidelity.

## Dependencies

- `System.Diagnostics.DiagnosticSource` (Activity API) — for OTEL span creation
- `OpenTelemetry` + `OpenTelemetry.Exporter.OpenTelemetryProtocol` — for OTLP export
- Garnet (already a dependency) — for flight recorder storage
