# Tasks: Protocol Specification

> **Ordering note:** Task 1 creates the skeleton and the Command Index; Task 2 makes the index enforceable before any prose is written, so the document is under test from the start rather than verified at the end. Tasks 3–8 fill in the content, each verified against the implementation rather than against the older tables. Tasks 9–11 point the other documents at it and close the loop.

## Task Dependency Graph

```
T1  (File skeleton + Command Index)                  [independent]
T2  (Registration table refactor + conformance test) → T1
T3  (Transport, framing, identifier rules)           → T1
T4  (Error contract)                                 → T1
T5  (RPC commands)                                   → T3, T4
T6  (Pub/Sub commands)                               → T3, T4
T7  (Registry commands)                              → T3, T4
T8  (Keys, entries, doorbells, stock deps, options)  → T5, T6, T7
T9  (Invariants + versioning/changelog)              → T5, T6, T7
T10 (Point the other documents at it)                → T8, T9
T11 (Living-conformance rule + full verification)    → T10
```

## Tasks

- [x] ### Task 1: File Skeleton and Command Index

**Fulfills:** Requirement 1 (AC1–AC4), Requirement 8 (AC1), Requirement 2 (AC1–AC2)

**Steps:**
1. Create `docs/HIGHWAY-PROTOCOL.md` with the section structure from design § "Document Structure"
2. Write the *About* section: what this file is, that it defines Highway's extension surface only, and that it supersedes the command table in `docs/product/product.md` (which is read-only and remains the product vision)
3. Write the **Command Index** exactly as specified in design § "The Command Index" — all twelve commands with arity and form count
4. Verify every arity against `HighwayServer.RegisterCommands` as it stands, not against the older tables, which are known wrong
5. Leave the remaining sections as headings with a one-line statement of what each will contain

**Done criteria:**
- The file exists with a complete, accurate Command Index; every arity matches the implementation

---

- [x] ### Task 2: Registration Table and Conformance Test

**Fulfills:** Requirement 8 (all), Requirement 10 (AC3)

> Written before the prose so the document is enforced from its first commit rather than audited at the end.

**Steps:**
1. Refactor `HighwayServer.RegisterCommands` to iterate a `CommandTable` per design § "The One Refactor". Same commands, same arities, same order, same call site
2. **Keep registration in the constructor, before `Start()`** — AOF replay re-executes stored procedures through the registered set, so this ordering is load-bearing (Requirement 10 AC3)
3. Create `tests/Highway.Server.Tests/ProtocolConformanceTests.cs`
4. Locate the protocol file by walking up from `AppContext.BaseDirectory` to the directory containing `Highway.slnx`. A missing file **fails** the test — never skip, because a conformance test that quietly passes when it cannot find its input is worse than none
5. Parse only the Command Index table: rows whose first cell is a backticked `HW.*` name, taking name and arity
6. **Direction 1:** every indexed command exists on a `HighwayTestServer` with the documented arity — invoke with a deliberately wrong argument count and assert an arity error rather than "unknown command"
7. **Direction 2:** every command in `CommandTable` appears in the index with a matching arity
8. Failure messages name the specific discrepancy, e.g. `"HW.FOO is registered but absent from the Command Index"`
9. Prove the gate fires: temporarily add a command to the table without documenting it, confirm the test fails, then revert. A gate never observed to fire is not known to work
10. Confirm the 004 durability tests still pass — they are what would catch a broken registration order

**Done criteria:**
- Documenting a command that does not exist, implementing one that is not documented, or changing an arity on either side all fail the test; the gate has been observed firing

---

- [x] ### Task 3: Transport, Framing Conventions, and Identifier Rules

**Fulfills:** Requirement 2 (AC2), Requirement 4 (AC4)

**Steps:**
1. Document RESP as the wire format and that Highway adds commands, not a new protocol — `redis-cli` and SE.Redis `Execute()` both work
2. Document the arity convention: positive is exact including the command name, negative is a minimum
3. Document identifier rules — non-empty, at most `MaxIdentifierBytes` (256), no byte below `0x20`, no `0x7F` — and **why**: mirror keys are newline-delimited, so a control character would split an entry and corrupt routing
4. Document that payloads are exempt and stored byte-for-byte
5. Document the envelope (`{v, src, ts, body}`) as the payload format Highway clients use, since a non-.NET client needs it

**Done criteria:**
- An implementer knows how to frame a command and what an identifier may contain, and why

---

- [x] ### Task 4: Error Contract

**Fulfills:** Requirement 3 (all)

**Steps:**
1. Document every code — `HW_INVALID_ARG`, `HW_PAYLOAD_TOO_LARGE`, `HW_INVALID_COUNT`, `HW_INTERNAL` — with exact prefix and meaning
2. State the classification rule: `ERR HW_` is permanent, bare `ERR Transaction failed.` is transient, anything else is permanent
3. Explain *why* the bare message is the transient signal — it is Garnet's own abort reply on watch-version conflict — so the rule is understandable rather than arbitrary
4. State which commands can produce a transient abort, since it is not all of them
5. Call out the `HW.PUBLISH` consequence explicitly: a transient abort means the message was **not** delivered, so a publisher that does not retry can silently lose it
6. Document `+REGISTER` as a normal reply that is **not** an error and must not be routed through error handling
7. Document Garnet's own arity error as distinct from Highway's codes

**Done criteria:**
- A client's retry policy can be written from this section alone, correctly

---

- [x] ### Task 5: RPC Commands

**Fulfills:** Requirement 2 (AC3–AC8)

**Steps:**
1. Document `HW.CALL`, `HW.REPLY`, `HW.DEQUEUE`, `HW.ACK` — one section each
2. Per command: arguments and order, exact reply shape with RESP type, keys read and written, idempotency, and governing options
3. Show `HW.DEQUEUE`'s two replies literally — the `[requestId, payload]` array and the **nil array** (`*-1\r\n`) when empty, which is easy to mis-parse as a nil bulk string
4. Document `HW.REPLY`'s last-writer-wins rule and the reply-slot TTL
5. Document `HW.DEQUEUE`'s two sweeps: per-entry lease expiry, and dead-node pruning — including that only nodes holding a *stale registration* are pruned, never unregistered ones
6. Verify every statement against the implementation, not the older tables

**Done criteria:**
- The RPC lifecycle is implementable from this section alone

---

- [x] ### Task 6: Pub/Sub Commands

**Fulfills:** Requirement 2 (AC3–AC8)

**Steps:**
1. Document `HW.PUBLISH`, `HW.SUBSCRIBE`, `HW.UNSUBSCRIBE`, `HW.RECEIVE`, `HW.RACK`
2. Show `HW.RECEIVE`'s reply shape explicitly — an array of two-element `[messageId, payload]` arrays — and both `COUNT` forms it accepts, with default and maximum
3. Document `HW.PUBLISH`'s integer reply as the group count, and that `0` means the message went to the backlog
4. Document the backlog: when it is used, that `HW.SUBSCRIBE` copies rather than drains it, that copying happens **only** for a genuinely new group, and the retention and entry-cap options
5. Document the group model — group identity is the node name, one copy per group — and that `HW.UNSUBSCRIBE` deletes group state, which is why clients never send it
6. Verify every statement against the implementation

**Done criteria:**
- The pub/sub lifecycle including backlog and late-subscriber behaviour is implementable from this section alone

---

- [x] ### Task 7: Registry Commands

**Fulfills:** Requirement 2 (AC3–AC8)

**Steps:**
1. Document `HW.HEARTBEAT`'s **three forms separately** with the rule that selects between them: absent second argument is liveness, `BYE` is departure, anything else is a catalog. This is the single largest gap in the documents being replaced
2. Document the liveness form's two replies and, critically, *why* `+REGISTER` exists: pruning removes index entries, so a beat that merely refreshed a timestamp would leave a node alive but undiscoverable
3. Document that the catalog crosses the wire once per node lifetime, not per beat, and that a beat's cost is independent of catalog size
4. Document the departure form as running the full teardown immediately, and what teardown does and does not touch
5. Document `HW.DISCOVER`'s reply as `[nodeId, secondsSinceLastSeen]` pairs, empty when no live host — the older tables say "heartbeat scores", which is wrong
6. Document `HW.STATS`'s three forms and their exact fields, the `kind` discriminator, that a name which is both resolves as a service, and that unknown names return zeroed counters rather than an error
7. Document the no-snapshot-consistency caveat for `HW.STATS`

**Done criteria:**
- Every heartbeat form and every stats form is documented; nothing in this section repeats the known-wrong claims of the older tables

---

- [x] ### Task 8: Keys, Entry Framing, Doorbells, Stock Dependencies, Options

**Fulfills:** Requirement 4 (AC1–AC3, AC5–AC6), Requirement 5 (all), Requirement 2 (AC8)

**Steps:**
1. Document every key: pattern, store type (main or object), value type, purpose, and TTL where one applies
2. Document the byte-level framing of every stored entry — RPC queue, RPC processing, channel, backlog, group processing, registration record — with endianness stated
3. Document the mirror-key mechanism and the watch-conflict constraint that makes it necessary, so its newline delimiter is understood as load-bearing rather than incidental
4. Document all doorbell channels with names and payloads, and that they are best-effort — correctness never depends on delivery
5. Document the **stock Garnet commands a client must use**: `GET`/`DEL` on `hw:rep:{requestId}` for reply retrieval, and RESP `SUBSCRIBE` for doorbells. Note that the reply doorbell is node-global, so a client must ignore request IDs it did not issue — the defect fixed in 005 came from exactly this
6. State that Highway requires no cluster, scripting, or stream commands
7. Document the server options that change observable protocol behaviour, with defaults

**Done criteria:**
- An operator can inspect any Highway state with `redis-cli`, and a client implementer knows every non-`HW.*` command they must issue

---

- [x] ### Task 9: Invariants and Versioning

**Fulfills:** Requirement 6 (all), Requirement 7 (all)

**Steps:**
1. Document the RPC ordering invariant — `HW.REPLY` strictly before `HW.ACK` — and why a crash between them still delivers
2. Document the pub/sub ordering invariant — `HW.RACK` only after dispatch — and why the reverse would lose messages
3. Document the pruning asymmetry: removing a node requeues its RPC work but **never** deletes its subscriber groups, with the reasoning. This is the invariant most at risk from a future "cleanup"
4. Document at-least-once for both paths, with duplicate handling named as the caller's responsibility
5. Document the subscriber-group model: identity is the node name, groups outlive the process, clients never send `HW.UNSUBSCRIBE`
6. Name the test that enforces each invariant, so a reader sees it is guaranteed rather than intended
7. Add the protocol version and a changelog recording what each version introduced and which feature delivered it; set the current version to reflect everything through 006
8. State the versioning rule: what is breaking versus additive

**Done criteria:**
- Every cross-command guarantee is written where it cannot be missed, and each names its enforcing test

---

- [x] ### Task 10: Point the Other Documents At It

**Fulfills:** Requirement 1 (AC5–AC7)

**Steps:**
1. Replace the command tables in `.kiro/steering/technical-reference.md` with a pointer to `docs/HIGHWAY-PROTOCOL.md`, removing one of the two live copies entirely
2. Add a line to each of `004/design.md`, `004.1/design.md`, `005/design.md` and `006/design.md` noting that the protocol file is now the reference for *what*, while the feature spec keeps the *why*. Do not delete their design rationale
3. Do **not** modify `docs/product/product.md` — it is read-only. The protocol file's *About* section already states that it supersedes that table
4. Add a pointer from `CLAUDE.md`'s key rules so a new contributor finds the protocol file immediately

**Done criteria:**
- Exactly one live copy of the protocol remains; every other document points at it rather than restating it

---

- [x] ### Task 11: Living-Conformance Rule and Full Verification

**Fulfills:** Requirement 9 (all), Requirement 10 (all)

**Steps:**
1. Amend `.kiro/steering/spec-workflow.md`, alongside the samples rule from 010: any feature adding or changing an `HW.*` command, reply shape, error code, key, or doorbell must update `docs/HIGHWAY-PROTOCOL.md` and its changelog **within that same feature**
2. State that the conformance test covers the command level automatically, and that everything else — reply shapes, error codes, keys, invariants — is the author's responsibility
3. `dotnet build Highway.slnx` — zero warnings, zero errors
4. `dotnet test Highway.slnx` — all 434 existing tests pass plus the new conformance test
5. Run the integration suite a second time to catch parallelism flakiness, per established practice
6. Re-read the finished protocol file end to end against the implementation, checking each command's arguments and replies one final time — the document's whole value is being right
7. Record the final test count and any discrepancy the write-up exposed between implementation and prior documentation

**Done criteria:**
- Green build, green suite twice, one enforced protocol file, and the obligation recorded where every future feature will read it

**Result:** Green build (0 warnings), full suite green twice.

| Project | Before 007 | After 007 |
|---|---|---|
| Highway.Abstractions.Tests | 2 | 2 |
| Highway.Client.Tests | 158 | 158 |
| Highway.Server.Tests | 101 | 107 |
| Highway.Integration.Tests | 173 | 173 |
| **Total** | **434** | **440** |

No behavioural change to any command. `RegisterCommands` now iterates a table
instead of making twelve inline calls — same commands, same arities, same order,
still in the constructor before `Start()`.

---

## Completion Record

### The drift was real, and worse than the requirements estimated

Writing the document against the implementation rather than against the older
tables confirmed every discrepancy the requirements predicted, and the
`HW.HEARTBEAT` gap was the largest: both prior tables documented only the
registration form, so the liveness form, the `BYE` form, and the `+REGISTER`
reply — the entire self-healing mechanism of feature 006 — appeared in no
document at all. `HW.DISCOVER`'s "heartbeat scores" and `HW.STATS`'s field list
were both wrong, and neither table carried the error contract, the keyspace, or
the stock-command dependencies without which no client can be written.

### An error the final read-through caught

Task 11 step 6 — re-reading the finished file against the implementation — found
a mistake in the *new* document. The Error Contract listed `HW.SUBSCRIBE` and
`HW.UNSUBSCRIBE` among the commands that can abort transiently. Neither reads a
key during `Prepare`, so neither registers a watch and neither can produce a
watch-version conflict. The section now carries a two-column table verified
command by command against each `Prepare` implementation.

Worth recording because it is the failure mode this feature exists to prevent: a
plausible claim, written confidently, wrong. The conformance test would never
have caught it — it checks names and arities, not prose. The final read-through
is not ceremony.

### The gate was observed firing

Per Task 2 step 9, a command was temporarily added to the registration table
without documenting it. Two tests failed with messages naming the exact problem
(`"HW.UNDOCUMENTED is registered but absent from the Command Index"`), then the
change was reverted and the suite returned to green. A gate never seen to fire is
not known to work.

### One live copy remains

`.kiro/steering/technical-reference.md` no longer carries a command table; it
points at the protocol file and records why it stopped duplicating it. The four
feature design docs gained a header note separating *why* (theirs) from *what*
(the protocol file). `CLAUDE.md` points at it so a new contributor finds it
first. `docs/product/product.md` is untouched — it is read-only, and the protocol
file's About section states plainly that it supersedes that table.

### Living conformance now covers both artefacts

`.kiro/steering/spec-workflow.md` gained a "Living Conformance" section covering
the protocol file (this feature) and the samples (feature 010, once built). Both
obligations fall on the feature making the change, not on a follow-up.

### Follow-up: superseded material retired

After the protocol file landed, a sweep for material it obsoletes found one more
instance of the same disease and one dead directory.

**`.kiro/steering/technical-reference.md` had a second stale copy.** Beyond the
command table already replaced in Task 10, its "Programming Model" section
reproduced the client and server configuration APIs — and had drifted just as
badly. It listed **three** client options when twelve exist, and showed a
`WithDashboard(port:)` builder method that **has never existed in the code**. The
API snippets are removed rather than refreshed: refreshing only restarts the
clock. The file now points at the types, which carry XML documentation including
the rationale for non-obvious defaults, and keeps only the genuinely stable
conceptual material (two verbs, four class shapes, architecture decisions).

**`docs/old-reference/`** — the v0.8 source dump kept as reference for the
rewrite — was already empty and its directory was removed. Features 003 through
006 have long since superseded what it held.

**`QWEN.md`** was byte-identical to `CLAUDE.md` apart from its title, and had the
same stale pointer. It was synced, not deleted: it is a different tool's
instruction file and not this project's to discard.

`docs/product/product.md` and `research.md` remain untouched — read-only by
convention, and now explicitly labelled historical in the reference table.

### Follow-up: product docs made living, and corrected

The project owner lifted the read-only rule on `docs/product/` on 2026-08-07.
That rule is what allowed the drift 007 exists to fix — a document nobody may
correct becomes wrong and stays wrong. `CLAUDE.md`, `QWEN.md`,
`spec-workflow.md`, `technical-reference.md` and `coding-standards.md` were all
updated to match, with two constraints attached to the new freedom:

1. **Do not rewrite history.** `research.md` records what was believed *at the
   time*; correct it with dated addenda and inline pointers, never by silently
   editing the original analysis.
2. **Do not restate the protocol.** Product docs link to
   `docs/HIGHWAY-PROTOCOL.md`; they never carry a copy.

**`product.md`** gained an implementation-status table separating shipped from
intended, and its command table was replaced by a pointer. Three things it
claimed are now explicitly labelled: the web dashboard and `WithDashboard(...)`
do not exist, performance is **uncharacterised**, and running as
separate processes is **unproven** — every test to date runs in one process.

**`research.md`** was kept whole, with a banner and two inline warnings. Its
§2.3 substrate table (`BLPOP`, sorted sets, `HEXPIRE`, `WATCH`, Lua) describes
almost nothing Highway actually uses, and §2.4 recommends *"write no Garnet
extensions in v1"* — the opposite of what shipped. Both now carry pointers to
Part 4, which verified the truth against the pinned submodule.

Deleting it was considered and rejected. Part 3's evaluation of the alternatives
is untouched by anything that happened during implementation and is the only
answer to "why not just use Wolverine?"; Part 1 is now the **only** surviving
record of the v0.8 system, since `docs/old-reference/` was removed. With no
commits in the repository, deletion would have been permanent.
