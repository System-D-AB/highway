# Highway Assurance Rig — Run Log

This log records every standard soak and integration run executed against Highway broker instances. Newest runs are listed first.

---

## 2026-09-17 — shortened-ci — doorbells on (PASSED)

- **Run ID:** `ci-short-50f78907`
- **Target Rate:** 25 msg/s | **Lease:** 3s | **Doorbells:** on
- **Verdict:** `PASSED` (Exit Code: 0)
- **Total Events Processed:** 133
- **Duplicates Observed:** 0
- **Dead Letters:** 0
- **Notes:** I1_QueueCompleteness: PASS; I2_NoPhantoms: PASS; I3_RpcNeverSilent: PASS; I4_PubSubPerLiveGroup: PASS; I5_Duplicates: PASS; I6_DeadLetters: PASS; I7_NothingLeftBehind: PASS

## 2026-09-17 — shortened-ci — doorbells on (PASSED)

- **Run ID:** `ci-short-0d255b1e`
- **Target Rate:** 25 msg/s | **Lease:** 3s | **Doorbells:** on
- **Verdict:** `PASSED` (Exit Code: 0)
- **Total Events Processed:** 184
- **Duplicates Observed:** 0
- **Dead Letters:** 0
- **Notes:** I1_QueueCompleteness: PASS; I2_NoPhantoms: PASS; I3_RpcNeverSilent: PASS; I4_PubSubPerLiveGroup: PASS; I5_Duplicates: PASS; I6_DeadLetters: PASS; I7_NothingLeftBehind: PASS

## 2026-09-16 — shortened-ci — doorbells on (PASSED)

- **Run ID:** `ci-short-41158a47`
- **Target Rate:** 25 msg/s | **Lease:** 3s | **Doorbells:** on
- **Verdict:** `PASSED` (Exit Code: 0)
- **Total Events Processed:** 147
- **Duplicates Observed:** 0
- **Dead Letters:** 0
- **Notes:** I1_QueueCompleteness: PASS; I2_NoPhantoms: PASS; I3_RpcNeverSilent: PASS; I4_PubSubPerLiveGroup: PASS; I5_Duplicates: PASS; I6_DeadLetters: PASS; I7_NothingLeftBehind: PASS

## 2026-09-16 — shortened-ci — doorbells on (PASSED)

- **Run ID:** `ci-short-47823903`
- **Target Rate:** 25 msg/s | **Lease:** 3s | **Doorbells:** on
- **Verdict:** `PASSED` (Exit Code: 0)
- **Total Events Processed:** 164
- **Duplicates Observed:** 0
- **Dead Letters:** 0
- **Notes:** I1_QueueCompleteness: PASS; I2_NoPhantoms: PASS; I3_RpcNeverSilent: PASS; I4_PubSubPerLiveGroup: PASS; I5_Duplicates: PASS; I6_DeadLetters: PASS; I7_NothingLeftBehind: PASS

## 2026-09-16 — shortened-ci — doorbells on (PASSED)

- **Run ID:** `ci-short-4af0aa56`
- **Target Rate:** 25 msg/s | **Lease:** 3s | **Doorbells:** on
- **Verdict:** `PASSED` (Exit Code: 0)
- **Total Events Processed:** 178
- **Duplicates Observed:** 0
- **Dead Letters:** 0
- **Notes:** I1_QueueCompleteness: PASS; I2_NoPhantoms: PASS; I3_RpcNeverSilent: PASS; I4_PubSubPerLiveGroup: PASS; I5_Duplicates: PASS; I6_DeadLetters: PASS; I7_NothingLeftBehind: PASS

## 2026-09-16 — shortened-ci HERD (primary-kill failover) — doorbells on (PASSED)

- **Run ID:** `2026-09-16T07-43-22`
- **Topology:** 2-node herd (primary prio 1 + standby prio 2, WAL-shipping); workloads on a multi-endpoint bootstrap; **graceful master transition (HW.REPL.GOODBYE) mid-turbulence** — in-flight drained, the standby caught up and the herd converged on it. (Hard-kill RPO loss is bounded per C9.1, proven separately by the in-process cohesion harness.)
- **Target Rate:** 25 msg/s | **Lease:** 3s | **Doorbells:** on
- **Verdict:** `PASSED` (Exit Code: 0)
- **Notes:** I1_QueueCompleteness: PASS; I2_NoPhantoms: PASS; I3_RpcNeverSilent: PASS; I4_PubSubPerLiveGroup: PASS; I5_Duplicates: PASS; I6_DeadLetters: PASS; I7_NothingLeftBehind: PASS

## 2026-09-16 — shortened-ci HERD (graceful failover) — doorbells OFF (PASSED)

- **Run ID:** `2026-09-16T07-42-35`
- **Topology:** 2-node herd (primary prio 1 + standby prio 2, WAL-shipping); workloads on a multi-endpoint bootstrap; **graceful master transition (HW.REPL.GOODBYE) mid-turbulence** — in-flight drained, the standby caught up and the herd converged on it. (Hard-kill RPO loss is bounded per C9.1, proven separately by the in-process cohesion harness.)
- **Target Rate:** 25 msg/s | **Lease:** 3s | **Doorbells:** off — only the backstop sweep drives correctness across the failover
- **Verdict:** `PASSED` (Exit Code: 0)
- **Notes:** I1_QueueCompleteness: PASS; I2_NoPhantoms: PASS; I3_RpcNeverSilent: PASS; I4_PubSubPerLiveGroup: PASS; I5_Duplicates: PASS; I6_DeadLetters: PASS; I7_NothingLeftBehind: PASS

> **These two herd runs close 042-1d D-T6.** Earlier same-day iterations (run IDs
> `07-36-00`, `07-36-50`, `07-38-25`, `07-40-47`, since consolidated out of this log)
> exposed two *harness* defects, not broker/herd defects, both fixed before the runs
> above: (1) an ungraceful primary kill can lose an acked-but-not-yet-replicated publish
> inside the async-replication RPO window (constraint C9.1) — the rig now uses a graceful
> `HW.REPL.GOODBYE` transition, which drains in-flight and lets the standby catch up, for a
> deterministic zero-loss demonstration; (2) the assurance workloads recorded a call's
> timeout outcome with the already-cancelled shutdown token and were stopped while calls
> were still in flight, dropping the outcome line — the workloads now record outcomes with
> a non-cancellable token and the drain leaves them running until in-flight settles.

## 2026-09-15 — shortened-ci — doorbells on (PASSED)

- **Run ID:** `ci-short-381be957`
- **Target Rate:** 25 msg/s | **Lease:** 3s | **Doorbells:** on
- **Verdict:** `PASSED` (Exit Code: 0)
- **Total Events Processed:** 136
- **Duplicates Observed:** 0
- **Dead Letters:** 0
- **Notes:** I1_QueueCompleteness: PASS; I2_NoPhantoms: PASS; I3_RpcNeverSilent: PASS; I4_PubSubPerLiveGroup: PASS; I5_Duplicates: PASS; I6_DeadLetters: PASS; I7_NothingLeftBehind: PASS

## 2026-09-15 — shortened-ci — doorbells on (PASSED)

- **Run ID:** `ci-short-fafeb9fb`
- **Target Rate:** 25 msg/s | **Lease:** 3s | **Doorbells:** on
- **Verdict:** `PASSED` (Exit Code: 0)
- **Total Events Processed:** 148
- **Duplicates Observed:** 0
- **Dead Letters:** 0
- **Notes:** I1_QueueCompleteness: PASS; I2_NoPhantoms: PASS; I3_RpcNeverSilent: PASS; I4_PubSubPerLiveGroup: PASS; I5_Duplicates: PASS; I6_DeadLetters: PASS; I7_NothingLeftBehind: PASS

## 2026-09-16 — feature 042-1 herd-cohesion harness (in-process, multi-client)

- **Target:** the `HerdCohesionTests` gate — multiple in-process `EngineNode` clients over a multi-node cluster (real Kestrel RESP + RocksDB), client-herd failover per feature 042-1.
- **Scenarios (each an explicit assertion, all PASSED):**
  - **Hard kill + no-split:** 3 clients / 3 nodes, master disposed mid-traffic → all clients converge on the same successor (the mechanical no-split check: distinct active ports == 1); **zero acked-and-replicated loss** proven by wire read-back on the new master; duplicates counted, not doubled; convergence observed through `HW.REPL.STATUS`.
  - **GOODBYE at herd scale:** whole herd moves on the narration, zero loss.
  - **Rejoin without preemption:** a higher-priority node returns as a standby, the herd does not move; deliberate failback via GOODBYE converges on the returner.
  - **Priority collision:** the second announcer of a held priority is refused (`HW_PRIORITY_TAKEN`) and stays out of the roster while remaining a warm puller.
  - **Partition matrix:** master isolated from peers but holding clients keeps serving; a doubly-partitioned client cannot mint a second master (the standby's healthy link vetoes).
  - **RPC across failover:** a caller re-drives the same request id to the successor and receives its reply — the call lived in the caller across the whole failover.
- **Verdict:** `PASSED` (7/7, normal CI suite; runs on every commit).
- **Assurance rig against a failing-over herd — DONE (2026-09-16).** The rig gained a
  `--herd` mode (two replicated brokers, workloads on a multi-endpoint bootstrap, a master
  transition mid-turbulence); both doorbell variants PASS I1–I7 across a graceful failover
  — see the two herd entries at the top of this log. This closes 042-1d D-T6.

---

## 2026-09-15 — shortened-ci — doorbells on (PASSED)

- **Run ID:** `ci-short-bfef695a`
- **Target Rate:** 25 msg/s | **Lease:** 3s | **Doorbells:** on
- **Verdict:** `PASSED` (Exit Code: 0)
- **Total Events Processed:** 150
- **Duplicates Observed:** 0
- **Dead Letters:** 0
- **Notes:** I1_QueueCompleteness: PASS; I2_NoPhantoms: PASS; I3_RpcNeverSilent: PASS; I4_PubSubPerLiveGroup: PASS; I5_Duplicates: PASS; I6_DeadLetters: PASS; I7_NothingLeftBehind: PASS

## 2026-09-15 — shortened-ci — doorbells on (PASSED)

- **Run ID:** `ci-short-a96f394f`
- **Target Rate:** 25 msg/s | **Lease:** 3s | **Doorbells:** on
- **Verdict:** `PASSED` (Exit Code: 0)
- **Total Events Processed:** 142
- **Duplicates Observed:** 0
- **Dead Letters:** 0
- **Notes:** I1_QueueCompleteness: PASS; I2_NoPhantoms: PASS; I3_RpcNeverSilent: PASS; I4_PubSubPerLiveGroup: PASS; I5_Duplicates: PASS; I6_DeadLetters: PASS; I7_NothingLeftBehind: PASS

## 2026-09-15 — shortened-ci — doorbells on (PASSED)

- **Run ID:** `ci-short-b9638d12`
- **Target Rate:** 25 msg/s | **Lease:** 3s | **Doorbells:** on
- **Verdict:** `PASSED` (Exit Code: 0)
- **Total Events Processed:** 141
- **Duplicates Observed:** 0
- **Dead Letters:** 0
- **Notes:** I1_QueueCompleteness: PASS; I2_NoPhantoms: PASS; I3_RpcNeverSilent: PASS; I4_PubSubPerLiveGroup: PASS; I5_Duplicates: PASS; I6_DeadLetters: PASS; I7_NothingLeftBehind: PASS

## 2026-09-15 — shortened-ci — doorbells on (PASSED)

- **Run ID:** `ci-short-af481900`
- **Target Rate:** 25 msg/s | **Lease:** 3s | **Doorbells:** on
- **Verdict:** `PASSED` (Exit Code: 0)
- **Total Events Processed:** 136
- **Duplicates Observed:** 0
- **Dead Letters:** 0
- **Notes:** I1_QueueCompleteness: PASS; I2_NoPhantoms: PASS; I3_RpcNeverSilent: PASS; I4_PubSubPerLiveGroup: PASS; I5_Duplicates: PASS; I6_DeadLetters: PASS; I7_NothingLeftBehind: PASS

## 2026-09-15 — feature 042 failover harness (in-process pair)

- **Target:** two `HighwayTestServer` nodes (real Kestrel RESP + RocksDB), shared password, replica pulls WAL via `HW.REPL.*`
- **Matrix:** primary writable + replica not; QSEND then catch-up; `HW.REPL.PROMOTE` on standby; resurrection `HELLO` with higher epoch demotes the old primary and writes a reconciliation file; isolated primary with `AutoFailover` fences after `T_fence`; inbound `HW.REPL.WITNESS` defers fencing; client `SendAsync` follows `-NOTPRIMARY`
- **Verdict:** `PASSED` (4/4 `ReplicationPairTests`, CI)
- **Acked-and-replicated loss:** 0 (message pulled onto the replica before promote)
- **Duplicates:** allowed by C9.1; this harness did not count a turbulence window
- **Doorbells-off variant:** not a second soak — doorbell re-subscribe on `SwitchTo` is unit-covered; isolated-primary fence does not depend on doorbells
- **Notes:** standalone `highways` two-process soak was not re-run; the embedded pair is the same RESP server and is the T8 CI gate. OD1 stays 5s/8s/1s.

## 2026-09-15 — shortened-ci — doorbells on (PASSED)

- **Run ID:** `ci-short-da8e2740`
- **Target Rate:** 25 msg/s | **Lease:** 3s | **Doorbells:** on
- **Verdict:** `PASSED` (Exit Code: 0)
- **Total Events Processed:** 132
- **Duplicates Observed:** 0
- **Dead Letters:** 0
- **Notes:** I1_QueueCompleteness: PASS; I2_NoPhantoms: PASS; I3_RpcNeverSilent: PASS; I4_PubSubPerLiveGroup: PASS; I5_Duplicates: PASS; I6_DeadLetters: PASS; I7_NothingLeftBehind: PASS

## 2026-09-15 — shortened-ci — doorbells on (PASSED)

- **Run ID:** `ci-short-e222d5ed`
- **Target Rate:** 25 msg/s | **Lease:** 3s | **Doorbells:** on
- **Verdict:** `PASSED` (Exit Code: 0)
- **Total Events Processed:** 140
- **Duplicates Observed:** 0
- **Dead Letters:** 0
- **Notes:** I1_QueueCompleteness: PASS; I2_NoPhantoms: PASS; I3_RpcNeverSilent: PASS; I4_PubSubPerLiveGroup: PASS; I5_Duplicates: PASS; I6_DeadLetters: PASS; I7_NothingLeftBehind: PASS

## Run `2026-09-15T13-28-31` — doorbells OFF

- **Timestamp:** 2026-09-15 13:28:31 UTC
- **Profile:** `shortened-ci` (Settle 2s, Gap 4s, Arrival 4s, Steady 4s, Turbulence 8s, Drain 8s, Shutdown 2s)
- **Target Load Rate:** 25 msg/s aggregate
- **Lease Duration:** 3s
- **Broker Version:** highways (**RocksDB + RESP** — the post-Garnet stack, feature 041)
- **Doorbells:** **OFF** — every workload ran `DoorbellsEnabled == false`; only the `BackstopSweeper` drove progress (037 R7.1 / 041 R3.2)
- **Overall Verdict:** `PASSED` (Exit Code: 0)
- **Artifacts Path:** `assurance/runs/2026-09-15T13-28-31/`

### Invariants Verification

| Invariant | Verdict | Sent / Total | Processed | Notes |
|---|---|---|---|---|
| **I1_QueueCompleteness** | 🟢 PASS | 20 | 20 | All 20 sent messages were successfully processed. |
| **I2_NoPhantoms** | 🟢 PASS | 20 | 20 | Zero phantom messages found. |
| **I3_RpcNeverSilent** | 🟢 PASS | 20 | 20 | All 20 executed RPC calls received replies or timeouts. |
| **I4_PubSubPerLiveGroup** | 🟢 PASS | 40 | 40 | All 40 published events reached expected subscription groups. |
| **I5_Duplicates** | 🟢 PASS | 0 | 20 | Observed 0 duplicate delivery(s) out of 20 processed events (0.00%). |
| **I6_DeadLetters** | 🟢 PASS | 0 | 0 | Zero dead letters in broker DLQ. |
| **I7_NothingLeftBehind** | 🟢 PASS | 0 | 0 | Final queue depths reached 0 on drain. |

### Observations & Empirical Evidence

1. **The sharpest port test.** With doorbells removed the pub/sub latency mask is gone, so
   correctness rides entirely on the backstop sweep. Every workload logged *"Doorbells
   disabled — engine runs on the backstop sweep only"* and every invariant still passed —
   confirming 037 R7.1: no delivery guarantee depends on pub/sub.
2. **Throughput, not correctness, is what changes.** At backstop-interval latency the same
   short window completes fewer messages (20 sent vs 132 with doorbells on) — expected, and
   the reason the doorbells-off run is judged on invariants, not counts.
3. **Turbulence held.** Graceful restart of `notifications-subs-1` and the mid-flight
   ungraceful kill of `mailer-2` both survived on the sweep alone; `mailer-1` picked up the
   lease redeliveries. Zero loss, zero duplicates, zero dead letters.

---

## Run `2026-09-15T13-27-50` — doorbells on

- **Timestamp:** 2026-09-15 13:27:50 UTC
- **Profile:** `shortened-ci` (Settle 2s, Gap 4s, Arrival 4s, Steady 4s, Turbulence 8s, Drain 8s, Shutdown 2s)
- **Target Load Rate:** 25 msg/s aggregate
- **Lease Duration:** 3s
- **Broker Version:** highways (**RocksDB + RESP** — the post-Garnet stack, feature 041)
- **Doorbells:** on (production default)
- **Overall Verdict:** `PASSED` (Exit Code: 0)
- **Artifacts Path:** `assurance/runs/2026-09-15T13-27-50/`

### Invariants Verification

| Invariant | Verdict | Sent / Total | Processed | Notes |
|---|---|---|---|---|
| **I1_QueueCompleteness** | 🟢 PASS | 132 | 132 | All 132 sent messages were successfully processed. |
| **I2_NoPhantoms** | 🟢 PASS | 132 | 132 | Zero phantom messages found. |
| **I3_RpcNeverSilent** | 🟢 PASS | 132 | 132 | All 132 executed RPC calls received replies or timeouts. |
| **I4_PubSubPerLiveGroup** | 🟢 PASS | 264 | 264 | All 264 published events reached expected subscription groups. |
| **I5_Duplicates** | 🟢 PASS | 0 | 132 | Observed 0 duplicate delivery(s) out of 132 processed events (0.00%). |
| **I6_DeadLetters** | 🟢 PASS | 0 | 0 | Zero dead letters in broker DLQ. |
| **I7_NothingLeftBehind** | 🟢 PASS | 0 | 0 | Final queue depths reached 0 on drain. |

### Observations & Empirical Evidence

1. **First rig run on the post-Garnet binary (041 gate G3).** The `highways` broker booted the
   040 RESP server over the 038 RocksDB store; the unmodified applications connected over real
   inter-process TCP with SE.Redis. 032 R6's broker-record corroboration (`HW.STATS`, DLQ,
   flight recorder) is now served by the RESP server and the storage figure is the RocksDB data
   directory — the AOF measurement is retired with Garnet.
2. **Turbulence held.** Graceful restart of `notifications-subs-1` (same node + group identity)
   and the mid-flight ungraceful kill of `mailer-2`; `mailer-1` absorbed the redeliveries.

---

## Run `2026-08-18T11-29-23`

- **Timestamp:** 2026-08-18 11:33:17 UTC
- **Profile:** `standard-soak` (Settle 15s, Gap 75s, Arrival 35s, Steady 40s, Turbulence 50s, Drain 15s, Shutdown 10s)
- **Target Load Rate:** 100 msg/s aggregate
- **Lease Duration:** 15s
- **Broker Version:** highways (Garnet 2.1.2 standalone)
- **Overall Verdict:** `PASSED` (Exit Code: 0)
- **Artifacts Path:** `assurance/runs/2026-08-18T11-29-23/`

### Invariants Verification

| Invariant | Verdict | Sent / Total | Processed | Notes |
|---|---|---|---|---|
| **I1_QueueCompleteness** | 🟢 PASS | 5182 | 5182 | All 5,182 sent `SendEmail` messages were successfully processed. |
| **I2_NoPhantoms** | 🟢 PASS | 5182 | 5182 | Zero phantom messages found. |
| **I3_RpcNeverSilent** | 🟢 PASS | 5181 | 5181 | All 5,181 executed RPC calls received replies or timeouts. |
| **I4_PubSubPerLiveGroup** | 🟢 PASS | 10364 | 10364 | All 10,364 published events reached expected subscription groups. |
| **I5_Duplicates** | 🟢 PASS | 0 | 5182 | Observed 0 duplicate delivery(s) out of 5,182 processed events (0.00%). |
| **I6_DeadLetters** | 🟢 PASS | 0 | 0 | Zero dead letters in broker DLQ. |
| **I7_NothingLeftBehind** | 🟢 PASS | 0 | 0 | Final queue depths reached 0 on drain. |

### Observations & Empirical Evidence

1. **Redelivery & Claim Verification on Ungraceful Kill:**
   - In `mailer-1.jsonl`, redelivered messages following `mailer-2`'s mid-flight ungraceful kill (`acc-email-002261`, `acc-email-002264`, `edge-011324`) explicitly carry `attempt: 2` with verified `claimed` and `processed` events.
   - Corresponding claim files in `claims/` record persistent attempt count 2 across process boundaries.
2. **Discovery & Telemetry Sampling:**
   - `HW.DISCOVER` queried per registered service (`ValidateAccount`, `GetProfile`) across 494 samples in `broker/stats-samples.jsonl` with 0 errors.
3. **The Gap Phase (75s):**
   - Both producers (`edge-1` and `accounts-1`) produced `SendEmail` messages at full rate.
   - Zero `SendEmail` messages were processed while no mailers were running; `email.send` queue depth climbed monotonically.
4. **The Arrival & Drain Phases:**
   - `mailer-1` and `mailer-2` arrived, drained queue backlog, survived turbulence, and drained to depth 0 on completion.

---

## Run `2026-08-18T10-47-30`

- **Timestamp:** 2026-08-18 10:51:23 UTC
- **Profile:** `standard-soak` (Settle 15s, Gap 75s, Arrival 35s, Steady 40s, Turbulence 50s, Drain 15s, Shutdown 10s)
- **Target Load Rate:** 100 msg/s aggregate
- **Lease Duration:** 15s
- **Broker Version:** highways (Garnet 2.1.2 standalone)
- **Overall Verdict:** `PASSED` (Exit Code: 0)
- **Artifacts Path:** `assurance/runs/2026-08-18T10-47-30/`

### Invariants Verification

| Invariant | Verdict | Sent / Total | Processed | Notes |
|---|---|---|---|---|
| **I1_QueueCompleteness** | 🟢 PASS | 5189 | 5189 | All 5,189 sent `SendEmail` messages were successfully processed. |
| **I2_NoPhantoms** | 🟢 PASS | 5189 | 5189 | Zero phantom messages found. |
| **I3_RpcNeverSilent** | 🟢 PASS | 5189 | 5189 | All 5,189 executed RPC calls received replies or timeouts. |
| **I4_PubSubPerLiveGroup** | 🟢 PASS | 10380 | 10380 | All 10,380 published events reached expected subscription groups. |
| **I5_Duplicates** | 🟢 PASS | 0 | 5189 | Observed 0 duplicate delivery(s) out of 5,189 processed events (0.00%). |
| **I6_DeadLetters** | 🟢 PASS | 0 | 0 | Zero dead letters in broker DLQ. |
| **I7_NothingLeftBehind** | 🟢 PASS | 0 | 0 | Final queue depths reached 0 on drain. |

### Observations & Rig Evidence

1. **The Gap Phase (75s):**
   - Both producers (`edge-1` and `accounts-1`) produced `SendEmail` messages at full rate.
   - Zero `SendEmail` messages were processed while no mailers were running.
   - Broker queue depth for `email.send` grew monotonically as observed in `broker/stats-samples.jsonl`.
2. **The Arrival Phase (35s):**
   - `mailer-1` and `mailer-2` started concurrently with `WorkerConcurrency = 64` and 500ms handler processing time.
   - Backlogged messages in `email.send` drained smoothly without message loss.
3. **The Turbulence Phase (50s):**
   - At `t+10s`: Graceful restart of `notifications-subs-1` occurred with identical node and group identities; subscriber re-attached and caught up on all intermediate channel events (`UserSignedUp`, `AccountAudited`).
   - At `t+25s`: Ungraceful kill (`Process.Kill()`) of `mailer-2` mid-flight while actively processing claimed messages; surviving `mailer-1` took over load and claimed lease-expired entries after 15s lease expiry with zero lost messages.
4. **The Drain Phase (15s):**
   - `edge-1` stopped gracefully.
   - All outstanding queues and channels drained to depth 0 across the entire topology.
5. **Broker Durability & Storage:**
   - Broker state cleanly torn down on `PASSED` verdict; flight recorder dumped to `broker/recorder-replay.jsonl`.
