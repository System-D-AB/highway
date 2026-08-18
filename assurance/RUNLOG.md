# Highway Assurance Rig — Run Log

This log records every standard soak and integration run executed against Highway broker instances. Newest runs are listed first.

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
