# Feature 046 — Dashboard & Flight-Recorder Correctness

*Field-reported: after a Highway broker runs for a few hours, the dashboard stops showing recent
RPC calls and shows stale timestamps, RPC responses are never displayed, the Diagnostics table
shows `undefined` in two columns, and the Nodes view lists long-gone nodes. Investigation traced
the "disappearing events" and "wrong timestamps" to a **single storage bug in the flight
recorder's ring buffer**; the rest are dashboard display gaps. This feature fixes all of them.*

## Requirement 1: The recorder returns correct events after a sweep (the core bug)

**User Story:** As an operator watching the dashboard, I want the most recent operations to appear
and the timestamps to be real, for the whole life of the broker — not just the first hour.

**Root cause (verified).** `NameBuffer` is a fixed circular buffer with a write cursor `_next` and
a live `_count`. `Read` reconstructs chronological order with `start = _count == Capacity ? _next :
0` and scans only `_count` slots ([NameBuffer.cs:91,93](../../src/Highway.Server/Observability/NameBuffer.cs#L91)).
That is correct only in two states: full-and-wrapped, or filling-from-zero-never-wrapped.
`SweepExpired` ([NameBuffer.cs:113-136](../../src/Highway.Server/Observability/NameBuffer.cs#L113))
nulls expired slots in place and decrements `_count` **without compaction and without adjusting
`_next`**, creating a third state — *wrapped-but-holed* — that `Read` mishandles: with `_count <
Capacity` it flips to `start = 0` and scans the wrong low-index window, omitting the most recent
events and surfacing older ones. `TrimTo` (the global-budget sweep) has the identical flaw. Default
retention is 1 hour, so nothing is swept in the first hour (buffer stays full → reads are correct),
and the corruption begins only after the broker has run long enough to sweep — exactly the reported
timeline.

#### Acceptance Criteria

1. After a buffer has **wrapped and then been swept** (retention) or **trimmed** (budget), `Read`
   returns exactly the live, in-retention events, in chronological order, including the most recent
   ones. No live event is omitted and no wrong (older) event is surfaced in its place.
2. The message view's "wrong timestamp" symptom is gone as a consequence: the times shown are the
   times of the actual recent operations (the display/timezone path was already correct and is not
   changed).
3. A **regression test** exercises wrap → sweep → read and wrap → trim → read and asserts the
   returned set and order. (The existing budget test only asserted byte accounting, which is how
   this shipped.)

## Requirement 2: RPC responses are visible

**User Story:** As a developer debugging an RPC, I want to see the **response** payload, not only
the request.

**Root cause.** The message-detail endpoint already merges the reply event from the `hw.replies`
bucket (so `RpcReplied` appears in the timeline), but it exposes a single payload picked as the
*first* non-empty one — the request — and `MessageDetailDto` has only one payload field
([DashboardEndpoints.cs:186,196](../../src/Highway.Server.Dashboard/DashboardEndpoints.cs#L186)).

#### Acceptance Criteria

1. The message-detail view shows the **response payload** (from the `RpcReplied` event) in addition
   to the request payload, each labelled, both obeying the capture-mode rules (Full / HeadersOnly /
   Off) exactly as the request payload does today.
2. When there is no reply (a queue message, a one-way send, or a reply not retained), the response
   panel says so rather than showing nothing or the request bytes.

## Requirement 3: The Diagnostics table shows real numbers

**User Story:** As an operator, I want the Events and Dropped columns to show counts, not
`undefined`.

**Root cause.** [diagnostics.js:16,19](../../src/Highway.Server.Dashboard/wwwroot/js/diagnostics.js#L16)
reads `n.events` and `n.dropped`; the `NameDto` fields serialize as `count` and `droppedCapacity`.

#### Acceptance Criteria

1. The Events column shows the per-name event count and the Dropped column shows the per-name
   capacity drops. (Fix the field names; no server change.)

## Requirement 4: The Nodes view does not present gone nodes as current

**User Story:** As an operator, I want the Nodes list to show what is running now, with absent nodes
out of the way but still reachable.

**Root cause.** `/api/nodes` returns every registered node tagged `live`/`stale`/`absent`, and
[nodes.js:53](../../src/Highway.Server.Dashboard/wwwroot/js/nodes.js#L53) renders all of them with
no filter.

#### Acceptance Criteria

1. The Nodes view shows **live and stale** nodes by default; **absent** nodes (>1h unseen) are not
   mixed into that list.
2. Absent nodes remain reachable — a toggle ("Show absent (N)") or a separate grouping — because an
   absent node can return, and the count itself is information.
3. No server/protocol change is required; the state is already on each row.

## Requirement 5: RPC replies survive under load (secondary)

**User Story:** As an operator under real RPC throughput, I want reply/outcome information to persist
as long as the request does, so RPC rows don't collapse to "Incomplete".

**Root cause.** All RPC replies across all services share one `hw.replies` buffer at the default
per-name capacity (1000). Under high aggregate throughput replies churn out within the retention
window, so a retained request can lose its reply and project as `InFlight`/`Incomplete`.

#### Acceptance Criteria

1. The `hw.replies` bucket is given headroom appropriate to being cluster-wide (a larger capacity
   than a single service's), **or** the limitation is documented in `constraints.md` with the
   configuration lever to raise it. (v1 may choose the pragmatic capacity bump; the design decides.)

## Non-goals

- Rewriting the recorder as durable/queryable storage — it stays bounded and volatile (002).
- Changing the wire protocol or any broker behaviour — every fix here is in the recorder read path
  or the dashboard.
