# Feature 049 — Tasks

### - [x] T1 — Succession view in replication.js

**Fulfills:** R1, R2, R3
`replication.js`: header line shows this node's endpoint + role + priority; the attached-replica
table joins the roster for endpoint + priority; a "Replica set" table lists members in succession
order (primary first, then eligible standbys by ascending priority, priority-0 last) with an
intuitive status ("Primary — serving now" / "Next if the primary fails" / "Standby — #N in line" /
"Never promotes"). The current primary is this node when Primary, else `repl.redirect`.
**Done when:** the tab shows endpoints, priorities, and who is next — verified against a running
primary/replica pair. Frontend only; no server change.
