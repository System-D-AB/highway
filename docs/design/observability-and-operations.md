# Observability & Operations

> **Status:** current, with a named roadmap
> **Protocol:** the *Observability Commands* section of
> [`docs/HIGHWAY-PROTOCOL.md`](../HIGHWAY-PROTOCOL.md) is authoritative for `HW.REPLAY` and the
> recorder `HW.STATS` forms. Guarantees are C7 in [`constraints.md`](../product/constraints.md); the
> doctrine is [`fail-safe-and-observability.md`](../product/fail-safe-and-observability.md).

*Highway is its own observability store: an always-on flight recorder captures every operation for
instant replay, a dashboard reads it live, and logs go to rolling files — with the machine channels
(metrics, health) a fail-safe system needs named as the next work, not yet built.*

## Overview

Out of the box, the broker records every RPC call, publish, registration and heartbeat with
millisecond timestamps and payloads, queryable through `HW.REPLAY` and shown on an embedded
dashboard. No Jaeger, no ELK, no external store required to see what just happened. Highway also
emits `Activity` spans so an application that *wants* OpenTelemetry wires its own pipeline — Highway
takes no OTel dependency itself.

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

## The fail-safe direction

The recorder is **volatile** — in-process, lost on restart — which is right for a flight recorder
but wrong for an audit trail; and two channels a *machine* depends on don't exist yet: there are **no
metrics** and **no health/readiness endpoints**. Under the ["loud, bounded, never
total" doctrine](../product/fail-safe-and-observability.md), a significant event — a leadership
change above all — must be loud on every channel, and today it is loud on only some. The roadmap
that closes this: **051** OpenTelemetry `Meter` metrics (role/epoch/lag, queue depth, DLQ, RPC
latency/errors), **052** `/health` and `/ready` endpoints so load balancers route around a
demoted node automatically, **053** a durable audit trail to complement the volatile recorder,
**054** client safety telemetry, **055** resource/durability guards, and **056** optional
notification webhooks. These are specced and sequenced in the doctrine document.

## Wire surface

`HW.REPLAY` and the recorder `HW.STATS` forms are defined in
[`docs/HIGHWAY-PROTOCOL.md`](../HIGHWAY-PROTOCOL.md). Metrics and health endpoints (051/052) will be
HTTP surfaces on the broker, documented with those features when they land.

## Guarantees & limits

- **Always-on, bounded recorder** (C7.4): every operation captured, correct read for the life of the
  broker, nothing growing without bound.
- **Diagnostics never break delivery** (C7.1): the recorder and failure reporting are best-effort
  and drop rather than block.
- **Capture obeys one switch** (C7.2): diagnostic detail honours the same per-name payload-capture
  mode as payloads; the exception *type* survives every mode.
- **Volatile by design:** the recorder is lost on restart — a durable audit trail is planned (053),
  not present.
- **No metrics or health endpoints yet:** the two machine-facing channels are the highest-leverage
  gap (051/052); until they ship, alerting and LB integration are manual.
