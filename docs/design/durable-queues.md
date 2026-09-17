# Durable Queues

> **Status:** current
> **Protocol:** the exact commands, replies and keys live in the *Queue Commands* and
> *Dead Letter Commands* sections of [`docs/HIGHWAY-PROTOCOL.md`](../HIGHWAY-PROTOCOL.md) —
> this doc explains how queues work and why, and never restates the wire surface.

*A Highway queue is a named, durable, competing-consumer work list: a sender drops a message and
walks away, and exactly one worker eventually processes it. It is the capability whose absence
leads people to misuse pub/sub for work that must not be lost.*

## Overview

A queue is **RPC minus the reply** — the same claim, lease, attempt-counting and dead-lettering
machinery, with no response slot. You get:

- **Fire-and-forget send.** Enqueuing never requires a running worker; the message waits until one
  claims it. That is the whole point of a queue.
- **Competing consumers.** Every worker pulling from a queue shares the work — no group name, no
  coupling to node identity; the broker hands each claim to exactly one caller.
- **At-least-once delivery.** A claimed message is redelivered if it is not acknowledged, so a
  crashed or slow worker never silently drops work.
- **Durable by default.** Queued work survives a broker restart; memory-only durability is opt-in
  by name (an ephemeral broker), never the accidental default.

Queues have their own key space, so a queue and an RPC service may share a name without colliding,
and a queue never appears in service discovery.

## How it works

**Send → claim → ack.** A send appends the message and rings the queue's doorbell so idle workers
wake. A worker *claims* one message at a time, receiving its id and payload; the claim starts a
**lease**. When the worker finishes it *acks*, and the message is gone. Until the ack arrives the
message sits in that worker's processing list — which is exactly what makes delivery at-least-once.

**Lease recovery is the safety net.** Every claim first sweeps expired leases across all known
workers: a message whose lease elapsed returns to the queue with its attempt count incremented, so
a worker that died mid-processing loses nothing. There is no background timer doing this — recovery
and deferred-delivery promotion both happen *on the claim path*, decided by the broker's clock.

**Slow work vs. dead work.** A handler that outlives its lease would be redelivered while still
running — a duplicate caused by nothing but slowness. A worker renews its lease ("still working")
to hold the message; Highway's client renews automatically. The renewal is **bounded by the
client**, not the server: it stops after a max processing time, so a genuinely hung handler is
still recovered rather than holding its message forever.

**Failure is explained, then recovered.** A worker can report *why* a delivery failed (the
exception type plus an opaque detail blob) without acknowledging it — reporting is orthogonal to
delivery. The message stays claimed and the lease sweep recovers it on the normal schedule; the
recorded reason is what turns a bare "failed *n* times" dead letter into something an operator can
diagnose without correlating logs across every worker.

**Dead-lettering is bounded retry.** Once a message exceeds its delivery-attempt cap the sweep
moves it to the queue's **dead-letter queue** instead of retrying forever, carrying its failure
block. The DLQ is inspectable and re-drivable through its own command family.

**Deferred delivery** uses an absolute UTC time, not a relative delay, so log replay after a
restart cannot re-delay a message from replay time. A deferred send is invisible to workers until
its time passes, when a claim promotes it into the live queue.

**Back-pressure refuses, never drops.** A queue has a byte cap. When it is full a send is *refused*
(a loud, named error) rather than silently dropping the oldest entry — a full queue is a signal,
not a place to lose data. A fan-out publish refuses in full when *any* target group's queue is
full, naming that group, so a message reaches every group or none.

**Shared machinery.** Two other capabilities ride on queues: **subscription groups** turn a
pub/sub `channel@group` into an ordinary derived queue (so fan-out gets the same durability and
recovery), and **recurring jobs** enqueue exactly one occurrence per due time from inside the
claim sweep — again, no broker-side timer.

## Wire surface

The queue command family — send, claim, ack, touch (renew), fail (explain), and the dead-letter
commands — is defined in the *Queue Commands* and *Dead Letter Commands* sections of
[`docs/HIGHWAY-PROTOCOL.md`](../HIGHWAY-PROTOCOL.md), which is authoritative for every argument,
reply, error code and key. Recurring-job scheduling shares the queue's claim path and is documented
alongside it.

## Guarantees & limits

- **Delivery:** at-least-once. Exactly one worker processes a given claim, but a handler must be
  idempotent because lease recovery can redeliver after a crash or timeout.
- **Ordering:** competing consumers do not guarantee global order; a subscription group preserves
  per-group order by processing one message at a time.
- **Durability:** on by default (persisted, survives restart); ephemeral is explicit opt-out and is
  surfaced, not silent.
- **Bounded costs:** a byte cap that refuses rather than drops; a delivery-attempt cap that
  dead-letters rather than loops; a client-bounded processing window that recovers hung handlers.
  These bounds are tracked line by line in [`constraints.md`](../product/constraints.md).
- **What it is not:** not a pub/sub bus (use channels for broadcast), not an ordered log, and not a
  result-returning call (use RPC when the sender needs a reply).
