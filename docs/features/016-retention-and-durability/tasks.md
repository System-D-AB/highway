# Tasks: Retention, Storage and Durability

**C4.5 and R6 land first, and they land alone.** Both are independent of every open decision,
both are shippable on their own, and between them they turn the product's biggest false promise
into a true one. Everything after them is a limit on data that, until they ship, does not
survive the process anyway.

---

## Phase 1 — Durable by default (C4.5)

### - [x] T1 — The default data directory

`AppContext.BaseDirectory/highway-data`, port-suffixed when the port is not the default.
Creatable-and-writable is checked at `Build()`.

*Requirements:* R1.1, R1.2, R1.3, R1.6
**Done when:** `new HighwayServerBuilder().Build()` produces a durable broker, the path is in
the startup log at Information, and an unwritable location **throws at `Build()` naming the path
and both escapes** (`WithDataDir`, `Ephemeral`). Never a silent fall back to memory — that is
the defect this feature removes, and it would be worse afterwards because the guarantee would
then be documented as true.

### - [x] T2 — `Ephemeral()`, one call

*Requirements:* R1.4
**Done when:** `new HighwayServerBuilder().Ephemeral().Build()` is memory-only and
`HighwayTestServer` uses it. **This lands with T1, not after.** Making durability the default
without a trivial opt-out means the test suite fights the default, and someone eventually flips
the default back rather than the tests.

### - [x] T3 — The restart test, all three verbs

*Requirements:* R1.1, R1.7
**Done when:** a queued message, a published message with a registered offline group, and an
unacknowledged RPC request each survive a broker restart against the same data directory.

018's unification is what makes this one test shape instead of three — the dividend, collected.
**Watch it fail against memory-only first**: a durability test that has never been seen to fail
proves the harness restarted, not that the data survived.

### - [x] T4 — Remove the 014 durability warning

*Requirements:* R1.5
**Done when:** the warning is gone and nothing in the docs still says the default is
memory-only. A warning that outlives its truth trains people to ignore warnings.

---

## Phase 2 — AOF growth (R6, independent)

### - [~] T5 — `AofSizeLimit` and checkpointing — **partial**

*Requirements:* R6.1, R6.2, R6.4
**Partially done.** `AofSizeLimitBytes` (default 512 MB) is wired to Garnet's `AofSizeLimit`, and
the background enforcement task demonstrably runs — checkpoints appear where none did before.
**But it does not bound the log on disk**; see T6. Kept because periodic checkpointing is worth
having on its own, and documented as partial rather than claimed as met.

### - [ ] T6 — Prove it is bounded — **BLOCKED, and the premise was wrong**

*Requirements:* R6.3
**Not done, and the test proved why.** `AofSizeLimit` is configured and Garnet's background
enforcement task does run — checkpoint files appear — but **the AOF is never truncated on
disk**. Measured twice with identical traffic:

| messages | AOF on disk |
|---|---|
| 2,000 | 8.9 MB |
| 4,000 | 17.8 MB |

Growth is exactly linear in total history, which is the thing R6 exists to prevent.

The test is **kept and skipped**, with the measurement in its skip reason. Deleting it would
lose the finding; weakening it until it passes would be worse than either, because the suite
would then assert that a broken thing works.

**What was learned, for whoever picks this up:**
- Enforcement is a **background task** on a frequency (`AofSizeLimitEnforceFrequencySecs`,
  5s by default), not a check on the write path. A test that measures immediately after its
  traffic measures the log before the task has run — my first version did exactly that.
- The limit is parsed as a size **string**, then converted to bits (`1L << AofSizeLimitSizeBits()`),
  so a raw byte count only works because it round-trips.
- The AOF lives at `{dataDir}/checkpoints/AOF/aof.log*`, **not** under `LogDir` — which is the
  storage-tier device log and stays empty. My first test measured `LogDir`, found 0 bytes, and
  passed whether the limit was set or not. It was vacuous in exactly the way 018's subscriber
  test was, which is why it was checked against a disabled limit before being believed.

**Next step is investigation, not implementation:** either Garnet needs compaction configured
alongside (`CompactionType`/`CompactionFrequencySecs`) for the log to be reclaimed, or
truncation is logical and disk reclamation needs something else again. R6.4 (bounded *recovery
time*) may still hold via checkpoints even while disk growth does not — that is unmeasured and
should not be claimed.

> **Ship Phases 1 and 2 separately from the rest.** They are worth having on their own, they
> touch different code from the byte budgets, and R6 is explicitly independent of every open
> decision.

---

## Phase 3 — Byte budgets (Decision 1: per structure)

### - [x] T7 — The byte counter

`hw:q:{name}:bytes`, maintained by the same transaction that pushes or pops.

*Requirements:* R2.1, R2.2, R2.4
**Done when:** enqueue, claim and sweep all keep it accurate, and the enqueue path takes no
measurable extra cost. A counter is O(1) where measuring a structure is O(n) — but a counter
trusts every writer, which is what T8 exists for.

**The key is derivable from the command's own arguments**, so it can be declared in `Prepare`.
That is not a convenience; Garnet rejects touching an undeclared key, and this wall has been hit
in 013, 014 and 015.

### - [x] T8 — Drift detection

*Requirements:* R2.2
**Done when:** a test recomputes each structure's true size and compares it to the counter after
a mixed workload of sends, claims, acks, redeliveries and dead-letters. **This is not optional
belt-and-braces**: the counter is only correct if *every* path maintains it, and the paths that
forget are exactly the ones nobody thought about.

### - [x] T9 — `MaxQueueBytes` and refusal

*Requirements:* R4.1, R4.2, R4.3
**Done when:** an enqueue that would exceed the limit is refused with `HW_QUEUE_FULL` — a
**permanent** error under the 004.1 contract — naming the queue and the limit, and the client
surfaces it as a typed exception. Nothing is dropped: under C1.2 a queued message is one nobody
has ever processed.

### - [x] T10 — Fan-out refuses as a whole, and names the group

*Requirements:* R4.1, R4.4, Decision 5
**Done when:** a publish where **any** group's queue is full writes **nothing** and returns an
error naming that group.

**Both halves are the requirement.** Writing nothing preserves 018's atomicity and keeps C2.1
honest; naming the group is what turns "one stuck subscriber blocked a channel" from a debugging
session into a fix. A test must assert both — that the healthy groups received nothing, and that
the error identifies the offending one.

### - [x] T11 — Every structure bounded, enumerated

*Requirements:* R3.1–R3.5
**Done when:** a test **enumerates every list, set and sorted set Highway creates** and asserts
each has a cap or an explicit, reasoned exemption.

The enumeration is the requirement, not the caps. Caps can be added; what stops the *next*
feature adding an unbounded structure is a test that fails when one appears.

---

## Phase 4 — Retention (R5)

### - [ ] T12 — 100-day retention, per queue — **BLOCKED: needs a breaking framing change**

*Requirements:* R5.1, R5.3
**Not done, and the reason is structural.** A queue entry is
`[ver][attempts][idLen][id][payload]` — it carries **no timestamp**, so there is nothing to age
it against. Time-based retention needs either a fifth field in the framing (breaking, exactly
like 013's attempt count) or a parallel time-keyed structure.

Deliberately not bolted on. R5.2 already says retention is the *secondary* limit: under C1.2
only unprocessed work is stored, so in a healthy system neither limit binds and in an unhealthy
one the byte budget arrives long before 100 days do. Shipping a breaking framing change for the
limit that binds second, in a feature whose point was the limit that binds first, would be the
wrong trade.

**For whoever picks it up:** the framing change is the decision, not the sweep. If the entry
gains a timestamp it should probably gain it alongside whatever else the next breaking change
needs, so the format breaks once rather than twice.

### - [x] T13 — Losses are counted and visible

*Requirements:* R4.6, R5.4
**Done.** `HW.STATS` reports `sendsRefused`, and every refusal — send or publish — records a
`SendRefused` event naming the queue, or for a publish the **group** whose queue was full.
Retention removals do not exist to count (T12 is blocked on the entry framing).

> **A defect found by writing that test.** A refusal is decided in `Main`, which the command's
> `Failed` flag does not cover — it only reflects `Prepare`-phase rejections. So a refused
> publish fell straight through `if (Failed) return;`, **rang every subscriber's doorbell and
> recorded itself as `Published`**. Workers woke for a message that did not exist and the replay
> claimed a publish that never happened. Introduced by 016 and caught only when the counter gave
> a reason to look at that path.

Superseded text: *A refusal is visible where it matters most — the producer gets `HW_QUEUE_FULL`
naming the queue or group, and `hw:q:{queue}:bytes` is readable, so an operator can see how
close a queue is to its limit. A **counter of refusals** in `HW.STATS` and a recorder event are
not built; retention removals do not exist to count (T12).

Left undone rather than half-built: a refusal counter belongs with the recorder event, and both
belong with whatever surfaces them in the dashboard — which is already outstanding from 015.

---

## Phase 5 — Conformance

### - [x] T14 — Protocol document

*Requirements:* R7.1, R7.2
**Done when:** `HW_QUEUE_FULL`, the changed option defaults and any new keys are documented, and
`ProtocolConformanceTests` is green. Updated in the same change that adds the code — that gate
has fired five times.

### - [x] T15 — Constraints

*Requirements:* R7.3
**Done when:** C4.1–C4.6 move to **Met**, and **C4.7 is added**: the byte budget is per
structure and does not bound the process. Decision 1 chose the shippable option; the gap is
recorded as a constraint rather than left for someone to infer from a default.

### - [x] T16 — Samples and full verification

*Requirements:* R7.4, R7.5, R7.6
**Done when:** a sample demonstrates a refused send at a limit, the samples are re-run across
real processes with a `RUNLOG.md` entry, all tests pass, the build is warning-free, and
`new HighwayServerBuilder().Build()` still starts a working broker with no configuration — now a
durable one.

---

## The line that must not move

**A broker never silently becomes non-durable.** If it cannot be durable it says so and refuses;
if the operator wants memory-only they ask for it by name. Every other requirement here is a
limit on data — and a limit on data that does not survive the process is a limit on how much can
be lost at once, which is not what anyone was asking for.

**And: nothing is ever dropped to make room.** Under C1.2 a queued message is one nobody has
ever processed. Refusing the producer is honest; discarding the message loses exactly the data
the queue exists to protect.
