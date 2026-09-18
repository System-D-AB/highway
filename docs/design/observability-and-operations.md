# Observability & Operations

> **Status:** current, with a named roadmap
> **Protocol:** the *Observability Commands* section of
> [`docs/HIGHWAY-PROTOCOL.md`](../HIGHWAY-PROTOCOL.md) is authoritative for `HW.REPLAY` and the
> recorder `HW.STATS` forms. Guarantees are C7 in [`constraints.md`](../product/constraints.md); the
> doctrine is [`fail-safe-and-observability.md`](../product/fail-safe-and-observability.md).

*Highway is its own observability store: an always-on flight recorder captures every operation for
instant replay, a dashboard reads it live, logs go to rolling files, it emits OpenTelemetry metrics a
monitoring system scrapes, and it serves HTTP health/readiness endpoints a load balancer routes on —
the machine channels a fail-safe system needs.*

## Overview

Out of the box, the broker records every RPC call, publish, registration and heartbeat with
millisecond timestamps and payloads, queryable through `HW.REPLAY` and shown on an embedded
dashboard. No Jaeger, no ELK, no external store required to see what just happened. Highway also
emits `Activity` spans and `Metric` instruments so an application that *wants* OpenTelemetry wires
its own pipeline — Highway takes no OTel dependency itself.

## How it works

**The flight recorder.** Each name (service or channel) gets a fixed **circular buffer** with
per-name retention and payload-capture modes (Full / HeadersOnly / Off, for sensitive data).
`HW.REPLAY name FROM -5min` returns recent operations for one name. The recorder is **bounded** —
that boundedness is what makes always-on diagnostics affordable — and its read is correct for the
life of the broker: retention is applied *at read*, and the read anchors at the oldest slot and
walks the whole ring, so a buffer that has wrapped and then been swept still returns exactly its live
events (a subtle correctness bug that silently dropped recent events after ~1 h of uptime was fixed
with a wrap→sweep→read regression test). RPC replies land in one reserved cluster-wide bucket
(`hw.replies`) correlated to requests by id, sized larger than a single service's history; a reply
that ages out shows its RPC as incomplete rather than inventing an outcome.

**Diagnostics can never break a delivery.** The recorder drops rather than blocks when full, and
failure reporting (`HW.FAIL`) is best-effort — if it fails, the exception is swallowed and logged
with the original attached, the worker loop continues, and the message is *not* acked, so lease
recovery runs exactly as it would have. Losing the diagnosis is survivable; losing the thing being
diagnosed is not.

**The dashboard.** Embedded in the `highways` distribution (it runs in the broker process and reads
the recorder live, which is why it isn't a separate package), it shows recorder health and event
browsing with live SSE tailing, plus the replication views — role and slots, a "seen from" node
address, and a succession view over the roster. It reflects reality: a node's address appears only
while connected, and degraded replication states are surfaced rather than left to infer.

**Logs to disk.** The broker writes rolling daily log files (console + file) so a
Windows-service or systemd deployment has a durable operational record, not just stdout.

**Metrics (feature 051).** The broker exposes a `Highway.Server` meter and the client a
`Highway.Client` meter through the in-box `System.Diagnostics.Metrics` API — replication
role/epoch/lag and promotions/demotions/fences, queue depth/bytes and dead-letters, RPC
throughput/latency/errors, connections and recorder drops; the client adds RPC latency and a
`failovers` counter (the caller-side view of a master change). Same posture as the spans: no OTel
dependency, and zero cost when unobserved — counters do a cheap `Add`, and the observable gauges
sample the live feeder/store only when a listener collects. The application wires the exporter:

```csharp
services.AddOpenTelemetry().WithMetrics(m => m
    .AddMeter("Highway.Server")   // or "Highway.Client" in a caller process
    .AddPrometheusExporter());
```

The full instrument list — names, types, units, labels — is the contract in
[`docs/HIGHWAY-PROTOCOL.md`](../HIGHWAY-PROTOCOL.md) § "Metric emission".

**Health & readiness endpoints (feature 052).** The broker serves three HTTP routes on the dashboard
host (no new port), so an orchestrator and a load balancer can see the node's real state:

- `GET /health` — **liveness**. `200` whenever the process is up and Kestrel answers, regardless of
  role. Keyless and dependency-free — safe to hit frequently; a healthy *replica* is live.
- `GET /ready` — **readiness**. `200` only when the node can serve client writes *now* — it is the
  writable primary, not fenced, not draining (GOODBYE), not mid-bootstrap; otherwise `503` with a
  one-word reason (`replica`, `fenced`, `draining`, `bootstrapping`, `demoted`). The decision is the
  feeder's own `repl.ready`/`repl.readyReason`, computed fresh per probe, so it flips within a probe
  interval on a promotion/demotion/GOODBYE. That is what lets a load balancer pull a stepped-down
  node out of rotation automatically — the infra-layer "route around the failover".
- `GET /replication` — the `HW.REPL.STATUS` fields as JSON (role, epoch, lag, roster, degraded
  flags). Behind the same API key as the dashboard, since it exposes topology.

`/health` and `/ready` are keyless (a probe leaks nothing beyond up/down and a role word); the
detailed `/replication` is gated. The endpoints bind on the dashboard host — enabled by default even
when the dashboard UI is off, so a headless broker still answers probes (`WithHealthEndpoints(...)`
serves them without the UI). Same binding posture as the dashboard: loopback by default; to let an
external LB probe, bind `0.0.0.0` and set an API key (the two probe routes stay keyless).

## The fail-safe direction

The recorder is **volatile** — in-process, lost on restart — which is right for a flight recorder
but wrong for an audit trail. Under the ["loud, bounded, never
total" doctrine](../product/fail-safe-and-observability.md), a significant event — a leadership
change above all — must be loud on every channel. Metrics (**051**) and the health/readiness
endpoints (**052**) closed the two machine channels. The roadmap that remains: **053** a durable
audit trail to complement the volatile recorder, **054** client safety telemetry, **055**
resource/durability guards, and **056** optional notification webhooks. These are specced and
sequenced in the doctrine document.

## Wire surface

`HW.REPLAY` and the recorder `HW.STATS` forms are defined in
[`docs/HIGHWAY-PROTOCOL.md`](../HIGHWAY-PROTOCOL.md), which also carries the metric-instrument
contract (§ "Metric emission", feature 051). The health/readiness HTTP endpoints (052) — `/health`,
`/ready`, `/replication` — are described under *How it works* above and in the
[distribution README](../distribution/README.md).

## Guarantees & limits

- **Always-on, bounded recorder** (C7.4): every operation captured, correct read for the life of the
  broker, nothing growing without bound.
- **Diagnostics never break delivery** (C7.1): the recorder and failure reporting are best-effort
  and drop rather than block.
- **Capture obeys one switch** (C7.2): diagnostic detail honours the same per-name payload-capture
  mode as payloads; the exception *type* survives every mode.
- **Metrics with no exporter dependency** (feature 051): a `Highway.Server` and a `Highway.Client`
  meter over `System.Diagnostics.Metrics`, always defined and zero-cost when unobserved; the
  application picks the exporter. Instrument names/units/labels are a stable contract.
- **Role-driven readiness** (feature 052): `/ready` returns `200` only on a writable primary and
  flips to `503` (with a reason) within a probe interval on demotion/fence/GOODBYE, so a load
  balancer routes client traffic around a stepped-down node automatically. `/health` is liveness
  only (a healthy replica is live), and `/replication` exposes the status JSON behind the API key.
- **Volatile recorder by design:** the recorder is lost on restart — a durable audit trail is
  planned (053), not present.
