# Tasks: Reliable Delivery

Ordered so **Part 1 is independently shippable**. It fixes the only thing here that is
actively broken; if the feature has to be cut, cut after T7.

---

## Part 1 — Dead letters (the actual bug)

### - [x] T1 — Entry framing with an attempt count

Add `[u16 attempts]` to the four entry framings, with saturating increment.

*Requirements:* R1.1, R1.2, R1.3
**Done when:** round-trip encode/decode is unit-tested for all four framings, and a
requeue-after-lease-expiry increments while a first enqueue does not. The distinction in
R1.2 is easy to get backwards and only a test will catch it.

### - [x] T2 — Storage-format guard

The `hw:*:fmt` marker, the check on every queue-touching command, and the
`HW_STORAGE_FORMAT` error.

*Requirements:* R7.2
**Done when:** a v1-format entry written directly into a queue produces a **refusal naming
the service or channel**, not a misparse. Write that test first and watch it fail without
the guard — a migration guard that has never been seen to fire is untested code that runs
only in the situation nobody can reproduce on purpose.

### - [x] T3 — Dead-letter lists and the sweep decision

DLQ keys, the dead-letter framing (timestamp, attempts, reason, original entry), and the
branch inside the existing lease sweep.

*Requirements:* R2.1, R2.2, R2.3, R2.4, R2.10
**Done when:** a permanently failing message stops being redelivered and lands in the DLQ
with its payload, identifiers and attempt count intact — and a test asserts the entry is
never in both lists nor in neither.

### - [x] T4 — Bounding the DLQ

`MaxDeadLetterEntries`, `DeadLetterRetention`, oldest-dropped with the drop counted and
logged.

*Requirements:* R2.9
**Done when:** an unattended DLQ cannot grow without limit, and drops are visible rather
than silent.

### - [x] T5 — `HW.DLQ` PEEK / REQUEUE / PURGE

*Requirements:* R2.5, R2.6, R2.7
**Done when:** `PEEK` is provably non-destructive, `REQUEUE` resets attempts and the
message is delivered again, and `PURGE` removes. Document `PEEK` before `REQUEUE`: look,
then decide.

### - [x] T6 — Observability

`DeadLettered` event type recorded, DLQ depth in `HW.STATS`.

*Requirements:* R2.8, R1.4
**Done when:** a dead-letter appears in `HW.REPLAY` alongside the rest of that message's
lifecycle, and DLQ depth reads like a drop counter — visibly not-normal when non-zero.
This is the mitigation for "dead-lettering hides the bug it used to shout about", so it is
not optional polish.

### - [x] T7 — Protocol document, Part 1

`HW.DLQ` in the Command Index, DLQ keys, the four framing changes, `HW_STORAGE_FORMAT`,
and the breaking-change note with its upgrade path.

*Requirements:* R7.1, R7.3, R7.4
**Done when:** `ProtocolConformanceTests` is green. It must be updated in the same change
that registers `HW.DLQ` — the gate has already fired on exactly this twice.

> **Shippable here.** Everything above fixes broken behaviour. Everything below adds
> capability.

---

## Part 2 — Delayed delivery

### - [x] T8 — Delayed set and `HW.PUBLISH ... AT`

`hw:ch:{channel}:delayed`, arity `3` → `-3`, absolute ticks on the wire.

*Requirements:* R3.1, R3.6
**Done when:** a delayed publish stores rather than delivers, and survives a restart with
AOF. Absolute ticks, not a relative delay — a relative delay replayed from the AOF would
re-delay from replay time and fabricate the future.

### - [x] T9 — Promotion in `HW.RECEIVE`

Declare and lock the delayed key in `Prepare`; range-query and move in `Main`.

*Requirements:* R3.3, R3.7, R3.8, R7.5
**Done when:** nothing is delivered early, a group that subscribed *during* the delay
receives the message, and ordering holds. **State in the code comment where each read
happens and why it is safe** — this is precisely where 004.1's watch-conflict trap lives,
and the reviewer should not have to re-derive it.

### - [x] T10 — Client API

One optional parameter on `PublishAsync`.

*Requirements:* R3.2
**Done when:** the existing call site is unchanged and the delayed call site is one extra
token.

### - [x] T11 — Honest documentation of the guarantee

*Requirements:* R3.4, R3.5, R3.9, R3.10
**Done when:** "not before, not an alarm clock" appears in the API docs, the protocol file
and the samples — not only in `design.md`. Also record why a server timer was rejected,
and that cancelling/listing pending delayed messages is out of scope. Someone will read
"delayed messages" and expect a scheduler; the only defence is saying so where they read.

### - [x] T12 — Retry with backoff

Requeue into the delayed set instead of the queue, capped schedule, off by default for RPC.

*Requirements:* R4.1, R4.2, R4.3, R4.4
**Done when:** pub/sub retries back off and RPC does not, with the `CallTimeout`
interaction written down where `RpcBackoffEnabled` is defined — not just in the design doc.

---

## Part 3 — Deduplication

### - [x] T13 — `[Idempotent]` and the consumer path

The attribute, `SET NX EX` claim, cached response, suppression.

*Requirements:* R5.1, R5.3, R5.4, R5.7
**Done when:** a duplicate delivery does not re-run the handler and the caller still
receives the original response.

### - [x] T14 — The crash window, as designed behaviour

*Requirements:* R5.5, R5.6
**Done when:** a test asserts that an `InProgress` marker **blocks** rather than
re-running, and that state expires. This is the case where the temptation is to "improve"
the behaviour into re-running on a stale marker, which silently breaks the only promise the
attribute makes — so it is asserted deliberately, with the reason in the test.

### - [x] T15 — Scope, in the docs

*Requirements:* R5.2, R5.8
**Done when:** the XML docs on `[Idempotent]` carry the verbatim "does not deduplicate a
caller that issues the same logical request twice" paragraph, and suppressed duplicates are
recorded by the flight recorder.

---

## Part 4 — Conformance

### - [x] T16 — Options and validation

*Requirements:* R6 (all)
**Done when:** every option has a documented default with its reasoning, and
`new HighwayServerBuilder().Build()` still starts a working broker with no configuration.

### - [x] T17 — Samples

A poison message reaching the DLQ, a delayed publish, and a duplicate suppressed.

*Requirements:* R8.9
**Done when:** all three are demonstrable in the samples, the samples are re-run, and
`samples/RUNLOG.md` records what was found. Any defect found is fixed **in the library with
a regression test**, never worked around in the sample.

### - [x] T18 — Product docs and full verification

*Requirements:* R8.8, R8.10
**Done when:** every test passes, `dotnet build` is warning-free, and `product.md` and the
roadmap reflect what shipped.

---

## The line that must not move

`new HighwayServerBuilder().Build()` starts a working broker with no configuration, and
dead-lettering requires no application code at all. If any task above makes reliability
something the developer has to switch on, that task is wrong — the whole argument for this
feature over the withdrawn nine primitives is that it fixes what Highway already promised.
