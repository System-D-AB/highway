# Design: Heartbeat & Service Registry

> **Protocol reference.** The authoritative definition of the wire protocol —
> commands, replies, errors, keys, framing, invariants — is
> [`docs/HIGHWAY-PROTOCOL.md`](../../HIGHWAY-PROTOCOL.md) (feature 007).
> This document keeps the *reasoning* behind the decisions; that file is the
> reference for *what* the protocol is. Where they differ, that file governs.
## Overview

Three new commands (`HW.HEARTBEAT`, `HW.DISCOVER`, `HW.STATS`) and one new client loop (`HeartbeatLoop`), plus the pruning path that turns liveness into reclaimed state. The registry is deliberately thin: a per-node record holding the node's own catalog JSON and a last-seen timestamp, plus a reverse index from service name to node set so discovery is a lookup rather than a scan.

Everything follows the established patterns rather than inventing new ones: commands are `CustomTransactionProcedure`s on `HighwayCommandBase` (004.1) with the same error contract; expiry is a **lazy sweep** triggered by commands already touching the keys, exactly like 004's lease recovery; the client loop uses the same two-token lifecycle as `RpcWorkerLoop` and `ChannelConsumerLoop`.

## Key Design Decisions

### Liveness is a timestamp, not a TTL

Garnet supports key TTLs, and expiring a registration key would prune it for free. This design stores an explicit `lastSeenTicks` instead, for one reason: **Requirement 4 AC4 requires that pruning a node redeliver its unacknowledged requests.** A key that silently vanishes gives no opportunity to run that recovery. An explicit timestamp lets the sweep observe "this node is stale" and act on it — requeue its in-flight work, remove it from node sets — before dropping the record.

This mirrors the choice 004 already made for RPC leases (`claimTicks` in the processing entry rather than per-entry TTL) and for the same reason.

### Two structures, one authority

```
hw:reg:node:{nodeId}        Main-store string   {"seen":<ticks>,"catalog":<raw catalog json>}
hw:reg:nodes                Main-store string   newline-delimited node ids (mirror list)
hw:reg:svc:{service}        Main-store string   newline-delimited node ids hosting the service
```

`hw:reg:node:{nodeId}` is authoritative; the other two are derived indexes maintained in the same transaction. The **mirror-list pattern is mandatory here, not stylistic**: reading an object-store set in `Prepare` creates a watch that conflicts with the later exclusive lock on the same key, which is exactly the constraint that shaped 004's `nodelist`/`grplist` keys (004.1 design § "Findings"). Using main-store strings read with `GET` keeps `Prepare` watch-free on keys we then lock.

Consequence, inherited from 004.1: identifiers must be free of newlines and control characters. `HighwayCommandBase.TryReadIdentifier` already enforces this, so the registry gets delimiter safety by construction.

### Discovery reads the reverse index, not every catalog

`HW.DISCOVER orders.create` must not deserialize every node's catalog JSON. The **registration form** of `HW.HEARTBEAT` maintains `hw:reg:svc:{service}` at write time — parsing the catalog once, when it is actually supplied — so discovery is one `GET` plus a staleness filter.

This means the **registration form** parses `catalogJson`. The stored copy stays verbatim (Requirement 1 AC8) and the parse is a read-only step used solely to derive the index. A catalog that fails to parse is rejected with `HW_INVALID_ARG` rather than silently indexed — a node whose catalog the server cannot read could never be discovered, so accepting it would be worse than failing loudly.

Because registration happens once per node lifetime rather than once per interval, this parse is not on any hot path. That is a direct consequence of splitting the forms: had the catalog ridden every beat, the server would parse every node's catalog every few seconds purely to recompute an index that never changes.

### Pruning removes RPC state but never pub/sub groups

This is the subtlest rule in the feature and the one most likely to be got wrong later, so it is stated as an invariant:

| Node state | On prune | Why |
|---|---|---|
| Registration record | deleted | The node is gone; that is the point |
| Entry in `hw:reg:nodes`, `hw:reg:svc:*` | deleted | Stops it appearing in discovery and stats |
| Membership in `hw:svc:{s}:nodes` + mirror | deleted | **Discharges the 004.1 deferral** — `HW.DEQUEUE` stops locking and sweeping a dead node's list |
| Unacked RPC requests in `hw:svc:{s}:proc:{node}` | **requeued to the service queue** | Requirement 4 AC4 — at-least-once must survive a node dying, not just a lease expiring |
| Subscriber group `hw:ch:{c}:grp:{node}:q` | **left untouched** | Requirement 4 AC5 — groups deliberately outlive the process (005 Req 9 AC3) so a restarting node resumes its backlog |

The asymmetry is intentional and follows from the delivery model. RPC work is claimed and must be released if the claimant dies. Pub/sub messages are *addressed* to a group; a node being down is not a reason to discard mail addressed to it. Deleting groups on prune would silently convert Highway's durable pub/sub into fire-and-forget for any node that outlives its expiry window — the exact regression 004's Requirement 8 AC5 and 005's restart-resume test exist to prevent.

### Expiry is lazy, and `HW.DEQUEUE` is where it pays off

Pruning runs inside commands that already lock the relevant keys:

- `HW.HEARTBEAT` **registration form** — sweeps stale entries from `hw:reg:nodes` while it is already writing there. The liveness form deliberately does not sweep: it must stay a two-key operation
- `HW.DISCOVER` / `HW.STATS` — filter stale nodes from their *results* but do not mutate (Requirements 2 AC7, 3 AC6 make them read-only)
- `HW.DEQUEUE` — the payoff: it already locks `hw:svc:{s}:nodes` and every node's processing list, so it is the natural place to drop dead nodes and requeue their in-flight work

Read-only commands filtering without pruning is what makes Requirement 2 AC3 ("stale nodes excluded even when not yet pruned") necessary and not merely defensive.

## Architecture

```
src/Highway.Server/
├── Commands/
│   ├── HwHeartbeatCommand.cs     # NEW — register | liveness | BYE, + lazy registry sweep
│   ├── HwDiscoverCommand.cs      # NEW — read-only, staleness-filtered
│   ├── HwStatsCommand.cs         # NEW — read-only counters
│   └── HwDequeueCommand.cs       # EXTENDED — prune dead nodes, requeue their work
├── Internal/
│   ├── HighwayKeys.cs            # EXTENDED — hw:reg:* schema
│   ├── NodeRegistration.cs       # NEW — record encode/decode
│   └── HighwayServerOptions.cs   # EXTENDED — NodeExpiry, PruningEnabled, MaxCatalogBytes

src/Highway.Client/
├── Engine/
│   ├── HeartbeatLoop.cs          # NEW — register once, then cheap liveness beats
│   ├── ServiceDiscoveryCache.cs  # NEW — short-TTL discovery cache for fast-fail
│   ├── HighwayConnection.cs      # EXTENDED — Register/Heartbeat/Depart/Discover/Stats
│   └── HighwayEngine.cs          # EXTENDED — starts/stops the heartbeat loop
├── HighwayClient.cs              # EXTENDED — optional fast-fail before HW.CALL
└── HighwayOptions.cs             # EXTENDED — heartbeat + fast-fail options
```

No new package references. `Highway.Abstractions` is unchanged — `CatalogInfo` and friends already exist from 003.

## Command Designs

### HW.HEARTBEAT — two forms, one command

The catalog is static for a node's lifetime, so sending it every beat would put up to `MaxCatalogBytes` on the wire per node per interval and force a server-side JSON parse to rebuild an index that never changes. Registration and liveness are therefore separate *forms* of one command — separate because they are different operations, one command because `product.md`'s protocol table is read-only and lists `HW.HEARTBEAT <nodeId> <catalogJson>`. Arity is `-2`: the catalog argument is optional.

**Registration form — `HW.HEARTBEAT <nodeId> <catalogJson>` → `+OK`.** Sent once at start, and again only on the re-register signal.

| Phase | Action | Keys locked |
|---|---|---|
| Prepare | validate nodeId (identifier rules) and catalog size; `GET hw:reg:nodes` for the sweep set | — |
| Main | 1. Parse catalog → service names (read-only, for indexing). 2. Read any previous record; remove its services from `hw:reg:svc:*`, then add the new set — so a redeployed node leaves no stale index entries (Req 1 AC7). 3. `SET hw:reg:node:{nodeId}` = `{seen: now, catalog: <verbatim>}`. 4. Add to `hw:reg:nodes` if absent. 5. Sweep registration records already past expiry. | `hw:reg:node:{nodeId}`, `hw:reg:nodes`, each touched `hw:reg:svc:{service}` (Exclusive, Main) |
| Finalize | none | — |

**Liveness form — `HW.HEARTBEAT <nodeId>` → `+OK` | `+REGISTER`.** The steady-state beat.

| Phase | Action | Keys locked |
|---|---|---|
| Prepare | validate nodeId | — |
| Main | `GET hw:reg:node:{nodeId}`. **Absent → reply `+REGISTER`, mutate nothing.** Present → rewrite the record with `seen = now`, catalog bytes untouched, reply `+OK`. | `hw:reg:node:{nodeId}` (Exclusive, Main) |
| Finalize | none | — |

Cost per beat: one small `GET`, one small `SET`, no parse, no index write, and a request payload of roughly `HW.HEARTBEAT` plus the node name. It is independent of catalog size (Requirement 5 AC9).

### Why `+REGISTER` is a correctness requirement, not an optimization

The obvious cheap heartbeat — "bump the timestamp, always reply `+OK`" — is subtly broken. Pruning deletes a node's registration record *and* its `hw:reg:svc:*` index entries. A bare beat arriving afterwards would recreate liveness but not the index, leaving the node **alive and undiscoverable**: `HW.DISCOVER` returns nothing, fast-fail returns 404, and the node sits there serving a queue nobody is told about. Nothing would surface the fault.

Replying `+REGISTER` whenever the record is absent closes that hole, and the record's absence is exactly the right trigger because record and index are written and pruned in the same transaction. The signal makes three otherwise-silent failures self-healing:

| Situation | Without the signal | With it |
|---|---|---|
| Server restarted memory-only | Every node alive but permanently undiscoverable | Next beat re-registers |
| Node pruned after a long GC pause or partition | Node alive, silently unroutable | Next beat re-registers |
| Registration lost to an operator action | Requires a rolling restart to recover | Recovers within one interval |

`+REGISTER` is a RESP simple string, so it is distinguishable from `+OK` without touching the 004.1 error contract — this is a normal reply, not an error, and must not be classified as one.

No periodic full re-registration is layered on top as belt-and-braces: the handshake is deterministic and directly testable, and a "resend every N beats" rule would quietly reintroduce the cost this design removes.

### HW.DISCOVER `<service>` → array of `[nodeId, secondsSinceLastSeen]`

| Phase | Action |
|---|---|
| Prepare | validate service identifier; `GET hw:reg:svc:{service}` |
| Main | For each candidate node: `GET hw:reg:node:{nodeId}` → skip when `now - seen > NodeExpiry`; else emit `[nodeId, secondsSinceLastSeen]` |

Returning the age alongside the id satisfies Requirement 2 AC5 and gives the caller something to reason about rather than a bare boolean. Empty array for unknown/all-stale (AC2). Read-only (AC7).

### HW.STATS `[<service>|<channel>]` → flat field/value array

Reply is a flat `[name, value, name, value, ...]` array — the shape `redis-cli` renders readably and clients parse without a schema, and which extends by appending fields (Requirement 3 AC5).

```
HW.STATS
  → nodes <liveCount> services <count> channels <count> pendingRequests <total>

HW.STATS orders.create
  → kind service queueDepth <n> hosts <liveNodeCount> inFlight <n>

HW.STATS order.events
  → kind channel groups <n> pending <total> backlog <n>
```

A name that is both a service and a channel resolves as a service; the `kind` field makes the resolution explicit rather than ambiguous. Unknown names return zeroed counters with the best-guess `kind` (Requirement 3 AC4).

No cross-key snapshot is attempted — the counters are read under the transaction's locks but describe independently-mutating structures, which Requirement 3 AC7 documents rather than pretends away.

### HW.DEQUEUE (extended)

The existing lazy lease sweep gains a liveness check. For each node in `hw:svc:{service}:nodes`:

```
if registration missing OR (now - seen) > NodeExpiry:      # node is dead
    requeue ALL its processing entries to the queue tail   # Req 4 AC4
    remove it from hw:svc:{service}:nodes + mirror         # Req 4 AC3
    remove its hw:reg:* records                            # Req 4 AC1
else:
    existing per-entry lease sweep (004)                   # unchanged
```

Dead-node requeue is the whole processing list at once; lease expiry remains per-entry. A node can be alive but slow (lease sweep) or simply gone (node sweep), and the two want different granularity.

## Client Design

### Heartbeat loop

```
HeartbeatLoop.RunAsync(stopToken, workToken):
  catalogJson = serialize(catalog.ToCatalogInfo())   # ONCE, at construction — never rebuilt
  register()                                          # registration form, immediately

  while not stopToken:
      await Task.Delay(HeartbeatInterval, stopToken)
      try:
          reply = HW.HEARTBEAT nodeName               # liveness form — no catalog on the wire
          if reply == "REGISTER": register()          # self-heal, do not wait for the next tick
      catch transient: log debug, next beat retries
      catch permanent: log error, next beat retries
  # the loop never dies and never blocks engine drain

register():
      HW.HEARTBEAT nodeName catalogJson               # registration form
```

The immediate first registration matters: a node that only beat on the interval would be invisible to fast-fail for up to one interval after start, making the first call after deployment fail spuriously.

Re-registering the instant `+REGISTER` is seen — rather than on the next tick — keeps the undiscoverable window to one round trip instead of a full interval.

Default `HeartbeatInterval` is 5s against a default `NodeExpiry` of 30s — a 6× margin, so several consecutive failures are tolerated before a healthy node is declared stale. That ratio is the tunable that matters and is documented on both options.

### Graceful departure (Requirement 5 AC5)

A third form carries departure: **`HW.HEARTBEAT <nodeId> BYE`**. The second argument is either a catalog (registration) or the reserved literal `BYE` (departure); the two are unambiguous because a catalog is JSON and always begins with `{`. Arity stays `-2` and the command table in `product.md` is unchanged.

`BYE` runs **the same prune path** `HW.DEQUEUE` uses for a dead node — it does not invent a second teardown routine:

- registration record and `hw:reg:*` index entries removed → the node disappears from discovery immediately rather than after the expiry window
- membership in `hw:svc:{service}:nodes` and its mirror removed
- unacknowledged RPC requests requeued (Requirement 4 AC4)
- **subscriber groups left untouched** (Requirement 4 AC5) — a node that shuts down cleanly still expects its pending messages when it returns

Reusing the prune path is what keeps the two teardown routes from drifting apart; a bug fixed in one is fixed in both.

Departure is best-effort: stop never blocks on it, and a node that is killed rather than stopped simply expires on the normal timeline. That fallback is why departure can be a nicety rather than a guarantee.

### Fast-fail and the discovery cache

```
ExecuteAsync:
  local catalog lookup → 404 if unknown            # 005, unchanged, no network
  if FastFailEnabled:
      hosts = discoveryCache.Get(service)           # short TTL, default 1s
      if hosts is a FRESH result AND hosts.Count == 0:
          return 404 SERVICE_NOT_FOUND data         # nothing enqueued
  ... existing HW.CALL path
```

The rule that makes this safe (Requirement 6 AC5): **only a fresh, successful, empty discovery result causes a 404.** A cache miss, an expired entry, or a failed discovery call all fall through to the normal enqueue path. The cache can therefore delay a fast-fail but can never cause a request to be dropped that would otherwise have been served.

Off by default (AC3): it costs a round trip on cold cache, and that trade belongs to the application.

## Options

| Option | Side | Type | Default | Validation |
|---|---|---|---|---|
| `NodeExpiry` | server | TimeSpan | 30s | > 0; warn when < 3 × client interval |
| `PruningEnabled` | server | bool | true | — (Req 4 AC7) |
| `MaxCatalogBytes` | server | int | 256 KiB | ≥ 1 KiB |
| `HeartbeatEnabled` | client | bool | true | — |
| `HeartbeatInterval` | client | TimeSpan | 5s | > 0 |
| `FastFailEnabled` | client | bool | **false** | — |
| `DiscoveryCacheTtl` | client | TimeSpan | 1s | ≥ 0 (zero disables caching) |

## Error Handling Strategy

Inherits the 004.1 contract wholesale — no new error classes.

| Failure | Server reply | Client reading |
|---|---|---|
| Blank/control-char nodeId or service | `ERR HW_INVALID_ARG` | permanent — configuration bug, log loudly |
| Catalog over `MaxCatalogBytes` | `ERR HW_PAYLOAD_TOO_LARGE` | permanent — log at error, keep beating |
| Unparseable catalog JSON | `ERR HW_INVALID_ARG` | permanent — indicates a client bug |
| Watch conflict | `ERR Transaction failed.` | transient — bounded retry, then next beat |
| Heartbeat fails entirely | n/a | never fatal: RPC and pub/sub keep working, the node just risks appearing stale |

A node that cannot heartbeat is degraded, not broken — it can still call and serve. Only discovery-dependent behavior (fast-fail, operator visibility) suffers. This is stated because the opposite choice — failing the engine when heartbeat fails — would turn an observability outage into a total outage.

## Sequence: Node Lifecycle

```
Engine.StartAsync                Server                      Another caller
────────────────                 ──────                      ──────────────
connect, loops, sweeper
HW.HEARTBEAT node-1 {catalog} ─▶ SET hw:reg:node:node-1      (registration form, once)
                                 index hw:reg:svc:orders.create
                                                          ◀─ HW.DISCOVER orders.create
                                                          ─▶ [[node-1, 0]]
HW.HEARTBEAT node-1           ─▶ +OK   refresh seen          (liveness form, every 5s)
   ... every 5s ...

   [server restarts memory-only — registry empty]
HW.HEARTBEAT node-1           ─▶ +REGISTER
HW.HEARTBEAT node-1 {catalog} ─▶ re-registered, discoverable again

Engine.StopAsync
HW.HEARTBEAT node-1 (expired) ─▶ record written already-stale
                                                          ◀─ HW.DISCOVER orders.create
                                                          ─▶ []          (fast-fail 404)

   next HW.DEQUEUE orders.create ─▶ node-1 stale:
                                    requeue its unacked requests
                                    drop from svc nodes set + registry
                                    (its subscriber group is left alone)
```

## Testing Strategy

| File | Level | Covers |
|---|---|---|
| `Server.Tests/NodeRegistrationTests.cs` | unit | record encode/decode, staleness arithmetic, boundary at exactly `NodeExpiry` |
| `Integration/RegistryTests.cs` | integration | all three commands over RESP; register → discover → stats; unknown names return empty/zeroed |
| `Integration/NodeExpiryTests.cs` | integration | short `NodeExpiry`: stale node leaves discovery (Req 8 AC3); **unacked requests requeued** (AC4); **subscriber group and pending messages survive** (AC5); `PruningEnabled=false` disables it |
| `Client.Tests/Engine/HeartbeatLoopTests.cs` | unit | immediate first beat, interval cadence, payload built once, transient/permanent failure never kills the loop, stops on drain |
| `Client.Tests/Engine/ServiceDiscoveryCacheTts.cs` | unit | TTL expiry, only fresh-empty triggers fast-fail, discovery failure falls through to enqueue |
| `Client.Tests/Engine/HighwayClientTests.cs` | unit (extend) | fast-fail 404 without enqueue; disabled → normal path; local-catalog 404 still short-circuits first |
| `Integration/RegistryLifecycleTests.cs` | integration | real engines: started node is discoverable, gracefully stopped node is not (Req 8 AC7) |

Requirement 8 AC4 and AC5 are the two tests that must not be skipped. AC4 protects at-least-once across node death; AC5 protects durable pub/sub from being silently downgraded by the pruning path. Both are the kind of guarantee that only breaks in production if untested.

## Risks

| Risk | Mitigation |
|---|---|
| Pruning deletes subscriber groups and silently downgrades pub/sub to fire-and-forget | Stated as an invariant in this design; Requirement 4 AC5 has a dedicated non-skippable test |
| Dead-node requeue duplicates work a slow-but-alive node is still processing | Inherent to at-least-once (identical to 004's lease semantics); handlers own idempotency per product G2 |
| Heartbeat interval and expiry misconfigured relative to each other | Validated with a warning when the margin is under 3×; both defaults documented as a ratio, not just values |
| `HW.HEARTBEAT` parsing every catalog costs CPU and bandwidth under many nodes | Resolved by the two-form split: the catalog crosses the wire once per node lifetime, not once per interval; the steady-state beat is a `GET` + `SET` on one small key with no parse |
| A cheap liveness beat silently resurrects a pruned node without its service index, leaving it alive but undiscoverable | The `+REGISTER` reply on a missing record makes this self-healing; it has a dedicated test (Task 11) because the failure is otherwise silent |
| `BYE` token mistaken for a catalog, or vice versa | A catalog is JSON beginning with `{`; `BYE` is a reserved literal. Validation rejects any second argument that is neither |
| Fast-fail cache causes a spurious 404 during a rolling deploy | Only a fresh, successful, empty result fast-fails; off by default; TTL configurable to zero |
| Registry keys grow with historical services never re-registered | `hw:reg:svc:{service}` entries are pruned with their last node; an empty index key is deleted rather than left behind |

## Dependencies & Constraints

- Depends on 004, 004.1 and 005 being merged (all are). Extends `HW.DEQUEUE`, so the 004 suite is the regression gate.
- `Highway.Abstractions` is unchanged — `CatalogInfo` has existed since 003 and is finally consumed here.
- The wire contract 005 pinned is unchanged; three commands are added, none altered.
- Coding standards apply unchanged: .NET 10, file-scoped namespaces, `CancellationToken` on async APIs, xUnit + FluentAssertions + NSubstitute, zero build warnings, no external test infrastructure.

## Cross-References

- Requirements: `docs/features/006-heartbeat-service-registry/requirements.md`
- Command patterns and error contract: `docs/features/004-server-hw-commands/design.md`, `docs/features/004.1-server-remediation/design.md`
- Client engine extended here: `docs/features/005-client-server-communication/design.md`
- Catalog contract consumed: `Highway.Abstractions.CatalogInfo` (feature 003)
- Successor: feature 002 (flight recorder) builds on `HW.STATS` shapes
