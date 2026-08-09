# Design: Dashboard — A Catalogue, Not a List of Names

> **Four original decisions + eight engineering-review decisions, answered.**
>
> | | Decision | Chosen |
> |---|---|---|
> | 1 | Node address | **Deferred to its own feature** (review 0A) — framing + feasibility risk |
> | 2 | Where `kind` comes from | **The server classifies**; the browser never parses a name |
> | 3 | The flat name list | **Removed from the main page**, kept under diagnostics |
> | 4 | 020's remaining views | **Folded into entity pages** |
> | R-0A | T3 scope | **Deferred** — unversioned `NodeRegistration` framing + unverified feasibility |
> | R-1A | Catalogue data source | **Union: registry (declared) + recorder name index (observed)** |
> | R-2A | `HasLiveHost` model | **Replaced by a state enum: Live \| HostStale \| NeverDeclared \| Unknown** |
> | R-3A | mTLS navigation loss | **Catalogue degrades to recorder-only, with a banner naming the setting** |
> | R-4A | Error banner mechanism | **Dedicated error region keyed by source, separate from broker identity** |
> | R-5A | Front-end structure | **ES modules, one per view, shared fetch/render helpers — no build step** |
> | R-6A | Routing with `/` in names | **Kind and name as query params: `#/entity?kind=service&name=…`** |
> | R-7A | Polling architecture | **One scheduler; only the visible view polls; interval in `DashboardOptions`** |

## The model the product already has

```
Node  ── hosts ──►  Service   (RPC)          ── has ──►  events, in-flight, dead letters
      ── hosts ──►  Queue     (SendAsync)    ── has ──►  events, depth, bytes, dead letters
      └─ subscribes ─►  Channel (PublishAsync)
                            └─ has ──► Group ── which IS a queue (018)
```

Five entity kinds and two relationships. **Every one of them already exists in the broker**; none
has ever reached the screen, because the dashboard renders the recorder's name index instead.

### Where each piece comes from

```
hw:reg:nodelist            ─► which nodes exist
hw:reg:node:{nodeId}       ─► [i64 lastSeenTicks][catalog json]
                                   └─ services[], channels[], queues[]   ← the catalogue

IBrokerState (020)         ─► depth, bytes, in-flight, dead letters, delayed
FlightRecorder             ─► events, per name
```

**The catalogue needs no new storage.** Every heartbeat since feature 006 has carried the node's
catalog; nothing has ever read it back for display.

## Decision 2 — The server classifies; the browser never parses a name

```
WRONG                                RIGHT
─────                                ─────
if (name.includes('@'))              server returns:
    kind = 'group'                     { name, kind: "group",
                                         channel: "orders.placed",
a naming convention, reimplemented     host: "shop-1" }
in JavaScript, that breaks the day
a queue is legitimately named with   one implementation, in the process
an '@' — which 018 reserved           that owns HighwayKeys and knows what
precisely because names collide       '@' means
```

`@` is reserved in user-declared names (018 T0) **because the server derives group-queue names
with it**. That is a server fact. A browser rule duplicating it would be a second implementation
of a naming convention — the class of drift this project keeps finding, most recently when the
dashboard would have had to learn the key layout (020 Decision 3).

So `IBrokerState` gains a catalogue read returning classified entities:

```csharp
internal sealed record CatalogueEntryDto(
    string Name,
    EntityKind Kind,          // Service | Queue | Channel | Group | Internal | Unknown
    string? ParentChannel,    // set for Group, so the view can nest it
    IReadOnlyList<string> Hosts,
    bool HasLiveHost);
```

**`Unknown` is a real member** (R1.4). A name the server cannot classify is shown as unknown
rather than filed under a plausible kind — a wrong classification is worse than an admitted gap,
because the operator stops questioning it.

## Decision 1 — Address is an observation, not a declaration

Highway records a node **name** and nothing else. An operator asking "which host is that?" has
no answer today.

```
node name       chosen by the operator        "order-service-1"
address         observed by the broker        seen from 10.1.4.22:51234
```

**Labelled as an observation on purpose.** A node behind NAT, in a container, or scaled
horizontally under one name will report an address that is true and not useful. "Seen from …" is
honest about what it is; a field called `Address` implies a property of the node rather than of
its connection.

## The views

```
┌─ Nodes ───────────────────────────────────────────────────────────┐
│  node               state        hosts                            │
│  shop-1             live         1 service · subscribes 1 channel │
│  order-service-1    live         2 services · 1 queue · 1 channel │
│  batch-07           stale 4m     1 queue                          │
│  legacy-03          absent 19h   ⚠ retires in ~5h — 41k messages  │  ← 017
└───────────────────────────────────────────────────────────────────┘

┌─ Catalogue ───────────────────────────────────────────────────────┐
│  SERVICES                                                          │
│    orders.create        order-service-1                            │
│    orders.get           order-service-1                            │
│    payments.refund      ⚠ no live host                             │  ← R3.4
│  QUEUES                                                            │
│    invoices.generate    order-service-1        12 · 3% of 1 GB     │
│  CHANNELS                                                          │
│    orders.placed                                                   │
│      └ @shop-1          shop-1                  2 · <1%            │  ← R3.3
│    inventory.low                                                   │
│      └ @order-service-1 order-service-1         2 · <1%            │
└───────────────────────────────────────────────────────────────────┘

┌─ orders.placed  (channel) ────────────────────────────────────────┐
│  hosted by: shop-1                                                 │
│  groups: @shop-1 — depth 2, no dead letters                        │
│  ── events ──  (the existing view, scoped to this entity)          │
└───────────────────────────────────────────────────────────────────┘
```

**A channel nests its groups.** `orders.placed` and `orders.placed@shop-1` are one channel and
one of its subscribers. Listing them as peers is the single biggest reason the current page is
unreadable, and it is a rendering decision, not a data one.

**"No live host" is the row worth having.** A service nobody serves and a queue nobody consumes
are real failures that look identical to healthy ones today — the queue view added in 020 shows
depth, but depth alone cannot tell "busy" from "abandoned".

## Decision 3 — The flat list moves, it does not vanish

The recorder's own name index is genuinely useful **when debugging the recorder**. It is
actively misleading as an operator's home page.

So it moves to a diagnostics view, alongside the internal names R5 hides — `hw.replies`, and the
node ids that become recorder names because `HW.HEARTBEAT` records under them.

**`hw.replies` is broker-internal by construction.** Feature 015 introduced it precisely so that
RPC replies stop creating one recorder buffer per request. Presenting it as a user entity shows
an operator a fix for a bug they never had.

## Decision 4 — 020's views fold in here

020's Phase 0 is the data layer and stands: `IBrokerState`, `LoopbackConnection`, and the
security matrix that proved the read path against open, password, TLS and mTLS.

Its T6–T9 describe four flat views. **The same information belongs on entity pages**, and
building it flat first would mean restructuring it immediately. That is not a criticism of 020 —
it was specced before this problem was visible, and the screenshot is what made it visible.

## Error handling

| Case | Behaviour | Why |
|---|---|---|
| Catalogue read fails | that panel reports it; events and the rest still work | 020 R1.3, C7.1 |
| A fetch fails | the banner **names which one** | R6.2 — "failed to fetch" identifies nothing when several panels poll |
| A transient failure recovers | the banner clears | R6.3 — a stale error is worse than none |
| A node's catalog is unparseable | that node shows as **unknown hosts**, not omitted | Omitting it would hide the misconfiguration |
| An entity has no events | says so | R4.4 — an empty table reads like a loading failure |
| mTLS | catalogue and state degrade with the 020 reason; events still work | The recorder is in-process and needs no connection |

## What this design does not do

**It does not change what the recorder stores.** Its `name` dimension stays exactly as it is.
This changes how the dashboard *interprets* it — and adds a second, typed source beside it.

**It does not add write operations.** Unchanged from 020.

**It does not invent a protocol.** The catalogue comes from the node registry the broker already
maintains, read through the path 020 built.

## Cross-References

- `docs/features/020-dashboard-operations/design.md` — the read path this inherits and the views it re-frames
- `docs/features/006-heartbeat-service-registry/` — the registry the catalogue is assembled from
- `docs/features/015-recoverability/design.md` — why `hw.replies` exists
- `docs/features/017-node-decommissioning/design.md` — the retirement countdown the nodes view surfaces
- `docs/features/018-pubsub-unification/design.md` — the `{channel}@{group}` derivation R3.3 renders as nesting


---

## Engineering Review Findings (2026-08-09)

Eight decisions resolved interactively, recorded here so a future implementer does not
re-discover them.

### R-0A — T3 deferred: the observed node address is a separate feature

**Problem.** T3 changes a persisted binary framing (`NodeRegistration`) that has no version
byte, touches the protocol, and has unverified feasibility — Highway registers commands as
parameterless factories with no route to `RespServerSession.networkSender`.

**Decision.** Defer to its own feature with a framing spike. 022 is rendering + read-path
extension; no storage or protocol change.

### R-1A — Catalogue is the union of registry + recorder name index

**Problem.** `ToCatalogInfo()` reports only what a node hosts/subscribes/processes. An entity
nobody hosts (R3.4's "no live host" case) appears in no catalog and would be invisible.

**Decision.** Union of declared (registry, read via `IBrokerState`) and observed (recorder
name index, in-process). The recorder needs no connection, so it also works under mTLS where
the loopback path is unavailable.

### R-2A — `HasLiveHost` replaced by a state enum

```csharp
internal enum EntityState
{
    Live,            // declared by at least one live node
    HostStale,       // declared by a node, but all its nodes are stale/absent
    NeverDeclared,   // observed in traffic only — no node ever registered it
    Unknown          // unclassifiable
}
```

**Why.** The last two have opposite remedies — restart a node vs deploy something that was
never there — and a bool collapses them.

### R-3A — mTLS: catalogue degrades to recorder-only, with a banner

**Problem.** Under `ClientCertificateRequired` the loopback connection is unsupported, so the
nodes and catalogue views lose host information. Since entity pages are the only route to
events (R4.3), losing them would lose access to events too.

**Decision.** The catalogue is still populated (from the recorder name index, which is
in-process). Entities list and are navigable. Hosts and state show "unavailable — mTLS" with
a reason. Events remain reachable.

### R-4A — Error banner is a keyed error region, not `#broker-info`

**Root cause.** `#broker-info` holds both broker identity and error text. Only `loadRecorder`
rewrites it. After navigation (`showNameView` → `stopAutoRefresh`), a stale error remains
permanently above a working page — the exact symptom R6.1 reports.

**Fix.** A dedicated error region where each source (recorder, catalogue, nodes) owns an entry.
Success clears that entry. Broker identity is separate and never overwritten by a failure.

### R-5A — ES modules, one per view, shared helpers

**Problem.** 022 roughly triples the JS and adds per-view mutable state. One IIFE with eight
globals is the file that makes the next feature cost double.

**Constraint.** Feature 011's "no build step" holds.

**Shape.** `app.js` becomes the router/scheduler. Each view is an ES module (`nodes.js`,
`catalogue.js`, `entity.js`, `diagnostics.js`) with its own state and lifecycle. Shared helpers
(`fetch.js`, `render.js`) prevent DRY violations. `<script type="module">`.

### R-6A — Routing: kind and name as query params

**Problem.** `Identifier.IsValid` permits `/` (only rejects `< 0x20`, `0x7F`, `@`). So
`orders/create` is a legal name and multi-segment path routes become ambiguous.

**Fix.** `#/entity?kind=service&name=orders/create` — names never participate in path parsing.
Kind travels explicitly, which also satisfies R1.2.

Routes: `#/`, `#/nodes`, `#/catalogue`, `#/entity?kind=…&name=…`, `#/diagnostics`.

### R-7A — One scheduler; only the visible view polls

**Problem.** Three uncoordinated pollers (recorder, catalogue, nodes), each able to fire while
their view is hidden. Interval hardcoded at 3 s despite 020 Open Decision 2 promising it
configurable.

**Fix.** One `ViewScheduler`: keeps one timer, calls `refresh()` on the active view only. The
interval is configurable via `DashboardOptions.PollIntervalMs` (default 3000). Hidden views do
not poll — navigation starts/stops cleanly.

### Escape hatches taken

- `esc()` will handle `0`/`false` explicitly rather than rendering them as blank.
- An ASCII diagram will document entity assembly (registry → declared; recorder → observed; union → classify → state) in the catalogue reader's doc comment.
