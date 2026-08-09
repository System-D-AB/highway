# Feature: Highway.Hosting — Deployable Client Nodes

## Introduction

Highway makes a client node easy to *write* and leaves it hard to *deploy*. A node is a
console application; turning it into something an operator can install, start on boot, and
upgrade is left entirely to the reader. Every team writes the same `sc.exe` script or the same
systemd unit by hand, slightly differently, and gets the shutdown timeouts wrong.

This is the gap Topshelf filled for a decade, and .NET only half-replaced it.

### What .NET already does — and what this feature must not rebuild

| Need | Built in? |
|---|---|
| Run as a console app | ✅ default |
| Run as a Windows Service | ✅ `AddWindowsService()` |
| Run as a systemd daemon | ✅ `AddSystemd()` |
| Same binary, same code, all three | ✅ both no-op off their platform |
| **Install / uninstall / query the service** | ❌ **nothing** |
| **A shutdown budget that is actually honoured** | ❌ **actively wrong today** |

So this feature is narrow: the dual-mode half is two one-line registrations, and the content is
the installer verbs plus the shutdown correctness nobody currently owns.

### The defect this feature opens with

`HighwayEngine.cs:305` bounds the drain by the caller's token as well as by `DrainTimeout`:

```csharp
var deadline = DateTime.UtcNow + _options.DrainTimeout;
while (Volatile.Read(ref _activeOperations) > 0
       && DateTime.UtcNow < deadline
       && !ct.IsCancellationRequested)      // ← the host cancels this at 5 s
```

The Generic Host's `ShutdownTimeout` defaults to **5 seconds** and cancels the token it passes
to every `IHostedService.StopAsync`. So `DrainTimeout` — documented as *"how long graceful
shutdown waits for in-flight work to finish"* and defaulted to 10 seconds — is silently capped
at 5, and every value above 5 is inert.

**The host does not need to be persuaded to wait.** `Host.StopAsync` *awaits* each hosted
service; the shutdown timeout cancels a token, it does not abort the await. A service that keeps
working keeps the host waiting. So the fix is to let `DrainTimeout` govern, not to negotiate with
the host — and it needs no new dependency, which matters because `HostOptions` is not in
`Microsoft.Extensions.Hosting.Abstractions` and `Highway.Client` deliberately references only
abstractions packages.

That fix ships in `Highway.Client`, in Phase 0, **before** the new package exists. Correct
shutdown must not require adopting a hosting package.

## Requirements

### Requirement 1: `DrainTimeout` Means What It Says

**User Story:** As an operator, I want the drain window I configured to be the drain window I get.

#### Acceptance Criteria

1. The drain is bounded by `HighwayOptions.DrainTimeout` alone. The caller's cancellation token no longer shortens it
2. A test proves a slow handler completes when `StopAsync` is passed a token that cancels well before `DrainTimeout` elapses — the shape the Generic Host produces. **It must be watched failing against the current code first**, because a shutdown test that has never failed proves the harness stopped, not that the drain finished
3. Total `StopAsync` duration stays bounded and predictable: `DrainTimeout`, plus the existing 2-second wait on loop tasks, plus the graceful `HW.HEARTBEAT BYE` and connection disposal. No path becomes unbounded
4. The teardown that follows the drain — cancelling the work token, the loop-task wait, `BYE`, disposal — is **unchanged**
5. `StopAsync` acquires its lifecycle lock with `CancellationToken.None`. A token already cancelled on entry currently throws before the drain, skipping the graceful departure entirely; shutdown is the one path that must not be abandoned because someone was in a hurry
6. `DrainTimeout`'s XML documentation states the relationship to the host's `ShutdownTimeout` (the host waits; Highway decides) and to an orchestrator's kill deadline (`terminationGracePeriodSeconds` and `TimeoutStopSec` must exceed it). The client cannot read either, so the documentation is the whole mitigation

### Requirement 2: A Separate Package, For Reasons

**User Story:** As a developer whose web app publishes messages, I want no service-installation machinery in my dependency tree.

#### Acceptance Criteria

1. Hosting ships as **`Highway.Hosting`**, referencing `Highway.Client`. Nothing moves into `Highway.Client`
2. `Highway.Client`'s dependency set is **unchanged** by this feature. It keeps referencing only `*.Abstractions` packages
3. Windows- and Linux-specific dependencies live in `Highway.Hosting` and are acquired only by applications that ask for it
4. `Highway.Hosting` is optional in the strict sense: everything a node can do today it can still do without the package. This feature adds deployment convenience, never capability

### Requirement 3: One Binary, Three Ways To Run

**User Story:** As a developer, I want `myworker.exe` to run in my terminal for debugging and as a service in production, with no conditional code.

#### Acceptance Criteria

1. Run with no arguments → console application: logs to stdout, `Ctrl+C` drains
2. Run under the Windows SCM → Windows Service, with lifetime and Event Log wiring
3. Run under systemd → daemon, with `Type=notify` readiness and journald-shaped logging
4. **The mode is detected, never declared.** `AddWindowsService()` and `AddSystemd()` are both registered and each no-ops off its platform, so exactly one takes effect and the other costs nothing
5. Logging providers are configured per mode rather than replaced. A caller who clears providers keeps that choice; the package adds and does not overwrite
6. The API composes with `Host.CreateApplicationBuilder`, which is what the samples and the docs already use. An escape hatch exists for callers who want to keep their own builder and opt into only part of this

### Requirement 4: Install, Uninstall, Status

**User Story:** As an operator, I want to install the worker as a service without writing a script or an MSI.

#### Acceptance Criteria

1. Verbs, handled before the host starts:
   ```
   myworker --install [--start]     install, optionally start immediately
   myworker --uninstall             stop if running, then remove
   myworker --status                installed? running? under what account?
   myworker --start | --stop        control an installed service
   ```
2. `RunAsync` returns `Task<int>` so `Main` can return a process exit code. Success is `0`; each failure class has a distinct, documented non-zero code
3. Service identity comes from configuration — name, display name, description, startup mode, account — with the name required and everything else defaulted
4. **Install is idempotent in outcome, never partial.** An already-installed service is either updated or refused with the reason; a failed install leaves nothing registered
5. **Uninstall stops first**, waits for the service to reach stopped, then removes. Removing a running service leaves Windows requiring a reboot to finish
6. Restart-on-failure is configured by default on both platforms. A crashed worker that stays down is the outcome an installer exists to prevent
7. On Linux, the generated unit file is **written where an operator can read it and diff it**, and the path is printed. A generated file nobody can inspect is a black box during an incident

### Requirement 5: The Failures An Installer Actually Hits

**User Story:** As an operator, I want the reason it did not install, not a Win32 error code.

#### Acceptance Criteria

1. **Insufficient privileges** are detected *before* any change is attempted, and reported as "run as Administrator" or "run with sudo" — never as a raw access-denied
2. **The `dotnet` muxer case is handled, not refused.** A framework-dependent deployment executes as `dotnet myworker.dll`, so `Environment.ProcessPath` is the muxer. The registered command line quotes both the muxer and the assembly. This is the case that defeated Topshelf on .NET Core, and it is common enough that refusing it would make the feature useless to half its audience
3. Because that command line embeds an absolute path to `dotnet`, install **warns** that a runtime relocation breaks the service, and recommends an apphost or single-file publish
4. **Paths with spaces are quoted** on both platforms. This is the single most common service-registration bug there is
5. Missing systemd — no `systemctl`, or a container without an init system — is reported plainly rather than producing a unit file nobody will read
6. Every verb is safe to run twice. Uninstalling an absent service, starting a running one, and querying an uninstalled one all succeed or report clearly; none corrupt state

### Requirement 6: The Shutdown Budget Is Derived Once, Not Configured Three Times

**User Story:** As an operator, I want one number to control shutdown, not three that must be kept consistent by hand.

#### Acceptance Criteria

1. The generated systemd unit sets `TimeoutStopSec` from `DrainTimeout` plus a margin. Left at its default, systemd would eventually `SIGKILL` mid-drain — the same defect as Requirement 1, one layer out
2. Windows' service-stop timeout is **machine-wide, not per-service**, so it cannot be derived. The documentation states the interaction: a `DrainTimeout` approaching it makes the SCM report a stop failure even though the drain is working
3. The documentation states the container relationship too: an orchestrator's kill deadline must exceed `DrainTimeout`, and Highway cannot see or enforce it
4. Where `MaxProcessingTime` exists (feature 019), the startup warning comparing it against `DrainTimeout` is **emitted from one place**, not once per hosting mode

### Requirement 7: Conformance

#### Acceptance Criteria

1. **`docs/HIGHWAY-PROTOCOL.md` is not modified.** No `HW.*` command, reply, key, framing or doorbell changes. If that file moves, this feature has grown something it should not have
2. `constraints.md` records the Requirement 1 fix: the drain honours `DrainTimeout`, with a note that it was previously capped by the host's shutdown timeout — recorded rather than silently corrected, because the register's value is that gaps are visible
3. `product.md` marks the `dotnet new` template item as addressed or narrowed by this feature, and lists `Highway.Hosting` in the package architecture
4. `roadmap.md` places 021 and states what it deliberately excludes
5. A sample worker runs all three ways, is installed and uninstalled on both platforms, and `samples/RUNLOG.md` records it — including the console output being **identical** across modes, which is the evidence Requirement 3 holds
6. All tests pass; `dotnet build` warning-free

## Open Decisions

**Answer before the design is final.** Each changes the shape.

1. **How does Windows install/uninstall talk to the SCM?**
   - *`sc.exe`* — no dependency, battle-tested, but a child process and localized output to parse for anything beyond an exit code.
   - *P/Invoke `advapi32`* — `OpenSCManager` / `CreateServiceW` / `DeleteService`. No child process, and real Win32 codes (`ERROR_SERVICE_EXISTS`, `ERROR_ACCESS_DENIED`) that map straight onto Requirement 5's messages.
   - **Recommendation: P/Invoke for create/delete/configure, `ServiceController` for start/stop/query.** Requirement 5 asks for precise causes, and precise causes come from error codes rather than from parsing text that changes with the OS language.

2. **Does `RunAsync` own argument parsing, or does the application?**
   - *Package owns it* — Topshelf's ergonomics, one line in `Main`, but it consumes argument names the application might want.
   - *Application opts in* — explicit, more ceremony.
   - **Recommendation: package owns the reserved verbs, and they are namespaced and documented.** Unknown arguments pass through untouched, so an application keeps its own command line.

3. **Should the service name default `NodeName`?**
   - Tempting: a service name is unique per host, which is exactly what `NodeName` needs.
   - **Recommendation: no, and this one is not close.** `NodeName` is a subscriber group's durable identity. Silently changing it on adoption orphans the group's backlog until feature 017's retirement threshold deletes it — data loss caused by installing a hosting package. The two names stay independent and the documentation says why.

## Non-Goals

- **Health and readiness probes.** A client node's health is "the engine is Running and the broker is reachable", which the engine already logs and which `HW.DISCOVER` already answers from the broker side. A probe surface is a separate feature with its own design.
- **Installing or hosting `Highway.Server`.** The broker is a different deployment shape — stateful, single-instance, block storage — and conflating the two in one package would produce a worse abstraction for both.
- **MSI, .deb, .rpm, Docker images, Windows Installer bundles.** Packaging is not service registration. `dotnet publish` plus one verb is the scope.
- **A supervisor, watchdog, or scheduler.** systemd and the SCM already restart processes, and Requirement 4 AC6 configures them to.
- **Changing `DrainTimeout`'s default.** Requirement 1 makes 10 seconds real; it does not make it different.
- **A general-purpose .NET service installer.** Most of this package is not Highway-specific, and that is a fair criticism. It ships here because Highway's thesis is removing deployment friction and this is the friction its users hit — not because Highway owns the concept.

## Cross-References

- `docs/features/019-long-running-tasks/requirements.md` — R5's `DrainTimeout` vs `MaxProcessingTime` warning, which R6.4 consolidates
- `docs/features/005-client-server-communication/design.md` — the engine lifecycle and the original `DrainTimeout ≪ lease` operating envelope
- `docs/features/010-create-samples/` — the precedent that running as real processes finds what tests do not
- `docs/product/constraints.md` — C1.2, C3.1 (in-flight work is requeued, never destroyed), C7.1
- `docs/product/product.md` — § Hosting model, and the unbuilt `dotnet new` template
