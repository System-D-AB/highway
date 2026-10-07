# The Highway Protocol

**Protocol version 5.0** (Highway 3.0) — see the [changelog](#protocol-version--changelog); the RESP surface the broker serves is in [Served RESP Commands](#served-resp-commands).

## About

This file is the complete and authoritative definition of the Highway wire protocol: every command, every reply shape, every error, every stored record layout, every doorbell channel, and the guarantees that span commands.

**Scope.** Highway's broker is its own RESP server (since feature 040). It serves the `HW.*` command set plus a small, exhaustive list of stock commands (`AUTH`, `PING`, `SUBSCRIBE` for doorbells, and the opt-in cache's `GET`/`SET`/…) — listed in [Served RESP Commands](#served-resp-commands). Since 5.0, **every Highway-owned structure is reached through an `HW.*` command**; no Highway state is read or written with stock key commands, so the storage layout is private to the broker.

**Authority.** This file is the single authoritative definition of the protocol. Where any other document disagrees, this file governs. The local feature working notes keep the *reasoning* behind each decision; this file is the reference for *what* the protocol is.

**How this file stays true.** The [Command Index](#command-index) is parsed by `ProtocolConformanceTests` and checked against a running server in both directions — a command documented but not registered, registered but not documented, or registered with a different arity all fail the test suite. The rest of the file is prose and is the author's responsibility; a feature that changes the protocol is required to update this file in the same feature.

**Reading order.** Implementing a client: [Transport & Framing](#transport--framing), then [Error Contract](#error-contract), then [Work Commands](#work-commands) and [Session & Registry Commands](#session--registry-commands). Operating a server: [Storage Layout & Stored Records](#storage-layout--stored-records) and [Server Options](#server-options).

---

## Contents

- [Protocol Version & Changelog](#protocol-version--changelog)
- [Command Index](#command-index)
- [Transport & Framing](#transport--framing)
- [Error Contract](#error-contract)
- [Work Commands](#work-commands)
- [Session & Registry Commands](#session--registry-commands)
- [Idempotency Commands](#idempotency-commands)
- [Dead Letter Commands](#dead-letter-commands)
- [Observability Commands](#observability-commands)
- [Replication Commands](#replication-commands)
- [Served RESP Commands](#served-resp-commands)
- [Storage Layout & Stored Records](#storage-layout--stored-records)
- [Doorbell Channels](#doorbell-channels)
- [Invariants](#invariants)
- [Server Options](#server-options)

---

## Protocol Version & Changelog

**Current version: 5.0**

A version is documentation for humans. Nothing negotiates it at runtime and no command reports it — Highway has no capability handshake. A 5.0 broker serves 5.0 clients (Highway 3.0 packages); 4.x clients are not supported.

**Versioning rule.** The minor version increases for additive changes: a new command, a new optional argument, a new field appended to a self-describing reply, a new error code. The major version increases for anything that could break a conforming client: removing or renaming a command, changing argument order, changing an existing reply's shape, or changing what an existing error code means.

| Version | Features | Change |
|---|---|---|
| 5.0 | 065, 067, 068, 069, 070, 072, 073, 074 | **Highway 3.0.** **Major.** *One work path:* `HW.CLAIM SVC\|Q <name> <node> [COUNT n]` replaces `HW.DEQUEUE` and `HW.QCLAIM` and answers rows of `[token, id, payload]` — an **empty array** (not a nil) when nothing is due. `HW.ACK`, `HW.TOUCH` and `HW.FAIL` take the claim's **token** (`HW.ACK <token>`, `HW.TOUCH <token>`, `HW.FAIL <token> <type> <detail>`) and answer `:1` held / `:0` not held; `HW.QACK` is removed. `HW.QSEND` is renamed `HW.SEND`. *Declarative session:* `HW.SESSION <node> <json>` declares a node's catalog (optional), group subscriptions, deadLetters feeds, retry policies, and job schedules in one idempotent command, replacing the heartbeat registration form and `HW.SUBSCRIBE`; a liveness heartbeat for an unknown node answers `+SESSION` (was `+REGISTER`). `HW.JOB SET` remains as an operator command. *Highway state only through `HW.*`:* `HW.RESULT <requestId>` takes an RPC reply (was stock `GET`/`DEL hw:rep:*`); `HW.IDEM BEGIN\|END\|ABORT` is the `[Idempotent]` gate (was stock `SET NX`/`GET`/`DEL hw:idem:*`) — raw keys are now served only for the opt-in cache. *Errors:* adds `ERR HW_INVALID_TOKEN`; `ERR Transaction failed.` is no longer emitted. *Dead-letter contracts (073):* `HW.CLAIM DEAD <target> <node> [COUNT n]` claims feed notifications from feed boxes; `HW.DLQ` gains `COUNT`, `ID <deadId>`, `AFTER <ms>`, `RESOLVE <token> KEEP\|DISCARD\|REQUEUE [AFTER <ms>]`, `deadId` (`{box:x8}{seq:x16}`), and lineage requeue tracking; `HW.SESSION` gains `deadLetters` (declared feed subscriptions) and `policies` (per-target retry schedules with deterministic integer-only jitter); entry framing gains `[0xFE][u16 requeues]` lineage trailer; new doorbell `hw:door:dlq:{targetKind}:{targetName}`; new flight recorder events `DeadLetterResolved`, `DeadLetterRequeueRefused`, `DeadLetterFeedOverflow`; metrics `highway.deadletters.resolved`, `highway.deadletters.requeued`, `highway.deadletter.feed.depth`. *Replication:* the replica form of `HW.REPL.HELLO` carries `FORMAT <n>` and its reply gains a third element, the primary's format; a mismatch is refused on both sides — the primary answers `ERR HW_STORAGE_FORMAT`, the replica stops pulling. Adds the peer form `HW.REPL.HELLO PEER <nodeId> <lastSeenEpoch>` (a standby defers to a live higher-priority peer before answering willing) and the equal-epoch tie-break on the replica form (069). *Storage format 4* (the box layout, 065): every structure in its own key range; a 2.x data directory is refused, never migrated. *Budgets:* a delayed publish now counts against, and can be refused by, each group's byte budget, as a delayed send always did. Expired reply slots and idempotency markers are now deleted, not merely hidden. *Sessions are authoritative (070):* `HW.SESSION`'s `subscriptions` list is the node's complete set — groups it no longer lists lose it, and a group whose last member left drains (skipped by publishes, backlog kept, retired by the first publish after `SubscriberRetirementThreshold`, never when retirement is disabled); `HW.STATS` gains `draining` on channel and group-queue rows. *Process identity (072):* a client names every connection `CLIENT SETNAME <node>@<instance>` with a random per-process instance id, and `HW.SESSION` gains an optional, display-only `instance` object; the broker counts the processes behind each node name and records `NodeNameShared` when more than one uses it. No command, reply or key changes. *Duplicate-safety (074):* a repeated `HW.CALL` for a `requestId`, or `HW.SEND` for a `messageId`, that this broker accepted within `RpcDuplicateWindow` / `SendDuplicateWindow` (10 min) answers `+OK` and is not stored again; `HW.PUBLISH` gains `ID <publishId>` (the client sends one per publish, reused by its retries) and a repeat within `PublishDuplicateWindow` answers the original group count and writes nothing. Ids are remembered in memory per broker process, only after their commit, and forgotten on every epoch change; a dead-lettered call's id is forgotten. `HW.PUBLISH` rejects a repeated or unknown keyword (`expected AT or ID`). `HW.STATS` server fields `callDuplicates`, `sendDuplicates`, `publishDuplicates`. Client-side (no wire change): handlers are cancelled at `MaxProcessingTime` and abandoned after `AbandonGrace`; `HW.TOUCH` renewal continues until the handler stops or is abandoned. |
| 4.13 | 062 | **Replica pulls from memory.** `HW.REPL.PULL`'s reply shape is unchanged; its page is now defined precisely: the first batch begins at exactly `fromSeq + 1` (the replica's watermark + 1), and a WAL record that spans the cursor is trimmed to start there. A batch is one committed write or several merged by RocksDB's group commit, so batch boundaries may differ between pulls of the same range; sequences and contents do not. A record that cannot be trimmed with certainty is refused with `ERR HW_REPL_GAP`. Compatible with existing replicas. |
| 4.12 | 059, 060 | **Idle write load.** Adds the key `hw:reg:seen:{nodeId}` (8-byte liveness timestamp). The `HW.HEARTBEAT` liveness form now writes only that key and **no longer rewrites** `hw:reg:node:{nodeId}` — the catalog is written once, at registration, instead of on every beat (and is no longer re-shipped to standbys per beat). Effective last-seen is the newer of the record header and the new key, so pre-4.12 records read correctly. Server-side: a commit that stages nothing (e.g. an empty `HW.QCLAIM`/`HW.DEQUEUE`) no longer issues a synced write. No command, argument or reply changes. Additive. |
| 4.11 | 052 | **Readiness field.** The `HW.STATS` server form and `HW.REPL.STATUS` append `repl.ready` (bool) and `repl.readyReason` (`primary`/`replica`/`fenced`/`draining`/`bootstrapping`/`demoted`) — the same decision the new HTTP `GET /ready` endpoint serves, so readiness is legible on every channel. The health endpoints (`/health`, `/ready`, `/replication`) are HTTP, not RESP, and carry no wire command. Additive. |
| 4.10 | 057 | **Message size.** The default `MaxPayloadBytes` is raised from 1 MiB to **5 MiB**, configurable up to a **15 MiB** ceiling (above which the server refuses at build). The `HW.STATS` server form appends a `maxPayloadBytes` field so a client learns the server's configured limit at connect and does not falsely reject below a raised server limit. Additive. |
| 4.9 | 044 | **Broker-local cache.** Adds a third raw-key family, `hw:cache:*`, routed on the existing stock `GET`/`SET`/`DEL`/`UNLINK`/`SETEX`/`PSETEX`/`TTL`/`PTTL` — **no new `HW.*` command**. It is served **only when `server.cache.enabled`** (off by default); the family answers exactly as the idempotency family does on the wire, but is backed by a **separate, never-replicated** store (its own RocksDB at `dataDir/cache`, or in-memory on an ephemeral broker). A cache `SET` on a non-master is refused `-NOTPRIMARY` like any write; a cache `GET` on a non-master returns a miss (null). TTL comes from `PX`/`EX`, defaults to the broker's `defaultTtl` when absent, and is clamped to `maxTtl`. Additive and opt-in. |
| 4.8 | 042-1a | **The herd contract.** `HW.REPL.HELLO` grows two additive forms: an optional 4th argument on the replica form (the caller's endpoint — a promoting node announces itself so a demoted primary's `-NOTPRIMARY` redirects correctly) and the **client handshake** `HW.REPL.HELLO CLIENT <clientId> <lastSeenEpoch>` answering `["master"\|"willing", epoch, rosterVersion]` or `["standby", masterEndpoint, masterEpoch]`. Adds `HW.REPL.JOIN` (roster admission; `ERR HW_PRIORITY_TAKEN` on a held priority), `HW.REPL.GOODBYE` (graceful drain + stand-down, parent R12), and the `roster.*` fields on `HW.REPL.STATUS`. Adds `ERR HW_REPL_GAP` on `HW.REPL.PULL` (a cursor behind the retained WAL is refused, never served a gapped stream — the replica re-syncs via SNAPSHOT). Adds the `hw:door:topology` narration channel (TOPOLOGY / GOODBYE / ROSTER-UPDATE, advisory). **Withdraws** the never-released `HW.REPL.WITNESS <nodeId> <role>` sketch — WITNESS is the bare `+OK` probe; failover is client-herd-driven (feature 042-1), not witness-gated. Additive. |
| 4.7 | 042 | **Replication (T3–T7).** Adds `HW.REPL.SNAPSHOT` (chunked checkpoint bootstrap), `HW.REPL.PROMOTE`, `HW.REPL.FENCE`, `HW.REPL.STATUS`, `HW.REPL.WITNESS`. A non-primary refuses client writes with `-NOTPRIMARY <endpoint> <epoch>` (not an `ERR HW_` prefix). `HW.STATS` server form appends `repl.*` fields. Additive. |
| 4.6 | 042 | **Replication stream (T1).** Adds `HW.REPL.HELLO`, `HW.REPL.PULL`, `HW.REPL.ACK` — pull-based, paged, resumable WAL shipping from a primary. Replicas register a slot, pull pages of `(seq, batchBytes)`, and ack a watermark. Epoch is stamped on every reply. Additive. |
| 4.5 | 028 | **Recurring jobs.** Adds `HW.JOB SET\|DEL\|LIST` (appended to the command table) and two keys: `hw:job:{queue}:schedules` (sorted set: score = nextFireTicks, member = a versioned schedule record carrying job name, expression, lastFire, nextFire and the template payload) and `hw:job:index` (main-store mirror of queues with schedules). **Firing lives in `HW.QCLAIM`**: the promotion sweep enqueues exactly one occurrence for each due schedule and re-arms it — one member replaced at a new score — atomically inside the claim transaction. Occurrence ids are `job:{name}:{ticks}`. Missed occurrences catch up as ONE fire with the next computed from now; a full queue refuses the fire without consuming it (`JobFireRefused`). Expressions: `daily:HH:mm`, `every:{seconds}`, `cron:{5-field}`, all UTC. Adds four recorder events (`JobFired`, `JobScheduleChanged`, `JobScheduleRemoved`, `JobFireRefused`). Additive. |
| 4.4 | 025 | **Subscription groups.** `HW.SUBSCRIBE` gains an optional third argument (arity 3 → -3): the subscribing **node**, so the server can track which nodes back a group. Absent means the pre-025 identity — the group is the node — so an old client's two-argument subscribe is unchanged in meaning. Adds two mirror keys: `hw:grp:members:{channel}@{group}` (nodes backing a group) and `hw:reg:node:{nodeId}:subs` (`{channel}@{group}` entries a node subscribes through). **Two behaviour changes:** automatic retirement now measures a group by its **youngest member's** heartbeat (a group with no membership record falls back to the 017 rule — group name as node name), and `HW.HEARTBEAT BYE PURGE` destroys a group's queue only when the departing node was its **last member** — otherwise it removes the membership and the group lives on for the siblings. Client-side, replicas sharing a `SubscriptionGroup` claim with the **group** as the claimant, competing through the ordinary queue machinery; the default (group = node name) is byte-identical to 018's wire traffic. Additive. |
| 4.3 | 019 | **Long-running tasks.** Adds `HW.TOUCH SVC/Q`, which moves a claimed entry's timestamp forward so a handler that outlives `Lease` is not duplicated while it is still running. No new field, framing or key — the sweep already decides expiry from that timestamp. Adds the `ProcessingCapExceeded` recorder event. **One behaviour change:** the client renews automatically by default, so a **hung** handler is now recovered after `MaxProcessingTime` (15 min) rather than after `Lease` (5 min). Deliberate: a slow-but-working handler executed five times and dead-lettered corrupts data, while a hung one taking ten minutes longer to recover is a delay. `MaxProcessingTime = TimeSpan.Zero` restores the old behaviour exactly. |
| 4.2 | 017 | **Node decommissioning.** Adds `HW.HEARTBEAT <node> BYE PURGE` — a third form that retires a node permanently, destroying its subscriber queues and returning `[groups, messages, bytes]`. Plain `BYE` is unchanged and still preserves a subscriber's backlog: `BYE` is "I am stopping", `PURGE` is "I am never coming back". Adds the `hw:reg:node:{nodeId}:channels` mirror key and the `GroupRetired` recorder event. *(Since 4.4/025: a purge destroys only groups whose **last member** is departing — a shared group loses the membership and lives on.)* **A subscriber group whose node has been absent past `SubscriberRetirementThreshold` (24h default) is now retired automatically**, which is what stops one dead subscriber blocking a channel once its queue reaches `MaxQueueBytes`. Additive — no existing command, reply or entry framing changed. |
| 4.1 | 016 | **Durable by default, and bounded.** No command changes. A server built with no configuration now creates a data directory beside the executable and enables AOF, so queued work survives a restart; memory-only is asked for by name (`Ephemeral()`). Adds the `HW_QUEUE_FULL` error and the `hw:q:{queue}:bytes` counter key. A full queue **refuses** rather than dropping its oldest entry, and a publish refuses in full when **any** group's queue is full, naming that group — a fan-out reaches every registered group or none. Additive: no existing command, reply or entry framing changed. |
| 4.0 | 018 | **Pub/Sub Unification.** Removes `HW.RECEIVE` and `HW.RACK` — subscribers now consume through `HW.QCLAIM`/`HW.QACK` on derived queues named `{channel}@{group}`. Removes two entry framings (channel entry, group processing entry); `Envelope` is down to two. `HW.FAIL` and `HW.DLQ` lose the `CH` target — a group **is** a queue, targeted as `Q`. The `hw:ch:{channel}:grp:{group}:*` key space, `hw:ch:{channel}:delayed`, and the `hw:door:ch:{channel}:grp:{group}` doorbells are all gone. `@` is reserved in identifiers. **Three semantic changes (R5):** (1) batch consumption is lost — one claim per round trip replaces `HW.RECEIVE`'s batch; (2) subscriber ordering is preserved by default (concurrency 1 per group); (3) deferred publish resolves groups at **publish time**, not promotion time — a group registering during the delay does not receive the message. **Major**: removes two commands, two framings, one key space; existing channel data becomes unreachable. A broker started against pre-018 data refuses with a diagnostic message. |
| 3.1 | 015 | **Diagnosable failures.** Adds `HW.FAIL`, which records the exception that caused a delivery to fail without acknowledging the message, and an optional **failure block** on every entry framing. Adds the `DeliveryFailed` recorder event and new dead-letter fields. Additive: the failure block is a *trailer*, so an entry written without one decodes byte-for-byte as before — unlike 013's attempt count, which changed the framings themselves. No existing command, reply or key changed. |
| 3.0 | 014 follow-up | **The channel backlog is removed.** A publish with no registered group is delivered to nobody; `HW.SUBSCRIBE` copies nothing and a new group starts empty. `BacklogRetention`, `MaxBacklogEntries`, the `hw:ch:{channel}:backlog` key, its entry framing and the `backlog` field on `HW.STATS` are all gone. **Major**: it removes a documented guarantee. The capability moved rather than vanishing — "hold this until someone can handle it" is `HW.QSEND` and a queue, which is durable by design and has no surprising dependence on when the first subscriber started. Existing backlog data becomes unreachable; delete the data directory. |
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
| `HW.CALL` | 4 | 1 | Enqueue an RPC request on a service and ring the service doorbell |
| `HW.SEND` | -4 | 2 | Enqueue work for exactly one processor, now or at a future time. Refuses with `HW_QUEUE_FULL` at the byte limit |
| `HW.PUBLISH` | -3 | 2 | Durable fan-out to every subscriber group, immediately or at a future time |
| `HW.CLAIM` | -4 | 2 | Claim the next due work from a service (`SVC`) or queue (`Q`) for a node; returns `[token, id, payload]` rows |
| `HW.ACK` | 2 | 1 | Settle a claim by its token — it can never be redelivered |
| `HW.TOUCH` | 2 | 1 | Renew a claim's lease by its token, without acknowledging |
| `HW.FAIL` | 4 | 1 | Record why a handler failed on a claim, by its token, without acknowledging |
| `HW.REPLY` | 3 | 1 | Write the caller's reply slot and ring the reply doorbell |
| `HW.RESULT` | 2 | 1 | Take (read and remove) a waiting RPC reply |
| `HW.DLQ` | -3 | 3 | Inspect, requeue, or purge dead letters |
| `HW.IDEM` | -4 | 3 | The `[Idempotent]` gate: claim, complete, or abort a delivery's run |
| `HW.SESSION` | 3 | 1 | Declare a node's session: catalog (optional), group subscriptions, job schedules |
| `HW.HEARTBEAT` | -2 | 3 | Prove liveness, depart, or retire permanently |
| `HW.UNSUBSCRIBE` | 3 | 1 | Remove a subscriber group and destroy its queue |
| `HW.JOB` | -2 | 3 | Register, remove, or list recurring-job schedules; firing rides `HW.CLAIM` |
| `HW.DISCOVER` | 2 | 1 | Live nodes hosting a service |
| `HW.STATS` | -1 | 4 | Server, service, channel, queue, or recorder counters |
| `HW.REPLAY` | -2 | 1 | Recent recorded operations for one name |
| `HW.REPL.HELLO` | -4 | 2 | Replica form: register/resume a slot, declaring the storage format (optional announcing endpoint). CLIENT form: the herd handshake — master / willing / standby-with-redirect. PEER form: the same answer to a standby probing its higher-priority peers |
| `HW.REPL.PULL` | 3 | 1 | Pull a paged WAL batch from a sequence, stamped with the primary's epoch |
| `HW.REPL.ACK` | 3 | 1 | Advance a replica slot's acked watermark |
| `HW.REPL.SNAPSHOT` | -2 | 3 | Stream a checkpoint (`BEGIN` / `GET` / `END`), resumable by file and offset |
| `HW.REPL.PROMOTE` | -1 | 1 | Increment epoch and become writable (admin or deadman) |
| `HW.REPL.FENCE` | -1 | 1 | Stop accepting client writes without changing epoch |
| `HW.REPL.STATUS` | 1 | 1 | Role, epoch, slots, lag, last promotion, reconciliation path, and the live roster |
| `HW.REPL.WITNESS` | 1 | 1 | Bare liveness probe (+OK) |
| `HW.REPL.JOIN` | 4 | 1 | Announce a node into the roster at a priority; a held priority is refused naming the holder |
| `HW.REPL.GOODBYE` | -1 | 1 | Begin the graceful drain: narrate GOODBYE, refuse new work, let in-flight complete, stand down |

---

## Transport & Framing

### RESP, not a new protocol

Highway speaks standard RESP2, so `redis-cli`, RESP protocol analyzers, and StackExchange.Redis `Execute()` all work against a Highway broker unmodified. The broker is not a general Redis: it serves the `HW.*` commands and the short list in [Served RESP Commands](#served-resp-commands), and answers anything else with an error naming the served set.

### Arity convention

Arity counts the command name itself:

- **Positive** — the exact argument count. `HW.CALL` has arity 4: the name plus three arguments.
- **Negative** — the minimum. `HW.SEND` has arity -4: the name plus at least three arguments.

The broker checks arity before a command's own validation runs, so a wrong argument count produces `ERR wrong number of arguments…` rather than one of Highway's codes. A negative arity places **no upper bound**: extra arguments pass the arity check, and a command rejects or ignores them as its section says.

### Identifiers

Service names, channel names, group names, queue names, node IDs, request IDs, message IDs and job names are *identifiers* and share one rule:

- non-empty;
- at most `MaxIdentifierBytes` (default 256) bytes;
- every byte `>= 0x20` and not `0x7F`;
- **must not contain `@`** — reserved for derived group queue names `{channel}@{group}` (since 4.0). A group queue is addressed by its derived name where a command takes a queue (`HW.CLAIM Q`, `HW.DLQ Q`, `HW.STATS Q:`), and nowhere else.

No C0 control characters, no DEL, no `@`. Validation runs on raw bytes before any string decoding, so no stored key is ever derived from an invalid value. A violation is rejected with `HW_INVALID_ARG`, naming the argument and why. Names inside `HW.SESSION`'s JSON obey the same rule and are reported the same way.

**Why the rule exists.** Identifiers become components of stored keys and of doorbell channel names; banning the whole C0 range plus DEL is cheaper to reason about than banning individual delimiters, and costs nothing for real identifiers.

### Payloads

Payloads are **not** identifiers. They are opaque bytes, stored and returned byte-for-byte, subject only to a size cap (`MaxPayloadBytes`, default 5 MiB, configurable to a 15 MiB ceiling). Any byte value is permitted, including control characters and invalid UTF-8.

### Claim tokens

`HW.CLAIM` returns a **token** per claimed message; `HW.ACK`, `HW.TOUCH` and `HW.FAIL` take it back. A token is opaque ASCII — echo it verbatim, never construct or parse one. (Today it is `{box}.{sequence}`; that may change without a protocol version.) A token names exactly one claim: sequences are never reused, so after a lease expiry the redelivered message carries a new token and the old one answers `:0`. A token that does not parse is `ERR HW_INVALID_TOKEN`.

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

The `tp` field is optional in both directions and does **not** change the envelope version. `v` and `body` are the only required fields, and a reader ignores properties it does not recognise. A reader that does not understand `tp` must ignore it rather than reject the envelope.

No polymorphic type metadata is ever written. The wire carries a service or channel name and a JSON shape, never CLR type identity.

---

## Error Contract

This is the most important section for a client implementer: the whole retry policy rests on it. Getting it wrong means either spinning forever on a request that can never succeed, or silently dropping one that would have worked.

### The classification rule

> A reply beginning `ERR HW_` is **permanent** — never retry.
> A reply beginning `NOTPRIMARY` is **failover** — reconnect to the advertised endpoint (feature 042) and retry there.
> `NOAUTH`, `WRONGPASS` and `NOPERM` are **permanent**, and are configuration, not code.
> A lost or reset connection is **transport** — retry with bounded backoff, failing over first when other endpoints are configured.
> Anything else is **permanent**.

That is the entire rule, and it is total. (Before 5.0 the bare `ERR Transaction failed.` was a retryable class, emitted by Garnet transactions; no 5.0 command emits it. A client may still treat it as retryable — nothing will send it.)

### Codes

| Code | Meaning | Class |
|---|---|---|
| `ERR HW_INVALID_ARG <detail>` | An identifier is blank, contains a control character or `@`, exceeds the length cap, or an argument is otherwise malformed (an unknown keyword, a bad `AT`, a session JSON that does not parse). The detail names the argument | Permanent |
| `ERR HW_INVALID_TOKEN <detail>` | A claim token that does not parse. A token that parses but is not held answers `:0`, not this | Permanent |
| `ERR HW_PAYLOAD_TOO_LARGE <actual> > <limit>` | Payload above `MaxPayloadBytes`, catalog above `MaxCatalogBytes`, or a job template above `MaxPayloadBytes` | Permanent |
| `ERR HW_INVALID_COUNT <detail>` | A `COUNT` or `LIMIT` that is non-numeric, zero, negative, overflowing, or above its cap | Permanent |
| `ERR HW_QUEUE_FULL <detail>` | A queue is at its byte limit. The message names the queue (or, for a publish, the **group**) and the limit | Permanent — the caller must shed load, buffer, or alert. Retrying into a full queue holds a connection and hammers a broker already over budget |
| `ERR HW_STORAGE_FORMAT <detail>` | Stored data this build cannot read: a pre-013 entry, or — on `HW.REPL.HELLO` — a replica declaring a different storage format (a 2.x node against a 3.0 primary) | Permanent — drain with the previous version, or wipe and re-sync |
| `ERR HW_REPL_GAP <detail>` | A replica's pull cursor fell behind the retained WAL (042-1a). The detail names the requested and first-available sequences | Permanent for that cursor — the replica must re-sync via `HW.REPL.SNAPSHOT`; never retried as-is |
| `ERR HW_PRIORITY_TAKEN <detail>` | A joining node announced a roster priority a live member already holds (042-1) | Permanent — an operator config error; the detail names the holder |
| `ERR HW_INTERNAL <detail>` | An unexpected exception escaped a command handler | Permanent — a server bug |
| `NOTPRIMARY <endpoint> <epoch>` | This node is not writable (replica, fenced, draining for new work, or demoted). `<endpoint>` is where the client should go; `<epoch>` is this node's epoch | **Failover — retry on `<endpoint>`** (042). Not an `ERR HW_` prefix |
| `NOAUTH <detail>` | The server requires authentication and none was presented | **Permanent** — configuration, not code |
| `WRONGPASS <detail>` | The credentials were rejected | **Permanent** |
| `NOPERM <detail>` | Authenticated, but not permitted to run the command | **Permanent** |
| `ERR wrong number of arguments...` | The arity check, before the command runs | Permanent |

**The authentication errors carry no `ERR HW_` marker.** They are permanent, and worth their own exception type because the remedy differs from every other permanent failure — it is a configuration problem, not a code or network one. StackExchange.Redis surfaces `NOAUTH`/`WRONGPASS` raised during connection setup inside a *connection* exception, so a client that does not inspect the message chain will report a wrong password as an unreachable host and send its operator to check the network.

**A command either commits entirely or changes nothing.** Every `HW.*` command stages its writes in one batch and commits it once; a rejected or failed command writes nothing. That is what makes a permanent error safe to report and move on from — there is no partial state to repair.

### `+SESSION` is not an error

A liveness heartbeat for a node the broker holds no session for replies with the simple string `SESSION`. This is a **normal reply**: the broker needs the node's session declared again (`HW.SESSION`). It must not be routed through error handling or classified by the rule above. See [`HW.HEARTBEAT`](#hwheartbeat).

---
## Work Commands

Highway moves three kinds of work, and 5.0 gives them one path:

- a **service** receives RPC requests (`HW.CALL`) and answers each with `HW.REPLY`;
- a **queue** receives work for exactly one processor (`HW.SEND`), with no reply;
- a **group queue** `{channel}@{group}` receives one copy of every message published to its channel (`HW.PUBLISH`) — a subscriber group *is* a queue (since 4.0).

All three are claimed with `HW.CLAIM` and settled by token with `HW.ACK`, `HW.TOUCH` and `HW.FAIL`. Delivery is **at least once**: a claim that is not acknowledged before its lease expires is delivered again, with its attempt count incremented, until `MaxDeliveryAttempts`, after which it is dead-lettered (see [Dead Letter Commands](#dead-letter-commands)).

A service and a queue may share a name without colliding — `HW.CLAIM SVC x` and `HW.CLAIM Q x` read different work.

An RPC request flows: `HW.CALL` enqueues it → a worker claims it with `HW.CLAIM SVC` → the worker writes the answer with `HW.REPLY` → the caller takes the reply with `HW.RESULT` → the worker settles the claim with `HW.ACK`.

### HW.CALL

```
HW.CALL <service> <requestId> <payload>   →   +OK
```

Appends the request to the service and rings the service doorbell.

| | |
|---|---|
| **Arguments** | `service`, `requestId` — identifiers. `payload` — opaque, up to `MaxPayloadBytes`. |
| **Reply** | `+OK` |
| **Doorbell** | `hw:door:svc:{service}`, payload = `requestId`. None for a suppressed repeat. |
| **Idempotency** | **Idempotent per `requestId` within `RpcDuplicateWindow`** (10 min, 074): a repeat answers `+OK` and is not queued again. The id is remembered only after the request is committed, per broker process (a newly promoted primary queues a re-send), and is forgotten when the request is dead-lettered so a caller still waiting can re-send it. The caller owns request-ID uniqueness. |

Calling a service with no workers succeeds — the request waits durably. There is no registration check; an unknown service name is a valid service. The `requestId` is the caller's correlation handle and names its reply slot. It is stored and returned unmodified.

### HW.SEND

```
HW.SEND <queue> <messageId> <payload>              →   +OK
HW.SEND <queue> <messageId> <payload> AT <ticks>   →   +OK   (deferred)
```

Enqueues work for exactly one processor (was `HW.QSEND`, 014).

| | |
|---|---|
| **Arguments** | `queue`, `messageId` — identifiers (so a send can never target a group queue). `payload` — opaque, up to `MaxPayloadBytes`. `AT <ticks>` — optional absolute .NET UTC delivery time. |
| **Reply** | `+OK`, or `ERR HW_QUEUE_FULL` at `MaxQueueBytes` |
| **Doorbell** | `hw:door:q:{queue}`, payload = `messageId`. **None for a deferred send**, which is in no worker's reach yet. |
| **Idempotency** | **Idempotent per `messageId` within `SendDuplicateWindow`** (10 min, 074): a repeat answers `+OK` and stores nothing — no doorbell, no recorder event. The id is remembered only after the message is committed, so a send refused with `HW_QUEUE_FULL` is not remembered and its retry is stored. Per broker process. |

**Sending never requires a running worker.** The message waits until one claims it.

**The byte budget refuses rather than drops** (016). A queue counts the bytes of its ready and deferred messages; a send that would take it past `MaxQueueBytes` is refused and nothing is stored. Claimed messages do not count.

**`AT` is absolute**, not a relative delay: the client computes `UtcNow + delay` and the broker stores what it was told, so a slow round trip cannot silently extend the delay and a restart cannot re-delay. A time in the past delivers immediately. The guarantee is **"not before", not an alarm clock**: a deferred message becomes claimable on the first `HW.CLAIM` of its queue after its time — promotion is driven by consumers polling, not by a timer in the broker, so a queue whose workers have all stopped promotes nothing until one starts (the message is not lost; it is not delivered either). Promotion is capped per claim; a larger due batch drains over successive claims.

### HW.PUBLISH

```
HW.PUBLISH <channel> <payload>                            →   :groupCount
HW.PUBLISH <channel> <payload> AT <ticks>                 →   :0            (deferred)
HW.PUBLISH <channel> <payload> ID <publishId> [AT <ticks>] →   :groupCount   (074; ID and AT in either order)
```

Appends the message to every registered group's queue, atomically.

| | |
|---|---|
| **Arguments** | `channel` — identifier. `payload` — opaque, up to `MaxPayloadBytes`. `AT <ticks>` — optional absolute delivery time, as for `HW.SEND`. `ID <publishId>` — optional identifier, the client's retry key for this publish (074). Each keyword at most once; anything else is `HW_INVALID_ARG` (`expected AT or ID`). |
| **Reply** | RESP integer: the number of groups the message reached. `0` means no group is registered, or that the message is deferred and has reached none yet. `ERR HW_QUEUE_FULL` naming the group when any group is at its budget. |
| **Doorbell** | `hw:door:q:{channel}@{group}` per group, payload = `messageId`. None for a deferred publish. |
| **Idempotency** | **With `ID`: idempotent within `PublishDuplicateWindow`** (10 min, 074) — a repeat answers the **original** group count and writes nothing (no doorbell, no recorder event). The id is remembered only after the publish is committed, so a refused publish is not remembered. Per broker process. **Without `ID`:** not idempotent — repeating publishes a second message. Highway's client always sends an id, one per `PublishAsync` call, reused by its retries. |

**All groups or none.** The fan-out is one commit. If any group's queue would pass `MaxQueueBytes`, the publish is refused whole — no group receives it — and the error names the full group. A deferred publish is budgeted too (5.0; before 5.0 a deferred publish bypassed the budget).

Each message gets a channel-unique `messageId` from an incrementing counter; it is the `id` a group's claim returns.

**Zero groups → nobody.** Publishing to a channel with no registered groups returns `0` and the message is **not stored**. Publish means "tell everyone who is listening"; a durable hand-off to whoever comes later is `HW.SEND` to a queue.

**Groups are resolved at publish time.** A deferred message is placed in each group's queue as deferred at once; a group registering during the delay does not receive it.

**Subscriber retirement** (017). Before fanning out, a publish retires any group all of whose registered members have been silent longer than `SubscriberRetirementThreshold` (a member with no registration record is not evidence of death), destroying its queue so one dead subscriber cannot block a channel at its byte budget. A group past half the threshold is reported as suspect in the flight recorder.

### HW.CLAIM

```
HW.CLAIM SVC  <service> <node> [COUNT <n>]   →   [[token, requestId, payload], ...]
HW.CLAIM Q    <queue>   <node> [COUNT <n>]   →   [[token, messageId, payload], ...]
HW.CLAIM DEAD <target>  <node> [COUNT <n>]   →   [[token, deadId, payload], ...]
```

Claims the next due work, oldest first, for `node`. **An empty array means nothing is due** — never a nil (5.0).

| | |
|---|---|
| **Arguments** | `SVC`, `Q`, or `DEAD`. `service` / `queue` — identifier; a `Q` name may be a derived group queue `{channel}@{group}`. For `DEAD`, `target` is `<kind>:<name>` or derived queue name (e.g. `svc:order.process`, `q:order.queue`, `q:events@billing`). `node` — identifier, recorded on the claim. `COUNT n` — optional, `1`..`ReceiveMaxCount`, default `1`. |
| **Reply** | An array of three-element arrays `[token, id, payload]`, in delivery order, possibly empty. For `DEAD`, `id` is the dead letter handle `deadId` (`{box:x8}{seq:x16}`). |
| **Idempotency** | Not idempotent — each call claims distinct work. |

A claim holds the work until it is acknowledged, settled, or its lease (`Lease`, default 5 minutes) expires. **Before handing out work, a claim does the recovery work for its target, in the same commit:**

1. *(queues)* fire recurring jobs that are due (see [`HW.JOB`](#hwjob)) and promote deferred messages whose time has come;
2. return every claim whose lease has expired to the queue with its attempt count incremented — or dead-letter it once it exceeds `MaxDeliveryAttempts`;
3. *(services)* return the claims of every node whose registration has gone stale (older than `NodeExpiry`) to the queue with attempts incremented, and remove that node from discovery and the registry — dead-node recovery (006). A node with **no** registration record is not participating in the registry (heartbeats off) and is left to the lease sweep. Set `PruningEnabled = false` to disable dead-node recovery; lease recovery still runs.
4. *(dead-letter feeds)* `HW.CLAIM DEAD` reads notifications from the target's dedicated feed box (`hw:feed:dlq:{kind}:{name}`). Claims are leased and settled via `HW.DLQ RESOLVE <token> KEEP|DISCARD|REQUEUE [AFTER <ms>]`. Lease recovery returns expired feed claims to the feed box.

The lease sweep reads only claims whose lease has actually expired, so it costs nothing on a claim when nothing expired.

A group queue's claims are recovered by lease alone — dead-node recovery never touches subscriber groups (see [Invariants](#invariants)).

### HW.ACK

```
HW.ACK <token>   →   :1 removed   |   :0 not held
```

Settles a claim for good: after it the message can never be redelivered by any mechanism. `:0` means the token is not held — already acknowledged, recovered by a lease sweep and redelivered under a new token, or never issued. A late acknowledgement is a race the client cannot avoid, not an error to investigate; a **stale token never settles the redelivery** of the same message.

`HW.REPLY` must be sent **before** `HW.ACK` — see [Invariants](#invariants).

### HW.TOUCH

```
HW.TOUCH <token>   →   :1 renewed   |   :0 not held
```

Says "still working" about a claim. Without it, a handler that outlives `Lease` has its message redelivered **while it is still running** — a concurrent duplicate caused by nothing but slowness.

| | |
|---|---|
| **Effect** | The claim's lease restarts now. Nothing else changes — not the attempt count, the payload, the byte counters, or its failure block |
| **Idempotency** | Idempotent: renewing twice is renewing |

**It does not acknowledge.** The claim stays held and every recovery path still applies. **Renewal is bounded by the client, not the server** (019): the server renews whenever asked. The client cancels the handler at `MaxProcessingTime` and keeps renewing only until the handler has stopped — or until it abandons the handler `AbandonGrace` later because it ignored the cancellation, after which it renews no more and lease recovery redelivers the message (074; before, renewal stopped at the cap while the handler kept running, so a second worker could start the same message under it).

### HW.FAIL

```
HW.FAIL <token> <type> <detail>   →   :1 recorded   |   :0 not held
```

Records **why** a delivery failed, on the claimed entry. Without it a dead letter says only that something failed *n* times.

| | |
|---|---|
| **`<type>`** | The exception type name, at most 65,535 bytes. A separate argument because the server maintains `firstType` from it |
| **`<detail>`** | An opaque blob the server never interprets (kept only when the target's recorder capture is `Full`). Highway's own client sends JSON carrying the message, stack, node and timestamp |
| **Idempotency** | Idempotent in effect: a second report replaces the block rather than appending |

**It does not acknowledge.** The lease sweep recovers the message on exactly the schedule it would have; the failure block rides with it through redelivery and into its dead letter. `firstType` is empty unless the failure changed shape — it answers *"did this fail the same way every time?"* — and once set is never overwritten.

### HW.REPLY

```
HW.REPLY <requestId> <payload>   →   +OK
```

Writes the caller's reply slot and rings the reply doorbell.

| | |
|---|---|
| **Arguments** | `requestId` — identifier. `payload` — opaque, up to `MaxPayloadBytes`. |
| **Reply** | `+OK` |
| **Doorbell** | `hw:door:rep`, payload = `requestId` |
| **Idempotency** | **Last-writer-wins.** A second reply overwrites the first and restarts its expiry. |

The slot expires after `ReplySlotTtl` (default 5 minutes). An uncollected reply is **deleted** when it expires (5.0; before 5.0 it was hidden from reads but stayed on disk).

### HW.RESULT

```
HW.RESULT <requestId>   →   payload   |   $-1 (nil bulk: no reply waiting)
```

Takes the reply for `requestId`: returns it and removes it, in one command (5.0; replaces stock `GET` + `DEL` on `hw:rep:{requestId}`). Two readers can never both collect one reply, and a reader that crashes after reading leaves nothing behind. A nil answer means the reply has not been written yet (the doorbell raced ahead of it) or has expired.

**The reply doorbell is node-global.** Every client subscribed to `hw:door:rep` is told of **every** reply on the broker. A client must ignore request IDs it did not issue, and in particular must never `HW.RESULT` a request it does not own — that takes another caller's reply and hangs that call until its timeout.

---
## Session & Registry Commands

A node's **session** is everything the broker holds on its behalf: its registration (catalog, discovery index, liveness), its subscriber-group memberships, and its recurring-job schedules. 5.0 declares all of it with one idempotent command, `HW.SESSION`, and re-declares it whenever the broker has lost it — a wiped broker, a fresh node, a re-bootstrapped standby. Before 5.0 subscriptions and schedules were sent once, at client start, and a broker that lost them kept serving while a publish silently reached nobody and jobs never fired.

The registry records which nodes are alive and what each hosts. It powers fast-fail, dead-node recovery and operator visibility; it does **not** route work — distribution is competing consumers on `HW.CLAIM`.

### HW.SESSION

```
HW.SESSION <node> <sessionJson>   →   +OK
```

```json
{
  "catalog":       { "services": [ { "name": "orders.create", "requestType": "...", "responseType": "..." } ],
                     "channels": [ { "name": "orders.placed", "subscriberCount": 1 } ],
                     "queues":   [ { "name": "invoices.generate", "messageType": "..." } ],
                     "uses":     { "services": ["payments.authorize"], "queues": [], "channels": [] } },
  "subscriptions": [ { "channel": "orders.placed", "group": "billing" } ],
  "deadLetters":   [ { "kind": "queue", "target": "invoices.generate" } ],
  "policies":      [ { "kind": "queue", "target": "invoices.generate", "maxAttempts": 3,
                       "backoff": "exp:500ms..30s:jitter", "maxRequeues": 5 } ],
  "jobs":          [ { "queue": "invoices.generate", "job": "nightly",
                       "expression": "daily:02:00", "template": "<base64 payload>" } ],
  "instance":      { "id": "3f9c0a17b2e4", "host": "WEB1", "pid": 4120, "hosting": "shop2",
                     "started": "2026-10-01T09:12:44Z", "client": "3.0.0" }
}
```

| | |
|---|---|
| **Arguments** | `node` — identifier. `sessionJson` — at most `MaxCatalogBytes` + 16 × `MaxPayloadBytes`. |
| **Reply** | `+OK`. `HW_INVALID_ARG` naming the problem (the JSON does not parse; a name fails the [identifier rule](#identifiers); a job expression or retry backoff is malformed); `HW_PAYLOAD_TOO_LARGE` for a catalog above `MaxCatalogBytes` or a template above `MaxPayloadBytes`. At most 1,024 subscriptions and 256 jobs. |
| **Idempotency** | **Idempotent.** Declaring the same session again changes nothing (an unchanged subscription list writes nothing). |

Everything present is applied; everything is optional:

- **`catalog`** — registers the node exactly as the 4.x registration form did: the catalog is stored verbatim (the server reads only `services[].name`, to index discovery), the node joins the node list, and liveness starts now. A **changed** catalog re-indexes: services no longer hosted leave the index. **Omit it when the node does not send heartbeats**: a registered node that never beats looks dead, and dead-node recovery would take its work and retire its groups. Highway's client omits it with `HeartbeatEnabled = false`.
- **`subscriptions`** — the node's **complete** set of `(channel, group)` memberships (070). The node joins each listed group (as `HW.SUBSCRIBE channel group node` did, 025) and **leaves every group it declared before and no longer lists**. A group backed by several nodes shares one copy of each message: its members compete for it through the group queue. A group's liveness, for retirement, is its youngest member's. When the node leaving was a group's **last** member, the group **drains**: publishes skip it (it receives nothing new and can never refuse a publish with `HW_QUEUE_FULL`), its backlog is kept, and it is retired by the **first publish to its channel after** it has drained for `SubscriberRetirementThreshold` (default 24 h) — retirement happens on the publish path, not on a timer — unless a node declares it again first, which resumes it with its backlog. With retirement disabled (a zero threshold) a draining group is never retired: it stays skipped, keeping its backlog, until a node declares it again or an operator removes it (`HW.UNSUBSCRIBE`). An **absent** `subscriptions` property leaves memberships unchanged; an explicit empty list leaves them all. Highway's client always sends the list.
- **`deadLetters`** — the target feeds (`{ "kind": "service"|"queue", "target": "<name>" }`) that this node's dead-letter worker loop consumes via `HW.CLAIM DEAD`. Declaring a target ensures its dead-letter feed box (`BoxKeys.Kind.DeadFeed`) exists and is active for lease-based feed delivery.
- **`policies`** — per-target retry policies (`{ "kind": "service"|"queue", "target": "<name>", "maxAttempts": n, "backoff": "<schedule>", "maxRequeues": m }`) discovered from `[Retry]` attributes. Stored in target box metadata (`CMaxAttempts`, `CBackoffKind`, `CBackoffMin`, `CBackoffMax`, `CJitter`, `CMaxRequeues`). Supported backoff schedules: `none`, `fixed:<dur>`, or `exp:<min>..<max>[:jitter]`. During lease recovery, failed attempts are rescheduled to delayed space (`now + backoff`) with deterministic integer FNV-1a jitter instead of immediately dead-lettering.
- **`jobs`** — registers or replaces each schedule on its queue, as `HW.JOB SET` does: last registration wins, `lastFire` is preserved. Jobs are **upsert-only**: a job dropped from the code is removed with `HW.JOB DEL`, never by omission.
- **`instance`** — what the sending process says about itself (072), for display: `id` (the 12-lowercase-hex instance id its connections carry in [`CLIENT SETNAME`](#served-resp-commands)), `host`, `pid`, `hosting` (Windows service or IIS application pool), `started` (ISO-8601) and `client` (version). The broker keeps it **in memory only** — never stored, never replicated, never used for a decision — and shows it against that instance's live connections. It is read leniently: a missing or mistyped field is dropped, and an object without a valid `id` is ignored. **It never causes an error.**

A client sends its session at start, again after every failover adoption, and at once whenever a heartbeat answers `+SESSION`.

### HW.HEARTBEAT

```
HW.HEARTBEAT <nodeId>             →   +OK | +SESSION                 (liveness)
HW.HEARTBEAT <nodeId> BYE         →   +OK                            (departure)
HW.HEARTBEAT <nodeId> BYE PURGE   →   [groups, messages, bytes]      (retire forever)
```

**Form selection.** Second argument absent → liveness. `BYE` → departure; `BYE PURGE` → retirement. Anything else is `HW_INVALID_ARG` (5.0: registration moved to `HW.SESSION`).

#### Liveness form

Refreshes the node's liveness timestamp — an 8-byte write, independent of catalog size (4.12) — and nothing else. When the broker holds no registration for the node it replies **`+SESSION`** and writes nothing.

**`+SESSION` is correctness, not politeness.** A beat that simply recreated the timestamp would leave the node alive but undiscoverable — and, since 5.0 knows sessions carry subscriptions and schedules, unsubscribed and unscheduled too. Replying `+SESSION` makes a wiped or pruned registry self-healing: the client re-declares its whole session before its next beat.

`+SESSION` is a normal reply and must not be treated as an error.

#### Departure form

Announces that the node is shutting down, so it leaves discovery immediately rather than after the expiry window. Returns the node's claims on every service it hosted to their queues (attempts incremented) and removes it from discovery and the registry — the same teardown dead-node recovery runs. **Subscriber groups are not touched** — see [Invariants](#invariants). Idempotent: departing an unknown node returns `+OK`. A node that is killed rather than stopped simply expires on the normal timeline.

#### Retirement form (017)

`BYE` plus: the node is never coming back. For each group it belongs to, the membership is removed; a group whose **last** member this was is destroyed — its queue, deferred messages, claims and dead letters, in one range delete. Returns what it destroyed, `[groups, messages, bytes]`, so an irreversible act leaves a record. A group other members still back lives on.

### HW.DISCOVER

```
HW.DISCOVER <service>   →   [[nodeId, secondsSinceLastSeen], ...]
```

Returns the live nodes hosting a service: an array of `[nodeId, age-in-whole-seconds]` pairs, both bulk strings. Empty for an unknown service or one whose hosts are all stale — never an error. Stale nodes are filtered even before they are recovered. Read-only; a lookup, not a scan — the index is maintained at registration.

### HW.STATS

```
HW.STATS                →   kind server   nodes N services N channels N … maxPayloadBytes N [repl.*]
HW.STATS <service>      →   kind service  queueDepth N hosts N inFlight N deadLettered N
HW.STATS <channel>      →   kind channel  groups N pending N deadLettered N draining N
HW.STATS Q:<queue>      →   kind queue    depth N workers N inFlight N deferred N deadLettered N [draining T]
HW.STATS RECORDER       →   kind recorder enabled 1 names N events N bytes N droppedCapacity N droppedBudget N failures N observerFailures N
```

Operational counters. The reply is a **flat array of alternating field names and values**, all bulk strings — readable in `redis-cli`, parseable without a schema, extended by appending fields.

**Form selection.** No argument → server. `Q:<name>` → that queue (a group queue is `Q:{channel}@{group}`). `RECORDER` (reserved, case-insensitive) → the flight recorder. Any other name is a *service* if a live node's catalog hosts it, otherwise a *channel*; the `kind` field makes the resolution explicit. An unknown name returns zeroed counters, never an error. *(Known gap: a service name no live node hosts resolves as a channel.)*

| Form | Fields |
|---|---|
| server | `nodes` (live registrations), `services` and `channels` (distinct names across live catalogs), `groupsRetired`, `messagesDiscarded`, `sendsRefused`, `callDuplicates`, `sendDuplicates`, `publishDuplicates` (074: repeats acknowledged without storing, per broker process), `maxPayloadBytes` (4.10); a durable broker appends the `repl.*` fields (see [Replication Commands](#replication-commands)) |
| service | `queueDepth` (waiting requests), `hosts` (live nodes hosting it), `inFlight` (claimed, not yet acknowledged), `deadLettered` |
| channel | `groups` (registered subscriber groups), `pending` (waiting across all groups), `deadLettered`, `draining` (groups whose last member left, 070) |
| queue | `depth`, `workers` (nodes that have claimed), `inFlight`, `deferred`, `deadLettered`; a group queue (`Q:{channel}@{group}`) adds `draining` — `0`, or the UTC tick count its drain began (070) |
| recorder | `enabled`, `names`, `events`, `bytes`, `droppedCapacity`, `droppedBudget`, `failures`, `observerFailures` |

**Every count is exact and costs nothing to read** (5.0): each structure maintains its counters as it changes, so `HW.STATS` never scans stored data. Counters describe independently-mutating structures read at slightly different instants — fine for monitoring; do not build invariants on cross-field arithmetic.

### HW.UNSUBSCRIBE

```
HW.UNSUBSCRIBE <channel> <group>   →   +OK
```

Removes the group from the channel and **destroys its queue** — every waiting, deferred, claimed and dead-lettered message, in one range delete. Idempotent: an unknown group returns `+OK`. Highway's client never sends it (a group is meant to outlive its process, so a restart resumes its backlog); it is an operator tool for a group that is finished forever. A group removed this way is new again if it is subscribed again, and starts empty.

### HW.JOB

```
HW.JOB SET <queue> <job> <expression> <template>   →   +OK
HW.JOB DEL <queue> <job>                            →   :1 | :0
HW.JOB LIST                                         →   [[queue, job, expression, nextFireTicks, lastFireTicks], ...]
```

Recurring-job schedule management (028). A schedule fires **exactly one** occurrence message onto its queue at each due time and re-arms — the firing happens inside `HW.CLAIM` on that queue, decided by the broker's clock on the poll path. **No timer runs in the broker.** Clients declare their schedules in `HW.SESSION`; `SET` remains for operators.

| | |
|---|---|
| **Arguments** | `queue`, `job` — identifiers. `expression` — `daily:HH:mm`, `every:{seconds}` (≥ 1), or `cron:{m h dom mon dow}` (standard 5-field), all UTC. `template` — the occurrence payload, stored verbatim and replayed on every fire; up to `MaxPayloadBytes`. |
| **Reply** | SET `+OK`; DEL `:1` removed / `:0` unknown (idempotent); LIST an array of 5-field rows. |
| **Errors** | `HW_INVALID_ARG` for a malformed expression (the message names the accepted forms); `HW_PAYLOAD_TOO_LARGE`. Both permanent. |
| **Idempotency** | **SET is last-registration-wins**: re-registering replaces expression and template, preserves `lastFire`, and records `JobScheduleChanged` naming both expressions when they differ. |

**Fire semantics** (in `HW.CLAIM Q`): each due schedule enqueues one occurrence with id `job:{name}:{ticks}` and re-arms with `nextFire` computed **from now** — a schedule due five times while nothing polled fires once (catch-up-one). A queue at `MaxQueueBytes` refuses the fire without consuming it: `nextFire` is unchanged, `JobFireRefused` is recorded, and a later claim retries. A schedule that cannot be decoded is removed rather than retried forever, loudly (`JobScheduleRemoved`). *Exactly one fire, at-least-once processing*: the occurrence is an ordinary queue message.

---

## Idempotency Commands

Delivery is at-least-once: a worker that runs a handler and dies before its acknowledgement lands will see the same message again. Only the worker knows the handler ran, so at-most-once **handler invocation** — `[Idempotent]` contracts — is a gate the worker consults before running, keyed by `(scope, id)`: the service or queue name, and the request or message id.

### HW.IDEM

```
HW.IDEM BEGIN <scope> <id> <windowSeconds>           →   +CLAIMED | +BUSY | [DONE, result]
HW.IDEM END   <scope> <id> <windowSeconds> <result>  →   +OK
HW.IDEM ABORT <scope> <id>                           →   +OK
```

(5.0; replaces stock `SET NX EX` / `SETEX` / `GET` / `DEL` on `hw:idem:{scope}:{id}`.)

1. **`BEGIN`** — atomically claims the right to run. `CLAIMED`: run the handler. `BUSY`: another attempt holds it, or held it when its process died — **do nothing**: do not run, reply or acknowledge; treating a stale claim as "probably crashed, run it again" defeats the mechanism, and the claim lapses with the window. `[DONE, result]`: a previous run completed — reply with **those exact bytes** and acknowledge; the caller must not be able to tell.
2. **`END`** — after a successful run, replaces the claim with the result for the window.
3. **`ABORT`** — releases the claim when the handler could not run at all (a transport failure before any work), so the redelivery is not blocked for the whole window over something that never ran.

The window is not merely "how long duplicates are remembered": it is also how long a crashed run stays blocked — the right trade for a contract that has declared running twice worse than running late. Claims and results are deleted when their window ends.

**This deduplicates redeliveries and nothing else.** A caller issuing the same logical request twice produces two request IDs, and nothing here relates them.

---
## Dead Letter Commands

### HW.DLQ

```
HW.DLQ COUNT   SVC <service> | Q <queue>                                          →   :n
HW.DLQ PEEK    SVC <service> | Q <queue>   [COUNT n]                              →   array of dead letters (non-destructive)
HW.DLQ REQUEUE SVC <service> | Q <queue>   [COUNT n] [ID <deadId>] [AFTER <ms>]   →   :n | :1 | :0
HW.DLQ PURGE   SVC <service> | Q <queue>   [COUNT n] [ID <deadId>]                →   :n | :1 | :0
HW.DLQ RESOLVE <token> KEEP | DISCARD | REQUEUE [AFTER <ms>]                      →   :1 | :0
```

`COUNT` defaults to `ReceiveDefaultCount` and is capped at `ReceiveMaxCount`. `COUNT` and `ID` are mutually exclusive.

**Target grammar: `SVC` or `Q`.** A subscriber group's dead letters are accessed as `Q {channel}@{group}` — the derived queue name — because a group **is** a queue (feature 018). The `CH` form no longer exists.

**How entries get here.** Nothing writes a dead letter directly. A claim arrives here when `HW.CLAIM`'s lease recovery finds it has exceeded `MaxDeliveryAttempts` (or the target's configured `[Retry]` policy attempt limit), in the same commit that releases the claim. Each target keeps at most `MaxDeadLetterEntries`, oldest dropped first. When a feed is declared for the target, a notification is also pushed to the target's feed box.

#### Actions

- **`COUNT`** returns the integer count of dead letters currently in the target box without reading or parsing entries.
- **`PEEK` is non-destructive** and is listed first deliberately: the supported workflow is look, then decide. An operator who can only drain has to destroy the evidence in order to see it. Each entry is a flat field/value array — the same self-describing shape `HW.STATS` and `HW.REPLAY` use, so fields can be appended later without breaking readers:

```
deadId          <box:x8><seq:x16>
deadLetteredAt  <ISO-8601 UTC>
attempts        <n>
requeues        <m>
reason          MAX_ATTEMPTS
requestId       <id>            (SVC targets)
messageId       <id>            (Q targets)
payload         <bytes>
failureType     <type>          (optional, present if worker reported failure)
failureFirstType <type>         (optional, first failure type if morphed)
failureDetail   <bytes>         (optional, exception message and stack trace)
failure         <diagnostic>    (if no failure reported)
```

- **`REQUEUE` moves entries back to the live queue.** Can be called in batch mode with `COUNT <n>` or for a single entry with `ID <deadId>`. An optional `AFTER <ms>` delay reschedules the requeued message to delayed space (`now + ms`). Increments the entry's `requeues` count (persisted via `[0xFE][u16 requeues]` trailer) and resets the attempt count to zero. REQUEUE counts against the target's byte budget like any send. Operator requeues bypass `MaxRequeues` policies.
- **`PURGE` permanently drops dead letters.** Can be called in batch mode with `COUNT <n>` or for a single entry with `ID <deadId>`. Returns `:n` or `:1`/`:0`.
- **`RESOLVE` settles a feed notification claim** obtained via `HW.CLAIM DEAD <target> <node>`.
  - `KEEP`: Leaves the dead letter in DLQ storage for operator inspection and marks the feed notification resolved.
  - `DISCARD`: Purges the dead letter from DLQ storage and marks the feed notification resolved.
  - `REQUEUE [AFTER <ms>]`: Requeues the dead letter to the live target with an optional delay. If the target has a retry policy with `MaxRequeues` and the dead letter has reached that limit, the requeue is refused with `ERR HW_REQUEUE_LIMIT`, recording `DeadLetterRequeueRefused`.

**An unknown service or queue returns an empty array or `:0`, never an error**, matching `HW.DISCOVER` and `HW.STATS`.

**Recorded events:**
- `DeadLetterResolved`: emitted when a dead letter is resolved (via handler `HW.DLQ RESOLVE` or operator `HW.DLQ REQUEUE` / `HW.DLQ PURGE`).
- `DeadLetterRequeueRefused`: emitted when `HW.DLQ RESOLVE ... REQUEUE` is refused because the message exceeded `MaxRequeues`.
- `DeadLetterFeedOverflow`: emitted when a dead-letter notification cannot be enqueued to a feed box because the feed is at capacity.

---

---

## Observability Commands

Highway records what it does. Two independent mechanisms, either disableable:

- the **flight recorder** — a bounded, in-process record of recent operations, read with `HW.REPLAY`;
- **activity emission** — `System.Diagnostics.Activity`, which any OpenTelemetry pipeline collects.

### The flight recorder is volatile

**Its contents are lost when the server stops.** It holds events in ordinary process memory, not in the store, and nothing about it is written to disk or replicated.

That is deliberate. Storing events would make a debugging aid compete with the actual queues for the same store and replicate a volatile diagnostic to every standby. **Anyone needing a durable audit trail wants the activity/OpenTelemetry path**, exported continuously to a system built to retain it.

Recording is **best-effort**: a failure to record never fails, delays, or alters the operation being recorded.

### What is recorded

Recording happens per **name**, and each name has its own bounded buffer with its own retention and payload-capture mode. One high-volume name therefore cannot evict another's history.

A name is a service name, a channel name, a node ID, or one of the reserved names below. **A name must be drawn from a bounded set** — the recorder never removes a buffer once created, so recording under a per-request or per-message value would grow the recorder without limit for the life of the process. This is not hypothetical: `HW.REPLY` recorded under the request ID until it was corrected.

| Name | Source | Cardinality |
|---|---|---|
| service or queue name | `HW.CALL`, `HW.SEND`, `HW.CLAIM`, `HW.ACK`, `HW.FAIL` | number of services and queues |
| channel name | `HW.PUBLISH`, `HW.UNSUBSCRIBE`, subscriptions declared by `HW.SESSION` | number of channels |
| node ID | `HW.SESSION` registration, `HW.HEARTBEAT BYE` | number of nodes |
| `hw.replies` | `HW.REPLY` — **reserved** | one |

| Command | Event | Note |
|---|---|---|
| `HW.CALL` | `RpcEnqueued` | |
| `HW.CLAIM SVC` / `HW.CLAIM Q` | `RpcClaimed` / `QueueClaimed` | one per claimed message; an empty claim records nothing |
| `HW.REPLY` | `RpcReplied` | recorded under the reserved name **`hw.replies`**, not under a service. `HW.REPLY`'s arguments are a request ID and a payload, so the service that produced the reply is not on the wire and the command cannot know it. Query `HW.REPLAY hw.replies` and correlate by `requestId` |
| `HW.ACK` | `RpcAcknowledged` / `QueueAcknowledged` | by the claim's service or queue |
| `HW.SEND` | `QueueSent` / `SendRefused` | |
| `HW.FAIL` | `DeliveryFailed` | `errorCode` carries the exception type |
| `HW.PUBLISH` | `Published` | `count` carries the group count |
| `HW.SESSION` subscription / `HW.UNSUBSCRIBE` | `GroupRegistered` / `GroupRemoved` | |
| `HW.JOB SET` or `HW.SESSION` job / `HW.JOB DEL` | `JobScheduleChanged` / `JobScheduleRemoved` | `errorCode` carries the change detail |
| `HW.CLAIM Q` (fire) | `JobFired`, `JobFireRefused` | `requestId` = occurrence id; `errorCode` = job name |
| `HW.CLAIM` recovery | `RpcDeadLettered` / `QueueDeadLettered` | a claim exhausted its attempts; `count` carries the attempt count, `errorCode` the reason |
| `HW.SESSION` registration / `HW.HEARTBEAT BYE` | `NodeRegistered` / `NodeDeparted` | |
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
| **Store** | **None.** The recorder is not in the store, so this cannot contend with traffic. |
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
count        (group count for a publish; attempts for a dead letter)
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

### Metric emission

Highway also emits `System.Diagnostics.Metrics` and takes **no OpenTelemetry dependency** — the same posture as its `Activity` emission (feature 051). Highway defines the instruments; the application adds `OpenTelemetry.Metrics` plus whatever exporter it runs and subscribes to the meters:

| Meter | Emits |
|---|---|
| `Highway.Server` | broker operational metrics — replication, queue/pub-sub, RPC, connections, recorder |
| `Highway.Client` | caller-side metrics — RPC outcome/latency and followed failovers |

Instruments are **always defined** (no config gate) and cost nothing when no listener is attached — counters do a cheap `Add`, and the observable gauges sample the live feeder / store / recorder **only** when a listener collects. Every label is drawn from a bounded name set (queues, replicas, the four roles, an `ok|error` result) — never a per-request id.

**`Highway.Server` instruments**

| Instrument | Type | Unit | Labels |
|---|---|---|---|
| `highway.rpc.requests` | Counter | `{request}` | `result` |
| `highway.rpc.latency` | Histogram | `s` | `result` |
| `highway.messages.published` | Counter | `{message}` | — |
| `highway.messages.delivered` | Counter | `{message}` | — |
| `highway.messages.acknowledged` | Counter | `{message}` | — |
| `highway.messages.failed` | Counter | `{message}` | — |
| `highway.messages.refused` | Counter | `{message}` | — |
| `highway.messages.deadlettered` | Counter | `{message}` | — |
| `highway.replication.promotions` | Counter | `{event}` | — |
| `highway.replication.demotions` | Counter | `{event}` | — |
| `highway.replication.fences` | Counter | `{event}` | — |
| `highway.replication.role` | ObservableGauge | `{role}` | `role` (1 for the current role, 0 for the others) |
| `highway.replication.epoch` | ObservableGauge | `{epoch}` | — |
| `highway.replication.slots` | ObservableGauge | `{slot}` | — |
| `highway.replication.lag_sequences` | ObservableGauge | `{sequence}` | `replica` |
| `highway.replication.rpo_seconds` | ObservableGauge | `s` | — |
| `highway.queue.depth` | ObservableGauge | `{message}` | `queue` |
| `highway.queue.bytes` | ObservableGauge | `By` | `queue` |
| `highway.deadletters` | ObservableGauge | `{message}` | `queue` |
| `highway.connections` | ObservableGauge | `{connection}` | — |
| `highway.recorder.dropped` | ObservableGauge | `{event}` | — |
| `highway.storage.memtable_deletes` | ObservableGauge | `{deletion}` | — |
| `highway.storage.estimated_keys` | ObservableGauge | `{key}` | — |

The replication instruments are present only on a durable (RocksDB) broker; an in-memory broker has no feeder, so they report nothing.

**`Highway.Client` instruments**

| Instrument | Type | Unit | Labels |
|---|---|---|---|
| `highway.client.rpc.requests` | Counter | `{request}` | `result` |
| `highway.client.rpc.latency` | Histogram | `s` | `result` |
| `highway.client.failovers` | Counter | `{failover}` | — |

Metrics add no command, reply or error to the wire, so they carry **no protocol version**. The instrument names, types, units and labels above are the stable contract.

---

## Replication Commands

The primary ships its WAL to warm-standby replicas over RESP. Pull is replica-driven and stateless per request — the replica owns its cursor; the primary owns slots. Replicas serve no `HW.*` client traffic. See [Replication & Failover](design/replication-and-failover.md) for the failover control model — client-herd mastership, the handshake, the roster.

**Client bootstrap.** A client's connection string may name several endpoints in the ordinary SE.Redis comma form — `host1:6500,host2:6500,password=…`. The string is **bootstrap only**: enough to reach some node; the live roster (`HW.REPL.STATUS` `roster.*`) is the running truth for successor order, so the cluster can grow beyond any client's original string. A single-endpoint string is unchanged behaviour.

These commands require a durable RocksDB broker. An ephemeral (in-memory) instance refuses with `ERR HW_INVALID_ARG`.

A node that is not writable (role `Replica`, `Fenced`, or `Demoted`) refuses mutating `HW.*` client commands with:

```
-NOTPRIMARY <advertise-endpoint> <epoch>
```

`HW.STATS`, `HW.DISCOVER`, `HW.REPLAY`, `HW.REPL.HELLO`, and the `HW.REPL.STATUS` / `PROMOTE` / `FENCE` / `WITNESS` / `GOODBYE` admin forms still run. A fenced primary additionally still serves `PULL` / `ACK` / `SNAPSHOT` so a replica can catch up.

**During a GOODBYE drain** (042-1) the quiescing master serves the **completion** verbs — `HW.REPLY`, `HW.RESULT`, `HW.ACK`, `HW.FAIL`, `HW.TOUCH`, `HW.IDEM` — plus reads and WAL shipping, while refusing new work (`HW.CALL`, `HW.SEND`, `HW.PUBLISH`, `HW.CLAIM`, `HW.SESSION`, job admin) with `-NOTPRIMARY`, so in-flight work drains and the herd converges.

The server-wide `HW.STATS` form appends `repl.role`, `repl.epoch`, `repl.endpoint`, `repl.priority`, `repl.fenced`, `repl.slots`, `repl.minAcked`, `repl.latestSeq`, `repl.lastPromotion`, `repl.lastPromotionReason`, `repl.reconciliation`, `repl.drops`, `repl.ready`, `repl.readyReason` (052), and per-slot `repl.slot.N.{id,acked,state,lag}` fields. (Self-describing and append-only — a reader ignores fields it does not know, and 050/052 added others: `repl.redundancy`, `repl.leadershipSince`, `repl.clients`, `repl.draining`, `repl.resyncRequired`.)

### HW.REPL.HELLO

```
HW.REPL.HELLO <replicaId> <lastAppliedSeq> <epoch> [endpoint] FORMAT <n>   →   [primaryEpoch, minSeq, format]
HW.REPL.HELLO CLIENT <clientId> <lastSeenEpoch>                 →   [role, a, b]   (three bulk strings)
HW.REPL.HELLO PEER <nodeId> <lastSeenEpoch>                     →   [role, a, b]   (as CLIENT)
```

**Replica form.** Register or resume a retention slot. `lastAppliedSeq` is the replica's derived watermark (`GetLatestSequenceNumber` after the last applied page). `epoch` is the highest epoch the replica has seen. The optional `endpoint` names where the caller is reachable — a **promoting node announces itself** with it (epoch+1 plus its endpoint), which is how a demoted ex-primary learns where to point its `-NOTPRIMARY`. The reply's `minSeq` is the retention floor: the minimum acked watermark across live slots (0 when this is the only / first slot). This form is served in **every** role: it is the epoch/endpoint gossip channel.

**`FORMAT <n>` (5.0)** declares the caller's storage format. A replica applies the primary's WAL byte for byte, so the formats must match; a replica form without `FORMAT`, or with another number, is refused `ERR HW_STORAGE_FORMAT` naming both formats — a 2.x node can never pull a 3.0 primary's batches into its old keyspace. `endpoint` and `FORMAT` may come in either order. The reply's third element is the **primary's** format, and a replica refuses to pull from a primary whose reply lacks it or names another: a 2.x primary ignores `FORMAT` and answers two integers, and its batches must never reach a 3.0 store.

**Client form (the herd handshake, 042-1).** The literal `CLIENT` disambiguates (it is also why `CLIENT` can never be a replica id). The reply is three bulk strings, keyed by the first:

| `role` | `a` | `b` | Meaning |
|---|---|---|---|
| `master` | epoch | rosterVersion | This node is serving the herd — connect. |
| `willing` | epoch | rosterVersion | A standby whose own master-link is dead past `W` (or that saw GOODBYE) — it will accept the herd. Answering never promotes; promotion happens on the first accepted client verb. |
| `standby` | masterEndpoint | masterEpoch | Unwilling — its master-link is healthy. Go where it points. One confused client cannot move mastership. A **draining** master (GOODBYE in progress) also answers `standby` — it is leaving, and must not pull the walking herd back onto itself; its self-redirect is a signal to walk on. |

A `lastSeenEpoch` above the node's own is adopted (the any-channel epoch rule).

**Peer form (5.0, 069).** `PEER` answers exactly as `CLIENT`, but the connection is a **peer session**: the probed node neither counts it as a herd client nor takes it as master contact. A standby that has lost its master sends it to every higher-priority roster peer and answers a client `willing` only when none of them answered `master` or `willing`, or `standby` with a redirect to someone other than itself or the asker; a peer with no answer in the last few seconds counts as alive. Otherwise the standby answers `standby`, redirecting to that peer. Like `CLIENT`, `PEER` can never be a replica id.

**Equal-epoch tie-break (5.0, 069).** A primary that receives a replica-form HELLO carrying an endpoint at its **own** epoch is hearing a second primary's promotion announce. If the roster gives the caller a higher priority, this node demotes and schedules its rejoin as the caller's replica; otherwise it keeps its role and the caller yields on hearing this node's announce.

Arity -4. Two forms.

### HW.REPL.PULL

```
HW.REPL.PULL <fromSeq> <maxBytes>   →   [epoch, [[seq, data], ...], nextSeq | *-1]
```

A page of WAL batches **following** `fromSeq` — the replica's applied watermark. The first batch begins at exactly `fromSeq + 1` (4.13): batches the replica already holds are not re-served, and a WAL record that spans the cursor is trimmed to start there. Each inner pair is the batch's first sequence number and its `WriteBatch` bytes (header sequence equal to it). A batch is one committed write or several merged by group commit; boundaries may differ between pulls of the same range, sequences and contents never do. A pull at the primary's latest sequence returns an empty page. `nextSeq` is the first sequence **not** included in this page; `*-1` (null array) means the iterator is drained — the replica is caught up until the next write.

`maxBytes` bounds the page. A single batch larger than `maxBytes` is still returned so the cursor can advance.

The epoch is the primary's. A replica that has seen a higher epoch refuses the page (T2).

**A cursor the retained WAL no longer reaches is refused** with `ERR HW_REPL_GAP requested=<n> firstAvailable=<m>; re-sync via HW.REPL.SNAPSHOT` — in both shapes (first available batch past the cursor, or an empty live-WAL while the node's sequence is ahead). A gapped stream is never served as contiguous; the replica re-bootstraps.

Arity 3.

### HW.REPL.ACK

```
HW.REPL.ACK <replicaId> <appliedSeq>   →   +OK
```

Advance the slot's acked watermark. WAL files are eligible for deletion only past the minimum acked watermark of registered replicas, and never past the configured cap (`SetWalTtlSeconds` / `SetMaxTotalWalSize`). A replica id with no slot (no prior `HELLO`) is `ERR HW_INVALID_ARG`. A slot whose lag exceeds `SlotLagCapSequences` is dropped with a named event; that replica re-bootstraps via `HW.REPL.SNAPSHOT`.

Arity 3.

### HW.REPL.SNAPSHOT

```
HW.REPL.SNAPSHOT BEGIN                                              →  [epoch, checkpointSeq, sessionId, [[fileName, size], ...]]
HW.REPL.SNAPSHOT GET <sessionId> <fileName> <offset> <maxBytes>     →  [fileName, offset, data, nextOffset | *-1]
HW.REPL.SNAPSHOT END <sessionId>                                    →  +OK
```

Streams a RocksDB checkpoint captured with `ReplicationSource.GetInitialState` (DisableFileDeletions only for the capture window). The replica writes files then opens and tails from `checkpointSeq`. `GET` is resumable: retry the same `fileName` at `nextOffset`. A single chunk larger than `maxBytes` is not produced — the server caps a chunk at 64 KiB internally when the caller asks for more.

Arity -2. Three forms.

### HW.REPL.PROMOTE

```
HW.REPL.PROMOTE [reason]   →   :epoch
```

Become writable and increment the epoch. A node with replica priority `0` refuses with `ERR HW_INVALID_ARG`. Already-primary is a no-op that returns the current epoch. The standalone host also exposes this as `highways --promote [reason]` (036-style verb) against a running broker.

Arity -1.

### HW.REPL.FENCE

```
HW.REPL.FENCE [reason]   →   +OK
```

Primary becomes read-only without incrementing the epoch. Peer contact within `FenceTimeout` unfences. A no-op if the node is not primary. The fence is a **backstop** (042-1): failover itself is client-herd-driven, and no timer ever promotes.

Arity -1.

### HW.REPL.STATUS

```
HW.REPL.STATUS   →   flat field/value array (same `repl.*` keys as `HW.STATS`, plus the roster)
```

042-1 appends the live roster: `roster.version`, then per member `roster.N.id`, `roster.N.priority`, `roster.N.endpoint`. This is the client's read of the running truth for successor order — the connection string is bootstrap only.

Arity 1.

### HW.REPL.JOIN

```
HW.REPL.JOIN <nodeId> <priority> <endpoint>   →   :rosterVersion
```

A starting node announces itself into the set (042-1 / parent R13). **Master-only** — elsewhere it draws the ordinary `-NOTPRIMARY`, which is itself the joiner's redirect to the master. The master admits the node into the roster (stored as replicated state, so every standby already holds it) and returns the new roster version. A priority already held by a **different** live member is refused:

```
ERR HW_PRIORITY_TAKEN priority=<p> holder=<nodeId>; fix this node's config
```

First announcer wins — succession order must never depend on restart order. A re-announce by the same `nodeId` updates its endpoint/priority (subject to the same collision rule).

Arity 4.

### HW.REPL.GOODBYE

```
HW.REPL.GOODBYE [reason]   →   +OK
```

Begin a graceful departure (042-1 / parent R12): the node narrates `GOODBYE <epoch>` on `hw:door:topology` (pure timing — the successor is already common knowledge from the roster), stops accepting new master-only work, lets in-flight complete (see the drain rules above), and stands down at the drain deadline (`GoodbyeDrainTimeout`, default 5s) or as soon as its herd has left — whichever comes first. Anything undrained falls back to the clients' ordinary replay path; a maintenance departure is never blocked by one stuck message. Idempotent; a no-op on a non-primary. The standalone host exposes it as `highways --goodbye [reason]`.

Arity -1.

### HW.REPL.WITNESS

```
HW.REPL.WITNESS   →   +OK
```

A bare liveness probe. *(A 4.7-era sketch gave this command a peer-question form; it was withdrawn, unreleased, by 042-1a — failover is client-herd-driven and no witness gates promotion.)*

Arity 1.

---

## Served RESP Commands

The broker serves the `HW.*` set plus exactly the stock commands below, and nothing else: an unrecognized command receives `ERR HW_INVALID_ARG unknown command '<name>'. This is a Highway broker; served commands are the HW.* set: …` (naming the set), never a plausible `+OK`.

| Command | As-built behaviour |
|---|---|
| `AUTH` | Both forms (`AUTH password`, `AUTH user password`). Failure is `-WRONGPASS`. **Pre-auth gate:** an unauthenticated connection may issue only `AUTH` and `QUIT`; everything else — `PING` included, because `PING` is StackExchange.Redis's connect handshake — answers `-NOAUTH Authentication required.` |
| `QUIT` | `+OK`, then the connection closes. Allowed in every state. |
| `PING` | `+PONG`, or a bulk echo of its argument. Post-auth only. |
| `HELLO` | RESP2 only: `HELLO` / `HELLO 2` answer a minimal server map; `HELLO 3` is refused with `NOPROTO`, and StackExchange.Redis falls back to RESP2. |
| `ECHO` | Bulk echo. |
| `SELECT` | `+OK` for any index — a single logical database. |
| `CLIENT` | `+OK` for every subcommand. `CLIENT SETNAME <node>@<instance>` names the connection: `<node>` is the node name with every character outside `[A-Za-z0-9-_.]` as `_`, `<instance>` the sending process's instance id — 12 lowercase hex characters, random per process (072). The broker splits on the **last** `@` (a node name cannot contain one); a suffix that is not an instance id is part of the name, and a name without one is a single "unidentified" process. The broker uses it to show where a node connects from and **how many processes** use each node name, and records `NodeNameShared` (logged at Warning) when more than one does. It is otherwise accepted and ignored. |
| `COMMAND` | Empty array — no command catalogue. |
| `CONFIG` | `CONFIG GET …` answers an empty array (StackExchange.Redis probes this on connect); any other subcommand `+OK`. Never an error, which would abort a client's connect. |
| `INFO` | A minimal bulk string declaring a standalone master, for server-type detection. |
| `SUBSCRIBE` / `UNSUBSCRIBE` | Doorbell channels only. A subscribed connection is in RESP2 restricted mode: only `SUBSCRIBE`, `UNSUBSCRIBE`, `PING`, `QUIT`. `PUBLISH` is **not** served — doorbells are rung server-internally. |
| `GET` | The opt-in cache: `hw:cache:*` when `server.cache.enabled` (a miss on a non-master). Any other key answers a null bulk — the honest reply to StackExchange.Redis's tiebreaker probe; there is no general keyspace. |
| `SET` / `SETEX` / `PSETEX` | Cache keys only, when enabled (`EX`/`PX` → TTL, `defaultTtl` when absent, clamped to `maxTtl`; `NX` is ignored). Any other key is refused with `ERR HW_INVALID_ARG` naming the boundary. |
| `DEL` / `UNLINK` | One cache key, when enabled; answers `:1`. Any other key is refused. |
| `TTL` / `PTTL` | Cache keys, when enabled: `-2` no key, `-1` no expiry, else remaining. |

`KEYS`, `SCAN`, `EVAL`, transactions, cluster commands and streams are not served — the broker serves its protocol, not a keyspace. Since 5.0 reply slots and idempotency are reached only through `HW.RESULT` and `HW.IDEM`; no stock command reads or writes Highway state.

**The cache family is opt-in and broker-local** (044). `hw:cache:*` is served only when `server.cache.enabled` is set. It is backed by a **separate store that replication never ships**, and wiped on any epoch change (a mastership move means another node may have changed the system of record). It is a cache: a miss is one more trip to the system of record, never data loss. `HybridCache` writes its L2 in the background, so an immediate L2 read after `GetOrCreateAsync` may still miss. See [`constraints.md`](product/constraints.md) C10.

### Authentication and transport security

A client authenticates exactly as against any RESP server, with `AUTH`.

**Password authentication.** A secured broker has exactly one user, `default`. A client may send the password alone or pair it with that username; anything else is refused.

**When authentication is required.** Highway refuses to build a broker bound to a **non-loopback** address with no authentication, unless the operator explicitly opts out. Loopback is exempt: running open is the right configuration for development, and a loopback-bound broker is reachable only by processes already on the machine.

**TLS is never required**, but strongly recommended wherever a password crosses a network — RESP `AUTH` sends it as an ordinary bulk string.

---

## Storage Layout & Stored Records

The storage layout is private to the broker — nothing on the wire names a stored key since 5.0 — and is documented in [Storage Engine](design/storage-engine.md) and [Durable Queues](design/durable-queues.md). What a client or operator needs to know:

- **Storage format 4** (Highway 3.0, feature 065): every service, queue and group queue lives in its own contiguous key range (a *box*), found through a catalog; creating one is a catalog write, retiring one is a single range delete, and every count is a maintained counter. Registry, membership, reply slots and idempotency state live in a system range. The format is stamped in `highway.format` in the data directory.
- **No in-place migration.** A 3.0 broker refuses a 2.x (format 3) data directory — by stamp, and by recognising 2.x keys even without one — rather than serving it as an empty, healthy-looking store. Drain it with 2.x, then `highways --wipe-data` (keeps the node's replication identity) or delete it; in a replica set the upgraded node starts empty and the other re-syncs from it.
- A replica applies its primary's WAL byte for byte, so both must store the same format: `HW.REPL.HELLO` declares it and a mismatch is refused.

### Stored records

All multi-byte integers are **big-endian**. All lengths are in bytes. Timestamps are .NET UTC tick counts (100-nanosecond intervals since 0001-01-01).

| Record | Layout |
|---|---|
| Queue entry (waiting or deferred work, any kind) | `[u8 0xFF][u16 attempts][u16 idLen][id][payload]` + optional failure block + optional lineage trailer |
| Claim | `[u8 1][i64 claimTicks][u16 nodeLen][node][queue entry]` |
| Dead letter | `[i64 deadLetteredTicks][u16 attempts][u16 reasonLen][reason][queue entry]` |
| Registration | `[i64 registeredTicks][catalog json]` — liveness beats go to a separate 8-byte timestamp (4.12) |
| Job schedule | `[u8 1][u16][name][u16][expression][i64 lastFire][i64 nextFire][template]` |

**The claim wraps the queue entry unchanged**, so a message moves between waiting, claimed and dead-lettered without re-encoding its payload or losing its failure block.

**The delivery attempt count** bounds redelivery. It is incremented when a claim is recovered — its lease expired, or its node was declared dead — not when the message is first enqueued: a message delivered once and acknowledged has one attempt, not two. A message that would exceed `MaxDeliveryAttempts` is dead-lettered instead. The count lives *in the entry* so that incrementing it is atomic with the move that caused it, and saturates at `u16` maximum rather than wrapping, because a wrapped counter silently restores unbounded retry.

**The version byte.** Entries begin with `0xFF`; an entry written before 013 is refused with `HW_STORAGE_FORMAT` rather than misparsed into a corrupt payload.

#### The failure block

An optional **trailer** on a queue entry (015):

```
<entry>  [block][u32 blockLen][u32 magic 0xFE15FA11]
block := [u16 typeLen][type][u16 firstTypeLen][firstType][u32 detailLen][detail]
```

Read from the **end**, which is what makes it additive: every framing ends with "payload is the rest", so an entry written without a block decodes byte-for-byte as it always did. It rides through every move — claim, recovery, dead-lettering — so `firstType` is never lost on redelivery. `firstType` is empty unless the failure changed shape; once set it is never overwritten. A payload would have to *end* with the eight trailer bytes to be misread as carrying a block; the magic's last byte is deliberately not `}` (a JSON envelope's last byte), and the declared length is bounds-checked as well.

#### The lineage trailer

An optional **trailer** tracking requeue lineage (073):

```
<entry>  [u8 0xFE][u16 requeues]
```

Read from the **end** (preceding the failure block if present): when a dead letter is requeued (via operator `HW.DLQ REQUEUE` or dead-letter handler `HW.DLQ RESOLVE <token> REQUEUE`), its requeue counter is incremented and encoded as a trailing trailer `[0xFE][u16 requeues]`. This tracks requeue lineage across multiple dead-letter and retry cycles without altering the entry's primary payload or framing.

---
## Doorbell Channels

Doorbells are RESP pub/sub messages that wake waiting clients so they need not poll. They are **best-effort by contract**: a client that never receives one must still make progress by polling. Correctness never depends on delivery.

| Channel | Rung by | Payload |
|---|---|---|
| `hw:door:svc:{service}` | `HW.CALL` | `requestId` |
| `hw:door:rep` | `HW.REPLY` | `requestId` |
| `hw:door:q:{channel}@{group}` | `HW.PUBLISH`, once per group | `messageId` |
| `hw:door:q:{queue}` | `HW.SEND` | `messageId` |
| `hw:door:dlq:{targetKind}:{targetName}` | `HW.CLAIM` lease recovery (dead-lettering) | `deadId` (`{box:x8}{seq:x16}`) |
| `hw:door:topology` | topology changes (042-1) | one of the three narration messages below |

Doorbells are rung **after** the command commits, never during recovery, and a rejected command rings nothing. A client must re-subscribe its doorbells after it follows a failover to another node (Highway's client does, for every doorbell including queue doorbells — before 3.0 queue doorbells were lost and those workers fell back to backstop polling).

**The narration channel (042-1).** `hw:door:topology` carries the master's advisory topology pushes — single-line, space-delimited:

```
TOPOLOGY <rosterVersion>                                   # re-read the roster, re-run your successor rule
GOODBYE <epoch>                                            # I am leaving now; converge (no successor named — the roster is common knowledge)
ROSTER-UPDATE <rosterVersion> <nodeId> <priority> <endpoint> JOINED|LEFT
```

Every narration message is **advisory**: a client re-runs its own rule and may stay put; a wrong or lost push is harmless because `-NOTPRIMARY` refusal and the TCP-drop walk remain the guarantee. Narration is never a vote.

`hw:door:rep` is a single node-global channel rather than one per request, keeping subscriptions O(1) per node instead of O(pending calls). The cost is that every client sees every reply notification and must filter — see [`HW.RESULT`](#hwresult).

---

## Invariants

Guarantees that span commands. Each names the test that enforces it, so a reader can see it is guaranteed rather than merely intended.

### RPC: reply strictly before acknowledgement

A worker must send `HW.REPLY` **before** `HW.ACK`.

If it crashes between the two, the reply is already delivered and the lease eventually returns the request for redelivery — the caller gets an answer either way. Reversed, a crash between acknowledgement and reply loses the request entirely: it is no longer claimed, so nothing will redeliver it, and the caller waits out its timeout for a reply that will never come.

*Enforced by* `RpcWorkerLoopTests.Process_SendsReplyBeforeAck`.

### Pub/Sub: acknowledge only after dispatch completes

A consumer must send `HW.ACK` only **after** the message has been fully dispatched to its handlers.

Acknowledging first turns a crash mid-dispatch into silent loss. Acknowledging last turns it into redelivery, which at-least-once permits.

*Enforced by* `SubscriptionWorkerLoop`'s structure: `ProcessAsync` calls `ExecuteSubscribersAsync` first, then `AckAsync(token)` only if all handlers succeed.

### A schedule fires exactly once per occurrence (028)

The fire — enqueue one occurrence, advance `nextFire` — is one commit inside `HW.CLAIM`,
under the queue's lock. However many workers poll at the due instant, one fires; the others
find the schedule already re-armed. The claim is the election.

*Enforced by* `RecurringJobTests.DueSchedule_FiresExactlyOneOccurrence_UnderRacingPollers`,
verified against deliberately broken re-arm.

### A group dies only when its last member is gone (025)

A subscriber group's queue is destroyed exactly when its **membership reaches zero** — by
`BYE PURGE` from the last member, by `HW.UNSUBSCRIBE` (which is explicit and total), or by
automatic retirement once **every** member's heartbeat is older than
`SubscriberRetirementThreshold`. One live member keeps the group — and every sibling's
pending messages — alive. A group with no membership record (pre-025) is measured by the
017 rule: its name is treated as a node name.

*Enforced by* `SubscriptionGroupTests.Purge_FromNonLastMember_PreservesTheGroup` and
`SubscriptionGroupTests.Retirement_SparesGroupWithOneLiveMember`.

### Pruning requeues RPC work but never deletes subscriber groups

Removing a node — by expiry or by `BYE` — requeues its unacknowledged RPC requests and removes it from worker sets, the discovery index, and the registry. It **never** deletes that node's subscriber groups or their pending messages.

The asymmetry follows from the delivery model. RPC work is *claimed*: if the claimant dies, the claim must be released or the work is stranded. Pub/sub messages are *addressed* to a group: a node being down is not a reason to discard mail addressed to it, and the group is expected to outlive the process so a restart resumes its backlog.

Deleting groups on prune would silently convert Highway's durable pub/sub into fire-and-forget for any node that outlives its expiry window. This is the invariant most at risk from a future change that looks like tidy housekeeping.

*Enforced by* `NodeExpiryTests.DeadNode_UnacknowledgedRequests_AreRequeuedNotLost` and `NodeExpiryTests.DeadNode_SubscriberGroupAndPendingMessages_Survive`.

### At-least-once for both RPC and pub/sub

Neither path is exactly-once. A request can be executed more than once (reply sent, acknowledgement lost, lease requeues it) and a message can be delivered more than once (dispatch completed, `HW.ACK` lost). `[Idempotent]` contracts use [`HW.IDEM`](#idempotency-commands) to run a handler at most once per delivery id.

**Duplicate handling is the application's responsibility.** Handlers should be idempotent.

*Enforced by* `RpcFlowTests`, `LeaseRecoveryTests`, and `NodeExpiryTests`.

### Group identity defaults to the node name, and groups outlive the process

A subscriber group is identified by `SubscriptionGroup`, which defaults to the consuming node's name. Every group receives its own copy of every message, so N nodes in N groups produce N deliveries; N nodes sharing one group compete for one copy (025).

The client never sends `HW.UNSUBSCRIBE`. A node that stops leaves its group registered, so messages published while it is down accumulate and drain when it returns under the same name. This is what makes "a message published with no online subscriber is delivered when the subscriber eventually starts" true across process restarts.

The corollary: **two live processes sharing a node name (with the default group) share a group**, and will compete for messages rather than each receiving a copy. Node names must be unique per running instance.

*Enforced by* `PubSubIntegrationTests.Subscriber_StopsAndRestartsWithSameNodeName_DrainsMessagesPublishedWhileDown`.

### A liveness beat never resurrects a node — it asks for the session

If the broker holds no registration for the node, the liveness form replies `+SESSION` and writes nothing. It never recreates a record from a bare beat, because without the catalog it cannot rebuild the discovery index, and without the session it cannot restore the node's subscriptions and schedules — a node that is live but absent from the index, unsubscribed and unscheduled is worse than one that is plainly gone. The client re-declares its whole session at once.

*Enforced by* `RegistryTests` (liveness for an unknown or departed node asks for a session and mutates nothing) and `HeartbeatLoopTests.SessionReply_ReDeclaresTheWholeSession_AtOnce`.

### A token names exactly one claim

Claim sequences are never reused, so a token is valid for one claim of one message, once. After a lease expiry the redelivered message carries a new token; the late holder's `HW.ACK`, `HW.TOUCH` or `HW.FAIL` answers `:0` and never settles, renews or annotates the redelivery.

*Enforced by* `WorkProtocolTests.AStaleToken_AfterLeaseRecovery_DoesNotSettleTheRedelivery`.

---

## Server Options

Options that change observable protocol behaviour. All are server-side.

| Option | Default | Effect |
|---|---|---|
| `MaxPayloadBytes` | 5 MiB (max 15 MiB) | Payload cap for `HW.CALL`, `HW.REPLY`, `HW.PUBLISH`, `HW.SEND`, and job templates. Exceeding → `HW_PAYLOAD_TOO_LARGE`. A configured value above the 15 MiB ceiling is refused at build (a message is buffered whole in memory). The server advertises the effective value on the `HW.STATS` server form so clients track it. |
| `MaxIdentifierBytes` | 256 | Identifier length cap. Exceeding → `HW_INVALID_ARG`. |
| `MaxCatalogBytes` | 256 KiB | Catalog cap for `HW.SESSION`. |
| `Lease` | 5 minutes | How long a claim may go unacknowledged (or unrenewed by `HW.TOUCH`) before redelivery. `TimeSpan.Zero` disables lease recovery. |
| `ReplySlotTtl` | 5 minutes | How long a reply waits for `HW.RESULT` before it is deleted. Should comfortably exceed the client's call timeout. |
| `NodeExpiry` | 30 seconds | How long a registration stays valid without a beat. What matters is the **ratio** to the client's heartbeat interval; the defaults give 6×. Below about 3×, ordinary GC pauses cause false staleness. |
| `PruningEnabled` | `true` | When `false`, stale nodes are still excluded from discovery but their claims are recovered only by the slower lease recovery, never by dead-node recovery. |
| `ReceiveDefaultCount` | 10 | `HW.DLQ` `COUNT` when omitted. |
| `ReceiveMaxCount` | 500 | Maximum accepted `COUNT` for `HW.CLAIM` and `HW.DLQ`. Above → `HW_INVALID_COUNT`. |
| `MaxDeliveryAttempts` | 5 | Deliveries before an entry is dead-lettered instead of requeued. `0` means unlimited, restoring the pre-013 behaviour in which a permanently failing message is redelivered forever. |
| `MaxDeadLetterEntries` | 10,000 | Per-target dead-letter cap; oldest dropped first. |
| `PubSubBackoffEnabled` | `false` | A pub/sub message returned after a lease expiry waits a growing delay before becoming claimable again. **Off by default because backoff and head-of-queue ordering are mutually exclusive**: holding a failed message serves the messages behind it first, and redelivery-preserves-order is a documented guarantee. Enable it where pacing matters more than order. |
| `RpcBackoffEnabled` | `false` | The RPC equivalent. Off because a caller waits against `CallTimeout` (30 s) while `Lease` defaults to 5 minutes — the caller has already given up before a retry is possible, so a delay changes nothing but when the dead-letter happens. Worth enabling only where `Lease` is tuned well below the call timeout. |
| `MaxBackoff` | 1 minute | Upper bound on the retry delay. The cap matters more than the curve: an uncapped exponential reaches hours by the twelfth attempt, when the message is functionally dead but still occupying a live queue. |
| `Replication.StartAsReplica` | `false` | Node starts not-writable and pulls from `PrimaryServer`. |
| `Replication.Priority` | 100 | Lowest non-zero replica promotes first; `0` never promotes. |
| `Replication.SlotLagCapSequences` | 100000 | Drop a slot whose acked watermark lags the primary by more than this. |
| `Replication.AutoFailover` | `false` | Two-timeout deadman. Refuses to start unless `PromoteTimeout > FenceTimeout + Margin`. |
| `Replication.FenceTimeout` | 5s | Primary fences itself after this long without replica or witness contact (OD1). |
| `Replication.PromoteTimeout` | 8s | Replica may self-promote after this long of primary silence (OD1). |
| `Replication.Margin` | 1s | Clock-rate / scheduling slack (OD1). |

Durability options (`DataDir`, `WaitForCommit`) affect whether state survives a restart but do not change any command's contract. With no data directory the server is memory-only and all state is lost on shutdown — including every session, which is what the `+SESSION` re-declaration recovers from.
