# Tasks: Heartbeat & Service Registry

> **Ordering note:** Tasks 1–5 build the server side, 6–9 the client side, 10–13 the coverage, 14 verification. Tasks 4 and 12 carry the two guarantees that must not be skipped: a dead node's unacked RPC work is requeued, and its subscriber group survives.

## Task Dependency Graph

```
T1  (Key schema + NodeRegistration + options)            [independent]
T2  (HW.HEARTBEAT)                        → T1
T3  (HW.DISCOVER)                         → T1, T2
T4  (HW.DEQUEUE dead-node prune + requeue) → T2
T5  (HW.STATS)                            → T1, T2
T6  (Connection helpers)                  → T2, T3, T5
T7  (HighwayOptions extensions)           [independent]
T8  (HeartbeatLoop + engine wiring)       → T6, T7
T9  (Discovery cache + fast-fail)         → T6, T7, T8
T10 (Unit tests — server primitives)      → T1
T11 (Integration — registry commands)     → T2, T3, T5
T12 (Integration — expiry, requeue, group survival) → T4
T13 (Unit + integration — client)         → T8, T9
T14 (Full verification)                   → all
```

Tasks 1 and 7 have no prerequisites and can start together.

## Tasks

- [x] ### Task 1: Key Schema, NodeRegistration, and Server Options

**Fulfills:** Requirement 1 (AC5), 7 (AC2), 4 (AC7)

**Steps:**
1. Extend `HighwayKeys` with the `hw:reg:*` schema per design § "Two structures, one authority": `RegistrationNode(nodeId)`, `RegistrationNodeList()`, `RegistrationService(service)` — plus UTF-8 byte overloads matching the existing convention
2. Create `src/Highway.Server/Internal/NodeRegistration.cs`: encode/decode `{seen, catalog}` and `IsStale(now, expiry)`. Decoding must expose the catalog bytes without copying them, so the liveness form can rewrite `seen` while leaving the catalog verbatim
3. Add to `HighwayServerOptions`: `NodeExpiry` (30s), `PruningEnabled` (true), `MaxCatalogBytes` (256 KiB) — XML docs stating the interval/expiry ratio rationale
4. Confirm the mirror-list rationale is captured in an XML comment on the new keys (main-store strings, not object-store sets, to keep `Prepare` watch-free)

**Done criteria:**
- Key schema and registration record exist with no command depending on them yet; namespacing does not collide with 004 keys

---

- [x] ### Task 2: HW.HEARTBEAT Transaction

**Fulfills:** Requirement 1 (all), 4 (AC2 partial)

**Steps:**
1. Create `HwHeartbeatCommand : HighwayCommandBase` handling all three forms per design § "HW.HEARTBEAT — two forms, one command" and § "Graceful departure". Dispatch on the optional second argument: absent → liveness, `BYE` → departure, otherwise → registration. Reject a second argument that is neither `BYE` nor parseable JSON with `HW_INVALID_ARG`
2. **Liveness form:** `GET hw:reg:node:{nodeId}` → absent replies `+REGISTER` and mutates nothing; present rewrites the record with `seen = now`, catalog bytes untouched, replies `+OK`. No parse, no index write, no allocation proportional to catalog size (Requirement 1 AC2)
3. **Registration form:** validate the catalog via `TryReadPayload` against `MaxCatalogBytes`; parse read-only to derive service names; remove the previous record's services from `hw:reg:svc:*` before adding the new set so a redeployed node leaves no stale entries (Requirement 1 AC7); store the catalog **verbatim**
4. **Departure form (`BYE`):** call the same prune helper Task 4 uses — registration and index removed, `hw:svc:*:nodes` membership removed, unacked RPC work requeued, **subscriber groups untouched**. Factor the prune helper so both this and `HW.DEQUEUE` share one implementation rather than two that can drift
5. Sweep registration records already past expiry while `hw:reg:nodes` is locked (registration form only); honor `PruningEnabled`
6. Register in `HighwayServer.RegisterCommands` with `Arity = -2`
7. Confirm `+REGISTER` is a RESP **simple string**, not an error — the 004.1 classification must not treat it as a failure

**Done criteria:**
- All three forms work; a liveness beat costs one small `GET` + `SET` with no parse; an unknown node gets `+REGISTER`; repeated identical registrations do not grow state (Requirement 1 AC6); empty catalog accepted (AC9)

---

- [x] ### Task 3: HW.DISCOVER Transaction

**Fulfills:** Requirement 2 (all)

**Steps:**
1. Create `HwDiscoverCommand : HighwayCommandBase`: validate the service identifier, `GET hw:reg:svc:{service}`, then filter each candidate by reading its registration and dropping stale ones
2. Reply an array of `[nodeId, secondsSinceLastSeen]` pairs; empty array for unknown service or all-stale hosts — never an error
3. Perform **no mutation** (Requirement 2 AC7) — staleness is filtered from results, pruning is left to `HW.DEQUEUE`/`HW.HEARTBEAT`
4. Register with `Arity = 2`

**Done criteria:**
- Discovery is a lookup, not a scan; stale nodes are excluded even before they are pruned (AC3); a re-beating node reappears with no re-registration step (AC4)

---

- [x] ### Task 4: HW.DEQUEUE — Dead-Node Prune and Requeue

**Fulfills:** Requirement 4 (AC1, AC3, AC4, AC5, AC6), discharges the 004.1 node-pruning deferral

**Steps:**
1. Extend `HwDequeueCommand`'s existing sweep: for each node in `hw:svc:{service}:nodes`, read its registration; when missing or stale, take the dead-node path instead of the per-entry lease path
2. Dead-node path: requeue **all** its processing entries to the service queue tail, then remove it from `hw:svc:{service}:nodes` and its mirror list, then delete its `hw:reg:*` records
3. **Do not touch `hw:ch:*:grp:{node}:*`** — subscriber groups outlive the process by contract (005 Req 9 AC3). Add a comment at the deletion site saying so, because this is the line a future change is most likely to get wrong
4. Keep the existing per-entry lease sweep for nodes that are alive but slow — the two paths differ in granularity by design
5. Honor `PruningEnabled = false`: fall back to lease-only behavior
6. Confirm idempotency and concurrency safety: two concurrent dequeues pruning the same node lose no requests

**Done criteria:**
- A dead node's in-flight RPC work is recovered and its lock/sweep cost disappears from `HW.DEQUEUE`; its pub/sub group is provably untouched

---

- [x] ### Task 5: HW.STATS Transaction

**Fulfills:** Requirement 3 (all)

**Steps:**
1. Create `HwStatsCommand : HighwayCommandBase` supporting the no-arg, service, and channel forms per design § "HW.STATS"
2. Reply a flat `[name, value, ...]` array including an explicit `kind` field; resolve a name that is both a service and a channel as a service
3. Unknown names return zeroed counters with a best-guess `kind`, never an error (Requirement 3 AC4)
4. Read-only and safe to poll (AC6); document the no-snapshot-consistency caveat (AC7) in XML docs
5. Register with `Arity = -1` (name optional)

**Done criteria:**
- An operator can read queue depth, host count, in-flight count, group counts and backlog from `redis-cli` with no tooling

---

- [x] ### Task 6: Connection Helpers

**Fulfills:** Requirement 7 (AC1), client prerequisite

**Steps:**
1. Add to `IHighwayConnection` and `HighwayConnection`, one method per heartbeat form so the call sites read as what they are:
   - `RegisterAsync(nodeId, catalogJson, ct)` → registration form
   - `HeartbeatAsync(nodeId, ct)` → `HeartbeatReply` enum (`Ok` | `ReRegisterRequired`), mapping the `+OK`/`+REGISTER` simple strings
   - `DepartAsync(nodeId, ct)` → the `BYE` form
   - `DiscoverAsync(service, ct)` → `IReadOnlyList<(string NodeId, TimeSpan SinceLastSeen)>`
   - `StatsAsync(name?, ct)` → field/value map
2. `+REGISTER` is a normal reply, not a failure: it must map to the enum and must never be classified as a transport or transient error by the 004.1 pipeline
3. Parse the `HW.DISCOVER` pair array and the `HW.STATS` flat array in exactly one place, consistent with how `ReceiveAsync` owns its shape
4. Route all of them through the existing `SendAsync` pipeline so 004.1 classification and bounded transient retry apply unchanged
5. Unit tests for reply-shape parsing, including empty results and both heartbeat replies

**Done criteria:**
- All wire shapes for the registry live in `HighwayConnection` alongside the other eight commands

---

- [x] ### Task 7: HighwayOptions Extensions

**Fulfills:** Requirement 5 (AC7), 6 (AC3)

**Steps:**
1. Add `HeartbeatEnabled` (true), `HeartbeatInterval` (5s), `FastFailEnabled` (**false**), `DiscoveryCacheTtl` (1s) with XML docs
2. Document `HeartbeatInterval` against the server's `NodeExpiry` as a ratio (default 6×), not just as a value — the relationship is what matters
3. Extend `HighwayOptionsValidator`: interval > 0, cache TTL ≥ 0, with descriptive messages naming the offending value
4. Unit tests for defaults and every validation rule

**Done criteria:**
- Options present, validated, and defaulted; fast-fail off by default

---

- [x] ### Task 8: HeartbeatLoop and Engine Wiring

**Fulfills:** Requirement 5 (all)

**Steps:**
1. Create `src/Highway.Client/Engine/HeartbeatLoop.cs` per design § "Heartbeat loop": serialize `ICatalog.ToCatalogInfo()` **once** at construction and retain it for re-registration; send the **registration form** immediately at start
2. Steady-state loop sends the **liveness form** — the catalog must never cross the wire on a normal beat (Requirement 5 AC2, AC9)
3. On a `+REGISTER` reply, re-send the registration form **immediately** rather than waiting for the next tick, then resume the liveness cadence (Requirement 5 AC3) — this keeps the undiscoverable window to one round trip
4. Follow the established two-token lifecycle (`stopToken`/`workToken`) used by the other loops; catch transient and permanent failures separately, log, and continue — the loop never dies and never blocks drain
5. Wire into `HighwayEngine.StartAsync` after doorbells and channel subscription; skip entirely when `HeartbeatEnabled == false`
6. On `StopAsync`, send `HW.HEARTBEAT <node> BYE` best-effort, never blocking or throwing during shutdown
7. Unit tests: registration sent once at start then liveness only; catalog serialized exactly once; `+REGISTER` triggers immediate re-registration; transient/permanent failure survival; `BYE` on stop; nothing sent when disabled

**Done criteria:**
- A node registers itself with no application code; steady-state beats are catalog-free; a wiped registry self-heals within one beat; heartbeat failure degrades discovery only, never RPC or pub/sub

---

- [x] ### Task 9: Discovery Cache and Fast-Fail

**Fulfills:** Requirement 6 (all)

**Steps:**
1. Create `ServiceDiscoveryCache`: short-TTL per-service cache over `DiscoverAsync`, TTL from `DiscoveryCacheTtl` (zero disables caching)
2. Extend `HighwayClient.ExecuteAsync` per design § "Fast-fail and the discovery cache": after the existing local-catalog 404, and only when `FastFailEnabled`
3. Implement the safety rule exactly: **only a fresh, successful, empty result returns 404.** Cache miss, expired entry, and failed discovery all fall through to the normal enqueue path (Requirement 6 AC5)
4. Confirm the disabled path is byte-for-byte the current behavior (AC6) and the local 404 still costs no round trip (AC7)
5. Unit tests covering each fall-through case, not just the happy path

**Done criteria:**
- Fast-fail returns 404 in milliseconds when nobody hosts a service, and provably cannot drop a request that would otherwise be served

---

- [x] ### Task 10: Unit Tests — Server Primitives

**Fulfills:** Requirement 8 (AC1 server half)

**Steps:**
1. `tests/Highway.Server.Tests/NodeRegistrationTests.cs`: encode/decode round trip, catalog preserved verbatim, staleness arithmetic including the exact-boundary case, sentinel-stale helper
2. Extend `HighwayKeysTests` for the `hw:reg:*` schema
3. Extend `HighwayServerOptionsTests` for the new options and defaults

**Done criteria:**
- Registry primitives covered with no running server

---

- [x] ### Task 11: Integration Tests — Registry Commands

**Fulfills:** Requirement 8 (AC2), 1–3, 7 end-to-end

**Steps:**
1. `tests/Highway.Integration.Tests/RegistryTests.cs` driving raw RESP against `HighwayTestServer`
2. Register → discover → stats round trip; two nodes hosting one service both discoverable; a node hosting nothing registers cleanly
3. Repeated identical registrations do not grow state (Requirement 1 AC6); unparseable catalog rejected with `HW_INVALID_ARG`; oversized catalog rejected with `HW_PAYLOAD_TOO_LARGE`
3a. **The `+REGISTER` handshake** (Requirement 1 AC3, AC5): a liveness beat for a never-registered node returns `+REGISTER` and mutates nothing; after registration a liveness beat returns `+OK`; after the node is pruned a liveness beat returns `+REGISTER` again — proving a wiped registry cannot leave a live node silently undiscoverable
3b. A liveness beat preserves the stored catalog byte-for-byte — a node stays discoverable across beats without ever resending it (Requirement 1 AC2, AC8)
3c. Re-registering a **changed** catalog removes the old services from the index and adds the new ones (Requirement 1 AC7)
3d. `HW.HEARTBEAT <node> BYE` removes the node from discovery immediately, requeues its unacked RPC work, and **leaves its subscriber group intact**
3e. A second argument that is neither `BYE` nor valid JSON is rejected with `HW_INVALID_ARG`
4. Unknown service/channel: `HW.DISCOVER` returns empty, `HW.STATS` returns zeroed counters — neither errors
5. `HW.STATS` in all three forms returns the documented fields; `kind` disambiguates a name that is both
6. Registrations survive an AOF restart via `HighwayTestServer.Restart()` (Requirement 1 AC8)

**Done criteria:**
- All three commands proven over real RESP with no external infrastructure

---

- [x] ### Task 12: Integration Tests — Expiry, Requeue, and Group Survival

**Fulfills:** Requirement 8 (AC3, AC4, AC5), Requirement 4

**Steps:**
1. `tests/Highway.Integration.Tests/NodeExpiryTests.cs` with a short `NodeExpiry` via the 004.1 test-server configuration delegate
2. A node that stops heartbeating disappears from `HW.DISCOVER` after the window (AC3)
3. **Non-skippable:** a dead node holding unacknowledged requests → next `HW.DEQUEUE` from another node returns that work; nothing is lost (Requirement 4 AC4)
4. **Non-skippable:** a dead node's subscriber group and its pending messages still exist after pruning, and drain when the node returns under the same name (Requirement 4 AC5) — this is the test that stops a future change from silently downgrading durable pub/sub
5. Pruning removes the node from `hw:svc:{service}:nodes` so `HW.DEQUEUE` no longer sweeps it (Requirement 4 AC3)
6. `PruningEnabled = false` disables the node sweep while leaving lease recovery intact (Requirement 4 AC7)

**Done criteria:**
- Both delivery guarantees hold across node death; the RPC/pub-sub asymmetry is proven, not just documented

---

- [x] ### Task 13: Client Tests — Heartbeat, Cache, Fast-Fail, Lifecycle

**Fulfills:** Requirement 8 (AC1 client half, AC6, AC7)

**Steps:**
1. `Client.Tests/Engine/HeartbeatLoopTests.cs` and `ServiceDiscoveryCacheTests.cs` with a mocked connection, per the coverage table in design § "Testing Strategy"
2. Extend `Client.Tests/Engine/HighwayClientTests.cs`: fast-fail 404 without enqueue; every fall-through case enqueues normally; disabled → unchanged behavior; local 404 short-circuits first (AC6)
3. `Integration/RegistryLifecycleTests.cs` through real engines: a started node becomes discoverable; a gracefully stopped node stops being discoverable promptly rather than after the expiry window (AC7)
4. Add the class to the `SubscriberRecorderCollection` xUnit collection if it asserts on the shared recorder — the parallelism trap recorded in 005's completion note

**Done criteria:**
- Client registry behavior covered at both levels; the graceful-departure path proven end-to-end

---

- [x] ### Task 14: Full Verification

**Fulfills:** Requirement 8, regression safety

**Steps:**
1. `dotnet build Highway.slnx` — zero warnings, zero errors
2. `dotnet test Highway.slnx` — full suite green, no external infrastructure
3. Confirm the 348 pre-existing tests still pass; any changed test is named here with its justification
4. Confirm the 004/005 wire contract is unchanged — three commands added, none altered
5. Re-run the integration suite a second time to catch parallelism flakiness before it reaches the branch
6. Record the final test count and per-project breakdown below

**Done criteria:**
- Green build, green suite twice, wire contract unchanged, roadmap updated to mark 006 complete

**Result:** Green build (0 warnings), full suite green twice.

| Project | Before 006 | After 006 |
|---|---|---|
| Highway.Abstractions.Tests | 2 | 2 |
| Highway.Client.Tests | 132 | 158 |
| Highway.Server.Tests | 83 | 101 |
| Highway.Integration.Tests | 131 | 173 |
| **Total** | **348** | **434** |

Wire contract unchanged: three commands added (`HW.HEARTBEAT`, `HW.DISCOVER`,
`HW.STATS`), none of the existing nine altered. `HW.DEQUEUE` gained a dead-node
sweep but its request/reply shape is identical.

---

## Completion Record

### Design change made during implementation: no stale sweep in HW.HEARTBEAT

The design had the registration form sweep expired registration records while it
already held `hw:reg:nodes` locked. Implementing it exposed that this is not
merely redundant but **actively harmful**, and two Task 12 tests caught it:

- `DeadNode_UnacknowledgedRequests_AreRequeuedNotLost`
- `DeadNode_IsRemovedFromTheServiceWorkerSet`

The registration record is the only evidence `HW.DEQUEUE` uses to recognise a
dead node. Deleting it in the heartbeat path removed that evidence *before* the
node's unacknowledged RPC work was recovered, so the work was stranded until the
far slower per-entry lease sweep (5 minutes by default) found it — silently
weakening the at-least-once guarantee this feature exists to strengthen.

Pruning now happens only where the full teardown can actually be performed:
`HW.DEQUEUE` (which locks the service's queue and processing lists) and the
`BYE` form (which knows its own catalog). The trade-off is that a node whose
services are never dequeued again leaves one small record behind; leaking a key
is strictly better than losing in-flight work. The reasoning is recorded in the
`HwHeartbeatCommand` XML docs so the sweep is not reintroduced as tidy-looking
housekeeping.

### Design change made during implementation: registration is part of startup

`HeartbeatLoop` originally registered as the first action of its background
task, which meant `IHighwayEngine.StartAsync` could return — and the engine
report `Running` — before the node existed in the registry.
`RegistryLifecycleTests.TwoNodes_BothDiscoverable_...` caught it: the second
node was not yet discoverable when the assertion ran.

This is exactly the spurious-fast-fail-after-deployment problem the design set
out to avoid, so registration moved into the engine's start sequence and is
awaited (`HeartbeatLoop.RegisterAsync`). A node is now discoverable by the time
`StartAsync` returns. Registration failure is still non-fatal: the engine starts
regardless, and the `+REGISTER` handshake recovers on the next beat.

### Safety rule added beyond the spec: unregistered nodes are never pruned

The spec did not say what to do about a node that holds no registration record at
all — which is the normal state when a client runs with
`HeartbeatEnabled = false`. Pruning on "no record" would have requeued a healthy
worker's in-flight work on *every* dequeue, turning a supported configuration
choice into a duplicate-execution storm.

`HW.DEQUEUE` therefore prunes only nodes that hold a registration record which is
stale. A node with no record is not participating in the registry and is left to
the per-entry lease sweep, exactly as before 006. Covered by
`NodeExpiryTests.UnregisteredNode_IsNeverPruned` and
`RegistryLifecycleTests.HeartbeatDisabled_NodeStaysOutOfTheRegistry_ButStillServes`.

### Implementation note: binary registration record

`NodeRegistration` frames the record as `[i64 BE seenTicks][catalog bytes]`
rather than the `{"seen":…,"catalog":…}` JSON sketched in the design. The
liveness form must refresh `seen` while leaving the catalog byte-for-byte
untouched; with a fixed 8-byte header that is a copy of the tail, whereas a JSON
envelope would mean parsing and re-emitting the catalog on every beat — the
exact cost the two-form split exists to remove. Consistent with `Envelope`.

### Debt discharged

`hw:svc:{service}:nodes` no longer grows without bound: a stale node is removed
from the worker set and its mirror by the next dequeue, so `HW.DEQUEUE` stops
locking and sweeping dead nodes' processing lists. This closes the pruning
deferral recorded in `004.1/requirements.md` § "Non-Goals".

### Owed to feature 010

Per the living-conformance rule, 006 changes the protocol and both option types,
so it owes the samples an update: participants become discoverable, and the
Storefront app should gain a `discover` / `stats` command. The samples do not
exist yet (010 is unimplemented), so nothing is stale today — but 010 must
include those commands when it is built, and this note is the reminder.
