# Highway — A Distributed Application Runtime for .NET

## The Name

A highway connects many small roads. Traffic flows smoothly because the infrastructure handles routing, speed, capacity, and safety. Drivers (developers) focus on their destination (business logic), not on building the road.

**Highway is a distributed application runtime.** One server, one connection, one package — and your application gets RPC, Pub/Sub, Caching, Locking, Rate Limiting, Counters, Scheduled Delivery, and Leader Election. No sidecar. No YAML. No Kubernetes operator. Just `dotnet add package Highway.Client` and go.

## Positioning Evolution

```
v1.0 (current scope):   "Distributed .NET without the infrastructure tax"
                         → RPC + Pub/Sub + Observability

v1.x (runtime scope):   "A distributed application runtime for .NET"
                         → RPC + Pub/Sub + Cache + Lock + Rate Limit + Counters
                           + Delayed Messages + Leader Election + Deduplication
```

## Why This Works

Highway.Server is a Garnet server — a full in-memory store with strings, hashes, lists, sets, sorted sets, TTL, atomic operations, Lua scripting, AOF persistence, and clustering support. Every node already has a persistent connection to it. That connection is idle between messages.

Garnet gives us:
- `GET`/`SET`/`DEL` + TTL → Distributed Cache
- `SET NX EX` → Distributed Locking
- `INCR` + `EXPIRE` → Rate Limiting
- `INCR` → Atomic Counters / Sequences
- `ZADD` with score = timestamp → Delayed/Scheduled Messages
- `HSET`/`HGET`/`HDEL` → Shared State / Distributed Dictionary
- Lock + Heartbeat → Leader Election / Singleton Services
- `SET NX` + cached response → Request Deduplication

None of these require new server commands. They're stock Garnet operations wrapped in a typed .NET API, flowing through the connection every node already has.

## Competitive Landscape

| Need | Without Highway | With Highway |
|---|---|---|
| RPC between services | gRPC + service mesh | `ExecuteAsync` |
| Pub/Sub events | RabbitMQ / SQS + MassTransit | `PublishAsync` |
| Distributed cache | Redis + IDistributedCache | Already there — same server |
| Distributed lock | Redlock + separate Redis | `client.AcquireLockAsync()` |
| Rate limiting | Custom middleware + Redis | `client.CheckRateLimitAsync()` |
| Counters/sequences | Database sequence or Redis INCR | `client.IncrementAsync()` |
| Scheduled messages | Hangfire / Quartz + database | `client.PublishDelayedAsync()` |
| Leader election | ZooKeeper / Consul / custom | `[Singleton]` attribute |
| Idempotency | Custom middleware + Redis | `[Idempotent]` attribute |

**Dapr comparison:** Dapr provides similar building blocks but requires a sidecar process, YAML component definitions, and typically Kubernetes. Highway is an embedded runtime — no sidecar, no ops tooling, no container orchestrator. `dotnet add package` and you have everything.

**The positioning statement:**

> Highway is what Dapr would be if it were a library instead of a sidecar, free instead of vendor-locked, and built for .NET instead of abstracted across languages.

## Feature Catalog

### Tier 1: Core (v1.0 — implemented)

| Feature | API Surface | Underlying Mechanism |
|---|---|---|
| RPC | `ExecuteAsync<T>(request)` | `HW.CALL` / `HW.DEQUEUE` / `HW.REPLY` / `HW.ACK` |
| Durable Pub/Sub | `PublishAsync(message)` | `HW.PUBLISH` / `HW.SUBSCRIBE` / `HW.RECEIVE` / `HW.RACK` |
| Service Discovery | `HW.HEARTBEAT` / `HW.DISCOVER` | Heartbeat + registry |
| Observability | `HW.STATS` / `HW.REPLAY` (future) | Flight recorder + OTEL |

### Tier 2: Runtime Primitives (v1.x — next)

| Feature | API Surface | Underlying Mechanism |
|---|---|---|
| Distributed Cache | `IDistributedCache` (standard .NET) | `GET`/`SET`/`DEL` + `SETEX` |
| Distributed Lock | `client.AcquireLockAsync(key, ttl)` | `SET key NX EX` |
| Rate Limiting | `client.CheckRateLimitAsync(key, limit, window)` | `INCR` + `EXPIRE` (Lua) |
| Atomic Counters | `client.IncrementAsync(key)` | `INCR` |

### Tier 3: Application Patterns (v1.x+)

| Feature | API Surface | Underlying Mechanism |
|---|---|---|
| Delayed Messages | `client.PublishDelayedAsync(msg, delay)` | Sorted set + delivery loop |
| Leader Election | `[Singleton]` on service | Lock + heartbeat |
| Shared Dictionary | `client.GetDictionary<K,V>(name)` | `HSET`/`HGET`/`HGETALL` |
| Request Deduplication | `[Idempotent(Window)]` on request | `SET NX EX` + response cache |
| Content-Based Routing | `[Filter("...")]` on subscriber | Server-side filter evaluation |

## Design Principles for Runtime Features

1. **Same connection** — No new connections. Every runtime feature flows through the existing `ConnectionMultiplexer`.

2. **Same server** — No new processes. The Garnet server that handles RPC and Pub/Sub also handles caching, locking, etc.

3. **Stock Garnet operations** — No new `HW.*` commands unless atomicity demands it. Prefer wrapping standard `GET`/`SET`/`INCR`/etc.

4. **Typed .NET API** — Not raw strings. Every operation has a strongly-typed, IntelliSense-friendly surface.

5. **Standard interfaces where they exist** — `IDistributedCache`, `IDistributedLock` (if standardized). Don't invent when .NET already defines it.

6. **Opt-in, not opt-in-to-avoid** — Each runtime feature is independently usable. You don't have to use the cache to use RPC. No coupling between features.

7. **Zero external infrastructure** — The same "one server, one package" promise applies to all features. No separate Redis for cache, no ZooKeeper for election, no database for sequences.

## Key Schema Convention for Runtime Features

All runtime feature keys live under `hw:rt:` to avoid collision with the messaging keys (`hw:svc:`, `hw:ch:`, `hw:rep:`, `hw:reg:`, `hw:fdr:`):

```
hw:rt:cache:{key}           — IDistributedCache entries
hw:rt:lock:{key}            — Distributed lock tokens
hw:rt:rate:{key}:{window}   — Rate limit counters
hw:rt:seq:{key}             — Atomic sequence counters
hw:rt:delay:{channel}       — Sorted set of delayed messages
hw:rt:leader:{service}      — Leader election lock
hw:rt:dict:{name}:{field}   — Shared dictionary entries (or hash)
hw:rt:dedup:{key}           — Idempotency tokens + cached responses
```

## Success Criteria

1. A developer can add distributed caching to a Highway application with zero additional configuration — no new connection string, no new server.
2. A developer can implement distributed locking with one line: `await using var lock = await client.AcquireLockAsync(...)`.
3. Running `dotnet test` against all runtime features requires zero external infrastructure (same `HighwayTestServer`).
4. The runtime features add < 1ms overhead over raw Garnet operations for equivalent commands.
5. The "hello world" for each runtime feature fits in 5 lines of application code.
