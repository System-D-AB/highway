# Feature: The RocksDB Engine — Replacing Garnet Without Touching the Protocol

## Introduction

Highway's hardest unmet constraint is a property of its storage engine, not of its design.
[C4.6](../../product/constraints.md) — *storage growth is bounded over time* — has been
investigated three times and recorded as **measured not to work** across every configuration:
`TruncateUntil` is logical only, retired AOF segment files are never deleted, and growth is
*"strictly linear in total history."* The stated remedy is a periodic planned restart against a
fresh data directory.

The diagnosis is in
[`../../product/research/2026-09-11-rocksdb-http-and-replication.md`](../../product/research/2026-09-11-rocksdb-http-and-replication.md).
In short: Highway's queue is a Garnet **List object**, its lease table a **SortedSet object**, its
registry and group membership **Set objects** — each a large, shared, mutable managed value taken
under an exclusive lock. That is the same root cause a sibling project measured independently and
named: *"a posting list must be a value holding many members … which makes every index a large,
shared, mutable managed object. That single consequence produces the lock contention, the
allocation floor, the eviction/replay fragility, and the memory cost."*

On an ordered key-value engine none of those structures is an object. A queue is a key range, a
lease table is a key range ordered by expiry, a set is a prefix. Space reclamation is compaction,
which is what an LSM does for a living.

**This feature replaces the storage engine and nothing else.**

### What this feature is

- **A new storage layer on RocksDB**, behind a seam that exposes no engine types.
- **Our own RESP server** on a Kestrel `ConnectionHandler` — Garnet supplied the socket and the
  codec, and both have to be replaced when it goes.
- **A port of the 23 `HW.*` command files** from Garnet's `Prepare`/`Main` transaction model to a
  single transactional write path.
- **The removal of Garnet entirely** — submodule, package references, `Tsavorite.core`, the object
  store, the AOF.

### What this feature is not

- **Not a protocol change.** `HW.*` stays, RESP stays, `HIGHWAY-PROTOCOL.md` stays true. HTTP was
  evaluated at length and **rejected** — D1.
- **Not a client change.** `Highway.Client` keeps SE.Redis and is expected to compile and pass
  unmodified. If it needs changing, that is a finding, not a task.
- **Not replication.** Deliberately stage 2, decided 2026-09-11. See
  [research Part VI](../../product/research/2026-09-11-rocksdb-http-and-replication.md).
- **Not a rewrite of command semantics.** Lease sweep, attempt counting, dead-lettering, delayed
  promotion, job firing and byte accounting keep their current behaviour. Their *mechanics* change;
  their observable results must not.
- **Not a performance project.** C5 stands: Highway claims no characterised throughput. Faster is
  expected and is not the point — **OD1** exists because we cannot currently tell whether it
  matters.

---

## Decisions — RESOLVED (2026-09-11)

Settled in the research document and by the user on 2026-09-11.

| | Decision | Why |
|---|---|---|
| **D1** | **RESP over TCP stays. No HTTP.** | With .NET-only positioning (research Part VIII), HTTP's advantages — multi-language producers, curl-ability — are worth nothing, while SE.Redis is free, proven, and already working. Decisive reason below |
| **D2** | **Keeping the protocol makes this feature engine-only** | `HW.QCLAIM` stays `HW.QCLAIM`; `Highway.Client` is untouched; `HIGHWAY-PROTOCOL.md` stays true. **One variable changes**, so a red assurance run points at the storage layer rather than at three simultaneous changes |
| **D3** | **Kestrel `ConnectionHandler` for the socket**, not Garnet's `Networking/` | The socket layer benefits most from being maintained by Microsoft and shipped with each .NET release. Garnet's is excellent and self-contained, but it is ~2 100 more lines owned forever, in the category where someone else finding the bug is worth most |
| **D4** | **Copy `RespWriteUtils.cs`; write our own reader** | MIT, 907 lines, **zero** Tsavorite coupling, pure output formatting — free and low-risk. The reader is the opposite: we need ~15% of Garnet's surface (inbound is always an array of bulk strings) and the risk is partial frames at buffer boundaries, which a focused reader plus a byte-by-byte split test addresses better than inheriting 1 333 lines of unused surface |
| **D5** | **SE.Redis stays a pinned NuGet package. Not forked** | Forking converts this into the custom-protocol option while keeping RESP's constraints as a souvenir — the worst of both. The decision is also **reversible**: nothing here forecloses a surgical fork later if profiling demands it |
| **D6** | **RocksDB is the engine** | Ordered key iteration, compaction that physically reclaims space, `WriteBatch` for multi-key atomicity, and `GetUpdatesSince` as the primitive stage 2 will need |
| **D7** | **Two of the four replication-era rules are adopted now; two are deferred** | The chokepoint and effects-never-intent earn their place on single-node merit. The epoch and the primary-only gate serve replication and can wait — a broker's data is drainable, so a later format change is a maintenance window, not a migration |

> **Why D1, stated once so it is not relitigated.** HTTP was the working assumption for most of the
> investigation, on three arguments that did not survive: multi-language clients (moot under
> .NET-only positioning), reverse-proxy failover (withdrawn — Highway is a standalone broker with
> no web tier), and a free client transport (SE.Redis is equally free and already integrated). The
> compatibility concern that remained was also overstated: we ship `Highway.Client`, so we pin the
> SE.Redis version, and SE.Redis has first-class support for partial servers via `CommandMap`.

---

## Requirements

### Requirement 1: The wire and the client do not change

`HW.*` command names, argument order and reply shapes are unchanged. `Highway.Client` is not
modified.

> **Amendment 2026-09-14, revised 2026-09-15:** the distributed cache (feature 026)
> lives in `src/Highway.Client/Caching/`, so its removal (T5.2) necessarily touches the
> client — found in review; R1 as originally worded contradicted it. Resolution: the
> cache is an add-on and its removal is a **task inside 041-garnet-removal** (it dies
> with the engine that made it free); that task's client diff is the one recorded
> justification. R1's empty-diff criterion applies to every other change in features
> 038–041.

#### Acceptance Criteria

1. `HIGHWAY-PROTOCOL.md` requires **no** correction on account of this feature. If it does, that
   is a defect in the port.
2. `Highway.Client` compiles and its test suite passes **with no source change**. Any required
   change is triaged as a finding and justified in `runs.md`, not absorbed silently.
3. SE.Redis connects to the new server and issues `HW.*` commands via `ExecuteAsync` exactly as
   today.
4. A `git diff` over `src/Highway.Client/` at the end of the feature is empty, or every hunk in it
   has a recorded justification.

### Requirement 2: Garnet is gone

#### Acceptance Criteria

1. No `ProjectReference` or `PackageReference` to any `Garnet.*` or `Tsavorite.*` assembly remains
   in any project.
2. The `libs/garnet` submodule is removed from `.gitmodules` and the working tree.
3. `RespWriteUtils.cs` is **vendored** under its MIT notice, with the Microsoft copyright header
   intact and an entry in `THIRD-PARTY-NOTICES.md`. It is the only Garnet-derived source in the
   repository.
4. A test asserts no type from a `Garnet.*` or `Tsavorite.*` namespace appears in any public or
   internal API surface.

### Requirement 3: A storage seam with no engine types on it

One interface — working name `IHighwayStore` — derived from the 15 primitives the commands
actually use, with **no RocksDB type on it**.

#### Acceptance Criteria

1. The seam covers the four families the commands use, and only those:
   **KV** (`Get`/`Set`/`SetEx`/`Delete`/`Increment`), **list** (push both ends, pop left, length),
   **ordered set** (add/remove/range/length), **membership set** (add/remove).
2. `Highway.Server` compiles against the seam with **no RocksDB type** on it. If it cannot, the
   seam is wrong and the port does not start — this is **Gate G1**.
3. **Two implementations exist**: RocksDB, and an in-memory one the tests use.
4. The seam is tested directly, independent of any command or any server.

### Requirement 4: One transactional write path

Every state change commits through a single `WriteBatch`. Nothing durable lives outside RocksDB.

#### Acceptance Criteria

1. There is exactly one place in the codebase where a batch is committed.
2. A command that fails part-way leaves **no** partial state — proven by a test that injects a
   failure between two writes that must be atomic.
3. `FlightRecorder` remains the documented exception: *volatile, in-process, lost on restart*, and
   therefore honestly outside the durability guarantee rather than accidentally outside it.
4. No in-memory state is required to reconstruct queue, lease, registry or group state after a
   restart.

### Requirement 5: Effects, never intent

#### Acceptance Criteria

1. **No clock is read inside a transaction.** Any `DateTime.UtcNow` is read *before* the batch is
   opened and the resulting absolute value is written *into* it.
2. The lease sweep persists the rows it produced, never an instruction to sweep.
3. A test asserts that replaying the WAL after an ungraceful kill produces byte-identical state —
   the property `HwQClaimCommand.Main`'s current `DateTime.UtcNow.Ticks` read cannot offer under
   Garnet's `AofEntryType.StoredProcedure` replay.

### Requirement 6: A RESP server that SE.Redis can talk to — and nothing more

#### Acceptance Criteria

1. Inbound parsing handles **arrays of bulk strings**, which is all a Redis client sends, and
   handles a frame split at **every byte boundary** — proven by a test that feeds each message one
   byte at a time.
2. Outbound supports the five RESP2 types the `HW.*` replies use.
3. The handshake subset SE.Redis requires is implemented — and **no more**. Commands outside the
   declared subset return a clear error naming the subset, never a plausible-looking `+OK`.
4. TLS is served by Kestrel's endpoint configuration, with ALPN configured for a non-HTTP protocol.
5. The implemented subset is **documented** in `HIGHWAY-PROTOCOL.md`, so no future reader assumes
   general Redis semantics hold.
6. `CommandMap` and `ConfigurationOptions` are used to suppress SE.Redis probing rather than
   implementing commands to satisfy it.

### Requirement 7: The doorbell stays best-effort

`SUBSCRIBE`/`UNSUBSCRIBE` plus push frames, as a lossy fan-out to connected subscribers.

#### Acceptance Criteria

1. A dropped doorbell costs **latency only** — proven by running the assurance rig with doorbells
   disabled entirely (`DoorbellsEnabled == false`) and passing every invariant.
2. Subscribed-connection restricted mode is honoured, per RESP2.
3. No delivery guarantee depends on pub/sub. `BackstopSweeper` remains the correctness path.

### Requirement 8: The assurance rig is the gate

#### Acceptance Criteria

1. **I1** queue completeness, **I2** no phantoms, **I3** RPC never silent, **I4** pub/sub reaches
   every live group, **I5** duplicates counted — all green, **including the turbulence phase with
   ungraceful kills**.
2. The rig's applications are **not modified** to accommodate the new engine.
3. Requirement 6 of feature 032 — *"the broker's own record corroborates"* — is re-pointed at the
   new storage. This is the only part of the rig this feature may touch.
4. A run is recorded in `assurance/RUNLOG.md` in the house pattern.

### Requirement 9: C4.6 is settled, and the parked constraints are decided rather than forgotten

A rewrite is the cheap moment for exactly the constraints blocked on *"this would be breaking."*

#### Acceptance Criteria

1. **C4.6** carries a measured status against the new engine — the same traffic profile that
   produced the 102 MB / 205 MB figures, re-run, with the result recorded either way.
2. **C4.1** (retention, 100 days — *"needs a breaking framing change first"*), **C4.7** (the byte
   budget bounds a queue, not the process), **C9** (TTL) and **C19** (change feed) each receive an
   explicit decision: **adopted in this feature, or deferred with a reason.** Deciding by omission
   is a failure of this requirement.
3. Every constraint whose *mechanics* this feature changes carries a dated amendment in
   `constraints.md` — the register's value is that its statuses can be trusted.

### Requirement 10: A transport seam, kept even though HTTP is rejected

#### Acceptance Criteria

1. Command handlers do not know which protocol delivered the request.
2. A test constructs and dispatches a command without any socket, proving the seam holds.

This costs almost nothing now and is the escape hatch if D1 is ever revisited — including for the
narrow case research Part VIII names: a **producer-only** HTTP ingress (`send`, `publish`, `call`),
which has no lease state machine and therefore no second conformance surface.

### Requirement 11: Our own auth — config-file users, connection-level, binary

Garnet's ACL leaves with Garnet. Its replacement is deliberately simpler, because 012
already learned that Highway's real model is connection-level shared credentials — the
per-command roles were specced and descoped there, and the category machinery only ever
produced the `+@all -@dangerous` trap.

#### Acceptance Criteria

1. Users live in the server config (`users`: name + **password hash** — PBKDF2 or
   equivalent; never plaintext in the file). A documented way to produce the hash ships
   with it.
2. `AUTH password` and `AUTH user password` are both accepted — whichever the SE.Redis
   connection string implies. The client-side experience is unchanged:
   `user:pass@host` in the connection string works as today.
3. Enforcement is **binary and at the connection**: an unauthenticated connection may
   send `AUTH` and `PING` only; anything else returns `-NOAUTH` with a sentence naming
   the fix. There is no per-command authorization — the server serves only `HW.*` plus
   the handshake subset, so authenticated *is* authorized. Roles are deferred until
   someone needs them.
4. C6.x semantics are preserved and re-proven: auth required by default,
   `WithoutAuthentication()` remains the explicit opt-out, loopback remains exempt,
   failures remain permanent and legible (the client's existing
   `HighwayAuthenticationException` path works unmodified).
5. The 012 Garnet findings are **retired with dated amendments** in `constraints.md`
   and `research.md`: `@dangerous` categories and the `nopass` bypass cannot exist in a
   model with no categories and no default user. C6.x carries its mechanics amendment
   (T6.4's list gains C6.x).

---

## Non-Goals

- Replication, failover, multi-node anything. Stage 2.
- Any change to the three verbs, the attribute model, or assembly scanning.
- The distributed cache (feature 026). It exists because Garnet was underneath and its native
  `GET`/`SET` were free. **It goes**, and its removal is announced rather than discovered.
- Characterising throughput. Wanted, and it is **OD1**, not a deliverable here.
- HTTP, WebSocket, SSE, gRPC or a custom binary protocol. All evaluated; all rejected under D1.

---

## Open decisions

| | Question | Owner |
|---|---|---|
| **OD1** | **What throughput does Highway target?** No figure exists. It has now blocked four separate design decisions in the research document and is the cheapest open question to close | Phase 0 |
| **OD2** | Which of C4.1, C4.7, C9, C19 this feature adopts (R9.2) | Phase 0 |
| **OD3** | The exact SE.Redis command subset and `CommandMap` configuration — **verify against the pinned version, do not assume** | Phase 1 spike |
| **OD4** | The key layout: prefixes and encodings for queue, lease, delayed, DLQ, registry and group keys. Cheap to change before the first write, a drain-and-restart afterwards | Phase 2 |
| **OD5** | Whether `SETEX` expiry is an expiry field filtered on read, or a compaction filter | Phase 2 |
