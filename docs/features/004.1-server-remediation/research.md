# Research: Garnet Transaction Semantics Behind the 004.1 Remediation

**Purpose:** Record the verified Garnet behaviors that force the 004.1 remediation design — why validation errors were unclassifiable, why the transient/permanent failure split exists, and which mechanisms the fixes rely on. This document amends and extends `docs/features/004-server-hw-commands/research.md`; it does not repeat that document's content.

**Verified against:** `libs/garnet` submodule at commit `8b329e30` (tag `v2.1.2` + 2 commits), same checkout as the 004 research. All file:line references point into it. Empirical findings were verified against a live `HighwayTestServer`.

`docs/product/research.md` and `docs/product/product.md` remain read-only per project rules and are not modified.

---

## Finding 1: `Prepare` cannot write RESP output

`CustomTransactionProcedure.Prepare` has no output parameter — its signature is:

```csharp
public abstract bool Prepare<TGarnetReadApi>(TGarnetReadApi api, ref CustomProcedureInput procInput)
    where TGarnetReadApi : IGarnetReadApi;
```

(`libs/server/Custom/CustomTransactionProcedure.cs`)

When a procedure fails — including `Prepare` returning `false` — Garnet writes a fixed literal message:

```csharp
// libs/server/Custom/CustomRespCommands.cs:48
while (!RespWriteUtils.TryWriteError($"ERR Transaction failed.", ref dcurr, dend))
```

**Consequence:** the per-error messages `004/design.md` promised (`-ERR payload too large`, blank-identifier errors) were never achievable from the phase where all 004 validation lives. Every validation failure and every Garnet-side abort surfaced as the identical string `ERR Transaction failed.`, making permanent and transient failures indistinguishable to a client. This drives Requirement 2's validate-in-`Main` redesign: `Prepare` captures the error and returns `true` (adding no keys), and `Main` renders it — empirically proven in Finding 4.

## Finding 2: Key locking blocks; it never times out

`TransactionManager.Run` (`libs/server/Transaction/TransactionManager.cs:482-491`):

```csharp
bool lockSuccess;
if (fail_fast_on_lock)
{
    lockSuccess = keyEntries.TryLockAllKeys(lock_timeout);
}
else
{
    keyEntries.LockAllKeys();
    lockSuccess = true;
}
```

`CustomTransactionProcedure.FailFastOnKeyLockFailure` defaults to `false` and Highway overrides it nowhere, so `LockAllKeys()` blocks indefinitely and `lockSuccess` is hardcoded `true`; `KeyLockTimeout` (100ms default, 5s in DEBUG) is never consulted on this path.

The only remaining failure gate (`TransactionManager.cs:493-494`):

```csharp
if (!lockSuccess ||
    (!internal_txn && !watchContainer.ValidateWatchVersion()))
```

**Consequence:** for Highway commands, `Run` returns `false` — producing the bare `ERR Transaction failed.` — only when **watch-version validation fails**. Key-lock timeout is not a reachable failure mode. Requirement 2 AC4/AC7 and the client classification rule in 004.1's design rest on this: the bare string is an unambiguous transient-retry signal.

## Finding 3: Mirror-key reads create the watches that cause transient aborts

A `Prepare` read through the read API registers a watch on the key (the `GarnetWatchApi` wrapper). 004's implementation reads mirror keys in `Prepare` precisely because reading the object-store sets directly caused Shared+Exclusive lock conflicts:

- `HwDequeueCommand.Prepare` — `api.GET(hw:svc:{service}:nodelist)`
- `HwPublishCommand.Prepare` — `api.GET(hw:ch:{channel}:grplist)`
- `HwSubscribeCommand.Prepare` — locks `hw:ch:{channel}:grplist` (and reads it in `Main`)
- `HwUnsubscribeCommand.Prepare` — same pattern

If another command mutates a watched key between this transaction's read and its lock acquisition, `ValidateWatchVersion()` fails and the transaction aborts having done no work. Under concurrency `HW.PUBLISH`, `HW.DEQUEUE`, `HW.SUBSCRIBE`, `HW.UNSUBSCRIBE` can therefore abort transiently — for `HW.PUBLISH` that is silent message loss unless the client retries. This is why Requirement 2 exists (client must be able to classify and retry) and why 004.1 deliberately does **not** fix the watch conflicts themselves — Non-Goals defers the throughput work, Requirement 9 documents the exposure.

## Finding 4: Zero-key transactions work — validate-in-Main is viable (Task 1 spike, verified 2026-08-06)

A scratch `HW.SPIKE` transaction procedure whose `Prepare` added **zero keys** and returned `true` was registered on a `HighwayTestServer` and driven via StackExchange.Redis `Execute`. All three properties held:

1. `Main` ran and its custom error reached the client **verbatim** — the test asserted the exact message `ERR HW_SPIKE_REJECTED bad argument`, not `ERR Transaction failed.`
2. The same connection performed a full `HW.CALL` → `HW.DEQUEUE` round trip immediately after the rejected command — no session corruption
3. `Finalize` ran on **both** the success path and the rejected path — proving the `Failed` guard the remediation adds to `HW.CALL`/`HW.REPLY`/`HW.PUBLISH` `Finalize` overrides is necessary, not incidental (without it, rejected commands would ring doorbells for work that never happened)

`LockAllKeys()` over an empty key set is a no-op and `ValidateWatchVersion()` with no registered watches succeeds, which is why the zero-key path works. Scratch code was removed after verification; outcome recorded in `004.1/design.md` § Risks.

## Finding 5: `SetAdd` reports the added count

```csharp
// libs/server/API/IGarnetApi.cs:656
GarnetStatus SetAdd(PinnedSpanByte key, PinnedSpanByte member, out int saddCount);
```

`saddCount` is the number of members actually added — `1` when the member was new, `0` when already present. The 004 `HwSubscribeCommand` discards this value (`out _`), which is exactly what makes re-subscription re-copy the backlog (defect behind Requirement 1). Capturing the count gives the authoritative new/existing signal from the durable set itself — no extra reads, no extra keys — and is the entire mechanism of the Requirement 1 fix.

---

## Finding 6: Rejected command with a raw newline in an argument desyncs the same session (upstream quirk, documented)

Discovered by Task 8's error-contract tests (2026-08-06), localized by `NewlineDesyncProbe`:

- A **rejected** `HW.*` transaction whose argument contains a literal `\n` (e.g. `HW.SUBSCRIBE ch "a\nb"` → `ERR HW_INVALID_ARG`) causes the **next custom command on the same session** to be mis-parsed — the observed symptom is a shifted/blank argument (`group is blank...` for a perfectly valid follow-up).
- **Not** general: accepted commands with `\n` in any position (including payloads — byte-opaque by contract) never desync; rejected commands whose arguments contain no newline never desync; stock commands (`SET`, `PUBLISH`, `PING`) with `\n` never desync; `PING` on the affected session keeps working, so wire framing stays intact — only custom-command argument parsing is shifted. A **fresh connection is always unaffected**.
- Suspected locus: Garnet's fast-path command parsing / session buffer accounting around literal newlines in bulk arguments (`libs/server/Resp/Parser/RespCommand.cs` `ParseCommand`/`FastParseCommand`); not root-caused further by decision (document-and-mitigate).
- **Practical impact on Highway: none.** Feature 005 validates identifiers client-side (`Identifier` rules) and never sends control characters; only a raw RESP client deliberately sending `\n` in a name can hit this.
- **Mitigation adopted:** tests that follow a newline-carrying rejection use a fresh connection (or run the newline case last); `NewlineDesyncProbe` pins the behavior as a regression marker — if a future Garnet bump fixes it, its C2 assertion flips loudly and this note should be updated.

## Impact Map

| Finding | Drives |
|---|---|
| 1 — `Prepare` cannot write output | Requirement 2 (validate-in-`Main` pattern), `HighwayCommandBase` design |
| 2 — locking blocks, watch validation is the only abort | Requirement 2 AC4/AC7 (bare `ERR Transaction failed.` = transient), 005 retry classification |
| 3 — mirror reads create watches | Non-Goals rationale (fix deferred), Requirement 9 AC1 (document the exposure) |
| 4 — zero-key transactions viable | The rebase approach for all nine commands; `Finalize` `Failed` guards |
| 5 — `SetAdd` added count | Requirement 1 idempotent re-subscribe fix |
| 6 — newline-in-rejected-arg session desync | Test layout (fresh connections after newline rejections), `NewlineDesyncProbe` regression marker, documented upstream quirk |
