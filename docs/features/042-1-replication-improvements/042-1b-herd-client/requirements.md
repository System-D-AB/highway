# Feature 042-1b — The Herd Client: Requirements

*Second sub-feature of [042-1](../requirements.md); implements the client half of the herd
model against the [042-1a contract](../042-1a-contract/design.md). Executes parent T2, T3, T4.
Touches `Highway.Client` only (042 RD9 licenses this). Starts after 042-1a closes.*

## Introduction

The client becomes the active party in failover: it holds the bootstrap endpoint list, learns
the live roster from the master, keeps exactly one live master connection, detects loss three
ways (TCP drop, narration, health timeout `x`), walks the roster's priority order to the first
*willing* node, and — the durability half — replays its own unacknowledged work to the new
master with the same request identifiers. 042's G6 groundwork (bootstrap parsing, the
serialized failover gate) is the seed; this feature grows it into the full HerdConnection.

## Requirements

### B-R1: Bootstrap parsing (parent R2.3, R13.2; contract A-R6)

1. `HighwayOptions.Server` accepts the SE.Redis comma multi-endpoint form; the existing
   `HostsOf`/`OptionsOf` parsing is the implementation. Single-endpoint strings behave
   byte-identically to today. Malformed input is refused with a sentence.
2. The parsed list is **bootstrap only**: used to reach a first node and on roster-less
   fallback; the live roster supersedes it for successor order (B-R2).

### B-R2: The live roster, client side (parent R13.2)

1. On session establishment the client reads the roster (`HW.REPL.STATUS` `roster.*` fields)
   and caches it with its version; a `ROSTER-UPDATE`/`TOPOLOGY` narration triggers a re-read.
2. The successor order is computed from the **cached roster** (priority ascending, skip
   priority-0); the bootstrap list is the fallback when no roster has ever been learned (a
   pre-042-1 server, or a one-node herd). Dynamic membership follows: a node absent from the
   bootstrap string is reachable once the roster names it.

### B-R3: HerdConnection — one live master (parent R1.4, R3)

1. Exactly **one** live master connection at a time; a transition is "disconnected, walking"
   — never two live masters observed by one client.
2. **Three triggers** start the walk: (a) TCP drop / connection loss; (b) a narration message
   on `hw:door:topology` (advisory — the client re-runs its own rule and may stay put);
   (c) the health timeout: no successful exchange within `x` (default 3s, config) against a
   TCP-alive master.
3. The walk: roster priority order, first node whose `HW.REPL.HELLO CLIENT` handshake answers
   **willing** wins; an **unwilling** answer's redirect (master endpoint) is followed in
   preference to continuing the walk blind. List exhausted → the existing
   transient/connection error class, with bounded backoff and continued retries.
4. The walk is serialized within a client (one prober; concurrent operations await its
   outcome) — the 042 G6 failover gate, retargeted.
5. `-NOTPRIMARY <endpoint> <epoch>` still redirects synchronously (042 behaviour kept): the
   walk is for when nobody can speak; the redirect is for when somebody can.

### B-R4: InFlightCache (parent R4.1)

1. The client caches every issued-but-unconfirmed request: queue/publish sends awaiting the
   broker ack (`requestId → verb + args + payload bytes`), alongside the existing RPC
   pending-call registry (which already holds unanswered calls).
2. An entry is removed the instant its ack/reply arrives. The cache is bounded by the same
   backpressure that bounds issuing (no new unbounded buffer; if a cap is needed it surfaces
   as the existing send-refusal class, never silent drop).

### B-R5: Replay on convergence (parent R4.2–R4.4, R5)

1. On converging to a new master: re-establish the session (auth, re-register services and
   subscriptions — the engine's existing startup path), **then** replay every InFlightCache
   entry with the **same requestId**, then resume normal traffic.
2. Pending RPCs are re-driven to the new master with their original request ids; the caller's
   awaited task is untouched — the reply arrives from wherever the call lands.
3. Dedupe is the server's job (replicated acked history + at-least-once contract): the client
   asserts only same-id replay. Duplicates are counted, never silently doubled (proved in
   042-1d's harness; unit-level here).
4. The ack-is-the-birth boundary (parent R5) is respected: nothing here pretends to save a
   request the client died holding.

### B-R6: Proof, client-scoped

1. Unit: successor function deterministic over a given roster; priority-0 skipped; unwilling
   redirect followed; roster supersedes bootstrap; single-endpoint unchanged.
2. Integration (single client, two nodes): TCP-drop walk lands on the willing standby; frozen
   master (health timeout) walks within `x`+margin; narration walk; replay of an unacked send
   and a pending RPC with id equality asserted at the new master.
3. The **named RPC-across-failover scenario** (parent R11.3) passes here or is explicitly
   deferred to 042-1d's harness with a pointer.

## Non-Goals

Server-side willingness/promotion (042-1c); multi-client cohesion (042-1d); narration
*emission* (042-1c — this feature only listens); sync acks.
