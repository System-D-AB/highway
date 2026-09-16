# Feature 042-1b — The Herd Client: Tasks

*Parent T2, T3, T4. Starts after 042-1a. Server-side willingness (042-1c) can be stubbed at
the contract seam for unit work, but B-T6's integration needs 042-1c's C-T4 — coordinate the
two trains there.*

### - [x] B-T1 — Bootstrap + roster cache

**Fulfills:** B-R1, B-R2
Confirm/parse the multi-endpoint form through `HighwayOptions.Server` (042 G6 parsing is the
base); add the roster cache to `HighwayConnectionSource`: read `roster.*` from
`HW.REPL.STATUS` on session establishment, re-read on narration, fall back to the bootstrap
list when no roster exists.
**Done when:** unit — roster supersedes bootstrap for order; dynamic membership (node absent
from the string, present in roster) resolves; single-endpoint behaviour unchanged; malformed
strings refused legibly.

### - [x] B-T2 — The successor walk

**Fulfills:** B-R3.1, B-R3.3–B-R3.5
Retarget `TryFailoverAsync` to the design's walk: roster priority order, `HW.REPL.HELLO
CLIENT` handshake, willing wins, unwilling-redirect followed, serialized by the existing gate,
exhaustion → transient class + bounded backoff.
**Done when:** unit table green (deterministic successor, priority-0 skip, redirect-following,
exhaustion surface); the 042 killed-primary walk skeleton test is re-pointed at the new rule.

### - [x] B-T3 — The three triggers

**Fulfills:** B-R3.2
Wire (a) the existing loss hook to the walk; (b) the `hw:door:topology` listener
(re-evaluate, stay put if incumbent answers); (c) the health timeout `x` — last-success
clock + idle PING ticker.
**Done when:** fake-clock unit for `x`; narration integration (spurious push → client stays);
loss path covered by B-T6.

### - [x] B-T4 — InFlightCache

**Fulfills:** B-R4
New `InFlightCache`: register before wire-write, clear on ack; sends and publishes covered;
RPC stays in `PendingCallRegistry`. No new unbounded buffer (B-R4.2).
**Done when:** unit — lifecycle exact (no entry survives its ack; every unacked entry
present); bounded-behaviour test.

### - [x] B-T5 — Replay + RPC re-drive on convergence

**Fulfills:** B-R5
On adoption: session re-establishment (existing startup paths on the new mux) → InFlightCache
replay in order, same ids → `PendingCallRegistry` re-drive, same ids → resume. `-NOTPRIMARY`
mid-replay re-enters the walk with the cache intact.
**Done when:** capturing-server unit asserts replay ids equal issue ids; re-drive answers the
original awaited task; mid-replay redirect loops safely.

### - [x] B-T6 — Client-scoped integration *(gate with 042-1c C-T4)*

**Fulfills:** B-R6
Two real nodes (042 pair fixture): kill the master with a willing standby → walk, re-register,
replay, traffic resumes; frozen-master variant; the named RPC-across-failover scenario
(parent R11.3) here if the fixture carries a service host, else a recorded pointer to 042-1d.
**Done when:** integration green against a real willing standby (C-T4 landed); the R11.3
scenario's home is decided and recorded.

---

## Completion record (2026-09-16)

All six tasks done; client 224/224, integration pair suite 10/10 green. Notes:

- **B-T4 amendment (dated):** the "InFlightCache" for queue/publish sends is satisfied
  **structurally**, not by a new buffer: every send runs inside `SendAsync`'s bounded
  retry loop whose closure holds the verb, arguments and requestId, and re-executes
  against the live `_db` after any convergence — the cache *is* the closure. RPC pending
  calls gained explicit replay data (`PendingCallRegistry.Register(serviceName,
  envelope)`) and `ReplayPendingAsync` (slots probed first, then same-id re-drive),
  raised by the connection's `Converged` event. No entry survives its ack/reply, and no
  new unbounded buffer exists — B-R4's intent met with less machinery.
- **Design holes found and fixed as product code:**
  1. *Idle is not frozen* — the health ticker PINGs first and walks only on a
     failed/late probe, or an unused healthy master would lose its herd.
  2. *A draining master answered "master"* — the walking herd was pulled back onto the
     departing node; it now answers `standby` (protocol doc updated).
  3. *A stood-down node self-redirects* — `-NOTPRIMARY` naming the current host now
     falls back to the roster walk instead of looping.
  4. The GOODBYE walk retries across the convergence window (the successor turns
     willing moments after the narration).
- **Dynamic membership proven end-to-end:** `Client_FollowsGoodbye_…` bootstraps with
  ONLY the primary's endpoint and converges onto a standby it learned from the roster.
- **The named RPC-across-failover scenario (parent R11.3)** is deferred to 042-1d D-T4
  (needs a service-hosting herd fixture); the replay mechanics it exercises are
  unit-wired here (`ReplayPendingAsync`, `Converged`).
