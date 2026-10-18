# Highway Server (`highways`)

Highway is a high-throughput, low-latency message broker and stream store built as an extension to Microsoft Garnet.

---

## 1. Quickstart (Zero-Configuration First Run)

1. Unpack the distribution archive (`highway-{version}-win-x64.zip`).
2. Double-click `scripts\run.bat` (or execute `scripts\run.ps1` in PowerShell).
3. The broker starts immediately with code defaults:
   - **Broker RESP listener:** `127.0.0.1:6500`
   - **Replication listener:** `127.0.0.1:6501` (the RESP port + 1), opened only once the broker is in a
     replica set
   - **Dashboard & flight recorder:** `http://127.0.0.1:7500`
   - **Durable data store:** `./data` (beside the distribution) — a SystemD.Crystal store: leave it to
     the broker, and keep nothing else in it
4. Press `Ctrl+C` in the console window to stop cleanly.

> **Reaching the dashboard from another machine.** Both the broker (`server.bindAddress`) and the
> dashboard (`dashboard.bindAddress`) default to **loopback (`127.0.0.1`)**, so from a remote browser
> the dashboard simply won't load — open it *on the server* at `http://localhost:7500`, or set
> `dashboard.bindAddress` to `0.0.0.0` **and** a `dashboard.apiKey` (an exposed dashboard with no key
> is an open window into the broker). These are separate listeners: exposing the broker for
> replication does not expose the dashboard.
>
> **Replica sets use two ports per node.** Clients and the replica set's control traffic (hello,
> join, handshake) use the RESP port; the data itself moves over the **replication port**
> (`server.replication.replicationListen`, default the RESP port + 1, opened when a standby first says
> hello or the node is promoted; set it explicitly to open it at start), authenticated by the replica
> set's shared secret — `server.replication.replicationSecret`, or the broker password when unset — and
> over TLS with the broker's own certificate when the broker serves TLS (a self-signed certificate the
> nodes share is trusted as itself). Open both between the nodes. Set `server.replication.replicationAdvertiseEndpoint` when peers must dial an
> address other than the node's own host.

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

### Health & Readiness Endpoints (load balancers & orchestrators)

The broker serves three HTTP routes on the dashboard host (same port — no extra listener), **on by
default** even when the dashboard UI is off (`dashboard.healthEndpoints`, default `true`):

| Route | Purpose | Auth | Meaning |
|---|---|---|---|
| `GET /health` | **Liveness** | keyless | `200` whenever the process is up, *regardless of role* — restart a wedged process on failure. A healthy replica is live. |
| `GET /ready` | **Readiness** | keyless | `200` **only** when this node can serve client writes now (writable primary, not fenced/draining/bootstrapping); otherwise `503` with a one-word reason (`replica`, `fenced`, `draining`, `bootstrapping`, `demoted`). |
| `GET /replication` | Replication detail (JSON) | API key | role, epoch, per-replica lag, roster and the degraded flags — the `HW.REPL.STATUS` data over HTTP. |

**Point client traffic at `/ready`, not `/health`.** Readiness flips within a probe interval when a
node is demoted, fenced or drained (GOODBYE), so the load balancer pulls a stepped-down node out of
rotation automatically — clients never hit a node that would only answer `-NOTPRIMARY`.

Example — NGINX upstream health check against a replica set:

```nginx
upstream highway {
    server 10.0.0.1:6500;   # each node's RESP port
    server 10.0.0.2:6500;
}
# Probe /ready on each node's dashboard/health port (default 7500); only the writable
# primary answers 200, so traffic follows the current master automatically.
# health_check uri=/ready port=7500 interval=2s;   (NGINX Plus)
```

Kubernetes:

```yaml
livenessProbe:  { httpGet: { path: /health, port: 7500 } }
readinessProbe: { httpGet: { path: /ready,  port: 7500 } }
```

**Loopback caveat.** Like the dashboard, the health host binds to `dashboard.bindAddress`
(**`127.0.0.1`** by default), so an external probe cannot reach it until you set
`dashboard.bindAddress` to `0.0.0.0`. `/health` and `/ready` stay keyless so probes work without a
secret; set `dashboard.apiKey` to protect `/replication` (and the UI) once exposed. To serve health
without the dashboard UI, set `dashboard.enabled: false` and keep `dashboard.healthEndpoints: true`.

---

### Offline Compaction (`--compact`)

**Not available in 3.0.** The command remains for 2.x data directories, which needed it once after
2.5.3 or earlier (tombstones on busy queues that every poll stepped over). A 3.0 broker starts every
data directory fresh, its storage engine (SystemD.Crystal) compacts delete-heavy data on its own, and it
offers no offline compaction yet, so `--compact` is refused and changes nothing. The metric
`highway.storage.memtable_deletes` still shows the engine's pending deletes.

### Emptying the Data Directory (`--wipe-data`)

Deletes everything the broker stores — every queued, in-flight, delayed and dead-lettered message, the
registry and the job schedules — and keeps only the node's replication identity (its node id and
epoch). It is the step between draining on Highway 2.x and starting 3.0 (see runbook 7); a directory an
earlier engine wrote (2.x, or a 3.0 pre-release) is emptied entirely. It needs the broker stopped, and
refuses while a running broker holds the directory.

```cmd
bin\highways.exe --wipe-data --config config\highway.json
```

---

## 5. Runbook: Data-Directory Rotation (Drain-Then-Rotate)

When upgrading across incompatible storage format versions or performing store maintenance, use the **Drain-Then-Rotate** procedure to ensure zero data loss.

### When to Use
- Startup fails with: `Highway's data directory was written in storage format X, but this build reads format Y`
  (for 2.x → 3.0, follow runbook 7 instead).
- Performing scheduled log rotation.

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

---

## 7. Runbook: Upgrading from 2.x to 3.0 (Fresh Start)

Highway 3.0 changes both the wire protocol (5.0) and the storage format (4), and **does not migrate data
in place**. A 3.0 broker refuses a 2.x data directory rather than serving it as empty, a 2.x client
cannot talk to a 3.0 broker, and a 3.0 node never replicates from a 2.x one (or the reverse). So the
upgrade is a planned short stop: empty the broker on 2.x, start 3.0 on an empty directory, and bring
the applications back on the 3.0 client.

### 1. Drain on 2.x
1. Stop or pause everything that **sends** (queue sends, publishes, RPC callers).
2. Keep the workers and subscribers running until the dashboard (or `HW.STATS`) shows every queue,
   group queue and service at **0 waiting, 0 in flight and 0 delayed**. Delayed messages wait for their
   due time — decide whether to wait or accept losing them.
3. Dead letters are **not** carried over. Re-drive or export anything in a dead-letter queue you want
   to keep.
4. Stop the Highway applications. Recurring jobs and subscriptions need no action — every 3.0 client
   re-declares its own at start.

### 2. Upgrade the broker(s) — standby first
In a replica set, do the standby, then the primary. Each node keeps its role and configuration.

On the **standby**:
```cmd
bin\highways.exe --stop
:: swap the bin\ files for 3.0
bin\highways.exe --wipe-data --config config\highway.json
```
Leave it stopped.

On the **primary** (or the single broker):
```cmd
bin\highways.exe --stop
:: swap the bin\ files for 3.0
bin\highways.exe --wipe-data --config config\highway.json
bin\highways.exe --start
```

Then start the standby: `bin\highways.exe --start`. Its data directory is empty, so it bootstraps
from the primary's snapshot and follows it; the primary's **Replication** tab shows it **Active**.

### 3. Bring the applications back
Deploy the applications built against the 3.0 client packages and start them, then resume the
senders. Each node establishes its session (services, subscriptions, jobs) on connect.

### What can go wrong, and what you will see
- **A 3.0 broker started on an undrained 2.x directory** refuses to start and names the directory and
  the `--wipe-data` step. Nothing is lost: start 2.x on it again to drain it.
- **Mixed versions in a replica set** do not replicate: a 3.0 primary refuses a 2.x standby with
  `HW_STORAGE_FORMAT`, and a 3.0 standby logs that its primary stores format 3 and does not pull. Finish
  upgrading the other node.
- **A 2.x application against a 3.0 broker** gets `HW_INVALID_ARG unknown command` errors for the
  renamed 4.x verbs. Upgrade the application's Highway packages.
