# Feature 034 — Production Readiness Corrections: Tasks

Four subjects, ordered so the riskiest change lands first while the suite is quietest.
Phase 1 rebuilds the connection path (the only structural change here). Phase 2 makes TLS
honest. Phase 3 ships an ACL a developer can use. Phase 4 runs the AOF experiment. Phase 5
records all of it.

**Decisions D1–D8 are settled in `requirements.md`; there is no gating phase.**

**Already landed before this spec was written (2026-08-18), during 032's harness work.**
`AddHighway` now builds the cache connection through
`HighwayConnection.BuildConfigurationOptions(server, options)` instead of connecting from
the raw string, and `TlsTests.FullClientBehaviour_WorksOverTls` is **green** — the defect
026 recorded is closed on that path. What remains is everything else in R1: `AddHighwayCache`
still connects raw and `HighwayCacheOptions` still cannot express credentials at all;
`IBufferDistributedCache` is registered nowhere; the engine's multiplexer is still read by
nobody, so a process still opens two connections; and both entry points still connect eagerly
during registration. T1–T3 below are written against that starting point, not against the
original four-defect state.

## Phase 1 — one connection path

### - [x] T1 — `IHighwayConnectionSettings` and the canonical `BuildConfiguration`

*Requirements:* R1.1, R1.3
**Done when:** the credential/TLS/`ConfigureConnection` block moves **intact** out of
`HighwayConnection.ConnectAsync` into `HighwayConnectionConfiguration.Build`, carrying its
precedence and its "which one wins" doc comment (D1 — extract, do not re-derive);
`IHighwayConnectionSettings` is implemented by `HighwayOptions` (no new members) and by
`HighwayCacheOptions` (which gains `Username`, `Password`, `Tls`, `ConfigureConnection` — it
has none today, so standalone cache mode cannot authenticate at all); unit tests cover
precedence at each level, TLS field mapping, and null settings. `HighwayConnection` uses the
extracted routine and every existing test still passes.

### - [x] T2 — `HighwayConnectionSource`: one owner for the process's multiplexer

*Requirements:* R1.4, R1.6
*Depends on:* T1
**Done when:** a singleton lazily creates **the** multiplexer via `Build` and returns the
same instance to every consumer; `HighwayConnection` takes its multiplexer from the source
instead of calling `Connect` itself (D2 — this is the part that cannot drift back, because no
second place can create one); `IHighwayEngineInternals.Multiplexer` remains as a read-only
diagnostic view; disposal is owned by the source and happens once.

### - [x] T3 — Registration becomes registration: lazy, complete, credential-aware

*Requirements:* R1.2, R1.4–R1.7
*Depends on:* T2
**Done when:** `AddHighway` and `AddHighwayCache` open **no connection** — factories only
(D3), with `AddHighwayCache` keeping its fail-fast on a missing `Server` because that needs no
network; both register `IDistributedCache` **and `IBufferDistributedCache`** (neither
registers the latter today, so `HybridCache` silently uses the allocating `byte[]` path that
026 T2 exists to prevent); `HighwayCache` takes the source rather than a live multiplexer.

Proofs, **all through the public extension methods** (R1.7):
1. `TlsTests.FullClientBehaviour_WorksOverTls` is green — the recorded 026 defect closed.
2. The same shape against a password-protected broker.
3. `IBufferDistributedCache` resolves from a container built by `AddHighway`, and by
   `AddHighwayCache`.
4. One multiplexer, asserted by object identity, from a real `AddHighway` container.
5. A container builds successfully with **no broker running**.

026's buffer test built its own service descriptors and passed for a whole feature while the
production registration was missing the interface. No proof in this task may construct the
wiring it verifies.

## Phase 2 — TLS that tells the truth

### - [x] T4 — Startup warnings for the two silent degradations

*Requirements:* R2.2, R2.3
*Depends on:* T1
**Done when:** the builder logs one warning per weak shape, naming the option and its
effect — `clientCertificateRequired: false` (the validation callback returns `true` for every
certificate, and for none) and `clientCertificateRequired: true` with no
`issuerCertificatePath` (chain errors accepted, issuer unvalidated); it **refuses neither**
(D5); the warnings sit beside the existing `WithoutAuthentication()` warning; a unit test per
shape asserts the warning and that the server still starts.

### - [x] T5 — The TLS section of the UserGuide

*Requirements:* R2.1, R2.4–R2.6
*Depends on:* T4
**Done when:** the guide carries D4's table — encryption **yes**, server identity **yes**,
certificate gate **yes**, *which client this is* **no**, per-command authorization **no** —
and states in words that a client certificate authenticates the **connection**, never a user,
because Garnet has no certificate-based authentication mechanism; both weak shapes are
documented with what they actually do; the client-side TLS surface is documented beside the
server's with what a mismatch looks like; Garnet's own caveat on its issuer-validation
routine (*"prototype code … validate for your requirements before using in production"*) is
recorded, because no other document here would carry it; `highway.json`'s TLS section, the
code and the guide agree.

## Phase 3 — an ACL a developer can use

### - [x] T6 — The Highway-minimal command allowlist, derived and enforced

*Requirements:* R3.2
**Done when:** the allowlist is computed from `docs/HIGHWAY-PROTOCOL.md`'s Command Index —
reusing `ProtocolConformanceTests`' parser — plus `PING`, `SUBSCRIBE`/`UNSUBSCRIBE`, the
string commands `HighwayCache` issues (enumerated in one place in that class), and `SCAN` for
the broker's own pre-018 startup check; `AclProfileTests` fails when a command Highway needs
has no entry in the shipped file. Adding an `HW.*` command without updating the ACL breaks
the build.

### - [x] T7 — `config/users.acl`, shipped and proven

*Requirements:* R3.1, R3.3
*Depends on:* T6
**Done when:** the 031 distribution ships `config/users.acl` with a `nopass` user restricted
to that allowlist (D6), working unedited against the shipped `highway.json`; an integration
test starts a broker with it and runs a real client, a `HybridCache` consumer and the broker's
own startup check with nothing added; `FLUSHALL`, `CONFIG` and `KEYS` are proven **not**
reachable — they are reachable today, since Highway sets no command restrictions at all.

### - [x] T8 — The access-control section of the UserGuide

*Requirements:* R3.4, R3.5
*Depends on:* T7
**Done when:** three postures are documented in one place — open on loopback (development),
`nopass` + allowlist (trusted network; the documented default for an in-house deployment),
and password/ACL users + TLS (exposed) — each with what it protects against and what it does
not; the relationship to TLS is stated once and plainly: complementary, not alternatives, and
a perfect certificate with no `AUTH` is still the `default` user; Garnet's `NOPERM`-class
error is named, with the pointer to `config/users.acl`, since this feature is what makes that
error newly possible.

## Phase 4 — the AOF experiment

### - [x] T9 — Expose `server.aofSegmentSize`

*Requirements:* R4.1, R4.2
**Done when:** the option joins `HighwayServerOptions`, `highway.json`, the schema
completeness test and `BuildGarnetOptions`, mapping to Garnet's `AofSegmentSize`, defaulting
to **`64m`** (D7 — Garnet rounds *down* to a power of two, so `100m` would take effect as 64
MB; the default is stated as the value that actually applies, and 64 MB satisfies
`AofPageSize` ≤ `AofSegmentSize`); `highways --validate` prints the **effective** value beside
the configured one; the rounding is documented.

### - [x] T10 — Run the experiment and record what it measures

*Requirements:* R4.3–R4.5
*Depends on:* T9
**Done when:** `DurableByDefaultTests.SustainedTraffic_DoesNotGrowTheLogWithoutBound` is
un-skipped, parameterised by segment size, and run at `32m` and `64m` — sizes at which 24,000
× 8 KB (≈205 MB) crosses several whole segments, which was never true at the 1 GB default
where every prior measurement sat inside segment zero; the record captures **the number of
`aof.log.*` files** as well as total bytes, because a decreasing file count is the signal and
total bytes alone can be explained by checkpoint timing.

Then, per D8, whichever happened:
- **Disk reclaimed** → C4.6's status corrected in `constraints.md` with the new measurement,
  the recommended production setting documented, the test kept running.
- **Not reclaimed** → the attempt and its numbers added to C4.6 as a third data point, and
  the test returns to skipped carrying the new evidence.

Either way the 016 analysis and the discarded `AofPageSize` hypothesis stay **exactly as
written** (R4.5): they are the record of what was believed, and the reason the entry has value.

## Phase 5 — the record

### - [x] T11 — Close 026, revisit C6.5, run the samples

*Requirements:* R5.3–R5.5
*Depends on:* T3, T10
**Done when:** 026's "Found after shipping" entry is closed with what was actually wrong —
**four** defects, including two tasks marked complete whose claims were never built (T5's
connection sharing, T2/T3's buffer registration); `constraints.md`'s C6.5 ("the tested path is
the secured path") is revisited now that T3 makes it true, having been a claim rather than a
fact since 026; the samples run and `samples/RUNLOG.md` records it, since
`HighwayCacheOptions` and `HighwayServerOptions` both gain public surface here (Living
Conformance).

### - [x] T12 — Everything green

*Requirements:* R5.1, R5.2, R5.5
*Depends on:* all above
**Done when:** full suite green with **zero** known-red tests — the 026 TLS failure was the
only one, and this feature is what closes it; `dotnet build --no-incremental` warning-free;
`docs/HIGHWAY-PROTOCOL.md` byte-identical to before the feature (the check, not the promise);
the UserGuide's new sections reviewed against the code they describe.

---

**Order:** 1 (T1 → T2 → T3) → 2 (T4 → T5) ∥ 3 (T6 → T7 → T8) ∥ 4 (T9 → T10) → 5 (T11 → T12).

Phase 1 first and alone: it is the only structural change, and every other phase is easier to
review against a suite that is already green.

**Deferred (registered, not built):**

- **Certificate-to-user mapping.** Garnet has no mechanism; building one would mean an
  authentication provider, which is a feature and not a correction.
- **Refusing weak TLS configurations.** T4 warns. Promoting a warning to a refusal is a
  breaking change to deployments that are legitimate on a trusted network.
- **Compaction as an operator procedure** (`highways --compact`) — the answer if T10's
  experiment fails. Specced then, against the measurement rather than ahead of it.
