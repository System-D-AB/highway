# Tasks: Dashboard — A Catalogue, Not a List of Names

**T1 decides whether anything else is honest.** If the server does not classify entities, the
browser will, and a naming convention reimplemented in JavaScript is the drift this project keeps
finding.

**020's Phase 0 is a dependency, and it has shipped.** `IBrokerState`, `LoopbackConnection` and
the security matrix are the read path this feature reads through. Nothing here re-litigates them.

---

## Phase 1 — The model

### - [x] T1 — The server classifies entities

`IBrokerState` gains a catalogue read returning `CatalogueEntryDto` — name, kind, parent
channel, hosts, and **entity state** (`Live | HostStale | NeverDeclared | Unknown`).

*Requirements:* R1.1, R1.2, R1.4, Open Decision 2, Review R-2A
**Done when:** every entity carries a kind and state decided **on the server**, `Unknown` is a
real member used for anything unclassifiable, `NeverDeclared` covers entities seen in traffic
but never registered by any node (R3.4's "no live host" case), and no browser code parses a
name or infers provenance.

`@` is reserved in user-declared names (018 T0) **because the server derives group-queue names
with it**. That is a server fact, and a `name.includes('@')` in JavaScript is a second
implementation of it that will be wrong the first time the convention moves.

### - [x] T2 — Read the catalogue as a union of registry + recorder + **structures**

The catalogue is assembled from **two sources** (Review R-1A):

1. **Declared** — `hw:reg:nodelist` → `hw:reg:node:{nodeId}` → `[lastSeenTicks][catalog json]`,
   which carries services, channels and queues each node hosts. Read via `IBrokerState`.
2. **Observed** — the recorder's name index (in-process, needs no connection). Surfaces entities
   that were addressed in traffic but never declared — precisely the "no live host" case.

*Requirements:* R3.1, R3.4, R3.5, Review R-1A
**Done when:** an entity hosted by nobody but addressed in traffic (e.g. `payments.refund`
called by callers but never deployed) is **visible** in the catalogue with state
`NeverDeclared` — proven by a test that uses the existing samples' shape.

**No new storage.** All sources already exist. The union is computed at read time.

> **It is three sources, not two.** A **group queue** exists as a structure the moment a publish
> fans out — but nothing *declares* it, because its name is derived (018), and the recorder does
> not *observe* it until a subscriber claims from it. With only registry + recorder, a channel's
> groups were invisible until consumed, which is exactly when an operator least needs to see them
> and most needs to see them sitting there unconsumed. The live queue keys are the third source.
>
> Found by `AGroupNamesItsParentChannel` failing, not by reading the spec.

**A second finding, in the opposite direction:** a test asserting that a node with an unreadable
catalog is still listed **could not be written**, because `HW.HEARTBEAT` refuses to register
unparseable catalog JSON at the door. `Catalogue.ReadNode`'s tolerance is defence against a
corrupted record, not a case an operator can cause. The test now asserts the refusal.

Under mTLS (Review R-3A), the declared half is unavailable and the catalogue degrades to
recorder-only, with a banner naming the setting and the consequence.

### - [ ] T3 — ~~A node's observed address~~ **DEFERRED (review decision R-0A)**

> **Moved out of scope.** Changes a persisted binary framing (`NodeRegistration`) with no
> version byte and has unverified feasibility (commands have no access to session state).
> Registered in `constraints.md` § Deferred as a candidate for its own feature with a spike.

---

## Phase 2 — The views

> **Structural prerequisite (Review R-5A, R-6A, R-7A):** before building views, `app.js`
> becomes an ES module router/scheduler. Each view is its own module (`nodes.js`,
> `catalogue.js`, `entity.js`, `diagnostics.js`) with shared helpers (`fetch.js`,
> `render.js`). No build step — `<script type="module">`. Routes use query params for names
> (`#/entity?kind=service&name=…`) so `/` in identifiers is unambiguous (R-6A). One
> `ViewScheduler` drives polling for the active view only, at `DashboardOptions.PollIntervalMs`
> (default 3000) (R-7A). A keyed error region replaces the dual-purpose `#broker-info` (R-4A).

### - [x] T4 — Nodes

*Requirements:* R2.1, R2.2, R2.3, R2.5
**Done when:** every registered node shows name, liveness **as an interpretation** ("live",
"stale 4m") rather than a raw timestamp, what it hosts, and — for a node past half
`SubscriberRetirementThreshold` — the countdown and its consequence.

**Nodes hosting nothing are still listed.** An empty catalog is usually a misconfiguration and is
invisible today.

The retirement countdown is the highest-value item here for the same reason it was in 020: 017
made retirement automatic, and it destroys a subscriber's entire backlog.

### - [x] T5 — Catalogue

*Requirements:* R3.1, R3.2, R3.3, R3.4
**Done when:** services, queues and channels each list their hosts; navigation works **both
ways**; **a channel nests its groups**; and an entity with no live host is highlighted.

**Nesting is the single biggest readability fix.** `orders.placed` and `orders.placed@shop-1`
are one channel and one of its subscribers; listing them as peers is what makes today's page
unreadable, and it is a rendering decision rather than a data one.

**"No live host" is the row worth having.** A service nobody serves and a queue nobody consumes
are real failures that look identical to healthy ones — depth alone cannot tell "busy" from
"abandoned".

### - [x] T6 — Entity pages, absorbing 020's views

*Requirements:* R4.1, R4.2, R4.3, R4.4, Open Decision 4
**Done when:** selecting a service, queue or channel shows its state, its dead letters and its
events on one page; a channel's page includes its groups' events; and the existing filters and
live stream still work, reached by navigating an entity rather than picking from a list of six
kinds of thing.

> **This supersedes 020 T6–T9**, which described four flat views. The same information belongs
> here. 020's Phase 0 stands unchanged — it is the read path this uses — and that feature's spec
> was written before the screenshot made this problem visible.

### - [x] T7 — Internal names stop leaking

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

### - [x] T8 — The banner tells the truth

*Requirements:* R6.1, R6.2, R6.3, Review R-4A
**Done when:** "Connection error: Failed to fetch" cannot appear above a page that loaded, a
failed fetch **names which** fetch failed, a recovered failure clears its own entry, and broker
identity is never overwritten by a failure.

**Root cause (R-4A):** `#broker-info` holds both broker identity and error text, and only
`loadRecorder` rewrites it. After navigation the error is permanently stale. The fix is a
dedicated error region keyed by source — each poller (recorder, catalogue, nodes) owns an entry,
success clears that entry.

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
LANE 0   T1, T2        the model (union + classify)    → blocks the views
LANE 1   T4, T5, T6    the views                       → needs lane 0
LANE 2   T7, T8        leaks, honesty and structure    → independent of lane 1, can run beside it
LANE 3   T9, T10       conformance                     → last

Order: 0 → (1 ∥ 2) → 3

T3 is deferred (R-0A); it was the only protocol-changing task.
```

---

## The line that must not move

**The dashboard shows the system, not the recorder's index.** Every row says what it is, and
nothing is classified by parsing a string in a browser. The moment kind is inferred client-side,
this feature has reintroduced the problem it exists to fix — in a place where it is harder to
see.

**And nothing here writes.** Read-only, as 020 established. An operator destroying a dead-letter
list from a browser tab is a different threat model and needs its own feature.


---

## What execution found

**A gap in feature 014, not in the dashboard.** Running the new catalogue against the samples
showed `invoices.generate` as `Unknown` / `NeverDeclared` while the order service was actively
processing it. The cause: **`ImmutableCatalog.ToCatalogInfo()` never populated `Queues`.**

014 added the `queues` property to `CatalogInfo` — with a comment about backward compatibility —
and never filled it in. So since 014 the node registry has been blind to queues: `HW.DISCOVER`
could not answer "who processes this queue?", and nothing noticed because nothing read the
catalog back until this feature did.

Before: `invoices.generate  Unknown  NeverDeclared`
After: `invoices.generate  Queue  Live  order-service-1`

**This is the strongest argument for the feature.** A view that shows what is *supposed* to be
true is how you discover that it is not.

**The catalogue needed a third source**, recorded under T2: declared (registry) + observed
(recorder) + **structures** (live queue keys). A group queue exists the moment a publish fans
out, but nothing declares it and the recorder does not see it until a subscriber claims — so
without the structures a channel's groups were invisible precisely while piling up unconsumed.

**A departed node reads honestly.** After the storefront quits, `shop-1` and `orders.placed`
show `NeverDeclared`: the `BYE` removed the registration, so nothing declares them any more.
That is correct, and it is the first time the dashboard could express it.

## Not done

**T9/T10 conformance is partial.** The samples were exercised and the API verified against the
real deployment, but there is no `RUNLOG.md` entry for this run and the front-end has no
automated coverage — the modules are verified by having been driven against a live broker, not
by tests. A headless-browser harness is a tooling decision this project has never had to make,
and it should be made deliberately rather than in the tail of a feature.
