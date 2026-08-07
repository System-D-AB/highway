# Claude Code Instructions

You are working on the **Highway** project — a distributed .NET framework providing RPC and Pub/Sub via a custom Garnet server extension.

## Mandatory: Read Steering Files First

Before doing any work, read and follow ALL steering files in `.kiro/steering/`:

- `project-overview.md` — What Highway is, package architecture
- `spec-workflow.md` — Feature spec conventions and document structure
- `coding-standards.md` — C#/.NET 10 style, dependencies, testing
- `technical-reference.md` — Protocol commands, API surface, architecture decisions

These files are the source of truth for project conventions.

## Mandatory: Spec-Driven Development

**Every feature must be implemented using spec-driven development.** No code gets written without a spec first.

### Workflow (strictly enforced):

1. **Requirements** — Create `docs/features/{NNN}-{feature-name}/requirements.md`
   - Define user stories and acceptance criteria
   - Each requirement must be specific and testable

2. **Design** — Create `docs/features/{NNN}-{feature-name}/design.md`
   - Technical architecture, interfaces, data models
   - Sequence diagrams for key flows
   - Error handling strategy

3. **Tasks** — Create `docs/features/{NNN}-{feature-name}/tasks.md`
   - Ordered, atomic implementation steps
   - Each task references which requirement(s) it fulfills
   - Clear done criteria per task
   - **Each task title prefixed with `- [ ]` (unchecked checkbox)**
   - **Mark completed tasks as `- [x]`**

4. **Ask user** — Present the task list and ask the user which tasks to implement
   - Do NOT start coding until the user approves the spec
   - Do NOT implement all tasks at once unless explicitly told to

### Numbering Convention

Check `docs/features/` for existing specs and use the next sequential number:
- `001-feature-name/`
- `002-feature-name/`
- etc.

## Product Reference

The product definition and technical research live in:
- `docs/product/product.md` — Vision, goals, package architecture
- `docs/product/research.md` — v0.8 analysis, Garnet evaluation, why-not-the-alternatives
- `docs/product/roadmap.md` — Feature order and status

**These are living documents — keep them current.** They were read-only earlier in the project, which let them drift: `product.md`'s protocol table described commands that no longer matched the code, and `research.md`'s Garnet analysis recommended the opposite of what shipped. Correct them when reality moves.

Two rules when you do:

1. **Do not rewrite history.** `research.md` is a record of what was believed *at the time*, and its value is that it explains why decisions were made. Correct it with dated addenda and inline pointers, not by silently editing the original analysis. `product.md`'s goals are the product's intent — distinguish *unbuilt intent* from *wrong*.
2. **Do not restate the protocol.** `docs/HIGHWAY-PROTOCOL.md` is the single definition. Product docs link to it; they never copy it.

## Constraints Are Enumerated, Not Implied

[`docs/product/constraints.md`](docs/product/constraints.md) numbers every guarantee Highway
makes and records whether the code currently keeps it. It exists so intent and reality can
be compared line by line instead of inferred.

Two rules:

1. **A feature that changes any behaviour it describes updates the status in the same feature.** Same discipline as the protocol file, applied to product guarantees.
2. **A gap is either a defect or a planned feature — never a silent difference.** If a constraint turns out to be wrong, change the constraint and say why. Do not let the code quietly diverge and call it the spec.

## The Protocol Lives in One File

**[`docs/HIGHWAY-PROTOCOL.md`](docs/HIGHWAY-PROTOCOL.md) is the authoritative definition of the Highway wire protocol** — every `HW.*` command, reply shape, error code, key, entry framing, doorbell channel, and cross-command invariant.

Read it before touching anything protocol-facing. It is enforced by `ProtocolConformanceTests`, which parses its Command Index and checks it against a running server in both directions. Any feature that adds or changes a command, reply shape, error code, key or doorbell must update that file **in the same feature**.

No other document may restate the protocol. `docs/product/product.md` links to this file rather than carrying its own command table — a second copy is a second thing to get wrong, which is exactly how the original one drifted.

## Key Rules

- Target: .NET 10, C# 14
- Serialization: System.Text.Json only (no Newtonsoft)
- Redis client: StackExchange.Redis
- Testing: xUnit + FluentAssertions + NSubstitute
- Always use CancellationToken in async APIs
- Errors are data (Output.StatusCode), not exceptions
- Integration tests use embedded Garnet — no external infrastructure required
