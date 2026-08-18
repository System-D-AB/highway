# Feature: Server Distribution — Highway as a Deployable Artifact

## Introduction

`product.md` promises *"the server is a Garnet extension you run as a single binary"* and
*"deployed as a standalone process in production."* The code does not keep that promise:
the broker runs standalone only if you write a host yourself. `Highway.Samples.Broker` —
a sample — is the production deployment path today.

This feature closes that gap. It makes `Highway.Server` plus its dashboard into what
MongoDB is when you download it: **a versioned folder you unpack and run**, with `bin/`,
`config/` and `data/` directories, and scripts to run it standalone or install it as a
Windows service or a Linux daemon.

**Lane:** connective tissue. No new verb, no new command, no protocol surface. The broker
this feature packages is exactly the broker that ships today.

### What this feature is

- **`highways`** — one executable that runs the broker and its dashboard from a
  configuration file.
- **`config/highway.json`** — every option the builder exposes, in one file, with
  environment-variable overrides for secrets.
- **A folder, zipped per platform** — `bin/`, `config/`, `data/`, `logs/`, `scripts/`,
  README, licence.
- **Scripts** — run standalone, install/uninstall as a Windows service, install/uninstall
  as a Linux daemon.

### What this feature is not

- **Not feature 021.** Client-node hosting stays its own feature; this reuses 021's
  installer *analysis*, not its unbuilt code.
- **Not a package-manager story.** No MSI, `.deb`, `.rpm`, winget, apt. The zip is the
  artifact, as MongoDB's zip is MongoDB's artifact.
- **Not a container image.** Highway's broker embeds in your application the way SQLite
  does — `HighwayServerBuilder` in your own host, containerized by your own pipeline.
  An application that embeds the broker already has its image, and no one ships a SQLite
  image. Where a *shared* broker needs to run in a cluster, the zip plus ten lines of
  `Dockerfile` is the operator's ten minutes, not a maintained artifact of ours.
- **Not new broker capability.** Everything the packaged broker does, the embedded
  `HighwayServerBuilder` does today. This feature is delivery, not behaviour.

## Decisions

Approved by the user; the reasoning lives in `design.md` § Decisions.

| # | Decision | Resolution |
|---|---|---|
| **OD1** | Executable name | **`highways`** *(revised 2026-08-12 by the user; was `highwayd`)*. Reads as "Highway server"; keeps `highway` free for future client tooling |
| **OD2** | Configuration format | **JSON (`highway.json`)** — the house serializer binds 1:1 onto the options classes; comments and trailing commas permitted |
| **OD3** | Publish flavour | **Self-contained folder publish** — `highways` plus its DLLs in `bin/` *(revised 2026-08-11 by the user; was single-file)*. No .NET runtime prerequisite either way |
| **OD4** | Platforms in v1 | **win-x64 and linux-x64**; arm64 is one publish flag away |
| **OD5** | Service installation | **Scripts are the interface; the executable owns the verbs.** The verbs hold idempotence and precise errors; the scripts are what an operator double-clicks. Windows via SCM P/Invoke + `ServiceController` (021 OD1 adopted wholesale); Linux renders the unit and drives `systemctl` |
| **OD6** | Relative paths | **Against the config file's own directory** — CWD is untrustworthy under services; the shipped config points at `../data`; absolute paths always honoured |
| **OD7** | Logging | **Stdout, platform-captured** — Event Log under the Windows SCM, journald under systemd. `logs/` ships as the scripts' redirect target |
| **OD8** | Relation to 021 | **Build bespoke now.** 021 is specced but unbuilt; folding this installer into it later is registered as deferred |
| **OD9** | Dashboard packaging | **Embedded in `highways`** *(added 2026-08-11 as a separate executable, reverted the same day)*. The flight recorder lives in the broker's memory; a separate process would need a polling bridge to receive a strictly worse copy of what the embedded dashboard gets live. One process, one config, one service. Remote placement is registered as deferred |

## Requirements

### Requirement 1: One Executable Runs the Broker

**User Story:** As an operator, I want one binary that starts the broker and its dashboard,
so that "run Highway" is one command like `mongod`.

#### Acceptance Criteria

1. `highways` starts a `HighwayServerBuilder`, applies the configuration (R2), enables the
   dashboard when configured, and runs until stopped. It uses the same public builder path
   the samples use — no internal access, no host-only server API
2. It is a **console application by default**: logs to stdout, `Ctrl+C` stops it cleanly.
   Under the Windows SCM or systemd the same binary runs as a service — the mode is
   detected, never declared
3. `highways --version` prints the product version, the storage format version, and the RID
   it was built for, so an operator upgrading in place can ask both binaries what they are
4. `highways --validate [--config <path>]` loads and validates **the configuration that
   would actually run** (same discovery as run mode), prints it with secrets masked, and
   exits without starting the server. Anything that would refuse at startup — including the
   bind-address rule of feature 012 — fails here too. A configuration error is a sentence
   naming the key, never a stack trace
5. Stopping the process — `Ctrl+C`, SIGTERM, or a service stop — disposes the server
   through its existing graceful path: components dispose, the recorder flushes, Garnet
   commits and closes the AOF. No new shutdown semantics, and none broken
6. The embedded path is **untouched**: `Highway.Server` and `Highway.Server.Dashboard` keep
   their behaviour and their API, save for anything additive this feature needs and records.
   Every existing test passes unchanged

### Requirement 2: One File Configures Everything

**User Story:** As an operator, I want a single configuration file with every option the
server supports, so that a deployment is one reviewable document.

#### Acceptance Criteria

1. `config/highway.json` covers **every** public option of `HighwayServerOptions`, the
   dashboard, authentication and TLS. An option that exists in code but not in the schema
   is a defect in this feature, and a test says so
2. Authentication is expressible as an explicit `enabled` switch plus a `password` or a
   Garnet ACL file path. `enabled: true` with no mechanism is an error; `enabled: false`
   maps to the builder's deliberate-open mode (warned at every start); omitted infers from
   the presence of a mechanism. TLS is expressible as PFX file + password, or
   certificate-store subject name, plus the mTLS options. Feature 012's bind-address rule
   is enforced exactly as today
3. Precedence is **defaults < file < environment < command line**, and is tested per level.
   Every key has a systematic `HIGHWAY_*` environment override; `--config`, `--port`,
   `--bind`, `--data-dir` are the command-line set. Secrets are documented as
   environment-first: a production deployment never needs them in the file
4. An invalid value fails `--validate` and startup alike, naming the key and the value
   (`authentication.aclFile does not exist: /etc/highway/users.acl`). An unknown key is an
   error, not a silence
5. Relative paths in the file resolve against the config file's directory (OD6), so the
   shipped `../data` works from any working directory. Absolute paths are honoured verbatim
6. The file permits `//` comments and trailing commas — a configuration file an operator
   annotates is one an operator reads

### Requirement 3: The Unpacked Folder Is the Product

**User Story:** As an operator, I want to download one archive, unpack it, and have
everything needed to run and install the broker — the way I do with MongoDB.

#### Acceptance Criteria

1. A build step produces, per RID, `highway-{version}-{rid}/` containing:

   ```
   bin/        highways(.exe) and its DLLs
   config/     highway.json
   data/       empty; where the broker writes
   logs/       empty; the scripts' redirect target
   scripts/    run, install and uninstall scripts (R4, R5, R6)
   README.md   unpack -> run -> install, in under a page
   LICENSE, THIRD-PARTY-NOTICES.md
   ```

   The zipped folder is the downloadable artifact
2. The executable is a self-contained folder publish (OD3): unpacking on a clean machine
   with **no .NET runtime** runs the broker and its dashboard. The verification runs it
3. The folder name, `--version` output and README header carry the same version. An
   operator can never be unsure which build a directory holds
4. The same command reproduces the folder from the same source and version — no
   timestamp-dependent content, no machine-specific paths inside
5. **The zero-edit first run works**: unpack, run the script, and the broker is up on
   loopback, durable into `data/`, dashboard on loopback, with the shipped config unedited

### Requirement 4: Run It Standalone

**User Story:** As an operator evaluating or running Highway in the foreground, I want a
script in the folder that starts it, so that I do not have to know the flags.

#### Acceptance Criteria

1. `scripts/run.ps1` (with a `run.bat` wrapper) and `scripts/run.sh` start `bin/highways`
   with `config/highway.json`, from any working directory — every path they pass is absolute
2. They pass extra arguments through (`run.bat --port 6600`), so the script is never a wall
   between the operator and the executable
3. Ctrl+C in the script's console stops the broker cleanly — the script must not swallow
   the signal or leave an orphan process
4. The scripts are the README's first instruction, and running one on a freshly unpacked
   zip is part of the verification

### Requirement 5: Install as a Windows Service

**User Story:** As a Windows operator, I want to install the broker as a service from the
unpacked folder, so that it starts on boot, restarts on crash, and stops cleanly.

#### Acceptance Criteria

1. `scripts/install-service.ps1` (with an `install-service.bat` wrapper) and
   `scripts/uninstall-service.ps1` do the job from the unpacked folder, double-clickable,
   with no arguments required. They carry no logic the executable's verbs do not have
2. The executable owns the verbs: `highways --install [--start]`, `--uninstall`,
   `--status`, `--start`, `--stop`. Success is exit code 0; each failure class has a
   distinct, documented non-zero code
3. Install registers the service with the configuration file's **absolute** path and a
   restart-on-failure policy (restart after 5 s, 30 s, 60 s). A crashed broker that stays
   down is the outcome an installer exists to prevent
4. **Install is idempotent in outcome, never partial**: an already-installed service is
   updated or refused with the reason; a failed install leaves nothing registered.
   **Uninstall stops first** and waits for stopped, because removing a running Windows
   service otherwise needs a reboot to finish
5. Insufficient privilege is detected before any change and reported as "run as
   Administrator" — never a raw access-denied. Paths containing spaces are quoted correctly
6. Service identity (name, display name, description) is defaulted (`Highway Server`) and
   overridable. Two brokers on one machine install as two services with two configurations

### Requirement 6: Install as a Linux Daemon

**User Story:** As a Linux operator, I want a systemd unit and an install script, so that
the broker behaves like every other daemon on the host.

#### Acceptance Criteria

1. `scripts/install-daemon.sh` and `scripts/uninstall-daemon.sh` install and remove the
   unit, printing every path they touch. `scripts/highway.service` ships beside them —
   readable and diffable before anyone runs anything. `highways --install` does the same
   thing for an operator who prefers the verb; both routes end at the same unit
2. The unit uses `Type=notify` readiness and restart-on-failure with a delay. Config, data
   and working directory are absolute; nothing depends on the invoking shell
3. The unit does not run as root by default: the README documents creating a `highway`
   user, and the script accepts the user as a parameter. Ports below 1024 are documented as
   needing capabilities or a proxy, not root-by-default
4. Logs land in journald via stdout, and the README shows the one command to read them. No
   log rotation is invented — journald already does it
5. Missing systemd is reported in a plain sentence rather than producing a unit file
   nothing will read

### Requirement 7: Upgrading Does Not Eat Data

**User Story:** As an operator running Highway in production, I want to move to a new
version without losing a message, and to know before I start whether I can.

#### Acceptance Criteria

1. The documented procedure is: stop the service, unpack the new version, point it at the
   old data directory, start. The existing storage-format guard (`highway.format`) and
   command-manifest guard protect the data directory exactly as they do today — this
   feature changes neither, and says so
2. A version mismatch is refused at startup with the existing message naming both ways out
   (drain with the old version, or delete). `--version` names the storage format so an
   operator can check without starting
3. The zip ships upgrade notes for the version it carries. When the storage format has not
   moved, the notes say so — absence of news is news

### Requirement 8: The Record

**User Story:** As a Highway maintainer, I want the distribution held to the same standard
as everything else, so that it does not drift the way unowned artifacts drift.

#### Acceptance Criteria

1. `docs/HIGHWAY-PROTOCOL.md` is **not modified**. If that file moves, this feature grew
   something it should not have
2. The UserGuide gains a "Deploying the Broker" section in the house pattern (concept →
   what you get → usage → behaviour) covering embedded, standalone, Windows service and
   systemd, plus the configuration reference. The schema is documented once, there; the
   shipped `highway.json` carries pointers, not a second copy
3. `product.md`'s hosting row marks standalone deployment delivered and names the artifact;
   `roadmap.md` records 031's status and its exclusions
4. `samples/RUNLOG.md` records the end-to-end proof: zip built, unpacked clean, run from
   the script, Windows service installed and uninstalled, Linux daemon installed and
   removed (or recorded honestly where the machine cannot)
5. All tests pass; `dotnet build --no-incremental` warning-free; the packaging step is
   repeatable from a clean checkout with one documented command

## Non-Goals

- **Client-side hosting** — feature 021's territory. Its analysis is reused; its package is
  not built here.
- **MSI, .deb, .rpm, winget, apt, Chocolatey** — distribution channels for this artifact,
  with signing and update machinery of their own. The zip precedes all of it.
- **A container image, a Dockerfile, or registry publishing.** The broker embeds like
  SQLite: an application that hosts it is already a container. A shared broker in a
  cluster wraps this zip in whatever base image that cluster standardises on — a
  `Dockerfile` we shipped would be a guess at someone else's base image, someone else's
  user id, someone else's secret store, maintained forever.
- **A multi-broker topology** — Highway is a single broker by constitution (C5). Two brokers
  on one machine is two instances with two ports and two data directories; clustering is not
  a distribution feature.
- **Built-in file logging, log rotation, metrics endpoints** — the platform captures stdout;
  the dashboard and `HW.STATS` already answer observability questions.
- **Auto-update** — a broker holding durable queues does not replace itself.
- **Changing any broker default.** The packaged broker runs with the defaults
  `HighwayServerOptions` ships — loopback, durable, 6500/7500. A deployment that wants
  something else says so in `highway.json`.

## Cross-References

- `docs/features/021-highway-hosting/requirements.md` — the sibling spec: its non-goals
  carve out this feature; its installer verbs, failure classes and OD1 are adopted wholesale
- `docs/features/016-retention-and-durability/` — durable-by-default and the data directory
  beside the executable, which the shipped config inherits
- `docs/features/012-introduce-security/` — the bind-address rule, password and TLS options
  this config file maps 1:1
- `docs/features/011-dashboard-flight-recorder/` + `020`/`022`/`023` — the dashboard this
  distribution bundles
- `docs/product/product.md` — the hosting promise this feature keeps
- `docs/product/constraints.md` — C5 (single broker, no HA) bounds what "deploy" can imply
