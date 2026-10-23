<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)"
            srcset="docs/assets/logo/highway-1a-white-transparent-512.png">
    <img src="docs/assets/logo/highway-1a-blue-transparent-512.png"
         alt="Highway" width="140" height="140">
  </picture>
</p>

<h1 align="center">Highway</h1>

<p align="center">
  <strong>Build distributed, event-driven microservices on .NET.</strong>
</p>

---

Highway gives you **durable queues**, **publish/subscribe** and **RPC**, **Recurring jobs** — all over one
broker you run yourself, in one process.

No AWS concepts. No Azure. No RabbitMQ, no Service Bus, no gRPC, no Kafka, no Redis, No Hangfire, no Quartz, no cron container. No connection
strings to a managed service, no SDK-shaped abstractions leaking into your domain. You write plain C#
POCOs — a class per message — and Highway builds the system around them: service discovery,
load balancing, durable delivery, retries, timeouts and serialization.

The broker Highway Server is a high-performance, low-footprint, distributed .NET 10 server (and an embedded in-process
server for development and tests), so every call is a round trip through a local store rather than a hop into
somebody else's cloud. It needs the **.NET 10 SDK** and nothing else — no Docker, no external
infrastructure, not even for the integration tests.

> **3.0 — storage and replication on [SystemD.Crystal](https://github.com/System-D-AB/crystal).**
> The broker's storage now runs on Crystal, an embedded storage kernel for .NET over RocksDB: Highway's data lives
> in Crystal *boxes*, and every command commits one atomic batch across the boxes it touches. **Replication comes
> with Crystal** — WAL shipping in batches over its own port, snapshot bootstrap, epochs and fencing, offline
> compaction — so Highway **removed its own RocksDB store and its own WAL-shipping replication** and keeps only its
> failover policy (the client herd, willingness, the roster, GOODBYE, auto-rejoin). Under the same load harness, on the same
> machine, RPC handling is about **30 % faster** than Highway's own RocksDB store (p50 2.3 ms against 3.4 ms) and a
> queue message reaches its worker about **2.5× sooner** (p50 20 ms against 48 ms).
>
> 3.0 is a **breaking release**: protocol 5.0, storage format 4, and every server line in its own directory, so a
> 2.x broker is upgraded **side by side** and can be rolled back. It ships as **`3.0.0-rc.4`**, a pre-release:
> see [Status](#status), [Known limits](#known-limits) and the
> [upgrade runbook](docs/distribution/README.md#7-runbook-upgrading-from-2x-to-3-side-by-side).

---

## Durable queues

Work that must happen exactly once, survive a restart, and wait for a consumer that may not
be running yet.

```csharp
[Queue("email.send")]
public sealed class SendEmail : ISend
{
    public string To   { get; set; } = "";
    public string Body { get; set; } = "";
}

public sealed class EmailProcessor : IProcess<SendEmail>
{
    public Task ProcessAsync(SendEmail message, CancellationToken ct = default)
        => _smtp.SendAsync(message.To, message.Body, ct);
}
```

```csharp
await client.SendAsync(new SendEmail { To = "user@example.com", Body = "..." });
```

**Sending never requires a running processor.** The message waits in the queue until one
claims it — through a deployment, a crash, or a mailer that is simply down. Delivery is
at-least-once; mark a message `[Idempotent]` to have Highway suppress redelivery.

Three instances of a processor **share** the work. Failures retry with backoff and land in a
dead-letter queue that tells you why.

---

## Publish / subscribe

Facts that several parts of the system care about, each independently.

```csharp
[Channel("users.signedup")]
public sealed class UserSignedUp : IPublish
{
    public int UserId { get; set; }
}

public sealed class SendWelcomeEmail : ISubscribe<UserSignedUp>
{
    public Task SubscribeAsync(UserSignedUp message, CancellationToken ct = default) => ...;
}
```

```csharp
await client.PublishAsync(new UserSignedUp { UserId = 42 });
```

Every **subscription group** gets its own copy. Three instances of a subscriber each receive
the message; three instances of a *processor* split the work between them. That difference
is the reason both verbs exist.

A subscriber that is down receives what it missed when it returns.

---

## RPC

When you need the answer before you can continue.

```csharp
[Service("orders.create")]
public sealed class CreateOrder : IReturn<OrderResult>
{
    public int CustomerId { get; set; }
    public string Item    { get; set; } = "";
}

public sealed class OrderResult : Output      // Output carries StatusCode and Error
{
    public string? OrderId { get; set; }
}

public sealed class CreateOrderService : AsyncService<CreateOrder, OrderResult>
{
    public override Task<OrderResult> ExecuteAsync(CreateOrder request, CancellationToken ct = default)
        => Task.FromResult(new OrderResult { OrderId = "ORD-1", StatusCode = 200 });
}
```

```csharp
var result = await client.ExecuteAsync(new CreateOrder { CustomerId = 7, Item = "WIDGET" });
if (result.StatusCode != 200) { /* handle it — errors are data, not exceptions */ }
```

Highway routes the call to whichever node hosts the service and balances across them. You
get **an answer or a timeout, never silence**. `ExecuteAsync` does not throw on a service
failure — errors arrive as `StatusCode` and `Error`, so a failing dependency is a branch in
your code rather than a `catch` block.

**Choosing between the three is one sentence:** one handler → `SendAsync`, many handlers →
`PublishAsync`, need the answer → `ExecuteAsync`.

---

## High availability via replication

Run more than one broker and Highway tolerates a server failure. The **mechanism** is
[SystemD.Crystal](https://github.com/System-D-AB/crystal)'s: standbys follow the primary by **WAL shipping** over a
replication port of their own (the RESP port + 1), bootstrap from a snapshot, and are fenced by epochs. The
**policy** is Highway's: the master is simply the node the client herd is connected to, and the herd converges on
the highest-priority reachable successor. A new node is *syncing* — never eligible to take over — until it holds the
primary's data and has caught up.

- **No elections, no quorum, no votes.** Succession is a deterministic priority order, and
  **epoch fencing** guarantees at most one writable master — no split-brain.
- **Clients replay their own in-flight work** with the same request id across the failover, so a
  call in flight when the master dies gets its answer from the successor, not a duplicate or a loss.
- **Graceful handover** (`highways --goodbye`) drains a node before it steps down and loses nothing; failover is
  automatic and there is no auto-failback to surprise you.

Turn it on with `HighwayReplicationOptions` (start-as-replica, a primary to follow, a priority).
Replication is asynchronous: a write acknowledged in the last few milliseconds before a primary is **hard-killed**
can be missing on its successor (about one Crystal shipping window, ~10–16 ms). That window is bounded and reported,
never silent ([C9](docs/product/constraints.md)); see
[Replication & Failover](docs/design/replication-and-failover.md).

---

## Also included

- **Recurring jobs** — a schedule that sends a queue message. 
  `o.Jobs.Daily<GenerateStatements>(new TimeOnly(2, 0))`,
  `o.Jobs.Every<ReconcileLedger>(TimeSpan.FromMinutes(15))`, or a five-field cron expression.
  Schedules survive restarts; missed occurrences collapse to one catch-up fire.
- **Distributed cache** — opt in with `AddHighwayCache()` and Highway provides an
  `IDistributedCache` (and a `HybridCache` L2) backed by a **broker-local, never-replicated**
  store, so caching needs no separate Redis. It is cold after a failover and TTL-bounded — a
  cache, not a second source of truth.
- **A dashboard** — embedded in the broker, on its own port. Live message flow, a service
  catalogue, queue depths, dead letters and a flight recorder you can replay.
- **Delayed delivery, dead letters, idempotency, graceful node decommissioning** — the
  reliability machinery, in the box.

---

## Installation & Quick Start

Highway is distributed via **NuGet** for application libraries and **GitHub Releases** for standalone broker binaries:

| Channel | Carries | Target Audience |
|---|---|---|
| **NuGet** | `Highway.Abstractions`, `Highway.Client`, `Highway.Client.Hosting`, `Highway.LocalServer` | Developers building applications and running in-process tests |
| **GitHub Releases** | `highways` server archives for **Linux** (`linux-x64`, systemd unit) and **Windows** (`win-x64`, Windows service) | Operators deploying a standalone broker |

The server archive unpacks as `server/3/` under your Highway root (for example `/opt/highway/server/3`), so each
major version lives in its own directory with its own data; see the
[server distribution guide](docs/distribution/README.md).

### 1. Add NuGet Packages

In your domain contracts library (zero dependencies):
```bash
dotnet add package Highway.Abstractions --prerelease
```

In your application / worker service:
```bash
dotnet add package Highway.Client --prerelease
```

### 2. Register Highway in DI

```csharp
builder.Services.AddHighway(o =>
{
    o.NodeName = "my-service-1";
    o.Server   = "127.0.0.1:6500";
});
```

Assembly scanning finds all `ISend`, `IPublish`, `AsyncService`, and `ISubscribe` implementations automatically.

---

## Contributor & Sample Workflow

To run the sample applications or contribute to Highway:

```bash
git clone https://github.com/System-D-AB/highway.git
cd highway
dotnet build Highway.slnx
```

Run the three sample processes, one per terminal:

```bash
dotnet run --project samples/Highway.Samples.Broker        # the broker  :6500
dotnet run --project samples/Highway.Samples.OrderService  # hosts services
dotnet run --project samples/Highway.Samples.Storefront    # calls them
```

In the storefront try `invoice ORD-1` (queue), `low WIDGET 2` (pub/sub) and
`order 2 WIDGET` (RPC). Start a **second** order service and watch queue work get shared
while published events reach both. See [samples/README.md](samples/README.md).

---

## Documentation

| Document | What it covers |
|---|---|
| [User Guide](docs/product/UserGuide.md) | Every verb in depth, recurring jobs, distributed cache, running the broker |
| [Wire protocol](docs/HIGHWAY-PROTOCOL.md) | Every `HW.*` command, reply shape, error code and key. **The single definition** — test-enforced against the server in both directions |
| [Constraints](docs/product/constraints.md) | Every guarantee Highway makes, numbered, each with whether the code currently keeps it |
| [Cookbook](docs/cookbook/) | Patterns for specific problems |
| [Design docs](docs/design/) | How each capability works — protocol, queues, pub/sub, replication, storage, cache, observability |

---

## Status

**3.0 — release candidate (`3.0.0-rc.4`).** Everything 2.x does, on a new foundation:
- **Storage and replication on [SystemD.Crystal](https://github.com/System-D-AB/crystal) 1.0** — Highway's own
  RocksDB store and WAL-shipping replication are gone; `highways --compact` is Crystal's offline compaction.
- **Protocol 5.0** — one work path for RPC and queues with claim tokens, and a declarative session per node.
- **Storage format 4** — every queue, group and service in its own box, with bounded growth.
- **Failover that does not split under load** — the herd hard-kill test passes 20/20 with a starved thread pool;
  a client keeps one shared failover search per wave of failures, so hundreds of calls in flight converge together.
- **One server directory per major version** — upgrade 2.x side by side, roll back to its untouched data.
- **Server archives for Linux and Windows** (a systemd unit and a Windows service). Metrics (`Meter`) and the
  `/health` and `/ready` endpoints carry over from 2.5.

**More than 1,800 tests pass**, on Windows; the Linux archive is smoke-tested (single node and a replicated pair).
The 2.x line stays on nuget.org as `2.5.0`.

---

## Known limits

Highway declines to promise these, and says so rather than letting you find out:

- **No exactly-once delivery.** At-least-once, with `[Idempotent]` to suppress redelivery.
- **The cache is broker-local and never replicated.** It is cold after a failover and
  epoch-invalidated — a cache miss is one more trip to the system of record, not data loss
  ([C10](docs/product/constraints.md)).
- **Not a replayable log.** Pub/sub does not retain history for groups that never registered.
- **No transactional enlistment**, message priority, or per-message TTL.
- **No published benchmark suite.** The figures above come from Highway's load harness on one machine, against
  Highway's own previous store; they show a relative change, not what your hardware will do.
- **Replication is asynchronous.** A hard kill can lose writes acknowledged in the last ~10–16 ms; a planned
  GOODBYE handover loses none ([C9](docs/product/constraints.md)).
- **Prebuilt server archives are `linux-x64` and `win-x64`.** macOS is supported from source (a self-contained
  `dotnet publish`), without a prebuilt archive.
- **Retention over time is still unbounded** ([C4.1](docs/product/constraints.md), awaiting a
  breaking framing change). Storage growth itself is now bounded — the storage engine's compaction
  reclaims consumed messages ([C4.6](docs/product/constraints.md), met on the current engine).

Every one is a numbered row in [constraints.md](docs/product/constraints.md) with an
implementation status, so intent and reality can be compared line by line.

---

## Building and testing

```bash
dotnet build Highway.slnx --no-incremental   # expected: zero warnings
dotnet test Highway.slnx                     # a few minutes
```

Integration tests run against a real embedded broker in-process — no Docker, no external infrastructure.

---

## License

[MIT](LICENSE).

Highway's broker stores and replicates its data with [SystemD.Crystal](https://github.com/System-D-AB/crystal)
(MIT), which runs on [RocksDB](https://github.com/facebook/rocksdb), and uses
[StackExchange.Redis](https://github.com/StackExchange/StackExchange.Redis) (MIT) as the RESP client. One RESP output-formatter source file is vendored from
[Microsoft Garnet](https://github.com/microsoft/garnet) (MIT) with its copyright header intact —
see [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md). Garnet itself is no longer a dependency
(removed in feature 041).
