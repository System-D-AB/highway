# Highway Assurance Rig

The Assurance Rig is an automated, multi-process correctness and reliability validation harness for Highway. It subjects real Highway brokers and multiple decoupled application processes to mixed workloads across structured temporal phases (Settle, Gap, Arrival, Steady, Turbulence, Drain, and Shutdown).

---

## What the Rig Proves

The rig proves the 7 foundational correctness invariants of Highway under real-world multi-process execution:

1. **I1 (Queue Completeness):** $sent \subseteq processed$. Every message sent into a queue is eventually processed. No messages are silently dropped or lost, even during ungraceful process crashes and lease recovery.
2. **I2 (No Phantoms):** $processed \subseteq sent$. No worker processes phantom messages that were never sent by any producer.
3. **I3 (RPC Never Silent):** $executed \subseteq replied \cup timed\_out$. Every RPC request initiated receives an explicit reply or a bounded timeout.
4. **I4 (Pub/Sub Per Live Group):** Every published event is delivered to each subscription group registered and live at the time of publish.
5. **I5 (Duplicates Counted):** Redeliveries under node crash recovery are counted and bounded under Highway's at-least-once guarantee ($C1.1$).
6. **I6 (Dead Letters):** Dead letter queue states are verified and attributed.
7. **I7 (Nothing Left Behind):** Final broker queue and channel backlog depths reach zero upon workload drain.

---

## What the Rig Deliberately Does NOT Prove

Per constraint **C5**, the Assurance Rig is **not a benchmark** and makes **no throughput claims**. The load generator applies a fixed rate target (50–100 msg/s aggregate) specifically chosen to exercise concurrent worker pools, queue accumulation in the gap phase, and lease sweep mechanics during turbulence. Achieved rates are recorded for diagnostic context only.

---

## Running the Rig

### Standard Soak Run (4 minutes)
```powershell
dotnet run --project assurance/Highway.Assurance.Runner/Highway.Assurance.Runner.csproj -- --profile standard
```

### Shortened CI Integration Run (< 60 seconds)
```powershell
dotnet test tests/Highway.Integration.Tests/Highway.Integration.Tests.csproj --filter FullyQualifiedName~AssuranceRigShortenedTests
```

---

## Run Artifact Layout

Each execution creates an immutable snapshot under `assurance/runs/{timestamp}/`:

```
assurance/runs/{timestamp}/
├── config/
│   ├── highway.json          # Generated broker configuration (lease, recorder, data dir)
│   ├── profile.json          # Timeline phase durations and rate targets
│   └── current_phase.txt     # Dynamic phase coordination signal
├── ledgers/
│   ├── edge-1.jsonl          # Load generator event ledger
│   ├── accounts-1.jsonl      # RPC & account worker event ledger
│   ├── notifications-subs-1.jsonl # Subscriber worker event ledger
│   ├── mailer-1.jsonl        # Primary mailer worker event ledger
│   └── mailer-2.jsonl        # Secondary mailer worker event ledger
├── processes/
│   ├── *.stdout.log          # Standard out and standard err logs per process
│   └── resources.json        # Peak working set memory and process exit codes
├── broker/
│   ├── stats-samples.jsonl   # Periodic HW.STATS samples throughout the run
│   └── recorder-replay.jsonl # HW.RECORDER dump captured on shutdown
├── versions.json             # Git SHA, .NET runtime, and OS environment
├── report.md                 # Formatted markdown reconciliation report
└── report.json               # Machine-readable invariant evaluation output
```
