# Feature 038 — Storage Engine: Tasks

```
T0 (paper decisions) ──► T1 (encoders proven) ──► T2 (contract suite + InMemory) ──► T3 (RocksDbStore core)
                                                                                      ├──► T4 (seq + head scheme)
                                                                                      ├──► T5 (expiry, OD5)
                                                                                      └──► T6 (crash + fault) ──► T7 (G1 + packaging)
```

### - [ ] T0 — The paper decisions *(no code)*

**Fulfills:** R0
OD1 target, OD2 constraint adoptions, WAL sync policy, read-view mechanism — each
recorded (`constraints.md` for the first two, this spec's design for the rest) with
the imported sibling evidence cited by path. 037's OD table gets dated pointers.
**Done when:** all four decisions are written down; none is "decided by omission."

### - [ ] T1 — Encoder property tests over the existing draft

**Fulfills:** R3.1
Round-trip, ordering (byte order = numeric order for int64; lexical for strings),
self-delimiting compound keys, `KeyWriter` overflow path.
**Done when:** property tests green over `KeyEncoding`/`KeyWriter` unchanged — or the
draft is amended with the layout doc updated in the same change.

### - [ ] T2 — Contract suite + `InMemoryStore` first

**Fulfills:** R2.1, R2.2, R1.1
The full R2.1 semantic list as one suite parameterized over store factories;
`InMemoryStore` per the design shape passes it. Seam adjustments only with a named
call site.
**Done when:** suite green in-memory; every T0 read-view sentence appears as a test.

### - [ ] T3 — `RocksDbStore`: open, CFs, families

**Fulfills:** R2.3, R3.1, R3.2
DB open with CF order asserted; four families + counters wired through the existing
layout code; snapshot + batch (read-view per T0); WAL options per T0's sync policy.
**Done when:** the same contract suite is green on RocksDB against a temp dir.

### - [ ] T4 — In-batch seq allocation + head-push scheme

**Fulfills:** R3.3
Per-list counter in the `n` family, allocated inside the batch; head scheme (§5
scheme 1 unless measured otherwise) implemented and recorded in physical-layout.md.
**Done when:** contract tests for FIFO-after-head-push and concurrent tail-push
(two threads, two lists, per-key locks) pass on both stores.

### - [ ] T5 — Reply-slot expiry (OD5)

**Fulfills:** R3.4
Implement T0's chosen mechanism.
**Done when:** an expired slot is unreadable immediately after its `expiresAtTicks`
and physically absent after the cleanup path runs; decision recorded in the layout doc.

### - [ ] T6 — Fault injection + crash-replay

**Fulfills:** R4.2, R4.4
Failure injected between two staged writes → no partial state; child-process kill
mid-stream → reopen → byte-identical dump versus reference.
**Done when:** both tests green on RocksDB; the fault-injection one also green
in-memory.

### - [ ] T7 — Gate G1 + single-commit + no-clock + packaging *(gate G1)*

**Fulfills:** R1.2, R1.3, R4.1, R4.3, R5
Reflection test over the seam surface (no engine/Garnet/Tsavorite types);
search-assert single commit point; no-clock-in-batch test; native lib present in
win-x64 + linux-x64 distribution artifacts via the existing verify script.
**Done when:** all four asserts green. **G1 passes or 039 does not start.**
