# Design: Protocol Specification

## Overview

One file — `docs/HIGHWAY-PROTOCOL.md` — defines everything Highway adds to Garnet. One test parses that file and asserts a running server matches it. Nothing else is produced.

The value is not the document existing; six partial descriptions already exist. The value is that after this feature there is exactly **one** place the protocol is defined, and disagreeing with it fails the build.

## Why the File Is the Source of Truth, Not the Code

There were two workable shapes. Generating the document from a code manifest would guarantee accuracy but produce a reference that can only state *what*, never *why* — and the reasoning is most of what makes this protocol usable. Nothing generated explains why the bare `ERR Transaction failed.` is the retryable signal, or why pruning must never delete subscriber groups.

So the document is written by hand and the code is checked against it. The Command Index is the machine-checked spine; the prose around it carries the reasoning that keeps the next person from "simplifying" an invariant that exists for a reason.

This inverts the usual instinct — docs describing code — deliberately. The protocol is the contract; the implementation is one party to it.

## Honest Scope of "Single File"

The requirement is that the **protocol** is defined in one file, and it is: every command, reply, key, error, and invariant is in `HIGHWAY-PROTOCOL.md` and nowhere else.

The code necessarily contains a registration list — a server cannot register commands without naming them. That list is *wiring*, not a second definition: it carries names and arities, no semantics. The conformance test exists precisely to guarantee the wiring cannot drift from the definition. This is the strongest achievable form, and stating it plainly is better than claiming a purity the code cannot have.

## Document Structure

```
docs/HIGHWAY-PROTOCOL.md

  1. About                     scope, what supersedes what, how to read this
  2. Protocol Version          current version + changelog
  3. Command Index             ◀── the strictly-formatted, machine-checked table
  4. Transport & Framing       RESP, arity conventions, identifier rules
  5. Error Contract            codes, classification rule, per-command applicability
  6. RPC Commands              CALL, REPLY, DEQUEUE, ACK — one section each
  7. Pub/Sub Commands          PUBLISH, SUBSCRIBE, UNSUBSCRIBE, RECEIVE, RACK
  8. Registry Commands         HEARTBEAT (×3 forms), DISCOVER, STATS
  9. Stock Garnet Dependencies GET/DEL on reply slots, SUBSCRIBE for doorbells
 10. Key Schema                every key, store type, value shape
 11. Entry Framing             byte-level layout of every stored entry
 12. Doorbell Channels         names, payloads, best-effort contract
 13. Invariants                the guarantees that span commands
 14. Server Options            options that change observable protocol behaviour
```

`docs/HIGHWAY-PROTOCOL.md` sits at the top of `docs/` rather than inside `docs/product/` (read-only) or `docs/features/` (per-feature specs). It is neither product vision nor a feature spec — it is the living contract, and the location should say so.

## The Command Index

One table, parsed by the conformance test. Kept minimal on purpose: it carries only what the test verifies, so it cannot contradict the prose sections that carry semantics.

```markdown
| Command | Arity | Forms | Summary |
|---|---|---|---|
| `HW.CALL` | 4 | 1 | Enqueue an RPC request |
| `HW.REPLY` | 3 | 1 | Write the reply slot |
| `HW.DEQUEUE` | 3 | 1 | Claim the next request |
| `HW.ACK` | 4 | 1 | Acknowledge an RPC request |
| `HW.PUBLISH` | 3 | 1 | Durable fan-out to all groups |
| `HW.SUBSCRIBE` | 3 | 1 | Register a subscriber group |
| `HW.UNSUBSCRIBE` | 3 | 1 | Remove a subscriber group |
| `HW.RECEIVE` | -3 | 1 | Consume a batch |
| `HW.RACK` | 4 | 1 | Acknowledge a message |
| `HW.HEARTBEAT` | -2 | 3 | Register, prove liveness, or depart |
| `HW.DISCOVER` | 2 | 1 | Live hosts of a service |
| `HW.STATS` | -1 | 3 | Server, service, or channel counters |
```

Arity follows Redis convention: positive is exact including the command name, negative is a minimum. `Forms` is documentation for the reader — the number of distinct behaviours documented in that command's section — and is not machine-checked, because form selection is semantic.

## Conformance Test

`tests/Highway.Server.Tests/ProtocolConformanceTests.cs`.

**Locating the file.** Walk up from `AppContext.BaseDirectory` to the repository root (the directory containing `Highway.slnx`) and read `docs/HIGHWAY-PROTOCOL.md`. A missing file fails the test loudly rather than silently skipping — a conformance test that quietly passes when it cannot find its input is worse than none.

**Parsing.** Extract the table under the `## Command Index` heading, take rows whose first cell is a backticked `HW.*` name, and parse name and arity. Nothing more; a lenient parser tolerates prose edits around it.

**Direction 1 — everything documented exists.** For each indexed command, invoke it against a `HighwayTestServer` with a deliberately wrong argument count and assert the reply is an *arity* error, not "unknown command". This proves the command is registered and its arity is what the document claims, without needing to construct a valid call for each.

**Direction 2 — everything registered is documented.** Compare against the server's registration list. This requires the registration list to be enumerable, which drives the one refactor below.

**Failure messages** name the specific discrepancy — `"HW.FOO is registered but absent from the Command Index"` — so the fix needs no debugging.

## The One Refactor

`HighwayServer.RegisterCommands` currently makes twelve inline `Register.NewTransactionProc` calls. Direction 2 needs those names and arities readable from a test.

Change it to iterate a table:

```csharp
internal static IReadOnlyList<HighwayCommandRegistration> CommandTable(
    HighwayServerOptions opts, DoorbellBridge doorbell) =>
[
    new("HW.CALL",      4, () => new HwCallCommand(opts, doorbell)),
    new("HW.REPLY",     3, () => new HwReplyCommand(opts, doorbell)),
    // ...
];

internal static void RegisterCommands(HighwayGarnetServer server, DoorbellBridge doorbell, HighwayServerOptions opts)
{
    foreach (var c in CommandTable(opts, doorbell))
        server.Register.NewTransactionProc(c.Name, c.Factory, new RespCommandsInfo { Arity = c.Arity });
}
```

Behaviourally identical: same commands, same arities, same order, same call site. **Registration must still happen in the constructor, before `Start()`** — AOF replay re-executes stored-procedure entries through the registered set, so moving it would break recovery. That ordering is untouched, and Requirement 10 AC3 exists to make sure it stays that way.

## Reconciling With `product.md`

`docs/product/product.md` is read-only and its command table is now wrong in several places. It cannot be fixed, and it should not be deleted — it is the product's founding document.

The protocol file's *About* section states plainly that it supersedes that table for implementation purposes, and that `product.md` remains the product vision. A reader who finds the older table learns immediately which one governs. `.kiro/steering/technical-reference.md` is editable, so its tables are replaced by a pointer — removing one of the two live copies entirely.

## What Goes In, Concretely

Assembled from what shipped, not from what the older tables claim:

| Source | What it contributes |
|---|---|
| `004/design.md` + implementation | Nine RPC and pub/sub commands, key schema, entry framing, doorbells, lease semantics |
| `004.1/design.md` + implementation | Error codes and the classification rule, identifier rules, mirror keys and the watch-conflict reason |
| `005/design.md` + implementation | Reply-slot retrieval via stock `GET`/`DEL`, doorbell subscription being node-global, envelope format |
| `006` implementation | Three heartbeat forms, `+REGISTER`, `HW.DISCOVER` pair shape, `HW.STATS` field/value shape, pruning rules |
| Test suite | The invariants worth naming, and the test that enforces each |

The envelope (`{v, src, ts, body}`) is included: it is what a payload contains on the wire, so a non-.NET client needs it.

## Risks

| Risk | Mitigation |
|---|---|
| The protocol file becomes the seventh stale description | The conformance test enforces the command level automatically; Requirement 9 puts the rest in steering where every future feature reads it |
| Markdown parsing is brittle and the test fails on formatting | Parser reads only the Command Index table and only two cells per row; a formatting-only failure is loud and takes seconds to fix |
| The test cannot find the file when run from a different working directory | Walk up to the directory containing `Highway.slnx`; fail loudly if absent, never skip |
| Direction 2 needs Garnet internals that are not reachable | The refactor exposes Highway's own registration table; nothing from Garnet is required |
| The refactor disturbs registration ordering and breaks AOF recovery | Registration stays in the constructor, before `Start()`; ordering preserved; the 004 durability tests are the gate |
| The document grows so long nobody reads it | Structured so each command is independently readable; the Command Index at the top serves as the map |

## Dependencies & Constraints

- Depends on 004, 004.1, 005, 006 (all merged). Does not block 002 or 010, though both benefit.
- No new package references. No behavioural change to any command.
- `docs/product/product.md` and `research.md` are not modified.
- Coding standards apply: zero build warnings, xUnit + FluentAssertions, no external test infrastructure.

## Cross-References

- Requirements: `docs/features/007-protocol-specification/requirements.md`
- Material being consolidated: `004/design.md`, `004.1/design.md`, `005/design.md`, `006/design.md`
- Superseded table (read-only, unmodified): `docs/product/product.md` § "Highway Protocol (HW.* Commands)"
- Living-conformance precedent: `docs/features/010-create-samples/` Requirement 11
