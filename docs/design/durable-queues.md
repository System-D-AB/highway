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

Every queue (and every RPC service, and every subscriber group's queue) lives in its own **box** — a
contiguous key range found through the broker's catalog — so a queue and a service may share a name
without colliding, and a queue never appears in service discovery.

## How it works

**Send → claim → ack.** A send appends the message and rings the queue's doorbell so idle workers
wake. A worker *claims* work with `HW.CLAIM`, receiving each message's id, payload and a **token**;
the claim starts a **lease**. When the worker finishes it *acks* the token, and the message is gone.
Until the ack arrives the message is held as that claim — which is exactly what makes delivery
at-least-once. The token names the claim directly, so an ack, a renewal or a failure report is one
lookup, and a stale token (the claim was recovered and redelivered) answers `:0` rather than settling
the redelivery.

**Lease recovery is the safety net.** Every claim first recovers expired leases: a message whose
lease elapsed returns to the queue with its attempt count incremented, so a worker that died
mid-processing loses nothing. There is no background timer doing this — recovery, deferred-delivery
promotion and recurring-job firing all happen *on the claim path*, decided by the broker's clock.
Claims are indexed by lease start, so recovery reads **only the claims that actually expired** — a
message still inside its lease is never touched, and a claim with nothing expired reads nothing
(feature 065; 061 had narrowed it to a throttled, read-only sweep of every worker's list).

**In-flight work is changed in place, never re-written.** Ack, fail and touch act on the one claim
they name — a delete of its key, or an in-place update — and leave every other claim exactly where it
is. The broker's store is an ordered LSM (RocksDB), where a delete leaves a tombstone that later scans
must step over; so the hot paths neither create churn nor walk history: a claim starts from the
queue's stored head, recovery and promotion seek their indexes from a known floor, and every scan is
bounded to its own sub-range. Every count — depth, in flight, deferred, dead-lettered, bytes — is a
counter the queue maintains as it changes, never a scan. The cost of a send, claim or ack therefore
does not grow with how long the broker has been running, nor with how much is in flight (features 061,
065).

**Slow work vs. dead work.** A handler that outlives its lease would be redelivered while still
running — a duplicate caused by nothing but slowness. A worker renews its lease ("still working")
to hold the message; Highway's client renews automatically. The bound is enforced by the
**client, on the handler**: at the max processing time the handler is cancelled, and renewal
continues only until it has stopped — so no second worker starts the message under a running first
copy. A handler that ignores the cancellation is *abandoned* a grace period later: its worker slot is
freed, renewal stops and lease recovery redelivers the message. The number of abandoned handlers
still running is bounded, after which the worker stops claiming and says so — a hung handler
degrades a worker visibly, never silently freezes it (C1.6, C1.8).

**A retried send is stored once.** A send whose reply was lost is retried with the same message id;
the broker remembers ids it has stored on a queue for a duplicate window (10 min) and answers a
repeat `+OK` without storing it again. An id is remembered only after its message commits, so a
refused send is never suppressed on retry. The memory is per broker process and is cleared when the
epoch changes — after a failover a re-send may be stored again, which at-least-once delivery
already allows (C1.7).

**Failure is explained, then recovered.** A worker can report *why* a delivery failed (the
exception type plus an opaque detail blob) without acknowledging it — reporting is orthogonal to
delivery. The message stays claimed and the lease sweep recovers it on the normal schedule; the
recorded reason is what turns a bare "failed *n* times" dead letter into something an operator can
diagnose without correlating logs across every worker.

**Dead-lettering is bounded retry and notification.** Once a message exceeds its delivery-attempt cap (or configured `[Retry]` attempts), the sweep moves it to the queue's **dead-letter queue** instead of retrying forever, carrying its failure block and lineage trailer. The DLQ is inspectable and re-drivable through `HW.DLQ` and `IHighwayDeadLetters`. When an application registers a dead-letter handler (`IDeadLetter<T>`), dead-lettering also writes a notification to the target's feed, in the same commit, and rings its doorbell. The handler receives the dead letter as stored — the failure that killed it and how often it was requeued — and its decision (`KEEP`, `DISCARD`, `REQUEUE`) is applied in the commit that settles the notification, so a crash cannot requeue a message twice. Highway never re-feeds a dead letter by itself; only a handler or an operator does (the wire surface is in the [protocol](../HIGHWAY-PROTOCOL.md#hwdlq)).

**Declarative retry policies.** Queue contracts annotate messages with `[Retry(MaxAttempts = n, Backoff = "exp:500ms..30s:jitter", MaxRequeues = m)]`. During lease recovery sweeps, failed messages are rescheduled into delayed space with deterministic integer FNV-1a jitter before exhausting attempts and dead-lettering. Each requeue increments the message's requeue count, which a handler sees and `MaxRequeues` caps (default 3; 0 = never). A contract without `[Retry]` declares the default policy, so removing the attribute clears what the broker stored.

**Deferred delivery** uses an absolute UTC time, not a relative delay, so a restart cannot re-delay a
message. A deferred send is invisible to workers until its time passes, when a claim promotes it into
the live queue. Deferred messages count against the queue's byte budget.

**Back-pressure refuses, never drops.** A queue has a byte cap. When it is full a send is *refused*
(a loud, named error) rather than silently dropping the oldest entry — a full queue is a signal,
not a place to lose data. A fan-out publish refuses in full when *any* target group's queue is
full, naming that group, so a message reaches every group or none.

**Shared machinery.** Two other capabilities ride on queues: **subscription groups** turn a
pub/sub `channel@group` into an ordinary derived queue (so fan-out gets the same durability and
recovery), and **recurring jobs** enqueue exactly one occurrence per due time from inside the
claim sweep — again, no broker-side timer.

## Wire surface

The work commands — `HW.SEND`, `HW.CLAIM`, `HW.ACK` / `HW.TOUCH` / `HW.FAIL` by token — and the
dead-letter commands are defined in the *Work Commands* and *Dead Letter Commands* sections of
[`docs/HIGHWAY-PROTOCOL.md`](../HIGHWAY-PROTOCOL.md), which is authoritative for every argument, reply
and error code. Recurring-job scheduling shares the queue's claim path and is documented alongside it;
the box layout is described in [storage-engine.md](storage-engine.md).

## Guarantees & limits

- **Delivery:** at-least-once. Exactly one worker processes a given claim, but a handler must be
  idempotent because lease recovery can redeliver after a crash or timeout.
- **Ordering:** competing consumers do not guarantee global order; a subscription group preserves
  per-group order by processing one message at a time.
- **Durability:** on by default (persisted, survives restart); ephemeral is explicit opt-out and is
  surfaced, not silent.
- **Bounded costs:** a byte cap that refuses rather than drops; a delivery-attempt cap that
  dead-letters rather than loops; a client-bounded processing window that cancels hung handlers
  and a bounded number of abandoned ones (C1.8).
- **Retried sends are not stored twice** within the duplicate window, per broker process (C1.7).
  These bounds are tracked line by line in [`constraints.md`](../product/constraints.md).
- **Cost independent of history and of in-flight size:** a poll, claim or ack costs the same after a
  million messages as after the first, and with ten thousand claims in flight as with one
  (constraint C4.9). An idle poll with messages in flight writes nothing.
- **What it is not:** not a pub/sub bus (use channels for broadcast), not an ordered log, and not a
  result-returning call (use RPC when the sender needs a reply).
