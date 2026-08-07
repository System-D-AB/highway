# Design: The Queue

## Overview

A queue is **RPC minus the reply**. That is not a simplification for the summary — it is the
design.

```
HW.CALL     → enqueue        ─┐
HW.DEQUEUE  → claim, lease    ├─ shared by RPC and Queue
HW.ACK      → complete       ─┘
HW.REPLY    → write reply slot   ← RPC only
```

Everything a durable queue needs — FIFO ordering, competing consumers, leases, redelivery,
delivery-attempt counting, dead-lettering, delayed delivery — already exists in that path
because RPC needed it and feature 013 finished it. This feature adds a *name space*, a
*contract shape*, and a *client verb*.

## What is genuinely new

| | |
|---|---|
| Client | `ISend`, `[Queue]`, `IProcess<T>`, `SendAsync`, a worker loop, scanner rules |
| Server | A key prefix, three thin commands, a catalog list, a `HW.STATS` form, a `HW.DLQ` target |
| Reused unchanged | Lease sweep, attempt counting, dead-lettering, delayed delivery, `[Idempotent]` |

## Decision 1: A separate key space, and three thin commands

Queues could reuse `hw:svc:*` and the existing commands verbatim — zero server changes. It
was rejected: a queue named `invoices` and a service named `invoices` would silently share a
work list, and `HW.STATS invoices` could not say which it was reporting. Name collisions
across kinds are the sort of thing that is discovered in production.

```
hw:q:{queue}:q                 Object  List         Pending messages, FIFO
hw:q:{queue}:proc:{nodeId}     Object  List         Claimed, not yet acknowledged
hw:q:{queue}:nodes             Object  Set          Nodes that have claimed work
hw:q:{queue}:nodelist          Main    String       Newline-delimited mirror (see below)
hw:q:{queue}:dlq               Object  List         Dead letters
hw:q:{queue}:retry             Object  Sorted Set   Backoff, if enabled
```

The mirror key is mandatory for the same reason it is everywhere else in Highway: reading an
object-store set during `Prepare` registers a watch that the later exclusive lock fails
(004.1). This is not a stylistic copy — the same trap applies to the same access pattern.

Three commands, each a thin subclass of the RPC equivalent parameterised by key prefix:

```
HW.QSEND  <queue> <messageId> <payload> [AT <ticks>]   →  +OK
HW.QCLAIM <queue> <nodeId>                             →  [messageId, payload] | nil
HW.QACK   <queue> <nodeId> <messageId>                 →  :0 | :1
```

**Why not one command with a kind discriminator** (`HW.CALL SVC|Q ...`): that is a breaking
change to three shipped commands, and every existing client would need updating for a feature
it does not use. Three additive commands cost more names and break nothing.

The implementations should share a base with the RPC commands rather than being copied — the
lease sweep and dead-letter branch are subtle enough that two copies would drift, and feature
013 already found the same defect living in three separate requeue paths.

`HW.DLQ` gains a `Q <queue>` target alongside `SVC` and `CH`. `HW.STATS` gains a queue form
reporting `kind queue`, depth, in-flight, and dead-lettered.

## Decision 2: The contract shape, and why the name is explicit

```csharp
[Queue("invoices")]
public sealed record GenerateInvoice : ISend
{
    public int OrderId { get; init; }
}

public sealed class InvoiceWorker : IProcess<GenerateInvoice>
{
    public Task ProcessAsync(GenerateInvoice message, CancellationToken ct = default) { ... }
}
```

**`ISend`, not `ICommand`.** Highway names its markers after the verb — `IPublish` pairs with
`PublishAsync`. `ISend` pairs with `SendAsync`. The Command/Event vocabulary is more standard
in messaging generally, but consistency inside Highway beats consistency with a vocabulary
Highway never adopted.

**`IProcess<T>`, not `IHandle<T>` or `IReceive<T>`.** `IReceive` was the better verb pair with
`Send`, and is blocked: `HW.RECEIVE` already means "consume a batch for a subscriber group",
and `MessagesReceived` is the flight-recorder event it emits. The same word meaning two things
depending on where you stand is exactly the drift the single protocol file exists to prevent.
`IProcess<T>.ProcessAsync` reads correctly as a sentence with the type as its object —
"process GenerateInvoice" — and describes what the developer's class actually does.

**The queue name is never inferred from the type name.** Convention would be shorter to type
and is a data-loss refactor: renaming `GenerateInvoice` would silently create a new queue
while every message in the old one is stranded with no processor and no error. For a durable
store the address must survive refactoring, so it must be written down. Same reasoning as
`[Service]` and `[Channel]`.

**Exactly one processor per message type**, enforced at startup naming both offending types.
Two processors is not fan-out — fan-out is `PublishAsync`. Highway already has this shape in
`ServiceWithSameNameAlreadyExistsException`.

## Decision 3: `SendAsync` returns the message id

```csharp
Task<string> SendAsync(ISend message, CancellationToken ct = default);
Task<string> SendAsync(ISend message, TimeSpan delay, CancellationToken ct = default);
```

Asymmetric with `PublishAsync`, which returns `Task`, and worth it. The first thing anyone
wants when a queued job misbehaves is to find it in the dead-letter queue, and `HW.DLQ PEEK`
returns entries keyed by message id. If the id is not returned at send time there is no way to
correlate, and adding it later is a breaking change.

The id is generated client-side (a GUID, as request ids already are) so the caller has it
before the round trip and it is stable under retry.

## Decision 4: Durability by default, or say so loudly

Requirement 5 AC2 forces a choice, because `new HighwayServerBuilder().Build()` is memory-only
and a queue that loses its contents on restart contradicts the entire point of the concept.

Three options were considered:

- **Refuse to serve queues without a data directory.** Safest, and it breaks the zero-configuration start that `constraints.md` and every prior feature protect.
- **Default a data directory.** Correct long-term and belongs to feature 016 (C4.5), where it is one line among the retention work rather than a surprise inside this one.
- **Serve them, and warn loudly at startup when a queue is used on a memory-only server.**

**The third is chosen for this feature**, with the second following in 016. The warning names
the queue and states that its contents are lost on restart, at `Warning` level, once — not per
send. This keeps the bare `Build()` working while making the gap impossible to miss, and it
avoids shipping a silent lie in the interim.

## Decision 5: What the worker loop reuses

`RpcWorkerLoop` already claims, executes, replies, acknowledges, and handles poison envelopes.
A queue worker is the same loop with the reply removed:

```
claim  →  deserialize  →  [Idempotent] gate  →  ProcessAsync  →  ack
```

The `[Idempotent]` gate is feature 013's, unchanged — including the crash-window behaviour
where an in-progress marker blocks rather than re-running.

**One difference worth stating:** RPC replies with a `400` for a poison envelope and
acknowledges, because a caller is waiting and deserves an answer. A queue has no caller. A
message that cannot be deserialized is therefore **dead-lettered immediately** rather than
acknowledged — acknowledging would discard it silently, and retrying would loop on a payload
that can never parse. This needs a second dead-letter reason code beyond `MAX_ATTEMPTS`, which
the framing already anticipates.

## Sequence

```
SendAsync(new GenerateInvoice{...})
  └─ catalog: type → queue name          (local; fails fast if unregistered)
  └─ envelope: v/src/ts/body + traceparent
  └─ HW.QSEND invoices <id> <envelope>   → RPUSH hw:q:invoices:q, ring doorbell
                                          → returns <id>

worker (any instance)
  └─ HW.QCLAIM invoices node-2
       ├─ promote due delayed + retries
       ├─ sweep expired leases → requeue or dead-letter
       └─ LPOP + RPUSH to hw:q:invoices:proc:node-2, stamped and attempt-carrying
  └─ IProcess<GenerateInvoice>.ProcessAsync(...)
  └─ HW.QACK invoices node-2 <id>        → remove from proc list
```

## Testing

| Test | Proves |
|---|---|
| `SentMessage_IsProcessedExactlyOnce` | R4.1 |
| `MultipleInstances_ShareTheWork` | **R4.2** — the property Pub/Sub cannot express |
| `SendWithNoProcessorRunning_IsProcessedWhenOneStarts` | **R1/R4.3** — the reason the feature exists |
| `SentMessage_SurvivesABrokerRestart` | R5.4, with AOF |
| `UnacknowledgedMessage_IsRedelivered` | R4.1 |
| `PoisonMessage_DeadLetters` | R4.4 |
| `UndeserializableMessage_DeadLettersImmediately` | Decision 5 — not acked, not looped |
| `DelayedSend_IsNotProcessedEarly` | R3.3 |
| `TwoProcessorsForOneType_FailAtStartup_NamingBoth` | R2.4 |
| `MessageTypeWithoutQueueAttribute_FailsAtStartup` | R1.4 |
| `SendToUnregisteredType_FailsLocally` | R3.5 |
| `QueueAndServiceMayShareAName` | R6.1 |
| `MemoryOnlyServer_WarnsOnce_WhenAQueueIsUsed` | Decision 4 |
| `SendAsync_ReturnsAnIdThatFindsTheMessageInTheDlq` | R3.2 — the reason for the return value |

`MultipleInstances_ShareTheWork` and `SendWithNoProcessorRunning...` are the two that justify
the feature; if either is weak, the feature has not been built.

## Risks

**A third verb is a third thing to explain.** Mitigated by the choosing rule being one
sentence, and by the triad table appearing in `product.md`, the samples and the README. The
risk is real but small: a queue is not a new concept, it is a familiar one Highway was
missing.

**Two copies of the lease/dead-letter logic.** Feature 013 found the same unbounded-redelivery
defect in three separate requeue paths — the strongest possible argument against copying this
code. The queue commands must share a base with the RPC ones.

**A queue on a memory-only server.** Decision 4 warns; 016 fixes. The window between them is
the honest cost of not bundling durability-by-default into this feature.

**Scope creep into 016.** Retention and byte caps will be tempting to add here because the
queue is where they belong. They are explicitly out of scope; this feature only has to avoid
making them harder, which per-queue keys and countable entries already do.

## Cross-references

- `docs/product/constraints.md` — C1 (delivered here), C2.4 (unblocked), C4 (deferred to 016)
- `docs/features/013-reliable-delivery/design.md` — the machinery inherited
- `docs/HIGHWAY-PROTOCOL.md` § Key Schema, § Entry Framing, § RPC Commands
- `docs/features/004.1-server-remediation/design.md` — the `Prepare` watch-conflict rule
