# Feature 045 — Design

## Approach: Serilog, console + rolling file

The host replaces its default logging pipeline with **Serilog** (`Serilog.Extensions.Hosting` +
`Serilog.Sinks.Console` + `Serilog.Sinks.File`). Serilog is the de-facto .NET file-logging library
and gives daily rolling, size caps and retention out of the box — the alternative (a hand-rolled
`ILoggerProvider`) would reimplement rolling and retention for no benefit. This is a deliberate,
scoped dependency on the **server host only** (not on `Highway.Client` or `Highway.Abstractions`,
which keep their minimal dependency sets); the broker executable is an application, not a library.

## The log directory (the load-bearing detail)

The distribution lays out `bin/highways.exe` with a sibling `logs/`. A Windows service runs with
its working directory set to `System32`, so a *relative* `logs` path would write there. The log
directory is therefore resolved from the **executable's base directory**, not the working
directory:

```
Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "logs"))
```

`AppContext.BaseDirectory` is `…/bin/` for the folder (non-single-file) self-contained publish the
packaging produces, so `../logs` is the shipped distribution folder. `HostLogging.CreateLogger`
creates the directory if missing.

## Wiring

`HostFactory.Create` clears the default providers and registers Serilog before the hosted service:

```csharp
builder.Logging.ClearProviders();
builder.Services.AddSerilog(HostLogging.CreateLogger(HostLogging.ResolveLogDirectory()), dispose: true);
```

`ClearProviders` drops the default Console/Debug/EventSource/EventLog set so there is exactly one
pipeline (no double console). `AddWindowsService()`/`AddSystemd()` still set the host **lifetime**;
only the logging providers change. The `--version`/`--validate`/`--status` verbs return before the
host is built, so they are unaffected and Serilog never initialises for them.

## File policy

- Path: `logs/highway-.log`, `RollingInterval.Day` → `highway-YYYYMMDD.log`.
- `retainedFileCountLimit: 31`, `fileSizeLimitBytes: 1 GiB`, `rollOnFileSizeLimit: true`, `shared: true`.
- Template includes timestamp, level, `SourceContext` and exception.

## Testing strategy

| Layer | Proof |
|---|---|
| Directory resolution | `ResolveLogDirectory()` is rooted and is the `logs` folder |
| File write | `CreateLogger(temp)` + a log call produces a `highway-*.log` containing the message |
| End-to-end | run the packaged `highways.exe` briefly; a log file appears in the dist `logs/` |

## Non-goals

A `logging` configuration section (levels, path, retention overrides) — deferred; this patch fixes
the "no logs at all" defect by convention, matching the cache-directory precedent.
