# Feature 041 — Garnet Removal and Proof

*Executes 037 Phases 5–6: remove the cache add-on, delete Garnet, then prove the port
kept every promise. Gates G2, G3, G4 close here. Authorities: 037 R2 (Garnet gone),
R8 (the rig is the gate), R9 (the register is true), R1 (client untouched — the
cache-removal diff here, R1.5, being the sole recorded exception).*

## Requirements

### Requirement 1: Garnet is gone (037 R2)

#### Acceptance Criteria

1. No `Garnet.*`/`Tsavorite.*` package or project reference in any project;
   `libs/garnet` removed from `.gitmodules` and the tree.
2. The API-surface test (no Garnet/Tsavorite type on any public or internal surface)
   is green and stays in the suite permanently.
3. The vendored `RespWriteUtils.cs` is the only Garnet-derived source (037 R2.3) —
   asserted by a source sweep for the Microsoft copyright header outside that file.
4. Server host, dashboard, samples and distribution scripts build and run on the new
   stack; the distribution verify script passes with RocksDB natives in place
   (038 R5.1 re-checked at the zip level).
5. **The distributed cache (feature 026) is removed with the engine that made it
   free**: `src/Highway.Client/Caching/` (+ tests, + now-unused
   `Microsoft.Extensions.Caching.*` refs) deleted; samples/docs swept; its removal
   **announced** in `product.md`, `roadmap.md` and the client release notes as a
   breaking change; its constraints marked retired with dated amendments. This diff
   is the one recorded exception to 037 R1.4 — the cache is an add-on, and add-ons do
   not get feature slots (owner's direction, 2026-09-15).

   > **2026-09-16 — succeeded by feature 044.** The cache returned, but not as the
   > Garnet-era one. Feature [044](../044-cache/requirements.md) reintroduces
   > `IDistributedCache` as a **broker-local, never-replicated** add-on over a *separate*
   > RocksDB (`dataDir/cache`), opt-in and off by default. It is a genuinely different
   > guarantee — cold after a failover, epoch-invalidated, TTL-bounded — so it is a new
   > feature, not a revert. This removal note stands as the record of the 026 cache's
   > retirement; 044 is its successor, not its undo.

### Requirement 2: The suite is the proof — gate G2 (037 R1.2, R1.4)

#### Acceptance Criteria

1. The **full** test suite is green: unit, command (039), contract (038), server
   (040), protocol conformance against the running new server, samples.
2. `git diff src/Highway.Client/` across features 038–041 is **empty** except the
   R1.5 cache removal (the recorded exception). Any other hunk = a triaged finding in
   `runs.md`, not a silent absorption.
3. `ProtocolConformanceTests` passes with `HIGHWAY-PROTOCOL.md` unchanged except
   040's served-subset section.

### Requirement 3: The assurance rig — gate G3 (037 R8)

#### Acceptance Criteria

1. I1–I5 green **including the turbulence phase with ungraceful kills**, rig
   applications unmodified; only feature 032 R6's broker-record corroboration is
   re-pointed at RocksDB.
2. A second full run with `DoorbellsEnabled == false` passes every invariant —
   037 R7.1's proof that no delivery guarantee rides on pub/sub.
3. Both runs recorded in `assurance/RUNLOG.md` in the house pattern.

### Requirement 4: C4.6 measured — gate G4 (037 R9.1)

#### Acceptance Criteria

1. The traffic profile that produced the 102 MB / 205 MB Garnet growth figures is
   re-run on the new engine; disk footprint over time is recorded in
   `constraints.md` **either way**.
2. `SustainedTraffic_DoesNotGrowTheLogWithoutBound` is unskipped and re-pointed, or
   retired with a written reason.

### Requirement 5: The register is true (037 R9.2, R9.3)

#### Acceptance Criteria

1. Dated amendments land for every constraint whose mechanics changed: **C4.2–C4.5**
   (met by Garnet mechanisms → met by RocksDB/WAL, with 038 T0's sync-policy wording),
   **C4.6** (measured status), **C6.x** (auth mechanics → 040; semantics preserved),
   **C23** (encryption at rest restated), **C24** (deletion logical until
   compaction restated).
2. The 012 Garnet findings (`@dangerous` category, `nopass` bypass) are retired with
   dated notes in `constraints.md` and a pointer addendum in `research.md` — history
   corrected the house way, never rewritten.
3. C5 carries OD1's number (038 T0) and, if measured during the rig runs, an
   observed figure beside the target.
4. The OD2 decisions (C4.1, C4.7, C9, C19) recorded in 038 T0 are re-checked against
   what actually shipped and their entries updated if implementation diverged.
5. `product.md` and `roadmap.md` reflect the new engine (dated edits, no history
   rewriting; the protocol table stays a link, never a copy).
