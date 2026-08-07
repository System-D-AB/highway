# Feature: Server Remediation (004.1)

## Introduction

Feature 004 delivered the nine `HW.*` commands, the hosting layer, and the embedded test server. Its architecture held up: custom transaction procedures for atomicity, `Finalize` for doorbells (correctly skipped during AOF replay), lists + doorbells instead of Streams, and the reply slot as a plain string key. All 127 tests are green and the wire contract matches what feature 005 pinned.

Review of the merged implementation against `004/requirements.md` and `004/design.md` found a correctness bug, a class of failures the client cannot classify, several requirements marked complete but never tested, and design documentation that no longer describes the code. This feature closes those gaps so that 005 can be built on a server whose behavior is both correct and knowable.

**This is a remediation feature, not a new capability.** Every requirement below either fixes a defect in 004, makes an existing 004 requirement testable, or records a decision that was made in code but never written down.

### Numbering note

This spec uses `004.1` rather than the next sequential number because `006` is reserved by `docs/product/roadmap.md` for Heartbeat & Service Registry, and this work is a direct amendment to 004 rather than an independent feature. It is the only intentional deviation from the `{NNN}` convention in `spec-workflow.md`.

## Glossary

Terms carry their 004 meanings (service queue, processing list, reply slot, doorbell, subscriber group, group list, backlog). Additional terms:

- **Mirror key** — A main-store string holding a newline-delimited copy of an object-store set (`hw:svc:{service}:nodelist` mirrors the nodes set; `hw:ch:{channel}:grplist` mirrors the groups set). Introduced during 004 implementation because reading the set in `Prepare` creates a watch that conflicts with the later exclusive lock on the same key.
- **Watch conflict** — Garnet aborts a transaction when a key read in `Prepare` (via `GarnetWatchApi`) is modified before locks are taken. The command performs no work and the client receives a generic error.
- **Permanent failure** — A command that will fail identically on every retry (blank identifier, oversize payload, malformed `COUNT`).
- **Transient failure** — A command that may succeed if retried (watch conflict).

## Requirements

### Requirement 1: Idempotent Re-Subscribe

**User Story:** As a client node that restarts, I want re-registering my subscriber group to be a no-op, so that I do not receive the channel backlog again on every restart.

**Defect:** `HwSubscribeCommand.Main` calls `CopyBacklogToGroup` unconditionally, with no check for whether the group was newly added. Confirmed by test: publish 2 messages to a channel with no groups, `HW.SUBSCRIBE`, drain both, `HW.SUBSCRIBE` again with the same group → both messages are delivered a second time. This violates 004 Requirement 8 AC2 and is directly triggered by 005's engine, which sends `HW.SUBSCRIBE channel NodeName` on every start.

#### Acceptance Criteria

1. `HW.SUBSCRIBE <channel> <group>` copies backlog entries to the group queue only when the group was not already a member of the channel's group set
2. A second `HW.SUBSCRIBE` for an already-registered group returns `+OK`, adds no entries to the group queue, and leaves the backlog unchanged
3. Two distinct groups subscribing to the same channel each receive the backlog exactly once (004 Requirement 10 AC3 still holds)
4. A group that unsubscribes and then re-subscribes is treated as new and receives the backlog again (its queue was deleted by `HW.UNSUBSCRIBE`)
5. An integration test named for this scenario proves AC2; it fails against the pre-remediation implementation

### Requirement 2: Distinguishable Command Errors

**User Story:** As a client engine, I want permanent failures and transient failures to be distinguishable from the RESP error alone, so that I retry what is retryable and fail fast on what is not.

**Defect:** All `Prepare`-phase validation returns `false`, and `CustomRespCommands.TryTransactionProc` renders that as the literal string `ERR Transaction failed.` — confirmed by test against an oversize payload. `CustomTransactionProcedure.Prepare` has no `output` parameter, so the distinct messages promised in `004/design.md` (`-ERR payload too large`, blank-identifier errors) cannot be produced from that phase. Garnet emits the same string when a transaction aborts on a watch conflict. A client therefore cannot tell "this request is 2 MB and will never succeed" from "retry me."

**Severity:** `HW.PUBLISH`, `HW.DEQUEUE`, `HW.SUBSCRIBE` and `HW.UNSUBSCRIBE` all read a mirror key in `Prepare`, which creates a watch. Under concurrency these commands can abort with no work done. For `HW.PUBLISH` that means a message the caller believes was published was not — silent loss unless the client retries.

#### Acceptance Criteria

1. Every validation failure returns a RESP error whose message begins with the stable prefix `ERR HW_`, followed by a machine-readable code and a human-readable detail
2. The following codes exist and are documented: `HW_INVALID_ARG` (blank or malformed identifier), `HW_PAYLOAD_TOO_LARGE` (payload exceeds `MaxPayloadBytes`, detail names actual and limit), `HW_INVALID_COUNT` (`COUNT` absent, non-numeric, zero, negative, overflowing, or above `ReceiveMaxCount`)
3. No validation failure mutates any key — the error is returned before any write
4. After this change, the bare message `ERR Transaction failed.` is emitted only by Garnet itself for a transient abort, making it an unambiguous retry signal
5. Wrong argument counts continue to be rejected by Garnet's arity check with its own distinct error (unchanged behavior)
6. Every code has an integration test asserting the exact message prefix, not merely that an exception was thrown
7. A test demonstrates that a watch conflict (or an equivalent transient abort) still produces the bare `ERR Transaction failed.` message, proving the two classes are separable

### Requirement 3: Identifier Validation and Delimiter Safety

**User Story:** As an operator, I want identifiers containing control characters rejected, so that a misconfigured node name cannot corrupt server-side routing state.

**Defect:** Mirror keys are newline-delimited strings. No command validates that a service, channel, group, or node identifier is free of `\n`. A group named `a\nb` writes two entries into the group list, causing `HW.PUBLISH` to fan out to a group queue that no consumer drains. 005's `NodeName` becomes the group name and is user-settable, making this reachable from client configuration.

#### Acceptance Criteria

1. Service, channel, group, node, request, and message identifiers are rejected with `HW_INVALID_ARG` when they are empty, or contain any character below U+0020, or contain U+007F
2. Rejection happens before any key is derived from the identifier
3. Payloads are exempt — they remain byte-for-byte opaque (004 Requirements 3 AC3, 4 AC6, 9 AC6)
4. An identifier length ceiling is enforced and documented, with a rejection code, so that a pathological identifier cannot produce an unbounded key
5. Integration tests cover newline, tab, null, and DEL characters in at least the group and node identifier positions

### Requirement 4: Configurable Embedded Test Server

**User Story:** As a Highway contributor, I want to override server timings and durability settings on the embedded server, so that lease, TTL, retention, and restart behavior can be tested at all.

**Defect:** `HighwayTestServer` exposes only `maxPayloadBytes`. `Lease`, `ReplySlotTtl`, `BacklogRetention`, `MaxBacklogEntries`, `ReceiveDefaultCount`, `ReceiveMaxCount` and `DataDir` are unreachable. This is the root cause of Requirements 5–7 below: those 004 behaviors are not merely untested, they are currently untestable.

#### Acceptance Criteria

1. `HighwayTestServer` accepts a configuration delegate or options object covering every field of `HighwayServerOptions` except `Port`
2. The parameterless constructor and the existing `maxPayloadBytes` constructor keep working unchanged (no break to the 34 existing integration tests)
3. A test server can be constructed with a data directory, enabling AOF and durability testing
4. A test server can be restarted on the same port and data directory within one test, so a client connection string stays valid across the restart
5. Memory-only remains the default when no data directory is supplied (004 Requirement 2 AC2)
6. Requirement 2 AC5 still holds — concurrent instances remain isolated on distinct ports

### Requirement 5: Durability and Restart Survival Coverage

**User Story:** As an operator, I want proof that in-transit work survives a restart, so that I can trust the durability claim before running Highway in production.

**Defect:** 004 Task 18 is marked complete, but `DurabilityTests.cs` contains a single test that verifies server isolation, not durability. 004 Requirement 13 and Requirement 15 AC7 have no coverage.

#### Acceptance Criteria

1. An integration test with AOF enabled enqueues requests, publishes messages, and registers subscriptions; restarts the server on the same data directory; and asserts `HW.DEQUEUE` and `HW.RECEIVE` return the pre-restart items with payloads intact
2. Subscriber group registration survives the restart (004 Requirement 8 AC6) — a publish after restart fans out to the pre-restart group without re-subscribing
3. Reply slots written before the restart are retrievable after it (004 Requirement 4)
4. A test asserts the documented memory-only expectation: state is lost when a server with no data directory is disposed and rebuilt (004 Requirement 13 AC3)
5. A test asserts that Highway keys coexist with stock Garnet keys without collision (004 Requirement 13 AC4)
6. `DurabilityTests.cs` contains tests that match its name; the existing isolation test moves to a file named for what it tests

### Requirement 6: Lease Expiry and Redelivery Coverage

**User Story:** As a client engine author, I want the at-least-once recovery path proven, so that 005 can rely on server-side redelivery when a worker dies mid-request.

**Defect:** 004 Requirement 7 defines lazy lease sweep and requeue in detail, and `HwDequeueCommand` and `HwReceiveCommand` both implement it. Neither path has a single test. 004 Task 16 lists a crash-simulation step and is marked complete.

#### Acceptance Criteria

1. With a short lease configured, a request dequeued and never acknowledged is returned by a later `HW.DEQUEUE` — including to a different node ID (004 Requirement 7 AC3, AC6)
2. A request acknowledged before lease expiry is never redelivered (004 Requirement 6 AC3)
3. `HW.ACK` after a requeue still returns `+OK` and leaves no residue in any processing list (004 Requirement 7 AC4)
4. With a short lease, a received-but-unacknowledged pub/sub message is redelivered by a later `HW.RECEIVE`, at the **head** of the group queue so ordering is preserved (004 Requirement 12 AC4)
5. A `RACK` in one group does not affect another group's copy of the same message (004 Requirement 12 AC5)
6. With `Lease = TimeSpan.Zero`, expired entries stay in their processing lists and are never requeued (004 Requirement 7 AC5)

### Requirement 7: Reply TTL, Backlog Retention, and Doorbell Coverage

**User Story:** As a Highway contributor, I want the remaining untested 004 behaviors covered, so that the checked boxes in `004/tasks.md` are true.

#### Acceptance Criteria

1. With a short `ReplySlotTtl`, an unretrieved reply slot is gone after the TTL elapses (004 Requirement 4 AC5)
2. Replying twice for one request ID leaves the last payload, proving the documented last-writer-wins rule (004 Requirement 4 AC4)
3. With a short `BacklogRetention`, backlog entries older than the window are not delivered to a group that subscribes afterwards (004 Requirement 10 AC4)
4. With a small `MaxBacklogEntries`, the backlog is capped and the oldest entries are dropped (004 Requirement 10 AC4)
5. Doorbell regression tests prove that `HW.CALL` rings `hw:door:svc:{service}` with the request ID, `HW.REPLY` rings `hw:door:rep` with the request ID, and `HW.PUBLISH` rings `hw:door:ch:{channel}:grp:{group}` for every group — each observed through a real StackExchange.Redis subscriber (004 Requirements 3 AC6, 4 AC1, 11 AC6)
6. A test proves a doorbell is **not** rung during AOF replay, confirming the `Finalize`-phase design decision
7. A test documents the `HW.RECEIVE` reply shape — an array of two-element `[messageId, payload]` arrays — as the contract 005 parses against

### Requirement 8: Configurable Bind Address

**User Story:** As an operator, I want to choose the network interface the server listens on, so that Highway nodes on other machines can reach the broker.

**Defect:** `HighwayServerBuilder.BuildGarnetOptions` hardcodes `IPAddress.Loopback`. There is no override. A distributed framework's broker is currently unreachable from any other host. 004 Requirement 1 never asked for a bind address, so this is a gap in the specification as much as in the code.

#### Acceptance Criteria

1. `HighwayServerBuilder` exposes a bind-address setting accepting at minimum a dotted-quad string and `IPAddress`
2. The default remains loopback — secure by default; exposing the broker is an explicit operator decision
3. Supplying `0.0.0.0` (or `IPAddress.Any`) makes the server reachable on all interfaces, verified by a test that connects via a non-loopback local address where the environment permits, and is skipped with a clear reason where it does not
4. `IHighwayServer.Endpoint` reports the configured bind address, not the hardcoded literal `localhost`
5. An invalid bind address is rejected at `Build()` with a descriptive exception naming the offending value
6. `HighwayTestServer` continues to bind loopback regardless of any global default

### Requirement 9: Design Documentation Reflects the Implementation

**User Story:** As anyone reading `004/design.md`, I want it to describe the code that exists, so that 005 and 006 are designed against reality.

**Defect:** The design describes `SetMembers` reads in `Prepare` for `HW.DEQUEUE` and `HW.PUBLISH`; the implementation cannot do this and uses mirror keys instead. The design specifies `ListRange` + `ListRemove` for `HW.ACK` and `HW.RACK`; the implementation pops the whole list and re-pushes. The design promises per-error RESP messages from `Prepare`, which that phase cannot produce.

#### Acceptance Criteria

1. `004/design.md` documents the mirror-key mechanism, the watch-conflict reason it exists, the invariant that mirror and set are updated together, and the consequence that mirror-reading commands can abort transiently
2. `004/design.md` describes the actual `HW.ACK` / `HW.RACK` scan-and-remove implementation and records its cost characteristics
3. `004/design.md` records that `Prepare` cannot write RESP output, and points to the validation approach adopted in Requirement 2
4. `004/design.md` records that `FailFastOnKeyLockFailure` is left at its default `false`, so key locking blocks rather than timing out, and the transient-abort path is watch-version validation
5. Each amended section is marked as an amendment referencing this feature, so the original design intent stays legible
6. `004/tasks.md` checkboxes are corrected: tasks whose steps were not delivered are unchecked with a note pointing at the requirement here that completes them
7. `docs/product/roadmap.md` statuses are corrected for 003, 004, and 005, and 004.1 is listed. `docs/product/product.md` and `research.md` are not modified

### Requirement 10: No Regression

**User Story:** As a contributor, I want remediation to preserve everything 004 got right, so that this feature is purely additive in confidence.

#### Acceptance Criteria

1. All 127 pre-existing tests pass unchanged, except where a test asserted behavior this feature deliberately corrects — each such change is called out in the task that makes it
2. The wire contract 005 pinned in `005/design.md` is unchanged in command names, argument order, and success-reply shapes; only error replies gain specificity
3. `dotnet build` produces zero warnings
4. Integration tests require no external infrastructure
5. No public API of `Highway.Abstractions` or `Highway.Client` changes in this feature

## Non-Goals

Explicitly out of scope, with rationale:

- **Hot-path performance work.** `HW.ACK` and `HW.RACK` pop and re-push the entire processing list; `HW.DEQUEUE` sweeps every node's processing list on every call. This is real cost on the hot path, but optimizing before 005 exists means optimizing without a benchmark — and no throughput target has ever been measured to optimize against. Deferred to a post-005 performance feature; Requirement 9 AC2 ensures it is at least written down.
- **Pruning the service node set.** The set of nodes that have ever dequeued a service grows without bound, and every dequeue locks a key per member. Pruning needs a liveness signal, which is exactly what `HW.HEARTBEAT` provides in feature 006. Deferred there.
- **Distinct error codes for Garnet-internal failures.** Only Highway's own validation gains codes; Garnet's transient abort message stays as-is, because its bareness is what makes it classifiable.
- **`EphemeralPort` race hardening.** The retry loop guards the wrong operation, but the race has not been observed. Left as a known cosmetic defect.
- **Dashboard, registry commands, flight recorder.** Features 006 and 002.

## Cross-References

- Feature under remediation: `docs/features/004-server-hw-commands/`
- Consumer of this contract: `docs/features/005-client-server-communication/` — Requirements 1, 2 and 4 here change 005's task list; see `004.1/design.md` § "Impact on Feature 005"
- Protocol table: `docs/product/product.md` § "Highway Protocol (HW.* Commands)" (read-only)
- Garnet transaction semantics: `libs/garnet/libs/server/Transaction/TransactionManager.cs`, `libs/garnet/libs/server/Custom/CustomRespCommands.cs`
