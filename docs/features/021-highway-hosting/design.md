# Design: Highway.Hosting

## Overview

Two changes that look like one feature and are not. The first is a defect fix in
`Highway.Client`; the second is a new optional package.

```
PHASE 0 — Highway.Client, no new dependency, no new API
  HighwayEngine.StopAsync: the drain is bounded by DrainTimeout, full stop.
  PROOF: a slow handler finishes when the caller's token cancels early.


PHASE 1+ — Highway.Hosting, new optional package

  myworker                 ─┬─► console      (stdout, Ctrl+C drains)
  myworker  (under SCM)     ├─► Windows Service (Event Log, SCM lifetime)
  myworker  (under systemd) └─► daemon       (journald, Type=notify)
        ▲
        │  mode is DETECTED, never declared — AddWindowsService() and
        │  AddSystemd() each no-op off their platform
        │
  myworker --install       ─┬─► SCM CreateService   |  unit file + systemctl enable
  myworker --uninstall      ├─► stop, then delete
  myworker --status         ├─► installed? running? which account?
  myworker --start|--stop   └─► control an installed service
```

**The dual-mode half is two registrations.** The content of this feature is the installer
verbs, and the shutdown correctness that nobody currently owns.

## Decision 1 — The drain ignores the caller's token, and the host waits anyway

The instinct is to raise the host's `ShutdownTimeout` so its token stops firing early. That
instinct is wrong twice over.

**It is unnecessary.** `Host.StopAsync` awaits each hosted service:

```csharp
using var cts = new CancellationTokenSource(_options.ShutdownTimeout);   // 5 s default
using var linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, cancellationToken);
foreach (var hostedService in _hostedServices.Reverse())
    await hostedService.StopAsync(linked.Token);      // ← awaits. No timeout on the await.
```

`ShutdownTimeout` cancels a token. It does not abort the await, and the host has no hard kill.
A service that keeps working keeps the host waiting. **Highway never needed permission to
finish draining; it was cutting itself short.**

**It is also not available.** `HostOptions` lives in `Microsoft.Extensions.Hosting`, not in
`Microsoft.Extensions.Hosting.Abstractions` — confirmed by the compiler, not assumed.
`Highway.Client` references only abstractions packages, so reading or configuring
`ShutdownTimeout` would mean pulling the full Generic Host implementation into every consumer
including web applications that will never own a host. That is a dependency change to fix a
one-line logic error.

So the fix is the logic error:

```csharp
// before — DrainTimeout was silently capped at the host's 5 s
while (Volatile.Read(ref _activeOperations) > 0
       && DateTime.UtcNow < deadline
       && !ct.IsCancellationRequested)

// after — DrainTimeout is the budget, and it is the only budget
while (Volatile.Read(ref _activeOperations) > 0
       && DateTime.UtcNow < deadline)
```

| | |
|---|---|
| No new dependency | `Highway.Client` keeps its abstractions-only reference set |
| No new API | Nothing to discover, configure, or get wrong |
| Bounded and predictable | `DrainTimeout` + the existing 2 s loop-task wait + `BYE` + dispose |
| One documented option becomes true | Which is the entire point |

**The lifecycle lock changes with it.** `StopAsync` currently does
`await _lifecycleLock.WaitAsync(ct)`; a token already cancelled on entry throws there and skips
the whole teardown — including the graceful `HW.HEARTBEAT BYE` that keeps an operator's topology
view honest. Shutdown is the one path that must not be abandoned because the caller was in a
hurry, so it waits with `CancellationToken.None`.

**What this does not do:** it does not make shutdown unbounded. `DrainTimeout` is the bound, and
an operator who wants a faster exit lowers it — which is now the option that actually means what
its name says. The layer that *can* still kill mid-drain is the orchestrator, and Decision 5
derives those timeouts from the same number.

## Decision 2 — `Highway.Hosting` is a separate package, and the boundary is load-bearing

Not a stylistic preference. Three concrete reasons, one of which Phase 0 just demonstrated:

1. **It is process lifecycle, not messaging.** Zero coupling to RPC, queues or pub/sub.
2. **The dependency cost lands on everyone.** Service installation needs Windows P/Invoke and
   systemd handling. In `Highway.Client` that reaches every consumer, including the ASP.NET app
   that only publishes.
3. **G3 says the API is three verbs and four class shapes.** An installer surface on the client
   package makes that false, and the claim is load-bearing for the product.

The `HostOptions` episode is the boundary proving itself: the moment hosting logic entered
`Highway.Client`, it demanded a dependency the package deliberately does not have.

```
Highway.Abstractions     contracts, zero dependencies
        ▲
Highway.Client           engine, scanning, DI, wire        ← unchanged by this feature
        ▲
Highway.Hosting          service lifetime + installer      ← new, optional
```

## Decision 3 — Detect the mode, never declare it

```csharp
builder.Services.AddWindowsService();   // no-op unless the SCM started us
builder.Services.AddSystemd();          // no-op unless systemd started us
```

Both are registered unconditionally. Each detects its own context and does nothing otherwise, so
exactly one takes effect and the same binary runs three ways with no branching in application
code. This is also what makes the sample's console output identical across modes — the evidence
R3 asks for.

**Rejected: a `--service` flag or an environment variable to select the mode.** A flag that must
agree with how the process was actually started is a flag that will one day disagree, and the
failure is a service that starts and immediately exits because it never reported readiness to
the SCM. The platform already knows; asking it is free.

**These are `IServiceCollection` extensions, not `IHostBuilder` ones.** The samples use
`Host.CreateApplicationBuilder`, so the design targets `HostApplicationBuilder`. Using the older
`IHostBuilder` form would not compose with the pattern the project already documents.

## Decision 4 — The API is one entry point plus an escape hatch

```csharp
// Program.cs — complete
return await HighwayHost.CreateBuilder(args)
    .ConfigureService(s =>
    {
        s.Name        = "acme-orders";                  // required
        s.DisplayName = "ACME Order Service";
        s.Description = "Hosts order RPC services and inventory subscribers.";
        s.StartMode   = ServiceStartMode.Automatic;
    })
    .ConfigureHighway(o =>
    {
        o.Server   = Environment.GetEnvironmentVariable("HIGHWAY_SERVER");
        o.Password = Environment.GetEnvironmentVariable("HIGHWAY_PASSWORD");
    })
    .RunAsync();          // Task<int> — verbs handled, or the host runs
```

`CreateBuilder` wraps `Host.CreateApplicationBuilder(args)` and exposes `Services`, `Logging`
and `Configuration` by delegation, so nothing is walled off.

**`RunAsync` returns `Task<int>`** because an installer is a CLI and a CLI has exit codes. An
operator's script needs to distinguish "already installed" from "not elevated" from "installed
fine":

| Code | Meaning |
|---|---|
| 0 | Success — ran to completion, or the verb succeeded |
| 1 | Configuration or argument error |
| 2 | Insufficient privileges |
| 3 | Service already exists / does not exist, as applicable |
| 4 | Platform does not support the verb (no systemd, not Windows) |
| 5 | The service manager rejected the operation |

**Escape hatch**, for callers keeping their own builder:

```csharp
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHighway(o => { /* … */ });
builder.AddHighwayServiceLifetime(s => s.Name = "acme-orders");   // modes + logging only
var app = builder.Build();
return await app.RunWithHighwayServiceVerbsAsync(args);           // installer verbs only
```

Two seams rather than one all-or-nothing entry point, matching `HighwayOptions.ConfigureConnection`'s
precedent: give the composed path, and let the escape hatch out.

**Reserved verbs pass through when unrecognised.** The package claims `--install`,
`--uninstall`, `--status`, `--start`, `--stop`; everything else reaches the application's own
parsing untouched, so adopting this package cannot break an existing command line.

## Decision 5 — One number, derived into every layer that can kill the process

`DrainTimeout` is the single source of truth, and four layers can cut a drain short:

```
DrainTimeout (10 s)
   │
   ├─► Highway's own drain loop ................ Decision 1  ✅ derived
   ├─► Host ShutdownTimeout .................... irrelevant  ✅ the host awaits (Decision 1)
   ├─► systemd TimeoutStopSec .................. generated unit: DrainTimeout + margin
   ├─► Windows SCM stop timeout ................ MACHINE-WIDE — cannot derive, documented
   └─► Orchestrator kill deadline .............. outside the process — documented
```

Fixing the drain and then generating a unit file that lets systemd `SIGKILL` at its default
would be the same bug one layer out, which is why `TimeoetStopSec` is derived rather than
defaulted.

Two layers **cannot** be derived and are therefore documented at the point of configuration:
Windows' service-stop timeout is a machine-wide setting, not per-service, so a `DrainTimeout`
approaching it makes the SCM report a stop failure while the drain is in fact working; and an
orchestrator's grace period is invisible from inside the process. Highway cannot see either, so
the documentation is the whole mitigation — the same honest position 019 took on
`LeaseRenewalInterval` versus the server's `Lease`.

## Decision 6 — P/Invoke the SCM; shell out to systemctl

Asymmetric on purpose, because the platforms are asymmetric.

**Windows: `advapi32` P/Invoke** for create, delete and configure; `ServiceController` for
start, stop and query.

```
OpenSCManager(SC_MANAGER_CREATE_SERVICE)
  ├─ ERROR_ACCESS_DENIED (5) ────────► "run as Administrator"        exit 2
  └─ CreateServiceW(...)
       ├─ ERROR_SERVICE_EXISTS (1073) ─► "already installed"          exit 3
       └─ ChangeServiceConfig2W(SERVICE_CONFIG_FAILURE_ACTIONS)  ← restart on failure (R4.6)
```

**Rejected: `sc.exe`.** It works, and it gives an exit code and *localized text*. R5 asks for
"run as Administrator" rather than a raw error, and distinguishing not-elevated from
already-exists by parsing console output that changes with the OS display language is exactly
the kind of cleverness that fails in the field. Win32 error codes are numbers, and numbers do
not get translated. It also avoids spawning a child process from an installer that may itself be
running under the SCM.

**Linux: generate a unit file, then `systemctl`.** There is no library alternative, and shelling
out to `systemctl` is the canonical interface rather than a workaround.

```
/etc/systemd/system/{name}.service     written, and its path PRINTED (R4.7)
        │
        ├─ systemctl daemon-reload
        ├─ systemctl enable {name}
        └─ systemctl start {name}      only with --start
```

The unit is written where an operator can `cat` and `diff` it, and the path is echoed. A
generated file nobody can find is a black box at 3am, and the point of generating it is to save
typing, not to hide it.

## Decision 7 — Support the `dotnet` muxer rather than refusing it

This is the case that defeated Topshelf on .NET Core, and refusing it would make the feature
useless to anyone doing a framework-dependent deploy.

```
Environment.ProcessPath
   │
   ├─ ends with dotnet / dotnet.exe   → framework-dependent via the muxer
   │      command line := "{ProcessPath}" "{entry assembly .dll}"
   │      ⚠ WARN: this embeds an absolute path to the runtime;
   │              relocating or removing it breaks the service
   │
   └─ anything else                   → apphost or single-file
          command line := "{ProcessPath}"
```

Both forms are quoted unconditionally. An unquoted path containing a space is the single most
common service-registration bug in existence, and `C:\Program Files\…` guarantees the space.

The warning is deliberate rather than a refusal: the deployment works, and the operator should
know it has acquired a dependency on the runtime's install location. Recommending
`PublishSingleFile` in that message costs nothing and is the right long-term answer.

## Decision 8 — `NodeName` is not derived from the service name

Tempting and wrong, and the reason is worth recording so it is not re-proposed.

A service name is unique per host, which is exactly the property `NodeName` wants. But after 018
`NodeName` **is a subscriber group's identity**, and that identity is durable state on the
broker. Deriving it from the service name would mean:

```
before adopting Highway.Hosting:  NodeName = "AcmeOrders-PROD01"   ← group holds a backlog
after  adopting Highway.Hosting:  NodeName = "acme-orders-PROD01"  ← a NEW, empty group
                                                                     the old one is orphaned
```

The orphaned group keeps its messages until feature 017's `SubscriberRetirementThreshold`
retires it 24 hours later and **deletes them**. Installing a hosting package must not cost a
subscriber its backlog. The two names stay independent, and the documentation says why rather
than merely saying what.

## Error handling and edge cases

| Case | Behaviour | Why |
|---|---|---|
| Not elevated | Detected **before** any change; "run as Administrator" / "run with sudo"; exit 2 | R5.1 — a raw access-denied sends people to the wrong place |
| Already installed | Update, or refuse naming the existing service; exit 3 | R4.4 — never a partial registration |
| Uninstall while running | Stop, wait for stopped, then delete | Deleting a running Windows service leaves it pending a reboot |
| Uninstall an absent service | Success, says so | R5.6 — idempotent verbs |
| Path contains a space | Quoted on both platforms | Decision 7 |
| `dotnet` muxer deployment | Supported, warns about the runtime path | Decision 7 |
| No `systemctl` | Reported plainly; exit 4; **no unit file written** | A file nobody will read is worse than an error |
| `--install` on an unsupported platform | Exit 4 naming the platform | Not a crash, not a silent no-op |
| Install fails midway | Nothing registered | Create-then-configure; failure to configure removes the service |
| Console mode, no verbs | Runs the host; `Ctrl+C` drains for `DrainTimeout` | Decision 1 |
| `DrainTimeout` > machine stop timeout (Windows) | Drain works; the SCM may report a stop failure | Decision 5 — machine-wide, documented not derived |

## Testing

```
PHASE 0 ── drain ─┬─ token cancels early, slow handler FINISHES   T-1  ★★★ watched failing
                  ├─ DrainTimeout elapses → work abandoned as before T-2  ★★
                  ├─ token cancelled on ENTRY → BYE still sent      T-3  ★★★
                  └─ existing suite, unedited                       ★★★ regression gate

verbs ────────────┬─ parse: each verb → correct action             T-4  ★★
                  ├─ unknown args pass through untouched           T-5  ★★★ adoption safety
                  ├─ exit code per failure class                   T-6  ★★
                  └─ no verb → host runs                           T-7  ★★

paths ────────────┬─ apphost → "{exe}"                             T-8  ★★★
                  ├─ muxer → "{dotnet}" "{dll}" + warning          T-9  ★★★
                  └─ spaces quoted, both forms                     T-10 ★★★

unit file ────────┬─ TimeoutStopSec derived from DrainTimeout       T-11 ★★★
                  ├─ Type=notify, Restart=on-failure               T-12 ★★
                  └─ golden-file compare of the whole unit          T-13 ★★

mode ─────────────┬─ both lifetimes registered                     T-14 ★★
                  └─ console output identical across modes          T-15 ★★★ sample

privileged ───────┬─ install → status → uninstall, Windows         T-16 explicit-run
                  └─ install → status → uninstall, Linux            T-17 explicit-run
```

| Test | Proves |
|---|---|
| **`T-1 SlowHandler_CompletesWhenHostTokenCancelsEarly`** | **The reason Phase 0 exists.** `DrainTimeout = 5 s`, a handler that takes 2 s, and a token cancelling at 200 ms — the shape the Generic Host produces. **Watched failing against current code first**, where the handler is abandoned. A shutdown test that has never failed proves the harness stopped, not that the drain finished |
| **`T-3 CancelledTokenOnEntry_StillDeparts`** | R1.5. The lock-wait path: a token already cancelled must not skip `HW.HEARTBEAT BYE`, or an operator's topology view goes stale on every hurried shutdown |
| **`T-5 UnknownArguments_PassThroughUntouched`** | R4 adoption safety. Adding this package must not break an application's existing command line — the failure would be silent and would only appear in production |
| **`T-9 MuxerDeployment_QuotesBothPaths`** | Decision 7, the case that defeated Topshelf. Pure function over a fake `ProcessPath`, so it runs everywhere without a service manager |
| **`T-11 UnitFile_DerivesTimeoutStopSecFromDrainTimeout`** | Decision 5. Fixing the drain and then letting systemd `SIGKILL` at its default would be the same bug one layer out |
| `T-13` | Golden-file compare. A unit file is a contract with the OS; reviewing its diff is how a mistake gets caught |
| `T-16 / T-17` | **Kept and skipped by default**, with the reason in the skip message: they need Administrator or root and mutate machine state. 016's precedent — a test that cannot run in CI is still worth keeping if its skip reason carries what it would have proved |

**Everything that can be a pure function is one.** Command-line parsing, binary-path resolution,
quoting and unit-file rendering are the parts most likely to be wrong and the parts that need no
privileges, so they are tested exhaustively and the privileged integration tests only have to
prove the plumbing.

## Failure modes

| Codepath | Realistic failure | Test | Handled | Silent? |
|---|---|---|---|---|
| **Drain loop** | **host token truncates it** | **T-1** | Decision 1 | **was silent — the defect** |
| Lock wait on stop | cancelled token skips `BYE` | T-3 | `CancellationToken.None` | was silent |
| Verb parsing | app's own args swallowed | T-5 | pass-through | would be silent |
| Binary path | muxer path unquoted | T-9, T-10 | quote both | no — service fails to start |
| Unit file | default `TimeoutStopSec` kills mid-drain | T-11 | derived | **would be silent** |
| Install | not elevated | T-6 | pre-checked, exit 2 | no |
| Install | partial registration | — | create-then-configure, roll back | no |
| Runtime relocation | muxer service breaks | T-9 | warned at install | no — warned |

Two would-have-been-silent failures, and both are the same class: **a timeout that cuts a drain
short without saying so.** One inside Highway, one in the unit file. That symmetry is why they
are specified together rather than as a fix and a feature.

## Risks

**Most of this package is not Highway-specific.** A fair criticism, acknowledged in the
requirements' Non-Goals. It ships here because Highway's thesis is removing deployment friction
and this is the friction Highway's users hit — but the concept is not Highway's, and if a good
general-purpose installer library appears, this should shrink to a thin adapter rather than
compete.

**Phase 0 changes shutdown timing for existing deployments.** A node that has been exiting in 5
seconds will now take up to 10. That is the documented contract finally being honoured, but it is
a behaviour change and belongs in release notes, not only in a spec.

**P/Invoke is a maintenance surface** — signatures, 32/64-bit, error codes. Bounded by keeping it
to three functions and by `ServiceController` handling everything queryable.

**Privileged tests cannot run in CI.** Mitigated by pushing all the logic into pure functions and
keeping the privileged tests skipped-with-their-reason rather than deleted.

## Parallelization

```
LANE 0  Phase 0        drain fix + tests                 → independent, ships alone
LANE 1  Package + modes project, lifetimes, API shape    → blocks lanes 2 and 3
LANE 2  Windows        P/Invoke, ServiceController       → needs 1
LANE 3  Linux          unit rendering, systemctl         → needs 1
LANE 4  Docs + sample  constraints, product, RUNLOG      → needs 2 and 3

Order:  0  →  1  →  (2 ∥ 3)  →  4

Lane 0 is genuinely separable and should merge on its own — it is a defect fix in a
different package and must not wait for an installer. Lanes 2 and 3 share only the
options type and are otherwise disjoint, which is the one real parallelism here.
```

## What this design deliberately does not do

**It does not touch the protocol.** No `HW.*` command, reply, key, framing or doorbell changes.
`docs/HIGHWAY-PROTOCOL.md` must be **unmodified** when this feature is done; if it moved, the
feature grew something it should not have.

**It does not rebuild dual-mode hosting.** `AddWindowsService()` and `AddSystemd()` already exist
and already no-op off-platform. Reimplementing them would be the "does the framework already do
this?" check failing.

**It does not host `Highway.Server`.** The broker is stateful, single-instance and block-storage
backed; one package covering both would be a worse abstraction for each.

**It does not add health probes, packaging, or a supervisor.** Non-Goals, each with its reason.

## Cross-References

- `docs/features/019-long-running-tasks/design.md` — Decision 4's client-cannot-read-the-server's-setting problem, which Decision 5 hits again across three layers
- `docs/features/005-client-server-communication/design.md` — the engine lifecycle Phase 0 modifies, and `DrainTimeout ≪ lease` as the intended envelope
- `docs/features/010-create-samples/` — real processes find what tests do not; R7.5 is why the sample must be installed, not just run
- `docs/features/017-node-decommissioning/` — `SubscriberRetirementThreshold`, the mechanism that makes Decision 8's rejected option lose data
- `docs/product/constraints.md` — C3.1 (in-flight requests are requeued, never destroyed), C7.1
