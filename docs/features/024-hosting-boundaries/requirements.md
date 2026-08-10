# Feature: Hosting Boundaries and Topology — Consent, Not Reference

## Introduction

Highway's scanner discovers handlers in every assembly the application references. That rule
was written for contracts — where it is correct and load-bearing (it fixed the caller-only
`SERVICE_NOT_FOUND` defect in feature 003) — but it applies equally to handlers, where it is
neither: **referencing a library for one helper class silently makes the process host every
`IProcess<>`, `ISubscribe<>` and `AsyncService<,>` that library contains.** Which application
processes a queue becomes a property of the dependency graph, answerable from no single file.

Three independent architecture reviews arrived at this same finding (F9 in
`docs/product/brainstorming.md`; `docs/product/concerns.md` §3 and §6; the UserGuide review of
2026-08-10). The third reviewer, while writing about Highway's mental model, *mis-learned the
identity rule* — evidence that the model as it stands cannot be held correctly even by a
careful reader.

The design principle, settled in review: **magic where it is safe, consent where it is
dangerous, visibility everywhere.** Contract discovery stays closure-wide and automatic
forever. Handler discovery gains a boundary. And what a process provides and can use becomes a
printed fact, not an investigation.

### What this feature is not

- Not a role taxonomy (`[HighwayRole(Publisher)]`) — outbound roles are unenforceable and
  were rejected in review with reasons recorded in `brainstorming.md`.
- Not direction markers (`[ProducedBy]`/`[ConsumedBy]`) — deployment facts asserted in
  contract assemblies drift; the runtime already derives the truth. Rejected in review.
- Not the subscriber-group split — that is feature 025.
- Not a breaking change — the default mode reproduces today's behavior exactly.

## Requirements

### Requirement 1: Handler Hosting Is a Decision

**User Story:** As a developer on a multi-application system, I want my process to host only
handlers someone consented to, so that adding an assembly reference cannot silently change
what my deployment does.

#### Acceptance Criteria

1. `HighwayOptions` SHALL gain a `HostingMode` with three values: `Implicit` (today's
   behavior — handlers from every scanned assembly), `Declared` (handlers from the entry
   assembly plus declared assemblies only), and `ExplicitOnly` (handlers from declared
   assemblies only, entry assembly included only if declared).
2. The default SHALL be `Implicit`. Existing applications, samples and the test suite SHALL
   run unchanged with zero configuration difference.
3. An assembly SHALL be declarable as a host module in two equivalent ways: an assembly-level
   `[assembly: HighwayHostModule]` attribute in the library, or
   `options.HostAssembly(typeof(SomeType).Assembly)` at the composition root.
4. **Contract discovery SHALL remain closure-wide in every mode.** A caller-only process
   keeps finding every contract it references, exactly as today.
5. In `Declared` and `ExplicitOnly` modes, a handler found in a non-hosted assembly SHALL be
   skipped **and reported** — one log line per skipped assembly naming the handlers not
   hosted and the two ways to consent. Silence never hides a skipped handler.

### Requirement 2: The Accident Is Visible Even in Implicit Mode

**User Story:** As an operator running today's default, I want to be told when a referenced
library — not my application — contributed handlers, so the three-library accident announces
itself instead of surfacing as duplicated side effects.

#### Acceptance Criteria

1. In `Implicit` mode, WHEN a handler is discovered in an assembly that is neither the entry
   assembly nor a declared module, the engine SHALL log one warning per such assembly at
   startup, naming the assembly and the handlers it contributed.
2. The warning SHALL name the remedy: declare the assembly a host module, or switch to
   `Declared` mode to stop hosting it.
3. Applications whose handlers all live in the entry assembly (the samples) SHALL log no
   such warning.

### Requirement 3: The Topology Manifest

**User Story:** As a developer or operator, I want a process to print what it provides and
what it can use at startup, so topology questions are answered by the boot log instead of by
investigation.

#### Acceptance Criteria

1. At engine start, the engine SHALL log a structured manifest: node name; **PROVIDES** —
   hosted RPC services, queue processors, and channel subscriptions (with subscriber group);
   **CAN USE** — request, queue and channel contracts discovered from references.
2. The consumption half SHALL be labelled **"can use"**, never "uses": a discovered contract
   proves the process can address a route, not that it does. The manifest SHALL NOT overclaim.
3. Each PROVIDES entry SHALL name the implementing type and its source assembly, so "where
   did this handler come from?" is answered by the same line that announces it.
4. The manifest SHALL be exposed on `IHighwayEngine` as a structured object, so hosts and
   tests can assert on it rather than parsing log text.

### Requirement 4: The Broker Learns the Can-Use Half

**User Story:** As an operator on the dashboard, I want a node's page to show what it can
call, send and publish — the half of topology the catalogue has never had.

#### Acceptance Criteria

1. The registration catalog SHALL additively gain the can-use lists (service, queue and
   channel contract names). The catalog is JSON; a record without the field SHALL read as
   empty — no framing or version change.
2. The dashboard node page SHALL show a "Can use" section beside "Declares", labelled as
   reference-derived.
3. Per standing instruction, **no tests are written for the dashboard project**; the
   projection logic feeding it SHALL be tested server-side.

### Requirement 5: The Mental Model Is Documented — Correctly

**User Story:** As a new Highway developer, I want the four rules of the model stated where I
start reading, so I do not have to infer them from behavior.

#### Acceptance Criteria

1. The UserGuide SHALL gain the four rules: contracts are the schema (inert packages);
   handlers declare capability (hosted by consent); **the verb decides sharing** (Execute and
   Send compete across nodes; Publish delivers one copy per subscriber group); verbs declare
   semantics (answer → Execute, one → Send, many → Publish).
2. Rule 3 SHALL state today's group identity (group = node name) and reference feature 025
   for the split — the third reviewer's misreading of this rule is why it must be written
   down precisely.
3. The UserGuide hosting section SHALL state the reference-equals-hosting behavior of
   `Implicit` mode and its remedy, now that a remedy exists.
4. `docs/product/product.md` goal G3 ("scanning discovers everything, no manual
   registration") SHALL be annotated: the promise holds for contracts unconditionally and for
   handlers per `HostingMode` — updated in this feature, not left to drift.

### Requirement 6: Nothing Breaks

#### Acceptance Criteria

1. The full test suite SHALL pass with no test edited to accommodate the default mode.
2. The samples SHALL run unchanged and their boot logs SHALL show the manifest.
3. `dotnet build --no-incremental` SHALL report zero warnings.
