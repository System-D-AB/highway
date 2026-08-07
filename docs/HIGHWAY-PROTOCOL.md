# The Highway Protocol

**Protocol version 2.2** — reflects everything shipped through features 012 and 014.

## About

This file is the complete and authoritative definition of the Highway wire protocol. Everything Highway adds to Garnet is here: every command, every reply shape, every error, every key, every stored byte layout, every doorbell channel, and the guarantees that span commands.

**Scope.** Highway's extension surface only. Garnet's own command set is out of scope — except for the handful of stock commands a Highway client is *required* to issue, which are listed in [Stock Garnet Dependencies](#stock-garnet-dependencies). A client built from the `HW.*` commands alone cannot function.

**Authority.** `docs/product/product.md` § "Highway Protocol (HW.* Commands)" contains an earlier command table. That document is read-only and remains the product's founding vision, but its protocol table has diverged from the implementation and **this file supersedes it** for anything implementation-facing. Where the two disagree, this file governs. Feature specs under `docs/features/` keep the *reasoning* behind each decision; this file is the reference for *what* the protocol is.

**How this file stays true.** The [Command Index](#command-index) is parsed by `ProtocolConformanceTests` and checked against a running server in both directions — a command documented but not registered, registered but not documented, or registered with a different arity all fail the test suite. The rest of the file is prose and is the author's responsibility; a feature that changes the protocol is required to update this file in the same feature.

**Reading order.** Implementing a client: [Transport & Framing](#transport--framing), then [Error Contract](#error-contract), then the command sections you need, then [Stock Garnet Dependencies](#stock-garnet-dependencies). Operating a server: [Key Schema](#key-schema) and [Server Options](#server-options).

---

## Contents

- [Protocol Version & Changelog](#protocol-version--changelog)
- [Command Index](#command-index)
- [Transport & Framing](#transport--framing)
- [Error Contract](#error-contract)
- [RPC Commands](#rpc-commands)
- [Pub/Sub Commands](#pubsub-commands)
- [Registry Commands](#registry-commands)
- [Observability Commands](#observability-commands)
- [Queue Commands](#queue-commands)
- [Dead Letter Commands](#dead-letter-commands)
- [Stock Garnet Dependencies](#stock-garnet-dependencies)
- [Key Schema](#key-schema)
- [Entry Framing](#entry-framing)
- [Doorbell Channels](#doorbell-channels)
- [Invariants](#invariants)
- [Server Options](#server-options)

---

## Protocol Version & Changelog

**Current version: 2.2**

A version is documentation for humans. Nothing negotiates it at runtime and no command reports it — Highway has no capability handshake.

**Versioning rule.** The minor version increases for additive changes: a new command, a new optional argument, a new field appended to a self-describing reply, a new error code. The major version increases for anything that could break a conforming client: removing or renaming a command, changing argument order, changing an existing reply's shape, or changing what an existing error code means.

| Version | Features | Change |
|---|---|---|
| 2.2 | 012 | **Security.** No command changes. `AUTH` joins the stock dependencies a client must issue against a secured server; the error contract gains a third class (`NOAUTH`, `WRONGPASS`, `NOPERM` — permanent, carrying neither existing marker); a section documents authentication, TLS and the `@dangerous` trap. Additive. |
| 2.1 | 014 | **The queue.** Adds `HW.QSEND`, `HW.QCLAIM` and `HW.QACK` under a `hw:q:` key space, a `Q` target on `HW.DLQ`, a `Q:name` form on `HW.STATS`, and a `queues` list in the node catalog. Additive — no existing command, reply or key changed. A queue is RPC minus the reply and shares its lease sweep, so dead-lettering, deferred delivery and `[Idempotent]` all apply unchanged. |
| 2.0 | 013 | Reliable delivery, parts 1 and 2. **Delayed delivery:** `HW.PUBLISH` gains an optional `AT <ticks>` argument (arity 3 → -3) and the `hw:ch:{channel}:delayed` sorted set; promotion is driven by `HW.RECEIVE`, not a timer. **Dead letters:** Adds a **delivery attempt count** to four entry framings and a `0xFF` version byte that makes pre-013 entries detectable; adds `HW.DLQ`, the dead-letter keys, the `HW_STORAGE_FORMAT` error, a `deadLettered` field on two `HW.STATS` forms, and two recorder event types. **Major** because the stored entry format changed: a broker started against a pre-013 data directory refuses to serve the affected queues rather than misparsing them. Nothing on the wire changed — no client needs modifying. |
| 1.1 | 002 | Observability. Adds `HW.REPLAY` and a fourth `HW.STATS` form (`RECORDER`); adds the optional `tp` envelope field carrying W3C trace context; documents the `ActivitySource` names. Additive — no existing command, reply or envelope field changed. |
| 1.0 | 004, 004.1, 005, 006 | Initial specification. Nine RPC and pub/sub commands (004); the `ERR HW_*` error contract, identifier rules and mirror keys (004.1); reply-slot retrieval and the client envelope (005); three registry commands, the three-form `HW.HEARTBEAT`, and dead-node pruning (006). |

---

## Command Index

Every command Highway registers. Arity follows the Redis convention: a positive number is the exact argument count **including the command name**; a negative number is the minimum. `Forms` is the number of distinct behaviours documented in that command's section.

| Command | Arity | Forms | Summary |
|---|---|---|---|
| `HW.CALL` | 4 | 1 | Enqueue an RPC request and ring the service doorbell |
| `HW.REPLY` | 3 | 1 | Write the caller's reply slot and ring the reply doorbell |
| `HW.DEQUEUE` | 3 | 1 | Claim the next request for a node; sweeps expired leases and dead nodes |
| `HW.ACK` | 4 | 1 | Acknowledge a claimed request |
| `HW.PUBLISH` | -3 | 2 | Durable fan-out to every subscriber group, immediately or at a future time |
| `HW.SUBSCRIBE` | 3 | 1 | Register a subscriber group and copy any backlog |
| `HW.UNSUBSCRIBE` | 3 | 1 | Remove a subscriber group and delete its state |
| `HW.RECEIVE` | -3 | 1 | Consume a batch of messages for a group; promotes due delayed messages and sweeps expired leases |
| `HW.RACK` | 4 | 1 | Acknowledge a consumed message |
| `HW.HEARTBEAT` | -2 | 3 | Register a catalog, prove liveness, or depart |
| `HW.DISCOVER` | 2 | 1 | Live nodes hosting a service |
| `HW.STATS` | -1 | 4 | Server, service, channel, or recorder counters |
| `HW.REPLAY` | -2 | 1 | Recent recorded operations for one name |
| `HW.DLQ` | -3 | 3 | Inspect, requeue, or purge dead letters |
| `HW.QSEND` | -4 | 2 | Enqueue work for exactly one processor, now or at a future time |
| `HW.QCLAIM` | 3 | 1 | Claim the next queued message for a worker; promotes deferred work and sweeps expired leases |
| `HW.QACK` | 4 | 1 | Acknowledge a claimed queued message |

---

## Transport & Framing

### RESP, not a new protocol

Highway adds commands to Garnet; it does not change the wire format. Everything here is standard RESP, so `redis-cli`, RESP protocol analyzers, and StackExchange.Redis `Execute()` all work against a Highway server unmodified. Stock Garnet commands continue to work alongside Highway's, and Highway's keys are namespaced so they cannot collide with application data.

### Arity convention

Arity counts the command name itself:

- **Positive** — the exact argument count. `HW.CALL` has arity 4: the name plus three arguments.
- **Negative** — the minimum. `HW.RECEIVE` has arity -3: the name plus at least two arguments.

Garnet enforces arity before a command's own validation runs, so a wrong argument count produces Garnet's error message rather than one of Highway's codes. Note that a negative arity places **no upper bound**: extra arguments pass the arity check and are ignored by the command.

### Identifiers

Service names, channel names, group names, node IDs, request IDs and message IDs are *identifiers* and share one rule:

- non-empty;
- at most `MaxIdentifierBytes` (default 256) bytes;
- every byte `>= 0x20` and not `0x7F`.

No C0 control characters, no DEL. Validation runs on raw bytes before any string decoding, so no key is ever derived from an invalid value. A violation is rejected with `HW_INVALID_ARG`.

**Why the rule exists.** Several internal keys are newline-delimited lists (see [Key Schema](#key-schema)). An identifier containing a newline would split into two entries and silently corrupt routing — a node could vanish from a worker set, or messages could fan out to a group no consumer drains. Banning the whole C0 range plus DEL is cheaper to reason about than banning newline alone and costs nothing for real identifiers.

### Payloads

Payloads are **not** identifiers. They are opaque bytes, stored and returned byte-for-byte, subject only to a size cap (`MaxPayloadBytes`, default 1 MiB). Any byte value is permitted, including control characters and invalid UTF-8.

### The Highway envelope

The `HW.*` commands never inspect a payload, so the payload format is a client convention rather than a server rule. The .NET client wraps every RPC request, RPC reply and published message in this JSON envelope; a client that wants to interoperate must do the same:

```json
{ "v": 1, "src": "orders-1", "ts": "2026-08-07T12:34:56.7890000Z", "body": {} }
```

| Field | Meaning |
|---|---|
| `v` | Envelope schema version. A reader rejects any value other than `1`. |
| `src` | Sending node's name — the audit and tracing hook. |
| `ts` | Send timestamp, ISO-8601 UTC. |
| `body` | The application object, embedded as a nested JSON value. |
| `tp` | **Optional.** W3C `traceparent` for distributed tracing (feature 002). |

The `tp` field is optional in both directions and does **not** change the
envelope version. `v` and `body` are the only required fields, and a reader
ignores properties it does not recognise — verified both ways: an existing reader
given an envelope carrying `tp` reads it correctly, and a new reader given one
without simply finds it absent. A reader that does not understand `tp` must
ignore it rather than reject the envelope.

No polymorphic type metadata is ever written. The wire carries a service or channel name and a JSON shape, never CLR type identity.

---

## Error Contract

This is the most important section for a client implementer: the whole retry policy rests on it. Getting it wrong means either spinning forever on a request that can never succeed, or silently dropping one that would have worked.

### The classification rule

> A reply beginning `ERR HW_` is **permanent** — never retry.
> The exact message `ERR Transaction failed.` is **transient** — safe to retry.
> Anything else is **permanent**.

That is the entire rule, and it is total.

### Why the bare message is the retryable one

`ERR Transaction failed.` is not Highway's message — it is Garnet's, emitted when a transaction aborts. For Highway commands the cause is a **watch-version conflict**: several commands read a key during their `Prepare` phase, which registers a watch, and the transaction is abandoned if that key changes before locks are taken. The command performs **no work at all**, which is exactly what makes retrying safe.

Highway's own errors carry the `ERR HW_` prefix so the bare Garnet message stays unambiguous. Highway deliberately emits nothing that could be confused with it.

### Codes

| Code | Meaning | Class |
|---|---|---|
| `ERR HW_INVALID_ARG <detail>` | An identifier is blank, contains a control character, exceeds the length cap, or is otherwise malformed (a non-numeric message ID; a second `HW.HEARTBEAT` argument that is neither `BYE` nor valid catalog JSON) | Permanent |
| `ERR HW_PAYLOAD_TOO_LARGE <actual> > <limit>` | Payload above `MaxPayloadBytes`, or catalog above `MaxCatalogBytes` | Permanent |
| `ERR HW_INVALID_COUNT <detail>` | `HW.RECEIVE` `COUNT` non-numeric, zero, negative, overflowing, or above `ReceiveMaxCount` | Permanent |
| `NOAUTH <detail>` | The server requires authentication and none was presented | **Permanent** — configuration, not code |
| `WRONGPASS <detail>` | The credentials were rejected | **Permanent** |
| `NOPERM <detail>` | Authenticated, but not permitted to run the command | **Permanent** |
| `ERR HW_STORAGE_FORMAT <detail>` | A queue holds entries written by a pre-013 Highway. The message names the key | Permanent — drain the queue or delete the data directory |
| `ERR HW_INTERNAL <detail>` | An unexpected exception escaped a command handler | Permanent — a server bug |
| `ERR Transaction failed.` | Garnet aborted the transaction; no work was performed | **Transient — retry** |
| `ERR wrong number of arguments...` | Garnet's arity check, before the command runs | Permanent |

**The authentication errors carry neither marker.** They are not `ERR HW_`-prefixed and are not the bare transient abort, so a client following the two-class rule literally has nowhere to put them. They are permanent: retrying a wrong password wastes the backoff budget and trips attempt counters on systems that keep them. They are worth their own exception type because the remedy differs from every other permanent failure — it is a configuration problem, not a code or network one.

Note that StackExchange.Redis surfaces `NOAUTH`/`WRONGPASS` raised during connection setup inside a *connection* exception, so a client that does not inspect the message chain will report a wrong password as an unreachable host and send its operator to check the network.

`HW_INTERNAL` is permanent deliberately. Several commands rewrite a whole list (acknowledgement, the lease sweeps), so a mid-operation failure can leave partial state that retrying would compound rather than repair.

### Which commands can abort transiently

Only commands that read a key during `Prepare`. That is:

| Can abort transiently | Cannot |
|---|---|
| `HW.DEQUEUE` | `HW.CALL` |
| `HW.PUBLISH` | `HW.REPLY` |
| `HW.DISCOVER` | `HW.ACK` |
| `HW.STATS` | `HW.SUBSCRIBE` |
| `HW.HEARTBEAT` — registration and departure forms | `HW.UNSUBSCRIBE` |
| | `HW.RECEIVE` |
| | `HW.RACK` |
| | `HW.HEARTBEAT` — liveness form |

The right-hand column takes its locks without a prior read, so there is no watch to invalidate.

A client may of course retry the bare message from any command — the classification is about safety, and no command performs partial work on this abort. The distinction matters for diagnosis: a transient abort from a command in the right-hand column would indicate something other than a watch conflict.

### The `HW.PUBLISH` consequence

A transient abort on `HW.PUBLISH` means the message reached **no group**. It was not partially delivered and not queued — it does not exist. A publisher that treats the failure as fire-and-forget silently loses the message.

**A Highway client must retry a transient `HW.PUBLISH`.** This is the one place where not retrying costs data rather than latency.

### `+REGISTER` is not an error

The `HW.HEARTBEAT` liveness form can reply with the simple string `REGISTER`. This is a **normal reply**: the server holds no registration record for the node and needs its catalog. It must not be routed through error handling or classified by the rule above. See [`HW.HEARTBEAT`](#hwheartbeat).

---

## RPC Commands

An RPC request flows: `HW.CALL` enqueues it → a worker claims it with `HW.DEQUEUE` → the worker writes the answer with `HW.REPLY` → the caller reads the reply slot with stock `GET`/`DEL` → the worker releases the claim with `HW.ACK`.

### HW.CALL

```
HW.CALL <service> <requestId> <payload>   →   +OK
```

Appends the request to the service's queue and rings the service doorbell.

| | |
|---|---|
| **Arguments** | `service`, `requestId` — identifiers. `payload` — opaque, up to `MaxPayloadBytes`. |
| **Reply** | `+OK` |
| **Keys written** | `hw:svc:{service}:q` |
| **Doorbell** | `hw:door:svc:{service}`, payload = `requestId` |
| **Idempotency** | **Not idempotent.** Repeating enqueues a second request. The caller owns request-ID uniqueness. |

Enqueuing to a service with no workers succeeds — the request waits durably. There is no registration check here; an unknown service name is a valid queue name.

The `requestId` is the caller's correlation handle and determines its reply slot. It is stored and returned unmodified.

### HW.REPLY

```
HW.REPLY <requestId> <payload>   →   +OK
```

Writes the reply slot and rings the reply doorbell.

| | |
|---|---|
| **Arguments** | `requestId` — identifier. `payload` — opaque, up to `MaxPayloadBytes`. |
| **Reply** | `+OK` |
| **Keys written** | `hw:rep:{requestId}`, with TTL `ReplySlotTtl` (default 5 minutes) |
| **Doorbell** | `hw:door:rep`, payload = `requestId` |
| **Idempotency** | **Last-writer-wins.** A second reply overwrites the first and refreshes the TTL. Deterministic, never an error. |

The reply slot is a plain main-store string. The caller retrieves it with stock `GET` and removes it with `DEL` — see [Stock Garnet Dependencies](#stock-garnet-dependencies). There is no `HW.*` command for retrieval.

The TTL bounds leakage: a caller that times out and never collects its reply leaves a slot that expires on its own.

### HW.DEQUEUE

```
HW.DEQUEUE <service> <nodeId>   →   [requestId, payload]   |   nil array
```

Atomically moves the oldest pending request into the calling node's processing list and returns it.

| | |
|---|---|
| **Arguments** | `service`, `nodeId` — identifiers. |
| **Reply** | A two-element array of bulk strings, or a **nil array** when nothing is available. |
| **Keys** | Reads and writes `hw:svc:{service}:q`, `hw:svc:{service}:proc:{nodeId}`, `hw:svc:{service}:nodes`, `hw:svc:{service}:nodelist`; may touch other nodes' processing lists and registry keys during its sweeps. |
| **Idempotency** | Not idempotent — each call claims a distinct request. |

**The empty reply is a nil array (`*-1\r\n`), not a nil bulk string.** StackExchange.Redis surfaces it as `RedisResult.IsNull`. This is easy to mis-parse; a client that expects `$-1` will break.

FIFO order is preserved, and exclusive key locks make concurrent dequeues on one service serialize, so competing consumers each claim a distinct request. A claimed request stays in the node's processing list until acknowledged.

**Two sweeps run before the pop.**

*Lease expiry* — an entry claimed longer ago than `Lease` (default 5 minutes) is returned to the queue tail for redelivery. This recovers work from a worker that is alive but stuck.

*Dead-node pruning* — a node whose registration has gone stale has its **entire** processing list returned to the queue, and is removed from the service's worker set, the service's discovery index, and the registry. This recovers work from a worker that has died, and stops the worker set growing without bound.

Only nodes that hold a registration record which is *stale* are pruned. A node with **no** registration record is not participating in the registry — a client may run with heartbeat disabled — and is left alone. Pruning on "no record" would requeue a healthy worker's in-flight work on every dequeue. Pruning never touches subscriber groups; see [Invariants](#invariants).

Set `PruningEnabled = false` to disable dead-node pruning; lease expiry still runs.

### HW.ACK

```
HW.ACK <service> <nodeId> <requestId>   →   +OK
```

Removes the request from the node's processing list.

| | |
|---|---|
| **Arguments** | All three are identifiers. |
| **Reply** | `+OK` |
| **Keys written** | `hw:svc:{service}:proc:{nodeId}` |
| **Idempotency** | **Idempotent.** An unknown or already-acknowledged request still returns `+OK`. |

After acknowledgement the request cannot be redelivered by any mechanism. Acknowledging after a lease requeue also succeeds and removes any residue.

`HW.REPLY` must be sent **before** `HW.ACK` — see [Invariants](#invariants).

---

## Pub/Sub Commands

Highway's pub/sub is durable and group-based, and is unrelated to Garnet's own `PUBLISH`/`SUBSCRIBE` (which Highway uses only for doorbells). A message is fanned out to every registered *group*; each group has its own independent copy.

### HW.PUBLISH

```
HW.PUBLISH <channel> <payload>              →   :groupCount
HW.PUBLISH <channel> <payload> AT <ticks>   →   :0            (delayed)
```

#### Delayed delivery

`AT` takes an **absolute** delivery time as a .NET UTC tick count, not a relative delay. The client computes `UtcNow + delay` and the server stores what it was told, so a slow round trip cannot silently extend the delay — and, more importantly, AOF replay cannot re-delay from replay time. A stored relative delay would fabricate a new future on every recovery.

A time already in the past delivers immediately rather than failing: clock skew between a client and the broker is normal, and refusing a publish over a few milliseconds of it would be worse than delivering slightly early relative to the client's clock.

A delayed publish replies `:0` — the reply counts groups the message was *delivered* to, and nothing has been delivered yet. No doorbell is rung, because the message is in nobody's queue.

**The guarantee is "not before", not an alarm clock.** A delayed message is held whole in `hw:ch:{channel}:delayed` and promoted into group queues by `HW.RECEIVE` — that is, **by consumer activity, not by a timer inside the broker**. Consequences a client implementer must know:

- The message arrives on the first `HW.RECEIVE` after its delivery time, so practical resolution is bounded by how often consumers poll.
- **A channel whose groups have no running consumer promotes nothing** until one starts. The message is not lost; it is not delivered either.
- Promotion is capped at 256 messages per `HW.RECEIVE`; a larger due batch drains over successive polls.

A background server timer would give tighter resolution and was rejected: it writes to the keyspace, so it needs its own transaction, its own failure handling and its own interaction with AOF replay, and it runs whether or not anyone is listening. Highway already recovers abandoned work lazily in exactly this shape.

**Groups are resolved at delivery time, not publish time.** The message is stored whole rather than fanned out, so a group registering during the delay receives it: a delayed publish behaves like a publish that happens later. This differs from an immediate publish, which fans out to the groups registered at that moment.

There is no way to cancel or list pending delayed messages.


Appends the message to every registered group's queue, atomically.

| | |
|---|---|
| **Arguments** | `channel` — identifier. `payload` — opaque, up to `MaxPayloadBytes`. `AT <ticks>` — optional absolute delivery time, .NET UTC ticks. |
| **Reply** | RESP integer: the number of groups the message reached. `0` means it went to the backlog, or that it is delayed and has reached none yet. |
| **Keys written** | `hw:ch:{channel}:seq`, then every `hw:ch:{channel}:grp:{group}:q`, or `hw:ch:{channel}:backlog`, or — when `AT` is in the future — `hw:ch:{channel}:delayed` |
| **Doorbell** | `hw:door:ch:{channel}:grp:{group}` per group, payload = `messageId`. **None for a delayed publish**, which is in nobody's queue yet. |
| **Idempotency** | **Not idempotent.** Repeating publishes a second message with a new ID. |

Fan-out is atomic: all groups receive the message or none do. There is no partial delivery.

Each message gets a channel-unique `messageId` from an incrementing counter. It is returned by `HW.RECEIVE` and used to acknowledge with `HW.RACK`.

**Zero groups → the backlog.** Publishing to a channel with no registered groups returns `0` and appends the message to a per-channel backlog, held for late subscribers. Backlog entries expire after `BacklogRetention` (default 1 day) and are capped at `MaxBacklogEntries` (default 10,000), oldest dropped first. Once at least one group exists, publishes go to group queues and the backlog is not used.

**On transient abort the message was not delivered at all.** See [Error Contract](#error-contract).

**Retry backoff is off by default, and shares the mechanism but not the scope.** A message returned to a group after a lease expiry goes into `hw:ch:{channel}:grp:{group}:retry` — a *per-group* sorted set — and is promoted back to that group's queue alone. The channel-wide delayed set is promoted to every registered group, which is correct for a delayed publish and would turn one group's retry into every other group's duplicate.

**A delayed publish uses none of the backlog path.** It is held whole in the delayed set regardless of how many groups exist, because groups are resolved when it is promoted, not when it is published.

### HW.SUBSCRIBE

```
HW.SUBSCRIBE <channel> <group>   →   +OK
```

Registers a subscriber group and, for a genuinely new group, copies the channel backlog into its queue.

| | |
|---|---|
| **Arguments** | Both identifiers. |
| **Reply** | `+OK` |
| **Keys written** | `hw:ch:{channel}:groups`, `hw:ch:{channel}:grplist`, `hw:ch:{channel}:grp:{group}:q` |
| **Idempotency** | **Idempotent.** Re-subscribing an existing group returns `+OK`, adds nothing, and does not re-copy the backlog. |

The backlog is **copied, not drained**: a second group registering within the retention window receives the same messages, so late subscribers do not compete for them.

The backlog copy happens **only when the group was not already registered**. Re-subscribing an existing group copies nothing — without that check, a node that re-subscribes on every start would receive the whole backlog again on every restart.

Group registration is durable and survives a restart when AOF is enabled.

### HW.UNSUBSCRIBE

```
HW.UNSUBSCRIBE <channel> <group>   →   +OK
```

Removes the group and deletes its state.

| | |
|---|---|
| **Arguments** | Both identifiers. |
| **Reply** | `+OK` |
| **Keys** | Removes from `hw:ch:{channel}:groups` and `hw:ch:{channel}:grplist`; **deletes** `hw:ch:{channel}:grp:{group}:q` and `:proc` |
| **Idempotency** | **Idempotent.** Unsubscribing an unknown group returns `+OK`. |

**This deletes undelivered messages.** Everything queued for the group is discarded. That is why the .NET client never sends this command: a node's group is meant to outlive its process so a restart resumes pending messages. Send it only when a group is genuinely finished forever.

A group removed this way is new again if it re-subscribes, so it receives the backlog once more.

### HW.RECEIVE

```
HW.RECEIVE <channel> <group>                →   [[messageId, payload], ...]
HW.RECEIVE <channel> <group> COUNT <n>      →   [[messageId, payload], ...]
HW.RECEIVE <channel> <group> <n>            →   [[messageId, payload], ...]
```

Moves up to `COUNT` messages from the group's queue into its processing list and returns them.

| | |
|---|---|
| **Arguments** | `channel`, `group` — identifiers. `COUNT` optional, either `COUNT n` or a bare `n`. |
| **Reply** | An array of two-element arrays. Each inner array is `[messageId, payload]`, both bulk strings; `messageId` is the decimal integer as text. Empty array when nothing is available — never an error. |
| **Keys written** | `hw:ch:{channel}:grp:{group}:q`, `hw:ch:{channel}:grp:{group}:proc` |
| **Idempotency** | Not idempotent — each call claims distinct messages. |

`COUNT` defaults to `ReceiveDefaultCount` (10) and must be between 1 and `ReceiveMaxCount` (500). Outside that range, non-numeric, or overflowing → `HW_INVALID_COUNT`.

**The reply is nested**: an outer array of pairs, not a flat list. A client that flattens it will mis-associate IDs and payloads.

Received messages move to in-flight state and are not returned again — until their lease expires. A lease sweep runs first: a message received longer ago than `Lease` and never acknowledged is returned to the **head** of the group queue, so redelivery preserves ordering against newer messages.

### HW.RACK

```
HW.RACK <channel> <group> <messageId>   →   +OK
```

Removes the message from the group's processing list.

| | |
|---|---|
| **Arguments** | `channel`, `group` — identifiers. `messageId` — decimal integer as text; non-numeric → `HW_INVALID_ARG`. |
| **Reply** | `+OK` |
| **Keys written** | `hw:ch:{channel}:grp:{group}:proc` |
| **Idempotency** | **Idempotent.** An unknown or already-acknowledged ID returns `+OK`. |

Acknowledging in one group never affects another group's copy — the keys are per-group.

`HW.RACK` must be sent only **after** dispatch completes — see [Invariants](#invariants).

---

## Registry Commands

The registry records which nodes are alive and what each hosts. It powers fast-fail and operator visibility. It does **not** participate in routing: work distribution is competing consumers on `HW.DEQUEUE`, unaffected by what the registry says.

### HW.HEARTBEAT

One command, three forms, selected by the second argument:

```
HW.HEARTBEAT <nodeId> <catalogJson>   →   +OK                  (registration)
HW.HEARTBEAT <nodeId>                 →   +OK | +REGISTER      (liveness)
HW.HEARTBEAT <nodeId> BYE             →   +OK                  (departure)
```

**Form selection.** Second argument absent → liveness. Exactly `BYE` → departure. Anything else → registration, and it must parse as catalog JSON. A second argument that is neither `BYE` nor valid JSON is rejected with `HW_INVALID_ARG`. The forms are unambiguous because a catalog is JSON and begins with `{`.

**Why three forms rather than one.** A node's catalog is static for its lifetime. Sending it on every beat would put up to `MaxCatalogBytes` on the wire per node per interval and force a server-side parse to rebuild an index that never changes. Splitting the forms makes a steady-state beat a few bytes regardless of how many services a node hosts.

#### Registration form

```
HW.HEARTBEAT <nodeId> <catalogJson>   →   +OK
```

Stores the catalog, rebuilds the node's entries in the discovery index, and refreshes liveness. Sent **once** per node lifetime, and again only when asked by `+REGISTER`.

| | |
|---|---|
| **Arguments** | `nodeId` — identifier. `catalogJson` — up to `MaxCatalogBytes` (default 256 KiB). |
| **Reply** | `+OK` |
| **Keys written** | `hw:reg:node:{nodeId}`, `hw:reg:nodes`, `hw:reg:svc:{service}` per service |
| **Idempotency** | **Idempotent.** Re-registering an unchanged catalog does not duplicate or grow state. |

The catalog is stored **verbatim**. The server parses it only to derive service names for the index, and rejects an unparseable catalog with `HW_INVALID_ARG` — a catalog the server cannot read would leave the node permanently undiscoverable, so failing loudly is better than indexing nothing.

The expected shape, from which only `services[].name` is read:

```json
{
  "services": [ { "name": "orders.create", "requestType": "...", "responseType": "..." } ],
  "channels": [ { "name": "orders.placed", "subscriberCount": 1 } ]
}
```

An empty catalog (`{"services":[],"channels":[]}`) is valid and registers a node that hosts nothing — a pure caller.

Re-registering a **changed** catalog removes index entries for services no longer hosted and adds the new ones, so a node redeployed under the same name leaves nothing stale.

#### Liveness form

```
HW.HEARTBEAT <nodeId>   →   +OK   |   +REGISTER
```

Refreshes the timestamp and nothing else. This is the steady-state beat.

| | |
|---|---|
| **Arguments** | `nodeId` — identifier. |
| **Reply** | `+OK` when refreshed. `+REGISTER` when the server holds no record for this node. |
| **Keys** | Reads and writes only `hw:reg:node:{nodeId}` |
| **Idempotency** | Idempotent. |

No catalog parse, no index write, and the stored catalog is preserved byte-for-byte. Cost is one small read and one small write, independent of catalog size.

**`+REGISTER` is correctness, not politeness.** Pruning deletes a node's registration record *and* its index entries. A beat that simply recreated the timestamp would leave the node alive but **undiscoverable** — serving a queue nobody is told about, with nothing to surface the fault. Replying `+REGISTER` when the record is absent makes a wiped registry self-healing.

A client receiving `+REGISTER` must send the registration form promptly — before its next scheduled beat, so the undiscoverable window is one round trip rather than a full interval. This recovers from a memory-only server restart, pruning after a long pause, and registry loss to operator action.

`+REGISTER` is a normal reply and must not be treated as an error.

#### Departure form

```
HW.HEARTBEAT <nodeId> BYE   →   +OK
```

Announces that the node is shutting down, so it leaves discovery immediately rather than after the expiry window.

| | |
|---|---|
| **Arguments** | `nodeId` — identifier. `BYE` — the reserved literal. |
| **Reply** | `+OK` |
| **Keys** | Registration, index, and per-service worker-set entries removed; unacknowledged RPC work requeued |
| **Idempotency** | **Idempotent.** Departing an unknown node returns `+OK`. |

This runs the **same teardown** as dead-node pruning: unacknowledged RPC requests return to their service queues, and the node is removed from every worker set, the discovery index, and the registry. **Subscriber groups are not touched** — see [Invariants](#invariants).

Departure is a courtesy, not a requirement. A node that is killed rather than stopped simply expires on the normal timeline.

### HW.DISCOVER

```
HW.DISCOVER <service>   →   [[nodeId, secondsSinceLastSeen], ...]
```

Returns the live nodes hosting a service.

| | |
|---|---|
| **Arguments** | `service` — identifier. |
| **Reply** | An array of two-element arrays: `nodeId` and the age in whole seconds of that node's last beat, both bulk strings. Empty array for an unknown service or one whose hosts are all stale — never an error. |
| **Keys** | Read-only. |
| **Idempotency** | Read-only, trivially idempotent. |

The age lets a caller reason about freshness rather than treating presence as binary.

Stale nodes are filtered from results **even before they are pruned**, so a node that stops beating disappears from discovery promptly while its state is reclaimed later by `HW.DEQUEUE`. This command performs no mutation: pruning must requeue the node's RPC work, and that belongs where the queue keys are already locked.

Discovery is a lookup, not a scan — the registration form maintains the index at write time, so no catalog is deserialized here.

### HW.STATS

```
HW.STATS                →   kind server  nodes N services N channels N
HW.STATS <service>      →   kind service queueDepth N hosts N inFlight N
HW.STATS <channel>      →   kind channel groups N pending N backlog N
```

Operational counters. The reply is a **flat array of alternating field names and values**, all bulk strings — a shape that stays readable in `redis-cli`, parses without a schema, and extends by appending fields rather than changing shape.

| | |
|---|---|
| **Arguments** | Optional name — identifier when present. |
| **Reply** | Flat field/value array, always beginning with `kind`. |
| **Keys** | Read-only. |
| **Idempotency** | Read-only; safe to poll on an interval. |

**Form selection.** No argument → the server form. With a name, the name is a *service* if the discovery index knows it, otherwise it is reported as a *channel*. A name that is both resolves as a **service**; the `kind` field makes the resolution explicit rather than ambiguous. An unknown name returns zeroed counters with `kind channel` — never an error, because an operator querying a name that has seen no traffic deserves an answer.

| Form | Fields |
|---|---|
| server | `nodes` (live registrations), `services` and `channels` (distinct names across live catalogs) |
| service | `queueDepth` (pending requests), `hosts` (live nodes hosting it), `inFlight` (claimed but unacknowledged, across all nodes) |
| channel | `groups` (registered subscriber groups), `pending` (undelivered across all groups), `backlog` (entries held for late subscribers) |
| recorder | `enabled`, `names`, `events`, `bytes`, `droppedCapacity`, `droppedBudget`, `failures` |

**The `RECORDER` form.** `HW.STATS RECORDER` reports flight-recorder health
(feature 002). `RECORDER` is a **reserved name**, matched case-insensitively, and
takes priority over a service or channel that happens to share it — the same kind
of explicit resolution rule as service-beats-channel above.

```
HW.STATS RECORDER
  -> kind recorder  enabled 1  names 12  events 84213  bytes 41224192
     droppedCapacity 1902  droppedBudget 0  failures 0
```

Drop counters are cumulative since server start, so an operator can tell whether
history is being lost rather than only how much is held. `droppedCapacity` counts
events pushed out of a name's own buffer; `droppedBudget` counts reclamation
forced by the server-wide memory budget. `failures` counts recording attempts
that threw and were swallowed — non-zero means a bug worth reporting, never a
lost operation. The form answers when the recorder is disabled, reporting
`enabled 0` rather than an error.

**No snapshot consistency.** Counters are read under the command's locks but describe independently-mutating structures. The reply is a set of point-in-time readings, not a coherent instant. Fine for monitoring; do not build invariants on cross-field arithmetic.

---

## Observability Commands

Highway records what it does. Two independent mechanisms, either disableable:

- the **flight recorder** — a bounded, in-process record of recent operations, read with `HW.REPLAY`;
- **activity emission** — `System.Diagnostics.Activity`, which any OpenTelemetry pipeline collects.

### The flight recorder is volatile

**Its contents are lost when the server stops.** It holds events in ordinary process memory, not in the Garnet keyspace, and nothing about it enters the AOF.

That is deliberate. Storing events in the keyspace would put them in the AOF, where recovery would replay them with replay-time timestamps and fabricate history on every restart — and it would make a debugging aid compete with the actual queues for the same store. **Anyone needing a durable audit trail wants the activity/OpenTelemetry path**, exported continuously to a system built to retain it.

Recording is **best-effort**: a failure to record never fails, delays, or alters the operation being recorded.

### What is recorded

Recording happens per **name**, and each name has its own bounded buffer with its own retention and payload-capture mode. One high-volume name therefore cannot evict another's history.

A name is a service name, a channel name, a node ID, or one of the reserved names below. **A name must be drawn from a bounded set** — the recorder never removes a buffer once created, so recording under a per-request or per-message value would grow the recorder without limit for the life of the process. This is not hypothetical: `HW.REPLY` recorded under the request ID until it was corrected.

| Name | Source | Cardinality |
|---|---|---|
| service name | `HW.CALL`, `HW.DEQUEUE`, `HW.ACK` | number of services |
| channel name | `HW.PUBLISH`, `HW.SUBSCRIBE`, `HW.UNSUBSCRIBE`, `HW.RECEIVE`, `HW.RACK` | number of channels |
| node ID | `HW.HEARTBEAT` registration / `BYE` | number of nodes |
| `hw.replies` | `HW.REPLY` — **reserved** | one |

| Command | Event | Note |
|---|---|---|
| `HW.CALL` | `RpcEnqueued` | |
| `HW.DEQUEUE` | `RpcClaimed` | a nil dequeue records nothing |
| `HW.REPLY` | `RpcReplied` | recorded under the reserved name **`hw.replies`**, not under a service. `HW.REPLY`'s arguments are a request ID and a payload, so the service that produced the reply is not on the wire and the command cannot know it. Query `HW.REPLAY hw.replies` and correlate by `requestId` |
| `HW.ACK` | `RpcAcknowledged` | |
| `HW.PUBLISH` | `Published` | `count` carries the group count |
| `HW.SUBSCRIBE` / `HW.UNSUBSCRIBE` | `GroupRegistered` / `GroupRemoved` | |
| `HW.RECEIVE` | `MessagesReceived` | one event per **batch**; `count` carries the batch size |
| `HW.RACK` | `MessageAcknowledged` | |
| `HW.DEQUEUE` sweep | `RpcDeadLettered` | a request exhausted its attempts; `count` carries the attempt count, `errorCode` the reason |
| `HW.RECEIVE` sweep | `MessageDeadLettered` | as above, for one channel group |
| `HW.HEARTBEAT` registration / `BYE` | `NodeRegistered` / `NodeDeparted` | |
| `HW.HEARTBEAT` liveness | **nothing** | fires every few seconds per node; recording it would evict real history to store the fact that nothing happened |
| `HW.DISCOVER`, `HW.STATS`, `HW.REPLAY` | **nothing** | read-only; recording reads would drown the record, and querying it would record the query |

**Failed commands are recorded**, carrying the error code that rejected them. A flight recorder that showed only successes would omit the thing it exists for.

### Payload capture

Per name, one of three modes:

| Mode | Retains |
|---|---|
| `Full` (default) | The complete payload bytes |
| `HeadersOnly` | Metadata and the payload **size**, but no content |
| `Off` | Nothing — no buffer is allocated for that name |

**`Full` is the default, and the consequence is worth stating plainly:** payload content sits in server memory and is readable by anyone who can issue `HW.REPLAY`, and **Highway has no authentication**. For names carrying personal or sensitive data use `HeadersOnly`, or disable replay entirely while keeping the recorder for metrics.

### HW.REPLAY

```
HW.REPLAY <name> [FROM <ts>] [TO <ts>] [LIMIT <n>] [NODE <nodeId>]
```

Returns one name's recorded operations in chronological order.

| | |
|---|---|
| **Arguments** | `name` — identifier. The rest are optional keyword arguments in any order. |
| **Reply** | An array of flat field/value arrays, one per event. Empty array when the name is unknown, disabled, or has nothing in range — never an error. |
| **Keys** | **None.** The recorder is not in the keyspace, so this is read-only with respect to Garnet and cannot contend with traffic. |
| **Idempotency** | Read-only. |

`FROM` and `TO` accept either an ISO-8601 timestamp or a **relative offset** — `-30s`, `-5min`, `-1h`, `-2d` — which is what an operator actually types during an incident. Units accepted: `s`/`sec`/`secs`, `m`/`min`/`mins`, `h`/`hr`/`hrs`, `d`/`day`/`days`. Omitting them defaults to a recent window (`ReplayDefaultWindow`, default 5 minutes) ending now.

`LIMIT` defaults to `ReplayDefaultLimit` (100) and may not exceed `ReplayMaxLimit` (1,000); violations return `HW_INVALID_COUNT`. `NODE` restricts results to events involving one node.

Each event is a flat field/value array — the same self-describing shape `HW.STATS` uses, so fields can be appended later without breaking readers:

```
timestamp    2026-08-07T13:26:59.1234567+00:00
eventType    RpcEnqueued
name         orders.create
nodeId       (empty unless the command carried one)
requestId    a3f1...  (opaque string, not necessarily a GUID)
messageId    (empty for RPC events; a channel sequence number for pub/sub)
payloadSize  512
errorCode    (empty on success; e.g. HW_INVALID_ARG when rejected)
statusCode   (empty unless the operation produced a client-facing status)
count        (group count for a publish, batch size for a receive)
payload      (empty unless the name is captured at Full)
```

`payloadSize` is always present, even when `payload` is empty — so throughput and message shape stay visible under `HeadersOnly`.

Invalid arguments are rejected with the codes in [Error Contract](#error-contract): `HW_INVALID_COUNT` for `LIMIT`, `HW_INVALID_ARG` otherwise. When `ReplayEnabled` is false the command returns `HW_INVALID_ARG` explaining that replay is disabled — the recorder keeps running and `HW.STATS RECORDER` keeps answering.

### Activity emission

Highway emits `System.Diagnostics.Activity` spans and takes **no OpenTelemetry dependency**. Applications that want OTLP add the OpenTelemetry packages themselves and subscribe to these sources:

| Source | Emits |
|---|---|
| `Highway.Client` | caller-side spans around `ExecuteAsync` and `PublishAsync` |
| `Highway.Server` | server-side spans around command execution |

That is how `HttpClient` and ASP.NET Core do it. It keeps the client light, and leaves sampling, exporters and resource attributes under the application's control rather than Highway's.

Trace context travels in the envelope's optional `tp` field (see [Transport & Framing](#transport--framing)). A server-side span parses it and joins the caller's trace, so one distributed trace spans both processes.

Attributes follow OpenTelemetry messaging semantic conventions:

| Attribute | Value |
|---|---|
| `messaging.system` | `highway` |
| `messaging.operation` | `publish`, `receive`, or `process` |
| `messaging.destination.name` | service or channel name |
| `messaging.message.id` | request or message ID |
| `messaging.client.id` | node name |

**Payload content is never placed on a span.** Spans leave the process for third-party systems; message bodies must not ride along by default.

With no listener attached, `StartActivity` returns null and nothing is materialised, so emission costs essentially nothing when unobserved.

---

## Dead Letter Commands

### HW.DLQ

```
HW.DLQ PEEK    SVC <service>          [COUNT n]   →   array of dead letters (non-destructive)
HW.DLQ PEEK    Q   <queue>            [COUNT n]
HW.DLQ PEEK    CH  <channel> <group>  [COUNT n]
HW.DLQ REQUEUE SVC <service>          [COUNT n]   →   :n   (moved back to the live queue)
HW.DLQ REQUEUE CH  <channel> <group>  [COUNT n]
HW.DLQ PURGE   SVC <service>          [COUNT n]   →   :n   (removed)
HW.DLQ PURGE   CH  <channel> <group>  [COUNT n]
```

`COUNT` defaults to `ReceiveDefaultCount` and is capped at `ReceiveMaxCount`.

**How entries get here.** Nothing writes to a dead-letter list directly. An entry arrives when `HW.DEQUEUE`'s lease sweep or `HW.RECEIVE`'s group sweep finds it has exceeded `MaxDeliveryAttempts`, and moves it out of the live queue in the same transaction that removes it from the processing list.

**PEEK is non-destructive** and is listed first deliberately: the supported workflow is look, then decide. An operator who can only drain has to destroy the evidence in order to see it. Each entry is a flat field/value array — the same self-describing shape `HW.STATS` and `HW.REPLAY` use, so fields can be appended later without breaking readers:

```
deadLetteredAt  <ISO-8601 UTC>
attempts        <n>
reason          MAX_ATTEMPTS
requestId       <id>            (SVC targets)
messageId       <id>            (CH targets)
payload         <bytes>
```

**REQUEUE resets the attempt count to zero.** An operator requeues *after fixing something*; a message that immediately re-dead-letters has wasted the round trip. Requeue is always operator-initiated — Highway never re-feeds a dead-letter list automatically, because a queue that retries its own failures without limit is the defect this feature removes.

**An unknown service, channel or group returns an empty array or `:0`, never an error**, matching `HW.DISCOVER` and `HW.STATS`.

**Recorded events:** none. `HW.DLQ` is an operator command, and recording reads would drown the record — the same reasoning that keeps `HW.STATS` and `HW.REPLAY` out of the recorder. The *dead-lettering* itself is recorded, by the sweep that performed it.

---

## Queue Commands

A **queue** is a named, durable, competing-consumer work list: exactly one worker processes each message, and the sender does not wait for a result. Mechanically it is RPC minus the reply — the same queue, lease, attempt counting and dead-lettering, with no reply slot.

**Queues have their own key space** (`hw:q:`), so a queue and a service may share a name without colliding. A queue never appears in `HW.DISCOVER` and carries no response type.

### HW.QSEND

```
HW.QSEND <queue> <messageId> <payload>              →   +OK
HW.QSEND <queue> <messageId> <payload> AT <ticks>   →   +OK   (deferred)
```

| | |
|---|---|
| **Arguments** | `queue`, `messageId` — identifiers. `payload` — opaque, up to `MaxPayloadBytes`. `AT <ticks>` — optional absolute .NET UTC delivery time. |
| **Reply** | `+OK` |
| **Keys written** | `hw:q:{queue}:q`, or `hw:q:{queue}:delayed` when `AT` is in the future |
| **Doorbell** | `hw:door:q:{queue}`, payload = `messageId`. **None for a deferred send**, which is in no worker's reach yet. |
| **Idempotency** | Not idempotent. Repeating enqueues a second message. |

**Sending never requires a running worker.** The message waits until one claims it. That is the whole point of a queue, and the capability whose absence leads people to misuse `HW.PUBLISH`.

`AT` is absolute rather than a relative delay so AOF replay cannot re-delay from replay time — the same reasoning as `HW.PUBLISH`. A time in the past delivers immediately.

### HW.QCLAIM

```
HW.QCLAIM <queue> <nodeId>   →   [messageId, payload]   |   *-1 (nil, queue empty)
```

| | |
|---|---|
| **Reply** | Two-element array, or a nil array when there is nothing to claim |
| **Keys written** | `hw:q:{queue}:q`, `:proc:{nodeId}`, `:delayed`, `:dlq`, `:nodes`, `:nodelist` |
| **Idempotency** | Not idempotent — each call claims a different message. |

Before serving, it **promotes** deferred messages whose time has passed, then **sweeps expired leases** across every known worker: an entry past its lease returns to the queue with its attempt count incremented, or is dead-lettered once it exceeds `MaxDeliveryAttempts`. This is the same shared implementation `HW.DEQUEUE` uses, not a second copy.

Unlike `HW.DEQUEUE` there is no dead-node prune — a queue has no service registry, so an abandoned claim is recovered by the lease sweep alone.

**Competing consumers by default.** Every worker calling this shares the work; there is no group name and no coupling to node identity.

### HW.QACK

```
HW.QACK <queue> <nodeId> <messageId>   →   :1 removed   |   :0 not found
```

Until this arrives the message remains in the worker's processing list and will be redelivered once its lease expires — that is what makes delivery at least once. Acknowledging an unknown message returns `:0` rather than an error: a worker retrying an acknowledgement is doing the right thing.

---

## Stock Garnet Dependencies

A client built only from the `HW.*` commands cannot function. These stock commands are required.

| Command | Purpose |
|---|---|
| `GET hw:rep:{requestId}` | **Retrieve an RPC reply.** There is no `HW.*` command for this — `HW.REPLY` writes a plain main-store string and the caller reads it directly. |
| `DEL hw:rep:{requestId}` | Remove the reply slot after collecting it. Optional but recommended; the `ReplySlotTtl` bounds leakage otherwise. |
| `SUBSCRIBE hw:door:...` | Observe doorbells. Optional — doorbells are a latency optimization and correctness never depends on them. |
| `SET hw:idem:... NX EX` / `GET` / `DEL` | **Deduplication.** Optional — required only for a client that implements `[Idempotent]`-style at-most-once handler invocation. See below. |
| `PING` / `ECHO` | Connection health, as with any RESP server. |
| `AUTH <password>` or `AUTH <user> <password>` | **Required against a secured server.** Highway's own client sends the password alone; the username defaults to Garnet's `default`. |

**The reply doorbell is node-global.** Every client subscribed to `hw:door:rep` receives a notification for **every** reply on the server, not just its own. A client must ignore request IDs it did not issue, and in particular must never `DEL` a slot it does not own — doing so destroys another caller's reply and hangs that call until its timeout. This is a real defect that occurred during development, not a hypothetical.

### Authentication and transport security

Highway does not define an authentication protocol — it configures Garnet's. A client
therefore authenticates exactly as it would against any RESP server, with `AUTH`.

**Password authentication.** A server secured with `WithPassword` has exactly one user,
Garnet's `default`. A client may send the password alone or pair it with that username;
anything else is refused. There is no configuration file and no user directory.

**When authentication is required.** Highway refuses to build a server bound to a
**non-loopback** address with no authentication, unless the operator explicitly opts out.
Loopback is exempt: running open is the right configuration for development, and a
loopback-bound broker is reachable only by processes already on the machine. This means
`new HighwayServerBuilder().Build()` starts an unsecured broker, deliberately.

**TLS is never required.** Not on loopback, not off it, not alongside a password. Highway
can demand a password; it cannot invent a certificate, so a TLS-by-default server would be
one that cannot start. **The password crosses the wire in clear text without it** — RESP
`AUTH` sends it as an ordinary bulk string — so TLS is strongly recommended wherever a
password crosses a network.

**A trap worth knowing if you configure Garnet's ACL directly.** Highway's commands fall
under Garnet's `@dangerous` ACL category, not `@admin`. A rule set of `+@all -@dangerous` —
a common hardening idiom — connects successfully and then refuses **every** `HW.*` command
with `NOPERM`. That reads like a Highway bug and is not. Grant `+@custom`, or name the
commands individually.

---

### Deduplication is a client protocol, not a server one

Highway's delivery is at-least-once by design: a consumer that runs a handler and dies before its acknowledgement lands will see the same request again. Nothing on the server can prevent that — the server learns a request is complete when `HW.ACK` arrives, and the duplicate exists *precisely because that acknowledgement never arrived*. Only the consumer knows the handler ran.

A client that wants at-most-once handler invocation therefore implements it itself, against a server key:

1. `SET hw:idem:{service}:{requestId} <in-progress> EX <window> NX` — atomic, so two concurrent redeliveries cannot both claim.
2. If it claimed: run the handler, then `SET` the same key to the response envelope with the same TTL, then reply and acknowledge.
3. If it did not claim and the value is a response: reply with **those exact bytes** and acknowledge. The caller must not be able to tell.
4. If it did not claim and the value is the in-progress sentinel: **do nothing** — do not run, do not reply, do not acknowledge. Another attempt holds it, or held it when its process died. Treating a stale sentinel as "probably crashed, run it again" defeats the entire mechanism.

Step 4 is why the window is not merely "how long duplicates are remembered": it is also how long a crashed in-flight request stays blocked. That is the correct trade for a contract that has declared running twice worse than running late.

**This deduplicates redeliveries and nothing else.** A caller issuing the same logical request twice produces two request IDs, and no server-side or client-side mechanism here relates them.

Highway requires **no** cluster commands, **no** scripting (`EVAL`), and **no** stream commands (`X*`) — Garnet does not implement streams, which is why Highway's queues are built on lists and doorbells.

---

## Key Schema

Every key Highway creates lives under the `hw:` namespace and cannot collide with application data.

### RPC

| Key | Store | Type | Purpose |
|---|---|---|---|
| `hw:svc:{service}:q` | Object | List | Pending requests, FIFO |
| `hw:svc:{service}:proc:{nodeId}` | Object | List | Requests claimed by one node, not yet acknowledged |
| `hw:svc:{service}:nodes` | Object | Set | Nodes that have claimed work for this service |
| `hw:svc:{service}:nodelist` | Main | String | Newline-delimited mirror of the nodes set |
| `hw:svc:{service}:dlq` | Object | List | Dead letters: requests that exhausted `MaxDeliveryAttempts`. Capped at `MaxDeadLetterEntries` |
| `hw:q:{queue}:q` | Object | List | Queued work, FIFO |
| `hw:q:{queue}:proc:{nodeId}` | Object | List | Claimed by one worker, not yet acknowledged |
| `hw:q:{queue}:nodes` | Object | Set | Workers that have claimed from this queue |
| `hw:q:{queue}:nodelist` | Main | String | Newline-delimited mirror of the workers set |
| `hw:q:{queue}:dlq` | Object | List | Dead letters. Capped at `MaxDeadLetterEntries` |
| `hw:q:{queue}:delayed` | Object | Sorted Set | Work awaiting a future delivery time. Score = delivery time in ticks |
| `hw:rep:{requestId}` | Main | String | Reply slot. **TTL `ReplySlotTtl`** (default 5 min) |
| `hw:idem:{service}:{requestId}` | Main | String | Deduplication marker for an `[Idempotent]` contract: an in-progress sentinel, then the response envelope. **TTL = the contract's window** (default 5 min). Written by *clients*, not by any `HW.*` command |

### Pub/Sub

| Key | Store | Type | Purpose |
|---|---|---|---|
| `hw:ch:{channel}:groups` | Object | Set | Registered subscriber groups |
| `hw:ch:{channel}:grplist` | Main | String | Newline-delimited mirror of the groups set |
| `hw:ch:{channel}:seq` | Main | Integer | Message-ID counter |
| `hw:ch:{channel}:backlog` | Object | List | Messages published with zero groups |
| `hw:ch:{channel}:delayed` | Object | Sorted Set | Messages awaiting a future delivery time. Score = delivery time in .NET UTC ticks, member = the channel entry |
| `hw:ch:{channel}:grp:{group}:q` | Object | List | Undelivered messages for one group |
| `hw:ch:{channel}:grp:{group}:proc` | Object | List | Received, not yet acknowledged |
| `hw:ch:{channel}:grp:{group}:dlq` | Object | List | Dead letters for one group. Capped at `MaxDeadLetterEntries` |
| `hw:ch:{channel}:grp:{group}:retry` | Object | Sorted Set | Messages held for this group's retry backoff. Score = the time they become claimable again |

### Registry

| Key | Store | Type | Purpose |
|---|---|---|---|
| `hw:reg:node:{nodeId}` | Main | String | Registration record: last-seen timestamp + catalog |
| `hw:reg:nodes` | Main | String | Newline-delimited list of registered node IDs |
| `hw:reg:svc:{service}` | Main | String | Newline-delimited node IDs hosting the service |

### Mirror keys, and why they exist

Three keys duplicate an object-store set as a newline-delimited main-store string: `:nodelist`, `:grplist`, and the registry's list keys.

This is **mandatory, not stylistic**. Reading an object-store set during a command's `Prepare` phase goes through Garnet's watch API, which registers a watch on that key; the exclusive lock the command then takes on the same key fails watch-version validation and aborts the transaction. Reading a main-store string with `GET` avoids the conflict.

The consequence is the newline delimiter, and that is why identifiers may not contain control characters. The delimiter is load-bearing — see [Identifiers](#identifiers).

Mirrors are updated in the same transaction as the set they mirror, so the two cannot diverge.

---

## Entry Framing

All multi-byte integers are **big-endian** (network byte order). All lengths are in bytes.

| Entry | Layout |
|---|---|
| RPC queue entry | `[u8 0xFF][u16 attempts][u16 requestIdLen][requestId][payload]` |
| RPC processing entry | `[u8 0xFF][i64 claimTicksUtc][u16 attempts][u16 requestIdLen][requestId][payload]` |
| Channel entry | `[u8 0xFF][u16 attempts][i64 messageId][payload]` |
| Group processing entry | `[u8 0xFF][i64 receiveTicksUtc][u16 attempts][i64 messageId][payload]` |
| Dead-letter entry | `[i64 deadLetteredTicksUtc][u16 attempts][u16 reasonLen][reason][original entry]` |
| Backlog entry | `[i64 publishTicksUtc][i64 messageId][payload]` |
| Registration record | `[i64 seenTicksUtc][catalog json bytes]` |

### The delivery attempt count

`attempts` bounds redelivery. It is incremented when an entry is **requeued after a lease expiry**, not when it is first enqueued — a message delivered once and acknowledged has one attempt, not two. An entry that would exceed `MaxDeliveryAttempts` is moved to a dead-letter list instead of being requeued.

The count lives *in the entry* rather than in a side key so that incrementing it is atomic with the move that caused it. A count kept beside the entry would be lost by exactly the crash it exists to survive. It saturates at `u16` maximum rather than wrapping, because a wrapped counter silently restores unbounded retry.

Three paths increment it: `HW.DEQUEUE`'s lease sweep, `HW.RECEIVE`'s group lease sweep, and the dead-node prune. A path that requeued without counting would let an entry escape the limit indefinitely.

### The version byte, and a breaking change

**Entries written before this was introduced cannot be read.** Adding `attempts` changed how entries parse, and an old entry read as a current one does not fail on its own: it reinterprets its leading bytes, reads a wrong length, and hands a **corrupt payload** to an application. That is worse than an error, so versioned entries begin with `0xFF` and a mismatch is refused with `HW_STORAGE_FORMAT`.

`0xFF` is unambiguous against every pre-existing leading byte: the high half of a `u16` identifier length (bounded by `MaxIdentifierBytes`), the high byte of a message-ID counter starting at 1, and the high byte of a .NET tick count. Raising `MaxIdentifierBytes` to 65,280 or above would break that property, and is rejected.

**Upgrade path:** drain the affected queues with the previous version, or delete the data directory. Backlog entries are deliberately unversioned and unchanged — a backlog entry has never been delivered, so it carries no attempt count and gains one (at zero) when promoted into a group queue. Existing backlog data therefore survives an upgrade.

Timestamps are .NET UTC tick counts (100-nanosecond intervals since 0001-01-01).

The processing variants are the queue entry with a timestamp inserted after the version byte — that timestamp is what the lease sweeps compare against.

The registration record is framed in binary rather than JSON so the liveness form can rewrite the timestamp while leaving the catalog byte-for-byte untouched: with a fixed 8-byte header that is a copy of the tail, whereas a JSON envelope would mean parsing and re-emitting the catalog on every beat.

---

## Doorbell Channels

Doorbells are Garnet RESP pub/sub messages that wake waiting clients so they need not poll. They are **best-effort by contract**: a client that never receives one must still make progress by polling. Correctness never depends on delivery.

| Channel | Rung by | Payload |
|---|---|---|
| `hw:door:svc:{service}` | `HW.CALL` | `requestId` |
| `hw:door:rep` | `HW.REPLY` | `requestId` |
| `hw:door:ch:{channel}:grp:{group}` | `HW.PUBLISH`, once per group | `messageId` |

Doorbells are rung **after** the transaction commits, and are **not** rung during AOF replay — a recovering server does not wake workers for requests that were already handled before the restart. A rejected command rings nothing.

`hw:door:rep` is a single node-global channel rather than one per request, keeping subscriptions O(1) per node instead of O(pending calls). The cost is that every client sees every reply notification and must filter — see [Stock Garnet Dependencies](#stock-garnet-dependencies).

---

## Invariants

Guarantees that span commands. Each names the test that enforces it, so a reader can see it is guaranteed rather than merely intended.

### RPC: reply strictly before acknowledgement

A worker must send `HW.REPLY` **before** `HW.ACK`.

If it crashes between the two, the reply is already delivered and the lease eventually returns the request for redelivery — the caller gets an answer either way. Reversed, a crash between acknowledgement and reply loses the request entirely: it is no longer in any processing list, so nothing will redeliver it, and the caller waits out its timeout for a reply that will never come.

*Enforced by* `RpcWorkerLoopTests.Process_SendsReplyBeforeAck`.

### Pub/Sub: acknowledge only after dispatch completes

A consumer must send `HW.RACK` only **after** the message has been fully dispatched to its handlers.

Acknowledging first turns a crash mid-dispatch into silent loss. Acknowledging last turns it into redelivery, which at-least-once permits.

*Enforced by* `ChannelConsumerLoopTests.Dispatch_RacksOnlyAfterSubscribersRun`.

### Pruning requeues RPC work but never deletes subscriber groups

Removing a node — by expiry or by `BYE` — requeues its unacknowledged RPC requests and removes it from worker sets, the discovery index, and the registry. It **never** deletes that node's subscriber groups or their pending messages.

The asymmetry follows from the delivery model. RPC work is *claimed*: if the claimant dies, the claim must be released or the work is stranded. Pub/sub messages are *addressed* to a group: a node being down is not a reason to discard mail addressed to it, and the group is expected to outlive the process so a restart resumes its backlog.

Deleting groups on prune would silently convert Highway's durable pub/sub into fire-and-forget for any node that outlives its expiry window. This is the invariant most at risk from a future change that looks like tidy housekeeping.

*Enforced by* `NodeExpiryTests.DeadNode_UnacknowledgedRequests_AreRequeuedNotLost` and `NodeExpiryTests.DeadNode_SubscriberGroupAndPendingMessages_Survive`.

### At-least-once for both RPC and pub/sub

Neither path is exactly-once. A request can be executed more than once (reply sent, acknowledgement lost, lease requeues it) and a message can be delivered more than once (dispatch completed, `HW.RACK` lost).

**Duplicate handling is the application's responsibility.** Handlers should be idempotent.

*Enforced by* `RpcFlowTests`, `LeaseRecoveryTests`, and `NodeExpiryTests`.

### Group identity is the node name, and groups outlive the process

A subscriber group is identified by the consuming node's name. Every group receives its own copy of every message, so N nodes subscribed to a channel produce N deliveries.

The client never sends `HW.UNSUBSCRIBE`. A node that stops leaves its group registered, so messages published while it is down accumulate and drain when it returns under the same name. This is what makes "a message published with no online subscriber is delivered when the subscriber eventually starts" true across process restarts.

The corollary: **two live processes sharing a node name share a group**, and will compete for messages rather than each receiving a copy. Node names must be unique per running instance.

*Enforced by* `PubSubIntegrationTests.Subscriber_StopsAndRestartsWithSameNodeName_DrainsMessagesPublishedWhileDown`.

### A liveness beat never resurrects a node into the index

If the server holds no registration record, the liveness form replies `+REGISTER` and writes nothing. It never recreates a record from a bare beat, because without the catalog it cannot rebuild the discovery index — and a node that is live but absent from the index is worse than one that is plainly gone.

*Enforced by* `RegistryTests.Liveness_ForUnregisteredNode_AsksForRegistration_AndMutatesNothing` and `RegistryTests.Liveness_AfterTheNodeIsGone_AsksForRegistrationAgain`.

---

## Server Options

Options that change observable protocol behaviour. All are server-side.

| Option | Default | Effect |
|---|---|---|
| `MaxPayloadBytes` | 1 MiB | Payload cap for `HW.CALL`, `HW.REPLY`, `HW.PUBLISH`. Exceeding → `HW_PAYLOAD_TOO_LARGE`. |
| `MaxIdentifierBytes` | 256 | Identifier length cap. Exceeding → `HW_INVALID_ARG`. |
| `MaxCatalogBytes` | 256 KiB | Catalog cap for the `HW.HEARTBEAT` registration form. |
| `Lease` | 5 minutes | How long a claimed RPC request or received message may go unacknowledged before redelivery. `TimeSpan.Zero` disables lease sweeps. |
| `ReplySlotTtl` | 5 minutes | TTL on `hw:rep:{requestId}`. Should comfortably exceed the client's call timeout. |
| `NodeExpiry` | 30 seconds | How long a registration stays valid without a beat. What matters is the **ratio** to the client's heartbeat interval; the defaults give 6×. Below about 3×, ordinary GC pauses cause false staleness. |
| `PruningEnabled` | `true` | When `false`, stale nodes are still excluded from discovery but their state is never reclaimed, and their unacknowledged work is recovered only by the slower lease sweep. |
| `BacklogRetention` | 1 day | How long backlog entries are offered to late subscribers. |
| `MaxBacklogEntries` | 10,000 | Backlog cap; oldest dropped first. |
| `ReceiveDefaultCount` | 10 | `HW.RECEIVE` `COUNT` when omitted. |
| `ReceiveMaxCount` | 500 | Maximum accepted `COUNT`. Above → `HW_INVALID_COUNT`. Also caps `HW.DLQ` `COUNT`. |
| `MaxDeliveryAttempts` | 5 | Deliveries before an entry is dead-lettered instead of requeued. `0` means unlimited, restoring the pre-013 behaviour in which a permanently failing message is redelivered forever. |
| `MaxDeadLetterEntries` | 10,000 | Per-list dead-letter cap; oldest dropped first. |
| `PubSubBackoffEnabled` | `false` | A pub/sub message returned after a lease expiry waits a growing delay before becoming claimable again. **Off by default because backoff and head-of-queue ordering are mutually exclusive**: holding a failed message serves the messages behind it first, and redelivery-preserves-order is a documented guarantee. Enable it where pacing matters more than order. |
| `RpcBackoffEnabled` | `false` | The RPC equivalent. Off because a caller waits against `CallTimeout` (30 s) while `Lease` defaults to 5 minutes — the caller has already given up before a retry is possible, so a delay changes nothing but when the dead-letter happens. Worth enabling only where `Lease` is tuned well below the call timeout. |
| `MaxBackoff` | 1 minute | Upper bound on the retry delay. The cap matters more than the curve: an uncapped exponential reaches hours by the twelfth attempt, when the message is functionally dead but still occupying a live queue. |

Durability options (`DataDir`, `WaitForCommit`) affect whether state survives a restart but do not change any command's contract. With no data directory the server is memory-only and all state is lost on shutdown — including registrations, which is what the `+REGISTER` handshake recovers from.
