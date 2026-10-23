# Highway Server (`highways`)

Highway is a high-throughput, low-latency message broker and stream store built as an extension to Microsoft Garnet.

---

## 1. Quickstart (Zero-Configuration First Run)

1. Unpack the distribution archive (`highway-{version}-win-x64.zip`) into your Highway root — for example
   `C:\highway`. It creates `server\{line}\`, this version line's own directory (see
   [Directory Layout](#2-directory-layout)); every path below is inside it.
2. In `server\{line}\`, double-click `scripts\run.bat` (or execute `scripts\run.ps1` in PowerShell).
3. The broker starts immediately with code defaults:
   - **Broker RESP listener:** `127.0.0.1:6500`
   - **Replication listener:** `127.0.0.1:6501` (the RESP port + 1), opened only once the broker is in a
     replica set
   - **Dashboard & flight recorder:** `http://127.0.0.1:7500`
   - **Durable data store:** `server\{line}\data` — a SystemD.Crystal store: leave it to the broker, and
     keep nothing else in it
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

Each **major version line** has its own directory under `server\`, with its own binaries, configuration, data and
logs. The data format never changes within a major version, so every `{line}.x` release upgrades in place in
`server\{line}\`; a new major installs **beside** it, on new data, and the old line stays untouched until you remove
it — which is what makes a major upgrade reversible (see [runbook 7](#7-runbook-upgrading-from-2x-to-3-side-by-side)).

```
C:\highway\                     Your Highway root (any directory; /opt/highway on Linux)
└── server\
    ├── 2.5\                    An older line, if one was installed this way — left as it was
    └── {line}\                 This line — everything below
        ├── bin\
        │   ├── highways.exe    Self-contained broker host
        │   └── *.dll           Runtime dependencies
        ├── config\
        │   └── highway.json    Server and dashboard configuration (dataDir "../data")
        ├── data\               This line's SystemD.Crystal store
        ├── logs\               Rolling daily broker logs (highway-YYYYMMDD.log)
        ├── scripts\
        │   ├── run.bat, run.ps1                Foreground runners
        │   ├── install-service.bat, .ps1       Installs the Highway service from this line
        │   └── uninstall-service.ps1           Removes the Highway service
        ├── LINE                The line's name ({line}), read by the service scripts
        ├── README.md           This operational guide
        ├── LICENSE             Highway MIT License
        └── THIRD-PARTY-NOTICES.md
```

Two lines are installed side by side but **never run at once**: there is one `Highway` service, and installing a
line points it at that line (section 3). On Linux the same tree sits under `/opt/highway/server/{line}/`, with
`run.sh`, `install-service.sh`, `uninstall-service.sh` and the `highway.service` unit template.

---

## 3. Production Windows Service

To install Highway as an automatic Windows Service with crash recovery:

### Installation
From the line's directory, run `scripts\install-service.bat` (it requests Administrator elevation). It installs and
starts the **`Highway`** service from this line. If a `Highway` service already exists and runs **another line**, the
script stops it, removes it and says which line it replaced — that line's data and configuration are left untouched,
so running *its* `install-service` again goes back. It refuses to start while the broker or dashboard port is still
held (a broker started by hand), naming the process. On Linux, `sudo bash scripts/install-service.sh` does the same
for the `highway` unit.

The bare equivalent, without the line handling:
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

Compacts every database of a **stopped** broker's data directory, dropping every tombstone; it changes
no data. It is rarely needed: the storage engine (SystemD.Crystal) compacts delete-heavy data on its
own while the broker runs. Use it to reclaim scan cost after an unusual burst of deletes. It is refused,
and changes nothing, while a broker holds the directory or for a directory that is not a Highway data
directory:
```cmd
bin\highways.exe --compact --config config\highway.json
```
The metric `highway.storage.memtable_deletes` shows the engine's pending deletes.

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

## 7. Runbook: Upgrading from 2.x to 3 (Side by Side)

Highway 3 changes both the wire protocol (5.0) and the storage format (4), and **does not migrate data in place**. A
3.x broker refuses a 2.x data directory rather than serving it as empty, a 2.x client cannot talk to a 3.x broker,
and a 3.x node never replicates from a 2.x one (or the reverse). So the upgrade is a planned short stop: empty the
broker on 2.x, start line 3 **in its own directory** on empty data, and bring the applications back on the 3.x
client. The 2.x installation and its data are not touched, so the way back stays open.

### 1. Drain on 2.x
1. Stop or pause everything that **sends** (queue sends, publishes, RPC callers).
2. Keep the workers and subscribers running until the dashboard (or `HW.STATS`) shows every queue,
   group queue and service at **0 waiting, 0 in flight and 0 delayed**. Delayed messages wait for their
   due time — decide whether to wait or accept losing them.
3. Dead letters are **not** carried over. Re-drive or export anything in a dead-letter queue you want
   to keep.
4. Stop the Highway applications. Recurring jobs and subscriptions need no action — every 3.x client
   re-declares its own at start.

### 2. Install line 3 beside 2.x — standby first
On each node, unpack the 3.x archive into the Highway root (it creates `server\3\`) and copy your settings into
`server\3\config\highway.json` — ports, bind addresses, password, TLS, replication (`primaryServer`, `replicaId`,
`priority`). Open the **replication port** (RESP + 1) between the nodes: 3.x replicates over it.

In a replica set, do the standby, then the primary. Each node keeps its role.

On the **standby**: stop its 2.x broker (`bin\highways.exe --stop` in the 2.x directory). Leave it stopped.

On the **primary** (or the single broker): stop its 2.x broker, then from `server\3\` run
`scripts\install-service.bat`. The `Highway` service now runs line 3, on empty data.

Then on the standby, from `server\3\`, run `scripts\install-service.bat`. Its data directory is empty, so it
bootstraps from the primary's snapshot and follows it; the primary's **Replication** tab shows it **Active**.

> **An existing flat install** (2.x, or a 3.0 pre-release unpacked straight into the Highway root, with `bin\` at the
> top) works the same way: it is the old line. Stop it, install line 3 under `server\3\` as above, and delete the
> flat directories once line 3 is trusted.

### 3. Bring the applications back
Deploy the applications built against the 3.0 client packages and start them, then resume the
senders. Each node establishes its session (services, subscriptions, jobs) on connect.

### Rolling back
Until you remove the old line, going back is one step per node, primary first: drain line 3 as in step 1, then run
the **old line's** `scripts\install-service.bat` (or start it as you did before). Its data directory is exactly as you
left it; messages sent while line 3 ran stay in line 3's data, so drain line 3 first.

### Removing the old line
Once line 3 is trusted, delete the old line's directory (`server\2.5\`, or the flat 2.x install) — data, logs and
all. Nothing in line 3 refers to it.

### What can go wrong, and what you will see
- **A 3.x broker pointed at a 2.x data directory** (a copied config with an old `dataDir`) refuses to start and names
  the directory. Nothing is lost: point `dataDir` back at `../data`.
- **The install script refuses because a port is in use.** A broker from the old line is still running by hand;
  stop it.
- **Mixed versions in a replica set** do not replicate: a 3.0 primary refuses a 2.x standby with
  `HW_STORAGE_FORMAT`, and a 3.0 standby logs that its primary stores format 3 and does not pull. Finish
  upgrading the other node.
- **A 2.x application against a 3.0 broker** gets `HW_INVALID_ARG unknown command` errors for the
  renamed 4.x verbs. Upgrade the application's Highway packages.
