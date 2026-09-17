# Highway — Design Docs

This folder is the **published design record** for Highway: one evergreen, topical document per
capability, describing the system as it is *now* — not a changelog of how it was built.

Start with **[overview.md](overview.md)**, then read the capability you care about:

| Doc | Capability |
|---|---|
| [overview.md](overview.md) | What Highway is, the three verbs, the engine, the packages |
| [protocol-and-transport.md](protocol-and-transport.md) | RESP framing, the `HW.*` surface, errors-as-data |
| [storage-engine.md](storage-engine.md) | RocksDB store, durability, bounded footprint |
| [rpc.md](rpc.md) | Request/reply over a durable queue |
| [pubsub.md](pubsub.md) | Durable fan-out, subscription groups |
| [durable-queues.md](durable-queues.md) | Competing-consumer work, leases, dead-letter, jobs |
| [registry-and-heartbeat.md](registry-and-heartbeat.md) | Discovery, liveness, decommissioning |
| [distributed-cache.md](distributed-cache.md) | Opt-in broker-local `IDistributedCache` |
| [replication-and-failover.md](replication-and-failover.md) | Client-herd mastership, WAL shipping, epochs |
| [observability-and-operations.md](observability-and-operations.md) | Flight recorder, dashboard, logs, roadmap |

## What this folder is, and isn't

- **The single source of truth for the wire protocol** is [`../HIGHWAY-PROTOCOL.md`](../HIGHWAY-PROTOCOL.md).
  These docs *link* to it and never restate it.
- **The guarantees Highway keeps** are enumerated line by line in
  [`../product/constraints.md`](../product/constraints.md). These docs *link* to it.
- **The doctrine these docs answer to** is
  [`../product/fail-safe-and-observability.md`](../product/fail-safe-and-observability.md).

Detailed per-feature working notes (requirements / design / tasks) are kept locally by the
maintainer and are not published; a design doc here is the distilled result, not the working log.
See [`_template.md`](_template.md) to add one.
