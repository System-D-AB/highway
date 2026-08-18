# Feature 031 — Server Distribution: Design

## Architecture Overview

One new project, one new artifact, zero changes to the broker's behavior:

```
src/
├── Highway.Server/                  ← unchanged. The broker, embedded or standalone.
├── Highway.Server.Dashboard/        ← unchanged. ASP.NET Core component.
└── Highway.Server.Host/             ← NEW. The executable host ("highways").
       │
       │  references (project refs — never a fork of the builder path)
       ▼
   HighwayServerBuilder ──► HighwayServerOptions ──► GarnetServerOptions
       │                          ▲
       │  WithDashboard(...)      │  binds 1:1
       ▼                          │
   DashboardComponent      highway.json + HIGHWAY_* env + CLI args
```

The host is a **consumer** of the exact public API the samples use —
`HighwayServerBuilder → Build() → RunAsync(ct)`. It adds configuration loading,
service-lifetime integration, and installer verbs. It does not add a server capability;
a behavior difference between `highways` and an embedded builder is a defect in the
host, not a feature of it. The dashboard stays embedded (OD9, after reconsideration):
its data source — the flight recorder — lives in the broker's memory, and the
in-process component is the only dress that sees it live.

Runtime shape of the packaged broker:

```
highways (one process)
├── Garnet listener            :6500   (RESP — HW.* + standard commands)
└── Dashboard component        :7500   (HTTP — ASP.NET Core, embedded assets)
```

### Dependencies the host takes, and why they are safe

| Package | Why | Why it does not violate minimalism |
|---|---|---|
| `Microsoft.Extensions.Hosting` | Generic Host: configuration binding, logging, lifetime, `Ctrl+C`/SIGTERM handling | Already an approved dependency (coding-standards); the host *is* a hosted service |
| `Microsoft.Extensions.Hosting.WindowsServices` | `UseWindowsService()` — SCM control codes, Event Log | Acquired only by the executable artifact; no library consumer ever sees it |
| `Microsoft.Extensions.Hosting.Systemd` | `UseSystemd()` — `Type=notify`, journald shaping | Same — artifact-only |

Feature 021's "separate package, for reasons" logic applies inverted: 021 keeps
installation machinery *out of* a library users reference; here the machinery lives in
an executable nobody references. The broker libraries (`Highway.Server`,
`Highway.Server.Dashboard`) take **no new dependency**.

---

## Decisions

**D1 — The artifact is an executable host project, not a publish profile on `Highway.Server`.**
`Highway.Server` is a class library referenced by applications; giving it a `Main` would
make every consumer's build argue about entry points and RIDs. A thin host project keeps
the library clean and gives the artifact somewhere to own configuration, verbs and
lifetime. *Rejected:* publishing the sample broker — a sample is documentation, and its
argument/env surface (SampleConfig) is deliberately smaller than a production config.

**D2 — Configuration is one JSON document bound onto the existing options classes.**
The schema (§ Configuration Model) maps 1:1 onto `HighwayServerOptions`,
`DashboardOptions`, `AuthenticationOptions`, `TlsOptions`. No new options are invented;
an option reachable from the builder is reachable from the file. `System.Text.Json` with
comments and trailing commas permitted (`ReadCommentHandling.Skip`,
`AllowTrailingCommas`), case-insensitive property matching — the house serializer doing
house work. *Rejected:* TOML/INI (a second parser for no expressive gain; nested
dashboard/TLS sections degrade badly in INI); MongoDB-style YAML (a YAML dependency).

**D3 — Precedence: defaults < file < environment < command line.**
Environment overrides exist so secrets never need to sit in a file a service account can
read; command line exists so a support session can change one value without editing.
The mapping is systematic (§ Environment Overrides), not curated — a curated subset
would silently strand options.

**D4 — Relative paths resolve against the config file's directory.**
Services do not have a working directory an operator chose: the SCM starts processes in
`C:\Windows\system32`, systemd in `/` (or `WorkingDirectory` if set — which this unit
sets to the distribution root, but the config must not depend on it).
Executable-relative was rejected because it puts `data/` inside `bin/`, where an
upgrade that replaces the folder endangers it. The shipped `highway.json` therefore
carries `"dataDir": "../data"`; absolute paths are honored verbatim and are the
documented production form (`/var/lib/highway`, `D:\Highway\data`).

**D5 — Scripts are the operator's interface; the verbs behind them live in the executable.**
An operator who unpacks the zip should never have to compose a command line: `scripts/`
holds `run`, `install-service`, `uninstall-service`, `install-daemon` and
`uninstall-daemon`, and the README points at those. `run.ps1`/`run.bat`/`run.sh` are
one line each — `bin/highways --config config/highway.json "$@"` — because a foreground
start needs no logic, only correct absolute paths and argument pass-through (R4).
The service scripts are equally thin, because the logic lives in the verbs:
`highways --install|--uninstall|--status|--start|--stop`, handled before the host
starts, with distinct exit codes (§ Exit Codes). Windows uses P/Invoke against the SCM
for create/delete/configure (real Win32 codes — `ERROR_SERVICE_EXISTS`,
`ERROR_ACCESS_DENIED` — map directly onto human messages) and `ServiceController` for
start/stop/status — feature 021 OD1's recommendation, adopted without re-litigation.
Linux `--install` renders the unit file, prints its path, and drives `systemctl`. The
shipped `.ps1`/`.bat`/`.sh` scripts call these verbs: an unpacked zip should contain
the thing its README points at, and a double-clickable entry point is part of the
MongoDB contract. *Rejected:* scripts-only (parsing `sc.exe`'s localized output for
error causes is the trap 021 documented).

**D6 — Distribution = self-contained folder publish (executable + DLLs), per RID, zipped.**
*(Revised 2026-08-11 by the user — the original recommendation was single-file.)*
The MongoDB contract is "unpack and run on a clean machine." Framework-dependent
publish breaks on machines without the matching ASP.NET Core runtime (the dashboard's
`FrameworkReference` means the *ASP.NET Core* runtime, not just .NET — a trap in
itself), so the executable publishes `--self-contained`; the user chose the folder
shape (exe beside its DLLs) over a fused single file. `bin/` therefore holds
`highways` plus its DLLs — one executable, the dashboard inside it (D10).
win-x64 and linux-x64 in v1; linux-arm64 is one flag away when a deployment asks.

**D7 — Logging is stdout; the platform captures it.**
`UseWindowsService` adds the Event Log automatically; systemd captures stdout into
journald; containers capture stdout by definition. The `logs/` directory ships for the
Windows scripts' redirection (`>> logs\highway.log`) and for operators who want a file
without a framework. Adding Serilog/NLog to the broker for what every target platform
already provides is the dependency Highway exists to delete.

**D8 — No container image, and no Dockerfile.**
*(Decided 2026-08-12 by the user; the earlier draft carried a multi-stage Dockerfile,
a container `highway.json` and a compose file.)* Highway's broker is embeddable the way
SQLite is: `HighwayServerBuilder` inside your own application, containerized by your own
pipeline. An application that embeds it **is** the image — there is nothing left for a
Highway image to contain, which is why nobody ships a SQLite image either. The case the
analogy does not cover is a broker *shared* by several services in a cluster, which is a
process someone must run; there, the answer is the zip plus a `Dockerfile` written
against that cluster's own base image, user id and secret store. Shipping ours would be
a guess at all three, maintained forever, to save ten lines. *Consequence, stated
plainly:* the UserGuide's deployment section covers embedded, standalone, Windows
service and systemd — and says that the container path is "embed it, or wrap the zip",
so the absence is documented rather than discovered.

**D9 — No shared code with 021 yet; the fold is registered, not promised.**
021 is specced, not built. Its installer *analysis* is reused wholesale (privilege
pre-check, muxer note inapplicable here — the zip is always self-contained, so there is
no `dotnet` muxer case; paths-with-spaces; restart-on-failure). If and when 021 ships
a general installer, this feature's verbs may fold into it; the fold is a task in the
Deferred table, not a prerequisite.

**D10 — The dashboard stays embedded in `highways` (OD9, reconsidered and reverted).**
OD9 was resolved on the morning of 2026-08-11 as a separate dashboard executable and
reverted the same afternoon, on the engineering merits. The dashboard's primary data
source is the broker's flight recorder — an in-process structure. A separate process
cannot subscribe to it; it would have to poll `HW.REPLAY` with a moving cursor, which
means building a whole data bridge to deliver a **strictly worse** (near-live) copy of
what the embedded component gets live. One process also means one config file, one
service registration, one set of credentials, and zero refactor of the shipped, tested
`DashboardComponent` path. The layout preference that motivated the split (a
`dashboard/` folder) is cosmetic next to that. *Registered as deferred:* placing a
dashboard on another machine — a real ask if it ever appears, to be specced then with
its own data transport, not retrofitted now.

---

## Configuration Model

### Schema — `highway.json`

One document, four sections. Every field optional; every default is the code's
default. The shipped file spells out the load-bearing ones and comments the rest.

```jsonc
{
  // ── Broker ────────────────────────────────────────────────────────────
  "server": {
    "port": 6500,                          // Garnet RESP listener
    "bindAddress": "127.0.0.1",            // loopback default; "0.0.0.0" to expose
    "dataDir": "../data",                  // relative → config file's directory
    "ephemeral": false,                    // true = memory-only, nothing survives
    "aofSizeLimitBytes": "512m",            // checkpoint + truncate threshold
    "maxQueueBytes": "1g",                 // per-queue cap (C4.7: not per-server)
    "lease": "00:05:00",                   // RPC processing lease
    "replySlotTtl": "00:05:00",
    "maxPayloadBytes": 1048576,
    "maxIdentifierBytes": 256,
    "nodeExpiry": "00:00:30",
    "pruningEnabled": true,
    "maxCatalogBytes": 262144,
    "subscriberRetirementThreshold": "24:00:00",
    "maxDeliveryAttempts": 5,
    "maxDeadLetterEntries": 10000,
    "pubSubBackoffEnabled": false,
    "rpcBackoffEnabled": false,
    "maxBackoff": "00:01:00",
    "receiveDefaultCount": 10,
    "receiveMaxCount": 500,
    "waitForCommit": false,
    "observability": {
      "recorderEnabled": true,
      "defaultCapacity": 1000,
      "defaultRetention": "01:00:00",
      "defaultCapture": "Full",            // Full | HeadersOnly | Off
      "maxBytes": "64m",
      "sweepInterval": "00:00:10",
      "replayEnabled": true,
      "replayDefaultLimit": 100,
      "replayMaxLimit": 1000,
      "replayDefaultWindow": "00:05:00",
      "activitiesEnabled": true,
      "overrides": {                        // per-name overrides, keyed by event name
        "orders.placed": { "capture": "Off" }
      }
    }
  },

  // ── Authentication (feature 012) ─────────────────────────────────────
  "authentication": {
    "enabled": true,                       // explicit on/off; omitted = infer from mechanism
    "password": null,                      // one shared password; username is 'default'
    "aclFile": null                        // Garnet ACL file → named users; XOR with password
  },

  // ── TLS (feature 012) ────────────────────────────────────────────────
  "tls": {
    "certFile": null,                      // PFX path; XOR with certSubjectName
    "certPassword": null,
    "certSubjectName": null,               // machine certificate store lookup
    "clientCertificateRequired": false,    // mTLS
    "revocationMode": "NoCheck",           // NoCheck | Online | Offline
    "issuerCertificatePath": null,         // private CA for client certs
    "refreshFrequencySeconds": 0           // cert rotation without restart; 0 = off
  },

  // ── Dashboard (features 011/020/022/023) ─────────────────────────────
  "dashboard": {
    "enabled": true,
    "port": 7500,
    "bindAddress": "127.0.0.1",
    "pathBase": "",
    "apiKey": null,                        // required wherever the dashboard is exposed
    "maxConcurrentStreams": 4,
    "streamBufferCapacity": 512,
    "keepAliveInterval": "00:00:15"
  }
}
```

Semantics of `authentication.enabled`: `true` requires a mechanism (password or ACL
file) and errors without one; `false` maps to the builder's deliberate-open mode
(`WithoutAuthentication()` — required to run off loopback without auth, warned at
every start); omitted infers from the presence of a mechanism, which keeps the file
backward-compatible with the password-only shape.

Size strings (`"512m"`, `"1g"`) parse with the same rules Garnet uses; durations are
ISO-like `"hh:mm:ss"`. Both are parse-or-fail with the key named.

### Mapping onto the builder

Loading is a pure function, tested in isolation:

```csharp
// Highway.Server.Host
HostConfiguration Load(path?, env, args)     // precedence applied here
HighwayServerBuilder Apply(HostConfiguration) // builder calls, nothing else
```

`Apply` translates sections to calls — `WithPort`, `WithBindAddress(string)`,
`WithDataDir`, `WithOptions(o => …)` for the long tail, `WithPassword` /
`WithAuthentication(new AclAuthenticationPasswordSettings(aclFile))` /
`WithoutAuthentication()` per `authentication.enabled`, `WithTls(…)`,
`WithDashboard(…)` — so the builder's existing validation (`Build()`'s
`Validate()` calls, the 012 bind-address rule, storage-format guard) runs unchanged.
The host never reaches past the builder into Garnet options.

The escape hatches (`WithAuthentication(IAuthenticationSettings)`,
`WithTls(IGarnetTlsOptions)`) are **not** reachable from JSON: they take live objects.
The ACL file path and the TLS fields already cover their real-world uses; a deployment
needing more hosts the builder itself — which remains the documented escape hatch,
unchanged.

### Environment Overrides

Every configuration key has one: `HIGHWAY_` + section + key, underscored, upper-cased.

| Key | Variable |
|---|---|
| `server.port` | `HIGHWAY_SERVER_PORT` |
| `server.bindAddress` | `HIGHWAY_SERVER_BINDADDRESS` |
| `server.dataDir` | `HIGHWAY_SERVER_DATADIR` |
| `authentication.password` | `HIGHWAY_PASSWORD` *(short form — it is the one operators type)* |
| `authentication.aclFile` | `HIGHWAY_ACL_FILE` |
| `tls.certFile` | `HIGHWAY_TLS_CERTFILE` |
| `tls.certPassword` | `HIGHWAY_TLS_CERTPASSWORD` |
| `dashboard.apiKey` | `HIGHWAY_DASHBOARD_APIKEY` |
| … | systematic for the rest |

An environment value beats the file; a command-line value beats both. Relative paths
from the environment resolve against the **current directory** (the file's directory
is unknowable to an environment variable) — documented in the README's one table.

An **unknown `HIGHWAY_*` variable is ignored, not an error**: the process environment
is shared space — the samples use their own `HIGHWAY_*` names, and a hard failure
there would break innocent shells. The JSON file, which the operator fully owns, is
the strict surface; unknown keys are refused there (R2.4).

### Command-line arguments

```
highways [--config <path>]              run with configuration
         [--port N] [--bind ADDR] [--data-dir PATH]   override one value
         [--validate]                   check configuration, print effective (masked), exit
         [--version]                    version + storage format + RID, exit
         [--install [--start]] [--uninstall] [--status] [--start] [--stop]
                                        service verbs (exit before the host starts)
         [--service-name NAME] [--service-display NAME]
```

Unknown arguments are an error that lists the known ones — a broker is not a place for
silent typos.

### Exit Codes

| Code | Meaning |
|---|---|
| 0 | success (ran and stopped cleanly; verb succeeded; `--validate` passed) |
| 1 | unexpected failure |
| 2 | configuration invalid (message names the key) |
| 3 | data directory unusable or incompatible (storage format, permissions) |
| 4 | privilege insufficient for a service verb |
| 5 | service state conflict (install over existing, uninstall of absent…) |
| 6 | platform unsupported for the verb (no SCM, no systemd) |

---

## Host Lifecycle

```
Main(args)
 │
 ├─ verb dispatch (before any host exists)
 │    --version / --validate / --install / --uninstall / --status / --start / --stop
 │    each returns an exit code; none starts the server
 │
 └─ run mode
      Host.CreateApplicationBuilder
        ├─ config: highway.json (discovered: --config > ./highway.json > ./config/highway.json > beside exe)
        ├─ env + CLI overrides applied
        ├─ UseWindowsService() + UseSystemd()   // both registered; each no-ops off-platform
        └─ hosted service: HighwayBrokerService
              StartAsync:  builder = Apply(config); _server = builder.Build(); _server.Start()
              StopAsync:   cancel the run token → RunAsync completes → Dispose()
```

Discovery order is deliberate: an explicit `--config` wins; a file beside the working
directory or in `config/` serves the unpacked zip from any reasonable launch point; the
file beside the executable covers `bin\highways.exe` invoked bare. **No configuration
file at all is valid** — the code defaults run (loopback, durable beside the
executable), which is the evaluation path. A warning logs that no file was found, so
the absence is visible rather than silent.

**`--validate` runs the same discovery and the same checks as run mode** (R1.4). It is
worthless otherwise: a `--validate` that reads a different file, or that passes a
configuration the builder would refuse, tells an operator the opposite of the truth.
So it discovers exactly as above, and it exercises the builder's validation — feature
012's bind-address rule included — without starting a listener. Only genuinely
runtime-bound failures (a port already taken, a disk that fills) are outside its reach,
and the README says which those are.

Startup failures (bad key, unreadable cert, unwritable data directory) print the
builder's existing exception message — which already names causes and ways out — and
exit with the mapped code. The host adds no error text of its own beyond the mapping;
two vocabularies for one failure is how runbooks rot. What it must not do is let the
exception escape: an operator gets the sentence, never a stack trace, so every startup
exception is caught and mapped, with exit code 1 as the catch-all.

**A port already in use is not reliably an error.** Verified 2026-08-12 on Windows: two
`highways` processes configured with the same `server.port` both started and both
logged `Listening on: 127.0.0.1:6577`, with no exception in either. Winsock lets the
second bind; only one of them then receives connections. So this distribution cannot
promise "a second instance refuses", and R5.6's two-brokers-on-one-machine story means
*two ports and two data directories* as a requirement, not a suggestion — the README
says so, and `--status` is how an operator checks what is already registered. Whether
`highways` should probe the port before `Build()` and refuse is an **open question**
for the packaging phase: a probe is racy and cannot be a guarantee, but it would catch
the copy-paste mistake, which is the one that actually happens.

---

## Distribution Layout and Build

```
highway-{version}-{rid}/
├── bin/
│   ├── highways(.exe)              self-contained publish output (OD3: exe + DLLs)
│   └── *.dll                       its dependencies, folder publish
├── config/
│   └── highway.json                the shipped default (loopback, dashboard on, ../data)
├── data/                           empty; the default dataDir target
├── logs/                           empty; script redirect target
├── scripts/                        what the README points at
│   ├── run.ps1                     foreground: bin/highways --config config/highway.json
│   ├── run.bat                     one-line wrapper for run.ps1 (double-clickable)
│   ├── run.sh                      the same, for Linux
│   ├── install-service.ps1         Windows: elevate → highways --install --start
│   ├── install-service.bat         one-line wrapper for the ps1
│   ├── uninstall-service.ps1
│   ├── highway.service             systemd unit (absolute paths templated at install)
│   ├── install-daemon.sh           Linux: user/paths → unit → daemon-reload → enable
│   └── uninstall-daemon.sh         the documented inverse
├── README.md                       unpack → run → install → configure → upgrade
├── LICENSE
└── THIRD-PARTY-NOTICES.md          Garnet (MIT), StackExchange.Redis (MIT)
```

### Packaging pipeline

One documented command per RID, wrapped by `scripts/package.ps1` (and a thin
`package.sh`), so a release is reproducible from a clean checkout:

```
dotnet publish src/Highway.Server.Host -c Release -r {rid} --self-contained \
    /p:Version={version} -o build/dist/highway-{version}-{rid}/bin
```

The script then lays out the folders above, stamps the README header with the version,
and produces `highway-{version}-{rid}.zip`. Determinism: fixed version property, no
timestamps embedded, script writes files with fixed content — a rebuild from the same
tag yields the same layout (byte-identical where the toolchain allows; the requirement
is *reproducible content*, not bit-for-bit compiler determinism).

Version comes from the repository's central version property — the same number the
assemblies carry, so `--version`, the folder, and the file metadata agree.

---

## Windows Service

Install (all P/Invoke, no `sc.exe` parsing):

1. Pre-check: elevated? (`OpenSCManager` with `SC_MANAGER_CREATE_SERVICE` — an
   access-denied here is exit code 4 with "run as Administrator", before anything
   changed).
2. `CreateServiceW` — binary path: `"{exePath}" --config "{absoluteConfPath}"`,
   quoted (spaces); `SERVICE_AUTO_START`; display name and description defaulted and
   overridable.
3. `ChangeServiceConfig2` — failure actions: restart after 5 s / 30 s / 60 s,
   reset period 24 h.
4. `--start` then `StartService` and wait for `SERVICE_RUNNING` with a timeout.

Uninstall: `ControlService(STOP)` → wait for `SERVICE_STOPPED` (bounded; a hung stop
is reported, not waited out forever) → `DeleteService`. Already-absent is success
with a note — every verb is safe to run twice.

The machine-wide service-stop timeout (default ~20–30 s, controlled by
`ServicesPipeTimeout`/`WaitToKillServiceTimeout`) cannot be set per service; the
README states the interaction with long AOF replays on slow disks, as 021 R6.2
requires of its sibling.

## Linux Daemon

`highway.service` ships readable; install pins the absolute paths:

```ini
[Unit]
Description=Highway Broker ({version})
After=network-online.target
Wants=network-online.target

[Service]
Type=notify
User={user}                      # 'highway' by default; script parameter
ExecStart={install}/bin/highways --config {install}/config/highway.json
WorkingDirectory={install}
Restart=on-failure
RestartSec=5
# Data lives outside the install directory in production layouts:
# see README — move config's dataDir to /var/lib/highway before first start.

[Install]
WantedBy=multi-user.target
```

`install-daemon.sh`: verify systemd exists (else exit 6 with a plain sentence),
create the user if asked, copy the unit with paths substituted, `daemon-reload`,
`enable --now`, print the unit path and the journald command
(`journalctl -u highway -f`). Uninstall is the documented inverse, and `--uninstall`
performs it for operators who prefer the verb.

## Containers — deliberately absent

There is no Dockerfile, no container `highway.json`, no compose file (D8). The two
container answers Highway gives are:

- **Embed the broker.** `HighwayServerBuilder` inside the application you already
  containerize. Nothing to package, nothing to run beside it — the SQLite shape.
- **Wrap the zip.** A shared broker in a cluster is `COPY` the unpacked folder into
  whatever base image that cluster standardises on and `ENTRYPOINT bin/highways --config
  config/highway.json`. Ten lines, written against their base image, their user id and
  their secret store rather than our guesses at all three.

The UserGuide says exactly this, so an operator looking for a Dockerfile finds the reason
instead of a gap. `bindAddress: "0.0.0.0"` and an `apiKey` from the environment are
already reachable from `highway.json` (R2), which is all a wrapped image needs.

---

## Error Handling Strategy

The host is a translation layer, and its errors come from three places with one rule
each:

1. **Configuration errors** (parse, unknown key, bad value, precedence conflict):
   thrown at load, message names `section.key` and the offending value; exit 2.
2. **Builder/server errors** (012 rule, cert load, data directory, port): the
   builder's existing exceptions surface verbatim at `Build()`/`Start()`; exit 3 for
   data-directory class, 1 otherwise. Their messages already name causes and ways
   out; the host does not paraphrase.
3. **Service-verb errors**: Win32/systemctl results mapped onto exit codes 4–6 with
   one human sentence each (021 R5's register: "run as Administrator", "no systemd
   here", never a raw code alone).

Logging keeps the server's structured output; the host adds exactly two lines of its
own at startup (config source + effective endpoint summary, secrets masked) and one
at shutdown.

---

## Testing Strategy

| Layer | What | How |
|---|---|---|
| Config loader | Precedence (file < env < CLI), unknown-key refusal, size/duration parsing, path resolution rules, masking, `authentication.enabled` semantics | Unit tests — pure function, no server |
| Host startup | Ephemeral broker from a temp `highway.json`; connect with SE.Redis; `--validate` discovers the same file run mode would and refuses what the builder refuses; no startup failure escapes as a stack trace; dashboard serves when enabled | Integration tests against real TCP (existing `EphemeralPort` pattern) |
| Packaging | Publish + assembly produce the layout; zip contains the required files; version stamp consistent | One CI-runnable test invoking the script at small scale |
| Service verbs | Windows: install/status/uninstall round-trip (requires elevation — gated, recorded); Linux: unit rendering correctness + script dry-run assertions | Where the machine allows; otherwise manual, RUNLOG-recorded — 021's precedent for platform-bound proof |
| Container | *nothing to test — D8: no image ships* | — |

The verb tests follow 021's honesty rule: a platform test that cannot run on the
machine says so in the RUNLOG rather than pretending to pass.
