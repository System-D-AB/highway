# Feature 041 — Garnet Removal and Proof: Tasks

```
T0 (cache out) ──► T1 (flip) ──► T2 (suite, G2) ──► T3 (rig ×2, G3) ──► T4 (delete) ──► T5 (C4.6, G4) ──► T6 (register + docs)
```

### - [x] T0 — Remove the cache add-on (feature 026)

**Fulfills:** R1.5
Delete `src/Highway.Client/Caching/` + tests + now-unused caching package refs; sweep
samples/docs; announce in `product.md`/`roadmap.md`/release notes; retire its
constraints with dated amendments; attach the client diff to this spec as the sole
037 R1.4 exception.
**Done when:** solution + samples green with no cache reference anywhere; every
announcement and amendment in place.

### - [x] T1 — Flip the default to the new stack

**Fulfills:** R1.4 (prep)
Server host boots 040's server + 038's store; dashboard, samples, distribution
scripts pointed over. Garnet still present, unreferenced by the running paths.
**Done when:** host starts, dashboard renders, a sample round-trips, distribution
verify script passes on a fresh zip.

### - [x] T2 — Everything green before deletion *(gate G2)*

**Fulfills:** R2
Full suite: unit, contract, command, server, protocol conformance, samples.
**Done when:** all green; `git diff src/Highway.Client/` over 038–041 is empty except
T0's recorded cache removal; conformance passes with only 040's subset section changed
in the protocol doc.

### - [x] T3 — The assurance rig, twice *(gate G3)*

**Fulfills:** R3
Run 1 normal; run 2 `DoorbellsEnabled == false`. Turbulence (ungraceful kills) in
both. Applications unmodified; 032 R6 corroboration re-pointed at RocksDB.
**Done when:** I1–I5 green in both runs; both recorded in `assurance/RUNLOG.md`.

### - [x] T4 — Delete Garnet

**Fulfills:** R1
Remove packages, submodule, dead code paths; enable the API-surface test and the
copyright-header sweep permanently.
**Done when:** R1.1–R1.4 all green; solution builds from a clean clone with no
submodule init.

### - [x] T5 — Measure C4.6 *(gate G4)*

**Fulfills:** R4
The recorded growth profile re-run per the design; unskip or retire
`SustainedTraffic_DoesNotGrowTheLogWithoutBound`.
**Done when:** the measurement is in `constraints.md` either way, with the profile
and figures.

### - [x] T6 — Make the register and the product docs true

**Fulfills:** R5
C4.2–C4.6, C5 (OD1 number ± observed), C6.x, C23, C24 amendments; 012 findings
retired (constraints + research addendum); OD2 decisions re-checked as-shipped;
`product.md` + `roadmap.md` dated updates.
**Done when:** every entry named in R5 carries its dated amendment; a read of
`constraints.md` top to bottom describes the shipped system with no stale line.
