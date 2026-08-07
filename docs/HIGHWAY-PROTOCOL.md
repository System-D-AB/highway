# The Highway Protocol

**Protocol version 1.0** — reflects everything shipped through feature 006.

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
- [Stock Garnet Dependencies](#stock-garnet-dependencies)
- [Key Schema](#key-schema)
- [Entry Framing](#entry-framing)
- [Doorbell Channels](#doorbell-channels)
- [Invariants](#invariants)
- [Server Options](#server-options)

---

## Protocol Version & Changelog

**Current version: 1.0**

A version is documentation for humans. Nothing negotiates it at runtime and no command reports it — Highway has no capability handshake.

**Versioning rule.** The minor version increases for additive changes: a new command, a new optional argument, a new field appended to a self-describing reply, a new error code. The major version increases for anything that could break a conforming client: removing or renaming a command, changing argument order, changing an existing reply's shape, or changing what an existing error code means.

| Version | Features | Change |
|---|---|---|
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
| `HW.PUBLISH` | 3 | 1 | Durable fan-out to every subscriber group |
| `HW.SUBSCRIBE` | 3 | 1 | Register a subscriber group and copy any backlog |
| `HW.UNSUBSCRIBE` | 3 | 1 | Remove a subscriber group and delete its state |
| `HW.RECEIVE` | -3 | 1 | Consume a batch of messages for a group |
| `HW.RACK` | 4 | 1 | Acknowledge a consumed message |
| `HW.HEARTBEAT` | -2 | 3 | Register a catalog, prove liveness, or depart |
| `HW.DISCOVER` | 2 | 1 | Live nodes hosting a service |
| `HW.STATS` | -1 | 3 | Server, service, or channel counters |

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
| `ERR HW_INTERNAL <detail>` | An unexpected exception escaped a command handler | Permanent — a server bug |
| `ERR Transaction failed.` | Garnet aborted the transaction; no work was performed | **Transient — retry** |
| `ERR wrong number of arguments...` | Garnet's arity check, before the command runs | Permanent |

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
HW.PUBLISH <channel> <payload>   →   :groupCount
```

Appends the message to every registered group's queue, atomically.

| | |
|---|---|
| **Arguments** | `channel` — identifier. `payload` — opaque, up to `MaxPayloadBytes`. |
| **Reply** | RESP integer: the number of groups the message reached. `0` means it went to the backlog. |
| **Keys written** | `hw:ch:{channel}:seq`, every `hw:ch:{channel}:grp:{group}:q`, or `hw:ch:{channel}:backlog` |
| **Doorbell** | `hw:door:ch:{channel}:grp:{group}` per group, payload = `messageId` |
| **Idempotency** | **Not idempotent.** Repeating publishes a second message with a new ID. |

Fan-out is atomic: all groups receive the message or none do. There is no partial delivery.

Each message gets a channel-unique `messageId` from an incrementing counter. It is returned by `HW.RECEIVE` and used to acknowledge with `HW.RACK`.

**Zero groups → the backlog.** Publishing to a channel with no registered groups returns `0` and appends the message to a per-channel backlog, held for late subscribers. Backlog entries expire after `BacklogRetention` (default 1 day) and are capped at `MaxBacklogEntries` (default 10,000), oldest dropped first. Once at least one group exists, publishes go to group queues and the backlog is not used.

**On transient abort the message was not delivered at all.** See [Error Contract](#error-contract).

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

**No snapshot consistency.** Counters are read under the command's locks but describe independently-mutating structures. The reply is a set of point-in-time readings, not a coherent instant. Fine for monitoring; do not build invariants on cross-field arithmetic.

---

## Stock Garnet Dependencies

A client built only from the `HW.*` commands cannot function. These stock commands are required.

| Command | Purpose |
|---|---|
| `GET hw:rep:{requestId}` | **Retrieve an RPC reply.** There is no `HW.*` command for this — `HW.REPLY` writes a plain main-store string and the caller reads it directly. |
| `DEL hw:rep:{requestId}` | Remove the reply slot after collecting it. Optional but recommended; the `ReplySlotTtl` bounds leakage otherwise. |
| `SUBSCRIBE hw:door:...` | Observe doorbells. Optional — doorbells are a latency optimization and correctness never depends on them. |
| `PING` / `ECHO` | Connection health, as with any RESP server. |

**The reply doorbell is node-global.** Every client subscribed to `hw:door:rep` receives a notification for **every** reply on the server, not just its own. A client must ignore request IDs it did not issue, and in particular must never `DEL` a slot it does not own — doing so destroys another caller's reply and hangs that call until its timeout. This is a real defect that occurred during development, not a hypothetical.

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
| `hw:rep:{requestId}` | Main | String | Reply slot. **TTL `ReplySlotTtl`** (default 5 min) |

### Pub/Sub

| Key | Store | Type | Purpose |
|---|---|---|---|
| `hw:ch:{channel}:groups` | Object | Set | Registered subscriber groups |
| `hw:ch:{channel}:grplist` | Main | String | Newline-delimited mirror of the groups set |
| `hw:ch:{channel}:seq` | Main | Integer | Message-ID counter |
| `hw:ch:{channel}:backlog` | Object | List | Messages published with zero groups |
| `hw:ch:{channel}:grp:{group}:q` | Object | List | Undelivered messages for one group |
| `hw:ch:{channel}:grp:{group}:proc` | Object | List | Received, not yet acknowledged |

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
| RPC queue entry | `[u16 requestIdLen][requestId][payload]` |
| RPC processing entry | `[i64 claimTicksUtc][u16 requestIdLen][requestId][payload]` |
| Channel entry | `[i64 messageId][payload]` |
| Backlog entry | `[i64 publishTicksUtc][i64 messageId][payload]` |
| Group processing entry | `[i64 receiveTicksUtc][i64 messageId][payload]` |
| Registration record | `[i64 seenTicksUtc][catalog json bytes]` |

Timestamps are .NET UTC tick counts (100-nanosecond intervals since 0001-01-01).

The processing variants are the queue entry with a timestamp prefixed — that timestamp is what the lease sweeps compare against.

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
| `ReceiveMaxCount` | 500 | Maximum accepted `COUNT`. Above → `HW_INVALID_COUNT`. |

Durability options (`DataDir`, `WaitForCommit`) affect whether state survives a restart but do not change any command's contract. With no data directory the server is memory-only and all state is lost on shutdown — including registrations, which is what the `+REGISTER` handshake recovers from.
