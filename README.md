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

Highway gives you **durable queues**, **publish/subscribe** and **RPC** — all over one
broker you run yourself, in one process.

No AWS concepts. No Azure. No RabbitMQ, no Service Bus, no Kafka. No connection strings to
a managed service, no SDK-shaped abstractions leaking into your domain. You write plain C#
POCOs — a class per message — and Highway builds the system around them: service discovery,
load balancing, durable delivery, retries, timeouts and serialization.

The broker is based on Microsoft Research's [Garnet](https://github.com/microsoft/garnet) so every call is a
round trip through an in-memory store rather than a hop into somebody else's cloud. It needs
the **.NET 10 SDK** and nothing else — no Docker, no external infrastructure, not even for
the integration tests.

> **Pre-1.0.** The core is complete and in use, and the packages are on nuget.org as
> `1.0.0-preview.1`. The broker distribution and two storage guarantees are still
> outstanding — see [Status](#status) and [Known limits](#known-limits).

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

## Also included

- **Recurring jobs** — a schedule that sends a queue message. No Hangfire, no Quartz, no
  cron container. `o.Jobs.Daily<GenerateStatements>(new TimeOnly(2, 0))`,
  `o.Jobs.Every<ReconcileLedger>(TimeSpan.FromMinutes(15))`, or a five-field cron expression.
  Schedules survive restarts; missed occurrences collapse to one catch-up fire.
- **Distributed cache** — Highway registers `IDistributedCache` and `IBufferDistributedCache`
  automatically, so `HybridCache` works with Highway as L2 and you get stampede protection
  and L1 layering for free.
- **A dashboard** — embedded in the broker, on its own port. Live message flow, a service
  catalogue, queue depths, dead letters and a flight recorder you can replay.
- **Delayed delivery, dead letters, idempotency, graceful node decommissioning** — the
  reliability machinery, in the box.

---

## Installation & Quick Start

Highway is distributed via **NuGet** for application libraries and **GitHub Releases** for standalone broker binaries:

| Channel | Carries | Target Audience |
|---|---|---|
| **NuGet** | `Highway.Abstractions`, `Highway.Client`, `Highway.LocalServer` | Developers building applications and running in-process tests |
| **GitHub Releases** | `highways` distribution zip (Feature 031) | Operators deploying a standalone broker daemon or service |

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

*(Optional: pass `-p:UseGarnetSource=true` if you want to build against the `libs/garnet` submodule source for local debugging. In that mode, ensure submodules are initialized with `git submodule update --init --recursive`.)*

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
| [Product](docs/product/product.md) · [Roadmap](docs/product/roadmap.md) | Vision, package architecture, what is being built next |
| [Feature specs](docs/features/) | Requirements, design and tasks for every feature ever built |

That last one is unusual and deliberate: every feature here was specified before it was
written, and the specs record the decisions that were rejected as well as the ones taken.

---

## Status

Core-complete and pre-1.0. Queues, pub/sub, RPC, dead letters, delayed delivery, recurring
jobs, distributed cache, dashboard, authentication, TLS, and NuGet packaging (`1.0.0-preview.1`) all ship today. **968 tests pass.**

**Not yet done:** the packaged broker distribution (the `highways` zip and its service
installers — until it lands, run the broker from source), metrics (`Meter`) and health
endpoints. Tracked on the [roadmap](docs/product/roadmap.md).

---

## Known limits

Highway declines to promise these, and says so rather than letting you find out:

- **No broker (Highway.Server), no system.** One broker. Durability yes, failover no.
- **No exactly-once delivery.** At-least-once, with `[Idempotent]` to suppress redelivery.
- **Not a replayable log.** Pub/sub does not retain history for groups that never registered.
- **No transactional enlistment**, message priority, or per-message TTL.
- **No characterised throughput.** No benchmark exists, so no figure is claimed anywhere.
- **Storage growth is not yet bounded** — the append-only file grows with total history and
  restart replays it. [Constraint C4.6](docs/product/constraints.md), unmet and measured,
  with the investigation recorded.

Every one is a numbered row in [constraints.md](docs/product/constraints.md) with an
implementation status, so intent and reality can be compared line by line.

---

## Building and testing

```bash
dotnet build Highway.slnx --no-incremental   # expected: zero warnings
dotnet test Highway.slnx                     # ~7 minutes
```

Integration tests run against a real embedded Garnet — no Docker, no external infrastructure.

---

## License

[MIT](LICENSE).

Highway builds against [Microsoft Garnet](https://github.com/microsoft/garnet) (MIT), included
unmodified as a git submodule pinned to a specific commit, and uses
[StackExchange.Redis](https://github.com/StackExchange/StackExchange.Redis) (MIT).
