# Tasks: Observability & Flight Recorder

## Task Dependency Graph

```
T1 (Event schema in Abstractions)
T2 (Observability config types) → depends on T1
T3 (EventEmitter interface) → depends on T1
T4 (Flight Recorder writer) → depends on T1, T2, T3
T5 (Flight Recorder reader / HW.REPLAY) → depends on T4
T6 (Flight Recorder evictor) → depends on T4
T7 (Flight Recorder metrics / HW.STATS RECORDER) → depends on T4
T8 (OpenTelemetry span emitter) → depends on T1, T3
T9 (Server integration — hook into HW.* commands) → depends on T4, T8
T10 (Client-side span emission) → depends on T8
T11 (Configuration API — server) → depends on T2, T4, T8
T12 (Configuration API — client) → depends on T2, T10
T13 (Integration tests) → depends on all above
```

## Tasks

- [ ] ### Task 1: Define Event Schema in Highway.Abstractions

**Fulfills:** Requirement 7

**Steps:**
1. Create `src/Highway.Abstractions/Observability/HighwayEvent.cs` with the full event type
2. Create `src/Highway.Abstractions/Observability/HighwayEventType.cs` enum
3. Create `src/Highway.Abstractions/Observability/EventDirection.cs` enum
4. Ensure types are serializable with System.Text.Json (public properties, init setters)
5. Add `SchemaVersion` field (default 1)

**Done criteria:**
- Types compile with zero dependencies
- Can round-trip serialize/deserialize with System.Text.Json
- Unit tests validate serialization

---

- [ ] ### Task 2: Define Observability Configuration Types

**Fulfills:** Requirement 6

**Steps:**
1. Create `src/Highway.Abstractions/Observability/PayloadCapture.cs` enum (Full, HeadersOnly, Off)
2. Create `src/Highway.Abstractions/Observability/FlightRecorderOptions.cs`
3. Create `src/Highway.Abstractions/Observability/OpenTelemetryOptions.cs`
4. Create `src/Highway.Abstractions/Observability/ObservabilityOptions.cs` (combines both)
5. Create `src/Highway.Abstractions/Observability/ServiceObservabilityOverride.cs`

**Done criteria:**
- All options have sensible defaults documented in XML comments
- Configuration types are in Abstractions (no external dependencies)
- Validation logic for invalid values (negative retention, memory > 0)

---

- [ ] ### Task 3: Define EventEmitter Interface

**Fulfills:** Requirement 1 (abstraction layer)

**Steps:**
1. Create `src/Highway.Abstractions/Observability/IEventEmitter.cs`
2. Interface: `Task EmitAsync(HighwayEvent event, CancellationToken ct)`
3. Create `src/Highway.Abstractions/Observability/NullEventEmitter.cs` (no-op implementation for local-only mode)

**Done criteria:**
- Interface defined in Abstractions
- Null implementation available for testing and local-only mode

---

- [ ] ### Task 4: Implement Flight Recorder Writer

**Fulfills:** Requirement 1, 3

**Steps:**
1. Create `src/Highway.Server/Observability/FlightRecorderWriter.cs`
2. Implements `IEventEmitter` — writes events to Garnet using bucketed keys
3. Respects `PayloadCapture` mode — strips payload when mode is HeadersOnly/Off
4. Tracks memory usage in `hw:fdr:meta` hash
5. Uses pooled serialization buffers (ArrayPool)
6. Fires asynchronously — never blocks command handlers

**Done criteria:**
- Events written to correct bucket keys
- Payload stripping works per capture mode
- Memory tracking is accurate
- Unit tests with mocked Garnet connection

---

- [ ] ### Task 5: Implement Flight Recorder Reader (HW.REPLAY)

**Fulfills:** Requirement 4

**Steps:**
1. Create `src/Highway.Server/Observability/FlightRecorderReader.cs`
2. Implement `HW.REPLAY` command parsing (name, FROM, TO, LIMIT, NODE)
3. Support relative timestamps (FROM -5min, FROM -1h)
4. Query by time-bucket range, filter, sort, limit
5. Return events as RESP array

**Done criteria:**
- HW.REPLAY returns correct events for time range
- NODE filter works
- LIMIT works
- Relative timestamps parse correctly
- Completes within 50ms for < 1000 results (benchmark test)

---

- [ ] ### Task 6: Implement Flight Recorder Evictor

**Fulfills:** Requirement 1 (memory cap), Requirement 2 (retention)

**Steps:**
1. Create `src/Highway.Server/Observability/FlightRecorderEvictor.cs`
2. Background service (IHostedService) that runs every 10 seconds
3. Evicts buckets older than retention period
4. Evicts oldest buckets when memory exceeds 90% of MaxMemory (down to 80%)
5. Removes corresponding index entries when evicting

**Done criteria:**
- Memory stays below configured max under sustained write load
- Retention-based eviction removes old data correctly
- Does not interfere with write path performance
- Unit tests with simulated time

---

- [ ] ### Task 7: Implement Flight Recorder Metrics (HW.STATS RECORDER)

**Fulfills:** Requirement 8

**Steps:**
1. Create `src/Highway.Server/Observability/FlightRecorderMetrics.cs`
2. Track: current memory, total events, write rate (rolling 10s window), eviction rate
3. Implement `HW.STATS RECORDER` command that returns these metrics
4. Expose as OTEL metrics (gauges/counters) when OTEL is enabled

**Done criteria:**
- HW.STATS RECORDER returns accurate metrics
- Write rate calculation is correct over rolling window
- Integration test validates metrics after known write load

---

- [ ] ### Task 8: Implement OpenTelemetry Span Emitter

**Fulfills:** Requirement 5

**Steps:**
1. Add OpenTelemetry packages to Highway.Server and Highway.Client
2. Create `src/Highway.Server/Observability/OtelSpanEmitter.cs` implementing `IEventEmitter`
3. Create Activity/Span per event following OTEL messaging semantic conventions
4. Set attributes: messaging.system=highway, messaging.destination.name, messaging.message.id, etc.
5. Support trace context propagation (caller → server → handler)
6. Configurable payload inclusion in spans

**Done criteria:**
- Spans created with correct attributes
- Trace context propagates across RPC calls
- Pub/sub creates linked producer/consumer spans
- No-op when no OTLP endpoint configured (zero overhead)

---

- [ ] ### Task 9: Server Integration — Hook into HW.* Commands

**Fulfills:** Requirement 1

**Steps:**
1. Modify each HW.* command handler to call `IEventEmitter.EmitAsync()` after business logic completes
2. Ensure emit happens AFTER response is sent to client (non-blocking)
3. Wire up composite emitter that fans out to both FlightRecorderWriter and OtelSpanEmitter
4. Handle emit failures gracefully (log, never crash)

**Done criteria:**
- Every HW.* operation produces events in both flight recorder and OTEL
- Business operation latency is not affected by recording (benchmark test)
- Emit failures don't crash the server

---

- [ ] ### Task 10: Client-Side Span Emission

**Fulfills:** Requirement 5 (client-side spans)

**Steps:**
1. Create `src/Highway.Client/Observability/ClientSpanEmitter.cs`
2. Wrap `ExecuteAsync` and `PublishAsync` with Activity/Span
3. Propagate trace context to server via request metadata
4. Measure call duration from client perspective

**Done criteria:**
- Client-side spans appear in OTEL traces
- Trace context is propagated to server (linked spans)
- Can be disabled via configuration

---

- [ ] ### Task 11: Server Configuration API

**Fulfills:** Requirement 6

**Steps:**
1. Create `HighwayServerBuilder.WithObservability(Action<ObservabilityOptions>)` extension
2. Wire FlightRecorderOptions → FlightRecorderWriter/Evictor
3. Wire OpenTelemetryOptions → OtelSpanEmitter + OTLP exporter
4. Validate configuration at startup (throw on invalid values)

**Done criteria:**
- Configuration flows from builder to all observability components
- Invalid config throws descriptive exceptions
- Defaults work with zero configuration

---

- [ ] ### Task 12: Client Configuration API

**Fulfills:** Requirement 6

**Steps:**
1. Add `ObservabilityOptions` to `HighwayOptions`
2. Wire `EmitClientSpans` flag to ClientSpanEmitter
3. Defaults: client spans enabled

**Done criteria:**
- Client observability configurable via `HighwayOptions.Observability`
- Disabled spans produce zero overhead

---

- [ ] ### Task 13: Integration Tests

**Fulfills:** All requirements

**Steps:**
1. Test: Perform RPC call → verify event appears in HW.REPLAY output
2. Test: Perform publish → verify event in flight recorder with correct subscriber count
3. Test: Fill buffer beyond MaxMemory → verify oldest events evicted
4. Test: Set retention → verify events expire after retention period
5. Test: PayloadCapture.Off → verify no payload in recorded events
6. Test: PayloadCapture.HeadersOnly → verify metadata present, payload null
7. Test: HW.STATS RECORDER returns correct metrics
8. Test: OTEL spans emitted (using in-memory exporter for testing)

**Done criteria:**
- All integration tests pass with embedded Highway.Server
- Tests run with no external infrastructure
- Tests validate both flight recorder and OTEL layers
