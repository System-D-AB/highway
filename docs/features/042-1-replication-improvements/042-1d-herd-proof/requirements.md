# Feature 042-1d — Herd Proof & Record: Requirements

*Fourth sub-feature of [042-1](../requirements.md); executes parent T9–T12 — the gate. Starts
when 042-1b and 042-1c are both green; this is where the two halves are proven **together**,
at multi-client/multi-node scale, and the product record is corrected.*

## Introduction

042's review (the [gaps report](../../042-replication/implementation-gaps.md), G10/G11)
established the standard this feature must meet: the harness must *kill* things, *read work
back*, *count duplicates*, and cover the partition matrix — and the constraint register must
describe only what the code proves. Everything here is an assertion with a name.

## Requirements

### D-R1: The herd-cohesion harness (parent R11.1, R3)

1. N in-process clients (N ≥ 3) + a multi-node cluster (≥ 3 nodes). Scripted **hard kill** of
   the master (process/server disposal, not a simulated flag): assert **every** client lands
   on the **same** successor (no split), in-flight RPCs and unacked sends replay and are
   answered, **zero acked-and-replicated loss proven by read-back on the new master**, and
   **duplicates counted** (a ledger, not a shrug).
2. Convergence latency is measured per client and recorded (parent R9.3, R3.3's `x` bound
   sanity-checked).

### D-R2: The partition matrix (parent R11.2, R8)

1. **Master isolated from peers, not clients** → keeps the herd, keeps serving; standbys stay
   unwilling-to-nobody (no client arrives); no second master.
2. **Master islanded** (no peers, no clients) → herd converges on the willing successor;
   island performs no master-only effects; on heal it stands down via epoch with a
   reconciliation report.
3. **Client-side reachability differences** → a client that can reach no willing node
   surfaces the transient class and keeps retrying; it never lands on a *different* live
   master. The invariant asserted by name: **never two live masters with clients**.

### D-R3: The named scenarios (parent R11.3–R11.6)

1. **RPC-across-failover**: kill mid-RPC; the caller re-drives the same request id to the
   successor and receives the reply (unless already closed green in 042-1b B-T6 — then this
   re-runs it at herd scale).
2. **Graceful GOODBYE**: drain → herd moves with **near-zero replay** (asserted: cache sizes
   at move time) and zero loss; a deliberately wedged in-flight item at the deadline falls
   back to replay and is not lost.
3. **Rejoin without preemption**: higher-priority node returns mid-service → herd does not
   move; on the next real transition it converges on the returner.
4. **Priority collision**: second announcer refused with the named error; never joins the
   roster.

### D-R4: Observability (parent R9)

1. `HW.STATS` + dashboard: role (master/standby/stood-down), epoch, connected-client count,
   the live roster with priorities, per-replica slot/lag (042, unchanged), last transition
   (cause, from→to, epochs, timing), convergence latency.
2. Every harness scenario's transitions are asserted *through* this surface (observability is
   proven by being load-bearing in the tests, not by field-existence checks alone).

### D-R5: The assurance rig (parent R11.7)

1. I1–I5 green against a multi-node herd with a **mid-turbulence master transition**;
   the **doorbells-off** variant included; both recorded in `assurance/RUNLOG.md`. This is the
   run 042's G10 recorded as skipped — it is not skippable here.

### D-R6: The record (parent T12)

1. Dated `constraints.md` amendments: ack-is-the-birth; the async RPO window restated;
   herd-mastership/no-elections (extending C9.5); no-auto-failback with GOODBYE as the
   deliberate failback; priority-collision-reject; the `x ≤ W < T_fence` coupling. C9.2/C9.3
   statuses corrected to describe the herd model as proven here.
2. `product.md` gains the client-herd failover paragraph; `roadmap.md` re-points the
   clustering line at 042 + 042-1; the 042 [gaps report](../../042-replication/implementation-gaps.md)
   gets a dated closure section mapping every G to where it was fixed (042 fixes) or
   superseded (042-1); parent 042-1 `tasks.md` boxes ticked with done-notes.

## Non-Goals

New mechanics — this feature builds assertions and documents only. Any behaviour change it
forces goes back into 042-1b/c as a fix.
