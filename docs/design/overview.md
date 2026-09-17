# Highway — Overview

> **Status:** current
> **Deeper references:** the wire contract is [`docs/HIGHWAY-PROTOCOL.md`](../HIGHWAY-PROTOCOL.md);
> the guarantees, tracked line by line, are [`constraints.md`](../product/constraints.md).

**Build distributed, event-driven microservices on .NET.**

Highway gives you **durable queues**, **publish/subscribe** and **RPC** — all over one
broker you run yourself, in one process.

No AWS concepts. No Azure. No RabbitMQ, no Service Bus, no gRPC, no Kafka, no Redis. No connection
strings to a managed service, no SDK-shaped abstractions leaking into your domain. You write plain C#
POCOs — a class per message — and Highway builds the system around them: service discovery,
load balancing, durable delivery, retries, timeouts and serialization.

The broker is a high-performance, low-footprint .NET 10 server (and an embedded in-process
server for development and tests), so every call is a round trip through a local store rather than a hop into
somebody else's cloud. It needs the **.NET 10 SDK** and nothing else — no Docker, no external
infrastructure, not even for the integration tests.

## What Highway is

There is no local-only mode — every call goes through the broker, so behaviour (timeouts, delivery
guarantees, observability) is identical whether two services share a process or sit on different
machines. That **location transparency** is the point: you write and test against the same path you
run in production, and the embedded in-process broker means the test path and the production path are
the same code.

Highway is a **library, not a runtime**. It does one category of thing — reliable RPC and durable
messaging between .NET services — and does it with a programming model a developer is productive in
within minutes. It is MIT-licensed, positioned as the free, self-contained answer to per-endpoint
commercial frameworks.

## The three verbs

Choosing between them is one sentence: **one handler → Send, many handlers → Publish, need the
answer → Execute.**

| Capability | Contract | Verb | Consumers | See |
|---|---|---|---|---|
| **RPC** | `IReturn<TResponse>` | `ExecuteAsync` | Compete | [rpc.md](rpc.md) |
| **Queue** | `ISend` | `SendAsync` | Compete | [durable-queues.md](durable-queues.md) |
| **Pub/Sub** | `IPublish` | `PublishAsync` | Each group gets a copy | [pubsub.md](pubsub.md) |

The deployment consequence is the whole point of having both of the last two: run three instances
of a **queue** handler and they *share* the work; run three instances of a **subscriber** and each
gets its *own* copy. Assembly scanning discovers handlers at startup — no manual registration, no
routing tables.

## The engine

The broker is a purpose-built stack: a **RocksDB storage engine** (`IHighwayStore`) behind a
**Kestrel RESP server** that serves Highway's own `HW.*` command surface. It speaks RESP on the
wire — so `redis-cli` and StackExchange.Redis connect unmodified — but it is not a general Redis
server; it serves only the `HW.*` subset plus the handshake. Highway originally ran as a Garnet
extension; Garnet was removed once the RocksDB + RESP stack replaced it, and durability is now a
RocksDB write-ahead log with a bounded, compaction-reclaimed footprint. See
[storage-engine.md](storage-engine.md).

The default listen port is **6500** (deliberately not Redis's 6379). The broker binds loopback by
default and refuses to start bound off-loopback without authentication; TLS is available and never
required.

## The packages

| Package | Purpose |
|---|---|
| **Highway.Abstractions** | Contracts, interfaces, attributes, base classes. Zero dependencies — what shared contract assemblies reference. |
| **Highway.Client** | The client: engine, assembly scanning, DI wiring, RPC, queues, pub/sub, caching, resilience. |
| **Highway.LocalServer** | The broker in-process — `HighwayTestServer` for integration tests, `HighwayServerBuilder` for a local run. |
| **Highway.Client.Hosting** | One-line Windows-service / systemd hosting for any .NET app; optional Highway integration. |

For production the broker ships as the **`highways`** distribution (a per-RID zip from Releases)
that runs the broker and the embedded dashboard as one process — the dashboard is not a separate
package.

## What Highway deliberately is not

- **Not a Redis/Valkey substitute.** Highway.Server is the only supported broker; compatibility is
  traded for correctness (atomic operations) and a thin client.
- **Not a multi-transport abstraction.** One broker, done well — not a bus over RabbitMQ/SQS/etc.
- **Not a saga/workflow/outbox framework, and not a replayable log.** Those are out of scope; the
  primitives are here to build on.
- **No gRPC, no external cache dependency.** RPC rides the same connection as everything else, and
  an opt-in broker-local `IDistributedCache` ([distributed-cache.md](distributed-cache.md)) removes
  the reason to stand up Redis alongside.

## Map of these docs

Protocol & transport · [Storage engine](storage-engine.md) · [RPC](rpc.md) ·
[Pub/Sub](pubsub.md) · [Durable queues](durable-queues.md) ·
[Registry & heartbeat](registry-and-heartbeat.md) · [Distributed cache](distributed-cache.md) ·
[Replication & failover](replication-and-failover.md) ·
[Observability & operations](observability-and-operations.md).
