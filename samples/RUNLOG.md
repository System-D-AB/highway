# Sample Run Log

Every run of the samples, newest first. Running the samples is a **test** — it
exercises what no unit or integration test reaches: a standalone broker process,
RESP over a real socket between OS processes, `Console.CancelKeyPress` shutdown,
generic-host lifecycle, and assembly scanning across a project boundary.

A sample that fails to start is a test failure. It is triaged like one: symptom,
root cause, fix **in the library**, regression test. Editing the sample to avoid
a broken path is not an acceptable fix — that converts a product defect into a
documentation defect and loses it.

**Entry format:** date, what was run, what it found, what was done.

---

## 2026-08-07 — feature 002 (observability)

**Libraries:** features 001–007 and 010 merged, plus the flight recorder.
**Ran:** broker + order service + storefront as three processes; the core
scenarios plus the new `replay` and `stats recorder` commands.

### Finding 6 — a rejection poisoned every later call on the same connection *(defect, fixed)*

Found while investigating a duplicate event in the recorder, not by looking for
it.

**Symptom.** The sample's recorder showed **two** `RpcClaimed` events for one
order. Chasing that led to a far worse one: on a single connection, a command
that failed validation caused every *subsequent* invocation of that command to
return the **previous** call's error. A valid `HW.CALL` answered
`ERR HW_PAYLOAD_TOO_LARGE 100 > 16` — a rejection belonging to a request sent
earlier.

**Root cause.** Garnet caches one procedure instance per session
(`CustomCommandManagerSession.sessionTransactionProcMap`) and reuses it for every
invocation of that command on that connection. `HighwayCommandBase` never cleared
its captured error, so the first rejection stuck. The duplicate `RpcClaimed` was
the same mechanism: a claimed request ID left over from a successful dequeue made
each later *nil* dequeue re-record a claim that never happened.

In production this is serious. The 005 client shares one multiplexer per node, so
a single oversize payload would have made that command fail for the whole node
until it reconnected.

**Fix.** `HighwayCommandBase.Prepare` is now `sealed`: it clears per-invocation
state and delegates to a new abstract `PrepareCore`, and commands override
`ResetState()` for their own conditionally-assigned fields. Sealing makes the
class of bug structurally impossible rather than fixed once. Covered by
`SessionStateIsolationTests` (5 tests).

**Why every test missed it.** Each test used a fresh connection, or never issued
a good call after a bad one on the same connection.

### Finding 7 — the "upstream Garnet parser quirk" was Highway's own bug *(correction)*

Feature 004.1 recorded a finding that a rejected `HW.*` command containing a raw
newline desynced subsequent custom-command parsing on the same session, and
attributed it to Garnet. `NewlineDesyncProbe` asserted the broken behaviour with
a note to "flip this assertion when Garnet fixes it".

Fixing finding 6 made that test fail: the follow-up command now succeeds. The
desync was never Garnet's — it was the same state leak. Newlines were incidental;
**any** rejection did it. The probe has been rewritten to document the correction,
and the misattribution is recorded rather than quietly deleted.

### Verified

| Scenario | Result |
|---|---|
| RPC, errors-as-data, publish/subscribe (all 010 scenarios) | ✅ unchanged |
| `replay orders.create` | ✅ RpcEnqueued → RpcClaimed → RpcAcknowledged, in order, with sizes |
| Rejected command recorded with its error code | ✅ `HW_PAYLOAD_TOO_LARGE` |
| `stats recorder` | ✅ enabled, names, events, bytes, drop counters, failures 0 |
| Recording adds no keys to the Garnet keyspace | ✅ `KEYS hw:fdr:*` empty |
| Liveness heartbeats not recorded | ✅ registration only |
| OTEL wiring shown in the broker sample | ✅ copy-pasteable, no package added |

Before the fix the same run showed a phantom duplicate `RpcClaimed`; after it,
the lifecycle reads exactly as it should. The recorder found a bug in Highway on
its first real use, which is roughly the best argument for it.

---

## 2026-08-07 — first run (feature 010)

**Libraries:** features 001–007 merged, 440 tests green before the run.
**Platform:** Windows 11, .NET 10.0.7, Garnet 2.1.2 (pinned submodule).
**Ran:** broker + order service + storefront as three separate processes; all
eleven scenarios in `README.md`, including a real LAN interface.

This was the first time Highway ran as a deployed system rather than inside a
test host.

### Finding 1 — a caller-only node could not call anything *(defect, fixed)*

**Symptom.** The storefront returned
`SERVICE_NOT_FOUND: The request type 'Highway.Samples.Contracts.CreateOrder' is
not registered in this node's catalog` for a service that was running in another
process at that moment. Every RPC failed. The order service, by contrast, worked
fine.

**Root cause — not the one predicted.** The spec anticipated a lazy-assembly-
loading problem. That was a real weakness and was fixed (finding 2), but it was
not the cause. `ImmutableCatalog` built its request-type → service-name map from
**locally hosted service implementations**:

```csharp
_requestTypeToServiceName = services.ToFrozenDictionary(s => s.RequestType, s => s.Name);
```

and `DiscoverServices` only finds classes extending `AsyncService<,>`. A process
that hosts nothing therefore has an empty map and can address nothing. The same
applied to channels: `GetChannelNameForMessageType` was derived from local
`ISubscribe<T>` implementations, so a node could not publish to a channel it did
not itself consume.

This breaks goal G4 (location transparency) and the headline example in
`product.md`, where a caller in another process simply calls
`client.ExecuteAsync(new CreateOrder { ... })`.

**Why 440 tests missed it.** Every integration-test node scans the *same test
assembly*, which contains both the contracts and their implementations. Every
node hosts everything, so no test ever exercised a genuine caller-only node.
This is precisely the blind spot the samples exist to find.

**Fix (library).** Addressing now derives from the contract, not from what
happens to be hosted. `ScanResult` gained `RequestContracts` and
`MessageContracts`, populated from `[Service]` / `[Channel]` on the contract
types themselves; `ImmutableCatalog` builds its lookups from those, with hosted
implementations folded in on top. A contract type *without* the attribute is
skipped rather than rejected — the local-404 path depends on that.

**Regression tests.** Four in `CatalogTests` (a node hosting nothing still
resolves a service name; a node with no subscriber still resolves a channel; a
hosted service stays addressable; an unattributed type still returns null) and
four in `TypeScannerTests` (contract discovery with no implementation, for both
services and channels).

### Finding 2 — assembly scanning depended on load order *(weakness, fixed)*

**Symptom.** None directly observable, because finding 1 masked it.

**Root cause.** `DefaultAssemblySource` filtered
`AppDomain.CurrentDomain.GetAssemblies()`. The runtime loads assemblies lazily,
so a contracts assembly that nothing has touched yet is simply absent. Discovery
therefore depended on what the runtime happened to have needed so far — which
differs between a host (whose service classes force the contracts assembly to
load) and a caller (whose contracts are referenced only from method bodies).

**Fix (library).** Discovery now seeds from the loaded set *and* walks the entry
assembly's reference closure, loading what it finds and skipping framework
assemblies by name prefix. Unresolvable references are ignored rather than
fatal, so trimmed and plugin-style deployments still work.
`AdditionalAssemblies` remains for genuinely dynamic cases.

### Finding 3 — no public API for discovery or stats *(usability, recorded)*

`HW.DISCOVER` and `HW.STATS` shipped in feature 006, and `HighwayConnection` has
`DiscoverAsync` and `StatsAsync` — but that type is `internal`. `IHighwayClient`
exposes only `ExecuteAsync` and `PublishAsync`, so **an application cannot reach
discovery or stats through `Highway.Client` at all.**

The storefront's `discover` and `stats` commands therefore open their own
StackExchange.Redis connection and issue raw RESP. That works, and it
demonstrates the protocol is usable by any RESP client — but it is not the
experience a .NET user should have with a shipped feature.

Not fixed here: adding public API is a design decision beyond this feature's
scope. Recorded so it is a decision rather than an oversight.

### Finding 4 — the default experience for an unroutable call is a 30-second hang *(usability, recorded)*

After finding 1 was fixed, `cancel ORD-1` — a valid contract nothing hosts —
blocked for the full 30-second `CallTimeout` and returned `504 CALL_TIMEOUT`.
Before the fix it failed instantly, but only because the caller could not
address *anything*.

Fast-fail (006 Requirement 6) is exactly the remedy and turns this into a 1 ms
404, but it is **off by default**. The out-of-box experience for calling a
service nobody hosts is therefore a 30-second wait.

The storefront enables `FastFailEnabled` and says why in a comment. Whether the
default should change is a product decision, recorded here rather than made.

### Finding 5 — first-start recovery logs an alarming stack trace *(cosmetic, not fixed)*

A broker starting with an empty data directory logs, at **info** level, a
`TsavoriteNoHybridLogException` stack trace reading "Unable to find valid
HybridLog token". This is Garnet's normal first-start path and nothing is wrong,
but it reads as a crash to anyone starting Highway for the first time. Upstream
behaviour; noted, not worked around.

### Scenarios verified

| # | Scenario | Result |
|---|---|---|
| 1 | RPC across three processes | ✅ typed response returned |
| 2 | Errors are data | ✅ 404 + `ORDER_NOT_FOUND`, nothing thrown |
| 3 | Unroutable service | ✅ fast-fail 404 in **1 ms** (30,014 ms without it — finding 4) |
| 4 | Caller is also a publisher | ✅ order service received `InventoryLow` |
| 5 | Fan-out to two nodes | ✅ `shop-1` and `shop-2` each got their own copy |
| 6 | Competing consumers | ✅ 6/6 responses, work split 3/3 across two hosts |
| 7 | **Durable delivery across downtime** | ✅ both events missed while `shop-1` was down arrived on restart |
| 8 | Broker restart with AOF | ✅ event queued for an offline group survived and was delivered |
| 9 | Broker not running | ✅ names the endpoint, no stack trace |
| 10 | Service host failover | ✅ calls succeeded via the survivor |
| 11 | `discover` / `stats` | ✅ correct topology (via raw RESP — finding 3) |
| — | **Cross-machine** | ✅ RPC **and** pub/sub over `192.1.2.103` with `--bind 0.0.0.0`, not loopback |

Scenario 7 is product success criterion 2, demonstrated across real processes for
the first time.

**A false alarm worth recording.** Scenario 7 initially appeared to fail — the
restarted subscriber printed nothing. The cause was the test harness quitting
milliseconds after startup, before the consumer loop's first drain. Re-run with
a delay, both events arrived. The product was fine; the measurement was not.

### Cross-machine caveat

Verified over a second local network interface (`192.1.2.103`), not a second
physical machine. That exercises the real socket path, `--bind 0.0.0.0`, and
non-loopback routing, but not a physical network hop, NAT, or a firewall between
hosts. Recorded as partial rather than claimed as complete.
