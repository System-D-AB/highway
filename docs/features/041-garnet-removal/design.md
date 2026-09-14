# Feature 041 — Garnet Removal and Proof: Design

Nothing new is built here; this feature's design is an **order of operations** chosen
so every failure points at its cause.

## Order, and why

1. **Remove the cache** (R1.5) — an add-on with no substrate on the new server; taking
   it out first keeps every later diff attributable to the engine swap alone.
2. **Flip the default** — server host boots the 040 stack; embedded tests already do
   (040 T8). Garnet code still present but unreferenced.
   *Why before deletion:* if anything still reaches for Garnet, the compiler or DI
   container says so while Garnet is still there to compare against.
3. **Run everything before deleting anything** — full suite + both rig runs against
   the flipped default.
   *Why:* a red here can still be debugged against a living Garnet baseline
   (`git stash`-distance away, plus golden replies from 039). After deletion, the
   baseline is history.
4. **Delete** — packages, submodule, dead server code paths; API-surface and
   copyright-sweep tests turn on permanently.
5. **Measure C4.6** — the growth re-run on the final binary, not an intermediate.
6. **Amend the register + product docs** — last, so the amendments describe what
   shipped, not what was intended.

## The C4.6 re-run

Same profile that produced 102 MB / 205 MB (the recorded configuration from the C4.6
investigation), run to the same message count, sampling directory size after each
compaction-settling interval. Pass = bounded steady state after drain; either result
is recorded (R4.1). Expect the DLQ CF to hold its bounded share — that split existed
for exactly this measurement's benefit.

## Rig conduct

Two full runs (normal, doorbells-off), unmodified applications, turbulence included.
The doorbells-off run is the port's sharpest test: it removes the latency mask and
leaves only `BackstopSweeper` correctness — if the port subtly broke sweep semantics,
this run finds it.

## What deletion must not take with it

- The **golden replies** captured in 039 — they graduate into the permanent suite.
- The 012 *lessons* — retired as facts, kept as history (`research.md` addendum
  pattern, per house rules).
- `WindowsServiceVerbsTests` and the 036 hosting stack — engine-agnostic, untouched,
  but re-run in the final sweep because the server host's composition changed.
