# Feature: Protocol Specification

## Introduction

Highway's wire protocol is currently defined in six places and agrees with itself in none of them. `docs/product/product.md` and `.kiro/steering/technical-reference.md` both carry command tables; `004/design.md`, `004.1/design.md` and `006/design.md` each describe the parts they introduced; and the implementation is the only thing that is actually true. Checked today, both command tables are already wrong about code that shipped hours earlier:

- `HW.HEARTBEAT` is documented with only its catalog form. The liveness form, the `BYE` form, and the `+REGISTER` reply — the most consequential behaviour in feature 006 — appear nowhere.
- `HW.DISCOVER` is documented as returning "node IDs with heartbeat scores"; it returns `[nodeId, secondsSinceLastSeen]` pairs.
- `HW.STATS` is documented as returning "queue depth, subscriber count, processing count"; it returns a flat field/value array with a `kind` discriminator and different fields per form.
- The 004.1 error contract — the rule every client's retry policy depends on — is documented in no command table at all.
- Neither table mentions the `hw:` keyspace, the doorbell channels, or the **stock** Garnet commands Highway depends on. A reply cannot be retrieved without `GET`/`DEL`, and a doorbell cannot be observed without `SUBSCRIBE`, so nobody could write a Highway client from the current documentation.

This feature replaces all of that with **one file** that defines the entire Highway protocol, and a conformance test that fails when the implementation and the file disagree.

## Glossary

- **The protocol file** — the single document produced by this feature; the authoritative definition of everything Highway adds to Garnet.
- **Command Index** — the strictly-formatted table inside the protocol file listing every command and its arity. This is the machine-checked part.
- **Conformance test** — the test that parses the Command Index and asserts the running server matches it, in both directions.
- **Extension surface** — what Highway adds: the `HW.*` commands, the `hw:` keyspace, the doorbell channels, and the error contract. Explicitly *not* Garnet's own command set.

## Requirements

### Requirement 1: One File, Whole Protocol

**User Story:** As anyone implementing against or reasoning about Highway, I want the entire protocol in a single document, so that there is exactly one thing to read and exactly one thing to keep true.

#### Acceptance Criteria

1. The entire Highway protocol is defined in **one file**. No part of the protocol definition lives in a second document, an appendix, or an include
2. The file is self-contained: a reader who has never seen the repository can implement a conforming client from it alone, without reading source code or feature specs
3. The file covers, at minimum: every `HW.*` command, the error contract, the key schema, entry framing, doorbell channels, the stock Garnet commands Highway depends on, and the cross-cutting delivery invariants
4. The file defines Highway's **extension surface only**. Garnet's own commands are out of scope except where Highway depends on one, and those dependencies are listed explicitly
5. Feature specs (`004`, `004.1`, `005`, `006`) keep their design rationale — *why* a decision was made — but cease to be the reference for *what* the protocol is; each links to the protocol file
6. `.kiro/steering/technical-reference.md` replaces its command tables with a pointer to the protocol file, so the steering files stop carrying a second copy
7. `docs/product/product.md` is **not modified** — it is read-only reference. The protocol file supersedes its command table as the implementation truth, and says so plainly, so a reader who finds the older table knows which one governs

### Requirement 2: Complete and Exact Command Coverage

**User Story:** As a client implementer, I want every command documented exactly as it behaves, so that I do not have to discover a form or a reply shape by experiment.

#### Acceptance Criteria

1. All twelve `HW.*` commands are documented: `CALL`, `REPLY`, `DEQUEUE`, `ACK`, `PUBLISH`, `SUBSCRIBE`, `UNSUBSCRIBE`, `RECEIVE`, `RACK`, `HEARTBEAT`, `DISCOVER`, `STATS`
2. Every command states its exact argument list, argument order, and arity — including negative arity where arguments are optional
3. Commands with multiple forms document **each form separately**, with the rule that selects between them. `HW.HEARTBEAT`'s three forms and `HW.STATS`'s three forms are documented individually, not collapsed into one row
4. Every possible reply is documented with its exact RESP type and shape: simple strings, integers, bulk strings, nested arrays, and the nil array returned by an empty `HW.DEQUEUE`
5. Non-obvious replies are shown as literal RESP or as a worked example, so an implementer can verify their parser against the document
6. Idempotency is stated per command — which commands are safe to repeat, and what a repeat does
7. Each command states which keys it reads and writes, so an operator can reason about contention
8. Where a command's behaviour depends on a server option, the option is named and its default given

### Requirement 3: The Error Contract

**User Story:** As a client implementer, I want to know exactly which failures are retryable, because getting it wrong means either spinning forever on a poison request or dropping a recoverable one.

#### Acceptance Criteria

1. Every error code Highway emits is documented with its exact message prefix and meaning
2. The classification rule is stated unambiguously: `ERR HW_` prefix means permanent, the bare `ERR Transaction failed.` means transient and retryable, anything else means permanent
3. The document explains *why* the bare message is the transient signal — it is Garnet's own abort reply, emitted on watch-version conflict — so the rule is understandable rather than arbitrary
4. Which commands can produce a transient abort is stated, since it is not all of them
5. The consequence for `HW.PUBLISH` specifically is called out: a transient abort means the message was **not** delivered, so a publisher that does not retry can silently lose it
6. `+REGISTER` is documented as a normal reply and explicitly **not** an error, so no implementer routes it through error handling

### Requirement 4: Key Schema, Framing, and Doorbells

**User Story:** As an operator debugging a live system, I want to know what Highway stores and where, so that I can inspect state with `redis-cli` without reading source.

#### Acceptance Criteria

1. Every key Highway creates is documented with its pattern, store type (main or object), value type, and purpose
2. The binary framing of every stored entry is documented byte by byte, with endianness stated
3. The mirror-key mechanism is documented — what it is, and the watch-conflict constraint that requires it — because its existence is otherwise inexplicable and its newline delimiter is load-bearing
4. The identifier rules are documented, along with the reason: mirror keys are newline-delimited, so control characters are rejected
5. All doorbell channels are documented with their names and payloads, and stated to be best-effort — correctness never depends on delivery
6. Which keys carry a TTL is stated, with the governing option

### Requirement 5: Dependencies on Stock Garnet Commands

**User Story:** As a client implementer, I want to know which non-Highway commands I must issue, because a client built only from the `HW.*` list cannot function.

#### Acceptance Criteria

1. Every stock Garnet/RESP command a Highway client must use is listed with its purpose
2. Reply-slot retrieval via `GET` and `DEL` on `hw:rep:{requestId}` is documented — it is not an `HW.*` command and is easy to miss
3. Doorbell observation via RESP `SUBSCRIBE` is documented, including that subscriptions are node-global and a client must ignore messages for requests it did not issue
4. The document states that Highway does not require Garnet's cluster, scripting, or stream commands

### Requirement 6: Cross-Cutting Invariants

**User Story:** As someone changing Highway later, I want the guarantees that span commands written where they cannot be missed, so that a local change does not silently break a global property.

#### Acceptance Criteria

1. The RPC ordering invariant is documented: `HW.REPLY` strictly before `HW.ACK`, and why
2. The pub/sub ordering invariant is documented: `HW.RACK` only after dispatch completes, and why
3. The pruning asymmetry is documented: removing a node requeues its RPC work but **never** deletes its subscriber groups, with the reasoning
4. At-least-once delivery is stated as the contract for both RPC and pub/sub, with duplicate handling named as the caller's responsibility
5. The subscriber-group model is documented: group identity is the node name, groups outlive the process, and the client never sends `HW.UNSUBSCRIBE`
6. Each invariant names the test that enforces it, so a reader can see it is guaranteed rather than merely intended

### Requirement 7: Versioning

**User Story:** As someone integrating with Highway over time, I want protocol changes to be trackable, so that I can tell what a given build speaks.

#### Acceptance Criteria

1. The protocol file carries a version number
2. A changelog section records what changed in each version and which feature introduced it
3. The versioning rule is stated: what constitutes a breaking change versus an additive one
4. The current version reflects everything shipped through feature 006

### Requirement 8: The Document Is Enforced, Not Merely Written

**User Story:** As the project owner, I want the implementation and the protocol file unable to disagree, because two command tables have already drifted while nobody noticed.

#### Acceptance Criteria

1. The protocol file contains a **Command Index**: one strictly-formatted table listing every command with its arity, in a shape a test can parse
2. A conformance test parses the Command Index from the file at test time — not from a copy — and asserts it against a running server
3. The test checks **both directions**: every command in the index exists on the server with the documented arity, and every command the server registers appears in the index
4. Adding a command without documenting it fails the test. Documenting a command without implementing it fails the test. Changing an arity on either side fails the test
5. The test failure message names the specific discrepancy, so the fix is obvious without debugging
6. The test lives with the server tests and runs in the normal suite — no separate command, no opt-in flag
7. The document remains the single definition: the test reads the file, rather than the file being generated from code. What the code must satisfy is written down in one place, in prose, for humans

### Requirement 9: Living Conformance

**User Story:** As the project owner, I want the protocol file to stay true as Highway grows, so that this feature does not become the seventh stale description.

#### Acceptance Criteria

1. Any feature that adds or changes an `HW.*` command, a reply shape, an error code, a key, or a doorbell must update the protocol file **within that same feature**
2. The obligation is recorded in `.kiro/steering/spec-workflow.md`, alongside the existing samples rule, so it binds every future feature rather than only the ones that remember
3. The conformance test makes the command-level half of this automatic rather than remembered
4. The protocol file's changelog gives each change a home, so "what changed" is answerable without reading git history

### Requirement 10: No Regression

#### Acceptance Criteria

1. All 434 existing tests pass unchanged
2. `dotnet build` produces zero warnings
3. Any refactor made to enable the conformance test preserves command-registration ordering — registration must still happen before `Start()`, because AOF replay re-executes stored procedures through the registered set
4. No behavioural change to any command; this feature documents and verifies, it does not alter the protocol

## Non-Goals

- **Documenting Garnet.** Only Highway's extension surface, plus the stock commands Highway depends on.
- **A machine-readable schema for codegen.** The Command Index is parseable because that is what the conformance test needs; this feature does not deliver an IDL, a JSON Schema, or client generation. If those become worthwhile, the Command Index is where they would start.
- **Generating the document from code.** The document is the definition and the code is checked against it, not the reverse. A generated file cannot explain *why* an invariant exists, and the reasoning is most of the value.
- **Documenting the .NET client API.** `IHighwayClient`, `AddHighway` and `HighwayOptions` are a library surface, not the wire protocol. They belong in library documentation.
- **A protocol negotiation or capability handshake.** Version is documentation for humans; nothing negotiates it at runtime.

## Cross-References

- Sources being consolidated: `docs/features/004-server-hw-commands/design.md`, `004.1/design.md`, `005/design.md`, `006/design.md`
- Command tables being superseded: `docs/product/product.md` § "Highway Protocol" (read-only — not modified), `.kiro/steering/technical-reference.md` (updated to point at the protocol file)
- Living-conformance precedent: `docs/features/010-create-samples/requirements.md` § Requirement 11
- Consumers this unblocks: feature 002 (flight recorder event taxonomy), feature 010 (samples), any non-.NET client or dashboard
