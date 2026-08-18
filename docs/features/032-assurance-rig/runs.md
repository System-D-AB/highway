# Feature 032 — The Assurance Rig: Accepted Runs

This document records accepted assurance rig runs providing empirical evidence for the verification of feature 032.

---

## Accepted Run: Standard Soak Run `2026-08-18T11-29-23`

- **Timestamp:** 2026-08-18 11:33:17 UTC
- **Profile:** Standard Soak Profile (4 minutes, 100 msg/s aggregate, 15s lease)
- **Verdict:** `PASSED` (Exit Code: 0)
- **Run Directory:** `assurance/runs/2026-08-18T11-29-23/`

### Invariant Verification Matrix

| Invariant | Description | Sent / Total | Processed | Verdict |
|---|---|---|---|---|
| **I1_QueueCompleteness** | Sent `SendEmail` messages are a subset of processed (`sent ⊆ processed`) | 5,182 | 5,182 | 🟢 PASS |
| **I2_NoPhantoms** | Processed messages were sent by known producers (`processed ⊆ sent`) | 5,182 | 5,182 | 🟢 PASS |
| **I3_RpcNeverSilent** | Every executed RPC received a response or timeout (`executed ⊆ replied ∪ timed-out`) | 5,181 | 5,181 | 🟢 PASS |
| **I4_PubSubPerLiveGroup** | Every published event reached expected active subscription groups | 10,364 | 10,364 | 🟢 PASS |
| **I5_Duplicates** | At-least-once duplicate attribution rate | 0 | 5,182 (0.00%) | 🟢 PASS |
| **I6_DeadLetters** | Zero unhandled poison pills in DLQ | 0 | 0 | 🟢 PASS |
| **I7_NothingLeftBehind** | Final broker queue depths are 0 | 0 | 0 | 🟢 PASS |

### Tested Topologies & Lifecycle Events

- **Multi-producer Queue:** Two distinct producers (`edge-1` and `accounts-1`) actively enqueueing to `email.send`.
- **The Gap Phase:** Proved queue accumulation without consumer presence (depth grew monotonically).
- **The Arrival Phase:** Two independent consumer processes (`mailer-1` and `mailer-2`) started late, concurrent handler processing with `WorkerConcurrency = 64` and 500ms handler latency.
- **Graceful Subscriber Restart:** `notifications-subs-1` restarted gracefully during turbulence, successfully resuming consumption of channel partitions without message loss or gap corruption.
- **Ungraceful Consumer Kill:** `mailer-2` killed ungracefully mid-flight; remaining consumer `mailer-1` reclaimed and processed expired leases via `HW.LEASESWEEP` / `HW.QCLAIM` with zero message loss.
