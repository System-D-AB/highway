# RPC (Request / Reply)

> **Status:** current
> **Protocol:** the *Work Commands* section of [`docs/HIGHWAY-PROTOCOL.md`](../HIGHWAY-PROTOCOL.md)
> is authoritative for `HW.CALL` / `HW.CANCEL` / `HW.CLAIM` / `HW.REPLY` / `HW.RESULT` / `HW.ACK`. Guarantees are
> C3 in [`constraints.md`](../product/constraints.md). This doc explains how RPC works and why.

*Highway RPC is request-and-reply with a deadline: a caller `ExecuteAsync`s a request and waits for the
answer or an explicit timeout — never silence — and the request lives exactly as long as the caller
waits. Workers hosting the service compete to handle it.*

## Overview

You declare a request POCO implementing `IReturn<TResponse>` with a `[Service("name")]` attribute
and a handler extending `AsyncService<TReq,TRes>`; a caller does
`await client.ExecuteAsync(new CreateOrder { ... })`. The same code runs whether the handler is in
the same process or across the network — every call goes through the broker (location
transparency). Multiple instances hosting the same service **compete**: they share the load, one
handler answers each request.

Mechanically RPC is a queue *with* a reply slot and a **deadline**. It shares the claim, lease and
attempt counting of [durable queues](durable-queues.md), but not their patience: a queued message
waits for a worker however long that takes, while an RPC request is worth something only while
someone waits for its answer. When the caller stops waiting, the request ends — it never runs later,
for nobody, and it is never parked as a dead letter.

## How it works

**Every call has a deadline.** The caller's timeout is the call's own (`ExecuteAsync(request,
timeout)`), else the contract's (`[Service("reports.run", Timeout = "00:05:00")]`), else `CallTimeout`
(30 s, the patience of a web request). It travels with `HW.CALL` as a *relative* duration; the broker
fixes the absolute deadline with its own clock and stores it on the request, replicated with it.
No clock is ever compared with another machine's: workers are told the *remaining* time, and the client
measures its own wait on a monotonic clock. A call without a timeout gets the broker's default (30 s),
and none may ask for more than an hour.

**Call → claim → reply → ack.** `HW.CALL` enqueues the request in the service's box and rings the
service's doorbell. A worker claims with `HW.CLAIM SVC` and receives a token and the request's remaining
time. The handler runs inside a fresh DI scope with a `CancellationToken` that fires at that remaining
time (or at `MaxProcessingTime`, when sooner); `HW.REPLY` writes the answer into the caller's reply slot,
the caller takes it with `HW.RESULT`, and the worker settles the claim with `HW.ACK <token>`. Settling is
one lookup: the token names the claim directly.

**After the deadline, nothing runs.** A claim removes a request whose deadline has passed — or that has
too little time left to start — instead of handing it out. Lease recovery, dead-node recovery and a
departing worker return a request to the queue only while its caller still waits. A claim's lease ends
with the deadline (plus a short grace), so a worker that dies holding a 30-second call does not hold it
for the full five-minute lease. A reply that arrives after the deadline is discarded. This is what keeps
an outage from turning into a storm: the backlog built up while workers were down expires instead of
running in full, for nobody, when they come back.

**A handler stops when its caller does.** A handler that honours its token stops at the deadline; that
is not a failure — the claim is acknowledged and nothing is retried. One that ignores it (a stuck driver
call, a blocking wait) is abandoned shortly after: its worker slot returns and its claim is acknowledged,
so a hung dependency costs each caller one timeout and never freezes the service. Abandonment is bounded
per worker, so a worker can be degraded but never spawns threads without limit. Calls a handler makes to
other services inherit its remaining time: an inner call never outlives the caller of the outer one.

**One call is one request.** The client re-sends its pending calls only when the master actually moved to
another endpoint — once per move, and each with the time its caller has left; a call with none left is
answered locally as a timeout. A connection that recovers in place re-sends nothing. The broker answers a
repeated `HW.CALL` for a request it queued, until that request's deadline, without queueing it again. A
caller that gives up early (the user left the page) withdraws its queued request with `HW.CANCEL`.

**Nobody home fails fast.** A call to a service no node has ever served — a typo, a service not yet
deployed — is refused at once and the caller gets a `503`, instead of waiting out its timeout. A service
that has been served is always queued, whether its workers are busy, restarting or all gone: its
deadline decides. A worker's session declares what it serves; a worker that sends no heartbeat is
recognised by its first claim.

**No dead letters.** A request that fails its last attempt with time left is removed, with its failure,
and the caller gets its timeout. Every request that ended without an answer is counted, recorded as an
event (`RpcExpired`, `RpcFailedFinal`) with the failure where there is one, and summarised in the broker
log once per service per minute. An RPC outcome is an answer, an error or a timeout; a parked request
either runs for nobody or waits for a human who can give the original caller nothing. Work that must
eventually happen belongs on a queue, whose dead letters exist for exactly that.

**Commands versus queries.** A query is safe to repeat; a command may have happened even when its caller
timed out. Make a command's request `[Idempotent]` and reuse its key when retrying — the stored answer is
kept for at least twice the timeout, so a retry after a timeout returns the original outcome — or send it
through a queue (`SendAsync`) and report the result separately. A handler marked `[SideEffects]` whose
request is not `[Idempotent]` is listed at startup as a reminder.

**Discovery.** A worker registers the services it hosts (see
[registry-and-heartbeat.md](registry-and-heartbeat.md)); `HW.DISCOVER` reports the live nodes hosting a
service. Competing consumers need no group name — the broker hands each claim to exactly one worker.

## Wire surface

`HW.CALL` (with `TIMEOUT`), `HW.CANCEL`, `HW.CLAIM SVC` (rows with the remaining time), `HW.REPLY`
(with `TOKEN`), `HW.RESULT`, `HW.ACK`, the `HW_NO_WORKERS` error, the entry's deadline trailer and the
service row of `HW.STATS` are defined in [`docs/HIGHWAY-PROTOCOL.md`](../HIGHWAY-PROTOCOL.md).

## Guarantees & limits

- **An answer or a timeout, never silence** (C3.2): the deadline bounds every wait; errors are data.
- **A request never runs after its deadline** (C3.3): not from the queue, not on redelivery, and a late
  reply is discarded. RPC has no dead letters; unanswered requests are counted, recorded and logged.
- **A handler stops when its caller stops waiting** (C3.4); one that will not stop is abandoned within a
  bound (C1.8).
- **One call is one request** (C3.5): re-sent only on a real master change, with the time left; repeats
  are not queued twice before the deadline. Across a failover the duplicate memory is per broker, so a
  request the new primary already held may run twice — bounded by its deadline; commands that must not
  run twice are `[Idempotent]` or queued.
- **In-flight requests survive a node leaving** while their caller waits (C3.1).
- **At-least-once, competing consumers:** exactly one worker handles each delivery, but a handler must
  tolerate redelivery after a crash (C3).
