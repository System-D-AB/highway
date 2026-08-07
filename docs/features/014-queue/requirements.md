# Feature: The Queue — `SendAsync` / `[Queue]` / `IProcess<T>`

## Introduction

Highway can address work **with** a reply (`ExecuteAsync` — competing consumers, caller
waits) and events **without** one (`PublishAsync` — fan-out, nobody waits). It has no way to
say:

> *Do this work. Exactly one worker. I am not waiting for an answer.*

That is the missing third shape:

| | Competing consumers | Fan-out |
|---|---|---|
| **With a reply** | `ExecuteAsync` ✅ | — (meaningless) |
| **Without a reply** | **missing** | `PublishAsync` ✅ |

Because it is missing, developers reach for `PublishAsync` and then need it to behave like a
queue — which is what pushed dead letters, 100-day retention and gigabyte budgets onto a
fan-out mechanism in the first place. This feature gives that work its own home, and in doing
so lets Pub/Sub become simpler rather than more complicated.

### The shape

| | Contract | Attribute | Handler | Verb |
|---|---|---|---|---|
| RPC | `IReturn<TResponse>` | `[Service("...")]` | `AsyncService<TReq,TRes>` | `ExecuteAsync` |
| **Queue** | **`ISend`** | **`[Queue("...")]`** | **`IProcess<T>`** | **`SendAsync`** |
| Pub/Sub | `IPublish` | `[Channel("...")]` | `ISubscribe<T>` | `PublishAsync` |

```csharp
[Queue("invoices")]
public sealed record GenerateInvoice : ISend
{
    public int OrderId { get; init; }
}

public sealed class InvoiceWorker : IProcess<GenerateInvoice>
{
    public Task ProcessAsync(GenerateInvoice message, CancellationToken ct = default)
        => _invoices.GenerateAsync(message.OrderId, ct);
}

await client.SendAsync(new GenerateInvoice { OrderId = 42 });
```

**Three verbs is not more to learn than two**, because a queue is not a new concept — it is
the one every developer already has, and the rule for choosing is one sentence:

> **One handler → Send. Many handlers → Publish. Need the answer → Execute.**

### Why this is cheap

The machinery exists. `hw:svc:{name}:q` is already a competing-consumer queue with leases,
acknowledgement, delivery-attempt counting and dead letters. **A queue is RPC minus the
reply.** Feature 013's work is inherited rather than rebuilt: delayed sends, `[Idempotent]`
and `HW.DLQ` all work on day one.

## Glossary

- **Queue** — a named, durable, competing-consumer work list. Addressed by `[Queue("name")]`.
- **Processor** — a class implementing `IProcess<T>`. Exactly one per message type.
- **Send** — enqueue a message for exactly one processor, without waiting for a result.

## Requirements

### Requirement 1: The Contract

**User Story:** As a developer, I want to declare queued work with a C# class and an attribute, exactly as I already declare services and channels.

#### Acceptance Criteria

1. A message type is marked with `ISend` and named with `[Queue("name")]`
2. `class`, `record`, `record struct` and `struct` all work — the marker is an interface and serialization is System.Text.Json, as everywhere else in Highway
3. The queue name is **explicit in the attribute, never inferred from the type name.** A convention would be shorter to type and is a data-loss refactor waiting to happen: renaming the class would silently create a new queue while every message in the old one is stranded with no processor. The name must survive refactoring, so it must be written down
4. A message type carrying `ISend` without `[Queue]` is rejected at startup, naming the type — matching the existing treatment of `[Service]` and `[Channel]`
5. A queue name follows the same identifier rules as every other Highway name

### Requirement 2: The Processor

**User Story:** As a developer, I want one class that does one job, discovered automatically.

#### Acceptance Criteria

1. `IProcess<T>` declares `Task ProcessAsync(T message, CancellationToken ct = default)`
2. Processors are discovered by the same assembly scanning that already finds services and subscribers — no registration, no configuration
3. Processors participate in DI with the same lifetime rules as services
4. **Exactly one processor per message type.** Two classes implementing `IProcess<GenerateInvoice>` is an error at startup naming both types, not a fan-out — fan-out is what `PublishAsync` is for
5. A queue declared with no processor in the node is not an error: a node may send to a queue it does not process
6. `IProcess<T>` is an **interface**, not an abstract class. There is no response type to constrain, and interfaces compose

### Requirement 3: Sending

**User Story:** As a developer, I want to hand work to a queue in one line and get on with my request.

#### Acceptance Criteria

1. `SendAsync(ISend message, CancellationToken ct)` enqueues and returns
2. **It returns the message id.** `Task<string>`, not `Task`. The first thing anyone wants when a queued job misbehaves is to find it in the dead-letter queue, and that is only possible if the id was kept. Cheap now; a breaking change later
3. `SendAsync(ISend message, TimeSpan delay, CancellationToken ct)` defers the work, reusing feature 013's mechanism rather than inventing a second one
4. **Sending never requires a running processor.** The message waits. This is the capability whose absence made people misuse `PublishAsync`
5. Sending to a queue with no `[Queue]` attribute fails locally, before the network, with a message naming the type — as `PublishAsync` already does for unregistered channels
6. A payload above `MaxPayloadBytes` is rejected before the network

### Requirement 4: Delivery Guarantees

**User Story:** As an operator, I want a queued message to be processed at least once and never quietly lost.

#### Acceptance Criteria

1. **At least once.** A message is delivered to exactly one processor at a time; if that processor crashes or never acknowledges, it is redelivered after the lease
2. **Competing consumers by default.** Multiple instances of the same application share the work. No group name, no coupling to node identity — this is the property Pub/Sub cannot express
3. **The message survives until processed.** Nothing removes it except successful acknowledgement, dead-lettering, or an explicit purge. Broker restart with a data directory does not
4. Delivery-attempt counting, `MaxDeliveryAttempts` and dead-lettering apply exactly as they do to RPC — inherited from feature 013, not reimplemented
5. `HW.DLQ` gains a queue target, so dead-lettered messages are inspectable, requeueable and purgeable through the same command
6. `[Idempotent]` applies to queue messages
7. Ordering is FIFO per queue, and a redelivery returns to the **head**, preserving order — matching Pub/Sub's existing behaviour and subject to the same backoff trade-off

### Requirement 5: Durability

**User Story:** As an operator, I want the queue to be the thing I can trust to hold my work.

**This is the queue's reason to exist.** `constraints.md` C1.2 makes it the durable store, and
this feature must not ship a queue whose contents are lost on restart in the configuration
people meet first.

#### Acceptance Criteria

1. Queue state lives in the Garnet keyspace and is covered by AOF when a data directory is configured
2. **A queue must be durable by default.** A server built with no configuration must not silently offer a queue that loses everything on restart. Either a data directory becomes the default, or an unconfigured server refuses to serve queues, or the limitation is surfaced loudly at startup — the design chooses and justifies one
3. Retention and size limits are **out of scope for this feature** and are `constraints.md` C4.1–C4.6, planned as feature 016. This feature must not build a queue that is structurally hard to bound later: keys are per queue, entries are countable and measurable
4. A test proves a sent message survives a broker restart and is processed afterwards

### Requirement 6: Separation from Services

**User Story:** As an operator, I want a queue named `invoices` and a service named `invoices` to be different things.

#### Acceptance Criteria

1. Queue keys live under their own prefix, distinct from `hw:svc:`, so a queue and a service may share a name without colliding
2. `HW.STATS` reports a queue distinctly from a service, and the reply says which kind it is
3. The node catalog distinguishes queues from services and channels, so `HW.DISCOVER` and the dashboard are not misleading
4. A queue is **not** a service: it does not appear in `HW.DISCOVER` results for services and does not carry a response type

### Requirement 7: Protocol and Documentation

#### Acceptance Criteria

1. `docs/HIGHWAY-PROTOCOL.md` is updated in this feature: any new commands, the key schema, the catalog shape, and the `HW.DLQ` queue target
2. `ProtocolConformanceTests` stays green
3. `docs/product/constraints.md` C1.1, C1.2 and C1.3 move from *Not built* to *Met*
4. `docs/product/product.md` and the roadmap reflect what shipped
5. The samples demonstrate a queue — send, process, competing consumers, and a poison message reaching the dead-letter queue — and are re-run with a `samples/RUNLOG.md` entry
6. The documentation states the deployment consequence prominently: **three instances of a processor share the work; three instances of a subscriber each get a copy**

### Requirement 8: No Regression

#### Acceptance Criteria

1. All existing tests pass; `dotnet build` produces zero warnings
2. `new HighwayServerBuilder().Build()` still starts a working broker with no configuration
3. RPC and Pub/Sub behaviour is unchanged by this feature
4. Any defect the sample run exposes is fixed in the library with a regression test, never worked around in the sample

## Non-Goals

- **Retention and size caps.** `constraints.md` C4, feature 016. This feature must not make them harder, and need not deliver them.
- **Per-queue concurrency, priority, or selective consumption.** `WorkerConcurrency` applies as it does for RPC. Priority and filtering are not planned.
- **Changing Pub/Sub.** The backlog removal that this feature makes possible is listed as a follow-up in `tasks.md`, deliberately separate so this feature stays reviewable.
- **A reply channel.** A queue message has no response. If you need one, you want `ExecuteAsync`.
- **Queue declaration or provisioning API.** A queue exists because a `[Queue]` attribute exists — implied, like every other Highway name.
- **Renaming `ISubscribe` or `AsyncService`** to match `IProcess`. RPC's handler is an abstract class while the other two are interfaces; that inconsistency predates this feature and is not worth a breaking change.

## Cross-References

- `docs/product/constraints.md` — C1 (this feature), C2.4 (what it makes possible), C4 (what it defers)
- `docs/features/013-reliable-delivery/` — the dead-letter, delay and dedup machinery inherited here
- `docs/HIGHWAY-PROTOCOL.md` § Key Schema, § RPC Commands — the queue mechanics being reused
- `docs/features/004.1-server-remediation/design.md` — the `Prepare`-phase watch-conflict rule
