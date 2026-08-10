# Highway — Product Definition

> ## Implementation status
>
> *As of 2026-08-08, after features 001–018.* This document states the product's
> **intent**. Most of it is now built; some describes capability that does not
> exist yet. Neither is wrong, but they should not be confused.
>
> | Area | Status |
> |---|---|
> | Programming model (**three verbs**, class shapes, assembly scanning) | **Shipped** — G1, G3, G4 |
> | RPC and durable Pub/Sub, at-least-once both paths | **Shipped** — G2 |
> | **Queue — `SendAsync`, `[Queue]`, `IProcess<T>`** | **Shipped** — feature 014 |
> | **Pub/Sub Unification — one engine, two verbs** | **Shipped** — feature 018. `HW.RECEIVE`/`HW.RACK` removed; subscribers consume via queue commands |
> | `HW.*` protocol, Highway.Server as a Garnet extension | **Shipped** — G6 |
> | Timeouts, competing consumers, structured errors, DI scoping | **Shipped** — G7 |
> | Heartbeat, service registry, `HW.DISCOVER` / `HW.STATS` | **Shipped** |
> | **Dead letters, delayed delivery, `[Idempotent]`** | **Shipped** — feature 013 |
> | **Diagnosable failures — `HW.FAIL`, failure context** | **Shipped** — feature 015 |
> | **Authentication and TLS** | **Shipped** — feature 012. Not required on loopback, required off it; TLS opt-in always |
> | Embedded Control Panel / web dashboard | **Partially built** — flight recorder view delivered in feature 011. Server settings and catalog views are deferred |
> | Flight recorder, `HW.REPLAY`, activity emission | **Shipped** — G8. The recorder is **volatile** (in-process, lost on restart); Highway emits `Activity` and takes no OpenTelemetry dependency, so the application wires its own pipeline |
> | Running as separate processes end to end | **Proven** — feature 010, and re-run for every feature since. See [`samples/RUNLOG.md`](../../samples/RUNLOG.md) |
> | **Retention, size caps, durability by default** | **Not built** — five unmet constraints, specced as feature 016. `Build()` is still memory-only, which makes every delivery guarantee conditional |
> | `dotnet new highway-server` template | **Not built** |
> | Performance | **Uncharacterised.** No benchmark exists and no throughput target is claimed |
>
> **Guarantees, with their implementation status, are enumerated in
> [`constraints.md`](constraints.md)** — that is the authority on what Highway
> currently keeps, and this table is a summary of it.
>
> For what the protocol actually is, see
> **[`docs/HIGHWAY-PROTOCOL.md`](../HIGHWAY-PROTOCOL.md)**. For feature order and
> status, see [`roadmap.md`](roadmap.md).

## Vision

**Distributed .NET without the infrastructure tax.**

Highway gives developers three verbs — `ExecuteAsync` (RPC), `SendAsync` (Queue) and `PublishAsync` (Pub/Sub) — and handles everything else: service discovery, load balancing, durable delivery, timeouts, and serialization. The server is a Garnet extension you run as a single binary. The client is a NuGet package. No external broker. No ceremony.

Choosing between them is one sentence: **one handler → Send, many handlers → Publish, need the answer → Execute.**

| | Contract | Attribute | Handler | Verb |
|---|---|---|---|---|
| RPC | `IReturn<TResponse>` | `[Service("...")]` | `AsyncService<TReq,TRes>` | `ExecuteAsync` |
| Queue | `ISend` | `[Queue("...")]` | `IProcess<T>` | `SendAsync` |
| Pub/Sub | `IPublish` | `[Channel("...")]` | `ISubscribe<T>` | `PublishAsync` |

The deployment consequence is the point of having both of the last two: run three instances of a **queue** handler and they *share* the work; run three instances of a **subscriber** and they each get *their own copy*.

Because the server is a full Garnet instance, Highway can offer a small number of adjacent primitives — caching and locking chief among them — through the same connection and the same server, without a second piece of infrastructure.

> **Highway is a library, not a runtime.** An earlier draft of this document called
> it "a distributed application runtime for .NET" — which is, word for word, what
> Dapr's name stands for. That framing was withdrawn. It invited comparison on
> breadth (actors, workflows, pluggable state stores, eight language SDKs) against
> a product whose actual advantage is that a developer is productive in five
> minutes. Depth in delivery you can trust beats a longer feature list, and the
> category claim was not earned.

## System Constraints

The guarantees Highway makes — and, just as importantly, which of them the code currently
keeps — are enumerated in [`constraints.md`](constraints.md). Every constraint is numbered
and carries an implementation status, so intent and reality can be compared line by line
rather than inferred.

Read it before relying on a delivery guarantee: **six of sixteen are not met today**, all
of them concerning retention, storage limits and durability-by-default.

## Problem Statement

Building distributed .NET applications today means choosing between:

1. **Expensive commercial frameworks** (NServiceBus: $2K–$5K/endpoint/year; MassTransit v9: now commercial) that still require external broker infrastructure.
2. **Open-source frameworks** (Wolverine, Rebus) that are free but still require you to install and operate RabbitMQ, Azure Service Bus, or Amazon SQS alongside your application.
3. **Rolling your own** with raw broker clients, which every team does slightly differently and slightly wrong.

In all three cases, the developer must make infrastructure decisions before writing business logic. The "hello world" for distributed .NET is Docker Compose + broker + configuration + serialization decisions + error handling patterns — before a single message is sent.

## Highway's Answer

```csharp
// Define a service (any process can host this)
[Service("orders.create")]
public class CreateOrder : IReturn<OrderResult> { public int CustomerId { get; set; } }

public class CreateOrderService : AsyncService<CreateOrder, OrderResult>
{
    public override async Task<OrderResult> ExecuteAsync(CreateOrder request) => /* ... */;
}

// Call it (from any other process — or the same one)
var result = await client.ExecuteAsync(new CreateOrder { CustomerId = 42 });

// Publish an event (durable, at-least-once, held until subscribers come online)
await client.PublishAsync(new OrderCreated { OrderId = result.Id });
```

That's it. No broker configuration. No transport selection. No routing tables. No connection strings to figure out before you can think about your domain.

## Product Goals

### G1: Minimal-infrastructure development experience

`dotnet add package Highway.Client` is all an application needs. For testing, the server embeds in-process — no Docker, no cloud accounts, no external processes. For production, run `Highway.Server` as a single lightweight process (a Garnet extension). One server binary, one client package, done.

### G2: At-least-once delivery for both RPC and Pub/Sub

- **RPC:** Requests are persisted in durable queues. A request survives brief consumer absence. Callers get responses or explicit timeouts — never silent hangs.
- **Pub/Sub:** Messages are held indefinitely until subscribers come online. No message is ever silently dropped. This is a deliberate break from the old fire-and-forget semantics — durability is the default.

### G3: The simplest possible programming model

Four class shapes. Two verbs. One attribute each. That's the entire API:

| Concept | What you write | Attribute |
|---|---|---|
| Request | POCO implementing `IReturn<TResponse>` | `[Service("name")]` |
| Response | POCO extending `Output` | — |
| Service | Class extending `AsyncService<TReq, TRes>` | — |
| Channel message | POCO implementing `IPublish` | `[Channel("name")]` |
| Subscriber | Class implementing `ISubscribe<T>` | — |

No `AddService<T>()`. No routing configuration. No manual wiring. Assembly scanning discovers everything at startup.

> **Annotation (2026-08-10, feature 024):** the promise above holds **unconditionally for
> contracts** — every route a process references is addressable in every configuration — and
> **per `HostingMode` for handlers**. The default (`Implicit`) hosts handlers from every
> scanned assembly exactly as this goal describes; `Declared` and `ExplicitOnly` let a team
> require consent before a referenced library's handlers run in their process, because
> "reference equals hosting" is the accident three architecture reviews independently found.
> The goal's intent — no ceremony between writing a handler and running it — is unchanged;
> what 024 adds is the ability to *decide where that convenience stops*.

### G4: Location transparency

The same code runs whether the service is in the same process or across machines. The difference is invisible to the developer — all calls go through the server. For testing, Highway.Server embeds in-process (`HighwayTestServer`) so `dotnet test` requires zero external infrastructure:

```csharp
// Production — connect to a running Highway.Server
services.AddHighway(o => {
    o.NodeName = "orders-1";
    o.Server   = "localhost:6500";
});

// Integration tests — embedded server, ephemeral, in-memory
using var server = new HighwayTestServer();
services.AddHighway(o => {
    o.NodeName = "test-node";
    o.Server   = server.ConnectionString;
});
```

There is no "local-only mode." Every call always goes through the server. This guarantees identical behavior (timeouts, delivery guarantees, observability) regardless of deployment topology. The embedded test server makes this zero-friction for development.

### G5: Free alternative to NServiceBus

MIT-licensed, forever. No per-endpoint fees. No production license. No sales calls. Highway targets the 80% of use cases where teams need reliable RPC + durable pub/sub between services and do not need sagas, transactional outbox, or monitoring dashboards.

### G6: Purpose-built protocol for correctness and simplicity

Highway extends Garnet with custom `HW.*` commands that provide atomic, single-round-trip operations. The server manages subscription state, routing, and delivery guarantees internally. The client is thin — it issues semantic commands and receives semantic responses.

This is a deliberate choice: **we do not constrain ourselves to stock Redis/RESP commands.** Highway.Server is the only supported broker. This unlocks:
- Atomic publish-to-all-subscriber-groups (no partial delivery on crash)
- Server-side subscription management (client says "subscribe," server handles routing)
- Single-command RPC enqueue + notify (no multi-step client workflows)
- Built-in acknowledgment and lease semantics
- Server-initiated push to subscribers (no polling)

The wire format is still RESP framing (for tooling compatibility), but the commands are Highway's own.

### G7: Production-grade from day one

- Real timeouts with `CancellationToken` support
- Competing-consumer load balancing (multiple nodes hosting the same service share the work)
- Backpressure observability (queue depth as a metric)
- Structured error propagation (HTTP-style status codes, not swallowed exceptions)
- Proper DI scoping (one scope per request)
- System.Text.Json serialization (secure, fast, no `TypeNameHandling.All`)

### G8: Built-in observability and replay

Highway records every operation — every RPC call, every publish, every registration, every heartbeat — with millisecond timestamps and full payloads, stored directly in Garnet. This is a **flight recorder** that is always on by default:

- **In-memory ring buffer** (default 1 GB, configurable) holds recent events for instant querying via `HW.REPLAY`. Oldest entries are evicted when the buffer fills.
- **OpenTelemetry export** streams all events as OTEL spans/traces in real-time. Connect to Jaeger, Datadog, or any OTLP-compatible collector — or leave it unconfigured and the flight recorder still works standalone.
- **Configurable retention** per service/channel — keep order data for 7 days, skip health-check noise entirely.
- **Payload capture modes** — Full (default), HeadersOnly, or Off per service/channel for sensitive data.
- **`HW.REPLAY`** — query the flight recorder by service/channel, time range, and node. Pipe the output into a test harness to replay exact production traffic against a fixed build.

This means: zero external infrastructure for observability. No Jaeger required. No ELK stack. The broker *is* the observability store. If you want to scale long-term storage, connect OTEL. But out of the box, `HW.REPLAY orders.create FROM -5min` shows you exactly what happened.

## Non-Goals (v1)

These are explicitly out of scope for the first release:

- **Sagas / process managers** — build on the primitives later
- **Transactional outbox** — important but a v2 concern
- **Multi-transport abstraction** — Highway is not MassTransit. One server, done well.
- **Redis/Valkey compatibility** — Highway.Server is the only supported broker. We trade compatibility for correctness (atomic operations) and simplicity (thinner client).
- **Backward compatibility with Highway 0.8 wire protocol** — source-compatible API, not wire-compatible

## Target Framework

**.NET 10** (LTS, ships November 2026). No netstandard2.0 support — clean break.

## Delivery (Package Architecture)

Three NuGet packages, clean separation of concerns:

```
┌─────────────────────────────────────────────────────────────────┐
│  Highway.Server                                                  │
│  Extension of Garnet — the broker process.                       │
│  Registers custom HW.* commands (RPC, Pub/Sub, Registry).       │
│  Manages durable queues, subscriber routing, acknowledgment.    │
│  Runs as a standalone process or embedded for testing.           │
│  References: Microsoft.Garnet, Highway.Abstractions              │
└─────────────────────────────────────────────────────────────────┘
         ▲ RESP framing + HW.* commands ▲
         │                               │
┌────────┴────────────┐  ┌──────────────┴─────────────────────────┐
│  Highway.Client      │  │  Your Application (service host)       │
│  For callers and     │  │  References Highway.Client to host     │
│  publishers.         │  │  services AND call/publish.            │
│  ExecuteAsync,       │  │                                        │
│  PublishAsync,       │  │  AsyncService<T,TRes> implementations  │
│  Subscribe.          │  │  ISubscribe<T> implementations         │
│  Assembly scanning,  │  │                                        │
│  DI integration,     │  └────────────────────────────────────────┘
│  engine, catalog.    │
│  References:         │
│  Highway.Abstractions│
└──────────────────────┘
         ▲
         │
┌────────┴─────────────────────────────────────────────────────────┐
│  Highway.Abstractions                                             │
│  Shared contracts and interfaces. Zero dependencies.              │
│  IHighwayClient, IReturn<T>, IPublish, ISubscribe<T>,            │
│  Output, AsyncService<T,TRes>, [Service], [Channel],             │
│  StatusCodes, ErrorDetail.                                        │
│  This is what contract/shared assemblies reference.               │
└───────────────────────────────────────────────────────────────────┘
```

| Package | Purpose | Who references it |
|---|---|---|
| **`Highway.Abstractions`** | Contracts, interfaces, attributes, base classes. No implementation, no dependencies. | Everyone — shared contract assemblies, client apps, server. |
| **`Highway.Client`** | The client library. Engine, assembly scanning, DI wiring, serialization, sends `HW.*` commands via RESP framing. | Any application that hosts services, publishes events, or calls remote services. |
| **`Highway.Server`** | A Garnet extension — the broker. Registers custom `HW.*` commands for RPC queuing, durable pub/sub, subscriber management, and service registry. | Deployed as a standalone process in production. Embedded in-process for integration tests. |

**Why this split:**

1. **Shared contracts without baggage.** A contracts assembly (e.g. `Orders.Contracts`) references only `Highway.Abstractions` — a tiny, stable package with no transitive dependencies. Callers and service hosts both reference it.
2. **Client is thin.** `Highway.Client` issues semantic commands (`HW.CALL`, `HW.PUBLISH`, `HW.SUBSCRIBE`) and handles responses. No multi-step workflows, no client-side routing logic.
3. **Server owns correctness.** `Highway.Server` extends Garnet with custom commands that guarantee atomicity — a publish either goes to all subscriber groups or none. Subscription state, routing, and delivery tracking all live server-side.

## Highway Protocol (HW.* Commands)

**The protocol is defined in one file: [`docs/HIGHWAY-PROTOCOL.md`](../HIGHWAY-PROTOCOL.md).**

Highway.Server registers its custom commands using Garnet's C# extensibility. All use RESP
framing, so `redis-cli` and SE.Redis `Execute()` work unmodified.

| | |
|---|---|
| **RPC** | `HW.CALL`, `HW.REPLY`, `HW.DEQUEUE`, `HW.ACK` |
| **Queue** | `HW.QSEND`, `HW.QCLAIM`, `HW.QACK` |
| **Pub/Sub** | `HW.PUBLISH`, `HW.SUBSCRIBE`, `HW.UNSUBSCRIBE` |
| **Registry** | `HW.HEARTBEAT`, `HW.DISCOVER`, `HW.STATS` |
| **Operations** | `HW.REPLAY`, `HW.DLQ`, `HW.FAIL` |

Three verbs, one engine underneath. Subscribers consume through the same queue commands
(`HW.QCLAIM`/`HW.QACK`) as queue processors — on a derived queue named `{channel}@{group}`.

**No count is stated here on purpose.** An earlier version said "twelve", and by the time
anyone read it there were seventeen — the same drift this section already warns about, in
the sentence above the warning. The [Command Index](../HIGHWAY-PROTOCOL.md#command-index) is
machine-checked against a running server; this grouping is orientation only.

Exact argument orders, reply shapes, error codes, keys, entry framing, doorbell
channels and cross-command invariants all live in the protocol file, which is
enforced by `ProtocolConformanceTests` against a running server.

This section previously carried its own command table. It drifted: it described
one of `HW.HEARTBEAT`'s three forms, gave the wrong reply shapes for
`HW.DISCOVER` and `HW.STATS`, and omitted the error contract entirely. A vision
document should say *what the protocol is for*, not restate it — that is what
made the copy wrong in the first place.

### Design Principles

- **Every operation is one command, one round-trip.** The client never needs to orchestrate multi-step workflows.
- **Atomicity lives server-side.** `HW.PUBLISH` either enqueues to all groups or fails — no partial delivery.
- **Server manages state.** Subscriber group membership, node catalogs, and processing leases are server-side concerns. The client is stateless (beyond its own DI container).
- **RESP framing preserved.** Tools like `redis-cli`, RESP protocol analyzers, and SE.Redis `Execute()` all work. The commands are custom; the wire format is not.
- **Garnet durability applies.** AOF persistence means all queued messages survive server restart.

## Highway.Server — Hosting & Control Panel

### What Highway.Server actually is

Highway.Server is a **Garnet process with Highway's custom commands registered**. It's not a new server — it's Garnet, pre-configured and extended. Think of it as "Garnet + Highway plugin, packaged as one thing."

### Hosting model: user's choice

Highway.Server ships as a **library** (NuGet package), not a pre-built executable. The user decides how to host it:

```csharp
// Option 1: Console app (development, small deployments)
var server = new HighwayServerBuilder()
    .WithPort(6500)
    .WithDataDir("./data")
    .WithBindAddress("127.0.0.1")     // loopback by default; 0.0.0.0 to expose
    .Build();

// NOT BUILT: .WithDashboard(port: 7500) appears in the Control Panel section
// below as intended design. No such method exists on the builder today.

await server.RunAsync();

// Option 2: .NET Generic Host / Worker Service (production on bare metal or VM)
Host.CreateDefaultBuilder(args)
    .ConfigureHighwayServer(o => {
        o.Port = 6500;
        o.DataDir = "/var/highway/data";
        o.Dashboard.Port = 7500;
        o.Dashboard.RequireAuth = true;
    })
    .Build()
    .Run();

// Option 3: Embedded in-process (integration tests)
using var server = new HighwayTestServer(); // ephemeral port, in-memory, no disk
var connectionString = server.ConnectionString;

// Option 4: Docker / cloud — just the same binary with config from env vars
// HIGHWAY_PORT=6500 HIGHWAY_DASHBOARD_PORT=7500 dotnet Highway.Server.dll
```

We provide a `dotnet new highway-server` template that scaffolds Option 1 or 2. Users who want Docker get a `Dockerfile` in the template. Users who want a Windows Service use the standard .NET `UseWindowsService()` extension. Users who want systemd use `UseSystemd()`. We don't pick for them.

### Embedded Control Panel (Web Dashboard)

> **Partially built.** Feature 011 delivers the flight recorder view: recorder
> health, event browsing per name, and SSE live tailing. The hosting, security,
> and streaming infrastructure is shipped. Server settings, catalog views
> (services, channels, nodes), and dead letter inspection are deferred to later
> features.
>
> The `WithDashboard(...)` builder method exists and is functional. See
> [`docs/features/011-dashboard-flight-recorder/design.md`](../features/011-dashboard-flight-recorder/design.md)
> for the current implementation.

Highway.Server includes an **embedded web UI** served on a configurable HTTP port. No separate web app. No SPA build step. It's baked into the server binary.

**What it shows:**

| Section | Content |
|---|---|
| **Overview** | Server uptime, connected nodes, total services, total channels |
| **Services** | List of registered services, queue depth per service, active workers, avg response time |
| **Channels** | List of channels, subscriber groups per channel, pending message count per group |
| **Nodes** | Connected nodes, their catalogs, last heartbeat, health status |
| **Configuration** | Current server settings (port, data dir, AOF mode, cluster config) — read-only or editable depending on auth |
| **Dead Letters** | Messages that exceeded retry limits, with payload inspection and replay button |

**Implementation approach:**

- Static HTML/JS/CSS embedded as .NET resources in the assembly (like Hangfire's dashboard or Swagger UI)
- Served by a lightweight Kestrel instance on a separate port from the RESP listener
- Optional authentication (API key, or defer to reverse proxy in production)
- No external dependencies — no Node.js build, no npm, no SPA framework beyond what's embedded
- Real-time updates via Server-Sent Events (SSE) or WebSocket from the server's internal metrics

**Configuration:**

```csharp
.WithDashboard(dashboard => {
    dashboard.Port = 7500;           // default: disabled (no port = no dashboard)
    dashboard.PathPrefix = "/hw";    // useful behind reverse proxy
    dashboard.RequireApiKey = true;
    dashboard.ApiKey = "my-secret";  // or read from env/config
})
```

For production deployments behind a reverse proxy, the dashboard path can be mounted at any prefix. For development, `localhost:7500` just works.

### Configuration sources (priority order)

1. **Programmatic** — `HighwayServerBuilder` / `ConfigureHighwayServer()` in code
2. **appsettings.json** — standard .NET configuration
3. **Environment variables** — `HIGHWAY_PORT`, `HIGHWAY_DATA_DIR`, etc.
4. **Command-line args** — `--port 6500 --data-dir ./data`

Standard .NET configuration pipeline. Nothing custom, nothing surprising.

### What we do NOT build

- **A cloud-hosted managed service** — that's an ops decision, not a library decision
- **A Kubernetes operator** — if people want to run Highway in k8s, they write a Deployment spec like any other .NET app
- **An installer / MSI** — it's a `dotnet` app, `dotnet publish` handles deployment

## Success Criteria

1. A developer with no prior Highway knowledge can go from `dotnet new console` to two processes talking via RPC + pub/sub in under 5 minutes, using only `dotnet add package Highway` and code.
2. A published message with no online subscriber is delivered when the subscriber eventually starts — verified by integration test.
3. Running `dotnet test` on the Highway test suite requires zero external infrastructure.
4. API source compatibility: a service written against the Highway 0.8 programming model compiles against the new library with at most a namespace change.

## Competitive Positioning

**Highway is what NServiceBus would be if it were free, self-contained, and radically simple.**

It is not trying to replace NServiceBus for teams that need sagas, the full Particular Platform monitoring suite, and enterprise support contracts. It replaces NServiceBus for every team that said "we just need services to call each other reliably" and ended up spending $50K/year on infrastructure they use 10% of.
