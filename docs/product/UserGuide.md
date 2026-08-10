# Highway User Guide

Highway provides a foundation for building distributed .NET applications. It
combines RPC, Pub/Sub and durable Queues into one programming model backed by a
single server process. You write plain C# objects — requests, responses, messages
— and Highway handles discovery, routing, delivery, retries, serialization and
load balancing.

No external broker. No infrastructure decisions before your first message.

---

## Getting Started

Three packages:

| Package | What it is | Who references it |
|---|---|---|
| `Highway.Abstractions` | Contracts, interfaces, attributes. Zero dependencies. | Shared contract libraries |
| `Highway.Client` | Assembly scanning, DI, engine, serialization. | Any application |
| `Highway.Server` | The broker. Runs as a standalone process. | Deployed once |

Register Highway in any .NET application:

```csharp
builder.Services.AddHighway(o =>
{
    o.NodeName = "my-service-1";
    o.Server   = "127.0.0.1:6500";
});
```

Assembly scanning discovers your services, processors and subscribers at startup.
Nothing is registered by hand.

---

## RPC

Remote procedure calls between services. A caller sends a request and waits for
a typed response. Highway routes the call through the broker to whichever node
hosts the service.

### The objects

A **request** — a plain C# class with a `[Service]` attribute and `IReturn<T>`
to declare the response type:

```csharp
[Service("orders.create")]
public sealed class CreateOrder : IReturn<OrderResult>
{
    public int CustomerId { get; set; }
    public string Item { get; set; } = "";
    public int Quantity { get; set; }
}
```

A **response** — a class extending `Output`, which carries a status code and
optional error detail:

```csharp
public sealed class OrderResult : Output
{
    public string? OrderId { get; set; }
    public decimal Total { get; set; }
}
```

A **service** — a class extending `AsyncService<TRequest, TResponse>` that
handles the request and returns the response:

```csharp
public sealed class CreateOrderService(IHighwayClient client)
    : AsyncService<CreateOrder, OrderResult>
{
    public override async Task<OrderResult> ExecuteAsync(
        CreateOrder request, CancellationToken ct = default)
    {
        var orderId = GenerateId();
        var total = request.Quantity * 9.99m;

        return new OrderResult
        {
            OrderId = orderId,
            Total = total,
            StatusCode = StatusCodes.Status200OK,
        };
    }
}
```

### Calling a service

```csharp
var result = await client.ExecuteAsync(new CreateOrder
{
    CustomerId = 42,
    Item = "widget",
    Quantity = 2,
});

if (result.StatusCode == StatusCodes.Status200OK)
    Console.WriteLine($"Order {result.OrderId} placed");
```

### Behavior

- **Errors are data.** A service returns a status code and an `ErrorDetail` on
  the response. `ExecuteAsync` does not throw for service-level outcomes.
- **Competing consumers.** Deploy multiple instances of the same service and
  Highway load-balances between them automatically.
- **Timeouts.** Callers get a response or an explicit timeout — never a silent
  hang. Default: 30 seconds.
- **Fast-fail.** Set `FastFailEnabled = true` to get an immediate 404 when no
  node hosts the service, instead of waiting for the timeout.

---

## Durable Queues

Fire-and-forget work handled by exactly one processor. The sender does not wait
for a result. Messages are durable and survive broker restarts.

### The objects

A **message** — a plain C# class (or record) with a `[Queue]` attribute and
`ISend` marker:

```csharp
[Queue("invoices.generate")]
public sealed record GenerateInvoice : ISend
{
    public string OrderId { get; init; } = "";
    public decimal Total { get; init; }
}
```

A **processor** — a class implementing `IProcess<T>` that handles the message:

```csharp
public sealed class InvoiceProcessor : IProcess<GenerateInvoice>
{
    public Task ProcessAsync(GenerateInvoice message, CancellationToken ct = default)
    {
        // Generate the invoice...
        return Task.CompletedTask;
    }
}
```

### Sending work

```csharp
var messageId = await client.SendAsync(new GenerateInvoice
{
    OrderId = "ORD-42",
    Total = 19.98m,
});
```

`SendAsync` returns a message ID. Keep it — you can use it to find the message
in the dead-letter queue if something goes wrong.

### Behavior

- **At-least-once delivery.** If a processor completes but the acknowledgment is
  lost, the message is delivered again. Handlers should be idempotent.
- **Multiple workers share the queue.** Deploy three instances and they compete
  for work — no duplication, no configuration.
- **Dead-lettering.** A message that fails repeatedly is moved to the dead-letter
  queue after `MaxDeliveryAttempts`, rather than blocking everything behind it.
- **Delayed send.** Schedule work for later:
  ```csharp
  await client.SendAsync(message, delay: TimeSpan.FromMinutes(5));
  ```
- **Long-running work.** For jobs measured in hours, chunk them: each message
  processes one slice, checkpoints progress to your database, then enqueues the
  next slice. Each message lives seconds; the job lives hours. See
  `docs/cookbook/long-running-work.md`.

---

## Recurring Jobs

Work that runs on a schedule — nightly statements, quarter-hourly reconciliation — without
deploying Hangfire, Quartz, or a cron container beside the broker.

A recurring job is **a schedule that sends a queue message**. The contract and processor are
ordinary (`[Queue]` + `ISend` + `IProcess<T>`); the schedule lives at the composition root:

```csharp
[Queue("statements.generate")]
public sealed record GenerateStatements : ISend;   // parameterless: the TYPE is the signal

services.AddHighway(o =>
{
    o.Jobs.Daily<GenerateStatements>(new TimeOnly(2, 0));      // 02:00 UTC, every day
    o.Jobs.Every<ReconcileLedger>(TimeSpan.FromMinutes(15));
    o.Jobs.Cron<PruneAudit>("0 3 * * SUN");                    // standard 5-field cron
});
```

### Behavior

- **Exactly one fire, at-least-once processing.** Each due time enqueues exactly one
  message, however many replicas run; the message then has normal queue semantics
  (competing consumers, retries, DLQ, `[Idempotent]`).
- **Durable.** Schedules survive broker restarts. Missed occurrences collapse to **one**
  catch-up fire, with the next computed from now.
- **The broker's clock, UTC.** An occurrence fires on the first worker poll after its due
  time — and not at all while no node hosts the processor (the dashboard shows this state).
- **The payload is a template**, frozen at declaration: every occurrence carries identical
  bytes. Fixed configuration goes in the registered instance
  (`o.Jobs.Daily(new Sync { Region = "EU" }, ..., name: "eu")`); per-occurrence data is
  derived by the handler from state — which is what makes catch-up fires safe.
- **Run it now**: `client.SendAsync(new GenerateStatements())` — same contract, same
  processor, no special API.
- **Overlap**: a new occurrence may fire while the previous is still being processed. If
  your job must not run twice at once, mark it `[Idempotent]` and derive work from state.

## Pub/Sub

Broadcast events to every subscriber. Each subscribing application gets its own
copy of every message. Delivery is durable — a subscriber that is offline when
the event is published receives it when it restarts.

### The objects

A **message** — a plain C# class with a `[Channel]` attribute and `IPublish`
marker:

```csharp
[Channel("orders.placed")]
public sealed class OrderPlaced : IPublish
{
    public string OrderId { get; set; } = "";
    public string Item { get; set; } = "";
    public decimal Total { get; set; }
}
```

A **subscriber** — a class implementing `ISubscribe<T>` that reacts to the
event:

```csharp
public sealed class OrderPlacedSubscriber : ISubscribe<OrderPlaced>
{
    public Task SubscribeAsync(OrderPlaced message, CancellationToken ct = default)
    {
        Console.WriteLine($"Order placed: {message.OrderId}");
        return Task.CompletedTask;
    }
}
```

### Publishing an event

```csharp
await client.PublishAsync(new OrderPlaced
{
    OrderId = "ORD-42",
    Item = "widget",
    Total = 19.98m,
});
```

### Behavior

- **Fan-out.** Every subscription group gets its own copy. By default each node
  is its own group.
- **Replicas.** To scale a subscriber horizontally *without* multiplying
  deliveries, give the replicas one `SubscriptionGroup`:
  ```csharp
  services.AddHighway(o =>
  {
      o.NodeName = $"billing-{Environment.MachineName}";  // unique per process
      o.SubscriptionGroup = "billing";                    // one logical consumer
  });
  ```
  Replicas sharing a group **compete** for that group's single copy of each
  event; other groups still receive their own.
- **Durable.** Stop a subscriber, publish events, restart it. The missed events
  arrive. A group's pending messages survive as long as **any** of its replicas
  still heartbeats.
- **Delayed publish.** Schedule an event for later:
  ```csharp
  await client.PublishAsync(message, delay: TimeSpan.FromMinutes(10));
  ```
- **At-least-once.** Delivery guarantees apply per subscriber group. Handlers
  should be idempotent.

---

## Distributed Cache

A distributed cache backed by the same Garnet broker your messaging already runs
on. No second server, no second connection string, no Redis package.

### What you get

Highway registers an `IDistributedCache` implementation in DI. It uses standard
Garnet string commands (`GET`, `SET`, `DEL`) over the existing connection. Cache
keys are prefixed (`hw:cache:` by default) so they never collide with Highway's
internal state.

For typed caching with stampede protection and L1/L2 layering, add .NET's
`HybridCache` on top. Highway provides the L2 store; `HybridCache` provides
the in-memory L1, serialization, and single-caller factory semantics.

### Registration

Caching registers automatically when you call `AddHighway`:

```csharp
builder.Services.AddHighway(o =>
{
    o.NodeName = "my-service";
    o.Server   = "127.0.0.1:6500";
});
```

If you want only the cache without the messaging engine (no services, no queues,
no pub/sub), use the standalone registration:

```csharp
builder.Services.AddHighwayCache(o =>
{
    o.Server    = "127.0.0.1:6500";
    o.KeyPrefix = "app:";  // optional, default "hw:cache:"
});
```

Both paths share the same connection when used together in one process.

### Basic usage — `IDistributedCache`

```csharp
public sealed class OrderLookup(IDistributedCache cache, IHighwayClient client)
{
    public async Task<OrderResult?> GetOrderAsync(string orderId, CancellationToken ct)
    {
        var key = $"order:{orderId}";
        var cached = await cache.GetAsync(key, ct);

        if (cached is not null)
            return JsonSerializer.Deserialize<OrderResult>(cached);

        var result = await client.ExecuteAsync(new GetOrder { OrderId = orderId }, ct);

        await cache.SetAsync(key, JsonSerializer.SerializeToUtf8Bytes(result),
            new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5)
            }, ct);

        return result;
    }
}
```

### Typed usage — `HybridCache`

Add `Microsoft.Extensions.Caching.Hybrid` to your application and register it:

```csharp
builder.Services.AddHighway(o =>
{
    o.NodeName = "my-service";
    o.Server   = "127.0.0.1:6500";
});

builder.Services.AddHybridCache(o =>
{
    o.DefaultEntryOptions = new()
    {
        Expiration = TimeSpan.FromMinutes(5),
        LocalCacheExpiration = TimeSpan.FromSeconds(30),
    };
});
```

Then inject `HybridCache` and use `GetOrCreateAsync<T>`:

```csharp
public sealed class OrderLookup(HybridCache cache, IHighwayClient client)
{
    public async Task<OrderResult> GetOrderAsync(string orderId, CancellationToken ct)
    {
        return await cache.GetOrCreateAsync(
            $"order:{orderId}",
            async token =>
            {
                return await client.ExecuteAsync(
                    new GetOrder { OrderId = orderId }, token);
            },
            cancellationToken: ct);
    }
}
```

### Behavior

- **Stampede protection.** When multiple callers request the same key
  simultaneously, `HybridCache` calls the factory once and gives the result to
  all waiters.
- **L1 + L2.** `HybridCache` holds recent entries in process memory (L1) and
  falls through to Highway's distributed cache (L2) on miss. No configuration
  — both layers activate by default.
- **Tag-based invalidation.** `HybridCache` supports tags for bulk invalidation.
  Assign tags at creation and invalidate by tag to clear related entries at once.
- **Serialization.** `System.Text.Json` by default — consistent with Highway's
  wire format. Configure alternative serializers through `HybridCache`'s
  `AddSerializer` / `AddSerializerFactory` extension points.
- **No background work.** The cache itself is stateless — no timers, no sweeps.
  TTL is managed by Garnet natively.
- **Connection failure.** If the broker is unreachable, cache operations throw
  (or return null for `Get`), same as any Redis-backed cache. No silent
  fallback.
- **Shares the broker's storage.** Entries live in the same Garnet instance as
  your queues and channels — the same memory and the same AOF on disk. Give
  entries TTLs: Highway does not enforce expiration, and a key set without one
  persists until deleted.

---

## Choosing Between Them

One sentence: **need the answer → RPC. One handler → Queue. Many handlers →
Pub/Sub. Already have the answer → Cache.**

The deployment consequence is the difference between the last two: deploy three
instances of a queue processor and they **share** the work. Deploy three
instances of a subscriber and each gets **its own copy** — unless they share a
`SubscriptionGroup`, in which case they share that copy too. The verb decides
the semantics; the subscription group decides who counts as *one* subscriber.

Caching is also available through the same server connection — no additional
infrastructure. Use it to avoid repeated RPC calls for data that changes
infrequently.

---

## Shared Contracts

Requests, responses and messages are plain C# objects. They live in a shared
class library that references only `Highway.Abstractions` — a package with zero
dependencies.

```
MyApp.Contracts/          → references Highway.Abstractions only
MyApp.OrderService/       → references Highway.Client + MyApp.Contracts
MyApp.Storefront/         → references Highway.Client + MyApp.Contracts
```

Route names are explicit (`[Service("orders.create")]`, `[Queue("invoices.generate")]`,
`[Channel("orders.placed")]`). They survive class renames — a refactored type
name does not break the wire.

Both the caller and the service host reference the contracts library. Neither
takes a dependency on the other.

---

## Redelivery Protection

Highway delivers at least once. If a handler completes but the acknowledgment is
lost, the message is delivered again. Mark a contract `[Idempotent]` to suppress
that redelivery within a window:

```csharp
[Service("payments.charge")]
[Idempotent(WindowSeconds = 300)]
public sealed class ChargeCard : IReturn<ChargeResult> { ... }
```

This deduplicates redeliveries of the same message ID. It does not deduplicate
two separate sends of logically identical work — for that, guard with your own
domain key at the start of the handler.

---

## Running the Broker

Highway.Server is the single broker process. Run it standalone:

```csharp
var server = new HighwayServerBuilder()
    .WithPort(6500)
    .WithDataDir("./data")
    .Build();

await server.RunAsync();
```

The broker is durable by default — messages survive restart via append-only file
persistence. Use `.Ephemeral()` for tests that need a disposable broker.

### For integration tests

Embed the broker in-process with no external infrastructure:

```csharp
using var server = new HighwayTestServer();

services.AddHighway(o =>
{
    o.NodeName = "test-node";
    o.Server   = server.ConnectionString;
});
```

---

## Additional Capabilities

- **Competing consumers** — deploy multiple instances of any service or processor
  to share work. No configuration needed.
- **Durable delivery across downtime** — messages queue for absent subscribers
  and drain when they return.
- **Dead-letter inspection** — failed messages land in a dead-letter queue with
  full failure context (`HW.DLQ`).
- **Flight recorder** — `HW.REPLAY` queries recent operations by name and time
  range. Always on, no external observability stack required.
- **Authentication** — set a password on the broker; required when binding beyond
  loopback.
- **TLS** — opt-in transport encryption.
- **OpenTelemetry** — Highway emits `System.Diagnostics.Activity` with no OTEL
  dependency. Subscribe with your own pipeline for distributed traces.
- **Delayed delivery** — schedule messages and events for future processing.
- **Distributed cache** — `IDistributedCache` backed by the same Garnet broker.
  Add `HybridCache` for typed caching with stampede protection and L1/L2
  layering.
- **Lease renewal** — handlers running up to 15 minutes are safe without
  configuration.

---

## Quick Start

```bash
# Terminal 1 — the broker
dotnet run --project samples/Highway.Samples.Broker

# Terminal 2 — hosts services, publishes events
dotnet run --project samples/Highway.Samples.OrderService

# Terminal 3 — calls services, subscribes to events
dotnet run --project samples/Highway.Samples.Storefront
```

At the storefront prompt type `order 2 widget` and watch an RPC call cross three
processes, return a typed response, and deliver a pub/sub event — all through one
server, with three lines of setup.
