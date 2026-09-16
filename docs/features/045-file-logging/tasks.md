# Feature 045 — Tasks

### - [x] T1 — Serilog file+console logging in the host

**Fulfills:** R1.1, R1.2, R1.3, R1.4
Add `Serilog.Extensions.Hosting` / `Serilog.Sinks.File` / `Serilog.Sinks.Console` to
`Highway.Server.Host`. `HostLogging` resolves the log directory from `AppContext.BaseDirectory`
(`../logs`) and builds a console + daily-rolling-file logger (31 files, 1 GiB cap, roll-on-limit).
`HostFactory.Create` clears the default providers and registers Serilog.
**Done when:** unit — directory resolves rooted to `logs`; a logger write produces a `highway-*.log`
file with the message; host tests stay green.

### - [x] T2 — The record

**Fulfills:** R2.1, R2.2, R2.3
Distribution `README.md` documents that the broker writes rolling logs to `logs/`; the stale
`package.ps1` "scripts redirect to logs" comment corrected; `CHANGELOG.md` 2.0.1 entry.
**Done when:** the docs describe the shipped behaviour with no stale line.

### - [x] T3 — Ship 2.0.1

**Fulfills:** the patch
Version → 2.0.1; re-pack the four NuGet packages and the `win-x64` distribution; verify the zip and
confirm a running broker writes a log file; tag `v2.0.1`.
**Done when:** `VerifyZip.ps1` passes and a real run leaves a `highway-*.log` in the dist `logs/`.
