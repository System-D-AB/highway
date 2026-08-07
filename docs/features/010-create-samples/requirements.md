# Feature: Runnable Samples

## Introduction

Highway has 348 passing tests and has never been run as a deployed system. Every test to date executes in a single process with the server embedded in-process. No standalone `Highway.Server` has been started and connected to over a real socket; the `HighwayServerBuilder` → `RunAsync` path that production users would take is covered only by unit-level assertions; and the developer experience the product is *sold* on — `dotnet add package`, write two classes, run — has never been walked end to end by anyone.

This feature builds the missing thing: a set of runnable console applications under `samples/` that a developer can start in three terminals and watch talk to each other over TCP. A broker process, a service host, and a caller — plus the shared contracts assembly that makes the three-package split real rather than theoretical.

**This is validation, not decoration.** The samples are the first exercise of cross-process RESP traffic, real Ctrl+C shutdown, real connection failure, and real assembly scanning across a project boundary. Where the samples reveal that Highway does not behave as documented, that is a finding this feature is expected to produce, and it is recorded rather than papered over with sample-side workarounds.

### Numbering note

This spec uses `010` at the user's direction, leaving `007`–`009` unassigned for work planned between 006 and this.

## Glossary

- **Broker app** — The console application hosting `Highway.Server` as a standalone process.
- **Service host app** — A console application that hosts `AsyncService` implementations and publishes events.
- **Caller app** — A console application that calls services and subscribes to events.
- **Contracts assembly** — A class library referencing only `Highway.Abstractions`, holding the request/response/event DTOs shared by the two participant apps.
- **Participant** — Either the service host or the caller; both run a Highway engine and connect to the broker.

## Requirements

### Requirement 1: Sample Layout and Build Integration

**User Story:** As a contributor, I want the samples to live in a conventional place and build with the solution, so that they cannot silently rot as the libraries change.

#### Acceptance Criteria

1. Samples live in a top-level `samples/` directory, a sibling of `src/` and `tests/` — matching .NET ecosystem convention and the structure documented in `.kiro/steering/coding-standards.md`
2. All sample projects are included in `Highway.slnx` under a `/samples/` solution folder, so `dotnet build Highway.slnx` builds them
3. Sample projects set `IsPackable=false` — they are demonstrations, never shipped packages
4. Sample projects reference the Highway libraries by **`ProjectReference` only**. A `PackageReference` to `Highway.Abstractions`, `Highway.Client`, or `Highway.Server` is forbidden, as is any pinned Highway version — the samples always compile and run against the current state of `src/`, never a published snapshot
5. A breaking change to a library therefore breaks the sample build immediately, in the same commit that introduces it
6. Any third-party package versions the samples need are declared centrally in `Directory.Packages.props`, consistent with the rest of the repository
7. Samples do not participate in `dotnet test`; they add no test-suite runtime
8. The full solution builds with zero warnings after the samples are added

### Requirement 2: Shared Contracts Assembly

**User Story:** As a developer evaluating Highway, I want to see how two processes share message contracts, so that I understand what the three-package split buys me.

#### Acceptance Criteria

1. A class library holds every shared DTO: RPC requests, responses, and channel messages
2. The contracts library references **only** `Highway.Abstractions` — no `Highway.Client`, no `Highway.Server`, no third-party packages
3. Both participant apps reference the contracts library; neither redefines a DTO locally
4. The contracts library contains no service implementations, no subscribers, and no hosting code — contracts only
5. A comment in the library states why this separation exists, tying it to the package architecture in `docs/product/product.md`

### Requirement 3: Broker Application

**User Story:** As a developer, I want to start a real Highway server with one command, so that I can see what running Highway in production actually involves.

#### Acceptance Criteria

1. A console application starts `Highway.Server` on a configurable port with a documented default
2. It uses the public `HighwayServerBuilder` → `RunAsync(CancellationToken)` path — the same API a production user would use, not an internal or test-only entry point
3. Ctrl+C triggers a graceful shutdown: the cancellation token is honored, listeners stop, resources dispose, and the process exits with code 0
4. Startup logs the endpoint it is listening on in a form that can be pasted directly into the participant apps
5. It runs with a data directory by default so durability is real and observable across restarts, with the directory location logged
6. Port, data directory, and bind address are configurable by command-line argument or environment variable, with defaults that require no configuration to run locally
7. Structured logging is enabled and readable at the console — an operator can see startup, command registration, and shutdown

### Requirement 4: Service Host Application

**User Story:** As a developer, I want to see a process that hosts services and publishes events, so that I can copy the pattern into my own application.

#### Acceptance Criteria

1. A console application hosts at least two `AsyncService` implementations against contracts from the shared library
2. One service demonstrates the success path and returns typed response data
3. One service demonstrates the error path by returning a non-200 `StatusCode` with an `ErrorDetail` — proving errors are data, not exceptions
4. After handling a request, the app publishes a channel message, demonstrating that one node both serves and publishes
5. It uses the standard `AddHighway` + generic-host wiring, with the engine started by the hosted service rather than manual `StartAsync` calls
6. It logs each request it handles and each event it publishes, so the demo is observable from the terminal
7. Ctrl+C drains in-flight work and shuts down cleanly
8. Multiple instances can run concurrently with distinct node names, demonstrating competing consumers on the same service

### Requirement 5: Caller Application

**User Story:** As a developer, I want to see a process that calls services and receives events, so that I understand the two verbs from the consumer side.

#### Acceptance Criteria

1. A console application calls the service host's RPC services through `IHighwayClient.ExecuteAsync` and prints the typed responses
2. It demonstrates the error path by calling the failing service and printing the returned status code and error detail — without an exception being thrown
3. It demonstrates the local-catalog 404 by calling a request type that no node hosts, showing the immediate failure
4. It subscribes to the channel the service host publishes on, and prints each message it receives
5. It publishes at least one message of its own, demonstrating that a caller is also a publisher
6. It accepts interactive commands from the console so a person can drive the demo and observe results, rather than watching a fixed script scroll past
7. Multiple instances can run concurrently with distinct node names, and **each instance receives its own copy** of every published message — demonstrating per-node subscriber groups
8. Ctrl+C shuts down cleanly

### Requirement 6: Zero-Ceremony Cross-Assembly Discovery

**User Story:** As a developer, I want assembly scanning to find contracts in a referenced library without me registering anything, because that is what the product promises.

**Risk this exists to test:** `DefaultAssemblySource` enumerates `AppDomain.CurrentDomain.GetAssemblies()`. The .NET runtime loads assemblies lazily, so a caller app that references the contracts library but defines no services of its own may reach `AddHighway` before the contracts assembly has been loaded. Discovery would then miss every contract and `ExecuteAsync` would return `SERVICE_NOT_FOUND` for a service that is in fact hosted — a confusing failure that directly contradicts product goal G3 ("Assembly scanning discovers everything at startup — no manual registration").

#### Acceptance Criteria

1. The caller app discovers contracts defined in the referenced library **without** populating `HighwayOptions.AdditionalAssemblies` and without any other manual registration step
2. If AC1 does not hold as the libraries currently stand, the fix is made in `Highway.Client` — not worked around in the sample. A sample that needs a workaround would document a ceremony the product claims not to require
3. Any such fix preserves existing scanning behavior: the full 348-test suite stays green
4. A test encodes the fixed behavior so it cannot regress: discovery must succeed for a contract type whose assembly is referenced but not yet loaded
5. `AdditionalAssemblies` remains available for genuinely dynamic scenarios (plugins, late-loaded assemblies); this requirement is about it not being *mandatory* for the ordinary case
6. The outcome — whether AC1 held or required a fix — is recorded in this feature's completion notes, because it is a finding about the product, not about the sample

### Requirement 7: Demonstrated Scenarios

**User Story:** As someone evaluating Highway, I want the samples to demonstrate the claims the product makes, so that I can verify them myself instead of trusting a README.

#### Acceptance Criteria

Each scenario below is reproducible by following documented steps, and each produces observable console output:

1. **RPC round trip** — the caller invokes a service in another process and receives typed response data
2. **Errors are data** — a failing service returns a status code and error detail; the caller prints them with no exception thrown
3. **Unknown service** — calling an unhosted request type fails immediately rather than after the call timeout
4. **Pub/Sub fan-out** — a published message reaches every running caller instance, each receiving its own copy
5. **Durable delivery across downtime** — with a caller instance stopped, messages published in its absence are delivered when it restarts under the same node name. This is product success criterion 2, demonstrated across real processes rather than in-process tests
6. **Competing consumers** — with two service host instances running, RPC load is shared between them and every call receives exactly one response
7. **Location transparency** — a participant that both hosts a service and calls one uses identical code for local and remote targets
8. **Broker unavailable** — starting a participant with no broker running produces a clear, actionable failure naming the endpoint, rather than a hang or an obscure stack trace
9. **Graceful shutdown** — Ctrl+C on the service host mid-request lets in-flight work finish and the caller receive its response

### Requirement 8: Getting Started Documentation

**User Story:** As a developer with five minutes, I want to run the whole thing without reading source code, so that I can decide whether Highway is worth more of my time.

#### Acceptance Criteria

1. A README in `samples/` gives the exact commands to run all applications, in order, with the terminal each belongs in
2. Every scenario in Requirement 7 has step-by-step reproduction instructions and a description of the expected output
3. The README states prerequisites explicitly: .NET 10 SDK, and nothing else — no Docker, no external broker, no cloud account
4. Expected console output is shown for the primary happy path so a reader can tell at a glance whether their run succeeded
5. Troubleshooting covers at minimum: broker not running, port already in use, and stale data directory
6. The README links back to `docs/product/product.md` for the concepts and to the feature specs for the mechanics
7. Documentation is accurate as of the run that completes this feature — every command in it has actually been executed, not merely written

### Requirement 9: Cross-Machine Capability

**User Story:** As a developer planning a real deployment, I want to point the participants at a broker on another machine, so that I know Highway works across a network and not only on loopback.

#### Acceptance Criteria

1. The broker's bind address is configurable so it can listen on all interfaces rather than loopback only
2. Participant apps accept the broker endpoint as configuration, so they can target a non-local host
3. The README documents the cross-machine setup, including the security implication of binding beyond loopback
4. The default configuration remains loopback-only — exposing the broker is an explicit choice, consistent with the secure-by-default stance established in feature 004.1
5. Whether a genuine cross-machine run was performed is recorded in the completion notes; if it was not, that is stated plainly rather than implied

### Requirement 10: Findings Are Recorded

**User Story:** As the project owner, I want everything the samples reveal written down, so that the first real run produces durable knowledge rather than undocumented fixes.

#### Acceptance Criteria

1. Every defect the samples expose is recorded in this feature's completion notes with its symptom, root cause, and resolution
2. Defects in the libraries are fixed in the libraries and covered by a regression test in the appropriate suite — samples never hide a library bug
3. Behavior that works but is surprising or awkward is recorded as a usability finding even when nothing is broken
4. Any product claim the samples could not demonstrate is stated explicitly, with the reason
5. The full test suite is green at completion, including any tests added by fixes made here

### Requirement 11: Samples Are a Living Conformance Gate

**User Story:** As the project owner, I want the samples to track the libraries as they evolve and to be re-run whenever the protocol or public API changes, so that "does Highway actually work end to end" is answered continuously rather than once.

**Rationale:** Running the samples exercises things no test in the suite reaches — a standalone broker process, real TCP between processes, real Ctrl+C, generic-host lifecycle, and cross-assembly scanning. A failure to start or run a sample is a genuine test failure, not a documentation chore. Because `Highway.Server` and `Highway.Client` will keep changing (feature 006 adds three protocol commands, 002 adds more), a sample frozen at today's API would stop testing anything within one feature.

#### Acceptance Criteria

1. The samples build and run against the current state of `src/` at all times — guaranteed structurally by Requirement 1 AC4 (project references, no version pinning)
2. Any feature that adds or changes an `HW.*` command, the wire envelope, `HighwayOptions`, `HighwayServerOptions`, or any public API surface **must** update the samples within that same feature — not in a follow-up
3. Any such feature must **re-run** the samples before it is considered complete, and record the outcome in its own completion notes
4. Where a change adds user-visible capability, the samples must demonstrate it, so the samples keep pace with what the product claims rather than showing an ageing subset
5. A change that cannot be expressed in the samples without added ceremony is treated as a signal about the API, and that signal is recorded — the samples are a design review surface, not only a test surface
6. Sample-run findings accumulate in a durable log rather than being scattered across feature notes, so recurring or regressing problems are visible over time
7. A sample that fails to start or run blocks the feature that broke it, exactly as a failing test would; fixing it by degrading the sample to avoid the broken path is not an acceptable resolution
8. This obligation is recorded in `.kiro/steering/spec-workflow.md` so it binds every future feature, not only the ones that happen to remember

## Non-Goals

- **A performance benchmark.** The samples prove Highway *works* across processes; characterising throughput needs a dedicated harness and belongs in its own feature. Highway claims no throughput figure, and none should be invented before one is measured.
- **A production-ready reference architecture.** These are minimal demonstrations of the API, not a template for structuring a real system.
- **Docker, Kubernetes, or deployment tooling.** `product.md` explicitly excludes these; the samples run with `dotnet run`.
- **A `dotnet new` template.** `product.md` mentions a `highway-server` template as a future deliverable; this feature builds runnable samples, not scaffolding.
- **Demonstrating features that do not exist yet.** No heartbeat, discovery, stats, or flight-recorder usage — those arrive with 006 and 002. The samples may be extended then.
- **Automated end-to-end testing of the samples.** Whether the sample processes are exercised by CI is a separate concern; this feature is about making the manual run possible and documented.

## Cross-References

- Product claims under demonstration: `docs/product/product.md` § "Vision", § "Product Goals" (G1–G4), § "Highway.Server — Hosting & Control Panel"
- Hosting API used by the broker app: `docs/features/004-server-hw-commands/design.md`, `docs/features/004.1-server-remediation/design.md` (bind address, configurable options)
- Client API used by the participants: `docs/features/005-client-server-communication/design.md`
- Scanning behavior under test in Requirement 6: `docs/features/003-assembly-scanning/design.md`
- Coding standards and project layout: `.kiro/steering/coding-standards.md`
