# RPC (Request / Reply)

> **Status:** current
> **Protocol:** the *Work Commands* section of [`docs/HIGHWAY-PROTOCOL.md`](../HIGHWAY-PROTOCOL.md)
> is authoritative for `HW.CALL` / `HW.CLAIM` / `HW.REPLY` / `HW.RESULT` / `HW.ACK`. Guarantees are C3 in
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

**Call → claim → reply → ack.** A caller issues `HW.CALL`, which enqueues the request in the
service's box and rings the service's doorbell so an idle worker wakes. A worker claims the next
request with `HW.CLAIM SVC` — which, before serving, recovers expired leases *and* the claims of dead
nodes, requeuing anything a departed worker held — and gets back a **token** for the claim. The
handler runs inside a fresh DI scope; when it finishes, `HW.REPLY` writes the answer into the caller's
reply slot and rings the reply doorbell, the caller takes it with `HW.RESULT` (read and removed in one
command), and the worker settles the claim with `HW.ACK <token>`.

**Settling is one lookup** (3.0). The token names the claim directly, so an ack, a lease renewal or a
failure report never reads a list of in-flight work, however much is in flight; a late ack of a claim
that was already recovered and redelivered answers `:0` and cannot settle the redelivery.

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

**Dead letters and RPC.** When an RPC request exhausts its delivery attempts, it moves to the service's dead-letter space (`SVC <service>`) carrying its full diagnostic failure block. Dead-letter handlers (`IDeadLetter<T>`) can monitor RPC dead letters and resolve them with `Keep` (leaving them for operator analysis) or `Discard` (purging them). Attempting to requeue an RPC dead letter via handler resolution (`HW.DLQ RESOLVE ... REQUEUE`) is refused: the calling client has already timed out, its reply slot is gone, and no caller is waiting for the result.

## Wire surface

`HW.CALL`, `HW.CLAIM SVC`, `HW.REPLY`, `HW.RESULT`, `HW.ACK`, and the discovery/registry commands
that support them are defined in [`docs/HIGHWAY-PROTOCOL.md`](../HIGHWAY-PROTOCOL.md). RPC shares the
queue's claim, lease and dead-letter machinery documented there — one work path for services, queues
and group queues since protocol 5.0.

## Guarantees & limits

- **At-least-once, competing consumers:** exactly one worker answers each request, but a handler
  must tolerate redelivery after a crash or lease expiry (C3).
- **An answer or a timeout, never silence:** `CallTimeout` bounds every wait; errors are data.
- **In-flight requests survive a node leaving:** they requeue rather than vanish (C3.1).
- **Retry budget may outlive the caller:** a stuck request exhausts attempts after the caller has
  timed out; the dead letter is the record (C3.3).
- **A re-sent call is queued once:** a repeat of a `requestId` the broker accepted within the
  duplicate window (10 min) answers `+OK` and is not queued again; the id is forgotten when its
  request is dead-lettered, so a caller still waiting can re-send. Per broker process (C1.7).
- **A hung handler is answered, not waited on:** it is cancelled at `MaxProcessingTime` and the
  caller gets `504 PROCESSING_TIME_EXCEEDED`; one that ignores the cancellation is abandoned (C1.8).
  A handler stopped by the worker *shutting down* is not answered: its request is left to lease
  recovery, so another worker can still answer a caller that is waiting.
- **Not exactly-once:** `[Idempotent]` suppresses a redelivery running twice; it cannot relate two
  independent calls.
