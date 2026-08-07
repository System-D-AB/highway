# Design: Observability & Flight Recorder

> **Protocol reference.** The authoritative definition of the wire protocol —
> commands, replies, errors, keys, framing, invariants — is
> [`docs/HIGHWAY-PROTOCOL.md`](../../HIGHWAY-PROTOCOL.md) (feature 007).
> This document keeps the *reasoning* behind the decisions; that file is the
> reference for *what* the protocol is. Where they differ, that file governs.

## Overview

Two independent layers over one event stream:

1. **The flight recorder** — a bounded, in-process, volatile record of recent operations, queried with `HW.REPLAY` and measured with `HW.STATS RECORDER`.
2. **Activity emission** — `System.Diagnostics.ActivitySource` on both client and server, which any OpenTelemetry pipeline collects. Highway takes no OpenTelemetry dependency.

Either can be disabled without affecting the other.

## The Three Decisions That Shape Everything

The original spec was reviewed against the shipped system and contained three contradictions. Resolving them determined this design, so they are stated first.

### 1. The recorder lives in process memory, not in Garnet

The original stored events in the Garnet keyspace. That produced three problems at once: events entered the AOF and were replayed on recovery with replay-time timestamps, fabricating history; the recorder competed with the actual queues for the same store budget; and every recorded event became extra AOF volume on the hot path.

Holding events in ordinary managed memory owned by `HighwayServer` removes all three. It also makes the structure ours to shape, which is what makes per-name retention possible at all.

The cost is honest and small: **the recorder is volatile.** That is the correct trade for a debugging aid. Anyone needing durable retention wants the OTEL path, exported to something built for it — and the documentation says so rather than leaving it to be discovered.

### 2. Recording happens in `Finalize`, and "after the reply" is impossible

The original required emitting "after the response is sent to the client". No such hook exists. In `TransactionManager.RunTransactionProcInternal`, `Finalize` runs inside the `finally` block; only afterwards does `TryTransactionProc` write output to the wire. Everything a custom procedure can do happens before the reply.

`Finalize` is nevertheless the right place:

- it runs **after the transaction commits**, so recording cannot affect the operation's outcome;
- it runs **even when the command failed**, which is what makes recording failures possible — and failures are the most valuable thing in a flight recorder;
- it is **skipped during AOF replay**, which is exactly right. Recovery replays *work*, not *history*. This is the same property that keeps doorbells from re-ringing.

The residual cost is that recording sits between commit and reply. This design makes that cost a single in-memory append by removing serialization from the write path entirely (below).

### 3. Per-name buffers, because per-name retention demands them

The original wanted `orders.create` kept seven days and `health.ping` discarded, while storing events in shared time buckets evicted whole. Those cannot both be true: a time bucket holds every name's events, so deleting it discards all of them or none.

Each name therefore gets its own bounded ring. Retention, capacity and capture mode are per-name properties of that ring. A name configured off gets no ring at all — the cheapest possible way to say "never record this".

The property this buys, beyond satisfying the requirement, is **isolation**: a chatty health check cannot evict the history of the service you are actually debugging.

## Architecture

```
Highway.Server (process)
├── FlightRecorder                       # owns the buffers; the only writer entry point
│   ├── NameBuffer × N                   # one bounded ring per service/channel name
│   ├── RecorderSweeper                  # retention + global budget, off the hot path
│   └── RecorderMetrics                  # counters incl. drops and failures
├── Commands/
│   ├── HwReplayCommand                  # NEW — reads the recorder
│   ├── HwStatsCommand                   # EXTENDED — fourth form: RECORDER
│   └── Hw*Command                       # EXTENDED — one Record(...) call in Finalize
└── Observability/
    └── HighwayActivitySource            # server-side ActivitySource

Highway.Client
└── Observability/
    └── HighwayActivitySource            # client-side ActivitySource

Highway.Abstractions
└── Observability/
    ├── HighwayEvent                     # the event shape
    ├── HighwayEventType
    └── PayloadCapture
```

No new package references anywhere. `ActivitySource` is in-box.

## The Write Path

```
command Finalize
  └─ recorder.Record(name, type, nodeId, id, payloadRef, outcome, durationTicks)
       ├─ buffer = buffers.GetOrNull(name)      # absent when the name is Off
       ├─ if buffer is null → return            # zero cost for disabled names
       ├─ event = new HighwayEvent { ... }      # one small allocation
       └─ buffer.Append(event)                  # ring write; drops oldest when full
```

**No serialization happens here.** The original design serialized each event to JSON on the write path, which is the expensive part of recording. Events are kept as objects and serialized only when `HW.REPLAY` reads them — moving the cost from every operation to the rare query.

**No payload copy happens here either.** Commands already own their payload as a `byte[]` (`_payloadBytes` in `HwCallCommand` and friends, copied once during `Prepare`). Under `Full` the recorder holds a reference to that existing array. Under `HeadersOnly` it holds the length and drops the reference; under `Off` it never sees it.

**Nothing throws out of `Record`.** Any exception is caught, counted, and swallowed. An operation cannot fail because recording failed.

## The Buffers

```
FlightRecorder
  ConcurrentDictionary<string, NameBuffer>

NameBuffer
  fixed-size circular array of HighwayEvent
  head / count, guarded by the buffer's own lock
  approximate byte total, maintained incrementally
  Capacity, Retention, CaptureMode  (resolved once at first use)
```

Per-buffer locking rather than one global lock: names are independent, so contention only occurs between operations on the same service — which are already serialized server-side by that service's key locks.

**Eviction is layered:**

| Trigger | Action |
|---|---|
| Buffer full on append | Drop the oldest event in that buffer, increment `droppedCapacity` |
| Event older than the name's retention | Excluded from `HW.REPLAY` immediately; reclaimed by the sweep |
| Global byte budget exceeded | Sweep trims the largest buffers first, increments `droppedBudget` |

Retention is applied at **read** as well as at sweep, so a stale event is never returned merely because the sweeper has not run yet. The sweep exists to reclaim memory, not to define correctness.

**The sweeper** is a timer owned by `FlightRecorder` and disposed with it. `HighwayServer` has no host and no `IHostedService` — the original spec's Task 6 assumed one — so this lifecycle is explicit and part of the work.

## Event Shape

```csharp
public sealed class HighwayEvent
{
    public required DateTimeOffset Timestamp { get; init; }
    public required HighwayEventType EventType { get; init; }
    public required string Name { get; init; }        // service or channel
    public required string NodeId { get; init; }

    public string? RequestId { get; init; }           // opaque identifier, NOT a Guid
    public long? MessageId { get; init; }             // channel sequence, NOT a Guid

    public byte[]? Payload { get; init; }             // only under Full
    public int PayloadSize { get; init; }             // always present

    public string? ErrorCode { get; init; }           // e.g. HW_INVALID_ARG — the 004.1 contract
    public int? StatusCode { get; init; }             // client-facing Output code, where one exists
    public double? DurationMs { get; init; }

    public int SchemaVersion { get; init; } = 1;
}
```

Two corrections to the original schema, both from the protocol rather than from taste:

- **`RequestId` is a string, not a `Guid`.** The protocol defines it as an opaque identifier. The .NET client happens to generate a GUID in `"N"` format, but nothing in the protocol requires that, and a non-.NET client is free to use anything.
- **`MessageId` is a `long`, not a `Guid`.** It is the per-channel sequence counter `HW.PUBLISH` assigns.

`ErrorCode` is new. The original carried only `StatusCode`, which is the client-side `Output` code — and a command rejected during validation never produces an `Output` at all. Without `ErrorCode` the recorder could not represent the failures it exists to show.

## Which Commands Produce Which Events

| Command | Event | Note |
|---|---|---|
| `HW.CALL` | `RpcEnqueued` | payload subject to capture mode |
| `HW.DEQUEUE` | `RpcClaimed` | nil result records nothing |
| `HW.REPLY` | `RpcReplied` | |
| `HW.ACK` | `RpcAcknowledged` | |
| `HW.PUBLISH` | `Published` | records the group count |
| `HW.SUBSCRIBE` / `HW.UNSUBSCRIBE` | `GroupRegistered` / `GroupRemoved` | |
| `HW.RECEIVE` | `MessagesReceived` | one event per batch, not per message |
| `HW.RACK` | `MessageAcknowledged` | |
| `HW.HEARTBEAT` registration / `BYE` | `NodeRegistered` / `NodeDeparted` | |
| `HW.HEARTBEAT` liveness | **nothing** | see below |
| `HW.DISCOVER`, `HW.STATS`, `HW.REPLAY` | **nothing** | read-only; recording reads would drown the record |

**Liveness beats are deliberately not recorded.** Feature 006 made the liveness form fire every five seconds per node. Twenty nodes would produce four events per second of pure noise, evicting real history to store the fact that nothing happened. Registration and departure — the events that actually change topology — are recorded.

`HW.RECEIVE` records one event per batch rather than per message, for the same reason: a batch of 500 is one operation, not 500.

## HW.REPLAY

```
HW.REPLAY <name> [FROM <ts>] [TO <ts>] [LIMIT <n>] [NODE <nodeId>]
```

- `FROM` / `TO` accept an ISO-8601 timestamp or a relative offset (`-5min`, `-1h`, `-30s`)
- Omitting the range defaults to a documented recent window
- `LIMIT` has a documented default and maximum; violations use `HW_INVALID_COUNT`
- `NODE` filters to one node
- Unknown name or empty range → **empty array**, never an error, matching `HW.DISCOVER`
- Arity `-2`

Each event is returned as a flat field/value array — the same self-describing shape `HW.STATS` uses — so fields can be appended later without breaking readers. Absent values are RESP nulls; `payloadSize` is present even when `payload` is null.

Validation and error codes follow the 004.1 contract exactly. `HwReplayCommand` derives from `HighwayCommandBase` like every other command and inherits identifier validation for free.

It locks no keys: the recorder is not in the keyspace. That makes the command genuinely read-only with respect to Garnet.

## HW.STATS RECORDER

A **fourth form** of the existing command, not a new one:

```
HW.STATS RECORDER
  → kind recorder  enabled 1  names 12  events 84213  bytes 41224192
    droppedCapacity 1902  droppedBudget 0  failures 0
```

Same flat field/value shape and `kind` discriminator as the server, service and channel forms from feature 006. `RECORDER` is matched case-insensitively as a reserved name, distinguishable from a service or channel because the recorder form takes priority — documented in the protocol file, as `HW.STATS`'s existing ambiguity resolution already is.

Drop counters are cumulative since start, so an operator can tell whether history is being lost rather than only how much is held.

## Activity Emission

Two `ActivitySource` instances with documented names — one in the client, one in the server. Nothing else.

**Why not the OpenTelemetry SDK.** The original spec added OpenTelemetry and OTLP exporter packages to both `Highway.Client` and `Highway.Server`. Emitting `Activity` instead is how `HttpClient` and ASP.NET Core do it, and it is better here for three reasons: `Highway.Client` stays light for every consuming application; the application keeps control of sampling, exporters and resource attributes rather than inheriting Highway's choices; and Highway takes on no telemetry-stack version conflicts. Applications that want OTLP add the OpenTelemetry packages themselves and subscribe to the sources — one wiring block, shown in the broker sample.

Guarded by `ActivitySource.HasListeners()`, so an application collecting nothing pays approximately nothing and no payload is materialised for a span nobody will read.

Attributes follow OpenTelemetry messaging semantic conventions where they apply (`messaging.system` = `highway`, `messaging.operation`, `messaging.destination.name`, `messaging.message.id`, `messaging.client.id`), with the full mapping in the protocol file.

### Trace-context propagation — settled by spike (2026-08-07)

The W3C `traceparent` rides the envelope as a new **optional** field, `tp`:

```json
{ "v": 1, "src": "orders-1", "ts": "...", "tp": "00-<traceId>-<spanId>-01", "body": { } }
```

**The envelope version does not change.** `HighwayJson.DecodeEnvelope` validates
`v` and requires `body`, and ignores every other property. Measured both ways:

- an existing reader given an envelope **with** `tp` reads it correctly and ignores the field;
- a new reader given an envelope **without** `tp` simply finds it absent.

So the change is compatible in both directions and needs no version bump — only
documentation that the field is optional and that readers must tolerate its absence.

Also confirmed by spike:

- `ActivityContext.TryParse(traceparent)` → `StartActivity(..., parentContext)` produces a
  server-side span carrying the caller's trace ID, so the two sides link into one trace.
- `Activity.Current` survives `Task.Run`, which is the boundary `RpcWorkerLoop` crosses when
  it hands a dequeued request to the thread pool. Execution context carries it; nothing extra needed.
- With no listener, `StartActivity` returns `null` and materialises nothing — so the
  `HasListeners()` guard is about avoiding *argument* evaluation, not about avoiding the call.

Payload content never goes on an activity. Spans travel to third-party systems, and quietly shipping message bodies to a collector is not a default anyone should get by accident.

## Configuration

Server, via `HighwayServerBuilder`:

| Option | Default | Meaning |
|---|---|---|
| `Recorder.Enabled` | `true` | Master switch; false allocates nothing |
| `Recorder.DefaultCapacity` | documented | Events per name buffer |
| `Recorder.DefaultRetention` | documented | Age beyond which events are not returned |
| `Recorder.DefaultCapture` | `Full` | Payload capture mode |
| `Recorder.MaxBytes` | documented | Global budget across all buffers |
| `Recorder.Overrides[name]` | — | Per-name capacity, retention, capture |
| `Recorder.ReplayEnabled` | `true` | Serve `HW.REPLAY`; false keeps the recorder but refuses queries |
| `ActivitiesEnabled` | `true` | Server-side activity emission |

Client, via `HighwayOptions`: `ActivitiesEnabled` (default `true`).

Validated at startup through `HighwayOptionsValidator` on the client side and the builder on the server side, with messages naming the offending value — matching the pattern established in 005 and 006.

**On the `Full` default.** It matches the product's intent that the broker be useful out of the box. It also means payload content sits in server memory readable by anyone who can issue `HW.REPLAY`, and **Highway has no authentication**. The protocol file and the server options state this plainly, and `ReplayEnabled = false` is the switch for operators who want the metrics without serving the bodies.

## Testing Strategy

| File | Level | Covers |
|---|---|---|
| `Server.Tests/NameBufferTests.cs` | unit | ordering, capacity eviction, retention at read, byte accounting, drop counters |
| `Server.Tests/FlightRecorderTests.cs` | unit | per-name isolation, capture-mode stripping, disabled names allocate nothing, a throwing append cannot escape |
| `Server.Tests/ReplayArgumentTests.cs` | unit | relative and absolute timestamps, `LIMIT` bounds, every rejection code |
| `Integration/FlightRecorderTests.cs` | integration | RPC round trip produces the right events in order with the right identifiers; a **failed** command is recorded with its error code; `Off`/`HeadersOnly` retain no content; `HW.STATS RECORDER` counts |
| `Integration/ActivityTests.cs` | integration | activities emitted with documented attributes, observed with an in-process `ActivityListener` — no OpenTelemetry dependency in the test either |
| `Server.Tests/ProtocolConformanceTests.cs` | existing | fails if `HW.REPLAY` is registered but undocumented |

Three of these guard properties that only break under conditions ordinary use does not reach: per-name isolation under flood, recording failure not propagating, and no AOF growth from recording.

## Risks

| Risk | Mitigation |
|---|---|
| Recording adds latency between commit and reply | **Measured (2026-08-07 spike): 80 ns and 48 bytes per append**, holding a 512-byte payload by reference; a disabled name costs 6.8 ns (a dictionary miss). Negligible between commit and reply. Method: 200,000 appends to a locked ring, `GC.GetTotalAllocatedBytes(precise: true)`, warm. A measurement of this build on this machine — not a target, and Highway claims no performance figures |
| Holding payload references keeps large buffers alive longer than expected | Payloads are capped by `MaxPayloadBytes`; the global budget bounds the total; `HeadersOnly` removes the reference entirely and is one setting away |
| `HW.REPLAY` exposes payloads on an unauthenticated port | Documented plainly rather than hidden; `ReplayEnabled` and `HeadersOnly` both address it; consistent with Highway having no auth anywhere |
| Trace-context propagation changes the envelope | Settled by a spike before implementation; treated as a protocol change with a version bump and a documented compatibility rule |
| Recording every command drowns the record in noise | Liveness beats and read-only commands are excluded by design; `HW.RECEIVE` records per batch; per-name capture and retention are the operator's controls |
| The recorder becomes a second source of truth for state | It is explicitly volatile and documented as a debugging aid. `HW.STATS` remains the answer for "what is true now" |

## Dependencies & Constraints

- Depends on 004, 004.1, 005, 006, 007 and 010 — all merged.
- **No new package references.** `ActivitySource` and `System.Text.Json` are in-box.
- Adds one command and one `HW.STATS` form; alters no existing command's arguments, replies or timing.
- Must update `docs/HIGHWAY-PROTOCOL.md` and the samples in this feature — enforced for the command surface by `ProtocolConformanceTests`.
- Coding standards unchanged: .NET 10, file-scoped namespaces, `CancellationToken` on async APIs, zero build warnings, no external test infrastructure.

## Cross-References

- Requirements: `docs/features/002-observability/requirements.md`
- Protocol this feature extends: `docs/HIGHWAY-PROTOCOL.md`
- Command execution model and the `Finalize` phase: `docs/features/004-server-hw-commands/design.md`
- Error contract used by events and validation: `docs/features/004.1-server-remediation/design.md`
- Envelope that may carry trace context: `docs/features/005-client-server-communication/design.md`
- `HW.STATS` shape extended here: `docs/features/006-heartbeat-service-registry/design.md`
- Living-conformance obligations: `.kiro/steering/spec-workflow.md`
