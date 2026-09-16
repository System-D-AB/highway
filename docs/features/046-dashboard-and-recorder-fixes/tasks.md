# Feature 046 — Tasks

```
T1 (recorder read/trim correctness) ── the data-corruption fix, highest priority
      │
      ├── T2 (RPC response in message detail)
      ├── T3 (diagnostics columns)
      ├── T4 (nodes view filtering)
      └── T5 (hw.replies headroom + the record)
```

### - [x] T1 — Fix `NameBuffer.Read`/`TrimTo` and regression-test it

**Fulfills:** R1
Anchor `Read` and `TrimTo` at `_next` and scan all `Capacity` slots, skipping nulls (removes the
`start = _count == Capacity ? _next : 0` window that breaks on a wrapped-and-holed buffer). Add a
regression test: append past capacity, sweep (retention) and trim (budget) to a wrapped-and-holed
state on a controlled clock, then assert `Read` returns exactly the live in-retention events in
chronological order including the newest.
**Done when:** the new test passes; all existing `FlightRecorderTests`/`NameBufferTests` stay green.

### - [x] T2 — Show the RPC response payload

**Fulfills:** R2
Extract the `RpcReplied` event's payload separately in `/api/message/{name}/{id}`; add
`ResponsePayload`/`ResponseState` to `MessageDetailDto` (same capture-mode gate as the request).
Render a "Response" panel in `message.js`; show a "no response recorded" note when absent.
**Done when:** an RPC message shows request and response; a one-way/queue message shows the
no-response note; capture modes respected.

### - [x] T3 — Diagnostics columns

**Fulfills:** R3
`diagnostics.js`: `n.events` → `n.count`, `n.dropped` → `n.droppedCapacity`.
**Done when:** the Events and Dropped columns show numbers.

### - [x] T4 — Nodes view filtering

**Fulfills:** R4
`nodes.js`: render live + stale by default; absent behind a "Show absent (N)" toggle.
**Done when:** absent nodes are out of the default list but reachable via the toggle.

### - [x] T5 — `hw.replies` headroom + the record

**Fulfills:** R5
Give `hw.replies` a larger built-in capacity via the recorder's per-name override (or document the
lever if a built-in default is not clean). Note the reserved bucket, its sizing, and that it is still
bounded, in `constraints.md`.
**Done when:** the reply bucket holds its configured capacity; the register describes it.

---

## Gate

The recorder read path is correct for the life of the broker (T1) — proven by a wrap → sweep/trim →
read test — and the dashboard surfaces responses, real diagnostics numbers, and a live node list.
