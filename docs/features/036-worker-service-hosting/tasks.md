# Tasks: Service Hosting (Feature 036)

> This spec was rewritten 2026-08-29 to its final, minimal scope: one-line host +
> service verbs, TopShelf-style. Earlier drafts (installer-only, worker shapes,
> singleton, health, emitters, tutorial sample) are superseded; none were implemented.
> `IWorker`/supervision, health endpoints, and cluster-singleton workers are candidate
> follow-up features, not part of 036.

```
T1 ──► T2 ──► T3 (windows) ──► T6
        └───► T4 (linux)   ──► T6
T1 ──► T5 (host) ──────────► T6
```

### - [x] T1 — Package skeleton

**Fulfills:** R1 (foundation)
`src/Highway.Client.Hosting/` (net10.0, 035 NuGet conventions) +
`tests/Highway.Client.Hosting.Tests/`; solution updated.
**Done when:** builds; empty test project green.

### - [x] T2 — Verb parsing, identity, exit codes

**Fulfills:** R2.1, R3
`VerbParser` (five verbs, options, `--` passthrough, unknown option = error+usage),
`ServiceIdentity` (convention → code → verb option; validation before any system
call), `ExitCodes` moved from 031 with values preserved (mapping in commit message).
**Done when:** unit tests cover the parser matrix, the precedence chain, every
validation rejection.

### - [x] T3 — Windows: extract 031's `WindowsServiceManager`

**Fulfills:** R2.2, R2.4, R2.5
Move into the package; parameterize identity; server's `--config` becomes a
passthrough arg; `Highway.Server.Host` re-pointed, private copy deleted.
**Done when:** `WindowsServiceVerbsTests` passes re-pointed; server verb
messages/exit codes unchanged (snapshot compare); elevated
`install → status → start → stop → uninstall` round-trip recorded in RUNLOG where the
machine allows.

### - [x] T4 — Linux: `SystemdUnit` + `SystemdServiceManager`

**Fulfills:** R2.3, R2.4, R2.5
Deterministic unit renderer (golden-file tested); manager: root check → systemd check
→ install (write, `daemon-reload`, `enable [--now]`, idempotent) / uninstall /
start / stop / status via injectable process runner.
**Done when:** golden files pass on all OSes; fake-runner tests cover each verb's
arguments and error sentences; real round-trip on WSL2/Linux recorded in RUNLOG where
available.

### - [x] T5 — `HighwayHost.RunAsync`

**Fulfills:** R1, R4
Three overloads; verb dispatch before host construction; content-root normalization;
`UseWindowsService` + `UseSystemd`; `AddHighway` + engine only when configured.
**Done when:** tests prove — plain app (no Highway config, own hosted service) runs
and stops with exit 0 and no Highway services registered; Highway app round-trips a
shape against embedded Garnet; a verb path never constructs a host; Ctrl+C drains
within the shutdown timeout.

### - [x] T6 — Sample, RUNLOG, product docs

**Fulfills:** R5
`Highway.Samples.Host`: one-line `Program.cs` + one trivial hosted service; README =
run + install/uninstall recipe per platform. Re-run samples, append RUNLOG (including
the T3/T4 real round-trips). `product.md` package list updated;
`HIGHWAY-PROTOCOL.md` untouched (assert).
**Done when:** samples run clean; RUNLOG entries present; product.md lists the
package; zero protocol diff.
