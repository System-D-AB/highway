# Design: Runnable Samples

## Overview

Four projects under `samples/`: one contracts library and three console applications. Together they form a working distributed system a developer can start in three terminals — a broker, an order service, and a storefront — and watch exchange RPC calls and durable events over TCP.

The design goal is **minimum ceremony, maximum honesty**. Every app uses the public API a real user would use, with no test hooks, no internal access, and no sample-only shortcuts. If something is awkward to write here, it is awkward for users, and that is a finding worth having.

## Why Four Projects, Not Three

The request was for three console apps. The design adds a fourth project — a contracts class library — because without it the sample cannot demonstrate the thing that most needs demonstrating.

Two processes exchanging typed messages must agree on the types. If each app defines its own `CreateOrder`, the sample shows copy-paste duplication and quietly teaches the wrong pattern. `product.md` is explicit that a contracts assembly referencing only `Highway.Abstractions` is the intended shape, and that this is *why* the package split exists:

> A contracts assembly (e.g. `Orders.Contracts`) references only `Highway.Abstractions` — a tiny, stable package with no transitive dependencies. Callers and service hosts both reference it.

The contracts library is a handful of DTOs with no logic. It costs almost nothing and is the sample's clearest illustration of the architecture. The three console apps remain exactly as requested.

## Project Layout

```
samples/
├── README.md                              # Requirement 8 — the run instructions
├── Highway.Samples.Contracts/             # class library — Abstractions only
│   ├── Orders.cs                          # CreateOrder / OrderResult / GetOrder / ...
│   └── Events.cs                          # OrderPlaced, InventoryLow
├── Highway.Samples.Broker/                # console — hosts Highway.Server
│   └── Program.cs
├── Highway.Samples.OrderService/          # console — hosts services, publishes events
│   ├── Program.cs
│   ├── CreateOrderService.cs
│   ├── GetOrderService.cs                 # the error-path service
│   └── InventoryLowSubscriber.cs
└── Highway.Samples.Storefront/            # console — calls RPC, subscribes to events
    ├── Program.cs                         # interactive command loop
    └── OrderPlacedSubscriber.cs
```

`samples/` is a sibling of `src/` and `tests/`. It does not go inside `src/` because `src/` holds shipped packages and the samples ship to nobody; `IsPackable=false` makes that explicit.

## The Demonstrated System

```
        ┌──────────────────────────────┐
        │  Highway.Samples.Broker      │   terminal 1
        │  Highway.Server on :6500     │
        │  data dir ./data (durable)   │
        └──────────────────────────────┘
              ▲                    ▲
    RESP over TCP            RESP over TCP
              │                    │
┌─────────────┴──────────┐  ┌──────┴──────────────────┐
│  OrderService          │  │  Storefront             │  terminals 2, 3
│  hosts orders.create   │  │  calls orders.create    │
│  hosts orders.get      │  │  calls orders.get       │
│  publishes OrderPlaced │  │  subscribes OrderPlaced │
│  subscribes InventoryLow│ │  publishes InventoryLow │
└────────────────────────┘  └─────────────────────────┘
```

Both participants host *and* consume. That is deliberate: it demonstrates location transparency (Requirement 7 AC7) and stops the sample implying a rigid client/server split that Highway does not have.

## Contracts

```csharp
// Highway.Samples.Contracts — references ONLY Highway.Abstractions.
// This is the whole point of the three-package split: callers and hosts share
// these types without either taking a dependency on the other, and without
// pulling in the client engine or the broker. See docs/product/product.md
// § "Delivery (Package Architecture)".

[Service("orders.create")]
public sealed class CreateOrder : IReturn<OrderResult>
{
    public int CustomerId { get; set; }
    public string Item { get; set; } = "";
    public int Quantity { get; set; }
}

public sealed class OrderResult : Output          // Output carries StatusCode + Error
{
    public string? OrderId { get; set; }
    public decimal Total { get; set; }
}

[Service("orders.get")]                            // the error-path demonstration
public sealed class GetOrder : IReturn<OrderResult>
{
    public string OrderId { get; set; } = "";
}

[Service("orders.cancel")]                         // deliberately NEVER hosted — Req 7 AC3
public sealed class CancelOrder : IReturn<OrderResult>
{
    public string OrderId { get; set; } = "";
}

[Channel("orders.placed")]
public sealed class OrderPlaced : IPublish
{
    public string OrderId { get; set; } = "";
    public decimal Total { get; set; }
}

[Channel("inventory.low")]
public sealed class InventoryLow : IPublish
{
    public string Item { get; set; } = "";
}
```

`CancelOrder` exists as a contract with no implementation anywhere. Calling it exercises the local-catalog 404 path — the caller fails instantly without a network round trip, which is a different and better failure than a timeout.

## Application Designs

### Broker

```csharp
var port    = Config.Int("HIGHWAY_PORT", "--port", 6500);
var dataDir = Config.String("HIGHWAY_DATA_DIR", "--data-dir", "./data");
var bind    = Config.String("HIGHWAY_BIND", "--bind", "127.0.0.1");

using var loggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole().SetMinimumLevel(LogLevel.Information));

var server = new HighwayServerBuilder()
    .WithPort(port)
    .WithDataDir(dataDir)          // durability on by default — Req 3 AC5
    .WithBindAddress(bind)         // loopback default — Req 9 AC4
    .WithLoggerFactory(loggerFactory)
    .Build();

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };   // Req 3 AC3

Console.WriteLine($"Highway broker listening on {bind}:{port}  (data: {Path.GetFullPath(dataDir)})");
Console.WriteLine($"Connect participants with:  --server {bind}:{port}");
await server.RunAsync(cts.Token);
```

A data directory by default is a deliberate choice: it makes Requirement 7 AC5 (durable delivery across downtime) real rather than simulated, and it means restarting the broker does not silently discard everything — which is what a user would expect from a system that advertises durability.

### OrderService

Standard generic-host wiring — the same shape a production app would use:

```csharp
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHighway(o =>
{
    o.NodeName = Config.String("HIGHWAY_NODE", "--node", "order-service-1");
    o.Server   = Config.String("HIGHWAY_SERVER", "--server", "127.0.0.1:6500");
});
await builder.Build().RunAsync();
```

`AddHighway` scans, registers, and adds the hosted service; the engine starts and drains with the host. No manual `StartAsync`. Services are discovered — nothing is registered by hand, which is the claim under test.

```csharp
public sealed class CreateOrderService(ILogger<CreateOrderService> log, IHighwayClient client)
    : AsyncService<CreateOrder, OrderResult>
{
    public override async Task<OrderResult> ExecuteAsync(CreateOrder request, CancellationToken ct)
    {
        var orderId = $"ORD-{Random.Shared.Next(1000, 9999)}";
        var total   = request.Quantity * 9.99m;
        log.LogInformation("Created {OrderId} for customer {CustomerId}", orderId, request.CustomerId);

        // A node that serves is also a node that publishes — Req 4 AC4.
        await client.PublishAsync(new OrderPlaced { OrderId = orderId, Total = total }, ct);

        return new OrderResult { OrderId = orderId, Total = total, StatusCode = StatusCodes.Status200OK };
    }
}

public sealed class GetOrderService : AsyncService<GetOrder, OrderResult>
{
    public override Task<OrderResult> ExecuteAsync(GetOrder request, CancellationToken ct)
        // Errors are DATA. No exception is thrown and none reaches the caller — Req 4 AC3.
        => Task.FromResult(new OrderResult
        {
            StatusCode = StatusCodes.Status404NotFound,
            Error = new ErrorDetail { Code = "ORDER_NOT_FOUND", Message = $"No order '{request.OrderId}'." },
        });
}
```

Injecting `IHighwayClient` into a service to publish is the pattern most likely to feel awkward or to fail in practice (it requires the engine to be running while a request is being handled, which it is). If it does not work cleanly, that is a Requirement 10 finding.

### Storefront

An interactive command loop rather than a fixed script, so a person can drive the demo and interleave it with stopping and starting processes — which is what Requirement 7 AC5 and AC6 actually need:

```
order <qty>   place an order (RPC → prints typed response)
get <id>      fetch an order (RPC → prints 404 status + error detail, no exception)
cancel <id>   call an unhosted service (immediate local 404, no network)
low <item>    publish an InventoryLow event
help / quit
```

Received events print as they arrive, interleaved with the prompt, so fan-out and durable catch-up are directly visible.

## Configuration

One tiny helper shared by all three apps, resolving in order: command-line argument → environment variable → default.

| Setting | Argument | Environment | Default |
|---|---|---|---|
| Broker port | `--port` | `HIGHWAY_PORT` | `6500` |
| Broker data dir | `--data-dir` | `HIGHWAY_DATA_DIR` | `./data` |
| Broker bind address | `--bind` | `HIGHWAY_BIND` | `127.0.0.1` |
| Participant server | `--server` | `HIGHWAY_SERVER` | `127.0.0.1:6500` |
| Participant node name | `--node` | `HIGHWAY_NODE` | per-app default |

Node name is configurable because Requirements 4 AC8 and 5 AC7 both depend on running two instances of the same app side by side. It is also the subscriber-group identity, so two instances sharing a node name would share a group — the collision risk documented in 005's design. The README says so.

## The Assembly Scanning Risk

Requirement 6 exists because of a specific mechanism, and the design records it so whoever implements this knows what to look for.

`DefaultAssemblySource.GetAssemblies()` filters `AppDomain.CurrentDomain.GetAssemblies()` to those referencing `Highway.Abstractions`. The .NET runtime loads an assembly on first use, not at startup. The two apps therefore differ:

- **OrderService** defines `CreateOrderService : AsyncService<CreateOrder, OrderResult>` in its own assembly. Reflecting over its types forces the contracts assembly to load in order to resolve the base type. Scanning is likely to work by accident of ordering.
- **Storefront** defines a subscriber but *no services*. Its RPC contracts (`CreateOrder`, `GetOrder`) are referenced only from method bodies. If nothing has touched a contracts type before `AddHighway` runs, the contracts assembly may not be loaded, `GetAssemblies()` will not list it, and `GetServiceNameForRequestType` will return null — so `ExecuteAsync` returns `SERVICE_NOT_FOUND` for a service that is running perfectly well two terminals away.

The failure is order-dependent and therefore may or may not reproduce on the first run. **Task 6 tests for it explicitly rather than waiting to be surprised.**

If it reproduces, the fix belongs in `Highway.Client` (Requirement 6 AC2). The direction that preserves the product's promise is to seed discovery from the entry assembly's reference closure — walking `Assembly.GetEntryAssembly().GetReferencedAssemblies()` transitively and loading those that reference `Highway.Abstractions` — rather than depending on whatever the runtime happens to have loaded. `AdditionalAssemblies` stays for genuinely dynamic cases.

What the sample must **not** do is set `AdditionalAssemblies` to make the problem go away. That would ship a workaround for the exact ceremony goal G3 promises does not exist, and it would hide the defect from every future user.

## Sequence: The Durability Demonstration

The scenario worth the most, because it is the product claim hardest to believe:

```
Terminal 1 (broker)     Terminal 2 (OrderService)   Terminal 3 (Storefront "shop-1")
───────────────────     ─────────────────────────   ────────────────────────────────
running                 running                     > order 2
                        creates ORD-1234            ORD-1234  total 19.98
                        publishes OrderPlaced   ──▶ event: OrderPlaced ORD-1234

                                                    Ctrl+C          (shop-1 stops)
                        > (still running)
                        creates ORD-5678
                        publishes OrderPlaced       (nobody listening on shop-1's group)

                                                    dotnet run -- --node shop-1
                                                    event: OrderPlaced ORD-5678   ◀── delivered
```

The group persists server-side because the client never sends `HW.UNSUBSCRIBE` (005 Requirement 9 AC3), so messages queue for an absent node and drain on its return. The same node name is what makes it the same group — which is why node name is configurable and why the README calls this out.

This is product success criterion 2, demonstrated across three OS processes instead of inside one test host.

## What the Samples Validate That Tests Do Not

Recorded here because it is the justification for the feature:

| Untested until now | Exercised by |
|---|---|
| Standalone `Highway.Server` process | Broker app — `HighwayServerBuilder` → `RunAsync` |
| Real TCP between OS processes | All three apps |
| Real Ctrl+C / cancellation shutdown | All three apps |
| Generic-host wiring and hosted-service lifecycle | Both participants |
| Scanning across a project boundary | Requirement 6 |
| Broker-unavailable failure quality | Requirement 7 AC8 |
| Durability across process restarts on disk | Requirement 7 AC5 |
| Non-loopback bind end to end | Requirement 9 |

## Version Tracking and the Living Conformance Gate

### Why project references, never packages

The samples reference `Highway.Abstractions`, `Highway.Client` and `Highway.Server` by `ProjectReference`. This is not a convenience — it is the mechanism that makes the samples a test.

A `PackageReference` to a published version would pin the samples to a snapshot. They would keep building after a breaking change, keep demonstrating an API that no longer exists, and quietly stop testing anything. With project references, a change that breaks the public surface breaks the sample build **in the same commit that introduces it**, which is the earliest possible moment to find out.

This also makes the samples an unplanned consumer of the API — the only code in the repository that uses Highway the way a user would, without `InternalsVisibleTo`, test fixtures, or knowledge of internals. That perspective is worth as much as the runtime validation.

### Running the samples is a test

The suite has 348 tests and reaches none of the following: a standalone broker process, RESP over a real socket between OS processes, `Console.CancelKeyPress` shutdown, generic-host lifecycle, connection failure against a genuinely absent server, or assembly scanning across a project boundary. Starting the three apps exercises all of them at once.

So a sample that fails to start is a test failure. It is triaged like one: symptom, root cause, fix in the library, regression test where the behavior can be pinned in the suite. Requirement 11 AC7 forbids the tempting alternative — quietly editing the sample to avoid the broken path — because that converts a product defect into a documentation defect and loses it.

### The obligation on future features

`Highway.Server` and `Highway.Client` are not finished. Feature 006 adds three protocol commands and new options on both sides; feature 002 adds more. A sample frozen at today's API would stop being representative within one feature.

Requirement 11 therefore binds every future feature that touches the protocol or public API:

| Trigger | Obligation |
|---|---|
| New or changed `HW.*` command | Update samples if user-visible; re-run; record outcome |
| Envelope or wire format change | Re-run — this is the highest-risk change class for cross-process behavior |
| New `HighwayOptions` / `HighwayServerOptions` | Demonstrate it in a sample when it is something a user would set |
| Any public API change | Samples must compile and run; a breaking change surfaces here first |
| New user-visible capability | Samples demonstrate it, so they keep pace with the product's claims |

Concretely for 006: the broker app gains nothing (heartbeat is client-driven), but the participants become discoverable, and the Storefront gains a `discover` or `stats` command so an operator can see topology from the sample. That is a small addition made *inside* feature 006, not deferred.

Because this obligation must outlive any single feature's memory, it is written into `.kiro/steering/spec-workflow.md` (Requirement 11 AC8), which `CLAUDE.md` establishes as the source of truth for project conventions.

### Where findings accumulate

`samples/RUNLOG.md` records every sample run: date, what was run, what was found, and what was done about it. One file, append-only, newest first.

A per-feature note would scatter the history across a dozen specs and make a recurring problem invisible — the same failure appearing in three consecutive features reads as three isolated incidents rather than one unresolved defect. A single log makes patterns visible and gives the next person a place to look before debugging from scratch.

## Risks

| Risk | Mitigation |
|---|---|
| Cross-assembly scanning fails (the Requirement 6 risk) | Tested deliberately in Task 6; fixed in the library, never worked around in the sample |
| Injecting `IHighwayClient` into a service to publish deadlocks or fails | Isolated in `CreateOrderService`; if it fails it is a Requirement 10 finding with a library fix, not a sample redesign |
| Samples rot as libraries change | In `Highway.slnx` with project references, so a breaking change fails the build |
| Data directory left dirty between runs confuses the demo | README troubleshooting covers it; the broker logs the resolved path at startup |
| Two instances started with the same node name silently share a subscriber group | Node name is explicit configuration; README states the uniqueness requirement |
| Samples become a maintenance burden | Deliberately minimal: no persistence, no domain logic, no frameworks beyond the generic host |
| Samples drift behind the libraries and stop testing anything | Project references break the build on API changes; Requirement 11 binds future features via steering; `RUNLOG.md` makes staleness visible |
| The re-run obligation is quietly skipped under delivery pressure | It lives in `spec-workflow.md`, which every agent reads before starting work, rather than only in this feature's spec |

## Dependencies & Constraints

- Depends on features 004, 004.1, and 005 (all merged). Does not depend on 006 — no heartbeat, discovery, or stats usage, so the samples are unaffected by whether 006 lands first.
- `.NET 10 SDK` is the only prerequisite. No Docker, no external broker, no cloud account — Requirement 8 AC3 and product goal G1.
- New package references (`Microsoft.Extensions.Hosting`, console logging) go in `Directory.Packages.props` per repository convention.
- Coding standards apply unchanged: file-scoped namespaces, nullable enabled, `CancellationToken` on async APIs, zero build warnings.
- Samples are excluded from packing and add no runtime to `dotnet test`.

## Cross-References

- Requirements: `docs/features/010-create-samples/requirements.md`
- Product claims demonstrated: `docs/product/product.md` § "Vision", G1–G4, "Highway.Server — Hosting & Control Panel"
- Server hosting API: `docs/features/004-server-hw-commands/design.md`, `docs/features/004.1-server-remediation/design.md`
- Client API and engine lifecycle: `docs/features/005-client-server-communication/design.md`
- Scanning mechanism under test: `docs/features/003-assembly-scanning/design.md`
