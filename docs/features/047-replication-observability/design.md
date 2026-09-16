# Feature 047 — Design

## Threading a logger in

`RespHighwayServer.Start` already holds an `ILoggerFactory` and is where the store is opened and the
`ReplicaPuller` is created. It creates one category — `"Highway.Replication"` — and hands it to:

- `RocksDbStore.Open(dir, ownsDirectory, replication, logger)` — a new **optional** `ILogger?`
  parameter (default null), used only to log the snapshot bootstrap that already happens there.
- `ReplicationFeeder.Logger` — a settable property, set right after the store opens (runtime
  transitions happen later, so this catches them all).
- `new ReplicaPuller(store, options, logger)` — a new **optional** `ILogger?` parameter.

Optional params keep `HighwayTestServer` (which opens the store and builds the puller for tests)
unchanged, and tests stay quiet.

## Feeder logging — route the transition tail

`ReplicationFeeder` already enqueues a human-readable string to `_transitions` at every important,
low-frequency event (promote / fence / unfence / demote / adopt-epoch / goodbye-begin/complete /
join-refused / resync-required / promote-refused / epoch-persist-failed). A small helper routes each
to the log as well:

```csharp
private void Note(string transition, LogLevel level = LogLevel.Information)
{
    _transitions.Enqueue(transition);
    Logger?.Log(level, "[replication] {Transition}", transition);
}
```

Each `_transitions.Enqueue(x)` becomes `Note(x, <level>)` — problem transitions (fence, demote,
join-refused, resync-required, promote-refused) at Warning, `epoch-persist-failed` at Error, the rest
at Information. Because these are the only enqueue sites and none is on the per-pull path, this gives
comprehensive, non-spammy coverage for free.

Two events are not transitions and are logged directly:
- **Replica attached:** in `Hello`, when the slot is newly created (guarded by a "was it already
  there" check so the per-HELLO frequency does not log), log `Replica {id} attached (watermark {n})`.
- **Slot dropped:** in `EnforceCap`, in the branch that sets `SlotState.Dropped`, log a Warning.

## Replica-puller logging — connection transitions, not traffic

`ReplicaPuller` gains an `ILogger?` and a `_connected` flag so it logs edges, not every pull:

- Loop entry (once): `Replica active: following {primary} as {id} (priority {p})`.
- On a successful connect + JOIN (transition to connected): `Replica connected to primary {primary}`.
- On the reconnect path (transition to disconnected, in the loop's catch): `Replica lost the primary
  connection; retrying` (Warning).
- On `HW_REPL_GAP` (`MarkResyncRequired`): `Replica WAL gap at seq {n}; re-bootstrap required —
  restart to re-snapshot` (Warning).
- The snapshot bootstrap itself is logged by `RocksDbStore.Open` (it runs there, before the puller):
  `Replica data directory blank; bootstrapping snapshot from {primary}` and `Snapshot applied
  (seq {n}) in {ms} ms`.

## Dashboard (R2)

`replication.js` already renders `role`/`epoch`/`slots`. Add: render the `roster.*` fields (both
nodes, priority, endpoint) as a small table, and — when `role == Replica` — show `repl.endpoint`
(the primary it points at, from its config) and its watermark/lag instead of an empty slot table.
The distribution README's dashboard note gains the loopback-bind caveat.

## Testing strategy

| Layer | Proof |
|---|---|
| Feeder | a captured `ILogger` receives a line on promote / demote / fence / goodbye / slot-drop; nothing on a plain Hello/Ack/Pull |
| Puller | (covered by integration) a replica connecting logs "connected"; a gap logs the resync warning |
| No-op | replication disabled → no new log output; existing replication tests stay green |

## Non-goals

No protocol or failover-behaviour change; observability only.
