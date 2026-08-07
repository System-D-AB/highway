# Tasks: Observability & Flight Recorder

> **Ordering note:** Two spikes come first, because both settle questions that would otherwise be discovered mid-implementation — where trace context lives, and what recording actually costs between commit and reply. Tasks 3–7 build the recorder bottom-up so each layer is tested before the next depends on it. Task 8 is the point of no return: wiring every command. Tasks 12–14 discharge the protocol and samples obligations, which are enforced.

## Task Dependency Graph

```
T1  (Spike: trace-context propagation)              [independent]
T2  (Spike: write-path cost)                        [independent]
T3  (Event schema + capture mode in Abstractions)   [independent]
T4  (NameBuffer)                          → T3
T5  (FlightRecorder + metrics)            → T4
T6  (Sweeper + lifecycle)                 → T5
T7  (Server options + validation)         → T5
T8  (Wire recording into every command)   → T5, T7, T2
T9  (HW.REPLAY)                           → T5, T7
T10 (HW.STATS RECORDER form)              → T5
T11 (Activity emission, client + server)  → T1, T7
T12 (Protocol file update)                → T9, T10, T11
T13 (Samples: replay command + OTEL wiring) → T9, T11, T12
T14 (Full verification + RUNLOG)          → all
```

## Tasks

- [x] ### Task 1: Spike — Trace-Context Propagation

**Fulfills:** de-risks Requirement 8 (AC5)

> Settled before anything is built on it, because the likely answer changes the envelope — and the envelope is protocol.

**Steps:**
1. Determine how a W3C `traceparent` travels from caller to server. The envelope (`v`, `src`, `ts`, `body`) is the natural carrier; confirm whether adding an optional field is compatible with the existing reader, which rejects unknown versions but may tolerate unknown fields
2. Verify empirically against a running engine: an old reader must not reject a new envelope, or the compatibility rule must be defined
3. Decide whether the envelope version increments, and what a reader that does not understand the field must do
4. Confirm `Activity.Current` flows correctly across the client's `Task.Run` boundaries in `RpcWorkerLoop` — the execution context should carry it, but async boundaries are exactly where trace context is lost in practice
5. Record the outcome in `design.md` § "Activity Emission", replacing the open question with the decision

**Done criteria:**
- The propagation mechanism is decided and its compatibility rule written down; no later task has to guess

---

- [x] ### Task 2: Spike — What Recording Costs Between Commit and Reply

**Fulfills:** de-risks Requirement 2 (AC3), design § "Risks" row 1

**Steps:**
1. Build a throwaway `NameBuffer` and measure an append: allocation, lock contention, and elapsed time under concurrent writers on the same name
2. Confirm the write path can hold to "one in-memory append, no serialization, no payload copy" — or find out now that it cannot
3. Measure the same append when the name is disabled, confirming the claimed zero cost
4. Record the measurement in `design.md`. State it as a measurement with its method, **not** as a target — Highway claims no performance figures, and this feature does not start

**Done criteria:**
- The write-path shape is validated by measurement before eleven command handlers depend on it; throwaway code removed

---

- [x] ### Task 3: Event Schema and Capture Mode

**Fulfills:** Requirement 6 (all), Requirement 4 (AC1)

**Steps:**
1. Create `src/Highway.Abstractions/Observability/HighwayEvent.cs` per design § "Event Shape"
2. **Identifiers must match the protocol**: `RequestId` is a `string?`, `MessageId` is a `long?`. The previous spec typed both as `Guid`; a request ID is an opaque identifier and a message ID is a channel sequence number
3. Include `ErrorCode` alongside `StatusCode` — a command rejected in validation produces no `Output`, so without it the recorder cannot represent the failures it exists to show
4. Create `HighwayEventType` covering the events in design § "Which Commands Produce Which Events", with XML docs naming the producing command for each
5. Create `PayloadCapture` (`Full`, `HeadersOnly`, `Off`) with the `Full` default's consequence documented
6. Unit tests: `System.Text.Json` round trip, including null payload and null identifiers

**Done criteria:**
- Public schema in Abstractions with zero new dependencies; identifiers match the wire protocol

---

- [x] ### Task 4: NameBuffer

**Fulfills:** Requirement 3 (AC1–AC4), Requirement 4 (AC2–AC4)

**Steps:**
1. Create `src/Highway.Server/Observability/NameBuffer.cs`: fixed-capacity circular buffer of `HighwayEvent`, its own lock, incremental byte accounting
2. Append drops the oldest when full and increments a capacity-drop counter — writes are never rejected and never block
3. Reads apply retention, so a stale event is never returned merely because the sweeper has not run. Retention is correctness at read; the sweep only reclaims memory
4. Apply capture mode at append: `Full` holds the existing payload reference, `HeadersOnly` records size and drops the reference, `Off` never reaches a buffer at all
5. Unit tests in `tests/Highway.Server.Tests/NameBufferTests.cs`: chronological ordering, wraparound, capacity eviction, retention boundary, byte accounting, drop counters, concurrent appends

**Done criteria:**
- One name's history behaves correctly in isolation, under concurrency, with tests for every eviction path

---

- [x] ### Task 5: FlightRecorder and Metrics

**Fulfills:** Requirement 1 (AC1–AC5), Requirement 2 (AC7), Requirement 3 (AC5–AC6), Requirement 7 (AC1, AC3, AC5)

**Steps:**
1. Create `FlightRecorder.cs`: `ConcurrentDictionary<string, NameBuffer>`, resolving per-name capacity, retention and capture on first use
2. A name configured `Off` (or zero capacity/retention) gets **no buffer**, so recording it costs a dictionary miss and nothing else
3. `Record(...)` **never throws**. Wrap in try/catch, count the failure, swallow it — an operation must not fail because recording did (Requirement 2 AC7)
4. Create `RecorderMetrics.cs`: names, events, approximate bytes, `droppedCapacity`, `droppedBudget`, `failures` — all cumulative since start
5. When disabled, the recorder allocates nothing and metrics report the disabled state rather than erroring
6. Unit tests: per-name isolation under flood (one name's writes must not evict another's), disabled-name cost, a deliberately throwing append caught and counted

**Done criteria:**
- Recording is isolated per name and provably cannot fail an operation

---

- [x] ### Task 6: Sweeper and Lifecycle

**Fulfills:** Requirement 3 (AC4, AC6–AC7), Requirement 1 (AC5)

**Steps:**
1. Create `RecorderSweeper.cs`: a timer owned by `FlightRecorder`, reclaiming retention-expired events and enforcing the global byte budget by trimming the largest buffers first
2. **`HighwayServer` has no host and no `IHostedService`** — the previous spec assumed one. Own the timer explicitly and dispose it with the server; verify no timer survives `HighwayServer.Dispose()`
3. Budget-driven reclamation increments `droppedBudget`, so an operator can distinguish "buffer full" from "server-wide budget hit"
4. Sweeping never blocks a command: it takes per-buffer locks briefly and independently, never a global lock
5. Unit tests with injected time where practical: retention reclamation, budget trimming order, idle sweep costs nothing, a throwing sweep iteration does not stop the timer

**Done criteria:**
- Memory stays inside the budget under sustained load; no timer leaks past disposal

---

- [x] ### Task 7: Server Options and Validation

**Fulfills:** Requirement 9 (AC1–AC2, AC4–AC6), Requirement 4 (AC5–AC7)

**Steps:**
1. Extend `HighwayServerOptions` per design § "Configuration": recorder enabled, default capacity/retention/capture, `MaxBytes`, per-name overrides, `ReplayEnabled`, `ActivitiesEnabled`
2. Add `WithObservability(...)` to `HighwayServerBuilder`, consistent with the existing `With*` methods
3. Validate at `Build()` with messages naming the offending value, matching the established pattern
4. XML-document every default, and for `Full` capture document the consequence: payload content sits in memory readable by anyone who can issue `HW.REPLAY`, and Highway has no authentication
5. Unit tests: defaults, every validation rule, per-name override resolution beating the global default

**Done criteria:**
- Zero configuration yields a useful recorder; every invalid value fails fast with a descriptive message

---

- [x] ### Task 8: Wire Recording Into Every Command

**Fulfills:** Requirement 1 (AC3), Requirement 2 (all), Requirement 12 (AC3–AC5)

**Steps:**
1. Pass the recorder to commands the way `DoorbellBridge` already is, through the registration table in `HighwayServer.CommandTable`
2. Add one `Record(...)` call in each command's `Finalize`, per the table in design § "Which Commands Produce Which Events"
3. **Record failures too.** The existing `if (Failed) return;` guard in `Finalize` exists to suppress doorbells; recording must happen on the failure path as well, carrying the 004.1 error code. Do not reuse that guard for recording
4. **Do not record**: liveness heartbeats (every 5s per node is noise that would evict real history), or the read-only commands `HW.DISCOVER`, `HW.STATS`, `HW.REPLAY`
5. `HW.RECEIVE` records one event per batch, not per message
6. Hold the payload reference the command already owns; introduce no copy
7. Verify **no AOF growth**: recording writes nothing to the keyspace. Confirm by comparing data-directory size across a recorded workload, and confirm the 004 durability tests still pass (Requirement 12 AC4–AC5)
8. Run the full suite after wiring each group of commands, not once at the end

**Done criteria:**
- Every recordable operation produces an event; failures are recorded with their error codes; no existing command's behaviour, reply or AOF footprint changes

---

- [x] ### Task 9: HW.REPLAY

**Fulfills:** Requirement 5 (all)

**Steps:**
1. Create `HwReplayCommand : HighwayCommandBase`, arity `-2`, inheriting identifier validation
2. Parse `FROM` / `TO` (absolute ISO-8601 and relative `-5min` / `-1h` / `-30s`), `LIMIT`, `NODE`; reject invalid values with the 004.1 codes — `HW_INVALID_COUNT` for `LIMIT`, `HW_INVALID_ARG` otherwise
3. Unknown name or empty range returns an **empty array**, never an error, matching `HW.DISCOVER`
4. Reply as a flat field/value array per event, the same self-describing shape `HW.STATS` uses, so fields can be appended later. `payloadSize` is present even when `payload` is null
5. Lock no keys — the recorder is not in the keyspace, so this is genuinely read-only with respect to Garnet
6. Honour `ReplayEnabled = false` with a clear, documented refusal
7. Register in `CommandTable`. **The conformance test will fail until Task 12 documents it** — that is the gate working, not a problem
8. Unit tests for argument parsing; integration tests for round trip, filters, and empty results

**Done criteria:**
- An operator can ask what happened to a service and get an ordered answer; every rejection path uses the established error contract

---

- [x] ### Task 10: HW.STATS RECORDER Form

**Fulfills:** Requirement 7 (AC1–AC2, AC4–AC5)

**Steps:**
1. Extend `HwStatsCommand` with a fourth form matching the reserved name `RECORDER`, case-insensitively, taking priority over a service or channel of that name
2. Reply with the **same flat field/value shape and `kind` discriminator** as the existing three forms — this is a fourth form of one command, not a new shape
3. Report enabled state, names, events, bytes, `droppedCapacity`, `droppedBudget`, `failures`
4. Answer correctly when the recorder is disabled rather than erroring
5. Document the name-resolution priority alongside the existing service-versus-channel rule
6. Integration tests: counters after a known workload, drop counters after a deliberate overflow, disabled-state reply

**Done criteria:**
- Recorder health is visible from `redis-cli` with no tooling, in the shape operators already know

---

- [x] ### Task 11: Activity Emission

**Fulfills:** Requirement 8 (all), Requirement 9 (AC3)

**Steps:**
1. Create an `ActivitySource` in each of `Highway.Client` and `Highway.Server`, with documented source names
2. **Add no OpenTelemetry package to either project.** `ActivitySource` is in-box; the application wires OTEL and subscribes to the sources
3. Client: wrap `ExecuteAsync` and `PublishAsync`. Server: emit around command execution with name, node, identifier and outcome
4. Propagate trace context per the Task 1 decision
5. Guard every emission with `ActivitySource.HasListeners()`; materialise nothing for a span nobody collects
6. Follow OTEL messaging semantic conventions, and record the attribute mapping for Task 12
7. **Never put payload content on an activity.** Spans leave the process for third-party systems; message bodies must not ride along by default
8. Honour `ActivitiesEnabled` on both sides
9. Tests use an in-process `ActivityListener` — no OpenTelemetry dependency in the test project either

**Done criteria:**
- Traces appear in any OTEL pipeline the application configures, with Highway depending on no telemetry package

---

- [x] ### Task 12: Update the Protocol File

**Fulfills:** Requirement 10 (AC1–AC2)

**Steps:**
1. Add `HW.REPLAY` to the Command Index with its arity — the conformance test fails until this lands, which is the mechanism working
2. Document `HW.REPLAY` fully in a new section: all arguments, both timestamp forms, the reply shape, every error code, and that it is read-only
3. Document the `RECORDER` form alongside the existing `HW.STATS` forms, including name-resolution priority
4. Document the event field/value shape and every field, noting that `payload` is null unless captured under `Full`
5. Document the `ActivitySource` names and the OTEL attribute mapping — a non-.NET client cannot infer either
6. If Task 1 changed the envelope, document the new field and its compatibility rule in § "Transport & Framing"
7. Bump the protocol version and add a changelog row naming this feature
8. State that the flight recorder is **volatile**, so nobody mistakes it for a durable audit log

**Done criteria:**
- `ProtocolConformanceTests` passes; a client implementer can use `HW.REPLAY` from the protocol file alone

---

- [x] ### Task 13: Samples

**Fulfills:** Requirement 10 (AC3–AC6)

**Steps:**
1. Add a `replay [name]` command to the storefront, printing recent events for a service — demonstrating the recorder rather than describing it
2. Extend the storefront's `stats` command, or add `stats recorder`, to show recorder health
3. Add the application-side OpenTelemetry wiring to the broker sample. A commented block is acceptable if adding the packages would burden the sample, but the wiring must be concrete enough to copy — "bring your own OTEL" is only friendly if the wiring is shown
4. **Re-run the samples** as three real processes and walk every scenario in `samples/README.md`, plus the new ones
5. Add a `samples/RUNLOG.md` entry: date, what was run, what was found, what was done
6. Any defect the run exposes is fixed **in the library** with a regression test, never worked around in the sample
7. Update `samples/README.md` with the new commands and their real output

**Done criteria:**
- The flight recorder is demonstrable in under a minute by someone who has never seen it; the RUNLOG has a new entry

---

- [x] ### Task 14: Full Verification

**Fulfills:** Requirement 11 (AC3–AC10), Requirement 12 (all)

**Steps:**
1. `dotnet build Highway.slnx` — zero warnings, zero errors
2. `dotnet test Highway.slnx` — all 448 pre-existing tests pass plus the new ones
3. Run the integration suite a second time to catch parallelism flakiness, per established practice
4. Confirm the non-obvious guarantees have tests that would actually fail if broken: per-name isolation under flood, recording failure not propagating, `Off`/`HeadersOnly` retaining no content, failed operations recorded with error codes, no AOF growth
5. Confirm `ProtocolConformanceTests` passes and the protocol file describes what shipped
6. Re-read the protocol file's new sections against the implementation one final time — the document's whole value is being right, and 007's final read-through caught an error the conformance test could not
7. Update `docs/product/roadmap.md`: 002 complete, and note that v1 is feature-complete
8. Update `docs/product/product.md`'s status table — G8 moves from "Not built" to shipped, with the volatility caveat stated
9. Record the final test count and any finding below

**Done criteria:**
- Green build, green suite twice, protocol file true, samples re-run, and every product-doc status claim matching reality

**Result:** Green build (0 warnings), full suite green.

| Project | Before 002 | After 002 |
|---|---|---|
| Highway.Abstractions.Tests | 2 | 2 |
| Highway.Client.Tests | 166 | 166 |
| Highway.Server.Tests | 107 | 148 |
| Highway.Integration.Tests | 173 | 202 |
| **Total** | **448** | **518** |

Seventy new tests. No new package references on any project.

---

## Completion Record

Full findings in [`samples/RUNLOG.md`](../../../samples/RUNLOG.md) (2026-08-07, feature 002).

### The three contradictions the rewrite resolved held up

Storing events **in process memory rather than the Garnet keyspace** removed the
AOF-replay problem, the store contention, and the write amplification in one
move — and made per-name retention possible at all. Verified by
`Recording_AddsNoKeysToTheStore`.

Recording in **`Finalize`** works exactly as reasoned: after commit, before the
reply, and skipped during AOF replay. The spec's honesty that "after the reply is
sent" is impossible saved the implementation from chasing a hook that does not
exist.

**Per-name buffers** deliver the isolation they were chosen for:
`OneNameFlooding_DoesNotEvictAnother` floods one name with 5,000 events and the
quiet name keeps its single important one.

### The recorder found a bug in Highway on its first real use

The sample showed **two** `RpcClaimed` events for one order. Chasing it uncovered
something far worse: Garnet caches one procedure instance per session, and
`HighwayCommandBase` never cleared its captured validation error — so **a single
rejection made every later invocation of that command on that connection return
the previous call's error**. Since the 005 client shares one multiplexer per node,
one oversize payload would have broken that command for the whole node.

Fixed by sealing `Prepare` so it clears state and delegates to `PrepareCore`,
with a `ResetState()` hook for command-specific fields. Sealing makes the class
of bug structurally impossible rather than fixed once.
`SessionStateIsolationTests` covers it.

### A 004.1 finding was wrong, and is now corrected

Feature 004.1 attributed a session desync to an upstream Garnet parser quirk and
asserted the broken behaviour in `NewlineDesyncProbe`. Fixing the state leak made
that test fail — the desync was Highway's own bug all along, and newlines were
incidental. The probe now documents the correction rather than the
misattribution.

### Deliberate departures from the original spec, all held

- **No OpenTelemetry dependency.** Both packages emit `ActivitySource` only; the
  application wires OTEL. The tests verify spans with an in-process
  `ActivityListener` — no OTEL in the test project either, which is what makes
  the "bring your own pipeline" claim real rather than nominal.
- **No serialization on the write path.** Events are objects, serialized only
  when `HW.REPLAY` reads them. Measured at 80 ns / 48 bytes per append.
- **The `tp` envelope field needed no version bump.** Verified compatible in both
  directions before anything depended on it.
- **Liveness heartbeats are not recorded.** Registration and departure are.

### Known limits, stated rather than implied

The recorder is **volatile** — documented in the protocol file, not left to be
discovered. `HW.REPLAY` serves payload content on an unauthenticated port under
the default `Full` capture; `HeadersOnly` and `ReplayEnabled = false` are the two
switches, and the exposure is stated plainly in three places rather than buried.
