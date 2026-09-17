# Highway Server (`highways`)

Highway is a high-throughput, low-latency message broker and stream store built as an extension to Microsoft Garnet.

---

## 1. Quickstart (Zero-Configuration First Run)

1. Unpack the distribution archive (`highway-{version}-win-x64.zip`).
2. Double-click `scripts\run.bat` (or execute `scripts\run.ps1` in PowerShell).
3. The broker starts immediately with code defaults:
   - **Broker RESP listener:** `127.0.0.1:6500`
   - **Dashboard & flight recorder:** `http://127.0.0.1:7500`
   - **Durable data store:** `./data` (beside the distribution)
4. Press `Ctrl+C` in the console window to stop cleanly.

> **Reaching the dashboard from another machine.** Both the broker (`server.bindAddress`) and the
> dashboard (`dashboard.bindAddress`) default to **loopback (`127.0.0.1`)**, so from a remote browser
> the dashboard simply won't load — open it *on the server* at `http://localhost:7500`, or set
> `dashboard.bindAddress` to `0.0.0.0` **and** a `dashboard.apiKey` (an exposed dashboard with no key
> is an open window into the broker). These are two separate listeners: exposing the broker for
> replication does not expose the dashboard.

---

## 2. Directory Layout

```
highway-{version}-win-x64/
├── bin/
│   ├── highways.exe              Self-contained broker host
│   └── *.dll                     Runtime dependencies
├── config/
│   └── highway.json              Server and dashboard configuration
├── data/                         Persistent storage (checkpoints and AOF)
├── logs/                         Rolling daily broker logs (highway-YYYYMMDD.log)
├── scripts/
│   ├── run.bat                   Double-clickable foreground runner
│   ├── run.ps1                   PowerShell foreground runner
│   ├── install-service.bat       Double-clickable Windows service installer (elevated)
│   ├── install-service.ps1       PowerShell Windows service installer
│   └── uninstall-service.ps1     PowerShell Windows service uninstaller
├── README.md                     This operational guide
├── LICENSE                       Highway MIT License
└── THIRD-PARTY-NOTICES.md        Third-party open-source attributions
```

---

## 3. Production Windows Service

To install Highway as an automatic Windows Service with crash recovery:

### Installation
Run `scripts\install-service.bat` (will request Administrator elevation) or run:
```powershell
bin\highways.exe --install --start --config config\highway.json
```

**Service Characteristics:**
- **Startup:** Automatic (starts on system boot).
- **Failure Policy:** Restarts after 5 seconds on 1st crash, 30 seconds on 2nd crash, 60 seconds on 3rd crash (reset period: 24 hours).
- **Shutdown:** Stops gracefully, flushing the storage engine's write-ahead log and checkpoint.

### Service Management Verbs
```cmd
bin\highways.exe --status               # Query service status (Running / Stopped)
bin\highways.exe --stop                 # Stop the service cleanly
bin\highways.exe --start                # Start the service
bin\highways.exe --uninstall            # Stop and remove the service
```

### Logs
The broker writes rolling daily log files to the `logs/` folder — `logs\highway-YYYYMMDD.log` —
whether it runs interactively or as a Windows service. (A service has no console, so these files
are the place to look.) Files roll daily and on size, and old files are pruned automatically. The
newest, live one:
```powershell
Get-Content -Path .\logs\highway-*.log -Tail 50 -Wait
```

### Multiple Instances on One Machine
To run multiple brokers on the same host, ensure each instance uses distinct settings:
```powershell
bin\highways.exe --install --start --service-name "Highway-6501" --service-display "Highway Server (Instance 2)" --config config\highway-6501.json
```
*(Ensure `server.port`, `dashboard.port`, and `server.dataDir` in `highway-6501.json` are unique).*

---

## 4. Configuration & Validation

Edit `config\highway.json` to change ports, data directories, TLS certificates, or authentication.

### Validation
To verify configuration validity and view effective settings (with secrets masked) without starting the server:
```cmd
bin\highways.exe --validate --config config\highway.json
```

### Environment Overrides
All configuration values can be overridden via environment variables using `HIGHWAY_<SECTION>_<KEY>`:
- `HIGHWAY_PASSWORD=YourStrongPassword`
- `HIGHWAY_SERVER_PORT=6600`
- `HIGHWAY_DASHBOARD_APIKEY=secret-token`

---

## 5. Runbook: Data-Directory Rotation (Drain-Then-Rotate)

When upgrading across incompatible storage format versions or performing store maintenance, use the **Drain-Then-Rotate** procedure to ensure zero data loss.

### When to Use
- Startup fails with: `Highway's data directory was written in storage format X, but this build reads format Y`.
- Performing scheduled log rotation / storage compaction.

### Step-by-Step Procedure

1. **Pause Ingestion / Producers**:
   - Pause or redirect client producers sending new messages to Highway queues.
2. **Drain Active Queues**:
   - Keep consumer workers running against the existing broker version until all queues and unacknowledged messages reach `0`.
   - Verify empty state in Dashboard (`http://127.0.0.1:7500`) or via `HW.STATS`.
3. **Stop the Broker**:
   - Stop the running service cleanly:
     ```cmd
     bin\highways.exe --stop
     ```
4. **Rotate & Archive Data**:
   - Rename the existing `data/` directory to an archive name:
     ```cmd
     ren data data-archive-%DATE%
     mkdir data
     ```
5. **Start the New Broker**:
   - Start the updated Highway service with the fresh, empty `data/` directory:
     ```cmd
     bin\highways.exe --start
     ```
6. **Resume Producers**:
   - Resume client producer traffic.

---

## 6. Runbook: Rolling Upgrade of a Replica Set (Zero-Downtime)

Upgrading the binaries of a two-node replica set **without an unplanned failover**. A bare restart of
the primary looks like a failure to the client herd and forces an ungraceful failover; this procedure
makes every hand-off deliberate and lossless. It relies on **auto-rejoin** (on by default,
`server.replication.autoRejoin`): a demoted node re-syncs as a replica of the new primary on its next
start, so you never reconfigure or wipe by hand.

**Order: upgrade the replica(s) first, the primary last.**

### 1. Upgrade each replica
On the replica node:
```cmd
bin\highways.exe --stop
:: swap the bin\ files for the new version
bin\highways.exe --start
```
The replica reconnects and catches up (lag → 0). Confirm on the primary's dashboard **Replication** tab
that the standby reads **Active** again before continuing.

### 2. Hand the primary off, then upgrade it
On the primary node, one command drains and stops it:
```cmd
bin\highways.exe --drain-and-stop
```
This issues `HW.REPL.GOODBYE` (the herd moves to the highest-priority standby with zero client loss),
waits for the drain to complete, then stops the service. Swap the binaries and start it:
```cmd
:: swap the bin\ files for the new version
bin\highways.exe --start
```
On start it **auto-rejoins as a replica** of the node that took over — the cluster is a healthy pair
again, with no reconfigure and no wipe.

### 3. (Optional) Return mastership to the preferred node
There is **no automatic failback**. If you want the original node to be primary again, hand it back
deliberately once it is caught up (Active on the dashboard):
```cmd
:: on the current primary
bin\highways.exe --goodbye
```
The herd converges back onto the higher-priority node.

### What you should see
- The dashboard **leadership banner** naming the current primary and epoch, updating at each hand-off.
- No **"No redundancy"** alert once both nodes are attached.
- Zero client errors: clients follow each `-NOTPRIMARY` redirect automatically. **List every broker
  endpoint in the client connection string** — a single-endpoint client has no failover, and the
  client warns at startup when it detects this.
