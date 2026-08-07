# Spec-Driven Development Workflow

## Feature Specs Location

All feature specs live in `docs/features/` with sequential numbering:

```
docs/features/{NNN}-{feature-name}/
├── requirements.md
├── design.md
└── tasks.md
```

- `NNN` = zero-padded three-digit sequence (001, 002, 003...)
- `feature-name` = kebab-case descriptive name

Examples:
- `docs/features/001-abstractions-package/`
- `docs/features/002-server-rpc-commands/`
- `docs/features/003-client-engine/`

## Document Structure

### requirements.md

Each requirement follows this format:

```markdown
# Feature: [Feature Title]

## Introduction
Brief description of what this feature delivers.

## Requirements

### Requirement 1: [Title]

**User Story:** As a [role], I want [feature], so that [benefit]

#### Acceptance Criteria
1. [Specific, testable criterion]
2. [Another criterion]
```

### design.md

Technical design covering:
- Architecture and component diagrams
- Data models and contracts
- API surface / public interfaces
- Sequence diagrams for key flows
- Error handling strategy
- Dependencies and constraints

### tasks.md

Implementation task list with:
- Ordered, atomic tasks that can be executed independently
- Each task references which requirement(s) it fulfills
- Clear done criteria per task
- Task dependency graph
- **Checkbox format:** Each task title MUST be prefixed with `- [ ]` (unchecked). When a task is completed, mark it as `- [x]` (checked). This allows all agents to track progress visually.

Example:
```markdown
- [ ] Task 1: Create .gitignore
- [x] Task 2: Create Directory.Build.props (completed)
- [ ] Task 3: Create Directory.Packages.props
```

## Workflow Order

1. **Requirements first** — Define what we're building and acceptance criteria
2. **Design** — Technical approach, architecture, interfaces
3. **Tasks** — Ordered implementation steps

## Rules for All Agents

- Always read existing specs in `docs/features/` before creating new ones to maintain consistency
- Reference `docs/HIGHWAY-PROTOCOL.md` for the wire protocol, and `docs/product/product.md` / `research.md` for product intent and background research
- Product docs are **living documents** — correct them when reality moves. Two rules: do not rewrite history (correct `research.md` with dated addenda and inline pointers, never by silently editing the original analysis), and never restate the protocol (link to `docs/HIGHWAY-PROTOCOL.md`)
- Keep specs focused: one feature per spec directory
- Cross-reference other feature specs when there are dependencies

## Living Conformance

Two artefacts must stay true as Highway changes. Both obligations fall on the
feature making the change, not on a follow-up.

### The protocol file

Any feature that adds or changes an `HW.*` command, a reply shape, an error code,
a key, entry framing, or a doorbell channel **must update
`docs/HIGHWAY-PROTOCOL.md` and its changelog within that same feature**.

`ProtocolConformanceTests` enforces the command surface automatically — names and
arities, in both directions. Everything else in that file is prose and is the
author's responsibility. Before 007 the protocol was described in six places and
two of them had already drifted from shipped code; one enforced file exists so
that cannot recur.

### The samples

Any feature that changes the protocol, `HighwayOptions`, `HighwayServerOptions`,
or any public API **must update the samples in that same feature, re-run them,
and append to `samples/RUNLOG.md`**.

Running the samples exercises what no test reaches: a standalone broker process,
real TCP between processes, real Ctrl+C shutdown, generic-host lifecycle, and
cross-assembly scanning. A sample that fails to start is a **test failure** and
blocks the feature that broke it. Degrading a sample to route around the break is
not an acceptable fix — that converts a product defect into a documentation one
and loses it.

(Applies once feature 010 has built the samples.)
