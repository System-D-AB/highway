# Tasks: The Queue

Server first, because the client has nothing to talk to otherwise. The two tests that justify
the feature are T12's `MultipleInstances_ShareTheWork` and
`SendWithNoProcessorRunning_IsProcessedWhenOneStarts` — if either is weak, the feature has not
been built.

---

## Phase 1 — Server

### - [x] T1 — Queue key space

`HighwayKeys.Queue*` for the six keys under `hw:q:{queue}:`, including the newline-delimited
node-list mirror.

*Requirements:* R6.1
**Done when:** a queue and a service may share a name with no shared state. The mirror key is
not optional styling — an object-store set read in `Prepare` registers a watch the later
exclusive lock fails (004.1).

### - [x] T2 — Share the RPC command implementations

Extract the lease sweep, attempt counting and dead-letter branch from `HwDequeueCommand` into
a base parameterised by key prefix, and have the RPC command use it unchanged.

*Requirements:* R4.4
**Done when:** the existing RPC tests pass against the refactored base with no behaviour
change. **Do this before writing the queue commands.** Feature 013 found the same
unbounded-redelivery defect living in three separate requeue paths; a fourth copy is how that
happens again.

### - [x] T3 — `HW.QSEND`, `HW.QCLAIM`, `HW.QACK`

Thin subclasses over T2's base. `HW.QSEND` accepts the optional `AT <ticks>` argument so
delayed sends work from day one.

*Requirements:* R3.1, R3.3, R4.1, R4.7
**Done when:** send, claim and acknowledge round-trip; an unacknowledged message is
redelivered after its lease; a delayed send is not claimable early.

### - [x] T4 — Dead letters and stats

`HW.DLQ` gains the `Q <queue>` target. `HW.STATS <queue>` reports `kind queue` with depth,
in-flight and dead-lettered. A second dead-letter reason for undeserializable payloads.

*Requirements:* R4.5, R6.2
**Done when:** a poison message reaches the queue's DLQ and is visible through the same
command and stats surface as the RPC and channel ones.

### - [x] T5 — Catalog

The node catalog gains a queues list, distinct from services and channels, so `HW.DISCOVER`
and the dashboard are not misleading.

*Requirements:* R6.3, R6.4
**Done when:** a queue does not appear in service discovery and carries no response type.

---

## Phase 2 — Client

### - [x] T6 — `ISend`, `[Queue]`, `IProcess<T>`

The three types in `Highway.Abstractions`.

*Requirements:* R1.1, R1.2, R2.1, R2.6
**Done when:** `class`, `record`, `record struct` and `struct` all work as message types.
`IProcess<T>` is an interface — there is no response type to constrain.

### - [x] T7 — Scanning and validation

Discover `IProcess<T>` implementations; map message types to queue names; enforce the rules.

*Requirements:* R1.3, R1.4, R1.5, R2.2, R2.3, R2.4, R2.5
**Done when:** `ISend` without `[Queue]` fails at startup naming the type; **two processors
for one type fail at startup naming both**; a node that sends to a queue it does not process
starts fine. Addressing derives from the **contract**, not from locally hosted processors —
the caller-only defect feature 010 found in the samples is the same shape and must not
reappear.

### - [x] T8 — `SendAsync`

Both overloads on `IHighwayClient`, returning the message id.

*Requirements:* R3.1, R3.2, R3.4, R3.5, R3.6
**Done when:** the returned id finds the message in the dead-letter queue. That is the whole
reason for the return value, so it is the test that proves it.

### - [x] T9 — Queue worker loop

Claim → deserialize → `[Idempotent]` gate → `ProcessAsync` → acknowledge.

*Requirements:* R2.1, R4.1, R4.6
**Done when:** it reuses feature 013's idempotency gate unchanged, including the crash-window
behaviour where an in-progress marker blocks rather than re-running.

**An undeserializable payload is dead-lettered, not acknowledged.** RPC replies `400` because
a caller is waiting; a queue has no caller. Acknowledging would discard it silently and
retrying would loop on a payload that can never parse.

### - [ ] T10 — Durability warning

Warn once, at startup, when a queue is used on a memory-only server, naming the queue and
saying its contents are lost on restart.

*Requirements:* R5.1, R5.2
**Done when:** the warning fires once rather than per send, and does **not** fire when a data
directory is configured. Feature 016 replaces this with durability by default; until then a
silent lie is the one unacceptable option.

---

## Phase 3 — Conformance

### - [x] T11 — Protocol document

The three commands in the Command Index, the key schema, the catalog shape, the `HW.DLQ`
queue target, the new dead-letter reason.

*Requirements:* R7.1, R7.2
**Done when:** `ProtocolConformanceTests` is green. It must be updated in the same change that
registers the commands — that gate has already fired three times.

### - [x] T12 — The tests that justify the feature

*Requirements:* R4.1, R4.2, R4.3, R5.4
**Done when:** all of these pass:

- `MultipleInstances_ShareTheWork` — the property Pub/Sub cannot express
- `SendWithNoProcessorRunning_IsProcessedWhenOneStarts` — the capability whose absence caused the misuse
- `SentMessage_SurvivesABrokerRestart`
- `QueueAndServiceMayShareAName`

### - [ ] T13 — Samples

Send, process, competing consumers across two instances, and a poison message reaching the
dead-letter queue. Re-run all sample processes; append to `samples/RUNLOG.md`.

*Requirements:* R7.5, R7.6, R8.4
**Done when:** the run log records what was verified, the README states the deployment
consequence — **three processors share the work, three subscribers each get a copy** — and any
defect found is fixed in the library with a regression test.

### - [x] T14 — Product documentation

`constraints.md` C1.1–C1.3 move to *Met*; `product.md` and the roadmap reflect what shipped.

*Requirements:* R7.3, R7.4

### - [ ] T15 — Full verification

*Requirements:* R8.1, R8.2, R8.3
**Done when:** every test passes, `dotnet build` is warning-free, and
`new HighwayServerBuilder().Build()` still starts a working broker with no configuration.

---

## Follow-up: what changes in Pub/Sub

**Deliberately not in this feature.** Removing the backlog is a behaviour change to a shipped,
tested guarantee and deserves its own review rather than riding along inside a feature that is
already adding a verb. Recorded here so it is a scheduled consequence, not a loose end.

Once queues exist, "hold this until someone can handle it" has a proper home, and Pub/Sub can
stop pretending to be a store.

### To remove

- [ ] **The channel backlog.** `hw:ch:{channel}:backlog`, `BacklogRetention`, `MaxBacklogEntries`, and the backlog branch in `HwPublishCommand` — a publish with no registered group delivers to nobody, which is what "publish" means
- [ ] **`CopyBacklogToGroup` in `HwSubscribeCommand`.** This deletes the one entry in `constraints.md` § Obstacles: it pops the entire backlog under an exclusive lock with `ListLeftPop(key, int.MaxValue)`, which is fatal at any serious size. Removing it is better than chunking it
- [ ] The backlog entry framing in `Envelope` — the only unversioned framing left after feature 013

### To update

- [ ] `constraints.md` — **C2.4** moves from *Not met* to *Met*, and its documented exception disappears entirely rather than needing defending
- [ ] `docs/HIGHWAY-PROTOCOL.md` — remove the backlog key, the backlog framing, and the "zero groups → the backlog" rule from § HW.PUBLISH; state plainly that a publish with no registered group is delivered to nobody
- [ ] `HW.SUBSCRIBE` — no longer copies anything; a new group starts empty
- [ ] `HW.STATS` channel form — drop `backlog`
- [ ] Samples and README — the scenario that demonstrates backlog behaviour becomes a queue scenario
- [ ] **Migration note.** Existing backlog data becomes unreachable. Highway has not shipped, so the remedy is deleting the data directory; it is documented anyway, because a silently orphaned key is worse than a documented one

### To decide

- [ ] **Does Pub/Sub keep dead-lettering and delayed publish?** Both should stay: a subscriber that fails repeatedly still needs its poison message removed, and a delayed notification is genuinely useful. Only the *backlog* is being removed, not feature 013
- [ ] **Do group queues keep retention and size caps** (`constraints.md` C4.4), or does bounding move entirely to queues? They must stay bounded — an orphaned group queue is what actually consumes a gigabyte, and feature 015 depends on it

---

## The line that must not move

`new HighwayServerBuilder().Build()` starts a working broker with no configuration, and
`ExecuteAsync` and `PublishAsync` behave exactly as they do today. This feature adds a third
verb; it changes neither of the first two.
