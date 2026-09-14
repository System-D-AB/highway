# Feature 040 — RESP Server: Tasks

```
T1 (reader tests) ──► T2 (reader) ──► T4 (handler+dispatch) ──► T5 (TLS) ──► T7 (doorbells)
T3 (vendor writer) ──┘                     └──► T6 (auth) ──────┘         └──► T8 (test server) ──► T9 (protocol doc)
```

### - [ ] T1 — The reader's suite, first *(037 T4.1)*

**Fulfills:** R1.1
Byte-by-byte feeding of every frame shape, malformed corpus, max-frame bound.
**Done when:** the suite exists, red, against a stub reader interface.

### - [ ] T2 — The reader

**Fulfills:** R1.2, R1.3
**Done when:** T1 green; need-more on partials; bounded buffering.

### - [ ] T3 — Vendor `RespWriteUtils.cs`

**Fulfills:** R2
**Done when:** copied with notices; `THIRD-PARTY-NOTICES.md` entry; reply round-trip
tests green through reader+writer.

### - [ ] T4 — `RespConnectionHandler` + state machine + dispatch

**Fulfills:** R3.1, R3.2, R3.4
Kestrel handler, connection state machine per design table, dispatch over 041's
transport seam, graceful shutdown with `CancellationToken` flow.
**Done when:** pinned SE.Redis connects — with the CommandMap/ConfigurationOptions verified against it and recorded as OD3's answer (tiebreaker, connect-time probes; **this passing is gate G0**) — and round-trips `HW.*`
against the real dispatch; out-of-subset commands error naming the subset;
state-machine table tests green; 039's socketless tests still green.

### - [ ] T5 — TLS endpoints

**Fulfills:** R3.3
Non-HTTP-ALPN `ListenOptions` established and recorded; plaintext + TLS both served.
**Done when:** SE.Redis `ssl=true` round-trips; ALPN verified non-HTTP.

### - [ ] T6 — Auth *(037 R11 complete)*

**Fulfills:** R4
Config users + PBKDF2 + recipe; both `AUTH` forms; `-NOAUTH` gate;
`WithoutAuthentication()` + loopback exemption; constant-time compare.
**Done when:** all R11 criteria green including the client's unmodified
`user:pass@` path and `HighwayAuthenticationException`; C6.x/012 amendment text
staged for 041.

### - [ ] T7 — Doorbell pub/sub

**Fulfills:** R5
`SubscriptionRegistry`, subscribe/unsubscribe/push frames, restricted mode,
`IDoorbellSink` wiring from 039's command runtime.
**Done when:** `DoorbellWatcher` receives end-to-end against the real server;
disconnect-mid-publish test green; nothing doorbell-shaped is ever written to the
store (assert).

### - [ ] T8 — The embedded test server

**Fulfills:** R6
`HighwayTestServer` internals → loopback Kestrel + temp-dir RocksDB (+ in-memory
switch), public surface preserved.
**Done when:** existing integration tests compile-and-pass with only fixture-internal
changes; startup cost measured and recorded (≤2× Garnet fixture).

### - [ ] T9 — Document the served subset *(the one permitted protocol-doc edit)*

**Fulfills:** R3.5
**Done when:** `HIGHWAY-PROTOCOL.md` states exactly which RESP commands the server
serves (as-built), and changes nothing about `HW.*`.
