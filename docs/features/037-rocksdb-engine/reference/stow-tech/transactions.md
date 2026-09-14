# Transactions — procedure anatomy, locking, failure, replay

*Authority: mechanics and measurements. For **why** atomicity is scoped per document see
`D10`/`D11`; for the wire shape of each command see `../STOW-PROTOCOL.md` (spec `003`).*

**Engine version:** established against **Microsoft.Garnet 2.1.3** (baseline `8b329e30`).
Re-verify when the D32 pin moves — several behaviours below are source-observed rather
than documented.

---

## 1 · The three phases

Every `ST.*` write is one Garnet custom transaction procedure with three phases
(`CustomTransactionProcedure`):

| Phase | API available | Can write RESP? | Runs at AOF replay? |
|---|---|---|---|
| `Prepare` | read-only (`IGarnetReadApi`, wrapped in `GarnetWatchApi`) | **No** — no output parameter exists | Yes |
| `Main` | full (`IGarnetApi`), under all locks | Yes | Yes |
| `Finalize` | full, **non-transactional**, per-key locks | Yes | **No** |

Execution order inside `TransactionManager`: `Prepare` → acquire every declared lock →
validate watch versions → `Main` → write the AOF entry → commit → `Finalize`.

**`Finalize` runs on both the success and failure paths**, and any exception it throws is
silently swallowed. Anything side-effecting there needs an explicit "did we fail?" guard.

## 2 · Declaring the key set, and validate-in-`Main`

`Prepare` declares every key `Main` will touch via `AddKey(key, lockType, storeType)`.
Two rules follow from the engine's behaviour:

- **`Prepare` cannot report errors.** Returning `false` produces the fixed string
  `ERR Transaction failed.` and nothing else. So validation *captures* its error in a
  field, adds **no keys**, and returns `true`; `Main`'s first act is to render that error
  and return without touching state. This is the **validate-in-`Main`** pattern.
- **The declared key list is safety-critical.** The transactional session locker only
  `Debug.Assert`s that an operated-on key was locked — in a Release build, writing a key
  you forgot to declare proceeds **with no lock, silently**.

Locks are hash-bucket locks, acquired all-at-once in a globally sorted order (so
transactions cannot deadlock against each other), and **blocking by default**:
`FailFastOnKeyLockFailure` is `false`, so contention appears as latency, never as an
error. `KeyLockTimeout` only applies if you opt into fail-fast.

## 3 · Watch validation: the transient-abort contract

Every read performed in `Prepare` is **automatically watched** — the read API is wrapped
so each read snapshots that key's version. After locks are taken and before `Main` runs,
the versions are revalidated; a mismatch aborts the transaction **having done no work**.

This is the mechanism that makes membership discovery safe: `Prepare` reads the document's
membership record to learn which stale index entries must be locked, and if a concurrent
save changes it in between, the transaction aborts cleanly instead of cleaning up a stale
key set. *(Forced experimentally: a writer parked inside `Prepare` while another mutated
the same document's membership aborted with the transient signal and no partial cleanup.)*

**The client contract that falls out of this:** see
[STOW-PROTOCOL.md § Error Codes](../STOW-PROTOCOL.md#-error-codes) for the full
classification rule and code table. The essential point here is that
`ERR Transaction failed.` is the engine's transient signal — Stow never emits it
itself — and the client must retry it with bounded back-off.

**Retry is mandatory in the client, not optional.** Two measured sources of transients on
logically independent work:

- **Watch-version false positives.** The version map is hash-indexed, so unrelated keys
  collide. Measured on saves to *disjoint documents*: **0.02 % at 16 concurrent writers,
  0.09 % at 64**. Small, but nonzero — an unretried client sees rare, unexplainable
  failures.
- **The post-fault one-shot.** After an exception inside `Main`, the session's watch state
  is left dirty and its **next** transaction aborts once before clearing. Retry budgets
  must assume at least one free transient after any server-side fault.

## 4 · Failure semantics: there is no rollback

**An exception inside `Main` does not undo writes already performed.** The engine's catch
path releases locks and resets transaction state; it never touches data. Worse, because
the AOF entry is written *after* `Main` returns, a mid-`Main` exception leaves writes in
memory that were **never logged** — memory and recovery state diverge silently.

Two hard design rules follow:

1. **Preflight, then a no-fail commit section.** Every expected failure — version
   conflict, unique violation, bounds, validation — is detected *before* the first
   mutation. After the first write there must be no expected failure path.
2. **The procedure catches its own exceptions** and emits a distinct permanent code.
   Otherwise an internal bug is indistinguishable on the wire from a transient abort, and
   clients will happily retry it — compounding damage rather than surfacing it.

**Measured behaviour** (exception injected after each of the nine commit-section
mutations, update path with maximum membership churn): every injected fault left the
completed prefix in place, and a bounded client retry converged to a fully consistent
state for faults through step 7; faults after the document write surfaced the documented
version conflict instead (the C17 ambiguous-outcome case). Detail:
`../specs/001-phase0-spike/artifacts/fault-matrix.md`.

**Rejections mutate nothing.** Validation, version-conflict, unique-violation and
bad-envelope rejections were each verified by full key inspection to leave the store
untouched.

**Commit order matters.** Order the commit section so that any prefix of it is repairable
by a client retry: stale-entry cleanup first, then new entries, then membership, then the
document, then the counter. One consequence the spike measured: a **naively incremented
count is not fault-idempotent** — a fault between the document write and the increment
leaves a permanent drift that a later delete makes worse. The count must be derived or
reconciled, not blindly incremented (spec `004`).

## 5 · AOF replay — the persistent-format contract

> **Authority:** the binding contracts are in
> [STOW-PROTOCOL.md § Persistent-Format Contracts](../STOW-PROTOCOL.md#-persistent-format-contracts).
> This section explains the mechanism and the spike evidence that forced them.

A custom transaction is logged as **one `StoredProcedure` AOF entry** carrying the
**procedure id and the raw arguments** — not the individual writes (`StoredProcMode`
suppresses those). Replay **re-executes the procedure**. Three obligations follow:

1. **Replay determinism.** The procedure must produce identical effects given the same
   arguments. No wall-clock reads, no generated ids, no randomness inside `Main` —
   timestamps and ids arrive as arguments (which is why ids are generated client-side,
   D27).
2. **Registration order is a persistent format.** Procedure ids are assigned positionally
   at registration; nothing in the AOF records a name. **Changing the order silently
   replays the wrong procedure.** Measured: reordering three procedures caused every
   logged save to replay through the delete procedure — 10 of 10 documents destroyed,
   no error raised. Registration is therefore **append-only, forever**, and a manifest
   recorded beside the data must be verified at startup and refuse a mismatch.
3. **`FailOnRecoveryError` must be enabled.** By default an unknown procedure id during
   replay is **swallowed** — measured: a deleted document silently resurrected because
   its delete entry could not be replayed. Fail-closed is opt-in, and Stow must opt in.

`Finalize` is skipped during replay, which makes it the correct home for post-commit
notifications (they must not re-fire during recovery).

**One open defect** affects replay under constrained log memory — see
`storage-model.md` § 4.

## 6 · What a procedure may not do

- **Publish.** Custom procedures have no pub/sub surface; the broker is reachable only
  from the host (a `GarnetServer` subclass). Any notification therefore happens
  post-commit at the host layer — which is one reason the change feed is deferred (D36).
- **Run user code.** The server never executes user-supplied write-time logic: index
  entries are computed client-side and validated server-side, migrations run in the
  client fleet. This keeps the server a byte-shuffler (D15/D26) and avoids a
  stored-procedure security surface.
- **Block.** `Main` runs on the session thread holding locks: no I/O, no awaits, no
  allocation beyond the fixed floor documented in `storage-model.md` § 3.
