# Feature 045 — File Logging for the Packaged Broker

*A patch (2.0.1) fixing a distribution defect found in the field: a broker installed as a Windows
service produced no readable log. Its stdout — the only sink the default logging pipeline reached
interactively — is discarded by the Service Control Manager, and the distribution's shipped
`logs/` folder was never written to by anything.*

## The defect

The 2.0 host builds logging with `Host.CreateApplicationBuilder()` + `AddWindowsService()`, giving
Console (discarded under a service, which has no console) and the Windows Event Log (Warning-and-
above by default, so a clean Info-level startup shows nothing). There is **no file sink**. The
`run.bat`/`run.ps1` scripts run the exe directly without redirecting output, and a service has no
stdout to redirect regardless. Result: `logs/` stays empty and operators have no log.

## Requirements

### Requirement 1: The broker writes readable log files

**User Story:** As an operator running Highway.Server as a Windows service, I want log files on
disk, so I can see what the broker is doing without a console or the Event Viewer.

#### Acceptance Criteria

1. When the broker runs, it writes **rolling daily** log files into the distribution's `logs/`
   folder, and continues to write to the **console** for interactive runs.
2. The log directory resolves from the **executable location**, not the process working directory
   (a service's working directory is `System32`), so it lands in the shipped `logs/` regardless.
3. The files are **bounded**: a size cap with roll-on-limit and a retained-file-count limit, so
   `logs/` never grows without bound.
4. By **convention, no configuration knob** (as with the cache directory) — the layout is fixed by
   the distribution. A future feature may add a `logging` config section; this patch does not.

### Requirement 2: The record is honest

**User Story:** As an operator reading the distribution docs, I want them to say where logs go.

#### Acceptance Criteria

1. The distribution `README.md` states that the broker writes rolling logs to `logs/`.
2. The stale packaging comment claiming the run scripts redirect to `logs/highway.log` is corrected.
3. `CHANGELOG.md` records the fix under 2.0.1.
