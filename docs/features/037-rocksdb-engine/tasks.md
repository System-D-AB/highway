# Feature 037 — The RocksDB Engine: Tasks

> **Decomposed 2026-09-14; re-cut 2026-09-15.** This feature's phases execute as
> **four features** shaped by the product's spine (the three verbs), not by process
> scaffolding; 037 remains the architecture authority (D1–D7, R1–R11, OD1–OD5, gates)
> they cite instead of restating. The 09-15 re-cut, by the owner's direction: no spike
> feature — **G0 is settled by 040-resp-server's first passing SE.Redis test**, and the
> RocksDB-mechanics questions are answered by imported sibling evidence
> (`C:\Software\ai\stow-rocksdb\spike`, spec `v2-001-engine-bakeoff`) plus 038's
> contract tests; and no cache feature — the cache is an add-on that existed because
> Garnet was underneath, so it is a **task inside 041**, not a headline.
>
> | 037 phase | Executes as |
> |---|---|
> | Phase 0 decisions (OD1/OD2, WAL sync, read-view) | **038-storage-engine T0** — decisions on paper against imported evidence |
> | Phase 2 (seam, stores, write path — G1) | **038-storage-engine** |
> | Phase 3 (port the 23 commands) | **039-command-port** |
> | Phase 1 + 4 (G0, RESP server, auth R11, doorbells, OD3) | **040-resp-server** (+ the embedded test-server replacement) |
> | Phase 5 (Garnet + cache removal) + Phase 6 (proof — G2/G3/G4) | **041-garnet-removal** |
>
> The task list below is kept as the original inventory; per-task state lives in the
> four feature specs.

**T1 comes before everything, and it is half a day.** The entire feature rests on D1 — that
SE.Redis will talk to a server we wrote. That is the one assumption which, if wrong, invalidates the
plan rather than delaying it. A stub that answers `PING` and round-trips one `HW.*` command settles
it on day one instead of in month two.

After that the order is: **the seam, then the store, then the commands, then the socket.** Phases 2
and 3 are testable with no network at all — porting the commands onto an in-memory store makes the
command logic unit-testable in-process for the first time, which is a better position than it is in
today. The RESP server (Phase 4) comes last of the build phases because by then the thing it
dispatches to already works.

**Nothing here touches `Highway.Client` or `HIGHWAY-PROTOCOL.md`.** If either needs changing, that
is a finding to record, not a task to do.

---

## Phase 0 — settle what blocks design

### - [ ] T0.1 — OD1: a throughput target

*Requirements:* OD1
**Done when:** a number exists, with the workload shape it assumes, recorded in
`../../product/constraints.md` against C5. It need not be ambitious — *"2 000 msg/s at 8 KB, 20
queues, 40 consumers"* is a target; *"uncharacterised"* is not.
**Why first:** it has already blocked four separate decisions in the research document, and it
governs RocksDB tuning (T2.4), whether batching matters, and whether D1 ever deserves revisiting.

### - [ ] T0.2 — OD2: which parked constraints this feature adopts

*Requirements:* R9.2
**Done when:** C4.1, C4.7, C9 and C19 each carry **adopted** or **deferred, because …** in
`constraints.md`.
**Note:** C19 is close to free here — `GetUpdatesSince` is exactly the durable, ordered, resumable
primitive it was postponed for want of. Adopting it is a decision, not a discovery.

---

## Phase 1 — prove the protocol decision

### - [ ] T1 — The SE.Redis handshake spike *(gate G0)*

*Requirements:* R6.3, R6.6
**Done when:** a throwaway Kestrel `ConnectionHandler` answers `PING` and echoes one `HW.PING`-style
command, and **SE.Redis connects to it and round-trips that command via `ExecuteAsync`** — with the
`CommandMap` and `ConfigurationOptions` needed to get there written down as the answer to OD3.
**Fails how:** if SE.Redis cannot be configured to accept a partial server, **stop and reopen D1.**
Everything downstream assumes this works.
**Keep:** the configuration, not the code.

---

## Phase 2 — the seam and the store

### - [ ] T2.1 — `IHighwayStore` from the existing call sites

*Requirements:* R3.1, R10.1
**Done when:** the interface covers the four families in `design.md` §1 and **nothing speculative**,
derived by reading the ~106 existing call sites rather than by designing forward. No RocksDB or
Garnet type appears on it.

> **A draft seam already exists** at `src/Highway.Server/Storage/IHighwayStore.cs`
> (plus `IStoreSnapshot.cs`, `IStoreBatch.cs`), written from a full inventory of the
> current Garnet call sites. It compiles against `Highway.Server` with no engine type
> on it. Two findings from that inventory are baked into it and worth knowing before
> touching it:
> - **`SetMembers` is new.** Garnet's commands never called it — they kept a Main-store
>   mirror string beside every Set because a `Prepare`-phase Set read registered a watch
>   the exclusive lock then failed (004.1). With no `Prepare`, the mirrors collapse
>   (their own task is T3.3), and a set read is just `SetMembers`.
> - **`Increment` carries the per-queue seq, not just the channel seq.** List keys need a
>   monotonic per-queue sequence allocated *in the same batch* — the B1 trap documented
>   in the reference material.
> - **`DeleteRange` is on the seam.** Group retirement and node decommission are one range
>   delete over a key prefix, not an enumerate-and-`DELETE` loop — and that range delete
>   plus compaction is the mechanical answer to C4.6 (T6.3).
>
> **Reference material** is copied under `reference/`:
> - `reference/stow-engine/` — the RocksDB source for every primitive here, from a sibling
>   project (the code you port).
> - `reference/stow-tech/` — the sibling's design docs (`keyspace.md`, `document-layout.md`,
>   `storage-model.md`, `transactions.md`, `index-structures.md`) that explain the physical
>   layout and, crucially, the failure/replay behaviour the code alone does not show —
>   including the fault-idempotent commit-ordering lesson the seam cannot express by itself.
>
> `reference/README.md` maps each file to a Highway concept and names the four hazards that
> project already paid for. It is prior art, not a dependency: port the idea, do not
> reference the folder.

### - [ ] T2.2 — `InMemoryStore`

*Requirements:* R3.3
**Done when:** the seam has a complete in-memory implementation and a contract test suite that both
implementations must pass. **Written before the RocksDB one**, so the contract is defined by what
the commands need rather than by what RocksDB happens to do.

### - [ ] T2.3 — `RocksDbStore` and the key layout

*Requirements:* R3.3, R4.1, OD4, OD5
**Done when:** the same contract suite passes on RocksDB; the key layout is documented; seq
allocation happens **inside the same batch** as its write (`design.md` §1); and OD5 — expiry field
versus compaction filter — is decided and recorded.

> **The key layout is already decided and coded** (settles OD4). See
> [`physical-layout.md`](physical-layout.md) for the design — the four families
> (`q`/`z`/`s`/`k`, plus the `n` counter family), every current Garnet key mapped onto
> them, the column-family split, and why there is **no collection/document model**
> underneath. The realizing code is in `src/Highway.Server/Storage/Layout/`:
> `KeyWriter` + `KeyEncoding` (order-preserving encoders, ported from stow),
> `HighwayKeyspace` (the family key builders), `HighwayNames` (the logical-name
> vocabulary — the direct translation of the old `HighwayKeys`, with the mirror keys
> gone), and `HighwayColumnFamilies`. It compiles clean. What T2.3 still owns:
> the `RocksDbStore` that opens the DB and wires these into `IHighwayStore`, the
> in-batch seq allocator (physical-layout.md §5), the head/tail seq scheme choice
> (§5), and OD5 (the reply-slot expiry mechanism).

### - [ ] T2.4 — The transactional write path *(gate G1)*

*Requirements:* R3.2, R4.1–R4.4, R5.1
**Done when:** there is **exactly one place** a batch is committed; a test injects a failure between
two writes that must be atomic and proves no partial state; and **no clock is read inside a
transaction** — enforced by a test, not a convention.
**Gate G1:** `Highway.Server` compiles against the seam with no engine type on it. If it does not,
the seam is wrong and Phase 3 does not start.

---

## Phase 3 — port the commands

### - [ ] T3.1 — Collapse `Prepare`/`Main` on one command first

*Requirements:* R1.1, R4.1
**Done when:** `HwQSendCommand` — the simplest — is ported whole: no `AddKey`, no `StoreType`, no
mirrors, one transactional method. It is the template every other file follows, so it gets reviewed
as a pattern rather than as a file.

### - [ ] T3.2 — Port the remaining 22 command files

*Requirements:* R1.1
**Done when:** every `HW.*` command runs on the seam. Type substitutions: `MemoryResult<byte>` and
`CreateArgSlice` (Garnet.common), `CustomProcedureInput` / `IGarnetApi` / `LockType` / `StoreType`
(Garnet.server), `PinnedSpanByte` (Tsavorite.core) all replaced by our own.
**Mechanical, but 23 files of it** — and it is the same pass in which the transaction model
collapses, so it is productive work rather than translation.

### - [ ] T3.3 — Collapse the main-store mirrors

*Requirements:* R1.1 · **Risk 4**
**Done when:** `QueueNodeList` and `JobIndex` have one source of truth instead of two, and **each
mirror's reader has a before/after equivalence test** proving it gets the same answer.
**Why singled out:** the mirrors exist only because Garnet's `Prepare` could not read the
object store. Removing them is correct and is the most likely place for a silent behavioural
regression.

### - [ ] T3.4 — Command logic under test, with no server

*Requirements:* R10.2, R3.4
**Done when:** the lease sweep, attempt counting, dead-lettering, delayed promotion and job firing
are all exercised in-process against `InMemoryStore`, with no socket and no broker.
**This is capability the project does not have today**, and it is where the port pays for itself
beyond C4.6.

### - [ ] T3.5 — WAL replay determinism

*Requirements:* R5.2, R5.3
**Done when:** an ungraceful kill mid-write, followed by recovery, produces **byte-identical**
state — the property `HwQClaimCommand.Main`'s in-transaction `DateTime.UtcNow.Ticks` read cannot
offer under Garnet's `AofEntryType.StoredProcedure` replay.

---

## Phase 4 — the RESP server

### - [ ] T4.1 — The reader's test suite, written first

*Requirements:* R6.1
**Done when:** a test feeds every frame **one byte at a time** and asserts the same parse, plus
malformed-input cases. Written before the reader, because this is the bug Garnet already found and
it is cheap to find deliberately.

### - [ ] T4.2 — The RESP reader

*Requirements:* R6.1
**Done when:** arrays of bulk strings parse off a `SequenceReader<byte>` over the Kestrel pipe, with
an incomplete frame returning *need more* rather than throwing. T4.1 green.

### - [ ] T4.3 — Vendor `RespWriteUtils.cs`

*Requirements:* R2.3, R6.2
**Done when:** the file is copied under its **MIT notice with the Microsoft copyright header
intact**, entered in `THIRD-PARTY-NOTICES.md`, and is the **only** Garnet-derived source in the
repository. 907 lines, zero Tsavorite coupling, nothing to substitute.

### - [ ] T4.4 — `ConnectionHandler`, dispatch and TLS

*Requirements:* R6.3, R6.4, R10.1
**Done when:** Kestrel serves the endpoint with TLS (**ALPN configured for a non-HTTP protocol** —
verify rather than assume), the handshake subset from T1 is implemented, and commands outside the
subset return an error **naming the subset** rather than a plausible `+OK`.

### - [ ] T4.5 — Pub/sub doorbells

*Requirements:* R7.1–R7.3
**Done when:** `SUBSCRIBE`/`UNSUBSCRIBE` and push frames work against SE.Redis's separate subscriber
connection, restricted mode is honoured, and **the rig passes with `DoorbellsEnabled == false`** —
which is what proves the delivery guarantees never depended on it.

### - [ ] T4.5b — Auth: config users, connection-level `AUTH`

*Requirements:* R11
**Done when:** config-file users (hashed passwords + a documented hash recipe) validate
both `AUTH` forms; an unauthenticated connection can send only `AUTH`/`PING` and gets
`-NOAUTH` naming the fix otherwise; `WithoutAuthentication()` and the loopback
exemption behave exactly as C6.x states; the client's `user:pass@` connection string
and `HighwayAuthenticationException` path work **unmodified**; C6.x amended and the
012 `@dangerous`/`nopass` findings retired with dated notes.

### - [ ] T4.6 — Document the subset

*Requirements:* R6.5
**Done when:** `HIGHWAY-PROTOCOL.md` gains a section stating exactly which RESP commands are served,
so no future reader assumes general Redis semantics.
**This is the one permitted edit to that document** — it adds what the server accepts, and changes
nothing about `HW.*`.

---

## Phase 5 — remove Garnet

### - [ ] T5.1 — Delete the dependency

*Requirements:* R2.1, R2.2, R2.4
**Done when:** no `Garnet.*` or `Tsavorite.*` reference remains; `libs/garnet` is gone from
`.gitmodules` and the tree; and a test asserts no such type appears in any API surface.

### - [ ] T5.2 — Remove the distributed cache (feature 026)

*Requirements:* non-goals
**Done when:** the `IDistributedCache` / `HybridCache` integration is removed, and its removal is
**announced** in `product.md` and `roadmap.md` rather than discovered by a user. It existed because
Garnet's native `GET`/`SET` were free underneath; with Garnet gone, so is its reason.

---

## Phase 6 — prove it

### - [ ] T6.1 — The existing suite, client unmodified *(gate G2)*

*Requirements:* R1.2, R1.4
**Done when:** the full test suite passes and `git diff src/Highway.Client/` is empty — or every
hunk carries a recorded justification.

### - [ ] T6.2 — The assurance rig *(gate G3)*

*Requirements:* R8.1–R8.4
**Done when:** I1–I5 green **including the turbulence phase**, with the rig's applications
unmodified. Only feature 032's Requirement 6 — *"the broker's own record corroborates"* — is
re-pointed at the new storage. A run is recorded in `assurance/RUNLOG.md`.

### - [ ] T6.3 — C4.6, measured *(gate G4)*

*Requirements:* R9.1
**Done when:** the traffic profile that produced 102 MB / 205 MB is re-run against the new engine
and the result is recorded in `constraints.md` **either way**. The skipped test
`SustainedTraffic_DoesNotGrowTheLogWithoutBound` is unskipped and re-pointed, or retired with a
reason.

### - [ ] T6.4 — Make the register true

*Requirements:* R9.3, R2.3
**Done when:** every constraint whose mechanics changed carries a dated amendment — C4.2, C4.3,
C4.4, C4.5 at minimum, since all four were met *by Garnet mechanisms*. C23 (no encryption at rest)
and C24 (deletion is logical until compaction) need restating against RocksDB.
**The register's whole value is that its statuses can be trusted.**

---

## Deferred — deliberately not in this feature

- [ ] **Replication and failover.** Stage 2 — research Part VI. The 2-node-versus-3-node fork
      (**O10**) should be settled before it starts: with two nodes and no external arbiter,
      automatic failover is not available at any price.
- [ ] **The epoch and the primary-only mutation gate.** D7. They serve replication only.
- [ ] **Protocol improvements** — batch claim, multi-queue claim, piggybacked ack, SSE doorbell.
      All evaluated in research Part III.6; all belong to a protocol change this feature is not.
- [ ] **A producer-only HTTP ingress** for non-.NET callers. R10 keeps the seam that would make it
      three endpoints rather than a second protocol. Add it when someone asks.
- [ ] **RocksDB tuning.** After OD1.
