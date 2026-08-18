# Feature 031 — Server Distribution: Tasks

Phase 0 settles the decisions; Phase 1 builds the executable heart; Phase 2 makes it an
artifact; Phase 3 installs it as a service on both platforms; Phase 4 writes the
record; Phase 5 proves all of it.

**Phase 0 gates implementation: no code before the decisions are resolved with the
user.** OD1–OD8 were approved on 2026-08-11; the same day OD3 was revised (folder
publish, exe + DLLs) and OD9 (dashboard packaging) was added as a separate executable
and then **reverted to embedded** after reconsideration. On 2026-08-12 OD1 was revised:
the executable is **`highways`**, not `highwayd`. The tasks below are written against
the final shape. Several phases touch platform-specific machinery that cannot be fully
exercised on one machine — those tasks say so, and the evidence lands in the RUNLOG
honestly (021's precedent for platform-bound proof).

## Phase 0 — settle the open decisions

### - [x] T0 — OD1–OD9 resolved with the user

*Requirements:* all — the decisions shape everything downstream
**Done when:** each decision in `requirements.md` carries its resolution (chosen option
+ one line of why), and any design section invalidated by a choice is rewritten before
implementation starts. Resolved 2026-08-11: OD1–OD8 as recommended, OD3 revised
(folder publish), OD9 added and reverted (dashboard embedded). Revised 2026-08-12:
OD1 → `highways` at the user's direction; requirements simplified to the plain shape
the ask describes (standalone server; `bin/`, `config/`, `data/`; scripts to run it or
install it as a Windows service or Linux daemon).

## Phase 1 — the host executable and its configuration

### - [x] T1 — `Highway.Server.Host` project, verb dispatch, exit codes

*Requirements:* R1.1, R1.3, R1.6
**Done when:** `src/Highway.Server.Host` exists with project references to
`Highway.Server` and `Highway.Server.Dashboard` (nothing forked, nothing internal);
`--version` prints product version, storage format and RID; unknown arguments exit
non-zero listing the known ones; the exit-code table from the design is the code's
table; `Highway.Server` and `Highway.Server.Dashboard` compile unchanged and every
existing test passes — the embedded path is untouched.

### - [x] T2 — Configuration model, loader, precedence, validation

*Requirements:* R2.1–R2.6
*Depends on:* T1
**Done when:** `highway.json` binds 1:1 onto the options surface (every public option
of `HighwayServerOptions`, dashboard, authentication, TLS — verified by a test that
fails when a public option lacks a schema entry); precedence file < environment < CLI
is tested per level; unknown keys are refused naming the key; sizes (`"512m"`) and
durations parse or fail naming the key; relative paths resolve against the config
file's directory (and environment-sourced paths against CWD, as designed); secrets
mask in any printed effective configuration. Unit tests cover all of it without
starting a server. *(Shipped before the 2026-08-12 amendments; T14 carries them.)*

### - [x] T3 — Host lifetime: one binary, three modes, graceful stop

*Requirements:* R1.1, R1.2, R1.4, R1.5, R2.4
*Depends on:* T2
**Done when:** Generic Host runs the broker via `HighwayServerBuilder` (no internal
access) with `UseWindowsService()` and `UseSystemd()` both registered, each no-oping
off-platform; `--validate` prints the masked effective configuration and exits without
starting; configuration discovery finds `highway.json` beside the working directory,
its config subdirectory, or beside the executable; a missing file logs a warning and
runs code defaults; an integration test starts an ephemeral broker from a temp
`highway.json`, reaches it over real TCP with SE.Redis, and stops it cleanly through
the host's cancellation path; the dashboard comes up when configured. *(Shipped before
the 2026-08-12 amendments; T14 carries them.)*

### - [ ] T14 — Amendments: the rename, the `enabled` switch, and the review findings

*Requirements:* R1.4, R1.6, R2.1, R2.2, R3.1
*Depends on:* T3
**Done when** every item below holds, with the completeness and precedence tests still
green:

1. **The executable is `highways`** — assembly name, usage text, doc comments, tests,
   spec and roadmap (OD1 revised 2026-08-12). No `highwayd` survives outside the one
   line of OD1's history.
2. **`authentication.enabled`** joins the schema: `true` without a mechanism is an
   error naming the key; `false` maps to the builder's deliberate-open
   `WithoutAuthentication()`; omitted infers from the mechanism. A unit test per case.
3. **`conf/` → `config/`** in discovery and every shipped path.
4. **`--validate` validates what would actually run** (R1.4): it applies the same
   discovery as run mode — today it ignores discovery entirely and silently validates
   code defaults — and it exercises the builder's validation, so feature 012's
   bind-address rule refuses there too. Proven by a test: a configuration that cannot
   start cannot validate. `bindAddress: "0.0.0.0"` with no authentication is the case
   that shipped passing.
5. **No startup failure escapes as a stack trace.** `Program` catches only
   `InvalidOperationException`/`IOException`/`UnauthorizedAccessException`; anything
   else (a `SocketException`, a `CryptographicException` from a bad PFX) crashes the
   CLR with no mapped exit code. Every exception out of `Build()`/`Start()` is caught
   and mapped, exit 1 as the catch-all, and the operator sees the sentence only.
6. **`server.observability.maxBytes` accepts `"64m"`**, as `aofSizeLimitBytes` and
   `maxQueueBytes` do — it is missing `SizeJsonConverter`, so the documented form is
   refused from the file while the environment variable accepts it. One key, one answer.
7. **`HighwayServerBuilder.StorageFormatVersion` becomes `static readonly`, not
   `const`** — a `const` is baked into `highways` at compile time, so the day the format
   moves, a stale binary prints the old number, which is exactly what R1.3 exists to
   prevent. R1.6's "additive only" note records the widening from private.

## Phase 2 — the artifact

### - [ ] T4 — Publish pipeline, layout, zip

*Requirements:* R3.1–R3.4
*Depends on:* T1, T14
**Done when:** `scripts/package.ps1` (plus `package.sh` wrapper) builds
`highway-{version}-{rid}/` for win-x64 and linux-x64 with the design's layout —
`bin/` (highways exe + DLLs, self-contained folder publish), `config/highway.json`,
`data/`, `logs/`, `scripts/`, docs — and zips it; the same command from a clean
checkout reproduces the layout; a test asserts the zip's required file list; version
stamps agree across folder name, README header and `--version`. Note the repository has
no central version property yet and `--version` currently prints `1.0.0+{sha}`: this
task establishes the property and decides whether the artifact name carries the suffix.

### - [ ] T15 — The run scripts: standalone in one double-click

*Requirements:* R4.1–R4.4
*Depends on:* T4
**Done when:** `scripts/run.ps1`, `scripts/run.bat` and `scripts/run.sh` start
`bin/highways` with `config/highway.json` using absolute paths derived from the script's
own location, so they work from any working directory; extra arguments pass through
(`run.bat --port 6600`); Ctrl+C stops the broker cleanly and leaves no orphan process;
running one on a freshly unpacked zip is the README's first instruction and part of the
verification.

### - [ ] T5 — The distribution's own documents

*Requirements:* R3.5, R7.3
*Depends on:* T4, T15
**Done when:** the zip's `README.md` runs unpack → run → install → configure → upgrade
in under a page with the exact commands; `LICENSE` and `THIRD-PARTY-NOTICES.md`
(Garnet, StackExchange.Redis) are present; the shipped `highway.json` starts unchanged
(loopback, durable into `../data`, dashboard on loopback) — verified by running it. The
README states plainly that two brokers on one machine need two ports and two data
directories (design § Host Lifecycle: a taken port does not reliably refuse).

## Phase 3 — service installation

### - [ ] T6 — Windows verbs: the SCM, precisely

*Requirements:* R5.2–R5.6
*Depends on:* T3
**Done when:** `--install [--start]`, `--uninstall`, `--status`, `--start`, `--stop`
work via P/Invoke (create/delete/configure) and `ServiceController`
(start/stop/status); install registers the absolute config path and
restart-on-failure (5 s/30 s/60 s, 24 h reset); install is idempotent-or-refused with
the reason and a failed install leaves nothing registered; uninstall stops first and
waits for stopped; privilege is pre-checked (exit 4, "run as Administrator", before
any change); paths with spaces survive quoting; every verb is safe to run twice; an
elevated install → status → stop → start → uninstall round-trip is recorded with its
machine in the RUNLOG.

### - [ ] T7 — Windows scripts the zip promises

*Requirements:* R5.1
*Depends on:* T6
**Done when:** `scripts/install-service.ps1` (+ `.bat` wrapper) and
`scripts/uninstall-service.ps1` call the verbs, handle elevation, and work
double-clicked from the unpacked zip with no arguments; the scripts contain no logic
the verbs do not have.

### - [ ] T8 — Linux: the unit and the daemon scripts

*Requirements:* R6.1–R6.5
*Depends on:* T3
**Done when:** `scripts/highway.service` ships readable with absolute-path templating;
`install-daemon.sh` verifies systemd exists (plain sentence + exit 6 where not),
creates/accepts the service user, substitutes paths, `daemon-reload`s, enables, and
prints every path plus the journald command; `uninstall-daemon.sh` is the documented
inverse; `highways --install` performs the same steps; restart-on-failure with delay is
in the unit; an install → run → uninstall round-trip on a real systemd host is recorded
in the RUNLOG (or recorded honestly as not run, naming why).

## Phase 4 — the record

### - [ ] T10 — UserGuide: Deploying the Broker

*Requirements:* R8.2
*Depends on:* T5, T6, T8
**Done when:** the UserGuide gains the section in the house pattern (concept → what you
get → usage → behaviour) covering embedded, standalone, Windows service and systemd
daemon, plus the configuration reference — and stating why no container image ships
(D8: embed it, or wrap the zip) — the schema is documented here once; the
shipped `highway.json` carries pointers, not a second copy.

### - [ ] T11 — `product.md` and `roadmap.md` catch up

*Requirements:* R8.3
*Depends on:* T5
**Done when:** `product.md`'s hosting row marks standalone deployment delivered and
names the artifact; `roadmap.md` records 031's status and its exclusions; neither
restates anything the UserGuide or the distribution README already owns.

### - [ ] T12 — RUNLOG: the end-to-end proof

*Requirements:* R8.4
*Depends on:* T5, T6, T7, T8, T15
**Done when:** `samples/RUNLOG.md` records: zip built from a clean checkout; unpacked
to a fresh directory; standalone run via `scripts/run` (broker and dashboard both
live); Windows install/uninstall round-trip (elevated); Linux daemon round-trip or the
honest absence of one; each with the evidence named.

## Phase 5 — full verification

### - [ ] T13 — Everything green, from nothing

*Requirements:* R8.5, R3.4, R1.6
*Depends on:* all above
**Done when:** full test suite green; `dotnet build --no-incremental` warning-free;
the packaging command re-run from a clean checkout reproduces the artifact;
`docs/HIGHWAY-PROTOCOL.md` is byte-identical to before the feature (the check, not the
promise); the embedded path's test count is unchanged-or-grown, never shrunk.

**Known external blocker:** `026-distributed-cache/tasks.md` records an OPEN defect —
`AddHighway` opens the cache multiplexer without the client's credentials, failing
`TlsTests.FullClientBehaviour_WorksOverTls`. "Full suite green" cannot be claimed until
that is fixed; it is not 031's defect, but it is 031's gate.

---

**Order:** 0 → 1 (T1 → T2 → T3 → T14) → 2 (T4 → T15 → T5) → 3 (T6 → T7, T8) → 4 → 5.

**Deferred (registered, not built):**

- **A dashboard on another machine** — OD9 considered a separate dashboard executable
  and reverted it: the flight recorder is in-process, and a remote dashboard would
  need its own data transport for a strictly worse stream. If remote placement ever
  becomes a real ask, it gets specced then — with that transport designed, not
  retrofitted.
- **A container image or Dockerfile** — removed 2026-08-12 (D8). The broker embeds like
  SQLite, so an application that hosts it is already the image; a shared broker wraps
  this zip in the cluster's own base image. Revisit only if a deployment appears that
  can do neither.
- **linux-arm64 and macOS builds** — one publish flag each; joins when a deployment
  asks.
- **MSI / .deb / .rpm / winget / apt** — package-manager channels for this artifact;
  a different conversation with signing and update machinery of their own.
- **Folding these installer verbs into 021's `Highway.Hosting`** — possible when 021
  ships; the surfaces are shaped to allow it (same verb names, same exit-code
  discipline), but the fold is not promised.
- **Built-in file logging** — the platform captures stdout; revisit only if a target
  platform appears that cannot.
- **A pre-bind port probe** — open question from the design's Host Lifecycle note: a
  taken port does not reliably refuse on Windows. Decide during Phase 2 whether
  `highways` probes and refuses, or the README carries the whole warning.
