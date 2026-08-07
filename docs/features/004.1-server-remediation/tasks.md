# Tasks: Server Remediation (004.1)

> **Ordering note:** Tasks 1–7 change behavior; Tasks 8–12 add the coverage 004 promised; Tasks 13–14 make the specs true; Task 15 verifies the whole. Task 5 is the only task that fixes a confirmed defect — write its test first and watch it fail.

## Task Dependency Graph

```
T1  (Spike: zero-key transaction + validate-in-Main)         [gates T3]
T2  (HighwayErrors + Identifier primitives)                  [independent]
T3  (HighwayCommandBase)                    → T1, T2
T4  (Rebase all nine commands + COUNT fix)  → T3
T5  (Re-subscribe idempotency fix)          → T4
T6  (Public options + BindAddress + builder)                 [independent]
T7  (HighwayTestServer: configure, Restart, Port) → T6
T8  (ErrorContractTests)                    → T4
T9  (DurabilityTests rewrite + isolation move) → T7
T10 (LeaseRecoveryTests)                    → T7
T11 (DoorbellTests)                         → T7
T12 (RetentionTests)                        → T7
T13 (004 design/tasks + roadmap truth-up)   → T4, T5, T6, T7
T14 (005 spec amendments)                   → T4, T7
T15 (Full verification)                     → all
```

Tasks 2 and 6 have no prerequisites and can start immediately alongside Task 1.

## Tasks

- [x] ### Task 1: Spike — Zero-Key Transaction and Validate-in-Main

**Fulfills:** de-risks Requirement 2 (design § "Validation Redesign")

**Steps:**
1. Add one throwaway command to a scratch build (or temporarily to `HwCallCommand`) whose `Prepare` adds **no keys** and returns `true`, and whose `Main` immediately calls `WriteError` and returns
2. Drive it through `HighwayTestServer` via `IDatabase.Execute` and confirm the client receives the custom error message verbatim — not `ERR Transaction failed.`
3. Confirm a zero-key transaction does not corrupt subsequent commands on the same connection: run a normal `HW.CALL` → `HW.DEQUEUE` round trip immediately afterwards
4. Confirm `Finalize` still runs for a zero-key transaction, validating that the `Failed` guard in `Finalize` is necessary rather than incidental
5. Record the outcome in `design.md` § "Risks" — replace the first risk row with the confirmed behavior

**Done criteria:**
- Zero-key validate-in-`Main` empirically proven to return custom RESP errors; scratch code removed. If it does **not** work, adopt the sentinel-key fallback named in design § "Risks" and update the design before proceeding to Task 3

---

- [x] ### Task 2: HighwayErrors and Identifier Validation Primitives

**Fulfills:** Requirement 2 (AC1–AC2), Requirement 3 (AC1, AC4)

**Steps:**
1. Create `src/Highway.Server/Internal/HighwayErrors.cs`: `const string` codes (`HW_INVALID_ARG`, `HW_PAYLOAD_TOO_LARGE`, `HW_INVALID_COUNT`, `HW_INTERNAL`) plus formatting helpers producing `ERR {code} {detail}`; XML docs stating these strings are a **stable client contract**
2. Create `src/Highway.Server/Internal/Identifier.cs`: `static bool IsValid(ReadOnlySpan<byte> id, int maxBytes)` implementing the rule from design § "Identifier rules" — non-empty, length ≤ `maxBytes`, every byte ≥ 0x20 and ≠ 0x7F; operates on raw bytes with no string decode
3. Add `MaxIdentifierBytes` (default 256) to `HighwayServerOptions` with XML docs
4. Unit tests `tests/Highway.Server.Tests/IdentifierTests.cs`: boundary bytes 0x1F/0x20/0x7E/0x7F, empty, at-limit, over-limit, embedded newline/tab/null, valid multi-byte UTF-8
5. Unit tests `tests/Highway.Server.Tests/HighwayErrorsTests.cs`: each code formats with the `ERR HW_` prefix; detail text is included

**Done criteria:**
- Validation rule and error formatting fully unit-tested in isolation, with no dependency on a running server

---

- [x] ### Task 3: HighwayCommandBase

**Fulfills:** Requirement 2 (AC3), Requirement 3 (AC2–AC3)

**Steps:**
1. Create `src/Highway.Server/Commands/HighwayCommandBase.cs` per design § "The pattern": `Failed`, `Fail(code, detail)` (first failure wins), `TryReadIdentifier`, `TryReadPayload`, `TryWriteError`
2. `TryReadIdentifier` calls `Identifier.IsValid` on the raw span **before** decoding to string, so no key is ever derived from an invalid value
3. `TryReadPayload` enforces `MaxPayloadBytes` and emits `HW_PAYLOAD_TOO_LARGE` with actual and limit in the detail
4. Document in XML comments that `Main` must call `TryWriteError` as its first statement and `Finalize` must return early when `Failed`
5. Unit tests for the base class where reachable without a live transaction (failure capture, first-failure-wins, message shape)

**Done criteria:**
- One class owns all validation and error rendering; the pattern is documented at the point of use

---

- [x] ### Task 4: Rebase All Nine Commands

**Fulfills:** Requirement 2 (AC1–AC5), Requirement 3 (AC1–AC3, AC5), Requirement 10 (AC1–AC2)

**Steps:**
1. Rebase each of the nine `Hw*Command` classes onto `HighwayCommandBase`, replacing every `return false` in `Prepare` with the corresponding `TryRead*` call and `return true`
2. Add `if (TryWriteError(ref output)) return;` as the first line of every `Main`
3. Add `if (Failed) return;` as the first line of `Finalize` in `HwCallCommand`, `HwReplyCommand`, `HwPublishCommand` — a rejected command must never ring a doorbell
4. Rename the existing `catch` message in every `Main` from `ERR internal: {msg}` to the `HW_INTERNAL` form so the classification rule stays total
5. Fix `HwReceiveCommand.TryParsePositiveInt`: reject on overflow rather than wrapping; emit `HW_INVALID_COUNT` for non-numeric, zero, negative, overflow, and above `ReceiveMaxCount`, each with a distinct detail
6. Leave every command body below the first line of `Main` untouched — this task changes validation and error rendering only
7. Run the full existing suite after each command is rebased

**Done criteria:**
- All nine commands validate through the shared base; no `Prepare` returns `false`; all 127 pre-existing tests still green; zero build warnings

---

- [x] ### Task 5: Re-Subscribe Idempotency Fix

**Fulfills:** Requirement 1 (all)

**Steps:**
1. **First**, add `Resubscribe_SameGroup_DoesNotDuplicateBacklog` to `tests/Highway.Integration.Tests/PubSubFlowTests.cs`: publish 2 messages with no groups, `HW.SUBSCRIBE`, drain 2, `HW.SUBSCRIBE` again with the same group, assert the next `HW.RECEIVE` is empty. **Run it and confirm it fails** (it currently returns 2)
2. Change `HwSubscribeCommand.Main` to capture the `SetAdd` added-count and call `CopyBacklogToGroup` only when the group was newly added; keep the mirror-list repair unconditional so an inconsistent mirror self-heals
3. Add `Unsubscribe_ThenResubscribe_ReceivesBacklogAgain` (Requirement 1 AC4) and `TwoGroups_EachReceiveBacklogOnce` (AC3)
4. Confirm the existing `LateSubscriber_ReceivesBacklog` test still passes unchanged

**Done criteria:**
- The new test failed before the fix and passes after; 005's "subscribe on every start, never unsubscribe" model no longer duplicates messages across restarts

---

- [x] ### Task 6: Public Options, Bind Address, and Builder Validation

**Fulfills:** Requirement 8 (all), Requirement 4 (AC1 prerequisite)

**Steps:**
1. Change `HighwayServerOptions` from `internal` to `public`; move it out of `Internal/` if namespace conventions require it; ensure every property has XML docs (it is now public API)
2. Add `BindAddress` (`IPAddress`, default `IPAddress.Loopback`) with XML docs stating the secure-by-default rationale
3. Add `WithBindAddress(IPAddress)` and `WithBindAddress(string)` to `HighwayServerBuilder`; the string overload parses and throws a descriptive exception naming the offending value
4. `BuildGarnetOptions` maps `BindAddress` into `EndPoints`, replacing the hardcoded `IPAddress.Loopback`
5. `HighwayServer.Endpoint` returns `{BindAddress}:{Port}`; confirm `HighwayTestServer.ConnectionString` still returns `localhost:{Port}` and is unaffected
6. Extend `tests/Highway.Server.Tests/HighwayServerBuilderTests.cs`: default is loopback, explicit address maps through, invalid string rejected at `Build()`, endpoint rendering
7. Add an integration test binding `IPAddress.Any` and connecting via a non-loopback local address; skip with an explicit reason where the environment has no such address

**Done criteria:**
- The broker can be made remotely reachable by explicit operator choice; the default is unchanged; Requirement 10 AC5 holds (no Abstractions or Client API change)

---

- [x] ### Task 7: HighwayTestServer — Configuration, Restart, Port

**Fulfills:** Requirement 4 (all)

**Steps:**
1. Add `HighwayTestServer(Action<HighwayServerOptions> configure)`; the delegate receives options with `Port` pre-set to the probed ephemeral port, and `Port` is re-asserted afterwards so the delegate cannot change it (documented in XML docs)
2. Keep the parameterless and `maxPayloadBytes` constructors working exactly as before
3. Expose `public int Port { get; }`
4. Add `public void Restart()`: dispose the inner Garnet server, construct and start a new one from the same options — same port, same data directory — leaving `ConnectionString` valid
5. Verify a data directory can be supplied through the delegate, enabling AOF; verify memory-only remains the default
6. Tests: delegate reaches every option, `Port` is not overridable, `Restart()` keeps the connection string valid, two concurrent instances stay isolated (Requirement 4 AC6)

**Done criteria:**
- Every `HighwayServerOptions` field except `Port` is reachable from a test; `Restart()` works for both memory-only and durable configurations; the 34 existing integration tests are untouched

---

- [x] ### Task 8: Integration Tests — Error Contract

**Fulfills:** Requirement 2 (AC6–AC7), Requirement 3 (AC5)

**Steps:**
1. Create `tests/Highway.Integration.Tests/ErrorContractTests.cs`
2. Assert the **exact message prefix** for every code across every command that can emit it: blank identifier in each position, control characters (newline, tab, null, DEL) in at least the group and node positions, over-length identifier, oversize payload on `HW.CALL`/`HW.REPLY`/`HW.PUBLISH`, and every invalid `COUNT` variant on `HW.RECEIVE` including overflow
3. Assert that arity errors still come from Garnet with its own distinct message (Requirement 2 AC5)
4. Assert separability (Requirement 2 AC7): construct or simulate a transient abort and show it yields the bare `ERR Transaction failed.` while a validation failure never does. If a watch conflict cannot be forced deterministically, assert the weaker invariant — that no Highway validation path produces the bare string — and note the limitation in the test file
5. Assert no state mutation on rejection: after each rejected command, the relevant queue/list/slot is untouched (Requirement 2 AC3)

**Done criteria:**
- A client can classify every server error from its message alone, and there is a test proving it for each code

---

- [x] ### Task 9: Integration Tests — Durability and Restart Survival

**Fulfills:** Requirement 5 (all)

**Steps:**
1. Rewrite `tests/Highway.Integration.Tests/DurabilityTests.cs`; move the existing `TwoTestServers_Isolated` into a new `ServerIsolationTests.cs` unchanged
2. With a temp data directory and AOF: enqueue requests, publish to a subscribed group, write a reply slot → `Restart()` → assert `HW.DEQUEUE` and `HW.RECEIVE` return the pre-restart items with payloads byte-identical, and the reply slot is retrievable
3. Assert subscriber groups survive: after restart, publish without re-subscribing and confirm the pre-restart group receives it (Requirement 5 AC2)
4. Assert the memory-only expectation: no data directory → `Restart()` → state is gone (Requirement 5 AC3)
5. Assert key coexistence: stock `SET`/`GET` on a non-`hw:` key alongside Highway traffic, neither disturbing the other (Requirement 5 AC4)
6. Use a unique temp directory per test and clean it up in `Dispose`

**Done criteria:**
- 004 Requirement 13 and Requirement 15 AC7 are covered for the first time; the file's name matches its contents

---

- [x] ### Task 10: Integration Tests — Lease Expiry and Redelivery

**Fulfills:** Requirement 6 (all)

**Steps:**
1. Create `tests/Highway.Integration.Tests/LeaseRecoveryTests.cs` using a short `Lease` via the Task 7 delegate
2. RPC: dequeue without ack → wait past the lease → a second `HW.DEQUEUE` from a **different** node ID returns the request (AC1); acked-before-expiry is never redelivered (AC2); `HW.ACK` after requeue returns `+OK` and leaves all processing lists empty (AC3)
3. Pub/Sub: receive without RACK → wait past the lease → `HW.RECEIVE` returns it again, and assert it comes back at the **head** so ordering with a newer message is preserved (AC4)
4. Assert per-group independence: one group's `RACK` leaves another group's copy in flight (AC5)
5. Assert `Lease = TimeSpan.Zero` disables the sweep entirely — expired entries stay in their processing lists (AC6)
6. Keep lease durations small enough that the file adds no meaningful wall-clock time to the suite

**Done criteria:**
- The at-least-once recovery path 005 relies on is proven for both RPC and pub/sub, including the disabled case

---

- [x] ### Task 11: Integration Tests — Doorbells and Reply Shape

**Fulfills:** Requirement 7 (AC5–AC7)

**Steps:**
1. Create `tests/Highway.Integration.Tests/DoorbellTests.cs`
2. Promote the three probes already validated during review: `HW.CALL` rings `hw:door:svc:{service}` carrying the request ID; `HW.REPLY` rings `hw:door:rep` carrying the request ID; `HW.PUBLISH` rings `hw:door:ch:{channel}:grp:{group}` once per group — each observed through a real `ISubscriber` with a bounded timeout
3. Assert a rejected command rings nothing, covering the Task 4 step 3 `Finalize` guard
4. Assert no doorbell fires during AOF replay: with a data directory, enqueue → `Restart()` with a subscriber attached → confirm no ring, proving the `Finalize`-skipped-on-replay design decision (AC6)
5. Add the `HW.RECEIVE` reply-shape test (AC7): array of two-element `[messageId, payload]` arrays, asserted through `RedisResult[]` casting exactly as 005 will parse it

**Done criteria:**
- The doorbell mechanism 005's entire latency design rests on can no longer regress silently; 005's Task 1 spike list drops to one open item

---

- [x] ### Task 12: Integration Tests — Reply TTL and Backlog Retention

**Fulfills:** Requirement 7 (AC1–AC4)

**Steps:**
1. Create `tests/Highway.Integration.Tests/RetentionTests.cs` using short `ReplySlotTtl` and `BacklogRetention` via the Task 7 delegate
2. Unretrieved reply slot is gone after the TTL elapses (AC1)
3. Two `HW.REPLY` calls for one request ID leave the second payload, proving last-writer-wins (AC2)
4. Backlog entries older than the retention window are not delivered to a group subscribing afterwards (AC3)
5. With a small `MaxBacklogEntries`, the backlog is capped and oldest entries are dropped (AC4)

**Done criteria:**
- 004 Requirements 4 AC4/AC5 and 10 AC4 are covered; all remaining unchecked 004 behaviors now have tests

---

- [x] ### Task 13: Truth-Up — 004 Design, 004 Tasks, Roadmap

**Fulfills:** Requirement 9 (all)

**Steps:**
1. Amend `004/design.md`: document the mirror-key mechanism and the watch-conflict reason it exists; state the mirror-and-set-updated-together invariant; state that mirror-reading commands can abort transiently
2. Amend `004/design.md`: replace the `ListRange`/`ListRemove` description of `HW.ACK`/`HW.RACK` with the actual pop-all-and-re-push implementation, and record its cost characteristics
3. Amend `004/design.md`: record that `Prepare` cannot write RESP output, that `FailFastOnKeyLockFailure` stays `false` so locking blocks rather than timing out, and that the transient path is watch-version validation — pointing at 004.1 for the adopted approach
4. Mark each amended section as an amendment referencing 004.1 so the original intent stays legible
5. Uncheck `004/tasks.md` Tasks 16, 17 and 18 (or their unfulfilled steps), each with a note naming the 004.1 requirement that completes it
6. Correct `docs/product/roadmap.md`: 003 and 004 are implemented, 005 is specced not implemented, and add a 004.1 entry. Do **not** touch `product.md` or `research.md`

**Done criteria:**
- Anyone reading 004's spec sees the system that exists; no checkbox claims work that was not delivered

---

- [x] ### Task 14: Amend Feature 005 Spec

**Fulfills:** Requirement 9 (AC5 spirit), design § "Impact on Feature 005"

**Steps:**
1. `005/tasks.md` Task 1: remove the `HW.RECEIVE` parsing spike and the doorbell-delivery spike (both now covered by 004.1 Tasks 11); leave only SE.Redis resubscribe-on-reconnect
2. `005/design.md` § "Error Handling Strategy" and `005/tasks.md` Tasks 4, 7, 8: add the transient-vs-permanent classification rule and replace unbounded retry with bounded retry on the transient class only
3. `005/design.md` § "Publish Flow": state that `HW.PUBLISH` must retry the transient class, because a watch-conflicted publish delivered nothing
4. `005/tasks.md` Task 14 step 4: rewrite against `HighwayTestServer.Restart()` now that the port is stable
5. `005/tasks.md` Task 2: note that `NodeName` validation is the client half of server identifier safety, not a cosmetic rule
6. `005/design.md` § "Pinned contract inputs": pin that `HW.DEQUEUE` returns a **nil array** (`*-1`) when empty, which SE.Redis surfaces as `RedisResult.IsNull`

**Done criteria:**
- 005 can be picked up and executed without re-deriving anything from 004.1; no step in 005 remains that is not implementable

---

- [x] ### Task 15: Full Verification

**Fulfills:** Requirement 10 (all)

**Steps:**
1. `dotnet build Highway.slnx` — zero warnings, zero errors
2. `dotnet test Highway.slnx` — full suite green with no external infrastructure
3. Confirm every pre-existing test still passes, or that any changed test is named in the task that changed it and the change is a deliberate correction
4. Diff the wire contract against `005/design.md` § "Pinned contract inputs" and confirm command names, argument orders and success-reply shapes are unchanged
5. Record the final test count and the per-project breakdown in this file below

**Done criteria:**
- Green build, green suite, wire contract provably unchanged, and 005 unblocked

**Result:** Completed 2026-08-06.
- `dotnet build Highway.slnx` — zero warnings, zero errors
- `dotnet test Highway.slnx` — **243/243 passed, 0 failed** (was 127 before 004.1; +116 tests), no external infrastructure:
  - `Highway.Abstractions.Tests` — green
  - `Highway.Client.Tests` — green
  - `Highway.Server.Tests` — green (incl. new IdentifierTests, HighwayErrorsTests, HighwayCommandBaseTests, extended HighwayServerBuilderTests)
  - `Highway.Integration.Tests` — green (incl. new ErrorContractTests, rewritten DurabilityTests, ServerIsolationTests, LeaseRecoveryTests, DoorbellTests, RetentionTests, TestServerTests, NewlineDesyncProbe)
- Wire contract verified unchanged: command names, argument orders, and success-reply shapes untouched; only error replies gained `ERR HW_*` specificity
- One upstream Garnet quirk discovered, documented, and mitigated (research.md § Finding 6): a rejected custom command carrying a raw newline in an argument can desync subsequent custom-command parsing on the same session. Unreachable by Highway clients (identifiers validated client-side); pinned by `NewlineDesyncProbe` regression tests
- One test-authoring false alarm resolved: the backlog-cap "missing entry" was a FluentAssertions params-overload misuse in the test itself, not a server defect (diagnostic proved the server copied all three entries)
