# Feature 046 — Design

## R1 — Fix `NameBuffer.Read` and `TrimTo` (the core bug)

The bug is that `Read`/`TrimTo` compute a start position and a scan length that are only valid when
the buffer has no holes: `start = _count == Capacity ? _next : 0`, scanning `_count` slots. A swept
buffer that has already wrapped has `_next != 0` **and** `_count < Capacity` **and** null holes, and
that combination reads the wrong slots.

**Fix: anchor every scan at `_next` and walk all `Capacity` slots, skipping nulls.** `_next` is
always the oldest position in ring order (Append writes at `_next` then advances, so `_next` forward
is oldest → newest), in every state:

- *Never wrapped* (`_next == _count`, tail `[_count, Capacity)` null): start at `_next`, the null
  tail is skipped, then `[0, _count)` is visited oldest → newest.
- *Full and wrapped* (`_next` = oldest): unchanged from today.
- *Wrapped and holed* (post-sweep/trim): start at `_next`, skip the null holes, ring order preserved.

Because `Read` already skips nulls and filters by retention/window/node, and `TrimTo` already skips
nulls, the only change is the start anchor and the loop bound (`_count` → `Capacity`). Cost goes from
O(`_count`) to O(`Capacity`) — 1000 slots, negligible, and only on a dashboard read.

```csharp
// Read: oldest-first, robust to holes.
for (var i = 0; i < Capacity; i++)
{
    var evt = _ring[(_next + i) % Capacity];
    if (evt is null) continue;
    // …retention / from-to / node filters, then add; break at limit…
}
```

`TrimTo` gets the same `start = _next` (drop the truly-oldest first, holes skipped). No compaction is
introduced — Append already reuses holes as `_next` cycles, incrementing `_count`; correctness now
no longer depends on the buffer being hole-free.

**Regression test (the gap that let it ship):** a test that appends past capacity (wrap), sweeps (or
trims) so the buffer is wrapped-and-holed, then asserts `Read` returns exactly the live in-retention
events in order — including the newest. Uses an injected/controlled clock so retention is
deterministic. Belongs beside the existing `FlightRecorderTests`/`NameBufferTests`.

## R2 — Show the RPC response

`/api/message/{name}/{id}` already has the reply event in `events` (merged from `hw.replies` by
`ReadAllFor`). Split the single payload pick into two:

- **request** = the payload of the first *request-side* event (today's behaviour — the `RpcEnqueued`
  / send / publish payload).
- **response** = the payload of the `RpcReplied` event for this id, if present.

`MessageDetailDto` gains a `ResponsePayload` (base64 or null) and `ResponseState`
(`captured`/`headers-only`/`not-captured`/`disabled`/`none`), mirroring the existing request
`Payload`/`PayloadState` and obeying the same capture-mode gate. The frontend `message.js` renders a
second panel, "Response", next to "Message"; when `ResponseState == "none"` it shows "no response
recorded (one-way, or reply not retained)".

## R3 — Diagnostics columns

One-line frontend fix in `diagnostics.js`: `n.events` → `n.count`, `n.dropped` → `n.droppedCapacity`
(the stat cards already read the correct `data.*` names). No server change.

## R4 — Nodes view filtering

`nodes.js` partitions `data.nodes` by `state`: render `live` + `stale` in the main table; collect
`absent` behind a **"Show absent (N)"** toggle that reveals a second table. Purely client-side — the
`state` field is already on every row. The empty-state text stays for the "no live/stale nodes" case.

## R5 — `hw.replies` headroom

`hw.replies` is cluster-wide (every RPC reply, all services) yet sized like one service (capacity
1000). Give it a **built-in per-name override** through the recorder's existing override map
(feature 002) — a larger capacity (design pins the number, e.g. 8× = 8192) and the standard
retention — so a retained request keeps its reply under realistic load. Record the reserved bucket
and its sizing in `constraints.md`, with the note that it is still bounded (a very high sustained RPC
rate can still age replies out within the retention window — raise capacity/retention to widen it).
If the override mechanism cannot cleanly carry a built-in default, fall back to documenting the lever
only; the correctness fix (R1) is independent of R5.

## Testing strategy

| Layer | Proof | Req |
|---|---|---|
| Recorder read | wrap → sweep → read returns the live newest set in order; wrap → trim → read likewise | R1 |
| Recorder read | existing FlightRecorder/NameBuffer tests stay green (no regression to the non-holed paths) | R1 |
| Message detail | an RPC with a captured reply returns a distinct response payload; a one-way message returns `ResponseState = none` | R2 |
| Frontend | diagnostics rows show numeric Events/Dropped; nodes view hides absent behind a toggle; message page shows a Response panel (verified against a running broker) | R3, R4, R2 |
| Reply retention | `hw.replies` buffer holds its configured (larger) capacity | R5 |

## Non-goals

Durable/queryable event storage; any protocol or broker-behaviour change. All fixes are in the
recorder read path or the dashboard.
