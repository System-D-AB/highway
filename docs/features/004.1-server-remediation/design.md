# Design: Server Remediation (004.1)

> **Protocol reference.** The authoritative definition of the wire protocol —
> commands, replies, errors, keys, framing, invariants — is
> [`docs/HIGHWAY-PROTOCOL.md`](../../HIGHWAY-PROTOCOL.md) (feature 007).
> This document keeps the *reasoning* behind the decisions; that file is the
> reference for *what* the protocol is. Where they differ, that file governs.
## Overview

This feature makes three kinds of change to `Highway.Server`: one behavioral fix (re-subscribe must not re-copy the backlog), one contract change (validation errors become classifiable), and one capability addition (the embedded test server becomes configurable, which unlocks the test coverage 004 promised). Everything else is documentation truth-up.

The guiding constraint is that 005 has already pinned 004's wire contract. **No command name, argument order, or success-reply shape changes.** Only error replies gain specificity, and error replies are the one part of the contract 005 could not usefully consume before.

## Findings That Drive This Design

Two facts about Garnet, verified against the pinned checkout, shape everything below.

**`Prepare` cannot write RESP output.** Its signature is `Prepare<TGarnetReadApi>(TGarnetReadApi api, ref CustomProcedureInput procInput)` — there is no `output` parameter ([`CustomTransactionProcedure.cs`](../../../libs/garnet/libs/server/Custom/CustomTransactionProcedure.cs)). Returning `false` causes `TryTransactionProc` to write the literal `ERR Transaction failed.` ([`CustomRespCommands.cs:48`](../../../libs/garnet/libs/server/Custom/CustomRespCommands.cs#L48)). The per-error messages `004/design.md` promised were never achievable from that phase.

**Key locking blocks; it does not time out.** `CustomTransactionProcedure.FailFastOnKeyLockFailure` defaults to `false`, and Highway overrides it nowhere. `TransactionManager.Run` therefore calls `keyEntries.LockAllKeys()` and hardcodes `lockSuccess = true`; `KeyLockTimeout` is never consulted. The only path by which `Run` returns `false` for a Highway command is `watchContainer.ValidateWatchVersion()` failing — a **watch conflict**, caused by the `api.GET` on a mirror key that `HW.DEQUEUE`, `HW.PUBLISH`, `HW.SUBSCRIBE` and `HW.UNSUBSCRIBE` perform in `Prepare`.

The consequence is the design's central point: **a transient abort and a permanent validation failure are currently the same string.** For `HW.PUBLISH` a transient abort means the caller believes a message was published when it was not. Making these separable is the prerequisite for any correct retry policy in 005.

## Architecture

```
src/Highway.Server/
├── Commands/
│   ├── HighwayCommandBase.cs          # NEW — shared validation, error codes, Finalize guard
│   ├── HwCallCommand.cs               # rebased on HighwayCommandBase
│   ├── HwReplyCommand.cs              # rebased
│   ├── HwDequeueCommand.cs            # rebased
│   ├── HwAckCommand.cs                # rebased
│   ├── HwPublishCommand.cs            # rebased
│   ├── HwSubscribeCommand.cs          # rebased + idempotency fix (Req 1)
│   ├── HwUnsubscribeCommand.cs        # rebased
│   ├── HwReceiveCommand.cs            # rebased + COUNT overflow fix
│   └── HwRackCommand.cs               # rebased
├── Internal/
│   ├── HighwayErrors.cs               # NEW — error code constants + formatting
│   ├── Identifier.cs                  # NEW — identifier validation rules
│   └── HighwayServerOptions.cs        # internal → public; + BindAddress, MaxIdentifierBytes
├── HighwayServerBuilder.cs            # + WithBindAddress, + WithOptions; validation at Build()
├── HighwayServer.cs                   # Endpoint reports configured address
└── HighwayTestServer.cs               # + configuration delegate, + Restart(), + Port
```

No new package references. No changes to `Highway.Abstractions` or `Highway.Client`.

## Validation Redesign

### The pattern

Validation moves out of `Prepare`'s return value and into a captured error that `Main` renders. `Prepare` still runs first and still adds no keys on failure, so nothing is locked, nothing is watched, and nothing is mutated — 004 Requirement 14 AC5 is preserved.

```csharp
internal abstract class HighwayCommandBase : CustomTransactionProcedure
{
    private string? _error;

    /// <summary>True when validation already failed; callers must stop.</summary>
    protected bool Failed => _error is not null;

    protected bool Fail(string code, string detail)
    {
        _error ??= $"ERR {code} {detail}";   // first failure wins
        return false;
    }

    /// <summary>Reads and validates an identifier argument.</summary>
    protected bool TryReadIdentifier(
        ref CustomProcedureInput input, ref int idx, string name, int maxBytes, out string value);

    /// <summary>Reads a payload argument, enforcing the size cap.</summary>
    protected bool TryReadPayload(
        ref CustomProcedureInput input, ref int idx, int maxBytes, out byte[] value);

    /// <summary>Writes the captured error, if any. Call as the first line of Main.</summary>
    protected bool TryWriteError(ref MemoryResult<byte> output)
    {
        if (_error is null) return false;
        WriteError(ref output, _error);
        return true;
    }
}
```

Each command becomes:

```csharp
public override bool Prepare<TApi>(TApi api, ref CustomProcedureInput input)
{
    var idx = 0;
    if (!TryReadIdentifier(ref input, ref idx, "service", _opts.MaxIdentifierBytes, out _service)) return true;
    if (!TryReadIdentifier(ref input, ref idx, "requestId", _opts.MaxIdentifierBytes, out _requestId)) return true;
    if (!TryReadPayload(ref input, ref idx, _opts.MaxPayloadBytes, out _payload)) return true;

    AddKey(CreateArgSlice(HighwayKeys.ServiceQueue(_service)), LockType.Exclusive, StoreType.Object);
    return true;
}

public override void Main<TApi>(TApi api, ref CustomProcedureInput input, ref MemoryResult<byte> output)
{
    if (TryWriteError(ref output)) return;
    // ... unchanged body ...
}

public override void Finalize<TApi>(TApi api, ref CustomProcedureInput input, ref MemoryResult<byte> output)
{
    if (Failed) return;              // never ring a doorbell for a rejected command
    _doorbell.Ring(...);
}
```

`Prepare` now returns `true` in every case. Running a transaction that locks zero keys is safe: `LockAllKeys()` over an empty set is a no-op and `ValidateWatchVersion()` with no registered watches returns true, so `Run` succeeds and `Main` gets to write the error.

**The `Finalize` guard is not optional.** `HW.CALL`, `HW.REPLY` and `HW.PUBLISH` ring doorbells there. Without the guard, a rejected command would wake workers for a request that was never enqueued.

### Error contract

| Message prefix | Meaning | Class | Emitted by |
|---|---|---|---|
| `ERR HW_INVALID_ARG <detail>` | Identifier blank, contains a control character, or exceeds the length cap | Permanent | all commands |
| `ERR HW_PAYLOAD_TOO_LARGE <actual> > <limit>` | Payload above `MaxPayloadBytes` | Permanent | `HW.CALL`, `HW.REPLY`, `HW.PUBLISH` |
| `ERR HW_INVALID_COUNT <detail>` | `COUNT` non-numeric, zero, negative, overflowing, or above `ReceiveMaxCount` | Permanent | `HW.RECEIVE` |
| `ERR HW_INTERNAL <detail>` | Unexpected exception escaped into the `Main` catch block | Permanent — indicates a server bug | all commands |
| `ERR Transaction failed.` | Garnet aborted the transaction (watch conflict) | **Transient — retry** | `HW.DEQUEUE`, `HW.PUBLISH`, `HW.SUBSCRIBE`, `HW.UNSUBSCRIBE` |
| `ERR wrong number of arguments...` | Garnet arity check, before `Prepare` runs | Permanent | all commands |

The classification rule a client implements is one line: **a message starting with `ERR HW_` is permanent; the bare `ERR Transaction failed.` is transient; anything else is permanent.** Renaming today's `ERR internal:` to `ERR HW_INTERNAL` is what keeps that rule total.

`HW_INTERNAL` is classified permanent deliberately. Because `HW.ACK`, `HW.RACK` and the lease sweeps pop a whole list and re-push it, an exception mid-loop can leave entries dropped; retrying compounds the damage rather than repairing it. Surfacing it loudly is the correct response.

### Identifier rules

Applied to service, channel, group, node, request and message identifiers — never to payloads, which stay byte-for-byte opaque.

```
valid(id) ⟺ 0 < len(id) ≤ MaxIdentifierBytes
            ∧ ∀ b ∈ id : b ≥ 0x20 ∧ b ≠ 0x7F
```

`MaxIdentifierBytes` defaults to 256. The control-character ban exists because mirror keys are newline-delimited: an identifier containing `\n` splits into two entries and silently corrupts routing. Banning the whole C0 range plus DEL is cheaper to reason about than banning `\n` alone, and it costs nothing real — identifiers are service names and node names.

Validation is on raw bytes before any string decode, so it runs before a key is ever derived from the value.

## Re-Subscribe Idempotency (Requirement 1)

`IGarnetApi.SetAdd` reports how many members were actually added. That is the signal the current code discards.

```
HW.SUBSCRIBE <channel> <group>

Main:
  SetAdd(hw:ch:{channel}:groups, group) → added        # 1 = new, 0 = already present
  repair mirror hw:ch:{channel}:grplist               # unconditional, already contains-checked
  if added == 1:
      CopyBacklogToGroup()                            # ONLY for a genuinely new group
  reply +OK
```

```
Before                                   After
──────                                   ─────
SUBSCRIBE ch grp-a  → backlog copied     SUBSCRIBE ch grp-a  → added=1 → backlog copied
RECEIVE             → m1, m2             RECEIVE             → m1, m2
SUBSCRIBE ch grp-a  → backlog copied ✗   SUBSCRIBE ch grp-a  → added=0 → no copy ✓
RECEIVE             → m1, m2  (dup!)     RECEIVE             → (empty)
```

The set — not the mirror — is authoritative for the added/not-added decision, because the set is the durable structure and the mirror is a derived cache. The mirror repair stays unconditional so that an inconsistent mirror self-heals on the next subscribe.

`HW.UNSUBSCRIBE` removes the group from the set and deletes its queue, so a later re-subscribe reports `added == 1` and legitimately receives the backlog again. That is Requirement 1 AC4, and it falls out of the design rather than needing a special case.

## Configurable Test Server (Requirement 4)

`HighwayServerOptions` becomes public. It is already the server's real configuration model, `HighwayServerBuilder` is already public, and `product.md`'s hosting section anticipates `ConfigureHighwayServer(o => ...)` — a public options type is where this was going regardless. Introducing a parallel `HighwayTestServerOptions` would only create two shapes to keep in sync.

```csharp
public sealed class HighwayTestServer : IDisposable, IAsyncDisposable
{
    public HighwayTestServer();                                     // unchanged
    public HighwayTestServer(int? maxPayloadBytes = null);          // unchanged — keeps 34 tests green
    public HighwayTestServer(Action<HighwayServerOptions> configure);  // NEW

    public string ConnectionString { get; }   // stable across Restart()
    public int Port { get; }                  // NEW

    /// <summary>Disposes the inner server and starts a new one on the same
    /// port and data directory. With a data directory configured this exercises
    /// AOF recovery; memory-only, it starts empty.</summary>
    public void Restart();                    // NEW
}
```

`Restart()` is what makes Requirement 5's durability tests and 005's server-restart-tolerance test expressible at all. Because the port is held constant, a client's connection string — and its `ConnectionMultiplexer` — stays valid across the restart. `Port` is exposed because a test that reconnects manually needs it.

`configure` runs against a pre-populated options object whose `Port` is already set to the probed ephemeral port; the delegate is documented as not able to change `Port`, and the value is re-asserted after the delegate returns.

## Bind Address (Requirement 8)

```csharp
public IPAddress BindAddress { get; set; } = IPAddress.Loopback;   // HighwayServerOptions

public HighwayServerBuilder WithBindAddress(IPAddress address);
public HighwayServerBuilder WithBindAddress(string address);       // parsed; throws on invalid
```

`BuildGarnetOptions` maps it to `EndPoints = [new IPEndPoint(opts.BindAddress, opts.Port)]`. The default stays loopback so that nothing becomes remotely reachable by upgrading. `IHighwayServer.Endpoint` returns `{BindAddress}:{Port}` instead of the hardcoded `localhost:{Port}`; `HighwayTestServer.ConnectionString` continues to return `localhost:{Port}` and is unaffected. Parse failure throws at `Build()` with the offending string in the message.

## Test Strategy

The tests are the substance of this feature — six of the ten requirements exist because a 004 behavior was implemented but never exercised.

| File | Status | Covers |
|---|---|---|
| `Integration/ErrorContractTests.cs` | new | Req 2, Req 3 — every error code, exact message prefixes, transient-vs-permanent separability |
| `Integration/DurabilityTests.cs` | rewritten | Req 5 — AOF restart survival for queues, groups, backlog, reply slots; memory-only loss; key coexistence |
| `Integration/LeaseRecoveryTests.cs` | new | Req 6 — RPC requeue, pub/sub head-requeue, ACK-after-requeue, lease disabled |
| `Integration/DoorbellTests.cs` | new | Req 7 AC5–AC7 — all three doorbell shapes via a real subscriber, no-ring-on-replay, `HW.RECEIVE` reply shape |
| `Integration/RetentionTests.cs` | new | Req 7 AC1–AC4 — reply TTL expiry, last-writer-wins, backlog retention window, entry cap |
| `Integration/ServerIsolationTests.cs` | new | Req 5 AC6 — receives the isolation test currently misfiled in `DurabilityTests.cs` |
| `Integration/PubSubFlowTests.cs` | extended | Req 1 — re-subscribe idempotency; must fail before the fix |
| `Server.Tests/IdentifierTests.cs` | new | Req 3 — validation rules at the unit level, including boundary bytes 0x1F/0x20/0x7E/0x7F |
| `Server.Tests/HighwayErrorsTests.cs` | new | Req 2 — message formatting and code stability |
| `Server.Tests/HighwayServerBuilderTests.cs` | extended | Req 8 — bind-address mapping, invalid-address rejection, endpoint rendering |

Three of the doorbell assertions and the `HW.RECEIVE` shape assertion have already been run as throwaway probes during review and passed; this feature promotes them to permanent tests so the behavior 005 depends on cannot regress silently.

The re-subscribe test in `PubSubFlowTests.cs` must be written and observed **failing** before the `SetAdd` fix lands. It is the only test here that proves a defect rather than covering a gap.

## Error Handling Strategy

| Situation | Server behavior | Client (005) reading |
|---|---|---|
| Blank / control-char / oversize identifier | `ERR HW_INVALID_ARG` before any key is derived | permanent — surface as data, never retry |
| Oversize payload | `ERR HW_PAYLOAD_TOO_LARGE` before enqueue | permanent — 413 data |
| Bad `COUNT` | `ERR HW_INVALID_COUNT`, no state read | permanent — configuration bug, fail loudly |
| Watch conflict | `ERR Transaction failed.`, no work performed | **transient — bounded retry with backoff** |
| Exception inside `Main` | `ERR HW_INTERNAL`, partial state possible | permanent — log at error, do not retry |
| Valid command | unchanged success reply | unchanged |

## Impact on Feature 005

This feature changes five things in `005/tasks.md`. They are listed here so the 005 spec can be amended in one pass.

1. **Task 1 (spikes) reduces to one open question.** The `HW.RECEIVE` nested-pair shape is confirmed and becomes a permanent test here (Req 7 AC7). Doorbell *delivery* is confirmed for all three channel shapes and becomes a permanent test here (Req 7 AC5). Only SE.Redis resubscribe-on-reconnect remains genuinely unknown.

2. **Tasks 4, 7, 8 gain a concrete retry policy.** `HighwayConnection` classifies replies by the rule above. The worker and consumer loops retry only the transient class, with a bounded attempt count — not the unbounded "loops never die" retry currently specified, which would spin forever on a permanent error. `PublishAsync` **must** retry the transient class, because a watch-conflicted `HW.PUBLISH` silently delivered nothing.

3. **Task 11 is unblocked.** The engine's start-time `HW.SUBSCRIBE` per channel no longer duplicates backlog on restart, so 005 can keep its "subscribe on every start, never unsubscribe" model (005 Req 9 AC3) as designed.

4. **Task 14 step 4 becomes implementable.** `HighwayTestServer.Restart()` holds the port, so "restart the server mid-session and assert the engine recovers" can be written. As specified it could not be: each `HighwayTestServer` took a fresh ephemeral port, so the client would have reconnected to a dead endpoint.

5. **Task 2's `NodeName` validation is load-bearing.** It is the client-side half of Requirement 3 and should say so — it is what keeps a user's configuration from corrupting server routing state, not a cosmetic rule. The server no longer trusts it, but the client should still fail fast and locally.

## Risks

| Risk | Mitigation |
|---|---|
| ~~Zero-key transactions behave unexpectedly in some Garnet path not yet exercised~~ **RESOLVED (Task 1, verified 2026-08-06):** a scratch `HW.SPIKE` transaction whose `Prepare` added zero keys and returned `true` ran `Main` normally: the custom error written in `Main` reached the client verbatim (`ERR HW_SPIKE_REJECTED bad argument`, not `ERR Transaction failed.`), the same connection performed a full `HW.CALL`/`HW.DEQUEUE` round trip immediately afterwards, and `Finalize` ran on **both** the success and the rejected paths — confirming the `Failed` guard in `Finalize` is necessary, not incidental. Scratch code removed; the sentinel-key fallback was not needed. | — |
| Making `HighwayServerOptions` public freezes a shape that later needs to change | It is a mutable options class with defaults, the standard .NET pattern; additive fields are non-breaking |
| Rebasing all nine commands regresses working behavior | The 127 existing tests run after every command; the rebase is mechanical and the command bodies are untouched below the first line of `Main` |
| Restart-based tests are slow or flaky | Restart reuses the probed port; durability tests use a temp directory per test and are the only tests permitted to touch disk |
| Watch conflicts prove common enough to hurt throughput | Out of scope to fix here, but Req 9 AC1 documents the exposure so 005's benchmarks can measure it |

## Dependencies & Constraints

- Depends on 004 being merged (it is). No dependency on 005.
- `libs/garnet` submodule pin is unchanged — this feature adds no requirement on Garnet APIs beyond what 004 already uses.
- `docs/product/product.md` and `docs/product/research.md` are read-only and are not touched. `docs/product/roadmap.md` is amended for status accuracy only (Req 9 AC7).
- Coding standards apply unchanged: .NET 10, file-scoped namespaces, xUnit + FluentAssertions, `CancellationToken` on async APIs, zero build warnings.

## Cross-References

- Requirements: `docs/features/004.1-server-remediation/requirements.md`
- Feature under remediation: `docs/features/004-server-hw-commands/design.md`
- Consumer: `docs/features/005-client-server-communication/design.md` § "Pinned contract inputs"
- Garnet internals relied on: `libs/garnet/libs/server/Transaction/TransactionManager.cs`, `libs/garnet/libs/server/Custom/CustomRespCommands.cs`, `libs/garnet/libs/server/Custom/CustomTransactionProcedure.cs`
