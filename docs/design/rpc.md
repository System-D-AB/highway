# RPC (Request / Reply)

> **Status:** current
> **Protocol:** the *RPC Commands* section of [`docs/HIGHWAY-PROTOCOL.md`](../HIGHWAY-PROTOCOL.md)
> is authoritative for `HW.CALL` / `HW.REPLY` / `HW.DEQUEUE` / `HW.ACK`. Guarantees are C3 in
> [`constraints.md`](../product/constraints.md). This doc explains how RPC works and why.

*Highway RPC is request-and-reply over a durable queue: a caller `ExecuteAsync`s a request and waits
for the answer or an explicit timeout — never silence — while workers hosting that service compete
to handle it.*

## Overview

You declare a request POCO implementing `IReturn<TResponse>` with a `[Service("name")]` attribute
and a handler extending `AsyncService<TReq,TRes>`; a caller does
`await client.ExecuteAsync(new CreateOrder { ... })`. The same code runs whether the handler is in
the same process or across the network — every call goes through the broker (location
transparency). Multiple instances hosting the same service **compete**: they share the load, one
handler answers each request.

Mechanically RPC is a queue *with* a reply slot — the same claim, lease, attempt-counting and
dead-lettering as [durable queues](durable-queues.md), plus a place to put the answer.

## How it works

**Call → claim → reply → ack.** A caller issues `HW.CALL`, which enqueues the request and rings the
service's doorbell so an idle worker wakes. A worker claims the next request with `HW.DEQUEUE` —
which, before serving, sweeps expired leases *and* prunes dead nodes, requeuing anything a departed
worker held. The handler runs inside a fresh DI scope; when it finishes, `HW.REPLY` writes the
answer into the caller's reserved reply slot and rings the reply doorbell so the waiting caller
returns, and `HW.ACK` clears the request.

**Discovery and load balancing.** A worker registers the services it hosts in the registry (see
[registry-and-heartbeat.md](registry-and-heartbeat.md)); `HW.DISCOVER` reports the live nodes
hosting a service. Competing consumers need no group name and no client-side routing — the broker
hands each claim to exactly one worker.

**Answers are values, timeouts are certain.** A handler returns an `Output` carrying a status code;
a business failure is data the caller inspects, not an exception thrown across the wire. If no answer
arrives within `CallTimeout` (30 s default), the caller gets an explicit timeout, never an
indefinite hang.

**In-flight requests belong to the caller, not the worker.** When a node leaves — gracefully or by
dead-node pruning — its unacknowledged *requests* are **requeued**, not deleted, because a request
in flight belongs to a caller who is still waiting. (This is the line that separates RPC from
queues: a decommissioned subscriber's *messages* can be destroyed; an in-flight request cannot.)

**The retry budget can outlive the caller.** `Lease` defaults to 5 minutes against a 30-second
`CallTimeout`, so a stuck request keeps being retried long after its caller gave up; the dead letter
is then the only trace. This is why RPC backoff is off by default.

## Wire surface

`HW.CALL`, `HW.DEQUEUE`, `HW.REPLY`, `HW.ACK`, and the discovery/registry commands that support them
are defined in [`docs/HIGHWAY-PROTOCOL.md`](../HIGHWAY-PROTOCOL.md). RPC reuses the queue's lease and
dead-letter machinery documented there.

## Guarantees & limits

- **At-least-once, competing consumers:** exactly one worker answers each request, but a handler
  must tolerate redelivery after a crash or lease expiry (C3).
- **An answer or a timeout, never silence:** `CallTimeout` bounds every wait; errors are data.
- **In-flight requests survive a node leaving:** they requeue rather than vanish (C3.1).
- **Retry budget may outlive the caller:** a stuck request exhausts attempts after the caller has
  timed out; the dead letter is the record (C3.3).
- **Not exactly-once:** `[Idempotent]` suppresses a redelivery running twice; it cannot relate two
  independent calls.
