# Tasks: Highway.Hosting

**Two deliverables that must not be bundled.** T1–T3 fix a defect in `Highway.Client` and should
merge on their own: correct shutdown must never require adopting a hosting package. Everything
after that is a new optional package.

**The measure of success includes a diff that is not there.**
`docs/HIGHWAY-PROTOCOL.md` must be **unmodified** when this feature is done, and
`Highway.Client.csproj`'s dependency list must be **unchanged**. If either moved, the feature
grew something it should not have.

---

## Phase 0 — The drain honours its own timeout

> Ships alone, before the package exists. A defect fix does not wait on a feature.

### - [ ] T1 — `DrainTimeout` bounds the drain; the caller's token does not

`HighwayEngine.cs:305` — drop `!ct.IsCancellationRequested` from the drain loop condition.

*Requirements:* R1.1, R1.3, R1.4

**Done when:** a slow handler completes when `StopAsync` is passed a token cancelling well before
`DrainTimeout` — and **the test was watched failing first**, where the handler is abandoned at the
token instead. A shutdown test that has never failed proves the harness stopped, not that the
drain finished; that is 016's C4.5 discipline and the reason two of its earlier tests were found
vacuous.

Everything after the drain — cancelling the work token, the 2-second loop-task wait, the graceful
`BYE`, disposal — is **untouched**. Total `StopAsync` stays bounded and predictable.

> **Why not raise the host's `ShutdownTimeout` instead.** It is unnecessary — `Host.StopAsync`
> *awaits* each hosted service, so the timeout cancels a token but never aborts the await. And it
> is unavailable: `HostOptions` is not in `Microsoft.Extensions.Hosting.Abstractions`, so reading
> it would drag the full Generic Host into every `Highway.Client` consumer. A one-line logic error
> does not justify a dependency change.

### - [ ] T2 — Shutdown is not abandoned because the caller was in a hurry

`StopAsync` acquires `_lifecycleLock` with `CancellationToken.None`.

*Requirements:* R1.5

**Done when:** `StopAsync` called with an already-cancelled token still drains and still sends
`HW.HEARTBEAT BYE`. Today it throws at the lock and skips the entire teardown, so an operator's
topology view goes stale on every hurried shutdown — silently.

### - [ ] T3 — Document what the client cannot enforce

*Requirements:* R1.6

**Done when:** `DrainTimeout`'s XML doc states that the Generic Host waits rather than bounds
(so this value is the real budget), and that an orchestrator's kill deadline —
`terminationGracePeriodSeconds`, systemd's `TimeoutStopSec` — must exceed it. The client can see
neither, so the documentation **is** the mitigation, exactly as 019 concluded for
`LeaseRenewalInterval` against the server's `Lease`.

---

## Phase 1 — The package, and one binary that runs three ways

### - [ ] T4 — `Highway.Hosting` project

*Requirements:* R2.1, R2.2, R2.3, R2.4

**Done when:** the project exists referencing `Highway.Client`, is in the solution, and
**`Highway.Client.csproj` is unchanged** — checked, not assumed. Platform-specific dependencies
live here and reach only applications that ask for the package.

### - [ ] T5 — Service options and the API surface

`HighwayHost.CreateBuilder(args)` wrapping `Host.CreateApplicationBuilder`, exposing `Services`,
`Logging` and `Configuration` by delegation, plus `ConfigureService` and `ConfigureHighway`.
`RunAsync` returns `Task<int>`.

*Requirements:* R3.6, R4.2, R4.3

**Done when:** `Name` is required and validated at startup with a message naming it; everything
else defaults; and the escape-hatch seams — `AddHighwayServiceLifetime` and
`RunWithHighwayServiceVerbsAsync` — exist so a caller keeping their own builder can opt into
either half. `HighwayOptions.ConfigureConnection` is the precedent: ship the composed path, and
leave a way out.

Target `HostApplicationBuilder`, not the legacy `IHostBuilder` — the samples and docs already use
`Host.CreateApplicationBuilder`, and an API that does not compose with them is the wrong API.

### - [ ] T6 — Detect the mode, never declare it

Register `AddWindowsService()` and `AddSystemd()` unconditionally.

*Requirements:* R3.1, R3.2, R3.3, R3.4, R3.5

**Done when:** both are registered, each no-ops off its platform, and logging providers are
**added rather than replaced** so a caller who cleared providers keeps that choice.

A `--service` flag was considered and rejected: a flag that must agree with how the process was
actually started will one day disagree, and the failure is a service that starts and instantly
exits because it never signalled readiness. The platform already knows.

### - [ ] T7 — Resolve the binary path, including the muxer

*Requirements:* R5.2, R5.3, R5.4

**Done when:** an apphost or single-file deployment registers `"{ProcessPath}"`; a
framework-dependent one registers `"{dotnet}" "{app.dll}"`; **both are quoted unconditionally**;
and the muxer form **warns** that the service now depends on the runtime's install location,
recommending `PublishSingleFile`.

This is the case that defeated Topshelf on .NET Core. Refusing it would make the feature useless
to anyone deploying framework-dependent, so it is supported and its cost is stated.

Pure function over an injected `ProcessPath` — so it is tested exhaustively on every platform
without a service manager anywhere near it.

---

## Phase 2 — Install, uninstall, status

> Lanes 2 and 3 share only the options type. Genuinely parallel.

### - [ ] T8 — Verb dispatch and exit codes

*Requirements:* R4.1, R4.2, R5.6

**Done when:** each verb maps to its action; **unknown arguments pass through untouched**; every
failure class returns its documented exit code; and no verb corrupts state when run twice.

The pass-through is the adoption-safety property and deserves its own test: if adding this package
silently swallowed an application's existing arguments, the failure would surface in production
and look like anything but a hosting change.

### - [ ] T9 — Windows: P/Invoke the SCM

`OpenSCManager` / `CreateServiceW` / `DeleteService` / `ChangeServiceConfig2W` for create, delete
and failure actions; `ServiceController` for start, stop and query.

*Requirements:* R4.4, R4.5, R4.6, R5.1, R5.5

**Done when:** install is idempotent in outcome and never partial (create-then-configure, rolling
back the service if configuration fails); uninstall **stops and waits for stopped** before
deleting; restart-on-failure is configured; `ERROR_ACCESS_DENIED` becomes "run as Administrator"
and `ERROR_SERVICE_EXISTS` becomes "already installed", each with its exit code.

**`sc.exe` was rejected.** It returns an exit code and *localized text*, and R5 asks for precise
causes. Distinguishing not-elevated from already-exists by parsing console output that changes
with the OS display language is the kind of cleverness that fails in the field; Win32 error codes
are numbers and numbers are not translated.

### - [ ] T10 — Linux: render the unit, then `systemctl`

*Requirements:* R4.6, R4.7, R5.5, R6.1

**Done when:** the unit sets `Type=notify` (what `AddSystemd()` expects), `Restart=on-failure`,
and **`TimeoutStopSec` derived from `DrainTimeout` plus a margin**; the file is written to
`/etc/systemd/system/{name}.service` and **its path is printed**; `daemon-reload` and `enable`
run, `start` only with `--start`; and a missing `systemctl` exits 4 **without writing anything**.

Rendering is a pure function with a golden-file test. A unit file is a contract with the OS, and
reviewing its diff is how a mistake gets caught before an operator inherits it.

> **`TimeoutStopSec` is the whole point of this task.** Fixing the drain in T1 and then generating
> a unit that lets systemd `SIGKILL` at its default would be the identical defect one layer out.

### - [ ] T11 — Document the two layers that cannot be derived

*Requirements:* R6.2, R6.3, R6.4

**Done when:** the docs state that Windows' service-stop timeout is **machine-wide, not
per-service**, so a `DrainTimeout` approaching it makes the SCM report a stop failure while the
drain is in fact working; that an orchestrator's grace period must exceed `DrainTimeout` and is
invisible from inside the process; and that 019's `DrainTimeout`-versus-`MaxProcessingTime`
warning is emitted from **one** place rather than once per hosting mode.

---

## Phase 3 — Docs, sample, verification

### - [ ] T12 — `constraints.md`, `product.md`, `roadmap.md`

*Requirements:* R7.2, R7.3, R7.4

**Done when:** `constraints.md` records the T1 fix **including that the drain was previously
capped by the host's shutdown timeout** — recorded rather than silently corrected, because the
register's value is that gaps stay visible; `product.md` lists `Highway.Hosting` in the package
architecture and narrows the unbuilt `dotnet new` item; and `roadmap.md` places 021 with what it
deliberately excludes.

### - [ ] T13 — A sample worker, installed and uninstalled

*Requirements:* R7.5

**Done when:** one sample runs as a console app, installs and runs as a Windows Service, installs
and runs as a systemd unit, uninstalls cleanly on both — and **its console output is identical in
all three modes**. That identity is the evidence R3 holds; anything else means the mode leaked
into application behaviour. `samples/RUNLOG.md` records the run.

### - [ ] T14 — Full verification

*Requirements:* R7.1, R7.6

**Done when:** all tests pass, `dotnet build` is warning-free, and the two absent diffs are
**checked**: `docs/HIGHWAY-PROTOCOL.md` unmodified, `Highway.Client.csproj` dependencies
unchanged. One new package, no new protocol surface, no new client dependency.

---

## Parallelization

```
LANE 0  T1..T3     drain fix                    → merges ALONE, first
LANE 1  T4..T7     package, API, modes, paths   → blocks lanes 2 and 3
LANE 2  T8..T9     Windows SCM                  → needs lane 1
LANE 3  T10..T11   systemd                      → needs lane 1
LANE 4  T12..T14   docs, sample, verification   → needs 2 and 3

Order:  0  →  1  →  (2 ∥ 3)  →  4

Lane 0 is a defect fix in a different package and must not wait on an installer.
Lanes 2 and 3 share only the service-options type; that is the one real
parallelism in this feature.
```

---

## The line that must not move

**Nothing here is a prerequisite for anything.** A node that runs today as a bare console app
must still run, unchanged, with no reference to `Highway.Hosting`. This feature adds deployment
convenience and **never capability** — the moment something can only be done by adopting the
hosting package, the boundary in Decision 2 has been breached.

And: **`DrainTimeout` is one number.** After this feature, the drain, the systemd stop timeout,
and 019's processing-time warning all derive from it. The two layers that cannot derive from it —
Windows' machine-wide stop timeout and the orchestrator's kill deadline — are documented at the
point of configuration rather than left for an operator to discover during a deployment that
looked fine in staging.
