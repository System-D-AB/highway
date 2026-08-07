# Tasks: Introduce Security — Authentication and TLS

The two spikes are **done** and their findings are in `design.md`. Roles were descoped after
them, so what follows is the minimal feature: a password, TLS, client credentials, credential
hygiene, and the conformance work.

---

## Phase 0 — Spikes (complete)

### - [x] T1 — Spike: Garnet authentication mechanisms and per-name ACL

Established how `AuthSettings` works, that ACL-with-default-password needs no file, that
per-name custom ACL works, that `+@connection` is mandatory in any allow-list, that Highway's
commands are `@dangerous`, that `AclStrictCustomCommands` conflicts with Highway's
registration order, and the exact `NOPERM` string. Recorded in `design.md`.

### - [x] T2 — Spike: user creation and the AOF question

Established that `ACL SETUSER` does not persist, that nothing reaches the AOF, and that a
`nopass` default user is a total authentication bypass. Recorded in `design.md`.

**Both spikes leave scratch files** — `AclSpike.cs`, `AclUserCreationSpike.cs`,
`SimpleAuthSpike.cs`. T11 disposes of them.

---

## Phase 1 — Server

### - [x] T3 — Authentication options and validation

`AuthenticationOptions` with the password, the `IAuthenticationSettings` escape hatch, and
`CreateSettings()` mapping onto `AclAuthenticationPasswordSettings(null, password)`.
`Validate()` rejects an empty or whitespace password.

*Requirements:* R2.1, R2.4, R2.5
**Done when:** a password-configured server refuses no credentials, a wrong password, and an
unrecognised username — the three rows the spike measured.

### - [x] T4 — `WithPassword`, `WithoutAuthentication`, and the bind-address policy

Builder methods, the mapping onto `GarnetServerOptions.AuthSettings`, the policy in one
place, the informational line on loopback, the `Build()` refusal off it, and the warning
`WithoutAuthentication()` emits.

*Requirements:* R1 (all), R2.2, R2.3, R2.6
**Done when:** all five rows of the design's policy table are asserted — in particular that
**`new HighwayServerBuilder().Build()` still starts and serves traffic with no security at
all.** That is the evaluation path and the thing most likely to regress silently; it gets its
own test, named so nobody deletes it as a duplicate.

Assert the *level* of the loopback log line too. Informational rather than warning is the
design decision, not an incidental choice.

---

## Phase 2 — Client

### - [x] T5 — Client credentials

`Username`, `Password`, `ConfigureConnection` on `HighwayOptions`; the mapping onto
`ConfigurationOptions`; documented precedence between connection string, properties, and the
delegate.

*Requirements:* R3.1, R3.2, R3.3
**Done when:** all three routes work and precedence is asserted by a test rather than
described.

### - [x] T6 — Error mapping

`HighwayAuthenticationException` and `HighwayAuthorizationException`, both permanent;
`Classify` extended for `NOAUTH`, `WRONGPASS`, `NOPERM`; the refused command name attached at
the call site, since T1 established the server does not send it.

*Requirements:* R3.6, R3.7, R3.8
**Done when:** each failure is distinguishable from the others and from a network failure,
and a test asserts the **attempt count** for a wrong password — proving it was not retried,
not merely that it eventually failed.

### - [x] T7 — Credential redaction

The shared `Redact` helper; the three known leak sites fixed
(`HighwayEngine.cs:150`, `HighwayConnection.cs:100`, `HighwayConnection.cs:113`).

*Requirements:* R3.4, R3.5
**Done when:** a test places a known password in the connection string, points it at a dead
endpoint, and asserts that string appears in neither captured log output nor the exception.
Then confirm the test fails with the helper removed — a redaction test that cannot detect a
leak is worse than none.

### - [x] T8 — Authenticated client end to end

Heartbeats, doorbell subscriptions, backstop sweeps and reconnection against an authenticated
server, including re-authentication after a dropped connection.

*Requirements:* R3.9
**Done when:** reconnect-and-reauthenticate is proven by killing the connection mid-run.
StackExchange.Redis is expected to handle this from `ConfigurationOptions`; that is an
assumption about someone else's library and is therefore tested rather than trusted.

---

## Phase 3 — TLS

### - [x] T9 — Server and client TLS

`HighwayTlsOptions` both sides, `WithTls` overloads (PFX, subject name, delegate, raw
`IGarnetTlsOptions`), the mapping onto `GarnetServerOptions.TlsOptions`, certificate loading
and validation at `Build()`, and client-side target host, certificate selection and custom
validation.

*Requirements:* R4.1–R4.6, R4.8
**Done when:** a missing file, a wrong password, a subject matching nothing, and setting both
file and subject are each reported at `Build()` naming the value — not as a handshake failure
later. Garnet's own production warning is quoted in the XML docs on `WithTls` alongside the
`IGarnetTlsOptions` escape hatch.

### - [x] T10 — TLS end to end

Self-signed certificate generated **in the test**, TLS round trip, mTLS round trip, and a
mismatch producing a diagnosable error.

*Requirements:* R4.7, R5.2, R5.3, R5.4
**Done when:** the handshake is genuinely exercised with no fixture files and no external
infrastructure, and the server-TLS/client-plaintext mismatch names the likely cause instead
of surfacing an opaque socket failure.

---

## Phase 4 — Conformance

### - [x] T11 — Authenticate the test suite, and retire the spikes

`HighwayTestServer` generates a random credential per instance and authenticates by default.
Delete `AclSpike.cs`, `AclUserCreationSpike.cs` and `SimpleAuthSpike.cs`, keeping only what
becomes a real test.

*Requirements:* R5.1, R5.7
**Done when:** the full suite passes with every connection authenticated. This is the
highest-value change in the feature — it turns "authentication works" from one test into a
property of all of them, and it is what makes Requirement 1's loopback exemption defensible.
Expect fallout; anything that breaks was relying on an unauthenticated path and is a finding,
not an inconvenience.

Keep T4's deliberately-unauthenticated tests on an explicitly unauthenticated server, so both
postures stay covered.

### - [x] T12 — Protocol document

`AUTH` added to § Stock Garnet Dependencies. § Error Contract gains the third class
(`NOAUTH` / `WRONGPASS` / `NOPERM` — permanent, carrying neither existing marker). A section
covers authentication and TLS for client implementers, **including the `@dangerous` trap**:
`+@all -@dangerous` connects and then refuses every `HW.*` command.

*Requirements:* R6.1, R6.2, R6.3
**Done when:** `ProtocolConformanceTests` is green and an implementer reading only the
protocol file can build a working authenticated client.

### - [x] T13 — Product documentation and roadmap

Including a roadmap entry for the deferred roles feature, pointing at this feature's recorded
findings so it does not start from scratch.

*Requirements:* R6.4

### - [ ] T14 — Samples

Keep the default sample run **unauthenticated on loopback** — it is the evaluation path. Add
authentication and TLS as a flag or environment variable rather than a source edit.
`samples/README.md` shows the zero-configuration run first.

*Requirements:* R5.8, R5.9, R5.10, R6.5, R6.7
**Done when:** the samples have been run **both ways** — three processes with no credentials,
and three processes authenticated — and `samples/RUNLOG.md` records both. Running only the
secured path would leave the configuration every newcomer meets untested by the one exercise
that catches what unit tests cannot.

Any defect found is fixed **in the library with a regression test**, never worked around in
the sample.

### - [ ] T15 — Full verification

*Requirements:* R6.6
**Done when:** every test passes and `dotnet build` is warning-free.

---

## The lines that must not move

**1. The bare case stays free.** `new HighwayServerBuilder().Build()` starts a working,
entirely unsecured broker on loopback, and still will when this feature is done. If any task
makes it require configuration, generate a credential, warn, or fail — that task is wrong,
not the requirement.

**2. TLS is never required.** Not on loopback, not off it, not when a password is set.
Requirement 1's bind-address rule tests **authentication only**. A certificate is something
Highway cannot invent, so requiring one anywhere would produce a server that cannot start.
`WithTls` is opt-in in every configuration; the recommendation to use it where a password
crosses a network is documentation, never enforcement.
