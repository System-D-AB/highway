# Registry & Heartbeat

> **Status:** current
> **Protocol:** the *Registry Commands* section of [`docs/HIGHWAY-PROTOCOL.md`](../HIGHWAY-PROTOCOL.md)
> is authoritative for `HW.HEARTBEAT` / `HW.DISCOVER` / `HW.STATS`. Related guarantees are C2.3 and
> C7.3 in [`constraints.md`](../product/constraints.md). This doc explains how the registry works.

*The registry is how the broker knows which nodes are alive and what each one hosts: it powers
service discovery and load balancing, bounds how long an absent subscriber's backlog is held, and
reports where a node is seen connected from — as an observation, never a stored declaration.*

## Overview

Every node tells the broker, on a heartbeat, which services and channels it hosts (its *catalog*)
and that it is still alive. From that live picture the broker answers "who hosts this service?" for
RPC routing, decides when an absent subscriber group has truly gone, and shows operators the cluster
on the dashboard.

## How it works

**One command, four forms.** `HW.HEARTBEAT` registers a node's catalog, proves liveness on a
recurring beat, or departs — and departure has two distinct meanings that must not be confused:

- **`BYE`** — "I am stopping." A subscriber's backlog is preserved; it expects to return.
- **`BYE PURGE`** — "I am never coming back." It retires the node's subscriber groups, destroying
  their queues, and reports the groups, messages and bytes removed.

**The catalog travels once; a beat is 8 bytes.** A node's catalog cannot change while it runs, so the
client sends it once at registration and every later beat is a bare `HW.HEARTBEAT <node>`. The server
mirrors that: the catalog record is written once, and each beat refreshes a separate 8-byte liveness
key (`hw:reg:seen:{node}`) — so a beat costs a tiny write on the primary and ships 8 bytes to each
standby, regardless of catalog size (feature 060). The broker only asks for the catalog again
(`+REGISTER`) if it has no record for the node — after a prune, or if the registry was lost.

**Discovery and load balancing.** `HW.DISCOVER` returns the live nodes currently hosting a service,
which is what lets RPC and queue work spread across competing consumers without any client-side
routing table. `HW.STATS` exposes server, service, channel and recorder counters (and appends
replication fields when replication is on).

**Retirement bounds the pub/sub backlog.** A subscriber group's queue holds messages while the
group is away — but "away forever" would fill a disk and, with byte budgets, block the channel for
healthy subscribers. So a group is retired three ways: the node says so (`BYE PURGE`), an operator
says so, or the broker decides after every backing node has missed the heartbeat past
`SubscriberRetirementThreshold` (24 h). Since groups can have several members, liveness is the
*youngest member's* heartbeat — one live replica keeps the whole group and its siblings' backlog
alive. Retirement is never silent: it warns, records a `GroupRetired` event, and reports what it
destroyed. This is the evidence-based bound that generic idle timers (`x-expires`,
`AutoDeleteOnIdle`) cannot express, because they cannot tell "nobody consumed" from "nobody is
alive."

**An address is an observation, never a declaration.** The broker reports where it currently sees a
node connected from — taken from the live socket (`CLIENT SETNAME` on connect, read back on demand)
— labelled **"seen from"** everywhere, and simply *absent* (not stale) for a registered node that is
not connected. Highway never asks a node its address and never stores one: a node behind NAT, in a
container, or scaled horizontally under one name would report a number nobody can reach, and a
stored address would outlive the socket it described.

## Wire surface

`HW.HEARTBEAT` (register / beat / `BYE` / `BYE PURGE`), `HW.DISCOVER` and `HW.STATS` are defined in
[`docs/HIGHWAY-PROTOCOL.md`](../HIGHWAY-PROTOCOL.md), along with the registry key schema and the
mirror keys that map nodes to the services and groups they back.

## Guarantees & limits

- **Discovery reflects live nodes**, so RPC and queue load spreads across whoever is up.
- **Backlog holding is bounded by evidence** (heartbeat absence), and retirement is loud, never a
  silent drop (C2.3).
- **`BYE` ≠ `BYE PURGE`:** stopping preserves a backlog; never-coming-back destroys it. Confusing
  the two loses data, so they are separate statements.
- **"Seen from" is an observation** — present only while connected, never stored, never a routing
  fact (C7.3).
