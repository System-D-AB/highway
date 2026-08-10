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
→ **Open.** The one item the do-nothing discussion below refuses to defer.

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
