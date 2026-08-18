# Highway.Client

High-performance .NET client library for the [Highway distributed messaging broker](https://github.com/System-D-AB/highway).

Highway gives you **durable queues**, **publish/subscribe**, **RPC**, **distributed caching**, and **lease management** over a single high-throughput broker.

## Installation

```bash
dotnet add package Highway.Client --prerelease
```

## Quick Start

### 1. Register Highway in DI

```csharp
using Highway.Client;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHighway(options =>
{
    options.NodeName = "orders-worker-1";
    options.Server   = "127.0.0.1:6379";
});
```

### 2. Define Messages and Handlers

```csharp
using Highway.Abstractions;

[Queue("orders.process")]
public sealed class ProcessOrder : ISend
{
    public string OrderId { get; set; } = "";
}

public sealed class OrderProcessor : IProcess<ProcessOrder>
{
    public async Task ProcessAsync(ProcessOrder message, CancellationToken ct = default)
    {
        // Business logic here
    }
}
```

### 3. Send, Publish, or Call

```csharp
public class CheckoutService(IHighwayClient client)
{
    public async Task PlaceOrderAsync(string orderId)
    {
        // Queue a job (competing consumers, at-least-once delivery)
        await client.SendAsync(new ProcessOrder { OrderId = orderId });
    }
}
```

## Features

- **Three Core Verbs**:
  - `SendAsync` / `IProcess<T>`: Durable queues with competing workers and dead-lettering.
  - `PublishAsync` / `ISubscribe<T>`: Fan-out pub/sub events with named subscription groups.
  - `ExecuteAsync` / `AsyncService<TReq, TResp>`: Load-balanced asynchronous RPC.
- **Distributed Cache Integration**: Native `IDistributedCache` and zero-allocation `IBufferDistributedCache` for ASP.NET Core and `HybridCache`.
- **Resilience**: Transparent heartbeat recovery, automatic lease renewal, and configurable retries.
- **Transport Security**: Opt-in TLS 1.2/1.3 and Redis/Garnet ACL user authentication.

## Documentation & Resources

- [Highway User Guide](https://github.com/System-D-AB/highway/blob/main/docs/product/UserGuide.md)
- [Wire Protocol Specification](https://github.com/System-D-AB/highway/blob/main/docs/HIGHWAY-PROTOCOL.md)
- [Guarantees and Known Limits (constraints.md)](https://github.com/System-D-AB/highway/blob/main/docs/product/constraints.md)
- [GitHub Repository](https://github.com/System-D-AB/highway)
