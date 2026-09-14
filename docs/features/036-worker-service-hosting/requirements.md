# Feature: Service Hosting — run any app as a Windows service or systemd unit

## Introduction

TopShelf let a console app install itself as a Windows service by convention. This
feature is that, for today's .NET, on both platforms: `Highway.Client.Hosting` gives a
console app a one-line host and the verbs `install`, `uninstall`, `start`, `stop`,
`status`. The app does not need to implement any Highway shape or connect to any
broker; if it does use Highway, `AddHighway` is wired for it.

The Windows service code already exists and is tested — feature 031 built it for the
broker (`WindowsServiceManager`). This feature extracts it into a package and adds the
systemd equivalent.

## Requirements

### Requirement 1: One-line host

**User Story:** As a developer, I want `return await HighwayHost.RunAsync(args);` to
be my `Program.cs`, so my app runs correctly as a console app, a Windows service, and
a systemd unit with no environment-specific code.

#### Acceptance Criteria

1. `HighwayHost.RunAsync(args)` runs a generic host and returns the exit code.
   Overloads: `RunAsync(args, Action<HighwayOptions>)` to wire Highway, and
   `RunAsync(args, Action<HighwayOptions>?, Action<HostApplicationBuilder>)` to
   register the app's own services. Highway configuration is optional — with none, no
   Highway machinery is registered.
2. `UseWindowsService()` and `UseSystemd()` are both registered; each activates only
   under its service manager (031's pattern). In a console: normal console app, Ctrl+C
   = graceful shutdown. Under SCM/systemd: start/stop control, logs to Event
   Log/journald.
3. Content root = `AppContext.BaseDirectory`, so relative paths work the same in all
   environments.

### Requirement 2: Service verbs

**User Story:** As a developer, I want `myapp install` / `myapp uninstall` (and
`start`, `stop`, `status`), elevated, to manage my app as a service on Windows and
Linux — no scripts, no `sc.exe` incantations, no hand-written unit files.

#### Acceptance Criteria

1. The five verbs are recognized as the exact first argument, handled before any host
   is built, and exit with 031's exit-code register (success / invalid args / already
   exists / not installed / elevation required / platform unsupported).
2. Windows: the verbs drive the SCM using the 031 implementation (own process,
   auto-start, description, restart-on-failure), extracted from `Highway.Server.Host`
   and parameterized; the server host is re-pointed to the shared code in this feature
   (no duplicate SCM code, behavior preserved, existing tests kept passing).
3. Linux: `install` writes `/etc/systemd/system/{name}.service` (`Type=notify`,
   absolute `ExecStart`, `WorkingDirectory`, `Restart=on-failure`,
   `WantedBy=multi-user.target`), then `systemctl daemon-reload` + `enable`
   (`--start` → `enable --now`). `uninstall` = `disable --now`, delete unit,
   `daemon-reload`. `start`/`stop`/`status` call `systemctl`. Re-install is idempotent.
4. Not elevated → one sentence ("run as Administrator" / "run with sudo") and the
   elevation exit code, nothing changed. No systemd on the machine → one sentence,
   platform-unsupported code. Known failures each get one plain sentence, never a raw
   error code alone.
5. Arguments after `--` on `install` are stored in the service's start command and
   passed to the app on every service start.

### Requirement 3: Conventions, with overrides

**User Story:** As a developer, I want the service name and description to come from
my app by convention, and be overridable when ops needs a different name.

#### Acceptance Criteria

1. Defaults: service name = entry assembly name; display name = service name;
   description = assembly description (or the display name).
2. Overrides: `--name`, `--display-name`, `--description` on the verbs (and
   `--user` on Linux install). Precedence: verb option > code option > convention.
   Names are validated before any system call.

### Requirement 4: No Highway required

**User Story:** As a developer with a plain .NET app — no Highway shapes, no broker —
I want to use this package purely for hosting and installation.

#### Acceptance Criteria

1. An app with no Highway configuration and no Highway types runs and passes every
   verb round-trip. The verbs never touch Highway at all.
2. When Highway *is* configured, the engine hosted service is registered and all
   existing shapes (services, subscribers, processors, jobs) work unchanged — this
   package adds no per-shape behavior of its own.

### Requirement 5: Proof and docs

#### Acceptance Criteria

1. One small sample (`Highway.Samples.Host`): the one-line `Program.cs` plus a trivial
   hosted service; README shows run + install/uninstall on both platforms. Sample
   suite re-run; `samples/RUNLOG.md` appended, including one real
   `install → status → stop → uninstall` round-trip where the machine allows
   (031's evidence precedent).
2. `docs/product/product.md` package list gains `Highway.Client.Hosting`.
   `docs/HIGHWAY-PROTOCOL.md` untouched — no protocol surface in this feature.
