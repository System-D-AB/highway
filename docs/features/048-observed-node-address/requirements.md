# Feature 048 — Restore the "Seen from" node address

*Field-reported: the dashboard Nodes view shows **"not connected"** in the "Seen from" column for
every node, including nodes that are plainly connected and working. The column is a leftover from
the Garnet era that the RESP-server rewrite (040/041) left non-functional, and it actively
misleads — a developer sees "not connected" and thinks their working node is down.*

## Root cause

"Seen from" is the peer address the broker observes a node's live connection coming from. It used
to be filled by joining the node registry against `CLIENT LIST`. On the current broker:
- The dashboard reads node state **in-process** (`StoreBrokerState`), which never populated
  `SeenFrom` (it defaulted to null).
- The RESP server's `CLIENT LIST` is a no-op, so the old join has no data anyway.

So `SeenFrom` is always null → "not connected" for everyone, permanently.

## Requirement 1: The broker tracks each named connection's peer address

**User Story:** As an operator, I want "Seen from" to show where a node is actually connected from,
and "not connected" to mean a registered node with no live connection right now.

#### Acceptance Criteria

1. When a connection issues `CLIENT SETNAME <node>` (which Highway's own client already sends on
   connect, as its `ClientName`), the broker records `node → observed peer address` in a live map,
   keyed by connection for exact teardown.
2. The entry is removed when the connection closes.
3. The dashboard `StoreBrokerState.Nodes()` joins the node registry against that map to fill
   `SeenFrom`; a node with no live named connection stays null → "not connected" (now *accurate*).
4. It is an **observation, not a declaration** (constraints C7.3): the address is what the broker
   sees, which behind NAT or a load balancer may not be dialable; the column is labelled "seen
   from" for exactly that reason.

## Requirement 2: Additive and cheap

#### Acceptance Criteria

1. No protocol, wire-format, or data-format change: `CLIENT SETNAME` is already sent and already
   accepted; the peer endpoint is already available on the session. A client that never names
   itself simply has no entry.
2. No hot-path cost: a `ConcurrentDictionary` touched once per connect / disconnect and read only
   when the dashboard lists nodes. No polling, no scanning of the store.

## Known limitation

A node whose name contains characters outside `[A-Za-z0-9-_.]` is sanitised in the client's
`ClientName` (SE.Redis restriction) but not in its registry name, so the join can miss for such
names. Identifier-style node names (the norm) match exactly. Recorded, not silently ignored.

## Non-goals

Reachability/health of the observed address (it is an observation), or reintroducing `CLIENT LIST`.
