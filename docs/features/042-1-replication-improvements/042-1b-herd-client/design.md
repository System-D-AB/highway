# Feature 042-1b — The Herd Client: Design

*Implements the client half of [042-1](../design.md) per the
[contract](../042-1a-contract/design.md). Grows the 042 G6 groundwork rather than replacing
it.*

## Where it lives

```
Highway.Client/Engine/
├── HighwayConnectionSource.cs   ← becomes the HerdConnection home:
│     bootstrap parse (exists) + roster cache + successor walk (retargets TryFailoverAsync)
├── HighwayConnection.cs         ← trigger wiring (loss hook exists), health timeout x,
│     narration listener, replay orchestration, mux adoption (exists)
├── InFlightCache.cs             ← NEW: unacked sends; pairs with PendingCallRegistry
└── PendingCallRegistry.cs       ← re-drive support (same ids, new connection)
```

The 042 G6 pieces map forward: `HostsOf`/`OptionsOf` → B-R1 done; the serialized
`TryFailoverAsync` + `_failoverGate` → the walk's skeleton (candidate iteration changes from
probe-`STATUS`-for-Primary to roster-order + `HELLO CLIENT` willingness); `AdoptCurrentMultiplexer`
+ delayed-dispose → connection swap, unchanged; the connection-loss hook in the retry loop →
trigger (a), unchanged.

## The successor walk (B-R3)

```
candidates = roster.membersByPriorityAscending.skip(priority == 0)
             ?? bootstrapList                     // roster never learned
walk:
  for node in candidates:
      handshake = HW.REPL.HELLO CLIENT <clientId> <lastSeenEpoch>
      willing        → adopt(node); break
      unwilling(m,e) → if m reachable: adopt-after-handshake(m) else continue
      unreachable    → continue
  none → transient error class, bounded backoff, re-walk
```

- One walk at a time (`_failoverGate`); concurrent operations await and inherit the adopted
  multiplexer (042 G6 semantics, kept).
- `lastSeenEpoch` is the highest epoch the client has observed (from `-NOTPRIMARY`, STATUS, or
  narration); it lets a willing-but-stale node be out-ranked by the unwilling-redirect of a
  fresher one. The client never *invents* epochs.
- Following an unwilling redirect re-runs the handshake at the target — the client connects
  only to a node that answered willing (or the incumbent master), never blindly.

## The three triggers (B-R3.2)

| Trigger | Source | Wiring |
|---|---|---|
| TCP drop | `RedisConnectionException` path | exists (042 G6 hook) — retarget to the walk |
| Narration | `hw:door:topology` subscription | new listener beside the doorbell subscriber; on TOPOLOGY/GOODBYE → schedule a re-evaluation (re-read roster, re-run rule; stay put if the incumbent still answers) |
| Health timeout `x` | no successful exchange within `x` | a lightweight last-success clock stamped by every completed operation + a 1s ticker issuing `PING` when idle; breach → treat as loss |

`x` default 3s, config `HighwayOptions.MasterHealthTimeout`; the contract fixes
`x ≤ W < T_fence` so a walking client meets a willing standby rather than racing it.

## InFlightCache and replay (B-R4, B-R5)

- **Population:** the send path registers `(requestId, verb, args, payload)` *before* the wire
  write; the ack handler removes it. RPC calls stay in `PendingCallRegistry` (already keyed by
  requestId) — the cache does not duplicate them, it complements them.
- **Replay order** on adoption: (1) engine session re-establishment — auth, `HW.HEARTBEAT`
  registration, `HW.SUBSCRIBE` group re-registration, doorbell re-subscribe (all existing
  startup code paths, invoked on the new mux); (2) InFlightCache entries in issue order,
  same ids; (3) PendingCallRegistry re-drive — re-issue `HW.CALL` with the original request
  ids; the awaiting tasks are untouched. (4) resume normal traffic (the gate releases).
- A replay that answers `-NOTPRIMARY` mid-sequence re-enters the walk (the herd may still be
  converging); the cache survives — replay is idempotent to repeat because ids are stable.
- **Boundary honesty (parent R5):** the cache lives in process memory, deliberately. Nothing
  is written to disk; a dead client's unacked work is a non-birth, stated, not patched.

## What is NOT built here

- No willingness evaluation client-side beyond reading the reply — the standby decides.
- No topology persistence — the roster cache is session state, rebuilt on connect.
- No change to application-facing semantics: the transient error class, retry budgets, and
  `[Idempotent]` behaviour are unchanged (parent R6.1's promise).

## Testing strategy

| Proof | Requirement |
|---|---|
| Successor function unit table (roster order, priority-0 skip, unwilling-redirect, bootstrap fallback, single-endpoint identity) | B-R1, B-R2, B-R3 |
| Frozen-master detection: fake-clock last-success breach → walk within x+margin | B-R3.2 |
| InFlightCache lifecycle: populated pre-wire, cleared on ack, replayed same-id (asserted at a capturing test server) | B-R4, B-R5 |
| RPC re-drive: pending call answered after adoption, id equality asserted | B-R5.2 |
| Two-node integration: kill node A (willing standby B) → walk, session re-establishment order, replay, traffic resumes | B-R6.2 |
| Named RPC-across-failover (parent R11.3) — here if a service host fits the fixture, else deferred to 042-1d with a pointer | B-R6.3 |
