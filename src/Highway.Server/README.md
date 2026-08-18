# Highway.LocalServer

An in-process [Highway](https://github.com/System-D-AB/highway) broker for **integration tests
and local development**.

Start a real broker inside your test host — real RESP, real durability, real `HW.*` commands —
with no Docker, no external infrastructure and nothing to install.

> [!IMPORTANT]
> **This package is not how you run Highway in production.**
> For a deployed broker, download the `highways` distribution from
> [GitHub Releases](https://github.com/System-D-AB/highway/releases): unpack it, run it, or
> install it as a Windows service or systemd daemon. Do not write a host application around
> this package — that is exactly the problem the distribution exists to remove.

## Installation

```bash
dotnet add package Highway.LocalServer --prerelease
```

## Integration tests

```csharp
using var server = new HighwayTestServer();

services.AddHighway(o =>
{
    o.NodeName = "test-node";
    o.Server   = server.ConnectionString;
});
```

The server is memory-only by default and disposed with the test. Every guarantee the deployed
broker makes — at-least-once delivery, dead-lettering, competing consumers, subscription
groups — behaves identically here, because it is the same broker.

## Local development

```csharp
var server = new HighwayServerBuilder()
    .WithPort(6500)
    .WithDataDir("./data")     // omit, or call .Ephemeral(), for memory-only
    .Build();

await server.RunAsync();
```

Useful when you want a broker alongside a debugging session. For anything longer-lived, the
distribution is less work and behaves the same.

## Documentation

- [User Guide](https://github.com/System-D-AB/highway/blob/main/docs/product/UserGuide.md) — the three verbs, jobs, cache, deployment
- [Wire protocol](https://github.com/System-D-AB/highway/blob/main/docs/HIGHWAY-PROTOCOL.md) — every command, reply and error code
- [Constraints](https://github.com/System-D-AB/highway/blob/main/docs/product/constraints.md) — every guarantee Highway makes, and whether the code keeps it

## Licence

MIT. Copyright (c) System D AB.
