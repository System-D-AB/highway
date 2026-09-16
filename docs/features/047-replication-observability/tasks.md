# Feature 047 — Tasks

### - [x] T1 — Feeder logging

**Fulfills:** R1.2, R1.3, R1.4
`ReplicationFeeder`: add `ILogger? Logger`, a `Note(transition, level)` helper routing the
transition tail to the log, convert the enqueue sites, and log replica-attached (in `Hello`, new
slot only) and slot-dropped (in `EnforceCap`). A unit test with a captured logger asserts a line on
promote/demote/fence/goodbye/slot-drop and none on a plain Hello/Ack.
**Done when:** the test passes; existing replication tests stay green.

### - [x] T2 — Replica-puller logging + wiring

**Fulfills:** R1.1
`ReplicaPuller`: optional `ILogger?`; log follow / connected / lost-connection / WAL-gap on
transitions (not per pull). `RocksDbStore.Open`: optional `ILogger?`; log the snapshot bootstrap.
`RespHighwayServer.Start`: create the `"Highway.Replication"` logger and pass it to `Open`, the
feeder, and the puller. Optional params keep `HighwayTestServer` unchanged.
**Done when:** the solution builds; a durable replica broker logs its connect and stream milestones.

### - [x] T3 — Dashboard replication tab + the record

**Fulfills:** R2
`replication.js`: render the roster; on a replica show its primary + lag rather than an empty slot
table. Distribution README dashboard note gains the loopback-bind caveat (`dashboard.bindAddress`
defaults to `127.0.0.1`; set `0.0.0.0` + `apiKey` to reach it remotely).
**Done when:** the tab is informative on both primary and replica; the note is in the README.
