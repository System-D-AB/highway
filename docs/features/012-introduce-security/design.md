# Design: Introduce Security — Authentication and TLS

## Overview

Highway wraps two Garnet facilities it currently leaves null:

```csharp
// Garnet.server/Servers/GarnetServerOptions.cs
public IAuthenticationSettings AuthSettings = null;   // line 47
public IGarnetTlsOptions TlsOptions;                  // line 218
```

The server side is a mapping from Highway options onto those two fields inside
`HighwayServerBuilder.BuildGarnetOptions`. The client side is a mapping onto
`StackExchange.Redis.ConfigurationOptions`. Neither side invents a mechanism, and the RESP
wire format is untouched.

The design work is in three places where a naive mapping would be wrong: the default
posture, credential hygiene, and being honest about what TLS convenience wrappers are.

## How Garnet authentication actually works

Verified against the pinned submodule with a real client, not read off the documentation
page (`SimpleAuthSpike.cs`, 2026-08-07).

| Mechanism | Construction | File? | Usernames |
|---|---|---|---|
| Password | `new PasswordAuthenticationSettings(pwd)` | no | none — `AUTH pwd` |
| **ACL, no file** | `new AclAuthenticationPasswordSettings(null, pwd)` | **no** | one, named `default` |
| ACL + file | `new AclAuthenticationPasswordSettings(path, pwd)` | yes | many, with per-command rules |

Measured behaviour of the middle row — the one this feature uses:

```
password only            OK (whoami=default)
user 'default' + pwd     OK (whoami=default)
made-up user + pwd       REFUSED
wrong password           REFUSED
no credentials           REFUSED
```

A file is needed for exactly one thing: more than one named user. This feature does not do
that, so this feature has no file.

### Why ACL-with-default-password rather than password mode

Both are a single call with no file, and both refuse the wrong password. ACL mode
additionally rejects an unrecognised username, answers `ACL WHOAMI` for diagnostics, and is
the *same authenticator* that named users would later need — so adding them is additive
rather than a mode change. Password mode ignores the username entirely, which is a slightly
worse diagnostic story for identical effort.

The honest limitation, stated in the API docs: **without a file the only username is
`default`.** Highway therefore promises a *password*, not a username directory. `WithPassword`
is named for what it delivers.

## Decision 1: Free on loopback, mandatory off it

The rule is one line of policy:

```csharp
// The whole of Highway's default security posture.
bool authenticationRequired = !IPAddress.IsLoopback(opts.BindAddress);
```

| Bind | Auth configured | Result |
|---|---|---|
| Loopback | no | **Runs.** One informational line. |
| Loopback | yes | Runs authenticated. |
| Non-loopback | no | **`Build()` throws**, naming both remedies. |
| Non-loopback | no, plus `WithoutAuthentication()` | Runs. Warning naming the address and the exposure. |
| Non-loopback | yes | Runs authenticated. |

`new HighwayServerBuilder().Build()` therefore behaves exactly as it does today: it starts,
with no credentials and nothing to read first.

**Why not require it everywhere.** An earlier draft made `Build()` throw on loopback too, on
the theory that a uniform rule is simpler and the tested path should be the deployed path.
Both arguments are weaker than they look. The uniformity is bought by taxing the one
configuration where the risk does not exist. And the "tested path" concern is a *test suite*
problem, solved in the test suite: `HighwayTestServer` authenticates by default (T10), so
every integration test exercises `AUTH` regardless of the default posture for users.

The informational line on the free path is deliberately **not** a warning:

```
info: Highway server is running without authentication on 127.0.0.1:6500 — expected for
      local development. Binding to another address will require credentials.
```

Warning on a correct configuration trains people to filter the category, and then the real
warning is invisible too. It also says what will change when they bind elsewhere, so the
throw at that point is not a surprise.

```
InvalidOperationException:
  Highway server is bound to 0.0.0.0:6500, which requires authentication. Call
  WithPassword(password) to secure it, or WithoutAuthentication() to run open on a
  network you trust.
```

The credential remedy is named first, deliberately: the escape hatch is the shorter fix and
should not also be the more prominent one.

## Server API

```csharp
// The whole of the common case.
builder.WithPassword(secret);

// Explicit, loud, documented: an open broker on a network you trust.
builder.WithBindAddress(IPAddress.Any).WithoutAuthentication();

// Nothing at all — still valid on loopback, still the fastest way to try Highway.
new HighwayServerBuilder().Build();

// Escape hatch: ACL files, named users, Entra ID.
builder.WithAuthentication(new AclAuthenticationPasswordSettings("/etc/highway/users.acl"));
```

```csharp
// TLS: PFX file.
builder.WithTls("server.pfx", certPassword);

// TLS: certificate store subject, with mTLS and rotation.
builder.WithTls(tls =>
{
    tls.CertSubjectName = "CN=highway.internal";
    tls.CertificateRefreshFrequencySeconds = 3600;
    tls.ClientCertificateRequired = true;
    tls.IssuerCertificatePath = "/etc/highway/ca.pem";
    tls.CertificateRevocationCheckMode = X509RevocationMode.Online;
});

// Escape hatch, per GarnetTlsOptions' own warning.
builder.WithTls(myVerifiedTlsOptions);   // IGarnetTlsOptions
```

Mapped in `BuildGarnetOptions`:

```csharp
garnet.AuthSettings = opts.Authentication.CreateSettings();   // null after WithoutAuthentication()
garnet.TlsOptions   = opts.Tls?.CreateTlsOptions(logger);
```

`WithTls(...)` maps onto the `GarnetTlsOptions` constructor (`TLS/GarnetTlsOptions.cs:66`),
passing `enableCluster: false` and `clientTargetHost: null` — Highway does not use cluster
mode, and the client-side options that constructor would build are for cluster gossip, not
for Highway clients.

Garnet's own warning is quoted verbatim in the XML docs on `WithTls`, because Highway would
otherwise be recommending it by silence:

> NOTE: Do not use in production without verifying the implementation yourself. This class
> can be replaced with your own implementation when instantiating GarnetServerOptions.

### Validation at Build()

Following `ObservabilityOptions.Validate`, every failure names the offending value:

| Condition | Message names |
|---|---|
| Non-loopback bind, no auth, no opt-out | the bind address and both remedies |
| Empty or whitespace password | that a password was configured but is blank |
| Both `CertFileName` and `CertSubjectName` | that Garnet accepts exactly one |
| Certificate file missing or unreadable | the resolved absolute path |
| Certificate password wrong | that the file loaded but the password was rejected |
| Subject name matches no certificate | the subject and the store searched |

The certificate cases matter most: loading the certificate at `Build()` turns what would
otherwise be an opaque handshake failure minutes later into a startup error naming the file.

## Client design

```csharp
public sealed class HighwayOptions
{
    public string? Server { get; set; }
    public string? Username { get; set; }         // new — defaults to Garnet's `default`
    public string? Password { get; set; }         // new
    public HighwayTlsOptions? Tls { get; set; }   // new
    public Action<ConfigurationOptions>? ConfigureConnection { get; set; }  // new: escape hatch
}
```

**Precedence, defined rather than incidental:** the connection string parses first,
`Username`/`Password`/`Tls` overwrite what it set, and `ConfigureConnection` runs last so it
can override anything. Documented on each property, because "which one wins" is exactly the
question a developer hits at 2am.

`ConfigureConnection` is what makes client certificate selection and custom validation
possible without Highway modelling every StackExchange.Redis knob:

```csharp
options.Tls = new HighwayTlsOptions { Enabled = true, TargetHost = "highway.internal" };
options.ConfigureConnection = c =>
{
    c.CertificateSelection += (_, _, _, _, _) => clientCert;
    c.CertificateValidation += (_, cert, chain, errors) => ValidateAgainstPrivateCa(cert, chain, errors);
};
```

### Credential hygiene — the three defects

| Site | Today | Fix |
|---|---|---|
| `HighwayEngine.cs:150` | logs `_options.Server` at **Information** | log the redacted form |
| `HighwayConnection.cs:100` | `$"'{configuration}' is not a valid..."` | redacted |
| `HighwayConnection.cs:113` | `new HighwayServerUnreachableException(configuration, ex)` | redacted |

One shared helper, so a fourth site is hard to add by accident:

```csharp
/// <summary>
/// A connection string with credentials removed, safe to log or put in an exception.
/// <b>Every</b> place a connection string leaves the process goes through here.
/// </summary>
internal static string Redact(string configuration);
```

It strips `password=` / `user=` from the raw string, and is applied to
`ConfigurationOptions.ToString(includePassword: false)` where a parsed instance is at hand.
Redacting the raw string matters: the parse-failure path (`:100`) has no parsed instance,
and that is precisely the path most likely to hold a malformed — and still secret-bearing —
value.

The test asserts the property directly: put a known password in the connection string, point
it at a dead endpoint, and assert the string appears in neither the log output nor the
exception.

### Error mapping

`HighwayConnection.Classify` splits two ways today: the bare `ERR Transaction failed.` is
transient, everything else is a `HighwayTransportException`. Authentication errors would land
in the second bucket — correct on retry behaviour, useless on diagnosis.

```csharp
public static Exception Classify(RedisException ex) => ex switch
{
    RedisServerException { Message: TransientAbortMessage } => new HighwayTransientException(ex.Message),
    RedisServerException e when e.Message.StartsWith("NOAUTH",    Ordinal) => new HighwayAuthenticationException(...),
    RedisServerException e when e.Message.StartsWith("WRONGPASS", Ordinal) => new HighwayAuthenticationException(...),
    RedisServerException e when e.Message.StartsWith("NOPERM",    Ordinal) => new HighwayAuthorizationException(...),
    _ => new HighwayTransportException(ex.Message),
};
```

Both new types derive from the permanent branch, so **authentication failures are never
retried** — retrying a wrong password wastes the backoff budget and trips attempt counters on
systems that keep them.

`HighwayAuthorizationException` carries the refused command name, **attached at the call
site**. Spike T1 established that Garnet's reply does not contain it:

```
NOPERM this user has no permissions to run the command
```

An earlier draft parsed the command out of the message. It would have been null every time.

StackExchange.Redis re-issues `AUTH` on reconnect from `ConfigurationOptions`, so
Requirement 3 AC9 needs no Highway machinery — but it does need a test, because it is an
assumption about someone else's library.

## Testing

`HighwayTestServer` generates a random credential per instance and authenticates by default,
so the whole integration suite runs the authenticated path. This is the single
highest-value change in the feature: it turns "authentication works" from one test into a
property of every test, and it is what makes Decision 1's loopback exemption defensible.

| Test | Proves |
|---|---|
| `BareBuild_OnLoopback_RunsWithNoSecurity` | **R1.1** — the evaluation path; must never regress |
| `NonLoopbackBind_WithoutAuth_Throws_NamingBothRemedies` | R1.4, R5.6 |
| `LoopbackNoAuth_LogsInformation_NotWarning` | R1.3 — asserts the level, since the level *is* the design |
| `WithoutAuthentication_OffLoopback_Works_AndWarns` | R1.5, R5.7 |
| `Password_RefusesWrongAndMissingCredentials` | R2.2 |
| `UnauthenticatedClient_IsRefused_Legibly` | R3.6, R5.5 |
| `WrongPassword_IsPermanent_AndNotRetried` | R3.7 — asserts the attempt count, not just the exception |
| `Credentials_NeverAppearInLogsOrExceptions` | R3.4, R3.5 |
| `Tls_RoundTrips_WithSelfSignedCertificate` | R4, generated in-test — no fixture files |
| `MutualTls_RoundTrips` | R4.3 |
| `TlsMismatch_ProducesADiagnosableError` | R4.7 |
| `MissingCertificate_FailsAtBuild_NamingThePath` | R4.6 |
| `Reconnect_ReauthenticatesAutomatically` | R3.9 — an assumption about StackExchange.Redis, therefore tested |

Certificates are generated in-process with `CertificateRequest`/`X509Certificate2`, keeping
Requirement 5 AC2 — no external infrastructure, no fixture files, no manual setup.

## Protocol impact

No new `HW.*` command, but `docs/HIGHWAY-PROTOCOL.md` changes because client-visible
behaviour does:

1. **§ Stock Garnet Dependencies** gains `AUTH`. A client that cannot authenticate cannot function against a secured server, which is what that section exists to list.
2. **§ Error Contract** gains a third class. Today it is binary: `ERR HW_`-prefixed is permanent, bare `ERR Transaction failed.` is transient. `NOAUTH` / `WRONGPASS` / `NOPERM` are permanent and carry *neither* marker, so an implementer following the current rule literally has nowhere to put them.
3. A section covers authentication and TLS for client implementers, including the `@dangerous` trap below.

`ProtocolConformanceTests` parses the Command Index, which does not change.

## Findings from the spikes

The spikes were run while roles were still in scope. Roles were then descoped
(`requirements.md` § Deferred: Roles), but the findings are what make that decision
informed, and two of them affect this feature directly.

### Affects this feature

**Highway's commands are in Garnet's `@dangerous` category, not `@admin`.**

| Rules | `HW.CALL` |
|---|---|
| `+@all -@admin` | works |
| `+@all -@dangerous` | **NOPERM** |
| `+@connection +@custom` | works |

`+@all -@dangerous` is a common hardening idiom. On a Highway server it connects fine and
then refuses every `HW.*` command — a failure that looks exactly like a Highway bug and is
not. Documented in the protocol file (R6 AC2). Highway's own configuration never produces
this, but an operator using the `IAuthenticationSettings` escape hatch can walk into it.

**`NOPERM` does not name the refused command.** Drives the client-side attachment above.

### Recorded for the later roles feature

- **Per-name custom-command ACL works, in both directions.** `+@all -hw.replay` lets `HW.CALL` through and refuses `HW.REPLAY`; `+@all -hw.call` does the reverse. Roles are viable.
- **`+@connection` is mandatory in any role.** An allow-list of `+hw.call +get +del +subscribe +ping` cannot even connect — `RedisConnectionException`. StackExchange.Redis's handshake needs more than `+ping`, so "the commands the participant issues" must be read as including the ones the client library issues on its behalf.
- **`AclStrictCustomCommands` must be disabled for any ACL rule naming an `hw.*` command.** Garnet validates those names inside `GarnetServer`'s constructor (`GarnetServer.cs:309`) against module-registered commands; Highway registers after construction, because registration must precede `Start()` for AOF replay. Strict mode — the default — therefore refuses to start:

  ```
  GarnetException: ACL strict mode: 3 unresolved (user, custom-command) entries in ACL
  rules: (caller,HW.CALL), (caller,HW.HEARTBEAT), (observer,HW.REPLAY).
  ```

  The orderings are incompatible. A roles feature must disable strict mode and replace the lost check with its own, validating `hw.*` names against `HighwayServer.CommandTable` at `Build()`.
- **A `nopass` default user is a total authentication bypass.** With `user default on nopass +@all` in an ACL file, a connection presenting a nonexistent username and a wrong password is *silently authenticated as `default`*:

  ```
  garbage credentials -> ACCEPTED as 'default'; HW.CALL -> ok
  ```

  This is `nopass` behaving as defined, not a defect — but it is a trap directly across the path of anything that generates an ACL file, since `user default on nopass +@all` is the natural first line to write. Any future generator must never emit `nopass`, and must treat a supplied file with one as *no authentication* for Decision 1's purposes.
- **`ACL SETUSER` does not persist.** Runtime-created users work immediately and are gone after a restart: absent from `ACL LIST`, absent from the ACL file, absent from the AOF. The AOF leak that was feared does not occur — and is moot, because runtime creation is not a viable route.
- **Key patterns are all-or-nothing.** `ACLParser.cs:257` treats `~*` as a no-op and parses nothing else, with a comment explaining a `GET` fast path that depends on it. Roles could therefore restrict *commands* but never *data* — any role that can read a reply slot can `GET` every key in the store.

## Risks

**Someone binds to `0.0.0.0`, hits the throw, and reaches for `WithoutAuthentication()`
because it is the shorter fix.** The realistic failure mode of Decision 1. Mitigated by
naming the credential remedy first, by the warning on every start thereafter, and by the
samples showing the authenticated configuration as a flag rather than an edit. Not fully
solvable: an escape hatch that is hard to use is one people work around in worse ways.

**Nothing forces existing users to adopt authentication.** A loopback broker stays open
forever and this feature never nags. Accepted cost of Requirement 1; the line is drawn where
exposure actually changes.

**`GarnetTlsOptions` is sample code by its author's own statement.** Highway wraps it for
convenience and quotes the warning verbatim in the XML docs on `WithTls`, alongside the
`IGarnetTlsOptions` escape hatch.

**The password crosses the wire in clear text without TLS.** Inherent to RESP `AUTH`.
Documented at the point of configuration, not buried.

## Cross-references

- `libs/garnet/libs/server/Auth/Settings/` — `IAuthenticationSettings` and the concrete modes
- `libs/garnet/libs/server/TLS/GarnetTlsOptions.cs:15` — the production warning
- `docs/HIGHWAY-PROTOCOL.md` § Error Contract, § Stock Garnet Dependencies
- `docs/features/011-dashboard-flight-recorder/design.md` § Security — the separate HTTP API key
