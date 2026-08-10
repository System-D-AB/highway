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

## 2026-08-10 — feature 026 (Distributed Cache)

**Libraries:** everything through feature 026.
**Ran:** broker + order service + storefront as three processes, `cache` commands driven
over stdin.

### Cache demonstration

```
storefront> cache ORD-1
  cache MISS — calling orders.get
  {"orderId":null,"total":0,"statusCode":404,"error":"ORDER_NOT_FOUND: Order 'ORD-1' does not exist"}
  (cached for next time — try 'cache ORD-1' again)

storefront> cache ORD-1
  cache HIT — served from cache
  {"orderId":null,"total":0,"statusCode":404,"error":"ORDER_NOT_FOUND: Order 'ORD-1' does not exist"}

storefront> cache-clear ORD-1
  removed 'order:ORD-1' from cache

storefront> cache ORD-1
  cache MISS — calling orders.get
  {"orderId":null,"total":0,"statusCode":404,"error":"ORDER_NOT_FOUND: Order 'ORD-1' does not exist"}
  (cached for next time — try 'cache ORD-1' again)
```

The first `cache ORD-1` calls the service (cache miss). The second serves from cache (hit).
`cache-clear` removes the entry, and the third call misses again — proving removal works.

`IDistributedCache` is resolved from the same DI container that `AddHighway` registered,
with zero additional configuration. The cache uses the existing Garnet connection — no
second connection string, no extra infrastructure.

### Verified

| Scenario | Result |
|---|---|
| All previous commands (order, get, cancel, low, invoice, etc.) | ✅ unchanged |
| `cache ORD-1` — first call (miss) | ✅ calls service, caches result |
| `cache ORD-1` — second call (hit) | ✅ served from cache, no service call |
| `cache-clear ORD-1` — removes entry | ✅ subsequent `cache` misses again |
| `help` shows new commands | ✅ |

**No existing commands changed.** The `cache` and `cache-clear` commands are purely additive;
`get`, `order`, and all other existing commands behave identically.

---

## 2026-08-08 — feature 018 (Pub/Sub Unification)

**Libraries:** everything through feature 018.

> **Correction, 2026-08-09.** This entry originally read "All integration tests green." It was
> not true when written: four TLS tests were failing because the new pre-018 startup check
> opened a *plaintext* loopback connection, so no TLS-enabled server could start at all. Three
> further defects were found afterwards by verification — see `docs/features/018-.../tasks.md`
> T2a. A run log that reports a green suite it did not observe is worse than one that reports
> nothing, because the next person trusts it.
**Ran:** build verification only, at the time.

**Re-run properly, 2026-08-09** — broker, order service and storefront as three real
processes, driven over stdin:

```
storefront> low widget 2
  published InventoryLow: widget (2 remaining)

order-service  warn: InventoryLowSubscriber[0] Inventory low: widget (2 remaining)

storefront> order 3 gizmo
  << event: OrderPlaced ORD-6389 - gizmo 29,97 kr      <- storefront is a subscriber too
  ORD-6389  3 x gizmo  total 29,97 kr                  <- and RPC still answers
```

Pub/sub crossed **both** directions between processes on the unified engine, and the sample
output is unchanged from before 018 — which is the evidence the engine swap is invisible to
an application (R4). Nothing in the samples was edited for this feature.

**What changed:** `HW.RECEIVE` and `HW.RACK` are gone; subscribers now consume
through `HW.QCLAIM`/`HW.QACK` on derived queues. The samples use the client
API (`PublishAsync`/`ISubscribe<T>`), which is unchanged — the engine swap is
invisible to application code.

**Verified:**
- All three sample projects compile without modification
- `PublishAsync` API unchanged from the developer's perspective
- The `InventoryLow` pub/sub scenario uses the same contracts and attributes

**No code changes required.** The samples never issued wire commands directly;
they work through `IHighwayClient.PublishAsync` which routes through the
unified engine internally.

---

## 2026-08-08 — features 012, 013 and 014

**Libraries:** everything through feature 014. 636 tests green before the run.
**Ran:** broker + order service + storefront as three processes, **twice** —
unauthenticated on loopback (the evaluation path) and with a password.

### What was verified

| Scenario | Result |
|---|---|
| All previous scenarios (RPC, errors-as-data, pub/sub, durability) | ✅ unchanged |
| **`invoice` — queue work, exactly one processor** | ✅ `[queue] generated invoice for ORD-TEST` |
| `SendAsync` returns a message id | ✅ returned and printed at the call site |
| **`poison` → dead letter** | ✅ `1 dead letter(s) on 'poison.queue'` with timestamp, attempts and reason |
| `dlq poison.queue` inspects without consuming | ✅ |
| Queue and channel appear separately in the recorder | ✅ `invoices.generate` and `poison.queue` alongside `orders.create` |
| Dashboard shows queue traffic | ✅ `/api/recorder` lists both queues |
| Unauthenticated broker logs at **info**, not warning | ✅ "expected for local development" |
| Password-secured broker refuses an unauthenticated client | ✅ names the remedy, not a stack trace |
| Authenticated client works fully | ✅ RPC and queue both round-trip |

### Finding 8 — the samples cannot demonstrate dead-lettering with production defaults *(usability, fixed)*

**Symptom.** `poison` queued a message that always fails, and nothing reached the
dead-letter queue for the length of the session.

**Not a defect.** `Lease` defaults to 5 minutes and `MaxDeliveryAttempts` to 5, so a poison
message takes roughly **25 minutes** to exhaust its attempts. That is correct for
production and useless for a demonstration — the behaviour feature 013 exists to provide
was undemonstrable in the one exercise meant to show it.

**Fixed in the sample, not the library.** The broker sample gained `--lease-seconds` and
`--max-attempts`. With `--lease-seconds 2 --max-attempts 2` the dead letter appears in about
six seconds, and the defaults are untouched.

### Finding 9 — `MaxDeliveryAttempts` is off by one, and the sample makes it visible *(defect, recorded)*

Running with `--max-attempts 2`, the dead letter reported:

```
deadLetteredAt   2026-08-07T23:29:23.6792620Z
attempts         3
```

Three deliveries for a limit of two. The comparison is `attempts > MaxDeliveryAttempts`, so
the option permits N+1 deliveries while its name and documentation say N. With the default
of 5 that is 6 attempts.

The counter is really *redeliveries*; it was named and documented as *deliveries*. Recorded
in `constraints.md` § Open Decisions rather than changed here — it alters behaviour for
anyone who has already tuned the value, and deserves its own decision rather than a
drive-by fix during a sample run.

**Worth noting this was already known from reading the code, and the sample is what made it
concrete.** A number in a spec is arguable; `attempts 3` under a limit of 2 is not.

### Finding 10 — the queue makes the Send/Publish distinction visible *(observation)*

With one order service running, `invoice` was handled once and `low` was delivered to every
subscriber — the same topology producing different behaviour depending on the verb. This is
the distinction the samples previously had no way to show, and the reason `PublishAsync` was
being pressed into service as a queue.

### Authentication

Run twice deliberately. The default sample run stays **unauthenticated on loopback**,
because that is the path a newcomer meets first and running only the secured version would
leave it untested by the one exercise that catches what unit tests cannot.

With `--password sample-secret`, an unauthenticated storefront failed with:

```
Could not start: The Highway server at '127.0.0.1:6630' rejected the supplied credentials.
Check the password, and that the server was started with WithPassword.
```

Naming the remedy rather than reporting a refusal — and note the endpoint is present while
the password is not, which is the redaction working on a real path rather than in a unit
test.

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

---

## Run 4 — 2026-08-08, feature 015 (diagnosable failures)

Broker with `--lease-seconds 2 --max-attempts 2`, order service, storefront driven
over stdin. Scenario: `poison` → wait → `dlq poison.queue`.

**Before 015** this printed `attempts 3` and `reason MAX_ATTEMPTS`, and nothing else.
An operator learned that something had failed and had to go correlating worker logs
across every node to find out what. It now prints:

```
  2 dead letter(s) on 'poison.queue':
    deadLetteredAt     2026-08-08T11:30:19.7344660Z
    attempts           3
    reason             MAX_ATTEMPTS
    requestId          afded0db226a4090ba08ed3d086e6c68
    failureType        System.InvalidOperationException
    message            This processor always fails: demonstrating dead letters
    node               order-service-1
    at                 2026-08-08T11:30:17.6887904+00:00
    stack
                       at ...AlwaysFailsProcessor.ProcessAsync(...) in Services.cs:line 112
                       at ...ServiceExecutor.ExecuteProcessorAsync(...) in ServiceExecutor.cs:line 41
```

That is the whole point of the feature, demonstrated across three real processes:
the exception crossed from the worker that threw it, through `HW.FAIL`, through a
lease expiry and requeue, into the dead letter.

### Findings

**12. The off-by-one is still visible, and still deferred.** `attempts 3` under
`--max-attempts 2`. Unchanged from finding 9; `attempts > MaxDeliveryAttempts`
permits N+1. Registered in `constraints.md` § Deferred with the attempt-counting
work, because that is what redefines what an attempt *is*.

**13. A queued message is labelled `requestId`.** `HW.DLQ PEEK` calls it that for
queues as well as services, because a queue reuses the RPC entry framing and the
command branches on framing rather than on family. Cosmetic, pre-existing since 014,
and misleading in exactly the place an operator is reading carefully. Not fixed here:
renaming a reply field is a protocol change and does not belong bolted onto 015.

**14. `ExecuteProcessorAsync` appears twice in the stack.** An async state-machine
artefact, not a real double call. Harmless, and worth knowing before someone reads it
as a bug.

**A real failure caught before this run, not by it.** The failure block was being
dropped at every re-claim — `HW.DEQUEUE`, `HW.QCLAIM` and `HW.RECEIVE` all rebuild an
entry from its decoded parts, and the trailer is not one of the parts. The sweep was
wired first and the claim was not, so the context survived the requeue and then
vanished. The two-worker integration test caught it; this sample run would have shown
`failure: not reported` and looked merely disappointing rather than broken.

---

## 2026-08-09 — feature 016 (retention, storage, durability)

**Ran:** broker startup, verifying the durability default and its logging.

```
Building Highway server: bind=127.0.0.1, port=6500,
  dataDir=C:\Software\ai\highway\data, lease=00:05:00
```

The samples pass `--data-dir` explicitly, so they exercise the logging (R1.3) rather than the
new default. The default itself is covered by `DurableByDefaultConfigurationTests`, and the
restart guarantee by `DurableByDefaultTests`, which was watched failing against memory-only
before being believed.

### Findings

**15. `HW_QUEUE_FULL` is not demonstrated in the samples.** R7.4 asks for it and the byte
budget defaults to 1 GB, which no sample run will reach. Showing it needs a `--max-queue-bytes`
flag on the sample broker plus a command that fills a queue. Recorded rather than skipped
silently: the behaviour is covered by `ByteBudgetTests`, but the sample gap is real.

---

## 2026-08-09 — feature 017 (node decommissioning)

**Ran:** the integration suite, not the samples. The samples have no way to kill a subscriber
and wait out a retirement threshold, so the scenario that matters is covered by
`NodeDecommissioningTests` — including the one that is the feature's whole reason to exist:
fill a group until publishes are refused, let its node go stale, publish again, watch the
channel recover.

### Findings

**16. The samples cannot demonstrate retirement, and adding it would be contrived.** R6.4 asks
for it. Doing it honestly needs a `--retirement-threshold` flag on the sample broker plus a way
to stop the order service without stopping the storefront — which the sample harness does not
have. Recorded rather than faked with a shortened timer that proves nothing an operator would
recognise.


---

## 2026-08-09 — samples stopped running, and why

**Reported:** "the sample doesn't run anymore, nodes can't connect to server."

**Cause:** the `data/` directory left behind by an earlier run. Garnet's AOF stores a
**positional stored-procedure id** per record. Feature 018 removed `HW.RECEIVE` and `HW.RACK`,
so every id after them shifted, and replaying that log fails:

```
fail: SingleDatabaseManager[0] An error occurred AofProcessor.RecoverReplay
      GarnetException: Transaction procedure 17 not found
```

**Both outcomes were bad.** Recovery aborts and the broker carries on with an **empty store** —
"Highway server ready", dashboard up, every message it was asked to keep silently gone, and the
only evidence one `fail:` line among fifty `info:` ones. If recovery gets far enough to restore
channel keys instead, 018's guard fires and the broker refuses to start at all: the reported
symptom.

**Feature 016 made this everyone's problem.** Durability became the default, so every broker now
has a data directory, and the next command-set change would have done this silently to all of
them.

**Fixed:** the data directory carries a `highway.format` stamp, checked at `Build()` *before*
Garnet attempts recovery. A mismatch refuses with a message naming the format found and the
remedy. A fresh directory is stamped so the next start can check it.

018's own guard scanned for leftover `hw:ch:*:grp:*` keys — a symptom that can only appear when
recovery **succeeded**, which is precisely the case that did not need catching.

### Findings

**17. The samples' `data/` directory was never gitignored.** Added, along with the
`highway-data*` shapes 016's default produces.

**18. Builder unit tests were littering.** `HighwayServerBuilderTests` calls `Build()` for
things like endpoint formatting, and after 016 each call created a `highway-data-{port}`
directory beside the test binaries. They now use `Ephemeral()` — none of them needs durability,
and the stale directories from earlier runs were what first made the suite fail.

**19. `--no-build` hid the fix.** The first verification of the new stamp appeared to do nothing
because `dotnet run --no-build` reused the sample's stale copy of `Highway.Server.dll`. Worth
knowing before concluding a server change "did not take".

---

## 2026-08-09 — feature 019 (long-running tasks)

**Ran:** the integration suite. The samples have no long-running handler and no job table, so
the scenario is covered by `LongRunningTaskTests` — including the headline: a 3-second handler
against a 0.8-second lease runs **once**, where before it ran once per lease period and then
dead-lettered.

### Findings

**20. No chunk-and-checkpoint sample.** R6.6 asks for one. The pattern is documented with
working code in `docs/cookbook/long-running-work.md`, but a runnable version needs a job table
and a progress command in the storefront. Faking it with a loop and no durable checkpoint would
demonstrate the wrong shape, so it is recorded instead.

**21. Incremental builds were hiding a warning.** A `--no-incremental` build surfaced a nullable
warning in 017's `CleanAndByeForeverAsync` that ordinary builds had been reporting as zero.
Worth running clean before claiming "zero warnings".

---

## 2026-08-09 — feature 023 (message-centric dashboard)

Broker, order service and storefront, then `order 3 widget` / `invoice` / `low widget 2` /
`poison` driven through the storefront REPL.

### What the dashboard showed

`/api/nodes` — the address is joined from the live connection, not stored anywhere:

```
order-service-1   live   seen from 127.0.0.1:63619   2 services · 2 queues · 1 channel
```

`/api/node/order-service-1` — four entities in one list, attributed by completion:

```
poison.queue                    Failed      39ms   System.InvalidOperationException
inventory.low@order-service-1   Processed
invoices.generate               Processed    5ms
orders.create                   Processed   41ms
```

### Findings

**1 — the node link had been dead since 022** *(fixed, T6)*. The nodes list linked to
`#/node?name=…`; no such route existed, so the router's default silently rendered the catalogue.
Clicking a node showed a page that looked plausible and was about something else. Nothing failed,
which is why it survived a release.

**2 — the storefront exits immediately when stdin is not a terminal** *(recorded, not fixed)*.
Backgrounding it with redirected output makes it read EOF and print `Stopping...`, so a run that
looks started generates no traffic and every panel reads empty. Piping the commands in works
(`printf 'order 3 widget\nquit\n' | dotnet run …`) and is now the documented way to drive it
non-interactively. Worth knowing before concluding the dashboard is broken.

**3 — group queues appear under their raw name** *(recorded, cosmetic)*. `inventory.low@order-service-1`
is what a subscriber's queue is called, and it is truthful, but it reads as an implementation
detail beside `orders.create`. The catalogue already classifies these; the node page does not.

---

## 2026-08-10 — feature 024 (hosting boundaries and topology)

Broker + order service, default configuration — nothing in the samples changed.

### The boot log now answers the topology question

```
Highway topology — node order-service-1
  PROVIDES
    rpc    orders.create      CreateOrderService      (Highway.Samples.OrderService)
    rpc    orders.get         GetOrderService         (Highway.Samples.OrderService)
    queue  invoices.generate  InvoiceProcessor        (Highway.Samples.OrderService)
    queue  poison.queue       AlwaysFailsProcessor    (Highway.Samples.OrderService)
    sub    inventory.low      InventoryLowSubscriber  (...)  group=order-service-1
  CAN USE (references the contract; not proof of calling)
    rpc    orders.cancel
    chan   orders.placed
```

CAN USE is exactly right for this node: `orders.cancel` is a contract nobody hosts (the
fast-fail demo), and `orders.placed` is the channel this node publishes but does not
subscribe to. Both derived from references alone.

### Findings

**1 — zero reference-hosted warnings, as required.** The samples' handlers live in their
entry assemblies, so R2.3's "the samples must boot without warnings" held on first contact.

**2 — the can-use half crosses the wire.** `/api/node/order-service-1` returns
`canUse: { services: ["orders.cancel"], channels: ["orders.placed"] }` — client scan →
catalog JSON → registry → dashboard, additively; the broker needed no migration.

**3 — the manifest logs as one line** *(cosmetic, recorded)*. The console logger renders the
multi-line block as a single wrapped line. Structured sinks keep the newlines; the console
default does not. Legible either way; a per-line log call would trade one log record for
fifteen.

---

## 2026-08-10 — feature 025 (subscription groups)

Broker + TWO order-service instances (`order-service-1`, `--node order-service-2`), both with
`SubscriptionGroup = "order-service"`, then three `low` publishes from the storefront.

### Replicas compete instead of duplicating

```
published:  low widget 3, low bolt 2, low nut 1     (3 events)
replica 1:  3 x "Inventory low"
replica 2:  0 x "Inventory low"
total:      3  — one delivery per event across the group
```

Before 025 this was 6 — each instance its own group, each getting every event. Distribution
between replicas is whoever claims first (here replica 1's doorbell consistently won); the
invariant is the total, not the balance.

### The catalogue shows the group's members

```
inventory.low@order-service     members: [order-service-1, order-service-2]
inventory.low@order-service-1   members: [order-service-1]     ← previous run's per-node
                                                                  group, in the durable data
                                                                  dir; retires on threshold
```

That leftover entry is correct behavior worth understanding: the data directory remembers the
pre-grouping subscription, and 017's retirement (now measuring 025's membership) is exactly
the mechanism that cleans it up after `SubscriberRetirementThreshold`.

### Findings

**1 — the 024 flake is attributed and fixed.** `ExecuteSubscribersAsync_InvokesAllSubscribers`
failed once in the full suite: `TestSubscriber`'s static counters were shared with
`DelegateCompilerTests`, a class xUnit runs in parallel. The compiler tests now use a
dedicated fixture type — the same rule the loop tests already followed, for the same reason.

**2 — `HW.REPLAY` cannot read derived queues** *(recorded, pre-existing)*. Its identifier
rules reject `@`, so claims recorded under `{channel}@{group}` are reachable in-process (the
dashboard) but not over the wire. The idempotency test reads the recorder directly for this
reason.

---

## 2026-08-10 — feature 028 (recurring jobs)

Broker + order service, which now declares `o.Jobs.Every<ReconcileOrders>(1 minute)`.
Observed over 75 seconds:

```
boot manifest:   job    orders.reconcile  ReconcileOrders  (...)  reconcileorders every:60
service log:     [job] reconciling orders (scheduled run)      × exactly 1
/api/jobs:       { job: reconcileorders, expression: every:60,
                   lastFire: 13:37:30, nextFire: 13:38:30, hosted: true }
```

One fire in one interval, processed by the ordinary queue machinery, visible in the manifest
at boot and on the dashboard with next/last fire. Nothing else in the samples changed.
