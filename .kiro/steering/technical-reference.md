# Technical Reference

This file orients you. It deliberately **does not duplicate** the protocol or the
API surface — every copy of those has drifted from the code within one feature.
It points at the authoritative source for each and states the decisions that are
stable enough to be worth writing down.

## Highway Protocol (HW.* Commands)

**The protocol is defined in one file: [`docs/HIGHWAY-PROTOCOL.md`](../../docs/HIGHWAY-PROTOCOL.md).**

Every command, argument order, reply shape, error code, key, entry framing,
doorbell channel and cross-command invariant lives there and nowhere else. It is
enforced: `ProtocolConformanceTests` parses its Command Index and checks it
against a running server in both directions, so a command that is registered but
undocumented — or documented but unregistered — fails the test suite.

This file carried a command table until feature 007. That copy had already
drifted from shipped code: it described only one of `HW.HEARTBEAT`'s three forms,
gave the wrong reply shape for `HW.DISCOVER` and `HW.STATS`, and omitted the
error contract entirely. A second copy is a second thing to get wrong.

## Public API Surface

**The code is the reference.** Read the types directly:

| Surface | Where |
|---|---|
| Contracts, attributes, base classes | `src/Highway.Abstractions/` |
| Client options | `src/Highway.Client/HighwayOptions.cs` |
| Server options | `src/Highway.Server/HighwayServerOptions.cs` |
| Server builder | `src/Highway.Server/HighwayServerBuilder.cs` |
| Embedded test server | `src/Highway.Server/HighwayTestServer.cs` |

All of these carry XML documentation, including the rationale for non-obvious
defaults. That is where option semantics belong — not in a snippet here that goes
stale the next time an option is added.

This section previously reproduced the client and server configuration APIs. By
feature 007 that snippet listed three client options when twelve existed, and
showed a `WithDashboard(port:)` builder method that **has never existed in the
code**. Removed rather than refreshed, because refreshing it just restarts the
clock.

## The Programming Model

The shape of the model is stable and worth stating; the exact signatures are in
`Highway.Abstractions`.

Two verbs: `ExecuteAsync` (RPC) and `PublishAsync` (Pub/Sub).

Four class shapes:

| Concept | What you write | Attribute |
|---|---|---|
| Request | POCO implementing `IReturn<TResponse>` | `[Service("name")]` |
| Response | POCO extending `Output` | — |
| Service | Class extending `AsyncService<TReq, TRes>` | — |
| Channel message | POCO implementing `IPublish` | `[Channel("name")]` |
| Subscriber | Class implementing `ISubscribe<T>` | — |

Nothing is registered by hand. Assembly scanning discovers all of it at startup.

## Key Architecture Decisions

1. **Highway.Server is the only broker** — no Redis/Valkey compatibility. Custom commands enable atomic operations.
2. **RESP framing preserved** — `redis-cli` and SE.Redis `Execute()` work unmodified.
3. **Garnet AOF for durability** — queued messages survive server restart.
4. **No Streams dependency** — Garnet has no `X*` commands; queues are lists plus doorbells.
5. **Server-side subscription management** — the client says "subscribe"; the server owns routing.
6. **Competing consumers** — multiple nodes hosting one service share work via atomic dequeue.
7. **System.Text.Json only** — no `TypeNameHandling.All` vulnerability from v0.8.
8. **Errors are data** — `Output.StatusCode` and `ErrorDetail`; `ExecuteAsync` never throws for a service-level outcome.
9. **At-least-once, both paths** — handlers own idempotency. Neither RPC nor pub/sub is exactly-once.
10. **Doorbells are a latency optimization only** — correctness rides on the backstop sweep, never on delivery.

## Non-Goals (v1)

- Sagas / process managers
- Transactional outbox
- Full monitoring dashboard (`HW.STATS` is the data source; no UI ships in v1)
- Multi-transport abstraction
- Redis/Valkey compatibility
- Wire compatibility with Highway 0.8

## Reference Documents

| Document | Status | Use it for |
|---|---|---|
| [`docs/HIGHWAY-PROTOCOL.md`](../../docs/HIGHWAY-PROTOCOL.md) | **Live, enforced** | Anything protocol-facing |
| `docs/product/product.md` | Live | Product vision and goals. Links to the protocol file rather than restating it |
| `docs/product/research.md` | Live, historical record | Why Garnet and not the alternatives; the v0.8 system. Parts 1–3 record what was believed *before* implementation — read Part 4 first |
| `docs/product/roadmap.md` | Live | Feature order and status |
| `docs/features/{NNN}-*/` | Live | The *reasoning* behind each decision |
