# Feature 040 — The RESP Server

*Executes 037 Phases 1 and 4: the socket Garnet used to be. Authorities: 037 R6 (the
server), R7 (doorbells), R11 (auth), R10 (transport seam), D3/D4. There is no
standalone spike (owner's call, 2026-09-15): **gate G0 — SE.Redis talks to our
server — is settled by this feature's first passing handshake test**, and OD3 (the
exact `CommandMap`/`ConfigurationOptions`) is verified here against the pinned
SE.Redis — verify, never assume; candidate probes to check: connect-time `CONFIG`/
`CLIENT`/`ECHO`, the tiebreaker `GET` (`TieBreaker = ""`), heartbeat cadence. Also
delivers the embedded test server 041's gates silently depend on.*

## Requirements

### Requirement 1: The reader, tests first (037 R6.1)

#### Acceptance Criteria

1. The byte-by-byte suite exists **before** the reader: every frame fed one byte at a
   time parses identically to whole-frame parsing; malformed input errors without
   corrupting the connection state machine.
2. The reader parses arrays of bulk strings off `SequenceReader<byte>` over the
   Kestrel pipe; incomplete frames return *need more*, never throw.
3. Oversized frames are bounded: a declared max (aligned with the existing payload
   limit) closes the connection with a legible error, never unbounded buffering.

### Requirement 2: The writer is vendored, once (037 R2.3, R6.2)

#### Acceptance Criteria

1. `RespWriteUtils.cs` copied with the Microsoft copyright + MIT notice intact;
   `THIRD-PARTY-NOTICES.md` entry added; it is the only Garnet-derived source in the
   repository.
2. The five RESP2 reply types `HW.*` uses round-trip through it against the reader's
   suite.

### Requirement 3: The handler, the subset, TLS (037 R6.3–R6.6)

#### Acceptance Criteria

1. Kestrel `ConnectionHandler` serves dispatch over the transport seam — commands
   cannot tell a socket delivered them (037 R10.1 stays true; 039's socketless tests
   keep passing).
2. The handshake subset is exactly what OD3 verification establishes (see the intro); anything outside it returns
   an error **naming the subset** — never a plausible `+OK`. `PUBLISH` is *not*
   served: doorbells are published server-internally, clients only subscribe.
3. TLS via Kestrel `ListenOptions` with **non-HTTP ALPN — verified here, not assumed**; plaintext and TLS
   endpoints both round-trip with pinned SE.Redis.
4. Graceful shutdown drains in-flight commands within the host timeout;
   `CancellationToken` flows from connection close into command execution.
5. `HIGHWAY-PROTOCOL.md` gains the served-subset section — **the one permitted edit**
   (037 T4.6).

### Requirement 4: Auth (037 R11, in full)

#### Acceptance Criteria

1. All five 037 R11 criteria hold: config users with hashed passwords + documented
   hash recipe; both `AUTH` forms; binary enforcement (`AUTH`/`PING` only before
   auth, `-NOAUTH` naming the fix otherwise); `WithoutAuthentication()` and loopback
   exemption preserved; client's `user:pass@` string and
   `HighwayAuthenticationException` path work unmodified.
2. Constraint amendments and 012-finding retirements are staged here and land with
   041's register pass.

### Requirement 5: Doorbell pub/sub (037 R7)

#### Acceptance Criteria

1. `SUBSCRIBE`/`UNSUBSCRIBE` confirmations, push frames, and RESP2
   subscribed-connection restricted mode work against SE.Redis's separate subscriber
   connection.
2. Command-side doorbell publishes reach the subscription registry in-process; a
   subscriber that disconnects mid-publish costs nothing but that delivery.
3. The rig-level "doorbells off, everything still passes" proof (037 R7.1) is 041's;
   here, `DoorbellWatcher` against the real server receives a doorbell end-to-end.

### Requirement 6: The embedded test server

`HighwayTestServer` is embedded Garnet today; every integration test uses it. Its
replacement is this feature's deliverable, before 041 needs it.

#### Acceptance Criteria

1. A new embedded server: in-process Kestrel on a loopback port + `RocksDbStore` in a
   temp directory — the real stack, no external infrastructure (house rule).
2. Startup/teardown fast enough for the suite (target: no slower than the Garnet
   fixture by more than 2×; measured, recorded).
3. The existing integration-test fixture API is preserved so test bodies do not
   change — only the fixture's internals (T6.1's "suite passes unmodified" depends
   on this).
4. An `InMemoryStore` variant exists for tests that want speed over engine fidelity,
   chosen per test class, defaulting to the real stack.
