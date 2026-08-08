# Highway

**A .NET library that unifies RPC and Pub/Sub behind one small, attribute-driven API.**

*Status: v0.8.0, written ~2018–2019, targeting `netstandard2.0`. This document describes what Highway is and how it works today, and then sets out the goal for the next version: keeping the same programming model while replacing the hand-rolled transport and broker with [Microsoft Garnet](https://github.com/microsoft/garnet).*

---

> ## ⚠️ Read this before Parts 2 and 3
>
> **Parts 1–3 are a pre-implementation record — what was believed before any code was written.** They are kept deliberately unedited, because their value is explaining *why* the project went the way it did. Several of their technical conclusions were overtaken by what was actually built.
>
> **Two sections will actively mislead you:**
>
> - **§2.3 "What Garnet gives us"** lists `BLPOP`/`BLMOVE`, sorted sets, `HEXPIRE`, `MULTI`/`EXEC`/`WATCH` and `EVAL` as Highway's substrate. Highway uses **essentially none of them**. Queues are Garnet lists driven by custom transaction procedures; atomicity comes from procedure-level key locking, not `WATCH`; there is no Lua anywhere.
> - **§2.4 "The decision that falls out of this"** recommends *"Build Highway entirely out of standard RESP commands. Write no Garnet extensions in v1."* **The opposite shipped.** Highway.Server *is* a Garnet extension with twelve custom `HW.*` commands, and is the only supported broker. The publish limitation that drove §2.4 was sidestepped by hosting rather than avoided — see Part 4.
>
> The API names in Part 2 (`RegisterCmd`, `CustomCommandRegistry`, `ArgSlice`) no longer exist in Garnet.
>
> **[Part 4](#part-4--addendum-verified-garnet-v212-findings-features-004--0041)** records what was verified against the pinned submodule during implementation. Read it before acting on anything in Part 2.
>
> **What has held up completely:** Part 1's description of the v0.8 system — now the only surviving record of it, since the old source dump has been removed — and **Part 3's evaluation of the alternatives** (Hangfire, RabbitMQ, ZeroMQ, NServiceBus, MassTransit, Wolverine, Rebus). Part 3 is a market and architecture analysis, untouched by anything that happened in implementation, and remains the answer to "why not just use X?".
>
> For what the protocol actually is, see **[`docs/HIGHWAY-PROTOCOL.md`](../HIGHWAY-PROTOCOL.md)**.
>
> *— Added 2026-08-07, after features 004–007.*

---

## Part 1 — The current library

### 1.1 The idea

Most distributed .NET applications end up bolting together two unrelated stacks: something for request/response (gRPC, WCF, HTTP + a client library) and something for events (a message bus, a broker, a hub). Each has its own configuration, its own contracts, its own failure modes, and its own mental model.

Highway's premise is that an application developer should not have to care. You write a class. You mark it. Other applications can call it or listen to it. The library figures out whether the target is in your own process or across a machine boundary, and the code you write is identical either way.

Concretely, Highway gives you two verbs and nothing else:

```csharp
public interface IHighwayClient
{
    Task PublishAsync(IPublish publish);
    Task<TRes> ExecuteAsync<TRes>(IReturn<TRes> input) where TRes : Output;
}
```

`ExecuteAsync` is RPC. `PublishAsync` is Pub/Sub. That is the entire public surface a consuming application touches.

### 1.2 The four concepts

Highway has exactly four things you define, and all of them are plain classes.

**1. A request** — a POCO carrying a `[Service("name")]` attribute that declares, via `IReturn<TResponse>`, what it returns:

```csharp
[Service("clientserver.helloworld")]
public class HelloWorldRequest : IReturn<HelloWorldResponse>
{
    public string Input { get; set; }
}
```

**2. A response** — a POCO deriving from the abstract `Output` base class:

```csharp
public class HelloWorldResponse : Output
{
    public string CompleteValue { get; set; }
}
```

`Output` contributes two members that make error handling uniform across the whole system:

```csharp
public abstract class Output
{
    public virtual int? StatusCode { get; set; } = null;
    public virtual Exception ErrorDetail { get; set; } = null;
}
```

The status codes are deliberately borrowed from HTTP (`StatusCodes.Status200OK`, `Status404NotFound`, `Status500InternalServerError`, …), because everyone already knows what they mean. A service that returns normally and leaves `StatusCode` null is stamped `200` by the engine. A service that throws is caught, and the caller receives a default-constructed output with `StatusCode = 500` and the exception in `ErrorDetail`. **`ExecuteAsync` does not rethrow** — failures are data, not control flow.

**3. A service** — the implementation, deriving from `AsyncService<TRequest, TResponse>`:

```csharp
public class HelloWorldService : AsyncService<HelloWorldRequest, HelloWorldResponse>
{
    public override Task<HelloWorldResponse> ExecuteAsync(HelloWorldRequest request)
        => Task.FromResult(new HelloWorldResponse { CompleteValue = request.Input + " World" });
}
```

Note where the `[Service]` attribute lives: on the **request type**, not on the implementation. The request is the contract; the implementation is an interchangeable detail. This is why a request type can live in a shared assembly that callers reference without ever seeing the code that answers it.

Services take constructor dependencies from the host's `IServiceCollection` like any other DI-managed class:

```csharp
public class AddCustomer : AsyncService<Customer, AddCustomerResult>
{
    private ICustomerStore _store;
    public AddCustomer(ICustomerStore store) => _store = store;

    public override async Task<AddCustomerResult> ExecuteAsync(Customer request) { /* … */ }
}
```

**4. A channel** — the Pub/Sub half. A message carries `[Channel("name")]` and implements the `IPublish` marker; subscribers implement `ISubscribe<T>`:

```csharp
[Channel("clientserver.channel.helloworld")]
public class PublishHelloWorld : IPublish
{
    public string Input { get; set; }
}

internal class HelloWorldSubscriber : ISubscribe<PublishHelloWorld>
{
    private Pipe _pipe;
    public HelloWorldSubscriber(Pipe pipe) => _pipe = pipe;   // DI works here too

    public Task Subscribe(PublishHelloWorld request)
        => Task.Run(() => _pipe.OnPublishDone?.Invoke("Publish Done with data: " + request.Input));
}
```

Subscribers may be `internal`. Multiple subscribers may bind to the same channel — that case is explicitly supported and tested. `PublishAsync` returns after the fan-out and the engine tracks the result as a `ChannelResponse { TotalSubscribers, SuccessCalls }`; subscribers are invoked **sequentially**, and a subscriber that throws is swallowed so it cannot abort its siblings. The only evidence of failure is `SuccessCalls < TotalSubscribers`.

### 1.3 Nothing is registered by hand

There is no `AddService<T>()`, no `MapChannel(...)`, no routing table. At startup Highway walks `AppDomain.CurrentDomain.GetAssemblies()`, finds every `AsyncService<,>` subclass and every `ISubscribe<>` implementation, reads their attributes, and builds a catalog. Wiring an entire application is one line:

```csharp
services.AddHighway();
```

Discovery enforces its rules loudly, with a dedicated exception per rule: the input must implement `IReturn<>` (`ServiceInputTypeShouldImplementIReturn`), the output must derive from `Output` (`ServiceOutputTypeShouldImplementOutput`), the input must carry `[Service]` (`ServiceAttributeNotFoundException`), service names must be unique within a node (`ServiceWithSameNameAlreadyExists`), publish types must carry `[Channel]`, and channel names must be unique (`ChannelAlreadyAddedException`).

Discovered types default to `ServiceLifetime.Scoped` and can be overridden per class with `[ServiceLiftTime(ServiceLifetime.Singleton)]`.

*Known limitation:* because scanning reads assemblies **already loaded** by the CLR, a service in an assembly the runtime has not touched yet is invisible. In practice this means the assembly containing your services must be referenced in a way that forces a load before `AddHighway()` runs.

### 1.4 The three ways to run

The same code runs in three topologies, and the difference is entirely in host configuration.

**Local only** — no transport at all. `ExecuteAsync` dispatches in-process; this is the `ConsoleApp` sample:

```csharp
builder.ConfigureServices((hosting, services) =>
{
    services.AddHighway();
    services.AddSingleton<ICustomerStore>(new CustomerStore());
});
```

**As a gateway** — the hub. It brokers for everyone else and can host its own services at the same time:

```csharp
services.AddHighway();
services.StartAsHighwayGateway(Extentions.CreateNamedPipeGateway("test_gateway"));
```

**As a node** — a spoke that dials a named gateway:

```csharp
services.AddHighway();
services.StartAsHighwayNode(Extentions.CreateNamedPipeNode("test_node", "test_gateway"));
```

Gateway and node are *roles*, not different code shapes. The gateway sample defines and hosts an ordinary `AsyncService<,>` exactly the way the node sample does.

Once the host is built, every process — gateway or node — obtains the same client:

```csharp
IHighwayClient client = host.Services.GetService<IHighwayClient>();

var result = await client.ExecuteAsync(new HelloWorldRequest { Input = "A big hello " });
Console.WriteLine(result.CompleteValue);

await client.PublishAsync(new PublishHelloWorld { Input = "Hi There i'm coming from Node1" });
```

`ExecuteAsync` infers `TRes` from `IReturn<TRes>`, so calls are strongly typed with no generic argument at the call site and no casting of the result.

### 1.5 How a call actually travels

**Local first.** `HighwayClient.ExecuteAsync` looks the service name up in the local catalog. If it is there, the call runs in-process and never touches the network. Only on a miss does it go remote — gateway role sends via an internal `StartRpc` service, node role via `StartRpcNodeToGateway`.

**Publish is local *and* remote.** `PublishAsync` always runs local subscribers first, then fans out: a gateway pushes to every node subscribing to that channel (excluding the originator), a node pushes to its gateway, which re-fans-out.

**The distributed machinery is itself built out of Highway services.** The internal plumbing — RPC forwarding, publish fan-out, catalog exchange — is implemented as ordinary services and channels named `highway.private.*` and `highway.internal.*`. The catalog filters those names out before advertising itself, so they are invisible to applications. It is a genuinely elegant piece of self-hosting: Highway routes Highway's own control plane through Highway.

**Discovery is push-based on connect.** When a node connects, the gateway immediately sends it the internal `highway.internal.getcatalog` request. The node replies with a `CatalogInfo` listing its services and channels, and the gateway records service-name → node-connection in a `RemoteServicesCatalog`. On disconnect those entries are removed. This is what lets any node call any service by name without knowing where it lives.

The catalog is announced exactly once, at connect time, from a snapshot computed during `AddHighway()`. There is no re-announcement, so a node cannot add or remove services at runtime.

**Correlation is by GUID.** `RpcRequest.Id` pairs with `RpcResponse.RequestId`, and an `IsCallerLocal` flag tells the correlator whether it is relaying on someone else's behalf or waiting on the response itself.

### 1.6 The wire protocol

Everything on the wire is a `HighwayMessage`:

```csharp
public abstract class HighwayMessage
{
    public Guid Id { get; set; }
    public MessageType MessageType { get; set; }
    public dynamic AdditionalData { get; set; }
}

public enum MessageType
{
    Ping, Pong, Challenge, Hello, Welcome, GoodBye,
    RpcRequest, RpcResponse, PublishRequest
}
```

The connection handshake is `Challenge → Hello → Welcome`: the gateway challenges a new connection, the node answers `Hello` carrying its name in `AdditionalData`, the gateway promotes the connection to a named node and replies `Welcome`.

`RpcRequest` carries the payload as `object InstanceData` plus a `FullTypeName` string; `RpcResponse` correlates by `RequestId` and carries `OutputData`, `StatusCode`, and `ErrorDetail`. `PublishRequest` carries `ChannelData`.

### 1.7 The transport layer

Transport is pluggable through three interfaces in `Highway.Abstractions.Transport`:

- **`IGateway`** — the server side: `StartAsync`/`StopAsync`, `PushMessage(HighwayMessage, Guid nodeConnectionId)`, events `NodeConnected` / `NodeDisconnected` / `NodeMessage` / `Error`, and `IList<(Guid, IHighwayNodeConnection)> AllConnectedNodes`.
- **`IHighwayNode`** — the client side: `Name`, `IsConnected`, `StartAsync`/`Stop`, `PushMessage(HighwayMessage)`, events `GatewayMessage` / `Disconnected`.
- **`IHighwayNodeConnection`** — the gateway's handle to one connected node: `Id`, `Name`, `IsConnected`, `PushMessage`.

That is a small and well-chosen seam: it is message-in / message-out with connection lifecycle events, and it says nothing about *how* bytes move. **This is the interface that survives the rewrite.**

The one shipped implementation, `Highway.Transport.NamedPipes`, is Windows named pipes with a vendored copy of NamedPipeWrapper. Frames are a 4-byte network-order length prefix followed by UTF-8 JSON produced by Newtonsoft with `TypeNameHandling.All`, which is how a payload declared as `object` rematerializes into the caller's concrete type on the far side. Each connection is a two-pipe handshake — a well-known pipe hands back a per-connection data-pipe name — with a dedicated read task and a write task draining a blocking queue.

### 1.8 An honest assessment

The design is the good part. The concept model — four class shapes, two verbs, one attribute each, location transparency, HTTP-style status codes, DI throughout, and a control plane written in terms of the library's own primitives — has aged remarkably well. It is still, in 2026, a nicer developer experience than assembling gRPC plus a broker by hand.

The implementation is where a decade shows, and the gaps are almost entirely in the parts that a rewrite would delete anyway:

- **No RPC timeout.** Both remote-call paths spin `while (true) { …; await Task.Delay(25); }` waiting for a correlated response, each carrying the identical comment `//TODO: request may never be completed so implement a timeout here`. A call to a node that never answers hangs forever.
- **Polling instead of signalling.** Response correlation is a 25 ms poll loop rather than a `TaskCompletionSource`.
- **No cancellation.** `CancellationToken` appears nowhere in the API.
- **A publish echo loop.** When a subscribing node receives a `PublishRequest` off the wire, it hands the payload to the *full* `HighwayClient.PublishAsync`. Because a user message is not marked `IInternalMessage`, that call re-forwards the same message back to the gateway, which fans it out again to everyone except the sender. With two or more subscribing nodes this does not terminate, and there is no message-id de-duplication anywhere in the publish path. The dead `NodeHandler` calls the engine directly instead and would not loop — evidence this was introduced by a refactor.
- **No load balancing.** When several nodes host the same service, target selection is `FirstOrDefault` — first registered node wins, always. The code says so: `//TODO: implementing routing here in case same service is deployed on multiple nodes`.
- **Catalog replies are identified by payload type.** The gateway distinguishes a catalog announcement from an ordinary RPC reply solely by testing `OutputData.GetType() == typeof(CatalogInfo)`. A user service that legitimately returns `CatalogInfo` would be silently swallowed by the discovery path.
- **`Scoped` is a fiction.** The default lifetime is `ServiceLifetime.Scoped`, but nothing ever creates an `IServiceScope` — everything resolves off the root provider, so scoped behaves as singleton.
- **Two service providers.** The catalog builds and caches its *own* `IServiceProvider` from the same `IServiceCollection` as the host. Singletons therefore exist twice, once per provider, which is a genuinely surprising trap.
- **A reflection executor is rebuilt on every call.** `ObjectMethodExecutor.Create` — which compiles expression trees — runs afresh for every single service invocation and every subscriber notification, with no caching. This is the largest performance defect in the codebase.
- **Local-only mode throws on an unknown service.** With `AddHighway()` alone, calling a service that is not registered dereferences a null `bool?` and throws `InvalidOperationException: Nullable object must have a value` instead of returning a clean 404.
- **`async void` on the ingress path.** The gateway's `OnNodeMessage` is `async void`, so an unhandled exception there takes down the process rather than surfacing to a caller.
- **Single point of failure.** One gateway process brokers everything, with no replication, no failover, and no clustering.
- **Nothing is durable.** A publish to a node that is momentarily disconnected is simply lost; there is no persistence, no acknowledgement, and no redelivery.
- **Weak liveness.** `Ping`/`Pong` are defined and handled but never actually sent, so there is no keepalive. `GoodBye` is sent but no one handles it.
- **Reconnect is a hot loop.** On disconnect the node calls `StartAsync().Wait()` immediately, with no backoff.
- **`TypeNameHandling.All` with no `SerializationBinder`** is the classic unrestricted-polymorphic-deserialization pattern, and it is a real security concern for any deployment where the transport is not fully trusted.
- **Status codes are not propagated on the happy path.** `RpcResponse.CreateRpcResponse` accepts a `statusCode` parameter and never assigns it, so successful remote responses arrive with `StatusCode = 0`.
- **Framing is fragile.** The reader issues a single `Stream.Read` for a whole frame with no loop and no check of the return count, so a partial read silently yields truncated JSON. The serializer swallows all exceptions and returns null, which the writer then dereferences.
- **Exceptions are swallowed by policy.** The vendored pipe wrapper catches everything with the comment *"we must igonre exception, otherwise, the namepipe wrapper will stop work."*
- **Dead code.** `Highway.Gateway` does not compile (it imports a namespace that does not exist) and is superseded by `Highway/InternalServices/Gateway/*`. `Highway.Node` is an empty class. `Highway.UnitTests` has no `.csproj`, is not in the solution, and targets a pre-`AsyncService<,>` API. `HighwayRemoteClient`, `GatewayRouter`, `NodeHandler`, and `ProcessRequest` are unreferenced or throw `NotImplementedException`.
- **Framework sprawl.** Libraries on `netstandard2.0`, tests on `netcoreapp2.0`/`2.1`/`2.2`, samples on `net5.0` — while still pinning `Microsoft.Extensions.Hosting 2.2.0`.
- **Logging is inert.** Serilog is referenced and `Log.ForContext<T>()` is used throughout, but the logger configuration block is commented out, so logging silently no-ops unless the host configures `Log.Logger` itself.

The through-line: **every one of these defects lives in the transport and the broker.** None of them are in the programming model. That is precisely what makes the next step attractive.

---

## Part 2 — The rewrite: Highway on Garnet

### 2.1 The goal, restated

**Keep the programming model exactly as it is. Delete the transport and the broker, and let [Microsoft Garnet](https://github.com/microsoft/garnet) be the central server.**

Concretely:

- `IHighwayClient`, `AsyncService<T,TRes>`, `Output`, `IReturn<T>`, `IPublish`, `ISubscribe<T>`, `[Service]`, `[Channel]`, `StatusCodes`, and `services.AddHighway()` all survive, as close to source-compatible as we can make them.
- `IGateway`, `IHighwayNode`, `IHighwayNodeConnection`, the `HighwayMessage`/`MessageType` envelope, the named-pipe transport, the `Challenge`/`Hello`/`Welcome` handshake, `RemoteServicesCatalog`, and the entire `Highway.Gateway` project all go away.
- The "gateway" stops being a process we write. It becomes a Garnet server.

The bet is that the decade-old design intuition was right and the decade-old plumbing was the liability. Part 1's defect list is almost entirely plumbing.

### 2.2 Why Garnet is the right substrate

Garnet is a Microsoft Research cache-store, MIT-licensed, written in C#, speaking the Redis RESP wire protocol. Version 2.1.2 shipped 2026-08-05; the repo is very active and Microsoft runs it internally behind Windows & Web Experiences Platform, Azure Resource Manager, and Azure Resource Graph, with an Azure Cosmos DB Garnet Cache offering in expanded private preview.

What makes it a good fit specifically for Highway:

- **It is C# all the way down.** Same language, same debugger, same ecosystem. `GarnetServer` is a public class we can host in-process for tests and single-box deployments.
- **It solves precisely the problems Part 1 listed.** Persistence, clustering, replication, connection management, backpressure, keepalive, and framing are all somebody else's well-tested problem now.
- **The performance ceiling is enormous.** The [PVLDB 2026 paper](https://badrish.net/papers/garnet-vldb2026.pdf) measures roughly 20× the throughput and a third of the p99 latency of standalone Valkey under a realistic client benchmark, with sub-300 µs p99.9 quoted on Azure. Highway will never be the bottleneck.
- **RESP is a stable, boring, universal protocol.** We add nothing to it — which, as the next section explains, turns out to matter more than expected.

### 2.3 What Garnet gives us — and what it does not

> **⚠️ Superseded in part.** The "Missing, and it matters" list below held up — Part 4 re-verified it against the pinned release. The "Available and directly useful" table did **not**: Highway uses lists and custom transaction procedures, not `BLPOP`, sorted sets, `HEXPIRE`, `WATCH` or Lua. See [Part 4](#part-4--addendum-verified-garnet-v212-findings-features-004--0041).

This is where the research changed the design, so it is worth being precise.

**Available and directly useful:**

| Primitive | Highway use |
|---|---|
| `PUBLISH` / `SUBSCRIBE` / `PSUBSCRIBE` | Channels (Pub/Sub), and RPC reply delivery |
| Lists incl. `LPUSH`, `RPOP`, `LMOVE`, and blocking `BLPOP`/`BLMOVE`/`BRPOPLPUSH` | RPC request queues, durable channel delivery |
| Sorted sets | Service registry with heartbeat expiry; retry/delay scheduling |
| Hashes, incl. **per-field TTL** (`HEXPIRE`, `HTTL`) | Node catalogs that expire themselves |
| `MULTI`/`EXEC`/`WATCH`, and `EVAL`/`EVALSHA` (Lua) | Atomic enqueue-and-notify |
| Key expiry, background reaper | Reply-slot and registry cleanup |
| AOF + checkpointing | Durability for anything we put in the store |
| Cluster mode, 16384 hash slots, replicas | Horizontal scale |

**Missing, and it matters:**

- **No Streams. At all.** All 23 `X*` commands are unsupported. [Issue #1379](https://github.com/microsoft/garnet/issues/1379) is open with no committed date — the maintainer said in September 2025 that it is *"of lower priority due to more pressing work items,"* and the [stabilization PR #1461](https://github.com/microsoft/garnet/pull/1461) has been a draft since December 2025 with acknowledged gaps. **Do not plan against it.** This removes the obvious substrate for consumer groups and replay.
- **Pub/Sub is strictly at-most-once and never persisted.** Delivery is a best-effort direct write into each subscriber's socket buffer, and `RespServerSession.Publish` catches all exceptions with the comment `// Ignore exceptions`. A subscriber that is disconnected at publish time never sees the message, and nothing anywhere reports that. There is a `TsavoriteLog` in the broker source, but `PUBLISH` does not use it.
- **Custom C# procedures cannot `PUBLISH`.** `IGarnetApi` has no publish surface, and the field that would reach the broker is `internal` — so an extension assembly loaded via `REGISTERCS` or `MODULE LOADCS` cannot touch it. **This kills the natural "atomic server-side operation that also notifies subscribers" design.** Lua appears to be able to publish (`redis.call` routes through normal command dispatch), but that path is undocumented and unverified.
- **No `WAIT`/`WAITAOF`**, so replication acknowledgement is not observable. **No keyspace notifications. No `CLIENT TRACKING`**, so no server-driven cache invalidation.
- **Cluster failover is passive.** Garnet ships the primitives (`CLUSTER MEET`, `FAILOVER`, gossip) but *"does not implement leader election"* and expects an external control plane, such as a Kubernetes operator, to detect failure and request failover.
- **AOF does not wait for commit by default**, so writes are acknowledged before they are durable unless you set `--aof-commit-wait` (which the docs warn *"will greatly increase operation latency"*).

### 2.4 The decision that falls out of this

> **⚠️ Not what shipped.** This section's recommendation was reversed. Highway.Server is a Garnet extension registering twelve custom `HW.*` commands, and is the only supported broker — see `product.md` §G6.
>
> The reasoning below is sound but its premise was incomplete. Custom procedures indeed cannot publish; what this analysis missed is that the *host* can. Highway subclasses `GarnetServer`, reaches the subscribe broker through `protected storeWrapper`, and rings doorbells from there. That single opening made server-side extensions viable, and with them the atomic multi-key operations that stock RESP could not express — atomic publish-to-all-groups, single-command enqueue-and-notify, lease-based redelivery.
>
> The cost is the one this section correctly identified: Highway does **not** run against Redis or Valkey. That was accepted deliberately, in exchange for correctness guarantees stock commands cannot provide. See [Part 4](#part-4--addendum-verified-garnet-v212-findings-features-004--0041).

Because custom C# procedures cannot publish, and because Streams do not exist, **the server-side-extension route buys us much less than it first appears.** It would also cost real money: extension assemblies must be signed, must be deployed to every node individually, and have no cluster-wide propagation.

So the recommendation is the opposite of the obvious one:

> **Build Highway entirely out of standard RESP commands. Write no Garnet extensions in v1.**

The payoff is larger than just avoiding work. If Highway only speaks stock RESP, then **Highway runs unmodified against Garnet, Redis, or Valkey.** Garnet becomes the recommended default rather than a hard dependency, the missing-Streams gap becomes a capability flag instead of a redesign, and anyone who already operates Redis can adopt Highway without adopting a new server. That is a strictly better position than coupling to Garnet-specific extensibility.

We revisit server-side C# only if profiling demands it, and even then most likely as a single Lua script rather than a registered assembly.

### 2.5 Mapping Highway onto Garnet

#### Services and RPC

The single most important consequence of moving to Garnet is that **the routing table disappears**. Today, a node announces its catalog, the gateway builds `RemoteServicesCatalog`, and every call does a name → node lookup before forwarding. On Garnet, the service name *is* the queue key. Whoever is listening gets the work.

```
Caller                          Garnet                        Service host
------                          ------                        ------------
LPUSH hw:req:{service} env  ──▶  list grows
PUBLISH hw:door:{service}   ──▶  doorbell            ──────▶  SUBSCRIBE wakes worker
                                                              RPOP hw:req:{service}
                                                              (drain until empty)
                                                              execute AsyncService<T,TRes>
                                 reply list  ◀──────────────  LPUSH hw:rep:{nodeId} result
                                                              PUBLISH hw:repdoor:{nodeId}
SUBSCRIBE hw:repdoor:{nodeId} ◀─ doorbell
RPOP hw:rep:{nodeId}         ──▶ correlate by requestId
                                 → complete TaskCompletionSource
```

Four things this buys us immediately, each of which is a named defect from Part 1:

- **Real timeouts.** The caller arms a `CancellationTokenSource`; on expiry it completes the pending call with `StatusCode = 504`. No more `while(true)` polling forever.
- **Real load balancing.** Run the same service on five nodes and they all `RPOP` the same list. Whoever is free takes the next item. No more `FirstOrDefault`.
- **Backpressure and observability for free.** `LLEN hw:req:{service}` is queue depth. That is a metric, an alert, and an autoscaling signal.
- **No echo loop.** The pathological publish recursion in Part 1 cannot occur, because delivery is a pop from a queue rather than a re-broadcast.

**On the doorbell pattern.** Correctness lives in the list; the `PUBLISH` is only a latency optimization. Workers also poll on a slow timer (say 250 ms) as a backstop, so a dropped doorbell costs latency and never a lost message. This is what lets us tolerate Garnet's at-most-once pub/sub without building on top of it.

**Why not just `BLPOP`?** Because StackExchange.Redis multiplexes commands over a shared connection and therefore does not support blocking commands — a `BLPOP` would stall every other operation sharing that connection. The doorbell-plus-poll pattern gets us the same latency using only non-blocking commands over the multiplexer, and keeps SE.Redis as the client. A dedicated non-multiplexed connection per worker using `BLMOVE` is a viable alternative for very high-throughput services, and is worth benchmarking, but should not be the default.

**Reliable delivery.** Using `LMOVE` from the request list into a per-worker processing list gives at-least-once with crash recovery: a worker that dies leaves its in-flight item visible, and a janitor can return it after a lease expires. This should be opt-in per service, since at-least-once requires idempotent handlers, and Highway today promises no such thing.

#### Channels and Pub/Sub

Here the mapping is close to exact, and that is worth stating plainly: **Highway's channels are already fire-and-forget, at-most-once, and non-durable.** Local subscribers are invoked sequentially, failures are swallowed, and a disconnected node simply misses messages. Garnet's `PUBLISH` has *the same semantics*. Moving to Garnet is not a regression here — it is a like-for-like swap that also happens to fix the echo loop and the fan-out targeting.

```csharp
// Publish: one command.
await db.PublishAsync($"hw:ch:{channelName}", envelope);

// Subscribe: one subscription per channel the node has subscribers for.
await sub.SubscribeAsync($"hw:ch:{channelName}", (ch, msg) => Dispatch(msg));
```

For users who want more, we offer an opt-in **durable channel** mode: a per-subscriber-group list plus the same doorbell, giving replay and at-least-once at the cost of requiring consumers to be declared in advance. This is the honest replacement for the consumer groups that Streams would have provided, and it is where the missing-Streams gap actually bites.

One thing to avoid: heavy `PSUBSCRIBE` use. Garnet evaluates every registered pattern against every published channel, so pattern-subscription cost is linear in the number of distinct patterns per publish. Highway should use exact channel names.

#### Discovery

Discovery stops being a protocol and becomes a heartbeat. Each node writes its catalog and refreshes a TTL:

```
HSET  hw:node:{nodeId} catalog {json}      # what this node hosts
ZADD  hw:svc:{serviceName} {now} {nodeId}  # score = last heartbeat
```

Readers prune by score. Because routing no longer depends on this, the registry is purely informational — used for tooling, diagnostics, and fast-failing a call to a service nobody hosts (returning a clean `404` instead of waiting out the timeout). And unlike today, a node can register a service at runtime, because there is no one-shot catalog announcement to miss.

### 2.6 What changes in the API

**Unchanged** — existing service and message declarations should compile as-is:

```csharp
[Service("clientserver.helloworld")]
public class HelloWorldRequest : IReturn<HelloWorldResponse> { public string Input { get; set; } }

public class HelloWorldResponse : Output { public string CompleteValue { get; set; } }

public class HelloWorldService : AsyncService<HelloWorldRequest, HelloWorldResponse>
{
    public override Task<HelloWorldResponse> ExecuteAsync(HelloWorldRequest request) => /* … */;
}
```

**Changed — startup.** `StartAsHighwayNode`/`StartAsHighwayGateway` collapse into one call, because there is no longer a gateway role:

```csharp
services.AddHighway(o =>
{
    o.NodeName    = "orders-1";
    o.Garnet      = "localhost:6379";
    o.CallTimeout = TimeSpan.FromSeconds(30);
});
```

For single-process development and integration tests, host Garnet in-process — `GarnetServer` is a public class in the `Garnet` namespace, so the "start a gateway" step of the old samples becomes an embedded server that needs no separate executable.

**Changed — the client gains what it was missing:**

```csharp
public interface IHighwayClient
{
    Task PublishAsync(IPublish publish, CancellationToken ct = default);
    Task<TRes> ExecuteAsync<TRes>(IReturn<TRes> input, CancellationToken ct = default) where TRes : Output;
}
```

**Changed — serialization.** Newtonsoft with `TypeNameHandling.All` is replaced by `System.Text.Json` plus an explicit type registry built from the `[Service]` and `[Channel]` attributes. This is not merely a security fix, though it does close the unrestricted-polymorphic-deserialization hole. It changes what the contract *is*: the wire carries a **service name and a JSON shape**, not an assembly-qualified CLR type name. Both ends no longer need the identical assembly, versioning becomes tractable, and a non-.NET client becomes possible later.

**Changed — errors cross the wire intact.** `Output.ErrorDetail` currently degrades to a string and is rehydrated as a bare `new Exception(text)`. It should become a structured `ErrorDetail { Code, Message, TypeName, Details }`, and the happy-path status code bug (successful remote responses arriving as `StatusCode = 0`) simply disappears with the old envelope.

### 2.7 Risks and open questions

Honest accounting of what could go wrong:

1. **Streams may never arrive.** The design above does not depend on them, which is deliberate — but if durable pub/sub with consumer groups becomes a headline feature, the list-based implementation is ours to build and maintain. Mitigation: because we speak only stock RESP, pointing Highway at Redis gets Streams immediately.
2. **Passive failover is an operational burden.** Someone must run a control plane. For single-node and replicated-pair deployments this is a non-issue; for a real cluster it is a genuine cost that should be weighed against Redis Sentinel or a managed offering.
3. **Durability defaults are not what people expect.** AOF acknowledges before commit, and there is no `WAIT`. If Highway advertises "durable," it must configure `--aof-commit-wait` and be honest about the latency cost.
4. **The Lua publish path is unverified.** If we ever want atomic enqueue-and-notify in one round trip, it depends on `redis.call('PUBLISH', …)` working from inside a script — traced in source but not documented and not executed. **This should be verified empirically before any design leans on it.**
5. **StackExchange.Redis 3.x needs Garnet ≥ 2.0.1-beta.9.** Publishing on a connection with an active subscription used to close the socket ([issue #1955](https://github.com/microsoft/garnet/issues/1955)), which breaks SE.Redis 3.x entirely since it defaults to RESP3 and multiplexes subscriptions. Pin the Garnet version, or force `protocol=resp2`.
6. **Do not use `GarnetClient` as the primary driver.** It has no pub/sub at all — its reply dispatcher cannot parse out-of-band push frames — no cluster redirection handling, and no RESP3. It is also not packaged separately, so depending on it drags in the entire ~10 MB server. Use StackExchange.Redis, which Microsoft states is *"tested with Garnet very well."*
7. **Cluster mode adds a slot-affinity constraint.** Any multi-key operation must hash to one slot, so related keys need hashtags (`hw:req:{orders}` style). Worth designing the key schema for from the start rather than retrofitting.
8. **Framework floor moves sharply.** Garnet targets `net8.0`/`net10.0` — no `netstandard2.0`. Since .NET 8 and 9 both reach end of support on 2026-11-10, the rewrite should target **.NET 10**. Existing `netstandard2.0` consumers cannot come along, which is a real, if overdue, break.

### 2.8 Verdict

**Yes — this is doable, and it is the right move.** But the shape is not the one you would guess going in.

The parts that map cleanly are the parts that matter most. RPC becomes better than it has ever been: list-based queues give us the timeouts, load balancing, backpressure, and durability that Part 1 lists as missing, and they delete the routing table, the catalog announcement protocol, the handshake, and the correlation polling loops along with the gateway process itself. Pub/Sub maps essentially one-to-one, because Garnet's at-most-once `PUBLISH` is exactly the guarantee Highway already provides.

The one genuine gap is durable pub/sub with consumer groups, and it is a gap because Garnet has no Streams and because custom C# procedures — the feature that looked most promising at the outset — turn out to be unable to publish. That combination is what pushes the design toward stock RESP commands only, and that constraint turns into the best decision in the plan: **a Highway that speaks only standard RESP runs on Garnet, Redis, or Valkey**, which de-risks every remaining Garnet-specific concern on this list.

The programming model — four class shapes, two verbs, location transparency — comes through completely untouched. A service written against Highway 0.8 should compile against the rewrite. That was the goal, and nothing discovered in this research threatens it.

**Suggested sequencing:**

1. Lift `Highway.Abstractions` to .NET 10, keeping the public types intact; replace the transport interfaces with a single narrow `IHighwayTransport` seam.
2. Build the Garnet transport: envelope, type registry, `System.Text.Json` serialization, request queues, doorbells, reply correlation with real timeouts.
3. Port the engine, fixing the local issues on the way — cache `ObjectMethodExecutor` per service, create a real DI scope per invocation, collapse the two service providers into one, and stop swallowing subscriber exceptions.
4. Delete `Highway.Gateway`, `Highway.Node`, `Highway.UnitTests`, `Highway.Transport.NamedPipes`, and the dead routing classes.
5. Port the samples unchanged as the compatibility proof, with an embedded `GarnetServer` replacing the gateway executable.
6. Add opt-in reliable delivery (`LMOVE` + lease) and opt-in durable channels.
7. Verify the Lua publish path, and benchmark dedicated-connection `BLMOVE` against the doorbell pattern before deciding the default.


---

## Part 3 — Architectural Review: Why Garnet and not the alternatives

*This section applies a senior-architect lens to the substrate decision. It evaluates Hangfire, RabbitMQ, ZeroMQ (NetMQ), and the existing .NET service-bus frameworks (NServiceBus, MassTransit, Wolverine, Rebus) against the specific requirements of Highway, and explains why Garnet remains the correct choice — and why Highway itself is worth building in the first place.*

### 3.1 What Highway actually needs from its substrate

Before comparing anything, we need to be precise about what Highway requires. Not every messaging system is the same *kind* of thing. Highway's substrate must provide:

1. **A durable request queue per service name** — so that multiple workers can compete for items (load balancing) and so that a message survives brief consumer absence.
2. **A low-latency notification mechanism** — so that idle workers wake instantly instead of polling.
3. **A reply-correlation path** — so that a caller's `ExecuteAsync` can receive a strongly-typed response within a timeout.
4. **Fire-and-forget pub/sub fan-out** — matching Highway's existing at-most-once channel semantics.
5. **A service registry with liveness** — so nodes can advertise what they host and detect when peers disappear.
6. **Embeddable for testing and single-process development** — `dotnet test` must work without Docker, without external processes, without cloud accounts.
7. **Deliverable as a NuGet package** — the entire Highway library, including the ability to run in local-only or embedded-server mode, must be `dotnet add package Highway` and nothing else.
8. **MIT or similarly permissive license** — Highway is intended as a free, open-source alternative to NServiceBus.
9. **C# native, debuggable, extensible** — same language, same toolchain, same IDE.
10. **High performance ceiling** — must not become the bottleneck before the application does.

### 3.2 Candidate evaluation

#### 3.2.1 Hangfire

**What it is:** A background job processing library for .NET. It persists job definitions (method calls serialized as expressions) into a storage backend (SQL Server, Redis, PostgreSQL) and executes them via worker threads. It has a dashboard, retries, scheduling, and recurring jobs.

**Why it fails for Highway:**

| Requirement | Hangfire | Verdict |
|---|---|---|
| Request queue | ✅ Has job queues | Partial — jobs are method invocations, not arbitrary message payloads |
| Low-latency notify | ❌ Polling-based (configurable interval, default 15s) | Fatal for RPC latency |
| Reply correlation | ❌ Fire-and-forget by design; no response path | Fatal |
| Pub/Sub fan-out | ❌ Not a concept | Fatal |
| Service registry | ❌ No discovery | Missing |
| Embeddable | ✅ In-process server | Good |
| NuGet delivery | ✅ | Good |
| License | LGPL (open) + commercial Pro features | Acceptable for core |
| C# native | ✅ | Good |
| Performance | Moderate — optimized for background jobs (seconds), not RPC (microseconds) | Insufficient |

**Verdict: Wrong tool entirely.** Hangfire solves a different problem (durable background job scheduling). It has no concept of request/response, no pub/sub, and its polling-based dispatch adds seconds of latency that would make RPC unusable. Highway could *use* Hangfire for scheduled/delayed jobs as a feature on top, but Hangfire cannot be the substrate beneath Highway.

#### 3.2.2 RabbitMQ

**What it is:** An Erlang-based message broker implementing AMQP 0-9-1, with exchanges, queues, bindings, and a rich routing model. The de-facto standard open-source broker for .NET, with a mature client library (`RabbitMQ.Client`).

**Why it is a reasonable candidate but the wrong choice:**

| Requirement | RabbitMQ | Verdict |
|---|---|---|
| Request queue | ✅ Excellent — durable queues with competing consumers | Best-in-class |
| Low-latency notify | ✅ Push-based delivery to consumers | Excellent |
| Reply correlation | ✅ Direct reply-to queue pattern | Good |
| Pub/Sub fan-out | ✅ Fanout/topic exchanges | Excellent |
| Service registry | ❌ Not built-in; must layer something on top | Missing |
| Embeddable | ❌ **Erlang VM required** — cannot embed in .NET process | **Fatal** |
| NuGet delivery | ❌ Client is NuGet, but **server requires separate Erlang/RabbitMQ install** | **Fatal** |
| License | MPL-2.0 (broker) | Acceptable |
| C# native | ❌ Erlang server; C# is only the client | Poor for extensibility |
| Performance | Good (tens of thousands msg/s per queue) but well below Garnet | Adequate |

**Verdict: Excellent broker, terrible substrate for a NuGet-deliverable library.** The fatal flaw is that RabbitMQ cannot be embedded. Every developer who installs Highway would need a running RabbitMQ server (with Erlang). That destroys the `dotnet add package` story, makes `dotnet test` require Docker or a cloud instance, and turns Highway into "yet another library that needs infrastructure" rather than the self-contained alternative to NServiceBus that we want.

If Highway were a *framework* that assumed enterprise infrastructure already exists (the way NServiceBus does), RabbitMQ would be the natural transport. But Highway's value proposition is the opposite: it's a library that works out of the box.

Additionally: RabbitMQ is written in Erlang. We cannot extend the broker in C#. We cannot debug into it. We cannot write custom server-side logic. We cannot ship it as a transitive dependency in a NuGet package. Every one of these is a strength that Garnet provides.

#### 3.2.3 ZeroMQ / NetMQ

**What it is:** A brokerless messaging library. Sockets with superpowers — pub/sub, request/reply, push/pull patterns all built directly between peers with no intermediary. NetMQ is the pure C# port.

**Why it fails for Highway:**

| Requirement | ZeroMQ/NetMQ | Verdict |
|---|---|---|
| Request queue | ❌ No persistence, no durable queue | Fatal |
| Low-latency notify | ✅ Excellent — direct socket delivery, sub-microsecond | Best possible |
| Reply correlation | ✅ REQ/REP and DEALER/ROUTER patterns | Good |
| Pub/Sub fan-out | ✅ Native PUB/SUB sockets | Good |
| Service registry | ❌ You build your own (the ZeroMQ guide dedicates entire chapters to this) | Missing — massive effort |
| Embeddable | ✅ In-process, library-only | Excellent |
| NuGet delivery | ✅ NetMQ is pure C# NuGet | Excellent |
| License | LGPL (libzmq) / MPL-2.0 (NetMQ) | Acceptable |
| C# native | ✅ NetMQ is pure C# | Good |
| Performance | Extreme — designed for millions msg/s | Excellent |

**Verdict: Fast but brokerless means we rebuild the broker ourselves.** ZeroMQ is a transport primitive, not a broker. If we use it, we have to build:
- Durable queue persistence
- Service discovery and heartbeat
- Clustering and failover
- Connection management and reconnection with backoff
- Message framing and serialization
- At-least-once delivery guarantees

That is precisely the work we are trying to *stop doing*. Part 1's entire defect list comes from Highway having built its own broker badly the first time. Using ZeroMQ means building it again — better, but still building it. And if we build it on top of something that has no persistence layer, we also need to add a database for durability.

ZeroMQ would have been the right substrate if Highway were a peer-to-peer mesh with no durability requirements. It is not.

#### 3.2.4 The existing .NET service-bus frameworks

This is the most important comparison, because it answers: **"Why build Highway at all? Don't NServiceBus, MassTransit, and Wolverine already solve this?"**

##### NServiceBus (Particular Software)

**What it is:** The gold standard commercial .NET service bus. Mature, battle-tested, enterprise-grade. Saga support, outbox pattern, monitoring platform (ServicePulse, ServiceInsight). Built on top of transports (RabbitMQ, Azure Service Bus, Amazon SQS, MSMQ).

**Licensing model (2026):**
- Free for development and testing
- **Production requires a commercial license — pricing is per-endpoint, per-year, unpublished (contact sales)**
- Historically reported at $2,000–$5,000+ per endpoint per year for enterprise tiers
- The full "Particular Platform" (monitoring, debugging tools) is additional

**Why Highway positions against it:**

NServiceBus is excellent software. It is also *expensive* and *heavy*. For a startup running 20 microservices, the licensing cost alone can be $40,000–$100,000/year before you've written a line of business code. It mandates an external transport (you still need RabbitMQ or Azure Service Bus underneath). Its programming model — while good — requires more ceremony than Highway's two-verb API.

Highway's positioning: **NServiceBus features at NuGet-package simplicity and zero license cost.** That is a large, underserved market.

##### MassTransit (v9 — now commercial)

**What it is:** The formerly-open-source .NET messaging abstraction. Supports RabbitMQ, Azure Service Bus, Amazon SQS, Kafka, and in-memory for testing. Saga support, outbox, scheduled messages.

**Critical change (2025):** MassTransit v9 moved to a **commercial license**. Production use requires payment. v8 remains open-source but will not receive new features or security patches indefinitely.

**Why this matters for Highway's positioning:**

MassTransit going commercial created a vacuum. The .NET ecosystem suddenly has no mature, free, open-source service bus that supports both in-process and distributed messaging with the full feature set. Wolverine is filling part of that gap, but it is younger and tightly coupled to specific transports.

Highway, if executed well, fills this vacuum with a *simpler* programming model and a *self-contained* deployment story.

##### Wolverine (JasperFx)

**What it is:** Next-generation .NET mediator + message bus by Jeremy Miller. MIT-licensed. Combines in-process command handling (replacing MediatR) with distributed messaging (replacing MassTransit). Uses source generators for zero-reflection dispatch. Supports RabbitMQ, Azure Service Bus, Amazon SQS, and has Marten integration for event sourcing.

**Honest comparison with Highway:**

| Dimension | Wolverine | Highway (target) |
|---|---|---|
| License | MIT | MIT |
| In-process dispatch | ✅ | ✅ |
| Distributed messaging | ✅ (needs external broker) | ✅ (Garnet — embeddable) |
| Self-contained | ❌ Needs RabbitMQ/ASB/SQS for distribution | ✅ Single NuGet, embedded server |
| RPC (request/response) | Supported but not primary pattern | First-class — `ExecuteAsync` is the headline |
| API surface | Convention-based handlers, more concepts | Two verbs, four class shapes, minimal |
| Maturity | ~3 years, active development | Rewrite of proven 7-year design |
| Saga/workflow | ✅ Built-in | ❌ Not in v1 |
| Outbox | ✅ With Marten/EF Core | ❌ Not in v1 |

**Where Highway differentiates:** Wolverine still requires you to operate an external broker for distribution. If you want two processes to talk to each other, you need RabbitMQ running somewhere. Highway's pitch is that you `dotnet add package Highway`, write your services, and distribution works — no Docker Compose, no cloud account, no infrastructure decisions. The Garnet server is either embedded for dev/test or run as a single lightweight binary in production.

Wolverine is also a *larger* framework. Highway is deliberately minimal — it does less, but what it does, it does with less ceremony.

##### Rebus

**What it is:** A lightweight, MIT-licensed service bus ("message bus without smarts"). Lean, simple, well-maintained. Supports many transports.

**Comparison:** Rebus is the closest philosophical match to Highway — lean, simple, focused. But Rebus still requires an external transport (RabbitMQ, Azure Service Bus, etc.) for distribution. It has no embeddable broker story, no built-in RPC with typed responses, and a slightly more verbose programming model.

### 3.3 The competitive landscape summary

```
┌─────────────────────────────────────────────────────────────────────┐
│                    .NET Messaging in 2026                            │
├─────────────────┬───────────┬──────────────┬────────────────────────┤
│ Framework       │ License   │ Self-contained│ RPC first-class        │
├─────────────────┼───────────┼──────────────┼────────────────────────┤
│ NServiceBus     │ Commercial│ ❌ Needs broker│ ❌ Messaging-first     │
│ MassTransit v9  │ Commercial│ ❌ Needs broker│ ❌ Messaging-first     │
│ Wolverine       │ MIT       │ ❌ Needs broker│ ⚠️  Supported          │
│ Rebus           │ MIT       │ ❌ Needs broker│ ❌ No typed RPC        │
│ Brighter        │ MIT       │ ❌ Needs broker│ ⚠️  Command-based      │
├─────────────────┼───────────┼──────────────┼────────────────────────┤
│ **Highway**     │ **MIT**   │ **✅ Embedded**│ **✅ Two verbs**       │
└─────────────────┴───────────┴──────────────┴────────────────────────┘
```

Highway occupies a unique position: **MIT-licensed, self-contained (no external infrastructure required for dev/test), with first-class RPC and pub/sub unified behind the simplest possible API.**

No existing framework occupies this cell.

### 3.4 Why Garnet specifically enables this position

The reason Garnet is the right substrate — and not any of the alternatives evaluated above — comes down to a unique combination of properties that no other system provides simultaneously:

**1. Embeddable as a NuGet dependency.**

`GarnetServer` is a public C# class in the `Microsoft.Garnet` NuGet package. You can start a Garnet server in-process with a few lines of code:

```csharp
var server = new GarnetServer(new GarnetServerOptions { Port = 0 /* ephemeral */ });
server.Start();
```

This means Highway can ship a `Highway.Testing` package that spins up an embedded Garnet instance for integration tests — no Docker, no external process, no cloud account. No other broker (RabbitMQ, Kafka, Azure Service Bus, Amazon SQS) can do this.

**2. Full C# extensibility.**

While Part 2 concluded that v1 should use only standard RESP commands (for portability), the extensibility path remains open. Garnet supports:
- Custom read/write commands via `REGISTERCS`
- Custom transactions (multi-key operations with arbitrary C# logic)
- Custom object types

This means if profiling reveals a hot path that needs server-side atomics, we can write it in C# and deploy it without changing languages or ecosystems. The extension runs in the same process, with the same debugger, and can be unit-tested like any other C# code.

No other broker offers this. RabbitMQ extensions are Erlang. Redis modules are C. Kafka has no server-side extensibility.

**3. NuGet-deliverable without external runtime.**

Garnet is pure .NET. It has no Erlang VM (RabbitMQ), no JVM (Kafka), no native dependencies beyond what .NET itself needs. The production deployment story is:
- **Development/Testing:** Embedded `GarnetServer` in-process. Zero setup.
- **Production (simple):** Run the `GarnetServer` binary (or Docker container) alongside your services. One process, one port.
- **Production (scale):** Run Garnet in cluster mode with replicas. Same binary, different config.

This is how we deliver on the "`dotnet add package Highway` and you're done" promise.

**4. RESP compatibility means zero lock-in.**

Because Part 2's design uses only standard RESP commands, Highway works against:
- **Garnet** (recommended — best performance, embeddable, C# extensible)
- **Redis** (if you already operate it)
- **Valkey** (the Linux Foundation Redis fork)
- **Any RESP-compatible server**

This is a strictly better position than coupling to any single system. Users who are allergic to "new infrastructure" can point Highway at their existing Redis. Users who want the best performance and the embedding story use Garnet. Users who want managed cloud use Azure Cache for Redis or the upcoming Cosmos DB Garnet Cache.

**5. Performance that removes itself from the conversation.**

The VLDB 2026 paper demonstrates Garnet achieving roughly 20× the throughput and one-third the p99 latency of standalone Valkey. At sub-300 µs p99.9 on the server side, the Highway client code and network will always be the bottleneck before Garnet is. This means we never have to apologize for our substrate choice or work around its limitations.

**6. MIT license, Microsoft-backed, actively maintained.**

- MIT-licensed — no licensing risk, no copyleft contamination
- Microsoft Research project with internal production usage (Azure Resource Manager, Windows Platform)
- Very active development (v2.1.2 shipped August 5, 2026)
- Aspire integration already available
- Not going anywhere

### 3.5 The abstraction layer decision

Given the analysis above, the correct architecture is:

```
┌─────────────────────────────────────────────────────────────┐
│  Application Code                                            │
│  [Service("name")] classes, [Channel("name")] classes       │
├─────────────────────────────────────────────────────────────┤
│  Highway.Core                                                │
│  IHighwayClient, AsyncService<T,TRes>, Engine, Catalog      │
├─────────────────────────────────────────────────────────────┤
│  IHighwayTransport (abstraction)                            │
│  - EnqueueRequest / DequeueRequest                          │
│  - SendReply / AwaitReply                                   │
│  - PublishChannel / SubscribeChannel                        │
│  - RegisterNode / Heartbeat                                 │
├──────────────┬──────────────────────────────────────────────┤
│  Highway.    │  Highway.      │  Highway.       │ Future    │
│  Transport.  │  Transport.    │  Transport.     │ Transport │
│  Garnet      │  Redis         │  InMemory       │ ...       │
│  (default)   │  (compat)      │  (testing)      │           │
└──────────────┴──────────────────────────────────────────────┘
```

**Three transport implementations:**

1. **`Highway.Transport.Garnet`** — The default. Uses StackExchange.Redis to talk RESP. Ships the embedded `GarnetServer` for testing. Recommended for production.
2. **`Highway.Transport.Redis`** — Literally the same code (both speak RESP). This is a packaging/branding distinction, not a code distinction. Exists so users who operate Redis know it works.
3. **`Highway.Transport.InMemory`** — For unit tests that don't need a real server. In-process queues, synchronous dispatch.

The `IHighwayTransport` interface is narrow and internal. It is **not** a user-facing extension point in v1. We are not building a universal transport abstraction (that is what MassTransit and NServiceBus do, and it is what makes them complex). We are building a focused library that works best with one substrate and happens to be compatible with a few others.

### 3.6 Positioning Highway in the market

**Highway is a free, MIT-licensed alternative to NServiceBus** that trades feature breadth for radical simplicity:

| What NServiceBus gives you | What Highway gives you |
|---|---|
| Sagas, outbox, delayed retries, monitoring platform, 15+ transports | Two verbs: `ExecuteAsync` and `PublishAsync` |
| Per-endpoint commercial license ($$$) | MIT license, forever free |
| Requires external broker infrastructure | `dotnet add package` — embedded server for dev, lightweight binary for prod |
| Convention-based with extensive configuration | Attribute-driven — `[Service]`, `[Channel]`, done |
| Enterprise sales cycle | NuGet install |

**Highway is not competing with NServiceBus on features.** It is competing on the 80% of use cases where teams need reliable RPC + pub/sub between services and do not need sagas, outbox patterns, or monitoring dashboards. For those teams, NServiceBus is $50K/year of capability they'll never use.

Highway's tagline: **"Distributed .NET in one NuGet package."**

### 3.7 What Highway should steal from the competition

Good architects borrow. Here is what we take from each:

| From | Steal |
|---|---|
| NServiceBus | The idea that messaging should be boring infrastructure that just works. The emphasis on error handling as data (which Highway already has via `Output.StatusCode`). |
| MassTransit | The consumer-per-queue concurrency model. The test harness concept (in-memory transport for tests). |
| Wolverine | Source-generated dispatch (no reflection at runtime). The idea of combining mediator and bus in one library. |
| Rebus | Simplicity as a feature. Lean documentation. "Message bus without smarts." |
| Hangfire | Dashboard inspiration — Highway should eventually have an observable queue-depth dashboard, but built on Garnet's sorted sets and lists rather than a custom persistence layer. |

### 3.8 Revised verdict

**Garnet is correct. The alternatives each fail on at least one critical requirement:**

- **Hangfire** — wrong category entirely (job scheduler, not messaging substrate)
- **RabbitMQ** — cannot embed, cannot ship via NuGet, cannot extend in C#
- **ZeroMQ/NetMQ** — no persistence, no broker; using it means rebuilding everything Part 1 proved we cannot maintain
- **NServiceBus** — commercial license, requires external broker
- **MassTransit v9** — commercial license, requires external broker
- **Wolverine** — MIT and good, but still requires external broker; does not provide the self-contained story
- **Rebus** — MIT and lean, but still requires external broker; no typed RPC

Garnet is the only system that is simultaneously:
- **Embeddable** (in-process for tests)
- **C# native** (same language, debugger, extensible)
- **NuGet-deliverable** (no external runtime required)
- **MIT-licensed** (no commercial risk)
- **RESP-compatible** (not locked in; works with Redis/Valkey too)
- **High-performance** (removes itself from the conversation)
- **Actively maintained by Microsoft** (not a community project that might go dormant)

The decision stands. Build Highway on Garnet.

---

# Part 4 — Addendum: Verified Garnet v2.1.2 Findings (Features 004 / 004.1)

*Appended at the project owner's request, 2026-08-06. Parts 1–3 above are unchanged. This section records what was verified against the pinned Garnet submodule (`libs/garnet` @ `8b329e30`, tag v2.1.2 + 2 commits — the latest release) while implementing features 004 and 004.1. Full details with file:line citations live in the feature-level research docs listed below.*

## What §2.3 got right

- **Custom C# procedures still cannot PUBLISH in v2.1.2.** Zero pub/sub surface in `IGarnetApi`; `RespServerSession.subscribeBroker` is private; `InternalsVisibleTo` excludes Highway; git history of the release contains no commit adding it (only a fix for Lua-script publish). The §2.3 limitation stands unchanged in the latest release.

## What changed relative to the Garnet assumed in Part 2

- **The extension API was heavily refactored.** `RegisterCmd`, `CustomCommandRegistry`, and `CustomRawCommandBase` no longer exist. Registration is `GarnetServer.Register` (`RegisterApi.NewTransactionProc` / `NewCommand` / `NewProcedure` / `NewType` / `NewModule`); `ArgSlice` became `PinnedSpanByte`.
- **Product direction superseded §2.4's recommendation** ("write no Garnet extensions in v1"). Per `product.md` §G6, Highway.Server is a Garnet extension with custom `HW.*` commands and the only supported broker. The publish limitation is sidestepped by hosting: Highway subclasses `GarnetServer` and reaches `protected storeWrapper` → `public readonly subscribeBroker` → `PublishNow(...)` (public, thread-safe). This is the doorbell mechanism.
- **Newly verified constraints found during implementation:**
  1. `CustomTransactionProcedure.Prepare` **cannot write RESP output** — validation errors are captured there and rendered in `Main` (validate-in-Main pattern, empirically proven with zero-key transactions).
  2. Key locking **blocks rather than times out** (`FailFastOnKeyLockFailure` defaults false); the only transient-abort path is watch-version validation — i.e., mirror-key reads performed in `Prepare`. The bare `ERR Transaction failed.` string is therefore an unambiguous transient-retry signal for clients.
  3. No public API reads back the OS-assigned port after `Port = 0` — embedded test servers probe a free port instead.
  4. `SetAdd` reports the added-member count, which gates idempotent re-subscribe backlog copying (feature 004.1).
  5. A rejected custom `HW.*` command whose argument contains a literal newline desyncs subsequent custom-command parsing on the same session (upstream parser quirk; accepted commands and fresh connections unaffected). Unreachable by Highway clients, which validate identifiers client-side — documented-and-mitigated, see `docs/features/004.1-server-remediation/research.md` § Finding 6.

## Where the details live

- `docs/features/004-server-hw-commands/research.md` — full verified extensibility report: registration, command shapes, the publish path, list/TTL primitives, reply writing, AOF behavior, hosting options
- `docs/features/004.1-server-remediation/research.md` — transaction semantics behind the remediation: error classification, watch conflicts, zero-key transaction spike, `SetAdd` added-count

---

# Part 5 — Addendum: what features 012–016 changed (2026-08-08)

This document records what was believed at the time it was written, and is corrected by
addendum rather than edited — its value is that it explains why decisions were made. Two of
its conclusions have since been overtaken.

## The channel backlog is gone

Part 4 §4 notes that `SetAdd`'s added-member count "gates idempotent re-subscribe backlog
copying (feature 004.1)". That mechanism no longer exists. A publish with no registered
subscriber group is now delivered to **nobody**, and `HW.SUBSCRIBE` copies nothing.

The backlog existed because nothing else could hold a message until someone could handle it.
Feature 014 added a queue — `SendAsync` / `[Queue]` / `IProcess<T>` — which does that
durably and without the backlog's surprising rule, where a late subscriber received an
arbitrary prefix of history determined by when the *first* subscriber happened to start.

## Garnet has more to offer than Part 3 assumed, and some of it is a trap

Feature 012's spikes measured Garnet's authentication surface directly rather than reading
its documentation, and found three things worth recording:

- **Per-name ACL rules work for custom commands.** `+hw.call` and `-hw.replay` are valid, so per-command roles are possible. They were specced and then descoped, because the deployment model that actually matters is one shared credential for a team.
- **Highway's commands are in Garnet's `@dangerous` category, not `@admin`.** `+@all -@dangerous` — a common hardening idiom — connects fine and then refuses every `HW.*` command.
- **A `nopass` default user is a total authentication bypass.** Any username with any password authenticates as the `+@all` default user. This is `nopass` behaving as defined, and it is a trap directly across the path of anything that generates an ACL file.

Full detail in `docs/features/012-introduce-security/design.md`.

## Where the authority now lives

Part 3's architectural conclusions stand. For **what Highway currently guarantees**, and
which of those guarantees the code actually keeps, the authority is
[`constraints.md`](constraints.md) — numbered, with an implementation status on every line.
