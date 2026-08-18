# Feature: Production Readiness Corrections

## Introduction

Highway is about to run in production: a durable queue for transactional email and
password-reset messages, on Windows, inside a trusted network. Verification of features 031
and 032 turned up a set of corrections that stand between the code as shipped and that
deployment. This feature is that set, and nothing else.

Three of the four are one subject — **how a client connects and proves what it may do** —
and the fourth is a storage knob that Highway never exposed:

1. **One connection path.** Feature 026 shipped a second copy of connection building. It
   ignores credentials, opens a duplicate multiplexer, connects eagerly during DI
   registration, and never registers the buffer cache interface it promised. Four defects,
   one root cause: two copies.
2. **TLS that tells the truth.** Highway's TLS surface works, but what it *means* is
   undocumented and easy to get wrong — a client certificate authenticates a **connection**,
   never a **user**, and two of Garnet's settings silently degrade validation to nothing.
3. **An ACL a developer can use.** Named users and per-command permissions already exist in
   Garnet. What is missing is the one thing only this project can supply: the list of
   commands a Highway client actually needs, shipped as a tested default.
4. **A reclaimable AOF.** `AofSegmentSize` — Garnet's on-disk segment granularity, default
   **1 GB** — is not exposed by Highway and has never been tested. Every C4.6 measurement so
   far fit inside segment zero, so the hypothesis "segments do not retire" was never in a
   position to be tested at all.

**Lane:** connective tissue. Three defects fixed, one guarantee made legible, one
configuration exposed. No new verb, no new command, no protocol surface.

### What this feature is not

- **Not a security feature.** Feature 012 built authentication and TLS. This makes the
  existing mechanisms usable and honest; it adds no mechanism.
- **Not a promise that C4.6 closes.** R4 runs an experiment and records what it measures.
  If disk is reclaimed, the constraint is corrected with evidence. If not, the finding is
  recorded so the next attempt starts from it rather than repeating it. Both outcomes are
  acceptable; a silent one is not.
- **Not the 033 chaos rig, and not v2's capture-and-replay.** Both are registered elsewhere.

## Decisions

| # | Decision | Resolution |
|---|---|---|
| **D1** | How the two connection paths converge | **One canonical `BuildConfiguration`** over a shared settings contract, used by the engine and by both cache entry points. The root cause is two copies; the fix must leave one |
| **D2** | Standalone cache credentials | `HighwayCacheOptions` gains the credential surface **through the same contract `HighwayOptions` implements**, not a second set of properties that can drift the same way |
| **D3** | When the cache connects | **Lazily, via a factory registration.** No network I/O inside `AddHighway`/`AddHighwayCache` — a worker service that starts alongside its broker must not fail on ordering |
| **D4** | What TLS is documented to provide | **Encryption, server identity, and an optional client-certificate gate — not authorization.** Certificates authenticate the connection; ACL and `AUTH` decide what that connection may do |
| **D5** | Weak TLS shapes | **Warned at startup, never refused.** Garnet degrades silently in two configurations; Highway says so out loud and leaves the operator's choice intact |
| **D6** | The shipped ACL default | **A `nopass` user restricted to the commands Highway needs.** Matches the stated deployment posture — trusted network, no credentials to distribute — while removing `FLUSHALL` and friends from network reach |
| **D7** | `aofSegmentSize` default | **`64m`.** Garnet rounds *down* to a power of two, so `100m` would silently become 64 MB; the default is stated as the value that actually takes effect, and 64 MB satisfies Garnet's rule that `AofPageSize` (32m) ≤ `AofSegmentSize` |
| **D8** | The C4.6 experiment's status | **A measurement, not a fix.** R4.3 unskips the existing test and records the result either way |

## Requirements

### Requirement 1: One Connection Path

**User Story:** As a developer registering Highway, I want every connection Highway opens to
be built the same way, so that credentials, TLS and connection tuning cannot apply to some
connections and not others.

#### Acceptance Criteria

1. A single canonical routine builds every `ConfigurationOptions` Highway uses. The
   credential, TLS and `ConfigureConnection` precedence currently living inside
   `HighwayConnection.ConnectAsync` is extracted intact — the parse-then-override-then-delegate
   order, and the documented answer to "which one wins", move with it rather than being
   re-derived
2. `AddHighway` opens the cache connection through that routine, so a client against a
   password- or TLS-protected broker registers successfully.
   `TlsTests.FullClientBehaviour_WorksOverTls` is green and is the regression proof
3. `HighwayCacheOptions` can express credentials at all: username, password, TLS and
   `ConfigureConnection`, through the **same contract** `HighwayOptions` satisfies (D2).
   Standalone cache mode against a secured broker currently has no way to authenticate,
   which is a gap in the surface, not only in its use
4. **One multiplexer per process, not two.** Feature 026 T5 promised the engine would share
   its connection with the cache; the property exists and nothing reads it. The handover is
   built, and a test proves object identity **through a real `AddHighway` container** — not
   by constructing the registration by hand
5. **`IBufferDistributedCache` is registered** by `AddHighway` *and* `AddHighwayCache`. It is
   registered by neither today, so `HybridCache` silently uses the allocating `byte[]` path
   that feature 026 T2 exists to avoid. The proof resolves it from a real container
6. **No network I/O during registration** (D3). Both entry points register a factory; the
   connection opens on first use. A test builds a container with no broker running and
   asserts registration succeeds
7. Every regression proof in this requirement goes through the public registration API. The
   existing buffer test builds its own service descriptors, which is exactly why a missing
   registration passed for a whole feature

### Requirement 2: TLS That Tells the Truth

**User Story:** As an operator deciding how to secure the broker, I want to know precisely
what TLS gives me and what it does not, so that I do not mistake a certificate for an
identity.

#### Acceptance Criteria

1. The UserGuide states plainly: TLS provides **encryption in transit, server identity, and
   optionally a client-certificate gate**. A client certificate authenticates the
   **connection**; it never becomes a user, a principal or an ACL identity. Authorization
   comes from `AUTH` — a password or an ACL user — or from nothing when authentication is
   off. Garnet has no certificate-based authentication mechanism, and the documentation says
   so rather than leaving it to be discovered
2. Two silent degradations are documented **and warned about at startup** (D5):
   - `ClientCertificateRequired = false` makes remote certificate validation
     unconditionally succeed — the callback returns `true` for every certificate
   - `ClientCertificateRequired = true` with no `IssuerCertificatePath` accepts chain
     errors without validating the issuer
3. Highway logs a warning at start for each weak shape, naming the option and its effect.
   It **refuses nothing** — the operator's configuration is theirs; the silence is what gets
   corrected
4. The client-side TLS surface is documented alongside the server's, including which
   settings must agree and what a mismatch looks like as an error
5. The UserGuide records that Garnet's issuer-validation routine carries its authors'
   caveat — *"prototype code … validate for your requirements before using in production"* —
   because a deployment reachable from an untrusted network needs that in its risk
   assessment, and no other document in this repository would carry it
6. `highway.json`'s TLS section and the guide agree with the code. An option that exists in
   one and not the others is a defect in this feature

### Requirement 3: An ACL a Developer Can Use

**User Story:** As a developer securing a broker on a trusted network, I want a working
access-control file I can start from, so that "restrict the commands" is a shipped default
rather than a research project.

#### Acceptance Criteria

1. The distribution ships `config/users.acl` containing a **`nopass` user restricted to the
   commands a Highway client actually needs** (D6). No credentials to distribute, no
   certificates to rotate — and the commands that can destroy a data directory are not
   reachable from the network. It works unedited against the shipped `highway.json`
2. The allowlist is **derived and enforced, not hand-maintained**: every `HW.*` command in
   `docs/HIGHWAY-PROTOCOL.md`'s Command Index, plus `PING`, `SUBSCRIBE`/`UNSUBSCRIBE` (the
   doorbells), the string commands the distributed cache issues, and `SCAN` (the broker's
   own startup check for pre-018 data). A test parses the protocol file and fails when a new
   command has no entry in the shipped file — the same discipline `ProtocolConformanceTests`
   applies to the command surface
3. A Highway client, a `HybridCache` consumer and the broker's own startup check all work
   against the shipped file with nothing added. Any command Highway needs that the file
   omits is a startup failure in a test, not a mystery in production
4. The UserGuide gains an access-control section covering the three postures in one place:
   **open on loopback** (development), **`nopass` + command allowlist** (trusted network —
   the documented default for this deployment), and **password or ACL users + TLS**
   (exposed). Each states what it protects against and what it does not
5. The relationship to R2 is stated once, explicitly: TLS and ACL are complementary, not
   alternatives. A perfect certificate with no `AUTH` is still the `default` user

### Requirement 4: A Reclaimable AOF — the Experiment

**User Story:** As an operator whose broker must run for months, I want the on-disk segment
size to be configurable, and I want to know whether configuring it makes the AOF reclaim
disk.

#### Acceptance Criteria

1. `server.aofSegmentSize` joins `HighwayServerOptions`, `highway.json` and the schema
   completeness test, mapping onto Garnet's `AofSegmentSize`. **Highway's default is `64m`**
2. The **power-of-two truth is documented and visible**: Garnet rounds the value *down* to
   the previous power of two, so `100m` takes effect as 64 MB. The effective value is
   reported by `highways --validate` alongside the configured one, and the guide says why
   the default is a power of two rather than a round decimal number
3. `DurableByDefaultTests.SustainedTraffic_DoesNotGrowTheLogWithoutBound` — kept and skipped
   since 016, carrying its measurement — is **un-skipped and run with a segment size small
   enough for whole segments to retire**. Every prior C4.6 measurement (102 MB, 205 MB, and
   032's 13.9 MB) fit entirely inside segment zero at the 1 GB default, so no segment could
   ever be deleted regardless of whether truncation worked
4. The result is recorded either way (D8):
   - **If disk is reclaimed**, C4.6's status is corrected in `constraints.md` with the new
     measurement, the discarded `AofPageSize` hypothesis is left in place as history, and the
     recommended production setting is documented
   - **If it is not**, the finding is added to C4.6 with what was tried and what was
     measured, so the next attempt starts from a third data point rather than a repetition.
     The test returns to skipped, carrying the new evidence
5. `constraints.md` is edited by **addendum, never by rewriting** the existing analysis. The
   record of what was believed and measured in 016 is the reason the entry has value

### Requirement 5: The Record

**User Story:** As a Highway maintainer, I want this batch held to the same standard as
every other feature, so that corrections do not arrive less documented than capabilities.

#### Acceptance Criteria

1. `docs/HIGHWAY-PROTOCOL.md` is **not modified** — no command, reply, key or error changes
2. The UserGuide gains the access-control and TLS material of R2.1–R2.5 and R3.4, in the
   house pattern, documented once
3. `constraints.md` reflects R4's outcome, and C6.5 ("the tested path is the secured path")
   is revisited now that R1 makes it true — it has been a claim rather than a fact since 026
4. 026's `tasks.md` "Found after shipping" entry is closed with what was actually wrong: not
   one defect but four, including two tasks marked complete whose claims were never built
5. All tests pass; `dotnet build --no-incremental` warning-free; the samples run and the
   RUNLOG records it, per Living Conformance — `HighwayCacheOptions` and `HighwayServerOptions`
   both gain public surface here

## Non-Goals

- **New authentication mechanisms.** No certificate-to-user mapping, no OIDC, no AAD wiring.
  Garnet offers what it offers; this feature makes it legible and usable.
- **Refusing weak configurations.** R2.3 warns. An operator who wants TLS without issuer
  validation on a trusted network gets it, loudly.
- **Fixing C4.6 by other means.** If R4's experiment fails, compaction as an operator
  procedure remains the answer, specced separately.
- **The 026 cache's feature surface.** Tag invalidation, stampede control and L1 layering are
  `HybridCache`'s, and they work; nothing here extends the cache.

## Cross-References

- `docs/features/026-distributed-cache/tasks.md` § Found after shipping — the entry R5.4
  closes, and the four defects R1 addresses
- `docs/features/012-introduce-security/` — the authentication and TLS mechanisms this
  feature documents and corrects without extending
- `docs/features/016-retention-and-durability/` — C4.6's origin, the skipped test R4.3
  revives, and the `AofPageSize` hypothesis already discarded
- `docs/features/031-server-distribution/` — the zip that ships `config/users.acl`, and the
  `highway.json` schema R4.1 extends
- `docs/product/constraints.md` — C4.6 (R4), C6.1–C6.5 (R2, R3); C6.5 in particular
- `docs/HIGHWAY-PROTOCOL.md` — the Command Index R3.2 parses to enforce the ACL allowlist
