# Tasks: Runnable Samples

> **Ordering note:** Tasks 1–5 build the projects, Task 6 tests the scanning risk before it can hide, Tasks 7–8 are the actual runs — where the findings come from — and Tasks 9–10 record and verify. Task 7 is the point of the feature: everything before it is preparation for running Highway for the first time.

## Task Dependency Graph

```
T1  (samples/ layout + solution + contracts)     [independent]
T2  (Broker app)                    → T1
T3  (Shared config helper)          → T1
T4  (OrderService app)              → T1, T3
T5  (Storefront app)                → T1, T3
T6  (Cross-assembly scanning check + library fix) → T5
T7  (RUN IT — scenarios 1-4, 6-9)   → T2, T4, T5, T6
T8  (RUN IT — durability + cross-machine) → T7
T9  (README)                        → T7, T8
T10 (Findings + full verification)  → T7, T8, T9
T11 (Living conformance gate: RUNLOG + steering rule) → T10
```

## Tasks

- [x] ### Task 1: Sample Layout, Solution Integration, and Contracts

**Fulfills:** Requirement 1 (all), Requirement 2 (all)

**Steps:**
1. Create `samples/` as a sibling of `src/` and `tests/`
2. Create `Highway.Samples.Contracts` (class library) referencing **only** `Highway.Abstractions`; set `IsPackable=false`
3. Add `Orders.cs` and `Events.cs` per design § "Contracts": `CreateOrder`/`OrderResult`, `GetOrder`, `CancelOrder` (contract only, never hosted), `OrderPlaced`, `InventoryLow`
4. Add the header comment explaining why contracts are separate, citing `product.md` § "Delivery (Package Architecture)" (Requirement 2 AC5)
5. Add a `/samples/` solution folder to `Highway.slnx` containing all sample projects (added as they are created)
6. Confirm the contracts project has no reference to `Highway.Client` or `Highway.Server` and no third-party packages

**Done criteria:**
- `dotnet build Highway.slnx` succeeds with zero warnings; contracts compile against Abstractions alone

---

- [x] ### Task 2: Broker Application

**Fulfills:** Requirement 3 (all), Requirement 9 (AC1)

**Steps:**
1. Create `Highway.Samples.Broker` console app referencing `Highway.Server`
2. Build the server through the public `HighwayServerBuilder` → `RunAsync(CancellationToken)` path only — no internal or test-only entry points (Requirement 3 AC2)
3. Wire `Console.CancelKeyPress` to a `CancellationTokenSource` with `e.Cancel = true` so Ctrl+C drains rather than killing the process
4. Enable console logging via `LoggerFactory` + `WithLoggerFactory`
5. Configure port, data directory, and bind address per design § "Configuration", defaulting to `6500`, `./data`, `127.0.0.1`
6. Log the listening endpoint and the **resolved absolute** data directory, plus a copy-pasteable `--server host:port` line for participants
7. Add `samples/*/data/` and `samples/*/bin`,`obj` to `.gitignore` if not already covered

**Done criteria:**
- `dotnet run` starts a real broker; Ctrl+C exits cleanly with code 0; the printed endpoint is directly usable by the participants

---

- [x] ### Task 3: Shared Configuration Helper

**Fulfills:** Requirement 3 (AC6), 5 (AC6), 9 (AC2)

**Steps:**
1. Add a small `Config` helper resolving argument → environment variable → default, per the design table
2. Keep it deliberately tiny — a few methods, no configuration framework. The samples demonstrate Highway, not configuration binding
3. Share it by file link or a trivial shared file across the three apps; do not create a fourth library for it

**Done criteria:**
- All three apps accept the documented arguments and environment variables and run with no configuration at all

---

- [x] ### Task 4: OrderService Application

**Fulfills:** Requirement 4 (all), Requirement 7 (AC7 host half)

**Steps:**
1. Create `Highway.Samples.OrderService` console app referencing `Highway.Client` and `Highway.Samples.Contracts`
2. Wire with `Host.CreateApplicationBuilder` + `AddHighway`, letting the hosted service start and drain the engine — no manual `StartAsync` (Requirement 4 AC5)
3. `CreateOrderService`: returns typed 200 data **and** publishes `OrderPlaced` via an injected `IHighwayClient` (Requirement 4 AC4)
4. `GetOrderService`: returns 404 `StatusCode` with `ErrorDetail` and throws nothing — the errors-are-data demonstration (Requirement 4 AC3)
5. `InventoryLowSubscriber`: implements `ISubscribe<InventoryLow>` and logs receipts, so the service host also consumes events
6. Log every request handled and every event published (Requirement 4 AC6)
7. Default node name `order-service-1`, overridable so a second instance can run for competing consumers (Requirement 4 AC8)

**Done criteria:**
- Builds and starts against a running broker; no service or subscriber is registered by hand anywhere

---

- [x] ### Task 5: Storefront Application

**Fulfills:** Requirement 5 (all), Requirement 7 (AC7 caller half)

**Steps:**
1. Create `Highway.Samples.Storefront` console app referencing `Highway.Client` and `Highway.Samples.Contracts`
2. Same generic-host + `AddHighway` wiring as Task 4
3. `OrderPlacedSubscriber`: implements `ISubscribe<OrderPlaced>` and prints each message as it arrives
4. Interactive command loop per design § "Storefront": `order <qty>`, `get <id>`, `cancel <id>`, `low <item>`, `help`, `quit`
5. `get` prints the returned status code and error detail, demonstrating no exception is thrown (Requirement 5 AC2)
6. `cancel` calls the never-hosted `CancelOrder`, demonstrating the immediate local 404 (Requirement 5 AC3)
7. `low` publishes `InventoryLow`, demonstrating that a caller is also a publisher (Requirement 5 AC5)
8. Default node name `shop-1`, overridable — required for the fan-out and durability scenarios
9. Ctrl+C shuts the host down cleanly

**Done criteria:**
- A person can drive every scenario in Requirement 7 from this app's prompt

---

- [x] ### Task 6: Cross-Assembly Scanning — Verify and Fix in the Library

**Fulfills:** Requirement 6 (all)

**Steps:**
1. Run Storefront against a live broker and OrderService and attempt `order 2`. Determine whether contracts in the referenced assembly are discovered **without** `AdditionalAssemblies`
2. If discovery fails (symptom: `SERVICE_NOT_FOUND` for a service that is demonstrably running), diagnose against design § "The Assembly Scanning Risk" and confirm the root cause is lazy assembly loading before assuming it
3. Fix in `Highway.Client` — the likely direction is seeding discovery from the entry assembly's reference closure rather than trusting `AppDomain.CurrentDomain.GetAssemblies()`. **Do not** set `AdditionalAssemblies` in the sample (Requirement 6 AC2)
4. Add a regression test proving discovery succeeds for a contract whose assembly is referenced but not yet loaded (Requirement 6 AC4). A test-only assembly that the test assembly references but never touches before `AddHighway` reproduces the condition
5. Confirm the full suite stays green — scanning changes affect feature 003 behavior (Requirement 6 AC3)
6. Record the outcome either way in Task 10's notes: whether AC1 held as-is or needed a fix (Requirement 6 AC6)

**Done criteria:**
- The caller discovers referenced contracts with zero manual registration, and a test stops that regressing

---

- [x] ### Task 7: Run It — Core Scenarios

**Fulfills:** Requirement 7 (AC1–AC4, AC6–AC9)

> This is the first time Highway runs as a real distributed system. Findings are the deliverable; a clean run and a run that exposes defects are both successes.

**Steps:**
1. Three terminals: broker, OrderService, Storefront. Confirm each starts and connects
2. **RPC round trip** — `order 2` returns typed data from another process (AC1)
3. **Errors are data** — `get ORD-0000` prints 404 + `ORDER_NOT_FOUND`, no exception (AC2)
4. **Unknown service** — `cancel ORD-1` fails immediately, not after the call timeout (AC3)
5. **Fan-out** — start a second Storefront as `shop-2`; place an order; confirm **both** instances receive their own copy (AC4)
6. **Competing consumers** — start a second OrderService as `order-service-2`; place several orders; confirm load is shared and every call gets exactly one response (AC6)
7. **Broker unavailable** — start Storefront with no broker; confirm the failure names the endpoint and is actionable, not a hang or a bare stack trace (AC8)
8. **Graceful shutdown** — Ctrl+C the OrderService with a request in flight; confirm the caller still receives its response (AC9)
9. Capture console output for the README; record every defect and every awkwardness for Task 10

**Done criteria:**
- Every scenario either demonstrably works or has a recorded finding; nothing is left untried

---

- [x] ### Task 8: Run It — Durability and Cross-Machine

**Fulfills:** Requirement 7 (AC5), Requirement 9 (AC3–AC5)

**Steps:**
1. **Durable delivery across downtime** per design § "Sequence: The Durability Demonstration": stop `shop-1`, place orders while it is down, restart it under the same node name, confirm the missed events arrive (AC5). This is product success criterion 2 across real processes
2. Confirm the counterpart: a *new* node name starts with a fresh group and does not receive the backlog — the group identity is the node name
3. **Broker restart** — stop and restart the broker with its data directory intact; confirm queued work and subscriptions survive
4. **Cross-machine** — start the broker with `--bind 0.0.0.0` and connect a participant from a second machine, or from a second network interface. Exercise a real RPC and a real publish, not just a connection
5. If no second machine or non-loopback interface is available, state that plainly and record what was actually tested (Requirement 9 AC5) — do not imply coverage that does not exist

**Done criteria:**
- Durability proven across process restarts on disk; cross-machine either proven or explicitly recorded as untested with the reason

---

- [x] ### Task 9: Getting Started README

**Fulfills:** Requirement 8 (all)

**Steps:**
1. Write `samples/README.md` with exact commands per terminal, in start order
2. Document every Requirement 7 scenario with reproduction steps and expected output, using the real output captured in Tasks 7–8 (Requirement 8 AC2, AC4)
3. State prerequisites: .NET 10 SDK and nothing else (AC3)
4. Troubleshooting: broker not running, port in use, stale data directory, and any failure mode found in Tasks 7–8 (AC5)
5. Document node-name uniqueness and why it matters — it is the subscriber-group identity, and two live processes sharing one silently share a group
6. Document cross-machine setup with the security implication of binding beyond loopback (Requirement 9 AC3)
7. Link to `docs/product/product.md` for concepts and to the feature specs for mechanics (AC6)
8. Re-run every command in the README as written and confirm it works (AC7) — no command ships unexecuted

**Done criteria:**
- A developer who has never seen Highway can go from clone to a working three-process system without reading source

---

- [x] ### Task 10: Findings and Full Verification

**Fulfills:** Requirement 10 (all), Requirement 1 (AC7)

**Steps:**
1. Append a **Completion Record** to this file covering: every defect found (symptom, root cause, fix, regression test), every usability finding that works but is awkward, the Requirement 6 outcome, and anything claimed by `product.md` that could not be demonstrated with the reason
2. Confirm every library defect was fixed in the library with a regression test in the appropriate suite — no sample-side workaround survives (Requirement 10 AC2)
3. `dotnet build Highway.slnx` — zero warnings, zero errors, samples included
4. `dotnet test Highway.slnx` — full suite green including any tests added here; confirm the 348 pre-existing tests still pass and record the new total
5. Run the integration suite a second time to catch parallelism flakiness, per the practice established in 005
6. Update `docs/product/roadmap.md` to list 010 and its status
7. Record whether a genuine cross-machine run happened (Requirement 9 AC5)

**Done criteria:**
- Green build, green suite twice, samples runnable and documented, and every finding from Highway's first real run written down

**Result:** Green build (0 warnings), full suite green.

| Project | Before 010 | After 010 |
|---|---|---|
| Highway.Abstractions.Tests | 2 | 2 |
| Highway.Client.Tests | 158 | 166 |
| Highway.Server.Tests | 107 | 107 |
| Highway.Integration.Tests | 173 | 173 |
| **Total** | **440** | **448** |

Eight new tests, all regression guards for finding 1.

---

- [x] ### Task 11: Establish the Living Conformance Gate

**Fulfills:** Requirement 11 (all), Requirement 1 (AC4–AC5)

> Without this task the samples are a one-off exercise that goes stale by feature 006. This is what makes them a recurring test.

**Steps:**
1. Verify every sample project references Highway by `ProjectReference` only — no `PackageReference` to `Highway.*`, no pinned Highway version anywhere (Requirement 1 AC4). Confirm by inspecting each `.csproj`, not by assuming
2. Confirm the guarantee holds in practice: make a temporary breaking change to a public API in `src/`, verify the sample build fails, then revert. A gate that has never been observed to fire is not known to work
3. Create `samples/RUNLOG.md` with a documented entry format — date, libraries' state (commit or feature), what was run, findings, resolutions — and seed it with the Task 7 and Task 8 runs as the first entries, newest first
4. Amend `.kiro/steering/spec-workflow.md` with the obligation from design § "The obligation on future features": any feature touching an `HW.*` command, the wire envelope, `HighwayOptions`/`HighwayServerOptions`, or any public API must update the samples in that same feature, re-run them, and append to `RUNLOG.md` before completion
5. State plainly in that rule that a sample failing to start or run **blocks** the feature that broke it, and that degrading the sample to route around the break is not an acceptable fix (Requirement 11 AC7)
6. Add a pointer from `samples/README.md` to `RUNLOG.md` so a reader hitting a problem checks known findings first
7. Sanity-check the rule against the next feature in the queue: confirm what 006 would owe the samples (participants become discoverable; Storefront gains a `discover`/`stats` command) and note it in 006's tasks so the obligation is concrete rather than abstract

**Done criteria:**
- Samples provably track current source; the breaking-change gate has been observed to fire; `RUNLOG.md` exists with real entries; the obligation is written into steering where every future feature will read it

**Result:** Green build (0 warnings), full suite green.

| Project | Before 010 | After 010 |
|---|---|---|
| Highway.Abstractions.Tests | 2 | 2 |
| Highway.Client.Tests | 158 | 166 |
| Highway.Server.Tests | 107 | 107 |
| Highway.Integration.Tests | 173 | 173 |
| **Total** | **440** | **448** |

Eight new tests, all regression guards for finding 1.

---

## Completion Record

Full detail in [`samples/RUNLOG.md`](../../../samples/RUNLOG.md). Summary:

**The samples did their job on the first run.** Highway had 440 passing tests
and had never run as a deployed system. Starting it as three real processes
found a defect that made the product's headline use case impossible.

### Finding 1 — a caller-only node could not call anything (fixed)

A process that referenced the contracts library but hosted no services could
address nothing: every `ExecuteAsync` returned `SERVICE_NOT_FOUND` for services
running in another process. `ImmutableCatalog` derived its request-type →
service-name map from **locally hosted implementations**, so a pure caller's map
was empty. Channels had the identical defect, so a node could not publish to a
channel it did not itself consume.

This broke goal G4 and the headline example in `product.md`.

440 tests missed it because every integration-test node scans the same assembly
and therefore hosts everything — no test ever exercised a genuine caller-only
node. Exactly the blind spot this feature exists to find.

Fixed in the library: addressing now derives from `[Service]` / `[Channel]` on
the contract types, independent of local implementations. Eight regression tests
across `CatalogTests` and `TypeScannerTests`.

### Finding 2 — assembly scanning depended on load order (fixed)

The weakness the spec predicted. `DefaultAssemblySource` filtered only
`AppDomain.CurrentDomain.GetAssemblies()`, and the runtime loads lazily.
Discovery now walks the entry assembly's reference closure. Masked by finding 1,
so it was never independently observable — fixed on its own merits.

### Findings 3–5 (recorded, not fixed)

- **No public API for `HW.DISCOVER` / `HW.STATS`.** They ship in the protocol and
  exist on the *internal* connection, but `IHighwayClient` exposes neither. The
  storefront drops to raw RESP. Adding public API is a design decision beyond
  this feature.
- **An unroutable call blocks for 30 seconds by default.** Fast-fail fixes it
  (1 ms vs 30,014 ms measured) but is off by default. Whether the default should
  change is a product decision.
- **First-start recovery logs an alarming stack trace** at info level. Upstream
  Garnet behaviour, harmless, looks like a crash.

### What is now proven that was not

Standalone broker process; RESP over a real socket between OS processes; the
generic-host lifecycle; scanning across a project boundary; broker-unavailable
failure quality; durable delivery across subscriber downtime **and** across a
broker restart; competing consumers across two host processes; and RPC plus
pub/sub over a real non-loopback interface with `--bind 0.0.0.0`.

Cross-machine is recorded as **partial**: a second network interface, not a
second physical machine — no physical hop, NAT, or firewall between hosts.

### The gate was observed firing

Renaming `HighwayServerBuilder.WithDataDir` — which the library itself still
compiled against — broke the sample build in the same commit, then was reverted.
A gate never seen to fire is not known to work.

### A false alarm worth recording

Durable-delivery-across-downtime first appeared to fail. The cause was the test
harness quitting milliseconds after startup, before the consumer loop's first
drain. Re-run with a delay, both missed events arrived. The product was fine;
the measurement was not.
