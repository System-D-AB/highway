# Highway.Abstractions

Contracts, interfaces, and declarative message attributes for the [Highway distributed messaging system](https://github.com/System-D-AB/highway).

`Highway.Abstractions` contains **zero third-party dependencies**. It is designed to be referenced by shared domain and contract assemblies without pulling in the client engine, Redis libraries, or the server runtime.

## Core Interfaces & Attributes

- **Queues**: `[Queue("name")]`, `ISend`, `IProcess<T>`
- **Pub/Sub**: `[Topic("name")]`, `IPublish`, `ISubscribe<T>`, `[SubscriptionGroup("group")]`
- **RPC**: `IReturn<TResponse>`, `AsyncService<TRequest, TResponse>`, `Output`
- **Message Semantics**: `[Idempotent]`, `[Sequential]`, `[Scheduled(seconds)]`
- **Distributed Caching & Primitives**: `IBufferDistributedCache`, `IHighwayDistributedLock`, `IHighwayRateLimiter`

## Usage

Reference this package in your domain contracts project:

```bash
dotnet add package Highway.Abstractions --prerelease
```

Define messages as plain C# POCOs:

```csharp
using Highway.Abstractions;

[Queue("orders.process")]
public sealed class ProcessOrder : ISend
{
    public string OrderId { get; set; } = "";
    public decimal Amount { get; set; }
}
```

## Documentation & Resources

- [Highway User Guide](https://github.com/System-D-AB/highway/blob/main/docs/product/UserGuide.md)
- [Highway Wire Protocol Specification](https://github.com/System-D-AB/highway/blob/main/docs/HIGHWAY-PROTOCOL.md)
- [Guarantees and Known Limits (constraints.md)](https://github.com/System-D-AB/highway/blob/main/docs/product/constraints.md)
- [GitHub Repository](https://github.com/System-D-AB/highway)
