# Feature 034 — Production Readiness Corrections: Design

## Architecture: one connection, one owner

Today two places build a connection and three places own one:

```
BEFORE
  HighwayConnection.ConnectAsync ──► ConfigurationOptions (credentials + TLS + delegate) ──► mux A
  AddHighway            ─────────────► ConnectionMultiplexer.Connect(rawString)          ──► mux B  ✗ no credentials
  AddHighwayCache       ─────────────► ConnectionMultiplexer.Connect(rawString)          ──► mux C  ✗ no credentials, no surface
  IHighwayEngineInternals.Multiplexer  ← exists, read by nobody
```

```
AFTER
  IHighwayConnectionSettings  (implemented by HighwayOptions and HighwayCacheOptions)
              │
              ▼
  HighwayConnectionConfiguration.Build(server, settings) ──► ConfigurationOptions   ← ONE definition
              │
              ▼
  HighwayConnectionSource (singleton, lazy)  ──► the process's ConnectionMultiplexer
              ├──► HighwayConnection  (engine)
              └──► HighwayCache       (both entry points)
```

One routine builds the configuration; one source owns the multiplexer. The engine no longer
creates its own — it asks. That is what makes 026's T5 promise true by construction rather
than by a handover that has to be remembered.

---

## Decisions

**D1 — Extract, do not re-derive.**
The credential/TLS/delegate block inside `HighwayConnection.ConnectAsync` is ~60 lines with a
documented precedence — parse the string, apply credentials over it, run the caller's
`ConfigureConnection` last — and a doc comment explaining "which one wins" because it is *"the
question a developer hits at 2am"*. That block moves **intact** into
`HighwayConnectionConfiguration.Build`. Rewriting it would produce a second subtly different
answer, which is the defect being fixed.

```csharp
public interface IHighwayConnectionSettings
{
    string? Server { get; }
    string? Username { get; }
    string? Password { get; }
    HighwayTlsOptions? Tls { get; }
    Action<ConfigurationOptions>? ConfigureConnection { get; }
}
```

`HighwayOptions` already has every member — it gains the interface and no new surface.
`HighwayCacheOptions` gains the four it lacks. Neither is a breaking change.

**D2 — One owner, not a handover.**
026's T5 was designed as "the engine gives its multiplexer to the cache at startup". That
shape has a resolution-order hazard — resolve the cache before the engine starts and it makes
its own — and it was never built. Instead a `HighwayConnectionSource` singleton lazily creates
**the** multiplexer on first request and hands the same instance to every consumer. Order
stops mattering. The engine's `HighwayConnection` takes its multiplexer from the source rather
than calling `Connect` itself; `IHighwayEngineInternals.Multiplexer` stays as a read-only view
for diagnostics.

*Cost, stated:* this touches the engine's connection creation, which is not a patch. It is
the only version of the fix that cannot drift back, because there is no second place left that
*can* create a connection.

**D3 — Register factories, connect on first use.**
`AddHighway` and `AddHighwayCache` become pure registration: no sockets, no DNS, no blocking.
`HighwayConnectionSource` connects the first time something asks for the multiplexer. This
fixes the ordering failure — a worker service that starts alongside its broker — and removes
blocking I/O from a method that looks like configuration. `AddHighwayCache` keeps its
fail-fast on a missing `Server`, because that is a configuration error knowable without a
network.

**D4 — What TLS is, in one table.**

| Property | Provided by TLS? | Provided by what, then |
|---|---|---|
| Messages encrypted in transit | **Yes** | — |
| Client verifies it is talking to *your* broker | **Yes** (server certificate) | — |
| Only holders of a CA-issued certificate can connect | **Yes**, with `ClientCertificateRequired` + `IssuerCertificatePath` | — |
| The broker knows *which* client this is | **No** | `AUTH` — password, or an ACL user |
| Per-command authorization | **No** | ACL file (R3) |

Evidence for the "No" rows: `libs/garnet/libs/server/Auth/` contains `NoAuthSettings`,
`PasswordAuthenticationSettings`, `AclAuthenticationPasswordSettings`, `AclAuthenticationAadSettings`,
`AadAuthenticationSettings` — and **nothing referencing `Certificate` or `X509`**. Garnet's
client-certificate callback
([`GarnetTlsOptions.ValidateClientCertificateCallback`](../../../libs/garnet/libs/server/TLS/GarnetTlsOptions.cs))
returns a bare `bool`: accept the connection or refuse it. It yields no principal and no
username. A certificate is a door lock; the ACL is what you may do inside.

**D5 — Warn, never refuse.**
Two configurations degrade silently in Garnet:

| Configuration | What actually happens |
|---|---|
| `clientCertificateRequired: false` | The validation callback is `(...) => true`. Every certificate — and no certificate — passes |
| `clientCertificateRequired: true`, no `issuerCertificatePath` | `RemoteCertificateChainErrors` is accepted; the chain is not validated against an issuer |

Highway logs one warning per shape at startup, naming the option and the consequence, beside
the existing `WithoutAuthentication()` warning. It refuses neither: on a trusted network both
are legitimate choices. Silence is the defect, not the configuration. Garnet's own comment on
its issuer check — *"prototype code … validate for your requirements before using in
production"* — is carried into the UserGuide, because no other document here would hold it.

**D6 — The shipped ACL: `nopass`, command-scoped.**
Garnet's ACL supports `nopass` users and per-command permission sets. For a broker on a
trusted network that is the shape that matches: nothing to distribute or rotate, and
`FLUSHALL`, `CONFIG` and `KEYS` are not reachable from the network — which today they are,
because Highway sets no command restrictions at all. A password profile is documented beside
it for anyone who wants one; the default is the one that costs an operator nothing to adopt.

**D7 — `aofSegmentSize` defaults to `64m` because that is what takes effect.**
`AofSegmentSizeBits()` calls `PreviousPowerOf2` and logs *"using lower AOF segment size than
specified"*. `100m` becomes 64 MB. Defaulting to a number that silently becomes a different
number teaches operators that the file is approximate. 64 MB is also ≥ `AofPageSize` (32m),
which Garnet requires.

**D8 — R4 is an experiment with two publishable outcomes.**
C4.6 says segments "do not retire in this configuration". That sentence is true and its
*configuration* was 1 GB segments with at most 205 MB of traffic — segment zero was never
obsolete, so retirement was never possible regardless of truncation. Whether smaller segments
retire is unknown until measured. The feature commits to measuring and recording, not to a
result.

---

## The Connection Path

```csharp
// Highway.Client.Engine
internal static class HighwayConnectionConfiguration
{
    // The block moved verbatim from HighwayConnection.ConnectAsync (D1):
    //   parse → apply username/password → apply TLS → run ConfigureConnection last.
    public static ConfigurationOptions Build(string configuration, IHighwayConnectionSettings? settings);
}

internal sealed class HighwayConnectionSource : IAsyncDisposable
{
    // Lazy<Task<IConnectionMultiplexer>> — created once, awaited by everyone.
    public ValueTask<IConnectionMultiplexer> GetAsync(CancellationToken ct);
}
```

Registration becomes:

```csharp
services.TryAddSingleton<HighwayConnectionSource>();
services.TryAddSingleton<HighwayCache>(sp => new HighwayCache(sp.GetRequiredService<HighwayConnectionSource>(), cacheOptions));
services.TryAddSingleton<IDistributedCache>(sp => sp.GetRequiredService<HighwayCache>());
services.TryAddSingleton<IBufferDistributedCache>(sp => sp.GetRequiredService<HighwayCache>());   // ← missing today
```

`HighwayCache` takes the source rather than a live multiplexer, so nothing connects until a
cache operation happens. Its existing `IConnectionMultiplexer` constructor stays for the unit
tests that pass a substitute.

---

## The ACL Allowlist

`config/users.acl`, shipped in the 031 zip:

```
user default on nopass ~* &* -@all +@highway
```

…expressed in whatever concrete form Garnet's parser accepts for a command set — the shape is
"one always-on user, no password, every key and channel, no commands except the ones Highway
needs".

The command set is **derived, never hand-maintained**:

| Source | Commands |
|---|---|
| `docs/HIGHWAY-PROTOCOL.md` Command Index | all 38 `HW.*` |
| Doorbells (RESP pub/sub) | `SUBSCRIBE`, `UNSUBSCRIBE` |
| Liveness | `PING` |
| Distributed cache (026) | the string commands `HighwayCache` issues, enumerated in one place in that class |
| Broker startup | `SCAN` — the pre-018 data check connects to itself over loopback |

`AclProfileTests` parses the protocol file's Command Index with the same parser
`ProtocolConformanceTests` uses, reads the shipped file, and fails when a command Highway
needs is absent. Adding a `HW.*` command without updating the shipped ACL breaks the build —
the discipline the protocol file already enjoys, extended to the file that decides whether
that command is reachable.

A second test runs a real client, a `HybridCache` consumer and a broker start against the
shipped file with nothing added. A missing command becomes a test failure rather than a
production mystery.

---

## The C4.6 Experiment

Protocol, so the result is interpretable whichever way it goes:

1. `server.aofSegmentSize` is exposed and mapped to `GarnetServerOptions.AofSegmentSize`.
2. `DurableByDefaultTests.SustainedTraffic_DoesNotGrowTheLogWithoutBound` is un-skipped and
   parameterised by segment size. Its existing shape is right: two identical waves of
   12,000 × 8 KB messages, asserting the second wave's growth is **less than** the first
   wave's total — sub-linear growth, not an absolute size.
3. Run at the smallest legal segment size (`32m`, equal to `AofPageSize`) and at the new
   default (`64m`). 24,000 × 8 KB ≈ 205 MB crosses six segments at 32 MB and three at 64 MB,
   so whole segments *can* fall below the begin address — which was never true at 1 GB.
4. Record, in the run and in `constraints.md`: number of `aof.log.*` files, total bytes after
   each wave, and whether file count ever decreases. **File count decreasing is the signal**;
   total bytes alone can be explained by checkpoint timing.
5. Whatever the outcome, `constraints.md` gains a dated addendum under C4.6. The 016 analysis
   and the discarded `AofPageSize` hypothesis stay exactly as written — they are the record of
   what was believed, and the reason the entry is worth anything.

---

## Error Handling

- **Configuration errors** (a cache with no server, an ACL file that does not exist) fail at
  registration or at `Build()` with the key named — unchanged from today's discipline.
- **Connection errors** now surface on first *use* rather than during `AddHighway` (D3). The
  message must therefore say what was being attempted, since the stack no longer points at a
  registration call.
- **Weak TLS** produces warnings, never failures (D5).
- **A missing ACL command** produces Garnet's `NOPERM`-class error at the client. The
  UserGuide names that error and points at `config/users.acl`, because it is the one failure
  this feature makes newly possible.

---

## Testing Strategy

| Layer | What | How |
|---|---|---|
| `BuildConfiguration` | Precedence: string < credentials < `ConfigureConnection`; TLS fields; username/password; nulls | Unit — pure function |
| Registration | `AddHighway` and `AddHighwayCache` resolve `IDistributedCache` **and** `IBufferDistributedCache`; one multiplexer by object identity; **no connection opened with no broker running** | Integration, **through the public API only** (R1.7) |
| Secured path | `TlsTests.FullClientBehaviour_WorksOverTls` green; the same shape with a password | Existing integration test, unskipped as the proof |
| TLS warnings | Each weak shape logs its warning and still starts | Unit against the builder |
| ACL | Protocol-derived allowlist covers every command; a client, `HybridCache` and broker startup all work against the shipped file | Unit + integration |
| AOF segments | The experiment (§ above) at 32m and 64m | Integration, recorded |

The registration tests carry this feature's real lesson. Feature 026's buffer test built its
own service descriptors and passed for an entire feature while the production registration
was missing the interface. Every proof here resolves from a container built by the public
extension method, because a test that constructs the wiring it is meant to verify verifies
nothing.
