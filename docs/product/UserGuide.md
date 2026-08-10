# Highway User Guide

Highway provides a foundation for building event-driven, distributed
microservices on .NET. It combines RPC, Pub/Sub and durable Queues into one
programming model, backed by a single broker process you run alongside your
services. The broker is itself a .NET process; there is no other infrastructure
to install or operate.

Three verbs, one rule for choosing between them:

- **Need the answer** → `ExecuteAsync`
- **One handler** → `SendAsync`
- **Many handlers** → `PublishAsync`

```csharp
services.AddHighway(o =>
{
    o.NodeName = "my-service-1";
    o.Server   = "127.0.0.1:6500";
});
```

Assembly scanning discovers your services, subscribers and processors at
startup — it covers every referenced assembly that references
`Highway.Abstractions`. Nothing is registered by hand.

---

## RPC — `ExecuteAsync`

A typed request/response call routed through the broker. The caller waits and
gets a response or an error — never a silent hang.

**Define a contract** (in a shared class library referencing only `Highway.Abstractions`):

```csharp
[Service("orders.create")]
public sealed class CreateOrder : IReturn<OrderResult>
{
    public int CustomerId { get; set; }
    public string Item { get; set; } = "";
    public int Quantity { get; set; }
}

public sealed class OrderResult : Output
{
    public string? OrderId { get; set; }
    public decimal Total { get; set; }
}
```

**Implement the service** (in whatever process hosts it):

```csharp
public sealed class CreateOrderService(IHighwayClient client)
    : AsyncService<CreateOrder, OrderResult>
{
    public override async Task<OrderResult> ExecuteAsync(
        CreateOrder request, CancellationToken ct = default)
    {
        var orderId = GenerateId();
        var total = request.Quantity * 9.99m;

        // A service can also publish events.
        await client.PublishAsync(
            new OrderPlaced { OrderId = orderId, Total = total }, ct);

        return new OrderResult
        {
            OrderId = orderId,
            Total = total,
            StatusCode = StatusCodes.Status200OK,
        };
    }
}
```

**Call it** (from any other process — or the same one):

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

Errors are data, not exceptions. A service returns a status code and an
`ErrorDetail` on the response; the caller reads it like any other field.
`ExecuteAsync` does not throw for a service-level outcome.

Multiple nodes hosting the same service share the work (competing consumers).
Start two instances and Highway load-balances between them automatically.

---

## Durable Queues — `SendAsync`

Fire-and-forget work handled by exactly one processor. The sender does not wait
for a result. At-least-once delivery: the message is retried until acknowledged
or dead-lettered.

**Define the message:**

```csharp
[Queue("invoices.generate")]
public sealed record GenerateInvoice : ISend
{
    public string OrderId { get; init; } = "";
    public decimal Total { get; init; }
}
```

**Implement the processor:**

```csharp
public sealed class InvoiceProcessor(ILogger<InvoiceProcessor> logger)
    : IProcess<GenerateInvoice>
{
    public Task ProcessAsync(GenerateInvoice message, CancellationToken ct = default)
    {
        logger.LogInformation("Generating invoice for {OrderId}", message.OrderId);
        // do the work...
        return Task.CompletedTask;
    }
}
```

**Send work to the queue:**

```csharp
var id = await client.SendAsync(new GenerateInvoice
{
    OrderId = "ORD-42",
    Total = 19.98m,
});
```

`SendAsync` returns a message ID you can use later to find it in the dead-letter
queue if something goes wrong.

Run multiple instances of the processor and they share the queue — work is
distributed, not duplicated. A message that fails repeatedly is dead-lettered
after `MaxDeliveryAttempts` rather than blocking the queue.

**Delayed send:**

```csharp
await client.SendAsync(message, delay: TimeSpan.FromMinutes(5));
```

The message becomes visible after the delay. Delivery is driven by workers
polling, so it arrives on the first poll after its time.

**Long-running work:** For jobs measured in hours, chunk them. Each message
processes one slice, checkpoints progress to your database, then enqueues the
next slice. Each message lives seconds; the job lives hours. This gives you
progress visibility, deploy safety, parallelism, and per-slice failure isolation.
See `docs/cookbook/long-running-work.md` for the full pattern.

---

## Pub/Sub — `PublishAsync`

Broadcast an event to every subscriber. Each subscribing node gets its own copy.
Delivery is durable: a node that is offline when the event is published receives
it when it restarts under the same node name.

**Define a channel message:**

```csharp
[Channel("orders.placed")]
public sealed class OrderPlaced : IPublish
{
    public string OrderId { get; set; } = "";
    public string Item { get; set; } = "";
    public decimal Total { get; set; }
}
```

**Subscribe:**

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

**Publish:**

```csharp
await client.PublishAsync(new OrderPlaced
{
    OrderId = "ORD-42",
    Item = "widget",
    Total = 19.98m,
});
```

The difference between `SendAsync` and `PublishAsync` is the deployment
consequence: run three instances of a queue processor and they **share** the work;
run three instances of a subscriber and each gets **its own copy**.

**Delayed publish:**

```csharp
await client.PublishAsync(message, delay: TimeSpan.FromMinutes(10));
```

Groups are resolved at delivery time, so a subscriber that registers during the
delay still receives the message.

---

## Redelivery Protection — `[Idempotent]`

Highway delivers at least once on every path. If a handler completes but the
acknowledgment is lost, the message is delivered again. Mark a contract
`[Idempotent]` to suppress that redelivery within a window:

```csharp
[Service("payments.charge")]
[Idempotent(WindowSeconds = 300)]
public sealed class ChargeCard : IReturn<ChargeResult> { ... }
```

This deduplicates redeliveries of the same Highway message ID. It does not
deduplicate two clicks, caller retries, or separately-issued sends — those
produce different message IDs. For those, guard with your own domain key at the
start of the handler.

---

## Shared Contracts

Contracts live in a class library that references only `Highway.Abstractions` —
a package with zero dependencies. Both the caller and the service host reference
this library and neither takes a dependency on the other.

```
MyApp.Contracts/          → references Highway.Abstractions
MyApp.OrderService/       → references Highway.Client + MyApp.Contracts
MyApp.Storefront/         → references Highway.Client + MyApp.Contracts
```

Route names are explicit (`[Service("orders.create")]`) and survive CLR type
renames. A refactored class name does not break the wire.

---

## Running the Broker

Highway.Server is the broker. Run it as a standalone process:

```csharp
var server = new HighwayServerBuilder()
    .WithPort(6500)
    .WithDataDir("./data")
    .Build();

await server.RunAsync(cancellationToken);   // returns when the token fires
```

For integration tests, embed it in-process with no external infrastructure:

```csharp
using var server = new HighwayTestServer();

services.AddHighway(o =>
{
    o.NodeName = "test-node";
    o.Server   = server.ConnectionString;
});
```

The broker is durable by default — messages survive restart via AOF. Use
`.Ephemeral()` for tests that need a throwaway broker.

---

## What Developers Can Do

- **Competing consumers** — run multiple instances of a service or processor to
  share work. No configuration, just deploy more copies.
- **Durable delivery across downtime** — stop a subscriber, publish events, restart
  the subscriber. The missed events arrive.
- **Fast-fail discovery** — set `FastFailEnabled = true` to get an immediate 404
  when no node hosts a service, instead of waiting 30 seconds.
- **Delayed messages** — schedule work or events for later delivery.
- **Dead-letter inspection** — failed messages land in a dead-letter queue with
  full failure context, inspectable via `HW.DLQ`.
- **Flight recorder** — `HW.REPLAY` queries recent operations by name and time
  range. Always on, no external observability stack needed.
- **Authentication** — set a password on the broker and pass it in the connection
  string. Required when binding beyond loopback.
- **OpenTelemetry** — Highway emits `System.Diagnostics.Activity` with no OTEL
  dependency. Subscribe with your own pipeline if you want distributed traces.

---

## Package Summary

| Package | Purpose | Who references it |
|---|---|---|
| `Highway.Abstractions` | Contracts, interfaces, attributes. Zero dependencies. | Shared contract libraries |
| `Highway.Client` | Assembly scanning, DI, engine, serialization. | Any application that hosts or calls services |
| `Highway.Server` | The broker. Garnet extension with custom HW.* commands. | Deployed as a standalone process |

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

At the storefront prompt, type `order 2 widget`: an RPC call crosses three
processes, returns a typed response, and delivers a pub/sub event — all through
the one broker.
