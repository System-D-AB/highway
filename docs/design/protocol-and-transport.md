# Protocol & Transport

> **Status:** current
> **Protocol:** every command, argument, reply shape, error code, key and doorbell is defined in
> [`docs/HIGHWAY-PROTOCOL.md`](../HIGHWAY-PROTOCOL.md), which is machine-checked against a running
> server. This doc explains the *shape and philosophy* of the wire protocol and never restates it.

*Highway speaks RESP framing but a command surface entirely its own: a small set of `HW.*` commands,
each one atomic and a single round trip, with errors carried as data rather than thrown.*

## Overview

The broker is reached over **RESP** — the Redis serialization protocol — so ordinary tooling
(`redis-cli`, StackExchange.Redis `Execute()`, RESP analyzers) connects and frames messages without
modification. But the vocabulary is Highway's: the server registers its own `HW.*` commands and
serves *only* that subset plus the RESP handshake and the one RPC reply-slot key. A client built
from stock Redis commands cannot drive Highway, and a Highway client issues semantic commands, not
Redis primitives. This is a deliberate trade — compatibility given up for correctness and a thin
client.

## How it works

**One command, one round trip.** Every operation is a single command the server executes
atomically; the client never orchestrates a multi-step workflow. `HW.PUBLISH` enqueues to *all*
subscriber groups or none; an RPC enqueue-and-notify is one command; acknowledgement and lease
semantics are built into the commands rather than assembled client-side. Atomicity lives on the
server, inside the transaction that touches the keys — which is what lets Highway promise
all-or-nothing fan-out and race-free claims that a generic client wrapper cannot.

**The command families.** The surface groups into RPC, pub/sub, registry, observability, queue and
dead-letter, and replication commands. The exact roster is the machine-checked
[Command Index](../HIGHWAY-PROTOCOL.md#command-index); no count is repeated here on purpose, because
every prose copy of it has drifted.

**Errors are data, not exceptions.** A handler result carries an `Output.StatusCode` (HTTP-style),
so a business failure is a value the caller inspects, never a swallowed or thrown surprise. On the
wire, protocol-level conditions use RESP error replies with stable codes — `HW_QUEUE_FULL`,
`HW_PRIORITY_TAKEN`, and the like — which the client maps to typed, permanent-or-transient outcomes.
A few conditions use a bare RESP error without the `HW_` prefix by design; the most important is
`-NOTPRIMARY <endpoint> <epoch>`, which redirects a write aimed at a standby to the current master
(see [replication-and-failover.md](replication-and-failover.md)).

**Keys, framing and doorbells.** Highway owns a structured key schema (`hw:*`), a compact binary
entry framing for queued work, and **doorbell** pub/sub channels the server rings so idle workers
and subscribers wake without polling. These are internal contracts, fully specified in the protocol
file; a capability doc points at them rather than duplicating the byte layout.

**No runtime capability negotiation — and that is a known gap.** The protocol version is
documentation for humans; nothing negotiates it at connect time and no command reports it. A client
too old to follow a `-NOTPRIMARY` redirect is not detected and warned today. Closing that — a
server↔client capability handshake — is a planned safety feature, not a property to assume exists.

## Wire surface

The whole protocol lives in [`docs/HIGHWAY-PROTOCOL.md`](../HIGHWAY-PROTOCOL.md): read
*Transport & Framing* and the *Error Contract* first, then the command sections a client needs. That
file is authoritative; these design docs only explain *why* the surface is shaped the way it is.

## Guarantees & limits

- **Atomic per command:** each `HW.*` command is all-or-nothing; there are no partial multi-step
  states for a client to reconcile.
- **Tooling-compatible framing, incompatible vocabulary:** RESP framing works everywhere; the
  command set does not pretend to be Redis.
- **Errors are legible and typed:** status codes and stable error codes, permanent vs transient,
  never a silent hang — a caller always gets an answer or a timeout.
- **No capability handshake yet:** version/protocol skew between client and server is currently
  undetected; treat matching versions as an operational requirement until the negotiation feature
  lands.
