# Tasks: Dashboard — A Catalogue, Not a List of Names

**T1 decides whether anything else is honest.** If the server does not classify entities, the
browser will, and a naming convention reimplemented in JavaScript is the drift this project keeps
finding.

**020's Phase 0 is a dependency, and it has shipped.** `IBrokerState`, `LoopbackConnection` and
the security matrix are the read path this feature reads through. Nothing here re-litigates them.

---

## Phase 1 — The model

### - [ ] T1 — The server classifies entities

`IBrokerState` gains a catalogue read returning `CatalogueEntryDto` — name, kind, parent
channel, hosts, and whether any host is live.

*Requirements:* R1.1, R1.2, R1.4, Open Decision 2
**Done when:** every entity carries a kind decided **on the server**, `Unknown` is a real member
used for anything unclassifiable, and no browser code parses a name to decide what it is.

`@` is reserved in user-declared names (018 T0) **because the server derives group-queue names
with it**. That is a server fact, and a `name.includes('@')` in JavaScript is a second
implementation of it that will be wrong the first time the convention moves.

### - [ ] T2 — Read the catalogue out of the node registry

`hw:reg:nodelist` → `hw:reg:node:{nodeId}` → `[lastSeenTicks][catalog json]`, which already
carries `services`, `channels` and `queues`.

*Requirements:* R3.1, R3.5
**Done when:** the catalogue is assembled from what nodes have **declared**, and "declared" is
distinguishable from "currently live".

**No new storage.** Every heartbeat since feature 006 has carried this and nothing has ever read
it back for display. That is the whole reason this feature is mostly rendering.

### - [ ] T3 — A node's observed address

*Requirements:* R2.4, Open Decision 1
**Done when:** the broker records the address it **observes** a node connecting from, and the
view labels it as an observation — "seen from 10.1.4.22" — not as a property of the node.

A node behind NAT, in a container, or scaled horizontally under one name reports an address that
is true and not useful. A field called `Address` implies otherwise; "seen from" does not.

---

## Phase 2 — The views

### - [ ] T4 — Nodes

*Requirements:* R2.1, R2.2, R2.3, R2.5
**Done when:** every registered node shows name, liveness **as an interpretation** ("live",
"stale 4m") rather than a raw timestamp, what it hosts, and — for a node past half
`SubscriberRetirementThreshold` — the countdown and its consequence.

**Nodes hosting nothing are still listed.** An empty catalog is usually a misconfiguration and is
invisible today.

The retirement countdown is the highest-value item here for the same reason it was in 020: 017
made retirement automatic, and it destroys a subscriber's entire backlog.

### - [ ] T5 — Catalogue

*Requirements:* R3.1, R3.2, R3.3, R3.4
**Done when:** services, queues and channels each list their hosts; navigation works **both
ways**; **a channel nests its groups**; and an entity with no live host is highlighted.

**Nesting is the single biggest readability fix.** `orders.placed` and `orders.placed@shop-1`
are one channel and one of its subscribers; listing them as peers is what makes today's page
unreadable, and it is a rendering decision rather than a data one.

**"No live host" is the row worth having.** A service nobody serves and a queue nobody consumes
are real failures that look identical to healthy ones — depth alone cannot tell "busy" from
"abandoned".

### - [ ] T6 — Entity pages, absorbing 020's views

*Requirements:* R4.1, R4.2, R4.3, R4.4, Open Decision 4
**Done when:** selecting a service, queue or channel shows its state, its dead letters and its
events on one page; a channel's page includes its groups' events; and the existing filters and
live stream still work, reached by navigating an entity rather than picking from a list of six
kinds of thing.

> **This supersedes 020 T6–T9**, which described four flat views. The same information belongs
> here. 020's Phase 0 stands unchanged — it is the read path this uses — and that feature's spec
> was written before the screenshot made this problem visible.

### - [ ] T7 — Internal names stop leaking

*Requirements:* R5.1, R5.2, R5.3
**Done when:** `hw.replies` and node ids are not presented as user entities, internal names are
marked and hidden by default with a way to show them, and the flat recorder index moves to a
diagnostics view.

`hw.replies` exists because feature 015 stopped RPC replies creating one recorder buffer per
request. Showing it to an operator displays a fix for a bug they never had.

**Node names as recorder names is a symptom, not the disease**: `HW.HEARTBEAT` records under the
node id. Once nodes are first-class (T4) they must not also appear as anonymous names.

---

## Phase 3 — Stop the page lying

### - [ ] T8 — The banner tells the truth

*Requirements:* R6.1, R6.2, R6.3
**Done when:** "Connection error: Failed to fetch" cannot appear above a page that loaded, a
failed fetch **names which** fetch failed, and a recovered failure clears the banner.

It is on screen today above a fully-rendered page. **An error message that is wrong is worse
than none** — it teaches the reader to ignore the banner that will one day be right.

---

## Phase 4 — Conformance

### - [ ] T9 — Protocol and constraints

*Requirements:* R7.1, R7.2
**Done when:** any new server surface is documented in the same change and
`ProtocolConformanceTests` is green. T3's observed address is the only likely candidate; the
catalogue itself adds none.

No constraint is expected to change. If one does, that is a finding worth stating rather than a
box to tick.

### - [ ] T10 — Samples and full verification

*Requirements:* R7.3, R7.4
**Done when:** the dashboard is exercised against the samples — two nodes, two services, a queue
and two channels with groups, which is exactly the deployment the screenshot came from — with a
`RUNLOG.md` entry, all tests passing, and `dotnet build --no-incremental` warning-free.

**Compare against the screenshot in `requirements.md`.** The before-and-after is the
verification: if the new page cannot be read at a glance where the old one could not, the feature
did not do its job.

---

## Parallelization

```
LANE 0   T1, T2, T3    the model            → blocks the views
LANE 1   T4, T5, T6    the views            → needs lane 0
LANE 2   T7, T8        leaks and honesty    → independent of lane 1, can run beside it
LANE 3   T9, T10       conformance          → last

Order: 0 → (1 ∥ 2) → 3
```

---

## The line that must not move

**The dashboard shows the system, not the recorder's index.** Every row says what it is, and
nothing is classified by parsing a string in a browser. The moment kind is inferred client-side,
this feature has reintroduced the problem it exists to fix — in a place where it is harder to
see.

**And nothing here writes.** Read-only, as 020 established. An operator destroying a dead-letter
list from a browser tab is a different threat model and needs its own feature.
