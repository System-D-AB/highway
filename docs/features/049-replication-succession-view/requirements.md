# Feature 049 — Replication succession view (dashboard)

*Owner request: the Replication tab lists a replica by name only. Show each member's endpoint and
priority, mark whether it's the primary, and — most importantly — present the replica set so it is
obvious **which node takes over if the primary goes away**, in succession order.*

Frontend only: every field needed (`repl.role`, `repl.endpoint`, `repl.priority`, `repl.redirect`,
and the `roster.*` id/priority/endpoint set) is already in `HW.REPL.STATUS`. No server change.

## Requirement 1: This node's identity is explicit

**Acceptance:** the header line names this node's endpoint, its role (Primary/Replica), and its
priority, rather than a bare `endpoint …`.

## Requirement 2: The attached-replica table carries endpoint and priority

**Acceptance:** the slot table joins each replica id to the roster and shows its **endpoint** and
**priority** alongside state / acked seq / lag.

## Requirement 3: A succession-ordered "Replica set"

**Acceptance:**
1. The replica set is listed in **succession order**: the current primary first (serving now), then
   the eligible standbys by ascending priority (lowest non-zero promotes first), then any priority-0
   member (never promotes) last.
2. Each row shows priority, node, endpoint, and an intuitive **status**: "Primary — serving now",
   "Next if the primary fails", "Standby — #N in line", or "Never promotes (priority 0)".
3. The current primary is identified as this node when its role is Primary, otherwise the primary it
   redirects to (`repl.redirect`), so the view is correct from either a primary or a replica.

## Non-goals

No server/protocol change; no reachability probing (endpoints remain observations).
