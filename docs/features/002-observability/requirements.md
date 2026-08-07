# Feature: Observability & Flight Recorder

## Introduction

Highway records what it does. Every operation the server performs is captured in an in-process flight recorder, queryable with `HW.REPLAY`, and emitted as a `System.Diagnostics.Activity` that any OpenTelemetry pipeline can collect. No Jaeger, no ELK, no collector required to get value — but nothing in the way of using one.

This is the last v1 feature. It is deliberately last: observability hooks into every command handler, and those handlers had to exist and settle first.

### Why this spec was rewritten

The original 002 spec was written before features 004–007 existed. Reviewed against the shipped system it contained three contradictions rather than mere staleness:

- **Per-service retention was incompatible with its own storage design.** It wanted `orders.create` kept 7 days and `health.ping` discarded, while storing events in shared *time* buckets evicted whole. You cannot delete one service's old events from a bucket holding another's.
- **Its central performance premise was unachievable.** It required emitting "after the response is sent to the client". Inside a Garnet custom transaction procedure there is no such hook: `Finalize` runs in `TransactionManager`'s `finally` block and only then does `TryTransactionProc` write output to the wire.
- **Durability was undeliverable either way.** Recording inside `Main` means AOF replays the write with replay-time timestamps, corrupting history on every restart. Recording in `Finalize` means it is skipped during replay by design. The original required events to survive restart via AOF; neither path delivers that.

It also predated the 004.1 error contract, the 006 registry, the enforced protocol file (007), and the samples (010). This rewrite resolves the contradictions and takes the obligations on.

## Glossary

- **Flight recorder** — A bounded, in-process record of recent operations, queryable via `HW.REPLAY`. Volatile by design.
- **Name** — A service name or channel name. Recording is configured per name.
- **Name buffer** — The bounded ring of events for one name. Independent capacity and retention.
- **Capture mode** — How much of a payload is recorded: `Full`, `HeadersOnly`, or `Off`.
- **Activity** — .NET's built-in distributed-tracing primitive (`System.Diagnostics.Activity`). OpenTelemetry collects these; emitting them requires no OpenTelemetry dependency.

## Requirements

### Requirement 1: The Recorder Is In-Process and Volatile

**User Story:** As an operator, I want recent operations recorded automatically with no setup, so that I can diagnose a live problem without having deployed observability infrastructure in advance.

**Design consequence being fixed:** the original spec stored events in Garnet's keyspace and required them to survive restart via AOF. That is not deliverable — see the introduction — and it also put the recorder in competition with the actual queues for the same store.

#### Acceptance Criteria

1. The flight recorder is an in-process component of `Highway.Server`, holding events in ordinary managed memory. It does **not** write to the Garnet keyspace and does **not** appear in AOF
2. The recorder is **explicitly volatile**: its contents are lost when the server stops, and this is stated in the protocol documentation rather than left to be discovered
3. Recording is **best-effort**. A failure to record never fails, delays, or alters the operation being recorded
4. The recorder is enabled by default and requires no configuration to be useful
5. The recorder can be disabled entirely, in which case it allocates nothing and adds no measurable cost
6. Because the recorder is volatile, the documentation directs anyone needing a durable audit trail to the Activity/OTEL path (Requirement 8), which exports continuously to a system that does persist

### Requirement 2: Where Recording Happens

**User Story:** As a Highway maintainer, I want recording to sit at one well-defined point in command execution, so that the cost is understood and no handler can forget to do it.

#### Acceptance Criteria

1. Recording happens in the command's `Finalize` phase — after the transaction commits, before the reply is written
2. The documentation states plainly that "after the reply is sent" is **not** achievable inside a Garnet custom transaction procedure, and that `Finalize` is the closest available point
3. The hot-path cost of recording one operation is an in-memory append. **No JSON serialization happens on the write path** — events are held as objects and serialized only when `HW.REPLAY` reads them
4. Payload capture reuses the byte array the command already owns; recording introduces no additional copy
5. `Finalize` is skipped during AOF replay, and this is correct and deliberate: recovery replays *work*, not *history*, and re-recording would fabricate events at replay time
6. Recording captures **failed** operations as well as successful ones, including commands rejected during validation. A flight recorder that only shows successes is worth little
7. An operation whose recording throws is still completed and still replied to; the failure is counted (Requirement 7) and never propagated

### Requirement 3: Per-Name Retention and Capacity

**User Story:** As an operator, I want noisy names discarded and important names kept, so that the memory budget is spent on data I will actually look at.

**Design consequence being fixed:** per-name retention is why events are held in **per-name buffers** rather than shared time buckets. The original design's global time-bucketing made this requirement unimplementable.

#### Acceptance Criteria

1. Each name has its own bounded buffer, so one high-volume name cannot evict another's history
2. Each buffer has a maximum event count and a retention window, both configurable globally and overridable per name
3. When a buffer is full, the oldest event is dropped to make room — writes are never rejected and never block
4. Events older than their name's retention are not returned by `HW.REPLAY` and are reclaimed by a periodic sweep
5. Setting a name's retention or capacity to zero disables recording for that name entirely, allocating no buffer
6. A global memory budget bounds the recorder as a whole; when exceeded, the sweep reclaims from the largest buffers first, and the fact that data was dropped is observable (Requirement 7)
7. The sweep runs off the hot path and never blocks a command

### Requirement 4: Payload Capture Modes

**User Story:** As an operator handling sensitive data, I want to control what payload content is retained per name, so that recording never becomes a data-protection problem.

#### Acceptance Criteria

1. Three modes: `Full`, `HeadersOnly`, `Off`
2. `Full` retains the complete payload bytes
3. `HeadersOnly` retains metadata — name, node, identifiers, timestamp, status, **payload size** — but no payload content
4. `Off` records nothing for that name
5. The mode is configurable globally and per name, with the per-name value winning
6. The default is documented together with its consequence: `Full` means payload content sits in server memory and is readable by anyone who can issue `HW.REPLAY`, and **Highway has no authentication**. The documentation states this plainly rather than burying it
7. `HW.REPLAY` can be disabled independently of recording, so an operator can keep the recorder for metrics while refusing to serve payloads

### Requirement 5: HW.REPLAY

**User Story:** As a developer debugging a live incident, I want to ask the server what just happened to a service, so that I can see the sequence without reproducing it.

#### Acceptance Criteria

1. `HW.REPLAY <name> [FROM <ts>] [TO <ts>] [LIMIT <n>] [NODE <nodeId>]` returns that name's events in chronological order
2. `FROM` and `TO` accept both an absolute timestamp and a relative offset (`-5min`, `-1h`, `-30s`); omitting them defaults to a documented recent window
3. `LIMIT` has a documented default and maximum; exceeding the maximum is a validation error using the 004.1 code contract
4. `NODE` restricts results to events involving one node
5. An unknown name, or one with no events in range, returns an **empty array** — never an error
6. Each event is returned as a self-describing field/value structure, so fields can be added later without breaking readers
7. Payload content appears only for events recorded under `Full`; otherwise the payload field is null and the size field is still present
8. Invalid arguments are rejected with `ERR HW_` codes consistent with the 004.1 error contract
9. The command is read-only and safe to issue against a live server

### Requirement 6: Event Schema

**User Story:** As someone building tooling on the recorder, I want a defined event shape that matches the protocol's real identifiers, so that I can parse results without guessing.

**Contract drift being fixed:** the original schema typed both `RequestId` and `MessageId` as `Guid`. In the shipped protocol a request ID is an **opaque identifier string** and a message ID is a **long** sequence number.

#### Acceptance Criteria

1. A public event type is defined in `Highway.Abstractions` with no dependencies beyond it
2. Identifiers match the wire protocol: request ID is a **string**, message ID is a **long**, node ID and name are strings
3. The event carries at minimum: timestamp, event type, name, node ID, the relevant identifier, payload (nullable) and payload size, outcome, and duration where meaningful
4. **Outcome covers failure**, including the 004.1 error code where the operation was rejected — not only the client-facing `Output` status, which does not exist for a rejected command
5. The event type enumeration covers the RPC lifecycle, the pub/sub lifecycle, and registry events, and is documented against the commands that produce each
6. The schema carries a version field
7. The type round-trips through `System.Text.Json`

### Requirement 7: Recorder Metrics

**User Story:** As an operator, I want to know whether the recorder is keeping up and what it is costing, so that it does not become an invisible problem.

#### Acceptance Criteria

1. `HW.STATS RECORDER` reports at least: enabled state, number of names being recorded, total events held, approximate bytes held, events dropped to capacity, events dropped to the global budget, and recording failures
2. The reply uses the **same flat field/value shape with a `kind` discriminator** as the three `HW.STATS` forms shipped in feature 006 — this is a fourth form of an existing command, not a new shape
3. Drop counters are cumulative since start, so an operator can tell whether history is being lost
4. The metrics are read-only and safe to poll
5. Metrics remain available when the recorder is disabled, reporting that state rather than erroring

### Requirement 8: Activity Emission, Not an OpenTelemetry Dependency

**User Story:** As an application developer, I want Highway's traces to appear in whatever OpenTelemetry setup I already have, without Highway forcing its own telemetry stack into my dependency tree.

**Design change from the original spec:** the original added the OpenTelemetry SDK and OTLP exporter packages to **both** `Highway.Server` and `Highway.Client`. This version emits `System.Diagnostics.ActivitySource` only — in-box, zero dependencies — and lets the hosting application wire OpenTelemetry. That is how `HttpClient` and ASP.NET Core do it, it keeps `Highway.Client` light, and it gives the application full control over sampling, exporters and resource attributes.

#### Acceptance Criteria

1. Neither `Highway.Client` nor `Highway.Server` takes a dependency on any OpenTelemetry package
2. Both emit activities from named `ActivitySource` instances, with the source names documented so an application can subscribe to them
3. Client-side: an RPC call and a publish each produce an activity spanning the caller's view of the operation
4. Server-side: command execution produces activities carrying the name, node, identifier and outcome
5. Trace context propagates from caller to server so a distributed trace links the two, and the mechanism is documented — including whether it rides the envelope
6. Attributes follow OpenTelemetry messaging semantic conventions where they apply, with the mapping documented
7. When nothing is listening, activity emission has negligible cost — the standard `ActivitySource.HasListeners` guard is used, and no payload is materialised for a span nobody collects
8. Activity emission can be disabled independently of the flight recorder
9. Payload content is **never** placed on an activity by default; if it is made available at all, it is separately opt-in from the recorder's capture mode
10. A sample or documented snippet shows the application-side wiring, since "bring your own OTEL" is only friendly if the wiring is shown

### Requirement 9: Configuration

**User Story:** As an operator, I want observability configured in the same style as the rest of the server, so that there is nothing new to learn.

#### Acceptance Criteria

1. Server-side observability options are reachable from `HighwayServerBuilder`, consistent with the existing `With*` methods
2. Options cover: recorder enabled, global capacity and retention, global memory budget, default capture mode, per-name overrides, `HW.REPLAY` enabled, activity emission enabled
3. Client-side options extend `HighwayOptions` and cover activity emission
4. All options are validated at startup with descriptive errors naming the offending value, consistent with `HighwayOptionsValidator`
5. Every option has a documented default and every non-obvious default has its rationale in XML docs
6. The defaults produce a useful recorder with no configuration at all

### Requirement 10: Protocol and Samples Obligations

**User Story:** As the project owner, I want this feature to leave the protocol file and the samples true, because both are enforced and one of them fails the build.

#### Acceptance Criteria

1. `docs/HIGHWAY-PROTOCOL.md` is updated **within this feature**: `HW.REPLAY` added to the Command Index with its arity, the new `HW.STATS` form documented alongside the existing three, the event field/value shapes specified, and the changelog updated with a new protocol version
2. `ProtocolConformanceTests` passes — it fails the build if `HW.REPLAY` is registered but undocumented, which makes AC1 non-optional rather than remembered
3. The samples are updated and re-run within this feature, and `samples/RUNLOG.md` gains an entry
4. The storefront sample gains a `replay` command, so the flight recorder is demonstrated rather than merely described
5. The broker sample shows the application-side OpenTelemetry wiring (Requirement 8 AC10), even if only as a commented block, so the "bring your own OTEL" story is concrete
6. Any finding from the sample run is recorded in `RUNLOG.md` and fixed in the library, not worked around in the sample

### Requirement 11: Testing

**User Story:** As a contributor, I want the recorder's guarantees tested, particularly the ones that only fail under pressure.

#### Acceptance Criteria

1. Unit tests cover the buffer: ordering, capacity eviction, retention expiry, per-name isolation, capture-mode stripping, and the drop counters
2. Unit tests cover `HW.REPLAY` argument parsing, including relative timestamps and every rejection case
3. Integration tests drive `HW.REPLAY` and `HW.STATS RECORDER` over real RESP against an embedded server, with no external infrastructure
4. A test proves an RPC round trip produces the expected events with correct identifiers and ordering
5. A test proves a **failed** operation is recorded with its error code (Requirement 2 AC6)
6. A test proves per-name isolation: flooding one name does not evict another's events (Requirement 3 AC1)
7. A test proves `Off` and `HeadersOnly` retain no payload content
8. A test proves recording failure cannot fail the operation being recorded (Requirement 2 AC7)
9. A test proves activities are emitted and carry the documented attributes, using an in-process `ActivityListener` — no OpenTelemetry dependency in the test either
10. A test proves the recorder disabled costs nothing observable and that `HW.STATS RECORDER` still answers

### Requirement 12: No Regression

#### Acceptance Criteria

1. All 448 existing tests pass
2. `dotnet build` produces zero warnings
3. Command behaviour is unchanged: no reply shape, error code, or timing contract of an existing command is altered by adding recording
4. The 004 durability tests still pass — recording must not perturb AOF content or replay
5. Recording adds no AOF volume, verified by inspection of the data directory or by test

## Non-Goals

- **A durable audit log.** The recorder is volatile by design. Durable retention is what the Activity/OTEL path is for, exported to a system built for it.
- **The web dashboard.** `HW.STATS` and `HW.REPLAY` are the data sources a dashboard would consume; the UI remains out of scope for v1, and the original spec's reference to "the dashboard" was to something that does not exist.
- **Shipping an OTLP exporter.** Highway emits activities; the application chooses and configures its exporter.
- **A sampling policy engine.** Per-name capture modes and retention are the controls. Sampling belongs to the application's OTEL pipeline.
- **Authentication or authorisation for `HW.REPLAY`.** Highway has none anywhere, and this feature does not invent it. Requirement 4 AC6 requires the exposure be documented; Requirement 4 AC7 provides the off switch.
- **Client-side flight recording.** The recorder is server-side. Clients emit activities.
- **Performance targets.** No throughput or latency figure is claimed. Requirement 2 constrains the *shape* of the hot path — an in-memory append with no serialization — which is a design constraint, not a benchmark result. Highway claims no measured performance numbers anywhere, and this feature does not start.

## Cross-References

- Product intent: `docs/product/product.md` § G8, and its status table entry marking this unbuilt
- Protocol, which this feature must update: `docs/HIGHWAY-PROTOCOL.md`
- Command execution model and the `Finalize` phase: `docs/features/004-server-hw-commands/design.md`
- Error contract this feature's events and validation must use: `docs/features/004.1-server-remediation/design.md`
- `HW.STATS` shape this feature extends: `docs/features/006-heartbeat-service-registry/design.md`
- Living-conformance obligations: `.kiro/steering/spec-workflow.md`
