# Brainstorming — API-surface review and the "do nothing" decision

*A record of design discussions that have not (yet) become features. Like `research.md`, this
documents what was believed at the time it was written; unlike `research.md`, nothing here is a
decision until it either becomes a numbered feature or an entry in `constraints.md`. When one of
these observations is acted on, add a dated pointer here rather than deleting the analysis.*

---

## 2026-08-09 — Critical review of the public API surface

*Context: after feature 023, before any production deployment. The question posed: read the
library class shapes as a senior architect — what debt do the class annotations carry, is
Highway usable for a medium project (tens of services, channels, queues), do circular
references matter in a distributed system, and can a developer hold the mental model when every
node is simultaneously host, publisher, subscriber, invoker and processor?*

### What holds up

- **Address on the contract, not the handler.** `[Service]` / `[Queue]` / `[Channel]` sit on the
  message type, names are explicit and never inferred from type names —
  `QueueAttribute` documents *why* (renaming a class must not strand a durable queue). This part
  will age well.
- **The verb triad is the mental model**: *Execute a verb, Publish a fact, Send a job.* One
  handler → `SendAsync`; many handlers → `PublishAsync`; need the answer → `ExecuteAsync`. The
  deployment consequence (three processors **share**; three subscribers each get a **copy**) is
  the point of having both.
- **The contracts-assembly pattern** (reference only `Highway.Abstractions`, no transitive deps)
  is exactly right for many services, and it dissolves assembly-level circular references by
  construction: node A and node B never reference each other, both reference `Contracts`.

### Findings

**F1 — Scanner silently drops multi-interface handlers. This is a defect, not debt.**
`DefaultTypeScanner` uses `GetInterfaces().FirstOrDefault(...)` for both `ISubscribe<>` and
`IProcess<>`. A class implementing `ISubscribe<OrderPlaced>` **and** `ISubscribe<InventoryLow>`
registers only one — no error, no warning, one channel never fires. Writing one handler class
for several events is the natural thing to do at 10+ channels, and its failure mode is invisible
even to the dashboard, because the node never declares the dropped subscription.
→ *Fixed 2026-08-10, with the failure-boundary set (no feature spec, by user instruction): the scanner now registers every closed interface, verified by tests run against the broken code first. Same change set fixed concerns.md 9.1 (DI activation outside the RPC error mapping), 9.2 (response-serialization failure re-running completed handlers), 9.3 (stack traces sent to remote callers), 5.7 (the `[Queue]` struct contradiction), and built the A1 AOF registration-manifest guard.*

**F2 — Handler discovery is asymmetric.** RPC needs a **base class** (`AsyncService<TReq,TRes>`,
burning the class's only C# base); subscribe and process are **interfaces**. Three roles, three
mechanisms, no stated reason. Fixable *additively* — an `IExecute<TReq,TRes>` interface can live
beside `AsyncService` without breaking anyone.

**F3 — Attributes freeze behaviour into contracts.** `[Idempotent(WindowSeconds = 300)]` is a
tuning knob compiled into the contract assembly: changing a dedup window means redeploying every
node that references the contract, including send-only nodes. Names belong on contracts; tuning
does not. Fixable additively (server-side override).

**F4 — Attribute placement is folklore.** `[Service]`/`[Queue]`/`[Channel]`/`[Idempotent]` go on
the *contract*; `[ServiceLifetime]` goes on the *implementation*. Defensible, but nothing guards
it — a misplaced attribute is silently inert. The standard remedy for attribute-convention
frameworks is a **Roslyn analyzer package**; Highway has none, so every contract mistake is a
startup-time error instead of a red squiggle.

**F5 — `Output` forces mutable response contracts.** `where TResponse : Output` with settable
`StatusCode` plus the parameterless-constructor requirement means responses cannot be positional
records while requests can. Contract assemblies end up stylistically split (the samples show it:
`GenerateInvoice` is a record, `OrderResult` cannot be).

**F6 — Name collisions are detected per node only.** `ServiceWithSameNameAlreadyExistsException`
fires within one scan. Two different applications can declare `[Service("orders.create")]` with
*different contracts* and the broker routes between them; the wire is JSON, so the mismatch
surfaces as silently-defaulted properties. A multi-team failure mode.

**F7 — No contract-evolution story, against durable storage.** Queues and pub/sub are durable. A
message serialized under v1 of a contract waits in a queue; the processor redeploys with v2;
`System.Text.Json` defaults the missing properties **silently**. Renaming a property on a
`[Queue]`/`[Channel]` type is a data-loss operation and nothing says so. The largest unpriced
debt in the design; invisible until the first production rename.

**F8 — Message-level cycles have no guard.** Assembly cycles are solved (see above); message
cycles are not. A handler that publishes something whose handler eventually re-publishes the
original is the distributed equivalent of infinite recursion — and because pub/sub is durable it
*persists and amplifies* rather than just spinning. The envelope carries `src`, id, attempts —
**no hop count, no causation id**. Nothing prevents, detects, or displays a loop; a synchronous
RPC cycle degenerates to per-hop timeouts diagnosed by waiting. The entry framing is versioned
(`0xFF`), so a hop count can be added later cleanly.

**F9 — Hosting is implicit in the reference closure. The mental-model break.**
`DefaultAssemblySource` deliberately walks the entry assembly's transitive references (that walk
is load-bearing for *contracts* — it fixed the caller-only `SERVICE_NOT_FOUND` defect). But it
also means **you host whatever your references contain**: reference a library for one helper
class and, if it contains an `IProcess<>` implementation, your app silently becomes a competing
processor; for a subscriber, a duplicated side effect (two apps both send the email). "Which app
processes invoices?" cannot be answered from any single file — the answer is a property of the
dependency graph. The dashboard catalogue shows the accident *after* it happens; nothing
prevents it.

### Conventions proposed (not yet features)

1. **Contracts assemblies are inert** — contracts reference only `Highway.Abstractions` and
   contain no handlers; handlers live in app projects or explicitly-declared host libraries.
   Enforceable at scan time: an assembly containing both contract types and handlers is a
   startup error naming the split. Makes "who hosts X?" answerable by looking at the app
   project.
2. **Explicit hosting opt-in for libraries** — entry assembly hosts by default; *referenced*
   assemblies host only if marked (e.g. `[assembly: HighwayHost]`). Contract discovery keeps
   walking the full closure — that magic is correct; only the handler half hurts.
3. **A naming grammar, enforced at scan time** — services imperative `context.noun.verb`
   (`orders.create`), channels past-tense facts (`orders.placed`), queues imperative work
   (`invoices.generate`). The samples already follow it; enforced, the name alone tells a reader
   which verb they are holding.
4. **An analyzer package** moving startup-time contract errors to compile time.

---

## 2026-08-09 — What if we do nothing and ship as-is?

*The counter-question, asked deliberately: no drops, no redos — Highway goes to production
unchanged. The findings above are not equal; they differ in when they bite, how loudly, and
whether waiting raises the price.*

### Triage

| Finding | If shipped as-is | When it bites | Loud or silent? | Cost later vs now |
|---|---|---|---|---|
| F1 scanner drop | Second subscription never fires | First multi-interface handler | **Silent** | Same; the *incident* is the cost |
| F7 rename on durable queue | Queued messages half-deserialize | First refactor after real traffic | **Silent** | Every pre-rule message is exposed |
| F9 implicit hosting | App becomes competing processor via a `.csproj` line | Second app references a handler library | Semi-loud — dashboard shows it, if someone looks | **Grows** — opt-in later is a behaviour break |
| F8 message cycles | Durable publish loop amplifies | Low until handlers publish from handlers (the sample already does) | Loud-ish — recorder fills | Envelope versioned; hop count addable later |
| F6 cross-app collision | Same name, different contracts, silent half-deserialization | Multi-team scale | Silent | Same |
| F2/F4/F5 API shape | Nothing fails; developers grumble | Never "bites" — friction | n/a | **Grows most** — public API hardens with every consumer; all fixable additively |
| F3 window in attribute | Tuning requires contract redeploy | First ops tuning request | Loud (annoying) | Additive later |
| No analyzer | Contract mistakes stay startup errors | Continuous low-grade | Loud (fail-fast works) | Zero — additive whenever |

### The case for doing nothing

- **Production is where the compensating controls live.** Features 020–023 built exactly the
  thing that makes "ship and watch" viable: `NeverDeclared` catches typo'd names, the node page
  shows who actually hosts what, the message view shows what really happened. The
  implicit-hosting accident is *visible the moment it happens* — two nodes appear against one
  queue.
- **Ergonomic debt doesn't page anyone.** F2/F4/F5 cost onboarding minutes, not data. Every
  framework in history has shipped equivalents.
- **Real traffic teaches better than review.** Highway's entire history is defects found by
  running the thing. Fixing the mental model before watching real teams misuse it risks
  building the wrong convention.
- **Every deferral has a structural escape hatch.** The envelope is versioned; the scanner is
  one class behind an interface; the analyzer is a new package; the uniform handler interface
  can sit beside the base class. Nothing deferred requires a rewrite later.

### The case against — what survives the triage

Three items, sharing one property: **they fail silently.** Loud failures self-report; silent
ones are discovered as corrupted business data with no error attached, weeks later.

1. **F1 is a bug, not a trade-off.** "Do nothing" for a design trade-off is a strategy; for a
   known silent-drop defect it is just shipping a known bug. One line plus tests, inside the
   client engine's normal test discipline.
2. **F7's mitigation costs a paragraph.** No versioning machinery — a written rule ("contracts
   are additive-only; renaming a property on a durable contract is a data-loss operation") in
   the steering docs. Zero code. Exposure otherwise grows with every message that crosses a
   deploy.
3. **Deferral must be registered, because that is Highway's own law.** A gap is either a defect
   or a planned feature, never a silent difference (`constraints.md`). "Do nothing" done
   properly is dated Deferred entries for F6, F8, F9 and the API-shape items — so the eventual
   incident review finds "known, deferred, here's why" instead of a surprise.

### Recommendation as of this discussion

Ship as-is, **minus the bug, plus the paperwork**: fix F1; write the additive-only rule and the
contracts-stay-inert convention into the steering docs as documentation only; register the rest
in `constraints.md` Deferred. Everything architectural — hosting opt-in, naming enforcement, hop
counts, the analyzer, API-shape cleanups — waits for production to say which of them matters.

If a feature is cut from this later, the suggested shape was **024-conventions-and-hosting**
(mental-model cluster, F1 as Phase 0) with message safety (F8 + F7 machinery) as a separate
future feature rather than a bundle.

---

## 2026-08-10 — Substrate strategy: eight sessions, one decision

*A multi-session discussion of Highway's foundations: could Highway be built without Garnet
(ZeroMQ, Orleans, Akka.NET, EventStoreDB, SQLite, Tsavorite+Kestrel were each priced); should
Garnet or SE.Redis be forked, stripped, or absorbed; how deep does the RESP coupling run; and
how do pinned dependencies earn production trust. Full reasoning lives in the conversation;
what follows is what must not be lost.*

### Findings that shaped everything

- **The 24-verb seam already exists.** `IHighwayConnection` has 24 semantic members; every
  `HW.*`/RESP call site lives in the one implementing class. The client engine is
  substrate-agnostic *today*. Three RESP-isms leak into the interface (reply-slot get/delete,
  doorbell subscriptions as a side channel, `StatsAsync` mirroring `HW.STATS`) and should be
  generalized while there is one implementation.
- **RESP-the-framing is separable from Redis-the-semantics** (precedent: Kvrocks, Tile38,
  Dragonfly). Declaring "RESP framing + `HW.*` + AUTH/PING" as Highway's native protocol makes
  every future substrate a server-side implementation detail — no client changes, conformance
  tests become the cross-substrate suite. The gap is ~5 commands wrapping the protocol doc's
  "Stock Garnet Dependencies" table. Accepted cost: RESP + SE.Redis's multiplexer caps delivery
  at doorbell+poll (B3 stays) until a dedicated-connection native driver exists.
- **You cannot subtract your way to ownership.** Measured against the pinned submodule:
  stripping Garnet to Highway's needs removes ~60k mechanical lines (`server/Resp` ~40k,
  cluster ~20k — already disabled) and keeps ~88k of the hardest code (Tsavorite core ~55k,
  session/AOF/network glue ~33k) as an orphaned copy, cut off from exactly the upstream fixes
  that matter (the pinned commit is itself a LightEpoch CAS hardening fix). Same arithmetic for
  SE.Redis: the command surface is the cheap part; the multiplexer core is the hard part and
  the wrong shape besides — a stripped multiplexer still cannot do blocking/streaming claims.
- **There is no Highway AOF.** Durability is Garnet's AOF behind one flag; structures are the
  state, the log is the survival plan, and they cannot be merged. Real kernel of the concern:
  Garnet's AOF is indiscriminate, so ephemera (reply slots, leases, idempotency markers) pay
  the same logging cost as durable queue messages. A two-tier durable/ephemeral store is v2
  material, unlocked by any future substrate move.
- **Pinned dependencies are certified by envelope, not by faith**: 340 integration tests
  already run against the real embedded Garnet every build (Layers 1–2 of the assurance case).
  The gap is Layer 3 — crash-recovery under load, disk-full injection, connection churn,
  multi-day soak (`AofGrowthTests.SustainedTraffic` is currently skipped) — plus a bump
  cadence and a recovery runbook.

### The decision

**Keep Garnet pristine and pinned; keep SE.Redis as a NuGet reference; modify neither, strip
neither. Ship v1, run production for months, learn.** Garnet is the first *binding* of
Highway, not its definition; SE.Redis is the first driver. Both are positions held behind
seams, not commitments.

Accompanying actions (all additive, none touch dependency code): (1) protocol-completion
feature — RESP+`HW.*` declared native, ~5 absorbing commands, per-node reply doorbells (fixes
B2); (2) A1 AOF registration-manifest guard; (3) command allowlist; (4) `Meter` beside the
`ActivitySource` (C2); (5) the Layer-3 assurance rig; (6) runbook + quarterly pin-bump
cadence; (7) seam hygiene — generalize the three RESP-isms, grow semantic conformance against
`constraints.md`; (8) the F1 scanner fix and the two documentation rules from the do-nothing
triage above.

Revisit triggers, in place of a schedule: push/streaming delivery becomes a requirement →
build the small dedicated-connection driver (never vendor SE.Redis); A2/C1 pain grows →
upstream contributions first, minimal rebaseable patch queue second, hard fork only if a
structural change (named-operation AOF) is refused upstream; SE.Redis diamond-dependency
conflict → same native driver; production shows AOF cost → commit knobs, then two-tier
storage in v2. The endgame, if ever triggered, is the client-first server: `HW.*` over RESP
framing, push delivery, two-tier storage, over Tsavorite-as-NuGet — reachable as a two-adapter
swap precisely because of the seams above.


---

## 2026-08-10 — The third review, its verdicts, and the two specs it produced

*A third independent review (pasted during UserGuide work) re-derived the hosting and
identity findings and added five proposals. Verdicts, recorded so they are not re-argued:*

- **Adopted into `024-hosting-boundaries`**: contracts-only scanning (it is the hosting
  opt-in mechanism); the topology manifest — with the honesty rule that the consumption half
  is labelled *"can use"*, since a referenced contract proves addressability, not calling.
- **Adopted, already established**: `SubscriptionGroup` split → `025-subscription-groups`;
  hop-count TTL + causation id → message-safety register; DAG-for-`ExecuteAsync` as a
  documented rule.
- **Rejected — `[assembly: HighwayRole(...)]`**: outbound roles are unenforceable (any code
  holding `IHighwayClient` can publish) and the enforceable fraction is the hosting modes.
- **Rejected — `[ProducedBy]`/`[ConsumedBy]` markers**: deployment facts asserted in
  contract assemblies drift and then lie with authority; the runtime derives the truth
  (catalogue declared-vs-observed) and the manifest generates diagrams from code.
- **Rejected — static RPC-cycle detection at startup**: not knowable from the catalog.
  Referencing a contract is not calling it; every process referencing a shared contracts
  package would flag as calling everything. Cycle detection needs the causation id, a
  runtime fact.

**The best evidence in the review was its own error.** It stated Highway's identity rule as
*"same NodeName = compete, different = replicate"* — wrong on both halves: Execute/Send
compete across distinct names automatically; subscribers always replicate per name; sharing
a name is invalid (it collides processing lists, leases, heartbeats, 017 retirement). A
careful reviewer mis-learning the rule *while writing about the mental model* is the
argument for 025 — which makes the rule they assumed actually true — and for writing the
four rules into the UserGuide with rule 3 corrected (024 T7).

**Specs produced:** `docs/features/024-hosting-boundaries/` (consent-based handler hosting,
Implicit-mode accident warning, topology manifest, can-use half to the dashboard, the four
rules) and `docs/features/025-subscription-groups/` (claimant-is-the-group design forced by
Prepare-declarability, membership mirror, group-aware retirement, last-member purge).


---

## 2026-08-18 — Logical application responsibility and functional dependency cycles

*Watermark: GPT5.6 SOl — architectural review note.*

*Context: immediately before production, the question was whether Highway should make every
node adopt a responsibility convention, and whether functional cycles — not DLL references,
but flows such as application A calling application B by RPC and B calling A — should be
forbidden. The goal is to keep Highway simple for ordinary .NET developers without making an
event-driven system's topology invisible or allowing durable feedback loops to grow forever.*

### What Highway already knows

Feature 024 built the correct first layer, and it should be preserved:

- **Inbound responsibility is knowable.** `HostingMode.Declared` hosts handlers from the entry
  assembly plus explicitly declared modules; `ExplicitOnly` requires every handler assembly to
  be declared. `HostAssembly(...)` and `[assembly: HighwayHostModule]` are the two consent
  mechanisms. `Implicit` remains the default and warns when a referenced assembly contributes
  handlers without consent.
- **Each process has a topology manifest.** `PROVIDES` names its hosted RPC services, queue
  processors, subscribers and recurring jobs. `CAN USE` names RPC, queue and channel contracts
  found through references. The manifest is logged at startup, exposed through
  `IHighwayEngine.Topology`, sent in the heartbeat catalog and rendered on the node page.
- **The honesty label matters.** `CAN USE` is addressability, not proof of calling. A process
  referencing a contracts package can address its routes even when no line of its code ever
  calls them. A shared catch-all contracts package can therefore make every node appear to be
  able to use everything.
- **The contracts-assembly convention solves the binary problem.** Contract libraries reference
  only `Highway.Abstractions` and contain no handlers. Applications depend on contracts, never
  on each other's executable or implementation assemblies.
- **Operations can be observed, but lineage cannot yet be reconstructed.** The flight recorder
  and `ActivitySource` show individual operations. The envelope has trace context, source and
  message identity, but no durable root-causation identity, hop count or handler-to-child edge
  from which Highway could prove a functional cycle.

This means Highway can accurately answer *"what can this deployment receive?"* and can offer a
conservative hint for *"what could it send?"* It cannot yet answer *"what does this handler
actually call?"* Static cycle detection over `CAN USE` would produce false positives and must
not be presented as authoritative.

### Current gaps in that foundation

Two implementation/documentation defects should be fixed before the topology manifest is treated
as authoritative guidance:

1. `TopologyManifest.Build` still writes `NodeName` as every subscriber's group even after
   feature 025 introduced `EffectiveSubscriptionGroup`. A replica configured with
   `SubscriptionGroup = "billing"` is therefore described incorrectly.
2. Feature 024 T7 is checked complete, but the current UserGuide has no `HostingMode`, hosting
   consent or topology-manifest section. The protection exists in code and XML documentation but
   is missing from the path most developers will read.

There is also a deliberate limitation rather than a defect: `TopologyManifest.CanUse` excludes
routes the process itself provides. That keeps the startup block readable, but it means the
manifest cannot be reused unchanged as a complete dependency or self-call graph.

### Decision: responsibility means a business capability, not a transport role

Every **logical application** should own one coherent bounded business responsibility. A node is
a physical instance or replica of that application; it should not be modelled as a
"publisher node", "subscriber node" or "processor node".

An Orders application may legitimately provide order RPC operations, process order jobs,
publish order facts and subscribe to payment facts. Splitting those by messaging interface would
turn one cohesive capability into several tiny deployments and make the system harder, not
simpler. The convention is therefore:

> One logical application owns one business capability and explicitly hosts its inbound routes.
> It may use every Highway verb needed to fulfil that responsibility.

Do not add `[HighwayRole(Publisher)]`, `[ProducedBy]` or `[ConsumedBy]` declarations. They either
cannot be enforced — any code holding `IHighwayClient` can send — or become deployment claims in
contract packages that drift and eventually lie. Hosting consent is enforceable; outbound role
labels are not.

### Decision: synchronous RPC dependencies form a DAG

The logical-application RPC graph should be acyclic by convention:

```
BAD

Orders  -- RPC, waits -->  Payments
   ^                         |
   +------ RPC, waits -------+
```

This rule applies even when the reverse call occurs in a different method rather than in the
same request. Bidirectional synchronous ownership creates circular availability requirements,
multiplies latency and timeout budgets, amplifies retries, encourages worker-pool starvation and
makes neither service independently understandable or operable. A same-request cycle is worse:
it becomes distributed recursion and normally ends only when a timeout breaks it.

When B needs information from A in order to answer A:

1. put the required information in A's original request;
2. move the decision to the application that owns the data and invariant;
3. extract a third capability both may depend on; or
4. make B's later outcome an event instead of a synchronous call back to A.

Highway should continue to be technically capable of making a reverse RPC call. The catalog does
not know the real call graph, migrations and integration adapters sometimes need temporary
exceptions, and a universal protocol-level ban would reject valid systems. The default design
guidance should nevertheless treat an RPC cycle as an architecture error, and optional strict
tooling may enforce that rule where the application declares enough truth to do so.

### Decision: asynchronous topology cycles are allowed; causal loops are not

A directed cycle on a topology diagram is not automatically a defect. Feature 032's
`Edge -> UserSignedUp -> Notifications -> EmailDispatched -> Edge` flow is reasonable: the
messages are different facts, state advances and the causal chain terminates.

```
GOOD

Orders  -- OrderPlaced -->  Billing
Orders  <-- InvoiceIssued -- Billing
```

An asynchronous cycle is acceptable only when all of these are true:

- each message represents a distinct state transition rather than an echo of its input;
- one application clearly owns the workflow or aggregate state;
- processing is idempotent under Highway's at-least-once delivery;
- progress is monotonic toward a named terminal state;
- an incoming message cannot blindly reproduce an ancestor message; and
- the causal chain has a bounded hop budget and a loud terminal outcome when that budget is
  exhausted.

The dangerous form is `A receives X -> publishes Y; B receives Y -> publishes X`. With durable
queues this is not ordinary recursion: it persists, retries and can amplify after the original
caller has gone away. It must be prevented or dead-lettered, not merely displayed after damage.
The same rule applies to two applications sending jobs back and forth.

### Production convention using today's library

The safe, still-simple default for a production application is:

```csharp
services.AddHighway(options =>
{
    options.NodeName = "orders-1";
    options.HostingMode = HostingMode.Declared;
});
```

Handlers should normally live in the entry application assembly, so `Declared` adds one option
and no per-handler registration. Use `HostAssembly(...)` at the composition root for an
intentional shared handler module. Prefer it over `[HighwayHostModule]` when the decision is
specific to one deployment; the assembly attribute deliberately makes the module hostable by
every referencing declared-mode application.

Contract packages should stay inert and be divided by business capability where practical.
`Orders.Contracts`, `Payments.Contracts` and `Notifications.Contracts` make `CAN USE` a useful
architectural hint; one `Everything.Contracts` package makes it truthful but nearly useless.

If compatibility permits before v1, reconsider making `Declared` the default. Its rule is the
one most .NET developers will assume: the application hosts handlers in its own assembly, and a
library reference does not silently make it run another assembly's handlers. If existing users
make that change too risky, production samples and documentation should still opt into
`Declared`, while `Implicit` retains its startup warning until the next breaking version.

### Recommended evolution of Highway's topology safety

#### 1. Name the logical application separately from its replicas

Add a stable logical application identity — for example `ApplicationName = "orders"`, defaulted
from the entry assembly and overridable. `NodeName` remains the unique process instance;
`SubscriptionGroup` remains the logical pub/sub consumer. Neither is a reliable substitute for
the bounded capability whose dependency graph an architect wants to see.

The manifest and dashboard would then group:

```
application orders
  nodes orders-1, orders-2
  provides ...
  can use ...
```

#### 2. Add automatic causal lineage to every verb

Carry a root operation id, immediate causation id and hop count through RPC, queue and pub/sub
envelopes. When a handler sends a child message, Highway should inherit and advance this context
automatically; application code should not have to remember to copy it. Preserve the existing
W3C trace context, but do not treat a sampled telemetry trace as the durable correctness field.

This unlocks three different protections:

- attribution: *which handler caused this send?*;
- detection: *has this route or logical application appeared earlier in the active chain?*; and
- containment: *has this chain exceeded its maximum permitted hops?*

For RPC, a repeated route in the active synchronous chain should fail quickly with a specific,
diagnosable loop outcome instead of consuming nested timeout budgets. For queues and pub/sub,
exhausting the hop budget should dead-letter the message with its lineage and a clear
`LOOP_DETECTED` reason rather than dropping it or continuing indefinitely.

#### 3. Distinguish possible topology from observed topology

Keep the current `CAN USE` list, but add runtime-derived edges:

```
CAN USE          reference-derived possibility
OBSERVED CALLS   handler/application -> verb -> route, with count and last seen
```

The dashboard may render possible edges as hints and observed edges as facts. It should mark a
possible cycle differently from an observed causal cycle; combining them under one red warning
would recreate the false-authority problem feature 024 carefully avoided.

#### 4. Offer an optional strict outbound policy

A composition root may optionally declare the outbound RPC, queue and channel routes the logical
application intends to use. `HighwayClient` can enforce that allowlist because every outbound
operation passes through it. This is different from an unenforceable publisher-role attribute:
the policy applies to the actual client operation, not to an assembly label.

Strict declarations would also make startup/build-time RPC-DAG validation possible. They should
remain opt-in so Highway's basic experience stays assembly-scanned and low-ceremony.

#### 5. Add a Roslyn analyzer as guidance, not the source of runtime truth

An analyzer/source generator can discover ordinary typed calls to `ExecuteAsync`, `SendAsync`
and `PublishAsync`, produce a useful project-level outbound manifest and warn about reciprocal
RPC edges. It cannot see every wrapper, dynamic call or externally supplied delegate, so its
result must be labelled static evidence and may not replace runtime causation.

### Recommended order

1. Fix the manifest's `SubscriptionGroup` value and add the missing UserGuide section.
2. Use `HostingMode.Declared` in production guidance and samples; decide whether pre-v1 is the
   right moment to make it the default.
3. Write the RPC-DAG rule and the asynchronous-cycle acceptance checklist into the UserGuide.
4. Add logical `ApplicationName`, causation id and hop count as one message-safety feature.
5. Record and display observed handler-to-route edges, including detected loops.
6. Add optional strict outbound declarations and an analyzer only after runtime truth exists.

The resulting mental model stays small:

> A logical application owns a business capability. Hosting is explicit. RPC points downstream
> and never back. Events may return as new facts, but every causal chain must terminate.
