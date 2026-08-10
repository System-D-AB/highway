# Tasks: Hosting Boundaries and Topology

## Phase 0 — the partition

### - [x] T1 — Split contract and handler assembly sets

*Requirements:* R1.1, R1.4
**Done when:** `DefaultTypeScanner` accepts a contract set and a handler set; contracts are
discovered from the full closure in every mode; handler discovery is limited to the handler
set; all existing tests pass with both sets equal (the `Implicit` wiring).

> The scanner's discovery methods already split by kind — this task must not change any
> discovery rule, only which type list each group receives.

### - [x] T2 — `HostingMode`, `[HighwayHostModule]`, `HostAssembly(...)`

*Requirements:* R1.1–R1.3
**Done when:** the three modes select the handler set as designed (D1, D2); the attribute and
the option are equivalent and idempotent; `Declared`-mode tests declare their fixture assembly
explicitly and prove entry-plus-declared selection; the test-host caveat (D2) is documented on
the option's XML doc.

### - [x] T3 — Skipped handlers are reported

*Requirements:* R1.5
**Done when:** in `Declared`/`ExplicitOnly`, an excluded assembly containing handlers produces
`SkippedHandlerAssemblies` and the engine logs one line per assembly naming the handlers and
both consent mechanisms; a test asserts the skip list; the error-handling table's dead-declaration
and zero-module warnings exist and are tested.

## Phase 1 — visibility

### - [x] T4 — Implicit-mode accident warning

*Requirements:* R2.1–R2.3
**Done when:** a handler discovered outside the entry assembly and declarations logs one
warning per assembly (D4); the samples boot with zero such warnings; a test proves the warning
names assembly, handlers, and remedy.

### - [x] T5 — `TopologyManifest`: built, logged, exposed

*Requirements:* R3.1–R3.4
**Done when:** the manifest is built from the scan result, logged at `StartAsync` in D6's
format with the "can use" honesty label, exposed via `IHighwayEngine.Topology`, and covered by
tests asserting content (not log text) — including that a subscriber line carries its group.

## Phase 2 — the broker's half

### - [x] T6 — Catalog carries `Uses`; node page shows it

*Requirements:* R4.1–R4.3
**Done when:** `CatalogInfo.Uses` round-trips through registration additively (an old record
reads as empty — asserted by a test against a record without the field); `Catalogue.ReadNode`
surfaces it; the dashboard node page renders "Can use — reference-derived" beside "Declares";
the protocol document's registration-catalog schema is updated **in this task**. No dashboard
tests, per standing instruction.

## Phase 3 — the model on paper

### - [x] T7 — UserGuide: the four rules, hosting section, corrected rule 3

*Requirements:* R5.1–R5.3
**Done when:** the UserGuide states the four rules with rule 3 as *the verb decides sharing*
(and today's group = node name, pointing at 025); the hosting section names Implicit's
reference-equals-hosting behavior and both remedies.

### - [x] T8 — product.md G3 annotation, deferred register

*Requirements:* R5.4
**Done when:** G3 distinguishes contracts (unconditional) from handlers (per `HostingMode`);
the default-flip-to-`Declared` candidate is added to `constraints.md`'s Deferred table with
its trigger (a major version).

### - [x] T9 — Samples and full verification

*Requirements:* R6.1–R6.3
**Done when:** samples run unchanged and their boot logs show the manifest (captured in the
RUNLOG); the full suite is green with no test edited for the default mode; the
`--no-incremental` build has zero warnings.

---

## Parallelization

```
LANE 0   T1, T2, T3     the partition          → blocks everything
LANE 1   T4, T5         engine visibility      → needs lane 0
LANE 2   T6             broker + dashboard     → needs T5's manifest shape
LANE 3   T7, T8, T9     docs + verification    → last

Order: 0 → 1 → 2 → 3
```

## Registered, not built

- **Default flip to `Declared`** — major-version candidate (T8 records it).
- **Roslyn analyzer** enforcing inert contracts at build time — the analyzer package remains
  a future feature; this feature's enforcement is startup-time.
- **Flow-diagram generation from the manifest** — becomes possible once T5 exists; not built.


---

## What execution found

- **The Implicit warning and the skip report share one shape** (`SkippedHandlerAssembly`),
  because they are the same fact — "this assembly's handlers were decided about" — with
  opposite decisions. One record type, two log messages.
- **Skip detection had to be structural, not the discovery pipeline**: an excluded assembly
  whose processor's message type lacks `[Queue]` would have *thrown* under full discovery —
  an excluded assembly's mistakes are not this process's errors. `LooksLikeHandler` names
  without validating, and a test proves it does not throw where discovery would.
- **CAN USE proved itself on first contact**: the order service's manifest showed
  `orders.cancel` (a contract nobody hosts — the fast-fail demo) and `orders.placed` (the
  channel it publishes but does not subscribe to). Reference-derived addressability is
  exactly the half of topology the catalogue never had.
- **One unattributed test flake**: a single client-test failure in one full-solution run,
  name not captured, not reproduced in four subsequent runs (3 isolated + 1 full). Recorded
  rather than hidden; if it reappears, the name comes first.
