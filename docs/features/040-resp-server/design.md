# Feature 040 — RESP Server: Design

Architecture is 037 design §4 and is not restated. This file fixes the component
boundaries, the connection state machine, and the auth/doorbell mechanics.

## Components

```
Kestrel endpoint (TLS, non-HTTP ALPN) ─► RespConnectionHandler
    ├─ RespReader        (ours; arrays of bulk strings; need-more on partial)
    ├─ ConnectionState   (unauthenticated → authenticated → [subscribed])
    ├─ CommandDispatcher (transport seam — same entry 041's tests use)
    ├─ RespReplyWriter   (over vendored RespWriteUtils)
    └─ SubscriptionRegistry (channel → connections; in-process fan-out)
```

## The connection state machine

| State | Accepts | Everything else |
|---|---|---|
| Unauthenticated | `AUTH`, `PING` | `-NOAUTH …` naming the fix (037 R11.3) |
| Authenticated | handshake subset + `HW.*` + `SUBSCRIBE` | error naming the subset (037 R6.3) |
| Subscribed | `SUBSCRIBE`/`UNSUBSCRIBE`/`PING` (RESP2 restricted mode) | error per RESP2 |

Loopback + `WithoutAuthentication()` short-circuit straight to Authenticated —
preserving C6.x exactly as today.

## Auth mechanics (037 R11)

`users` in `highway.json`: `[{ name, passwordHash }]`, PBKDF2 (iterations + salt
encoded in the hash string, `hash-password` recipe documented in the config docs).
Verification is constant-time compare. Failures: permanent and legible (C6.3) — the
connection stays open, retry allowed, each failure answered `-ERR invalid credentials`;
no lockout state (single-team model, unchanged semantics).

## Doorbells

Commands raise doorbells through a `IDoorbellSink` given to the command context (041's
runtime already routes through an abstraction; here it lands on
`SubscriptionRegistry`). Publish is: snapshot the channel's connection list, write the
push frame per connection with a per-connection write lock, drop on any error — lossy
by spec (037 R7). Nothing is stored; `hw:door:*` never touches RocksDB
(physical-layout §3).

## The embedded test server

`HighwayTestServer` keeps its public surface; internally: `WebApplication` (minimal
host) with the RESP endpoint on port 0, temp-dir `RocksDbStore` deleted on dispose,
optional `InMemoryStore` constructor switch. Client fixtures connect exactly as
production does — the same SE.Redis config this feature verifies and records, which makes every
integration test a handshake conformance test for free.

## Testing strategy

| Layer | Proof |
|---|---|
| Reader | R1.1 byte-by-byte suite + malformed corpus + max-frame bound |
| State machine | table-driven: every state × every command class |
| Handshake | pinned SE.Redis against the real server: connect, auth, execute, subscribe — asserted against the sequence recorded during T4 |
| Doorbells | end-to-end `DoorbellWatcher` receipt; disconnect-mid-publish loses only that delivery |
| Shutdown | in-flight command completes or cancels within timeout; connections closed cleanly |
| Test server | existing fixture-based integration tests compile-and-pass with internals swapped |
