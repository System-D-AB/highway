# Feature: Introduce Security — Authentication and TLS

## Introduction

Highway has **no authentication and no transport security**. Anyone who can reach the RESP
port can issue every `HW.*` command, drain every queue, and — since feature 002 — replay
recorded message payloads. Every byte crosses the network in the clear.

Garnet provides both halves and Highway uses neither: `GarnetServerOptions.AuthSettings`
and `GarnetServerOptions.TlsOptions`. This feature wires them up on the server, matches them
on the client, and fixes three credential-handling defects that become real the moment a
password exists.

### Deliberately minimal

The operational model this serves is the ordinary one: **an administrator sets a password on
the broker and gives it to the team, who put it in their clients.** Garnet supports that with
a single call and no configuration file, which is what this feature exposes.

**Per-command roles are explicitly out of scope**, and were removed after the spikes rather
than before — see [Deferred: Roles](#deferred-roles). Everything learned about them is
recorded in `design.md`, so the later feature starts from evidence instead of repeating the
work.

**It does not make Highway harder to start.** Running with no security at all stays the
zero-configuration default on loopback. What changes is that an unauthenticated broker can no
longer reach the network by accident.

### Three defects this feature must fix

Found while researching, and in scope because they are credential-handling bugs:

1. `HighwayEngine` logs `HighwayOptions.Server` at **Information** level. Once that string carries `password=`, the credential lands in every log sink.
2. `HighwayConnection.ConnectAsync` puts the full configuration string into an `ArgumentException` on a parse failure.
3. The same method passes it to `HighwayServerUnreachableException`, so an unreachable server produces an exception whose message contains the password.

## Requirements

### Requirement 1: Zero-Friction Locally, Impossible to Expose by Accident

**User Story:** As someone evaluating Highway, I want `new HighwayServerBuilder().Build()` to just work with no credentials and no reading — and as an operator, I want it to be impossible to put that same server on the network without noticing.

**The rule.** Authentication is **not required on loopback** and **is required off it**.

**Why.** Running with no security is the *right* configuration for development and
evaluation, and anything that taxes it — an exception, a required call, a generated password
to copy out of a log — is paid by every newcomer to protect a case that does not exist on
loopback. Highway already draws this exact line twice: `BindAddress` defaults to loopback
(004.1), and feature 011's dashboard binds loopback and warns beyond it.

The counter-argument — that a loopback exemption means the tested configuration is not the
deployed one — is answered in Requirement 5 instead: `HighwayTestServer` authenticates by
default, so the suite exercises the authenticated path regardless of what users do. That is
the right place to solve it.

#### Acceptance Criteria

1. **`new HighwayServerBuilder().Build()` succeeds and runs with no security whatsoever** — no credentials, no certificate, no extra call, no generated password to find in a log
2. This remains true for the samples and every evaluation path a newcomer meets first
3. A loopback server with no authentication logs **one informational line** saying so — informational, not a warning, because on loopback it is not a problem and crying wolf trains people to filter the category
4. A server bound to a **non-loopback** address with no authentication **fails at `Build()`**, naming the bind address and both remedies
5. `WithoutAuthentication()` exists as the explicit override for a deliberately open server on a trusted network, and logs a **warning** naming the address and what is exposed
6. The rule depends only on the bind address, lives in one place in the code, and is documented on both `WithBindAddress` and `WithAuthentication`
7. Nothing in this requirement changes the RESP wire format or the `HW.*` command set

### Requirement 2: Server Authentication

**User Story:** As an administrator, I want to set a password on the broker in one line and hand it to my team.

#### Acceptance Criteria

1. A password is configured in **one builder call**, with **no configuration file** anywhere — verified available in Garnet via `AclAuthenticationPasswordSettings(null, password)`
2. With a password set, the server refuses: no credentials, a wrong password, and an unrecognised username
3. Authentication applies to **every** connection, including any Highway makes itself
4. Configuration is validated at `Build()` — an empty or whitespace password is rejected there, consistent with `HighwayServerOptions.Validate`
5. An operator can supply a fully-formed `IAuthenticationSettings` directly, so ACL files, named users and Entra ID remain reachable without waiting for Highway to wrap them. Highway cannot reason about a custom instance, so for Requirement 1's rule it counts as authentication configured, and that is documented rather than silently assumed
6. The credential is never written to a log, an exception message, or the dashboard, at any level

### Requirement 3: Client Authentication and Credential Hygiene

**User Story:** As an application developer, I want to give the Highway client a password the same way I give it a server address — and never find it in a log file.

#### Acceptance Criteria

1. `HighwayOptions` accepts a password (and username) as first-class properties, not only as connection-string fragments
2. Credentials already present in the connection string keep working, and the precedence between the two is defined and documented rather than incidental
3. A `ConfigureConnection` escape hatch exposes the underlying `ConfigurationOptions`, so certificate selection and custom validation are reachable without Highway modelling every StackExchange.Redis knob
4. **Credentials never appear in a log entry or an exception message.** The three known leak sites are fixed, and connection strings are redacted through a single shared helper so a fourth is hard to add casually
5. A test asserts redaction directly: a password placed in the connection string appears in neither captured log output nor the exception raised by an unreachable server
6. An authentication failure produces a **clear, distinct** error naming the cause. `NOAUTH`, `WRONGPASS` and `NOPERM` are distinguishable from a network failure and from an ordinary command error
7. Authentication failures are **permanent** and are never retried, matching the existing permanent/transient split — retrying a wrong password wastes the backoff budget and trips attempt counters on systems that keep them
8. A permission failure names the command that was refused. Spike T1 established that Garnet's reply does **not** carry it (`NOPERM this user has no permissions to run the command`), so the client attaches it from the call site
9. All existing client behaviour — heartbeats, doorbell subscriptions, backstop sweeps, reconnection — works unchanged against an authenticated server, including re-authenticating after a reconnect

### Requirement 4: Transport Security (TLS)

**User Story:** As an operator, I want traffic between my nodes and the broker encrypted, because it carries application payloads and, now, a password.

**Why this is not optional in practice.** Requirement 2 introduces a password sent over the
wire. Without TLS, `AUTH` transmits it in the clear, and a feature that adds a credential
without offering a way to protect it in transit has moved the problem rather than solved it.

#### Acceptance Criteria

1. The server can be started with a certificate from a **PFX file with a password**, and from a **certificate store subject name** — the two mechanisms `GarnetTlsOptions` supports
2. Certificate refresh frequency is configurable, so a rotated certificate is picked up without a restart
3. **mTLS is supported**: the server can require a client certificate, with a configurable revocation-check mode and issuer certificate path
4. The client can enable TLS, set the target host name for validation, present a client certificate, and supply custom validation for private certificate authorities
5. An operator can supply a fully-formed `IGarnetTlsOptions` directly. **Garnet's own `GarnetTlsOptions` is documented in its source as a sample not intended for production without review** — Highway surfaces that warning where an operator will see it, and does not present the convenience wrapper as an endorsement
6. TLS configuration is validated at `Build()`: a missing certificate file, an unreadable one, a wrong password, and a subject name matching nothing are each reported there naming the file or subject, rather than surfacing as an opaque handshake failure later
7. A server-TLS / client-plaintext mismatch produces a diagnosable error, because a mismatched handshake is otherwise one of the least legible failures in networking
8. TLS is **off by default and never mandatory**. A certificate cannot be invented, so a TLS-by-default server would be one that cannot start. Requirement 1's rule covers authentication only; TLS is documented as strongly recommended wherever a password crosses a network

### Requirement 5: Tests and Samples

**User Story:** As a contributor, I want the suite to exercise the configuration that ships — and the zero-configuration path to keep working.

#### Acceptance Criteria

1. `HighwayTestServer` authenticates by default, generating a random credential per instance, so the integration suite exercises the authenticated path throughout. **This is what makes Requirement 1's loopback exemption safe**
2. Integration tests continue to require no external infrastructure — no certificate authority, no ACL file, no manual setup
3. TLS is covered end-to-end with a **self-signed certificate generated in the test**, so the handshake is genuinely exercised rather than mocked
4. mTLS is covered at least once end-to-end
5. A test proves an unauthenticated client is **refused** by an authenticated server, legibly
6. Tests prove **both halves of Requirement 1's rule**: a loopback server with no configuration builds and serves traffic, and a non-loopback server with no configuration fails at `Build()`
7. A test proves `WithoutAuthentication()` still works, since it is a supported configuration and must not rot
8. **The samples keep working with no credentials.** The default sample configuration stays loopback and unauthenticated — it is the evaluation path AC of Requirement 1 protects
9. The samples demonstrate authentication and TLS as a **switchable** configuration — an environment variable or flag, not a source edit — and are run **both ways**
10. `samples/README.md` shows the zero-configuration run first, then the authenticated and TLS configurations

### Requirement 6: Documentation and Living Conformance

#### Acceptance Criteria

1. **`docs/HIGHWAY-PROTOCOL.md` is updated in this feature**, per the steering rule. No `HW.*` command changes, but client-visible behaviour does:
   - `AUTH` joins § Stock Garnet Dependencies — a client that cannot authenticate cannot function against a secured server
   - § Error Contract gains a third class: `NOAUTH` / `WRONGPASS` / `NOPERM` are permanent and carry **neither** existing marker, so an implementer following today's binary rule has nowhere to put them
   - A section covers authentication and TLS as they affect a client implementer, including the `@dangerous` trap below
2. **The `@dangerous` trap is documented.** Spike T1 established that Highway's commands fall under Garnet's `@dangerous` ACL category, so `+@all -@dangerous` — a common hardening idiom — connects successfully and then refuses every `HW.*` command. That looks exactly like a Highway bug and is not
3. `ProtocolConformanceTests` stays green
4. `docs/product/product.md` and the roadmap are updated
5. The samples are re-run within this feature and `samples/RUNLOG.md` gains an entry
6. All existing tests pass and `dotnet build` produces zero warnings
7. Any defect the sample run exposes is fixed in the library with a regression test, never worked around in the sample

## Deferred: Roles

Per-command roles were specified, spiked, and then **removed from this feature** once the
deployment model became clear: one shared credential for a team gains nothing from
per-service permissions, and the machinery to support them — ACL file generation, password
hashing, `nopass` detection, `hw.*` name validation — is most of the feature's cost.

They are deferred, not abandoned. `design.md` records what the spikes established, so the
later feature starts from evidence:

- Per-name ACL rules for custom commands **work**, in both directions (`+hw.call`, `-hw.replay`)
- Every role must grant `+@connection`, or StackExchange.Redis cannot complete its handshake
- Highway's commands are in `@dangerous`, not `@admin`
- `AclStrictCustomCommands` must be disabled, because Garnet validates ACL command names during `GarnetServer` construction and Highway registers its commands after it
- A `nopass` default user is a **total authentication bypass** — any username and any password authenticate as the `+@all` default user
- Garnet ACL key patterns are all-or-nothing, so roles could restrict *commands* but never *data*

## Non-Goals

- **Per-command roles and ACL files.** Deferred above. The `IAuthenticationSettings` escape hatch (R2 AC5) keeps them reachable for anyone who needs them now.
- **Named users.** Without an ACL file Garnet supports exactly one user, `default`. Highway promises a password, not a username directory.
- **Entra ID / AAD wrapper.** Reachable via R2 AC5; a first-class wrapper needs a real deployment to validate against and would otherwise be untested code.
- **Per-key or per-keyspace authorization.** Not possible — Garnet ACL key patterns are all-or-nothing.
- **Encryption at rest.** The AOF and checkpoints stay unencrypted; that is a disk-encryption concern.
- **Credential storage, rotation, or a secret manager.** Highway accepts a password; where it comes from is the host application's business.
- **Dashboard authentication changes.** Feature 011's API key guards an HTTP port, not the RESP protocol. The interaction is documented, not merged.
- **Rate limiting, connection quotas, auditing.** Different problems.

## Cross-References

- `libs/garnet/libs/server/Auth/Settings/` — the authentication settings this feature wraps
- `libs/garnet/libs/server/TLS/GarnetTlsOptions.cs:15` — its own "not for production without review" warning
- https://microsoft.github.io/garnet/docs/security
- `docs/HIGHWAY-PROTOCOL.md` § Error Contract, § Stock Garnet Dependencies
- `docs/features/004.1-server-remediation/design.md` — the loopback-by-default precedent
