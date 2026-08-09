# Feature: Dashboard — A Catalogue, Not a List of Names

## Introduction

### The evidence

This is the dashboard's main page against a running samples deployment:

```
Recorded Names
  Name                              Events   Bytes   Capture   Dropped
  shop-1                                 1   152 B   Full      0
  orders.placed                          2   446 B   Full      0
  inventory.low@order-service-1          2   436 B   Full      0
  hw.replies                             2   786 B   Full      0
  inventory.low                          2   435 B   Full      0
  orders.get                             3   784 B   Full      0
  orders.placed@shop-1                   2   364 B   Full      0
  orders.create                          3   826 B   Full      0
  invoices.generate                      3   838 B   Full      0
  order-service-1                        1   188 B   Full      0
```

Ten rows, one column called **Name**, and **six different kinds of thing**:

| Rows | What they actually are |
|---|---|
| `shop-1`, `order-service-1` | **nodes** |
| `orders.get`, `orders.create` | **services** (RPC) |
| `invoices.generate` | **a queue** |
| `orders.placed`, `inventory.low` | **channels** |
| `orders.placed@shop-1`, `inventory.low@order-service-1` | **group queues** — the same channels again, per subscriber |
| `hw.replies` | **an internal bucket**, not a user concept at all |

Nothing distinguishes them. An operator cannot tell which row is a machine and which is a
message type, cannot see that `orders.placed` and `orders.placed@shop-1` are the same channel
from two angles, and cannot answer the first question anyone asks: **what is running, and where?**

### The diagnosis

**The dashboard shows the flight recorder's index, and calls it the system.** The recorder keys
its buffers by an arbitrary `name` string, because that is all it needs to do its job. Every
command records under whatever name it has — a service, a channel, a node id, a derived group
queue — and the dashboard renders that dictionary directly.

That is a faithful view of the recorder and a useless view of the broker.

### What this feature does

**Introduces the entity model the product already has** — nodes, services, queues, channels,
and the groups within them — and makes the dashboard navigate it:

```
Nodes ──────────► what is running, since when, and what it hosts
   │
Catalogue ──────► every service, queue and channel, and which nodes serve it
   │
Entity ─────────► one service / queue / channel: its state, and its events
```

**The server already knows all of this.** `hw:reg:node:{nodeId}` holds
`[lastSeen][catalog json]`, and that catalog carries the node's services, channels and queues —
written by every heartbeat since feature 006. The catalogue view is almost entirely a rendering
problem, not a protocol one.

### Its relationship to 020

Feature 020 is in flight and its Phase 0 has shipped: `IBrokerState`, the security matrix, and
the `LoopbackConnection` that makes a self-connection safe. **That work is the data layer and it
stands.**

020's remaining view tasks (T6–T9) assume four flat views bolted beside the existing name list.
**This feature supersedes that framing**, because building four flat views and then restructuring
them into an entity model is waste. See [Sequencing](#sequencing).

## Requirements

### Requirement 1: Every Row Says What It Is

**User Story:** As an operator, I want to know whether I am looking at a machine or a message type.

#### Acceptance Criteria

1. Every entity the dashboard shows carries an explicit **kind**: node, service, queue, channel, or subscriber group
2. Kind is **derived on the server**, not guessed in the browser from a naming convention. `name.Contains('@')` is a rule that will be wrong the moment a queue is legitimately named with one
3. The flat "Recorded Names" list is **replaced**, not supplemented. Leaving it beside a typed view means two answers to the same question, and the wrong one is easier to read
4. An entity the server cannot classify is shown as **unknown**, explicitly, rather than silently filed under a plausible kind

### Requirement 2: Nodes Are First-Class

**User Story:** As an operator, my first question is what is running, and my second is whether it is healthy.

#### Acceptance Criteria

1. A nodes view lists every registered node with: name, last-seen, liveness (live / stale / absent against `NodeExpiry`), and what it hosts — services, queues, subscribed channels
2. **Liveness is shown as an interpretation, not a timestamp.** "Last seen 14:02:11" makes an operator do arithmetic; "live" or "stale for 4m" does not
3. A node approaching `SubscriberRetirementThreshold` is flagged, with the consequence stated: its subscriber queues will be destroyed (017)
4. **Node identity beyond the name is included where Highway knows it** — see Open Decision 1, because today Highway records only a name
5. Nodes that are registered but hosting nothing are still listed. A node with an empty catalog is usually a misconfiguration, and it is invisible today

### Requirement 3: The Catalogue Shows What Serves What

**User Story:** As an operator, I want to see which nodes handle a service, and which services a node handles, without reading two pages and joining them in my head.

#### Acceptance Criteria

1. A catalogue view lists every **service**, **queue** and **channel** the broker knows, each with the nodes that host it
2. It is navigable **both ways**: from a node to what it hosts, and from an entity to who hosts it
3. **A channel shows its subscriber groups as children**, not as siblings. `orders.placed` and `orders.placed@shop-1` are one channel and one of its subscribers; listing them as peers is what makes the current page unreadable
4. An entity with **no live host** is highlighted. A service nobody serves and a queue nobody consumes are both real failures, and both look identical to a healthy one today
5. The catalogue is assembled from the node registry, so it reflects **what nodes have declared**, and the distinction between "declared" and "currently live" is visible rather than blurred

### Requirement 4: Events Belong To Entities

**User Story:** As an operator looking at a queue, I want that queue's events, not a search box.

#### Acceptance Criteria

1. Selecting a service, queue or channel shows **its** events, its state, and its dead letters together on one page
2. A channel's page includes its groups' events, because a subscriber's failures are the channel's failures from the operator's point of view
3. The existing per-name event view, filters and live stream are **kept** — they work; what changes is that they are reached by navigating an entity rather than by picking a name from a list of six kinds of thing
4. An entity with no recorded events says so, rather than showing an empty table that reads like a loading failure

### Requirement 5: Internal Names Stop Leaking

**User Story:** As an operator, I do not want to see the broker's implementation in a list of my things.

#### Acceptance Criteria

1. `hw.replies` — the reserved bucket feature 015 introduced to stop one recorder buffer per RPC reply — is **not** presented as a user entity. It is broker-internal and belongs, at most, in a diagnostics section
2. **Node names appearing as recorder names is a symptom, not the disease.** `HW.HEARTBEAT` records under the node id, so every node becomes a "recorded name". Once nodes are first-class (R2) they are shown as nodes; they must not also appear as anonymous names
3. Any name the server considers internal is marked as such, and hidden by default with a way to show it. Hiding it irreversibly would make the recorder harder to debug, which is the one job it has

### Requirement 6: The Page Stops Lying About Its Own Health

**User Story:** As an operator, I want to trust the error banner.

#### Acceptance Criteria

1. **"Connection error: Failed to fetch" must not appear above a page that loaded successfully.** It does today, and an error message that is wrong is worse than none — it trains the reader to ignore the banner that will one day be right
2. A failed fetch names **which** fetch failed. With several panels polling, "failed to fetch" identifies nothing
3. A transient failure that recovers clears the banner

### Requirement 7: Conformance

#### Acceptance Criteria

1. Any new server surface is documented in `docs/HIGHWAY-PROTOCOL.md` in the same change, and `ProtocolConformanceTests` stays green
2. `constraints.md` is updated if any guarantee changes; this feature is expected to change none
3. The dashboard is exercised against the samples, which host two nodes with services, a queue and channels — the exact shape the screenshot above came from — with a `RUNLOG.md` entry
4. All tests pass; `dotnet build --no-incremental` warning-free

## Open Decisions

### 1. Does Highway record a node's address?

Today it records a **name** and nothing else. R2.4 wants identity an operator can act on — which
host is that, actually?

- *Add an endpoint to the registration.* The client knows its own hostname; the server could also
  observe the connection's remote address. Cheap, and it is real operational information.
- *Do not.* A node name is already operator-chosen and can encode whatever they need. Adding an
  address invites a false sense of precision when nodes are behind NAT, in containers, or scaled
  horizontally under one name.

**Recommendation: record the address the broker actually sees, and label it as such** — "seen
from 10.1.4.22" is honest about being an observation rather than a declaration. A node name is
what the operator chose; the address is what is true.

### 2. Where does kind come from?

R1.2 forbids guessing in the browser. So either the server classifies and returns kind with each
entity, or the dashboard derives it from the registry it is already reading.

**Recommendation: the server classifies.** It has the registry, it owns `HighwayKeys`, and it is
the only place that knows `@` is a derived-name separator rather than a legal character. A
browser rule would be a second implementation of a naming convention.

### 3. Is the flat name list removed, or kept behind a toggle?

R1.3 says replaced. But the recorder's own index is genuinely useful when debugging the
recorder.

**Recommendation: removed from the main page, available under a diagnostics view.** The
distinction is who it is for — an operator navigating their system, versus someone debugging
Highway itself.

### 4. What happens to 020's remaining view tasks?

**Recommendation: fold them into this feature's entity pages.** 020's Phase 0 (read path,
security matrix) is the data layer and stands unchanged. Its T6–T9 describe four flat views; the
same information belongs on entity pages instead. Building them flat and restructuring later is
the waste this decision avoids.

## Sequencing

```
020 Phase 0   ✅ shipped — IBrokerState, LoopbackConnection, security matrix
020 T4, T5    HW.STATS additions; Detail + severity      ← still needed, unchanged
022           the entity model and the views              ← replaces 020 T6–T9
020 T10–T12   conformance                                 ← folded in here
```

**This feature does not restart 020.** It re-frames its remaining UI work around the model the
product already has, and inherits the read path 020 built and proved.

## Non-Goals

- **Write operations.** Unchanged from 020: no requeue, purge or retire buttons. Read-only first.
- **A new protocol for the catalogue.** The registry already carries it; if a command is needed it extends `HW.STATS` or `HW.DISCOVER` rather than inventing a third surface.
- **Historical charting.** The flight recorder is explicitly volatile (002).
- **Topology across brokers.** Highway is a single broker (`constraints.md` C5).
- **Changing what the recorder stores.** Its `name` dimension stays as it is; this feature changes how the dashboard *interprets* it, not what is written.

## Cross-References

- `docs/features/011-dashboard-flight-recorder/` — the dashboard this restructures
- `docs/features/020-dashboard-operations/` — the read path this inherits, and the view tasks this supersedes
- `docs/features/006-heartbeat-service-registry/` — the node registry the catalogue is assembled from
- `docs/features/015-recoverability/design.md` — why `hw.replies` exists, and why it must not be a user-facing name
- `docs/features/018-pubsub-unification/design.md` — why a group queue is named `{channel}@{group}`, which R3.3 renders as a parent/child relationship
