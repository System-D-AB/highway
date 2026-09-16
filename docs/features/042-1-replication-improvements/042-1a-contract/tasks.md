# Feature 042-1a — The Herd Contract: Tasks

*Parent T0 + T1. Blocks 042-1b/c/d.*

### - [x] A-T1 — The reconciliation map

**Fulfills:** A-R1 (parent T0)
Write `reconciliation-map.md` in this folder: the reuse/change/delete table from design §D7
expanded with file references into the tree as committed, plus the no-autonomous-side-effects
insight (A-R1.2). Note explicitly which 042 gap-fixes (G1 witness work, G3 stagger) are
superseded and why, so the deletion in 042-1c cites this map, and mark the corresponding
sections of `../042-replication/implementation-gaps.md` as overtaken by 042-1.
**Done when:** the map exists, is dated, and 042-1c's deletion task references it.

### - [x] A-T2 — Close the contract decisions

**Fulfills:** A-R2, A-R3, A-R5, A-R6 (+ A-OD1–A-OD4)
Confirm design D1–D6 (or amend with reasons, dated): the willingness predicate + threshold `W`
and the `x ≤ W < T_fence` validation; herd-arrival promotion on first accepted verb; the
`HW.REPL.HELLO CLIENT` handshake carrier; roster-in-store (`k repl:roster`, master-writes-only)
read via `HW.REPL.STATUS`; `HW.REPL.JOIN` + `HW_PRIORITY_TAKEN`; SE.Redis comma form as the
bootstrap grammar. Update parent `requirements.md` OD table (OD1, OD2, OD6 close here; OD5 was
already closed).
**Done when:** every A-OD row carries a dated closure; parent OD table updated.

### - [x] A-T3 — Protocol document

**Fulfills:** A-R4, A-R7 (house rule)
`HIGHWAY-PROTOCOL.md`: changelog entry; `HW.REPL.HELLO` gains the CLIENT form (both reply
shapes); `HW.REPL.JOIN` added to the Command Index with arity; `HW.REPL.STATUS` documents the
`roster.*` fields; the `hw:door:topology` channel and its three message grammars
(TOPOLOGY / GOODBYE / ROSTER-UPDATE) documented under Doorbell Channels; `HW_PRIORITY_TAKEN`
added to the error contract; the bootstrap-string note added where the served surface
describes client connection. `HW.REPL.WITNESS` re-documented as the bare probe (peer-question
form withdrawn — noted in the changelog since 042's wire never shipped in a release).
**Done when:** doc + changelog updated; `ProtocolConformanceTests` green in both directions.

### - [x] A-T4 — Contract-level tests

**Fulfills:** A-R2, A-R5 (testable now, ahead of the implementations)
The willingness truth-table unit test (against the predicate as a pure function), the roster
record encode/decode round-trip, and the `x ≤ W < T_fence` validation-refusal test — written
against small seams so 042-1b/c plug implementations under them rather than inventing their
own shapes.
**Done when:** tests exist and are green against the contract stubs/seams.
