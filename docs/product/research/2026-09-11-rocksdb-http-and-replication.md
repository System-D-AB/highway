# Research: Highway on RocksDB — a new engine, an HTTP protocol, and replication

**Date:** 2026-09-11
**Status:** **Exploratory.** Nothing here is approved and no code has changed. This is a
feasibility assessment and a design record, written so the reasoning survives the session it
came from. A feature spec, if one follows, is a separate act under the
`docs/features/{NNN}-*` workflow.
**Question:** Can Highway be rebuilt on RocksDB with an HTTP protocol, and does that open a
path to removing the single-broker risk through replication?

> **Sequencing decided 2026-09-11.** **Stage 1 (engine + protocol) comes first and stands on its
> own.** Replication is stage 2 and is deliberately deferred until stage 1 has settled and its
> bugs are out. The precedent supports the patience: Redis shipped replication in 2010, but
> Sentinel did not stabilise until 2.8 (2013) and Cluster until 3.0 (2015) — and it still
> acknowledges writes that can be lost on failover. Five-plus years, and the hard part was never
> the replication. It was arbitration (Part VI.3).
>
> Part VII.2 is written against this decision: two of its four rules earn their place on
> single-node merit and two can wait.

> ## RESOLVED 2026-09-11 — the protocol does not change
>
> **RESP over TCP stays. SE.Redis stays. HTTP is rejected.** Recorded as **D1** in
> [`../../features/037-rocksdb-engine/requirements.md`](../../features/037-rocksdb-engine/requirements.md) — spec **037-rocksdb-engine**, which is what
> gets built from this document.
>
> This document was written HTTP-first and the analysis is kept as written, because its value is
> explaining why the decision went the way it did. **Where a section still recommends HTTP, it is
> superseded** — III.7 carries the correction inline. Three arguments for HTTP did not survive:
> multi-language clients (moot once .NET-only was settled, Part VIII), reverse-proxy failover
> (withdrawn in VI.3 — Highway is a standalone broker), and a free client transport (SE.Redis is
> equally free and already integrated).
>
> **The decisive reason was none of those.** Keeping RESP makes the work **engine-only**:
> `HW.QCLAIM` stays `HW.QCLAIM`, `Highway.Client` is untouched, `HIGHWAY-PROTOCOL.md` stays true.
> One variable changes, so a red assurance run points at the storage layer instead of at three
> simultaneous changes.

> **Origin.** This came out of a conversation that started somewhere else entirely — whether a
> sibling project (StowDB) could host Highway's queue semantics. It could not, and should not,
> but the investigation surfaced something more useful: **StowDB's Garnet post-mortem is a
> direct read-out of Highway's own architecture**, because both put multi-member collections in
> Garnet's object store under exclusive locks. That is Part I.

## Verdicts at a glance

| Question | Verdict |
|---|---|
| Is Garnet the cause of Highway's hardest unmet constraint? | **Probably yes** — same root cause StowDB measured independently |
| Can Highway run on RocksDB? | **Yes.** The entire storage dependency is **15 primitives at ~106 call sites** |
| How much of the server survives? | **Most of it.** ~5 000 lines of command logic port; the Garnet transaction scaffolding is *deleted*, not translated |
| Can the protocol be HTTP? | **Yes** — and **rejected anyway**, see the banner above. The client is shaped for it; that turned out not to be the deciding question |
| Does HTTP weaken durability or fan-out? | **No.** Both already happen inside the broker's transaction, not on the connection |
| Does HTTP weaken pub/sub across machines? | **No.** Fan-out is a write into N durable queues; Redis pub/sub only ever rang a doorbell |
| WebSocket? | **Optional, later, additive.** It can only ever be a doorbell — a pushed message carries no lease |
| Webhooks as the consumer transport? | **No.** Egress adapter only — see Part III.4 |
| Shard instead of replicate? | **No.** Sharding subdivides the single-node risk; it does not remove it |
| Raw TCP / RESP instead of HTTP? | **ADOPTED (D1).** Keeps SE.Redis entirely and makes the work engine-only. The compatibility cost was overstated — see III.7's correction |
| Best transport improvements? | HTTP/2, batched claim, **piggybacked ack**, multi-queue claim. SSE later as the doorbell. Part III.6 |
| Clients in other languages? | **Not pursued.** .NET-only is the positioning (Part VIII). A producer-only HTTP ingress stays available if anyone asks |
| Is the proposed replication design sound? | **In shape, yes** — with four corrections in Part VI |
| Does this give MongoDB-style automatic failover? | **Not with two nodes.** For a self-contained broker it needs three and a quorum — Part VI.3 |

---

## Part I · Why the engine is the problem

### I.1 The root cause, diagnosed twice independently

StowDB retired Garnet on 2026-08-13 after measuring four blocking defects — lock contention, an
allocation floor, **AOF replay deadlock**, and ~570 B/document of derived state. Its post-mortem
names one cause:

> *"Garnet's keyspace is a hash: no ordered iteration, no prefix scan. So a posting list must be
> a **value holding many members** — a set or sorted set — which makes every index a large,
> shared, mutable managed object. That single consequence produces the lock contention, the
> allocation floor, the eviction/replay fragility, and the memory cost."*

**Highway has the same shape.** Its queue is a Garnet List object. Its lease table is a SortedSet
object. Its node registry and group membership are Set objects. Every one is a large, shared,
mutable object-store entry taken under an exclusive lock.

The two projects reached this independently, from different workloads. That is worth more than
either finding alone.

### I.2 C4.6, which has resisted three investigations

[`constraints.md`](../constraints.md) C4.6 — *storage growth is bounded over time* — is **Not met**,
and unusually well documented as such: `TruncateUntil` is logical only, retired segment files are
never deleted, and growth is *"strictly linear in total history"* across every configuration
tried. Two hypotheses are recorded as discarded (AOF page size in 016, `AofSegmentSize` in 034).
The stated remedy is a periodic planned restart against a fresh directory.

**On an LSM this is not solved, it is absent.** Compaction physically reclaiming space is the
engine's ordinary job — StowDB measured the RocksDB side of exactly this: `DeleteRange` dropped
50 000 keys in ~1 ms, *"reclamation is compaction, which already exists."*

The related line in C4.6 — *"restart replays all of it"* — goes the same way. RocksDB recovery is
opening the files plus a bounded WAL tail since the last flush, not a replay proportional to
total history.

> **Open item.** The user states C4.6 has since been solved but not written up. This document does
> not amend the constraint, because the fix is not visible here. **`constraints.md` should be
> updated by whoever made the fix** — its entire value is that its statuses can be trusted, and a
> solved constraint reading "measured not to work" costs more than an unsolved one.

### I.3 Non-determinism under replay, which a new engine dissolves

`HwQClaimCommand.Main` reads `DateTime.UtcNow.Ticks` to compute lease expiry. Under
`AofEntryType.StoredProcedure`, Garnet's AOF logs the **invocation** and re-runs the procedure on
recovery — so the clock is read again, at a different time, against different state.

RocksDB's WAL logs the **effect**. The clock is read once and the resulting value is what becomes
durable. **The bug class disappears rather than being fixed**, and it disappears a second time on
the replication path in Part VI, for the same reason.

This is the strongest single technical argument in this document — stronger than throughput,
stronger than ordered iteration.

---

## Part II · Feasibility: the surface is small

### II.1 What Highway actually asks of Garnet

Across all 23 command files in `src/Highway.Server/Commands/` (~5 000 lines), the entire storage
dependency is **15 primitives at ~106 call sites**:

| Family | Calls | As ordered keys |
|---|---|---|
| `GET` / `SET` / `SETEX` / `DELETE` / `Increment` | 57 | Direct. `SETEX` needs an expiry field filtered on read, or a compaction filter — RocksDB's native TTL is too coarse |
| `ListRightPush` / `ListLeftPop` / `ListLeftPush` / `ListLength` | 30 | `q\|<queue>\|<seq>` with a monotonic seq; pop is seek-first on the prefix |
| `SortedSetAdd` / `Remove` / `Range` / `Length` | 12 | `z\|<name>\|<big-endian score>\|<member>`; range is a prefix iterate |
| `SetAdd` / `SetRemove` | 7 | `s\|<name>\|<member>` with an empty value; membership is a point get |

Four families, every one a natural ordered-key shape — *"sorting is free because it **is** the key
order."* Critically, none of them is a large shared object any more: the queue becomes a key range,
not a value.

### II.2 What gets deleted rather than ported

`HwQClaimCommand.PrepareCore` declares an exclusive lock set of seven keys **plus one per known
node**, across two stores, and carries this comment:

> *"Read the worker list from the main-store mirror, never the object-store set: an object-store
> read here registers a watch that the exclusive locks below would then fail (004.1)."*

That is a scar, not queue logic. On RocksDB a claim is: take the per-queue in-process lock, read
through a snapshot, build a `WriteBatch`, commit. The `Prepare`/`Main` split, the key
pre-declaration, the main-store mirrors that exist only so `Prepare` can read them, and the
`StoreType.Main` / `StoreType.Object` distinction all disappear.

### II.3 What is genuinely lost

| Lost | Consequence |
|---|---|
| **RESP, TLS, auth, connection handling** | You own the server. Deliberate, given the protocol is being replaced anyway — but it is the largest single piece of new work |
| **Feature 026, the distributed cache** | Documented as using *"Garnet's native GET/SET; no protocol extension"*. It exists only because Garnet was underneath. It goes |
| **SE.Redis's multiplexer** | One persistent pipelined connection with auto-reconnect and auto-resubscribe. Replacing it badly would be much worse than Redis TCP — see Part III.5 |

### II.4 What survives untouched

Dead-letter handling, lease sweep with attempt counting, delayed promotion, recurring-job firing,
the service registry, heartbeat, DLQ, replay, and the byte accounting from feature 016. That is
the ~5 000 lines, and it sits entirely on the 15 primitives above.

---

## Part III · The protocol can be HTTP

### III.1 The client is already a polling client wearing a Redis connection

`SingleMessageWorkerLoop` is the shared base for all three consumer types, and it is a plain loop:
`ClaimAsync()` → if null, wait for a wake or `Task.Delay(100)` → repeat. `QueueWorkerLoop`,
`SubscriptionWorkerLoop` and `RpcWorkerLoop` differ only in which `ClaimAsync` they supply.

There is exactly **one** push channel, and `DoorbellWatcher` states its own status:

> *"Doorbells are a pure latency optimization — correctness rides on the backstop sweep. With
> `DoorbellsEnabled == false` nothing is subscribed and the engine still works at
> backstop-interval latency."*

It carries no payload either — the handler is `_ => captured.Signal()`, which discards the
message. Even RPC replies work this way: `PendingCallRegistry.SweepAsync` GETs reply slots on a
timer and `hw:door:rep` only shortens the wait.

**This is not a push protocol being converted to pull. It is already pull, with one optional
contentless wake to replace.**

### III.2 Long-poll is the protocol

```
POST /queues/{q}/claim?wait=30s&max=10
```

Server holds the request until a message arrives or the timeout expires; `200` with messages,
`204` empty.

This **collapses the doorbell and the claim into one call.** `DoorbellWatcher`, `LoopWake`, the
doorbell half of `BackstopSweeper` and the `Task.Delay(100)` are all deleted; the wait moves
server-side where it belongs. It is what SQS does with `WaitTimeSeconds` and what the Kubernetes
watch does, it needs no second channel or subscription state, and it traverses any proxy.

Two rules that come with it:

- **Batch from day one.** Claim + ack is two round trips per message; Redis pipelining hid this.
  `max=10` and a batched ack amortise it. The server already thinks in batches —
  `HwQClaimCommand.MaxPromotionBatch = 256`.
- **The held request must not hold the per-queue lock.** Park it on a per-queue
  `TaskCompletionSource`, signal on enqueue, *then* take the lock and claim. Otherwise a
  30-second long-poll blocks the lease sweep.

### III.3 WebSocket — additive, and safely deferred

A socket can only ever be a **doorbell**: the claim has to stay transactional and leased, and a
pushed message carries no lease, no attempt count and no sweep target. So it sits in exactly the
relationship doorbells already have to the backstop sweep — a latency optimisation over a correct
polling path.

Deferring it therefore costs nothing structural. Committing to it now would buy reconnect logic
(currently free via SE.Redis) and sticky routing through the load balancer. Much of the case for
it also evaporates under **HTTP/2 or HTTP/3**, where Kestrel multiplexes over one connection
natively.

### III.4 Webhooks — egress adapter, never the consumer transport

Highway's delivery model is pull-with-a-lease: claim → lease → `HW.TOUCH` heartbeat → ack, with
lease expiry driving the sweep, attempt counting and dead-lettering. That is what earns
at-least-once under an ungraceful kill, and it is what makes competing consumers free — workers
race on `QCLAIM` and the server never chooses.

A webhook inverts all of it:

- Retry/backoff, an in-flight table, a delivery-attempt counter and receiver-side dedup all have
  to be rebuilt around the delivery attempt.
- **The heartbeat is lost entirely** — a slow handler becomes indistinguishable from a dead one
  except by HTTP timeout.
- Competing consumers are lost — the server must now choose a receiver, so it needs an endpoint
  registry, health checking and load balancing.
- Every consumer needs an inbound-addressable endpoint, which breaks the five-minute-productive
  story precisely where Highway is aimed: console apps, worker services behind NAT, a laptop.

**Keep webhooks as an egress adapter** — how non-.NET systems and external partners receive from
Highway, where the receiver is not running the client anyway and push-with-HTTP-status is the
right model.

### III.5 The connection model is the part to get right

SE.Redis gives one persistent, pipelined, auto-reconnecting connection per client. The
replacement is a **single long-lived `HttpClient`** over `SocketsHttpHandler` with
`PooledConnectionLifetime` set, on **HTTP/2**, so multiplexing happens over one connection rather
than a pool of HTTP/1.1 sockets. Done naively — an `HttpClient` per call, no pooling — this would
be well behind Redis TCP. Done properly it is comparable.

One real change in shape: **idle clients stop being idle.** Today a waiting worker has zero
in-flight requests. With long-poll every worker on every machine holds one open request
continuously. Kestrel handles that trivially, but connection limits must be sized for it.

### III.6 Improving on plain long-poll, ranked

**The invariant that decides all of it:** a **lease handover** must be a committed operation; a
**notification** may be anything. And long-poll gives one thing for free that is easy to lose —
**flow control.** The client only claims when it has a free slot; `SingleMessageWorkerLoop` is
explicit that *"a claim taken while the gate is full is a lease running on work that cannot
start."* **The request is the credit.** Any design where the server pushes *messages* must
reintroduce that as credit-based flow control (AMQP prefetch, gRPC windows) or suffer lease-expiry
storms: push 100 messages to a worker with 4 slots and 96 leases burn down on work that never
started, producing spurious redelivery and dead-lettering.

Streaming the **wake** is cheap. Streaming the **delivery** costs a flow-control state machine on
both ends.

| Rank | Change | Why |
|---|---|---|
| **1** | **HTTP/2** | Not an alternative to long-poll — an improvement underneath it. Multiplexing puts all N polls on **one** connection as N streams; HPACK makes near-identical repeated claim headers almost free; binary framing cuts parse cost. A Kestrel setting plus `EnableMultipleHttp2Connections`. **Do this unconditionally** |
| **2** | **Batch claim + piggybacked ack** | Claim+ack is two round trips per message. `max=10` makes it two per *batch*; carrying the previous batch's acked ids **in the next claim** makes it **one**. What Kafka does with offset commits. Pure protocol design, and a larger real-world win than any transport change |
| **3** | **Multi-queue claim** | One request waiting across several queues, returning from whichever fires. A node with 40 handlers goes from 40 held requests to ~1, using only the long-poll already there. Far easier to design in than to retrofit once routes are public |
| **4** | **SSE as the doorbell** | One `text/event-stream` per node carrying *"queue X has work"*, with the claim still a POST. Held requests drop to one per node; wake latency equals a doorbell; **flow control stays free** because the claim is still client-initiated. Over HTTP/2 it is one stream on the existing connection. **Additive** — long-poll stays the contract and `BackstopSweeper` stays the backstop, so a dropped event costs latency, not a message |
| **5** | **WebSocket** | SSE wins here. Duplex buys client-to-server on the same connection, which is not wanted: claims and acks are better as ordinary requests where `Idempotency-Key` and status codes apply. Against that, an upgrade handshake, sticky routing, own reconnect logic, worse debuggability. **Only justified if you move to push-delivery with credits** |
| **6** | **HTTP/3** | Removes TCP head-of-line blocking, adds connection migration and 0-RTT — benefits aimed at lossy and mobile links. This is server-to-server. Costs UDP, which proxies and corporate networks handle unevenly. **Skip** |
| **7** | **gRPC** | The obvious .NET answer, and the wrong one here. Streaming, strong typing and HTTP/2 windows give transport-level flow control — but it undercuts the property that motivated HTTP+JSON: a producer in any language being an afternoon's work, and the thing being curl-able at 3am (Part VIII) |

**Sequenced:** HTTP/2 + batching + piggybacked ack + multi-queue claim in stage 1 (all protocol
design, no new transport); SSE later as an option; push-delivery only if a measurement demands it
— which currently cannot happen, because `product.md` records *"Performance: **Uncharacterised.**
No benchmark exists and no throughput target is claimed."*

### III.7 The alternative: raw TCP, keeping RESP

A genuine option, and cheaper than it looks. **Kestrel is a connection transport, not only an HTTP
server** — `ListenAnyIP(port, l => l.UseConnectionHandler<RespConnectionHandler>())` gives socket
transport, `PipeReader`/`PipeWriter`, endpoint TLS, connection limits, graceful shutdown and
diagnostics while speaking anything on the pipe. So "own TCP server" means "write a protocol
codec", not "write a socket server". RESP2 is five types; the fiddly part is partial buffers, not
grammar. And `Garnet.common`'s `RespReadUtils` / `RespWriteUtils` are the codec without the server,
already in the submodule — vendoring them roughly halves the work.

Estimate: **~1 400 lines, 2-3 weeks solo**, or about half with the codec lifted. Against HTTP's
~1 200 lines of endpoints plus long-poll parking, close to a wash on the server side.

**The prize is the client.** Staying on RESP means `Highway.Client` **keeps SE.Redis entirely** —
multiplexing, pipelining, reconnect with backoff, per-operation timeouts, and **automatic
resubscribe**, which `DoorbellWatcher` explicitly depends on: *"SE.Redis re-establishes
subscriptions automatically after a reconnect ... so no re-issue logic is needed here."* The worker
loops, `PendingCallRegistry` and the doorbell survive unchanged, and stage 1 becomes
**engine-only** — materially smaller and lower-risk.

**The cost is permanent Redis compatibility.** SE.Redis issues `HELLO`, `AUTH`, `CLIENT SETNAME`,
sometimes `CONFIG GET` and `INFO`, and expects Redis semantics for `PING`, subscribe/unsubscribe
and error shapes. You implement enough to keep it happy, then keep it happy across every future
version — an open-ended obligation to a library you do not control, for a protocol you are not
actually implementing.

| | HTTP/JSON on Kestrel | RESP on own TCP server |
|---|---|---|
| Server transport | Kestrel, free | Kestrel `ConnectionHandler`, free |
| Protocol codec | Free | **~600-1 000 lines, yours forever** |
| TLS / auth | Free | Endpoint TLS free; `AUTH` yours |
| **Client transport** | Rebuild (~600 lines) | **SE.Redis, free** |
| Per-op overhead | **~140 us** (StowDB, `CreateSlimBuilder`) | **~10-30 us** |
| Other languages | Trivial — any HTTP client | Redis clients are everywhere, but custom commands need raw-command support |
| Debuggability | `curl` | `redis-cli` |
| Observability | Free — `http_server_*` already collected | Yours to instrument |

**On the 5-10x overhead gap:** batching largely closes it. A claim of 10 amortises one HTTP round
trip to ~14 us per message, same order as unbatched RESP; the piggybacked ack closes more. And
there is no target to measure against, so the gap cannot currently be judged.

> **Superseded 2026-09-11, same day — this section recommended HTTP and the decision went the
> other way.** Kept as written, with the corrections below, because the reasoning is the record.

**What was recommended here:** *"build the protocol behind a seam and lead with HTTP"*, on the
grounds that RESP was right only *"if Highway is .NET-only, LAN-local and throughput-bound."*

**What was decided:** exactly that condition held. Part VIII established .NET-only positioning, so
HTTP's remaining advantages were worth nothing — and **RESP was adopted (D1)**. The transport seam
survives the reversal and is still built (spec R10), because it costs almost nothing and is the
escape hatch.

**Two things above are wrong and are corrected here rather than edited out:**

**(a) The compatibility cost was overstated.** This section calls it *"an open-ended obligation to a
library you do not control."* But **we ship `Highway.Client`**, so we pin the SE.Redis version and
upgrade deliberately — ordinary dependency management. And SE.Redis supports partial servers
**first-class** via `CommandMap`, which has built-in presets for non-Redis servers (`Twemproxy`,
`SSDB`, `Envoy`). Being a subset server is a supported scenario, not a hack.

**(b) The estimate was pessimistic.** *"~1 400 lines, 2–3 weeks"* assumed writing a full codec.
Inbound from a Redis client is **always an array of bulk strings** — two types — and outbound
needs five. That is ~15% of Garnet's surface. With `RespWriteUtils.cs` vendored (907 lines, **zero**
Tsavorite coupling, nothing to substitute) and a narrow reader written against a byte-by-byte split
test, the real figure is closer to **~1 000 lines, most of it copied.**

**And the deciding argument was not in this section at all.** It is not performance and not the free
multiplexer — it is that **keeping RESP makes the work engine-only**, so exactly one thing changes
and a failed assurance run is diagnosable. For a rewrite, changing one variable is worth more than
100 microseconds per operation.

> **Two protocols is two conformance surfaces.** "We might do both later" must not quietly become
> "we are doing both."

---

## Part IV · Many machines, one broker

### IV.1 Garnet was never giving multi-broker

There is one Highway server — one Garnet instance, one data directory. The "nodes" in `HW.QCLAIM`
are **client** nodes; they never talk to each other, only through the broker. `_knownNodes` exists
so the broker can sweep leases across all of them.

The topology is therefore unchanged by this work: **N machines → N HTTP clients → 1 broker → 1
RocksDB**. The Redis TCP layer was a connection, not a cluster.

### IV.2 Delivery never rode the connection

This is what makes HTTP a non-event for durability. Both verbs are already **storage** fan-out:

- **Queue** — `HW.QSEND` appends a row; consumers race on `HW.QCLAIM`.
- **Pub/Sub** — `HW.PUBLISH` locks `{channel}@{group}` for *every* registered group and pushes
  into all of them in one transaction, all-or-none. Subscribers then consume via the ordinary
  `QClaimAsync` on their derived queue. Feature 018 deleted `HW.RECEIVE`/`HW.RACK` outright.
- The doorbell is rung in `Finalize` — **after** the transaction commits — and only wakes a waiter.

### IV.3 The cases, concretely

| Scenario | Behaviour | Source |
|---|---|---|
| 5 machines, same `IProcess<T>` | All long-poll `claim`; broker serialises under the per-queue lock; one wins, four keep waiting. Work is shared | Broker transaction, not the wire |
| 5 replicas, same `SubscriptionGroup` | One derived queue; replicas compete; handled once per group | C2.1 — *"Within a group, replicas compete"* |
| 5 different subscribers | 5 groups → 5 derived queues → one publish writes 5 rows atomically; each gets its own copy | Default group = node name |
| A machine down | Its group's queue holds every publish while it is away, bounded by 017's retirement rule; drains on restart | C2.3 |
| RPC | `POST /call` writes the service queue; handler claims; `POST /reply` writes the slot; caller long-polls it | Today's `PendingCallRegistry` sweep with the wait moved server-side |

### IV.4 Ambiguity is unchanged

A claim that commits server-side but whose response is lost leaves a message leased to a node that
never saw it → lease expires → redelivery. Identical to a dropped Redis reply. `[Idempotent]` and
`Idempotency-Key` already cover it.

---

## Part V · Replication, not sharding

Sharding was considered and **rejected for this goal.**

**It subdivides the single-node risk rather than removing it.** If `orders` lives on broker A,
then when A dies `orders` is down and its messages are unreachable. For any individual queue the
outcome is still binary. And five unreplicated brokers means five times the chance that
*something* is broken at any moment — partial failure becomes more frequent while total failure
becomes less frequent, which is the wrong trade for a broker carrying orders.

**The dataset's small size argues for replication, not sharding.** C2.2 is explicit: *"Storage
tracks **undelivered** work, which in a healthy system is near zero."* Sharding exists to split
data that will not fit on one node. A small dataset instead makes replication cheap in every
dimension that usually makes it painful — seconds to sync initially, trivial catch-up, low WAL
volume, and a lagging secondary can simply be discarded and rebuilt rather than repaired.

**Catalogue sync is easy; queue state is the hard part.** The catalogue — services, queues,
channels, groups, node registrations — is small, rare-changing and largely idempotent. The
difficulty is elsewhere, and it has one source: **more than one writer.**

1. **A claim must be globally serialised per queue.** Two brokers owning `orders` both handing out
   message 47 turns at-least-once into always-twice. The options are one owner (sharding, so no HA
   for that queue) or consensus on every claim (Raft, the expensive version).
2. **Leases are time-bound.** A lease expiring on A must not be swept by B on a different clock.
   Highway is deliberately careful here — *"Node clocks never participate"* for jobs — and
   multi-master breaks it directly.
3. **Fan-out is atomic.** `HW.PUBLISH` is all-groups-or-none. Groups on different brokers make
   C2.1 a distributed transaction, or weaken it.

Primary-plus-replica is tractable **because it keeps one writer**: one serialisation point, one
clock, one transaction boundary, and a secondary that only applies what the primary already
decided. It is not a lesser design — it avoids the hard problem rather than solving it.

### V.1 Blast-radius isolation is available now, for free

If partial-failure isolation is wanted before replication exists, **run separate Highway instances
per bounded context** — an orders broker, an integration broker, a notifications broker. Different
data directories, different ports, different upstreams. That is sharding done by the operator: the
isolation benefit with no protocol work, no routing logic, and each instance free to gain its own
replica later, independently.

### V.2 If both are ever needed

Highway has no cross-queue transaction — the only atomicity spanning anything is publish across
groups *within one channel*. So a channel is a clean partition boundary, and the eventual form is
**N primary/secondary pairs, each owning a set of channels.** Strictly more work than one pair, and
it should follow one pair working. The stage-1 rules in Part VII.2 are per-shard concepts anyway,
so nothing is wasted.

---

## Part VI · The replication design

**Proposed shape:** a primary drives everything; nodes carry priorities 1..N; the primary
replicates synchronously to the next priority and waits for an ack before answering the caller;
replication chains onward (prio2 → prio3 → …); a down node is skipped; when the primary dies, the
next comes online.

That is **chain replication with priority failover and a synchronous ack** — the family Azure
Storage and Ceph's primary-copy path sit in. The shape is sound. Four things need sharpening.

### VI.1 The ack must come from the *specific successor*

Waiting for *any* node to ack is not sufficient. If prio3 acks and prio2 does not, and the primary
then dies, **prio2 is promoted without a write that was already acknowledged to the caller.** That
is silent loss at exactly the moment the guarantee is supposed to pay out.

**Rule: ack when the node that would be promoted next has the write.** Not any node, not a count.

### VI.2 "Skip a down node" requires an in-sync set

Two failures otherwise. RPO silently degrades from 0 without anyone being told; and prio2 returns
— still prio2, still next in line — but now behind, so promoting it loses everything written while
it was away.

**Adopt the in-sync set** (Kafka's ISR; MongoDB enforces the equivalent through oplog catch-up):

- A node is **promotable only while it is in the in-sync set.**
- It leaves the set the moment it fails to ack, and rejoins only after catching up from the WAL.
- The primary acks to the caller when the **highest-priority in-sync node** has the write.
- If no node is in sync, the write is either refused or explicitly degraded **loudly** — C4.3
  already requires that reaching a limit is never silent, and this is the same principle.

### VI.3 Split brain, and who is allowed to arbitrate

> **Corrected 2026-09-11, same day.** An earlier version of this section proposed making a Caddy
> reverse proxy the fencing authority, with `lb_policy first` as the priority scheme. **That was
> wrong for this product** — it was imported from an unrelated project where a proxy already
> sits in front of the application tier. **Highway is a standalone broker.** Applications connect
> to it directly, `Highway.Server.Host` is a daemon, and there is no web tier in the model. The
> original text is replaced rather than kept, because it would read as a recommendation.
>
> One consequence elsewhere: Part III.7's comparison no longer counts "a proxy cannot forward
> RESP" against a TCP protocol, because there is no proxy either way.

"When the primary dies, the next comes online" hides the hard question: **how does prio2
distinguish a dead primary from an unreachable one?** It cannot. If the prio1–prio2 link breaks
while clients can still reach both, there are two primaries, both handing out claims, diverging
permanently. Priority ordering does not prevent this. **Something outside the two nodes has to
decide.**

| | Option | Gets you | Costs |
|---|---|---|---|
| **A** | **Operator-triggered promotion** | RPO 0 for acked writes; no split brain; no dependency | RTO is however long a human takes. StowDB's stated position: *"operator-triggered with epoch fencing"* |
| **B** | **Three nodes with a quorum** | **The only self-contained automatic failover** | Real consensus, and a third machine |
| **C** | **An external witness** — a lease only one node can hold (a lock row, a file, an object-storage conditional write) | Automatic failover with two brokers | Reintroduces external infrastructure, which is what *"no external broker, no ceremony"* exists to avoid |
| **D** | **Client-side quorum** | — | **Does not work.** The client population is dynamic, so there is no stable majority to count |

**The sharpened conclusion: with two nodes and no external arbiter, automatic failover is not
available at any price.** The fork is therefore not a matter of scope but of capability, and it
should be settled before stage 2 starts rather than during it:

| | Scope | Guarantee |
|---|---|---|
| **2 nodes** | WAL shipping plus a `promote` command | **Operator-triggered.** RPO 0 for acked writes |
| **3 nodes** | Quorum and real consensus | **Automatic election** |

**What the client needs under every option.** It must hold a **node list** rather than a single
endpoint and re-target on failure — a `Highway.Client` feature, not infrastructure. Priority
ordering therefore lives client-side too: try prio1, then prio2. The epoch (VI.5) is what stops a
client from successfully writing to a demoted node that is still running and still answering.

### VI.4 The sync scope is every write, not the catalogue

A subscription arriving is a catalogue change — but the catalogue is **not** what failover loses.
If only catalogue writes replicate, the promoted node knows exactly which queues exist and has
none of the messages in them.

**Every durable write goes through the same path**: send, claim, ack, publish, lease renewal,
dead-letter. That is where the cost lands, because every `HW.QSEND` now pays a round trip before
the caller hears yes.

So make it a **declared choice rather than a global one** — the pattern StowDB's C40 uses for
durability modes and Kafka spells `acks`:

| Level | Behaviour | Fits |
|---|---|---|
| `acks=local` | Primary commits and returns | A cache-invalidation publish |
| `acks=replica` | Wait for the highest-priority in-sync node | Orders |

Per queue or per channel, declared on the attribute, published in `_capabilities` alongside
everything else. Latency is then paid only where it is worth paying.

### VI.5 The epoch becomes load-bearing

Every write carries the primary's term. A returning old primary writing with term N against a
cluster on term N+1 is **rejected, not merged.** Without it, a node that was partitioned corrupts
state on reconnect. Cheap to add before the first byte is written; a migration afterwards.

### VI.6 Chain replication, specifically

The textbook version acks from the **tail**, which buys strongly consistent reads anywhere in the
chain. Acking from the next link instead trades that away for latency. For a broker that is a
reasonable trade — there are no read-anywhere semantics to protect — but it is a **deliberate
divergence** from the standard design and is recorded as one rather than left to be inferred.

The fiddly part is **chain repair**: when prio2 dies, prio1 must feed prio3, which may be behind
wherever prio2 reached. A WAL sequence number per node makes this mechanical — prio1 asks prio3
where it is and resumes `GetUpdatesSince` from there. Another reason the sequence number belongs
in stage 1.

### VI.7 Why RocksDB is what makes this tractable

`GetUpdatesSince(seq)` is a durable, ordered, resumable WAL iterator. StowDB's V26.3 lists losing
it as one of the four real costs of moving to Firebird, calling it *"the durable, ordered,
resumable primitive underneath the change feed and replication"* — and C6 records that **WAL
replication streaming was already verified in a bake-off probe.**

It ships **effects, not commands**, so Part I.3's determinism fix pays out a second time here.

**Stage 1 is therefore not merely prerequisite work — it creates the replication primitive.** Doing
it in the other order would mean building replication over Garnet's AOF, the mechanism that
already defeated C4.6 three times.

---

## Part VII · Staging

### VII.1 Stage 1 is parity, and the gate already exists

**Goal: match current Highway on RocksDB with an HTTP protocol.** No replication.

Feature 032's assurance rig is unusually well suited as the acceptance gate — three real
applications, one broker, every verb, a scripted timeline with ungraceful kills, each participant
keeping its **own ledger**, and a reconciler proving five invariants:

| | Invariant |
|---|---|
| **I1** | Queue completeness |
| **I2** | No phantoms |
| **I3** | RPC never silent |
| **I4** | Pub/sub reaches every live group |
| **I5** | Duplicates counted, not failed |

Because those assert from participant ledgers rather than broker internals, **they survive a
transport and engine change unchanged.** Definition of done for stage 1 is then not a judgement
call: *the same rig, the same five invariants, green on RocksDB + HTTP, including the crash
phases.* Requirement 6 — "the broker's own record corroborates" — is the only part needing
re-pointing at the new storage.

Most rewrites have no independent oracle. This one does, and it already runs shortened in CI.

**Suggested order within stage 1:**

1. Extract the storage interface from the 15 primitives at their existing call sites.
2. Implement it twice — over RocksDB, and as the in-memory shim the tests want anyway.
3. Port the command logic; the `Prepare`/`Main` split collapses into one transactional method.
4. Build the HTTP surface and swap the client transport.
5. Run the rig.

Steps 1–3 prove the ~5 000 lines worth keeping on the new engine **before** taking on the network
layer, which is the part with no reference implementation.

### VII.2 Four rules, two of which earn their place regardless

> **Revised 2026-09-11.** An earlier version of this section said all four were *"free now and
> invasive later."* **That framing is too strong for a broker.** It holds for a database, where
> data is permanent — but C2.2 says a healthy Highway tracks *"undelivered work, which in a
> healthy system is near zero."* **A breaking storage-format change here is: drain the queues,
> stop, wipe the directory, start.** A maintenance window, not a migration project. Given
> replication is deliberately stage 2, the four no longer carry equal weight.

| # | Rule | Verdict under the stage-1-first decision |
|---|---|---|
| **1** | One write chokepoint | **Do it anyway** — not a replication tax. One place to reason about durability, and far easier to test |
| **2** | Write effects, never intent | **Do it anyway** — this is what kills the `DateTime.UtcNow` replay bug on a *single* node (I.3). Nothing to do with replication |
| **3** | Mutation is primary-only | **Defer the flag, keep the shape.** Do not add dead code with no test — but do not scatter mutation-initiating work across a dozen paths either. *Collecting* the sweeps later is the expensive part; adding a boolean is not |
| **4** | Epoch + WAL sequence number | **Defer.** With a drainable dataset, adding these later is a restart |

The four in full:

1. **One write chokepoint.** Every state change goes through a single `WriteBatch` commit, and
   nothing durable lives outside RocksDB. State in process memory diverges on the replica the
   moment the WAL is applied. The `FlightRecorder` is the model for a legitimate exception —
   already declared *volatile, in-process, lost on restart*, so it is honestly outside the
   guarantee rather than accidentally outside it.
2. **Write effects, never intent.** Never persist "sweep the leases" — persist the rows the sweep
   produced. Read the clock *before* opening the batch and put the resulting absolute tick value
   *into* it. Already half-true today, and making it a rule is what permanently retires Part I.3's
   bug class.
3. **Mutation is primary-only, decided now.** The lease sweep currently runs *inside*
   `HW.QCLAIM`. A secondary that also sweeps diverges immediately. Put every sweep, promotion and
   job-fire behind a single "am I primary" gate while there is no secondary to test against —
   retrofitting that gate across the command set later means touching every write path.
4. **An epoch and a WAL sequence number in the layout from day one.** VI.5 and VI.6.

### VII.2b What a rewrite unblocks — the real argument for doing it as one

Stage 1 is the cheap moment for exactly the constraints that were blocked on *"this would be a
breaking change."* If they are not designed in here they stay unmet for another cycle.

| | Constraint | Status today | Why stage 1 is the moment |
|---|---|---|---|
| **C4.1** | Retention, 100 days | **Not met** | The register says why: *"Feature 016 found it needs a breaking framing change first."* Stage 1 **is** that change |
| **C4.7** | The byte budget bounds a queue, not the process | **Deliberately unmet** | Ten queues at their limit is ten gigabytes. A process-level bound belongs in a new storage layer, not bolted onto an old one |
| **C9** | TTL expiry | **Parked**, with a reserved on-disk flag | Decide now whether the new layout makes room for it, or drop it formally. Parking it a second time is the worse outcome |
| **C19** | Change feed | **Parked**, unwritten | `GetUpdatesSince` makes this nearly free on RocksDB — and it is the same primitive stage 2 needs |
| **C4.6** | Storage growth bounded over time | **Not met**, status disputed | I.2 and the addendum in `constraints.md` |

**C19 is worth singling out.** On Garnet the change feed was postponed for want of a durable,
ordered, resumable primitive. RocksDB has exactly that, and stage 2 needs it anyway — so a
capability parked for a year becomes close to a by-product.

### VII.3 Stage 2 is replication

Part VI, once stage 1 is green on the rig. The 2-node-versus-3-node fork in VI.3 should be settled
before it starts.

---

## Part VIII · Clients in other languages

> **RESOLVED 2026-09-11: .NET-only.** This analysis is what settled it, and settling it is what
> then settled the protocol (D1) — so the order of reasoning ran Part VIII → III.7, not the
> reverse. The decisive evidence is VIII.6 below.

Only reachable if the protocol is HTTP (III.7 weighs this against RESP). The question is not
whether another language can speak HTTP — obviously it can — but **how much of `Highway.Client`
would have to be rewritten.**

### VIII.1 The wire is portable; the programming model is not

Highway's stated advantage is the attribute model: `[Queue("...")]`, `IReturn<T>`, assembly
scanning building a catalog, DI-scoped dispatch, *"productive in five minutes."* **None of that is
on the wire.** It is reflection over .NET types. What crosses the wire is small — send, publish,
call, claim, ack, fail, touch, heartbeat, discover — and trivially expressible anywhere.

### VIII.2 The asymmetry: producers are cheap, consumers are not

**A producer** needs `POST /send`, `POST /publish`, and for RPC `POST /call` plus a reply poll. An
afternoon in any language. No state machine, nothing subtly wrong.

**A consumer** must implement, correctly: the long-poll claim loop; **lease renewal on a timer
while the handler runs**; ack/fail with attempt-count and dead-letter semantics; the concurrency
gate; group-scoped idempotency claims; group registration and 017's retirement rules; heartbeat;
and graceful shutdown that drains in-flight work and releases leases. Every item has a
*correctness* consequence, not a convenience one — that is the bulk of `Highway.Client`, owned N
times over, permanently.

### VIII.3 Four protocol prerequisites

1. **The payload envelope must stop being .NET-shaped.** Right split: **JSON envelope, opaque
   payload with a declared content-type.** .NET-to-.NET pairs keep today's serialisation at zero
   cost; cross-language pairs agree on `application/json`. Forcing JSON all the way down taxes
   every existing path to serve a case that may never arrive.
2. **Names must be wire-stable.** `TryReadDerivedIdentifier` and `_derivedQueueName` show some
   names are *derived* from .NET types. Either the derivation is deterministic and documented so a
   foreign client can reproduce it, or the protocol requires explicit names and .NET derivation is
   a convenience that produces one. Cheap now, breaking later.
3. **`HIGHWAY-PROTOCOL.md` must become normative, not descriptive.** It must specify the
   lease/attempt state machine, every status code, and the exact error shape as contract — a wire
   document written alongside one implementation drifts into describing that implementation.
4. **Structured JSON errors with codes**, not .NET exception types. Feature 015 already produced
   the diagnosable failures; this is putting them on the wire.

### VIII.4 The conformance gate already exists

Feature 032's rig asserts from **participant ledgers**, not broker internals. Swap one participant
for a Python one and I1-I5 still apply unchanged. So *"is this client correct?"* has a mechanical
answer: **it joins the rig and the five invariants stay green through the crash phases.** Very few
projects can define client conformance that precisely.

### VIII.5 Recommended scope: producers plus webhook egress, not consumer SDKs

Complete cross-language participation without anyone implementing the lease state machine twice:

- A foreign service **produces** with a trivial HTTP POST.
- It **receives** via a **webhook** — push with HTTP status, which is exactly the egress case
  where webhooks are right *because the receiver is not running your client* (III.4).

Roughly 15% of the cost for most of the value, and the correctness-critical consumer path keeps
one implementation.

### VIII.6 The market read that decided it

| Language | Incumbent | Backing store |
|---|---|---|
| **Python** | **Celery** (dominant, mature), RQ, Dramatiq, arq, Huey | Redis / RabbitMQ |
| **Node** | **BullMQ** (dominant), pg-boss, Agenda, bee-queue | Redis / Postgres / Mongo |
| **Ruby** | **Sidekiq** (dominant, a real business), Solid Queue (Rails default), GoodJob | Redis / Postgres |
| **Go** | **Asynq** (closest analogue to Highway), River, Machinery, Watermill | Redis / Postgres |
| **Java** | JobRunr, Quartz, Spring Cloud Stream; Kafka owns pub/sub | JDBC / Kafka |
| **.NET** | **Hangfire** (jobs only, SQL-backed, commercial Pro tier), NServiceBus, MassTransit, Wolverine, Rebus | SQL / RabbitMQ / ASB / SQS |
| **Polyglot** | **Faktory**, Temporal, NATS JetStream, Redis Streams, Dapr | own server |

**Three findings:**

**1. Every ecosystem already has an entrenched, free, mature incumbent.** Entering Python or Node
means competing with Celery and BullMQ on their home ground. That is a wall, not a gap.

**2. Highway's differentiator inverts outside .NET.** In .NET, *"no external broker"* is real — the
alternatives all require RabbitMQ, ASB or SQS, or cost $2–5k per endpoint. In Python or Node **the
team already runs Redis**: adding Celery is adding a library, adding Highway is adding a *server*.
The property that wins here loses everywhere else.

**3. Faktory is the cautionary data point.** The closest existing thing to "Highway, but polyglot" —
a language-agnostic job server, built by the author of **Sidekiq**, i.e. by someone with maximum
credibility and an existing audience. It has not come close to Sidekiq's traction. The strongest
available signal that what people adopt is the idiomatic library, not the neutral server.

**And the .NET gap is real and monetisable.** Hangfire does background jobs only — no pub/sub, no
RPC — and still sustains a commercial tier. **.NET has no Sidekiq.** Being fifth-best in Python is
a worse place to stand than filling that.

> **Strategic caution.** `product.md` records that the "distributed application runtime" framing
> was withdrawn because it *"invited comparison on breadth (actors, workflows, pluggable state
> stores, **eight language SDKs**) against a product whose actual advantage is that a developer is
> productive in five minutes."* Shipping language SDKs walks back toward exactly that comparison —
> and by Highway's own measure a Python client with explicit registration is a *worse* product than
> the .NET one, because the five-minute story came from the attributes. Producer clients and
> webhooks do not have that problem: they are interop, not a second front.

---

## Part IX · Decisions not made

| # | Open |
|---|---|
| **O1** | **2 nodes with operator promotion, or 3 with automatic election?** VI.3. Different scopes, different products |
| **O2** | The HTTP surface itself — route shapes, payload encoding, versioning. Nothing here specifies it beyond long-poll being the contract |
| **O3** | Whether the RPC reply path stays a polled slot or becomes something else under HTTP |
| **O4** | Where `acks` is declared — attribute, configuration, or per-call — and what the default is |
| **O5** | Whether feature 026's cache is reimplemented, dropped, or delegated. Current reading: dropped |
| **O6** | Whether the existing `HW.*` command set is preserved semantically under new route names, or re-cut |
| **O7** | **C4.6's real status** — see I.2. Not a design question, a documentation one, and it blocks trusting the register |
| **O8** | **HTTP or RESP** (III.7). Turns on whether the multi-language property in Part VIII is actually wanted, not on benchmarks |
| **O9** | **Multi-queue claim** — in the route shape from the start, or not at all (III.6 rank 3) |
| **O10** | **Two nodes with operator promotion, or three with a quorum** (VI.3). Supersedes the earlier framing of O1: this is a capability fork, not a scope one — automatic failover is unavailable with two |
| **O11** | **Which parked constraints the rewrite adopts** — C4.1, C4.7, C9, C19 (VII.2b). Deciding by omission is the bad outcome |
| **O12** | **A throughput target.** Named here because III.6, III.7 and the batching design all stall on its absence |

---

## Appendix · Evidence index

Everything asserted above, with where to check it.

| Claim | Where |
|---|---|
| 15 primitives, ~106 call sites | `src/Highway.Server/Commands/`, `grep -rhoE "api\.[A-Za-z]+"` |
| The lock-set scar and the 004.1 comment | `HwQClaimCommand.PrepareCore` |
| `DateTime.UtcNow` inside a replayed procedure | `HwQClaimCommand.Main`, lease expiry |
| Publish is storage fan-out across group queues | `HwPublishCommand.PrepareCore`, `{channel}@{group}` |
| Doorbells are optional by design | `src/Highway.Client/Engine/DoorbellWatcher.cs`, `BackstopSweeper.cs` |
| All three consumers are the same polling loop | `SingleMessageWorkerLoop.cs` and its three subclasses |
| Replica competition within a group | `constraints.md` C2.1, feature 025 |
| Queues hold messages for absent subscribers | `constraints.md` C2.3, features 017 / 018 |
| Undelivered work is near zero in a healthy system | `constraints.md` C2.2 |
| C4.6 unbounded growth, three investigations | `constraints.md` C4.6 |
| The five invariants and the rig's shape | `docs/features/032-assurance-rig/requirements.md`, R5 |
| Garnet's four defects and their root cause | StowDB, `engineering/research/2026-08-13-architecture-rethink.md` |
| RocksDB dropped for query capability, not performance | StowDB, `engineering/research/2026-08-16-v3-decisions.md` V26.2 |
| `GetUpdatesSince` as the replication primitive | StowDB, same file, V26.3; `constraints.md` C6 |
