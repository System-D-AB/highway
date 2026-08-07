# Feature: Retention, Storage and Durability

## Introduction

`docs/product/constraints.md` reports **five unmet constraints, and all five are here.**
They are one coherent piece of work rather than five problems:

| | Constraint | Today |
|---|---|---|
| C4.1 | Retention: 100 days | 1 day |
| C4.2 | Size cap: 1 GB, configurable | a count of 10,000 entries |
| C4.3 | Reaching a limit is never silent | drops the oldest, silently |
| C4.4 | Every queue-like structure is bounded | group queues have no cap at all |
| C4.5 | Durability is the default | **memory-only** |
| C4.6 | Storage growth is bounded over time | AOF grows forever, never compacted |

### The one that makes the others conditional

**C4.5.** `new HighwayServerBuilder().Build()` starts a broker with no data directory, no
AOF, and everything lost on restart. Every guarantee in C1 (queue) and C2 (pub/sub) is false
in that configuration — including "a sent message survives until it is processed", which is
the queue's entire reason to exist.

Feature 014 shipped a warning because a silent lie was the one unacceptable option. This
feature replaces the warning with the fix.

### Why now

Feature 014 moved durability onto the queue, where it belongs. Building gigabyte budgets and
100-day retention into pub/sub first would have been the expensive order — that is why this
feature waited. It is no longer waiting on anything.

## Requirements

### Requirement 1: Durable by Default

**User Story:** As someone evaluating Highway, I want the broker I start with no configuration to keep my messages.

#### Acceptance Criteria

1. A server built with no configuration is **durable**: a data directory is created, AOF is enabled, and queued work survives a restart
2. **The zero-configuration start still costs nothing to use.** No path to type, no directory to create by hand, no error if the location is unwritable-but-recoverable. This requirement must not be paid for out of Requirement 1 of feature 012 (`new HighwayServerBuilder().Build()` just works)
3. The default location is documented, predictable, and stated in the startup log — an operator must be able to answer "where is my data?" without reading source
4. An explicitly **ephemeral** server remains available in one call, for tests and for genuinely disposable brokers. `HighwayTestServer` uses it
5. The 014 durability warning is removed, because it is no longer true by default
6. A server that cannot create or write its data directory **fails at `Build()`** naming the path, rather than silently degrading to memory-only. Silent degradation would reintroduce exactly the problem this requirement removes

### Requirement 2: Byte Budgets, Not Entry Counts

**User Story:** As an operator, I want to say how much memory Highway may use, because that is the resource I actually have.

#### Acceptance Criteria

1. Limits are expressed in **bytes**, with a documented default. A count cannot express "as much memory as I am willing to give this", and what exhausts a server is bytes
2. Accounting is real: the size of a structure is tracked or measured, not estimated from entry counts
3. **What the budget is measured against is decided and documented** — see Open Decisions. A per-structure cap is simpler and does not bound the process; a server-wide budget bounds the process and needs an accountant and an eviction policy
4. Byte accounting must not add measurable cost to the enqueue path. Highway's write path is measured in nanoseconds and the flight recorder precedent (feature 002) shows what is acceptable
5. Entry-count limits may remain as a secondary guard, but the byte budget is the primary one

### Requirement 3: Every Queue-Like Structure Is Bounded

**User Story:** As an operator, I want no structure in Highway that can grow without limit.

#### Acceptance Criteria

1. **Pub/Sub group queues are bounded.** They are currently the only unbounded structure, and the one that will actually consume a gigabyte: a decommissioned node keeps receiving a copy of every publish forever, with nothing draining it
2. Queues (feature 014) are bounded
3. Dead-letter lists remain bounded (feature 013)
4. Delayed and retry sorted sets are bounded
5. A test enumerates every list, set and sorted set Highway creates and asserts each has a cap — so a structure added later cannot quietly join without one

### Requirement 4: Limits Are Never Silent

**User Story:** As a producer, I want to be told when the broker cannot accept my message, not to discover months later that history has a hole in it.

#### Acceptance Criteria

1. When a limit is reached, the **send or publish is refused** rather than the oldest entry being dropped. Under C1.2 a queued message is one nobody has ever processed; discarding it is losing the data the queue exists to protect
2. The refusal is a distinct error code, and its class (permanent or transient) is decided and documented — see Open Decisions
3. The client surfaces it as a typed exception naming the queue or channel and the limit that was hit
4. Where dropping is genuinely correct — the flight recorder, which is diagnostic and explicitly volatile — it stays dropping, and the difference is documented rather than inconsistent
5. Every drop or refusal is **counted and visible** in `HW.STATS`, and recorded by the flight recorder

### Requirement 5: Retention

#### Acceptance Criteria

1. Retention defaults to **100 days**, per C4.1
2. Retention is a *secondary* limit: under C1.2 only unprocessed work is stored, so the byte budget is expected to bind first in an unhealthy system and neither to bind in a healthy one. The documentation says so, so nobody sizes a disk for 100 days of throughput
3. Retention applies per queue and per channel group, and is configurable per structure where that is meaningful
4. An entry removed by retention is counted and visible, like any other loss

### Requirement 6: Storage Growth Is Bounded Over Time

**User Story:** As an operator, I want a broker that has been running for a year to still start in a reasonable time.

#### Acceptance Criteria

1. `AofSizeLimit` is configured, so Garnet's checkpoint-on-AOF-size actually runs. Highway sets a checkpoint directory today but never turns this on, so the log grows without limit
2. Compaction is configured or its absence is a documented decision
3. A test proves the AOF does not grow without bound under sustained traffic
4. Restart recovery time is bounded by the checkpoint interval rather than by total history
5. This requirement is **independent of every other decision here** and is worth doing even if the rest is descoped

### Requirement 7: Conformance

#### Acceptance Criteria

1. `docs/HIGHWAY-PROTOCOL.md` updated: new error code, changed option defaults, any new keys
2. `ProtocolConformanceTests` green
3. `constraints.md` C4.1–C4.6 move to **Met**, and the C4 section stops being the reason the summary reads badly
4. Samples demonstrate a refused send at a limit, and are re-run with a `samples/RUNLOG.md` entry
5. All existing tests pass; `dotnet build` warning-free
6. `new HighwayServerBuilder().Build()` still starts a working broker with no configuration

## Open Decisions

**These need answering before the design is written.** They are recorded here rather than
guessed, because each changes the shape of the feature.

1. **What is the byte budget measured against?**
   - *Per structure* — simple, no global state, but N queues × 1 GB does not bound the process
   - *Server-wide* — actually bounds the process, needs a global accountant and an eviction or refusal policy across unrelated structures
   - The second is what an operator means by "1 GB of RAM". It is also materially more work.

2. **Is a full-queue refusal permanent or transient?**
   A full queue may drain, which argues transient and retryable — and a client that retries into a full queue in a tight loop makes it worse. A permanent error forces the application to decide, which is more honest and less convenient.

3. **`MaxDeliveryAttempts` off-by-one.**
   `attempts > MaxDeliveryAttempts` permits N+1 deliveries while the name says N; the samples made this concrete (`attempts 3` under a limit of 2). Change the comparison, or rename to "redeliveries"? Changing it alters behaviour for anyone who has already tuned the value. This is not strictly part of this feature but is in the same area and should be settled with it.

4. **Where does the default data directory live?**
   Beside the executable is predictable and breaks in read-only deployments. A platform application-data path is correct and less obvious. A required explicit path is clearest and violates Requirement 1 AC2.

## Non-Goals

- **Encryption at rest.** The AOF and checkpoints stay unencrypted; that is disk encryption's job.
- **Tiered or external storage.** Garnet's storage tier is used as configured; Highway does not add its own spill.
- **Per-message TTL.** Retention stays per structure.
- **Quotas per tenant or per client.** Highway has no tenancy model.
- **Changing the flight recorder.** It is volatile and drop-on-full by design (feature 002), and that stays — Requirement 4 AC4 documents the difference rather than removing it.

## Cross-References

- `docs/product/constraints.md` — C4, the five constraints this closes; C1.2 and C4.3 for why refusal beats dropping
- `docs/features/014-queue/` — the durability warning this replaces
- `docs/features/013-reliable-delivery/design.md` — dead-letter bounding, the pattern to follow
- `docs/features/002-observability/design.md` — byte accounting that does not cost the write path
