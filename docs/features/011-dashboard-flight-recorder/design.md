# Design: Dashboard — Flight Recorder View

## Overview

A separate assembly, `Highway.Server.Dashboard`, hosts a small ASP.NET Core minimal-API
application on its own port inside the broker process. It reads the flight recorder
**in-process** and serves a hand-written HTML/CSS/JS page from embedded resources.

Three decisions drive everything below:

1. **Separate package** — so `Highway.Server` keeps its current dependency graph.
2. **Disabled, loopback-bound, optionally key-protected** — so a diagnostic tool cannot quietly become a data leak.
3. **Server-Sent Events for live tailing** — which puts a push path next to an 80 ns hot path, and is therefore the part of this design that needs the most care.

## Why a separate package

`Highway.Server.csproj` today references `Highway.Abstractions`, `Garnet.host`,
`Garnet.server` and `Tsavorite.core`. Garnet itself pulls in no ASP.NET Core. Adding
`FrameworkReference Microsoft.AspNetCore.App` to `Highway.Server` would place the
ASP.NET Core shared framework behind **every** consumer — including console brokers,
test hosts, and anything that will never serve a page.

Splitting the packages makes "no configuration → no page" true at three levels:

| Level | Mechanism |
|---|---|
| Dependency | Do not reference the package; no dashboard code exists |
| Configuration | Reference it but never call `WithDashboard`; no component is created |
| Runtime | Call `WithDashboard` with `Enabled = false`; no listener opens |

## Why ASP.NET Core minimal APIs, not Razor Pages or `HttpListener`

`HttpListener` avoids the framework reference entirely, but then routing, MIME types,
range handling, HEAD, chunked responses, and SSE framing are all hand-rolled — a lot of
easy-to-get-subtly-wrong code to avoid a dependency in a package whose entire purpose is
to serve HTTP.

Razor Pages brings a view engine, a compilation model, and a project-shape convention for
what amounts to one page of markup. Minimal APIs give routing, DI, lifetime, logging and
graceful shutdown, and .NET 10's `TypedResults.ServerSentEvents` gives correct SSE framing
without writing it by hand. The markup is a static embedded file.

No SPA framework and no client build step (Requirement 6). The page is roughly 200 lines of
JavaScript using `fetch` and `EventSource`, both in-box in every browser this will meet.

## Project layout

```
src/Highway.Server.Dashboard/
├── Highway.Server.Dashboard.csproj    FrameworkReference Microsoft.AspNetCore.App
├── DashboardOptions.cs                options + Validate()
├── HighwayDashboardBuilderExtensions.cs   WithDashboard(...) on HighwayServerBuilder
├── DashboardComponent.cs              IHighwayServerComponent: owns the WebApplication
├── DashboardEndpoints.cs              route mapping
├── DashboardAuth.cs                   API-key middleware + session cookie
├── EventStream.cs                     SSE subscriber: bounded channel + drop counting
├── StreamRegistry.cs                  concurrency cap, lifetime, shutdown
├── Dto.cs                             JSON shapes (source-generated context)
└── wwwroot/                           EmbeddedResource: index.html, app.css, app.js
```

## The seam into Highway.Server

`WithDashboard` lives in another assembly but needs the `FlightRecorder`, which is
`internal` and stays that way (Requirement 1 AC6, Requirement 7 AC6). Two options were
considered:

- **Make a read-only recorder view public.** Grows `Highway.Server`'s public API for the benefit of one optional package, and permanently commits us to that shape.
- **Internal seam + `InternalsVisibleTo`.** No public API growth; the coupling is explicit and version-locked because the two packages ship together.

The second is chosen. The trade-off is real and worth naming: the dashboard is
tied to `Highway.Server`'s internals, so the two must version together. That is
acceptable for a first-party diagnostic package and is not acceptable as a general
extension mechanism — which is precisely why the seam is internal rather than public.

```csharp
// Highway.Server/Properties/AssemblyInfo.cs
[assembly: InternalsVisibleTo("Highway.Server.Dashboard")]
```

### The component contract

```csharp
namespace Highway.Server;

/// <summary>
/// An optional in-process component hosted alongside the broker — currently the
/// web dashboard. Internal: this is a first-party seam, not an extension point.
/// </summary>
internal interface IHighwayServerComponent : IDisposable
{
    string Name { get; }

    /// <summary>
    /// Starts the component. <b>Must not throw.</b> A component that cannot start
    /// logs and returns; the broker carries on without it.
    /// </summary>
    void Start();
}

internal sealed record HighwayComponentContext(
    HighwayServerOptions Options,
    FlightRecorder Recorder,
    ILoggerFactory LoggerFactory,
    string Endpoint);
```

`HighwayServerBuilder` gains an internal factory list; `Build()` passes it to
`HighwayServer`, which creates components after command registration, starts them in
`Start()`, and disposes them **first** in `Dispose()` — before the recorder, so no stream
can read a disposed recorder.

```csharp
public void Start()
{
    if (_started) return;
    _started = true;
    _garnet.Start();

    foreach (var component in _components)
    {
        try { component.Start(); }
        catch (Exception ex)
        {
            // A diagnostic component must never take down the broker.
            _logger.LogError(ex, "Component {Component} failed to start; the broker continues without it.", component.Name);
        }
    }

    _logger.LogInformation("Highway server ready on {Endpoint}", Endpoint);
}
```

The `try`/`catch` is belt-and-braces: `Start()` is contractually non-throwing, and this
guarantees Requirement 2 AC4 even if an implementation breaks that contract.

## Recorder additions

Two additions, both internal, both designed so an application without the dashboard pays
nothing.

### Enumerating names

```csharp
internal readonly record struct RecorderName(
    string Name, int Count, long Bytes, PayloadCapture Capture, long DroppedCapacity);

public IReadOnlyList<RecorderName> Names()
{
    var result = new List<RecorderName>(_buffers.Count);
    foreach (var (name, buffer) in _buffers)
        if (buffer is not null)
            result.Add(new RecorderName(name, buffer.Count, buffer.Bytes, buffer.Capture, buffer.DroppedCapacity));
    return result;
}
```

Enumerating a `ConcurrentDictionary` is a weakly-consistent snapshot, which is exactly
right here — the answer is a moment-in-time view of a moving target either way.

### Observing new events

```csharp
private volatile IRecorderObserver[] _observers = [];
private long _observerFailures;

internal interface IRecorderObserver
{
    /// <summary>
    /// Offers an event. <b>Must not block, throw, or do work proportional to the
    /// event.</b> Called on the recording path, between transaction commit and the
    /// client reply.
    /// </summary>
    void OnRecorded(in HighwayEvent evt);
}
```

Subscription is copy-on-write onto a `volatile` array, so `Record` never takes a lock and
never allocates an enumerator:

```csharp
// in Record(), after buffer.Append(...)
var observers = _observers;
if (observers.Length != 0)
    Notify(observers, evt);
```

With no dashboard attached this is one volatile read and a length check — Requirement 7 AC4.
`Notify` is a separate non-inlined method so the common path stays small, and it isolates
each observer:

```csharp
private void Notify(IRecorderObserver[] observers, in HighwayEvent evt)
{
    foreach (var observer in observers)
    {
        try { observer.OnRecorded(in evt); }
        catch (Exception) { Interlocked.Increment(ref _observerFailures); }
    }
}
```

Isolating per observer matters: the outer `try` in `Record` would let one bad observer
skip the rest. `ObserverFailures` joins the recorder snapshot and is shown in the UI.

## Server-Sent Events, and keeping them off the hot path

This is the part that can hurt the broker, so it is designed defensively.

**The rule:** the recording thread's entire involvement in streaming is *one bounded,
non-blocking enqueue per subscriber*. No serialization, no I/O, no lock, no allocation
beyond what already happened.

```
Finalize (Garnet session thread)
  └─ FlightRecorder.Record
       ├─ buffer.Append(evt)                    existing, ~80 ns
       └─ foreach subscriber: TryWrite(evt)     new: name check + non-blocking enqueue
                                                        │
                                     ─────────── channel boundary ───────────
                                                        │
SSE response task (Kestrel thread pool)                 ▼
  └─ await ReadAllAsync → project → JSON → write to response body
```

Everything expensive — filtering to the capture mode, JSON serialization, socket writes,
waiting on a stalled TCP window — happens on the *reader* side.

### Backpressure: drop, count, and tell the page

Each subscriber owns a bounded channel:

```csharp
_channel = Channel.CreateBounded<HighwayEvent>(new BoundedChannelOptions(capacity)
{
    FullMode = BoundedChannelFullMode.Wait,   // never used: we only ever TryWrite
    SingleReader = true,
    SingleWriter = false,
});
```

`FullMode.Wait` is deliberate and slightly counter-intuitive. The `DropOldest` /
`DropWrite` modes make `TryWrite` return `true` even when they discard something, so drops
become invisible. With `Wait`, `TryWrite` returns `false` when the channel is full and
**still does not block** — which gives both properties Requirement 5 needs: non-blocking
delivery, and a countable drop.

```csharp
public void OnRecorded(in HighwayEvent evt)
{
    if (!NameMatches(evt.Name)) return;        // cheap ordinal compare, before anything else
    if (!_channel.Writer.TryWrite(evt))
        Interlocked.Increment(ref _dropped);   // slow consumer: drop, never block
}
```

The reader emits a `dropped` frame whenever the counter moved, so a gap is visible instead
of silent (Requirement 5 AC4):

```
event: dropped
data: {"count":42}
```

`HighwayEvent` is an immutable record holding a reference to a payload array the recorder
already owns, so handing it across the channel copies a reference, not bytes.

### Connection lifecycle

- The stream endpoint registers a subscriber, returns `TypedResults.ServerSentEvents(...)` over an `IAsyncEnumerable<SseItem<EventDto>>` driven by the channel, and unregisters in a `finally`.
- `HttpContext.RequestAborted` ends the enumeration when the browser navigates away or the tab closes.
- A keep-alive comment frame (`: ping`) every 15 s keeps intermediaries from reaping an idle connection and surfaces a half-open socket as a write failure.
- `StreamRegistry` holds a `CancellationTokenSource` cancelled on dashboard disposal, so shutdown ends every stream rather than waiting on browsers (Requirement 5 AC6).
- `MaxConcurrentStreams` (default 4) is enforced on registration; over the limit the endpoint returns **503** with a body naming the limit rather than accepting a connection it will serve badly (Requirement 5 AC5).

Four is deliberately low: this is a diagnostic page for an operator or two, not a fan-out
channel, and every open stream is a per-event cost on the recording path.

### Capture modes and `ReplayEnabled` apply to the stream

Projection to the DTO is the single place both rules are enforced, shared by the query and
stream paths, so the stream cannot become a way around Requirement 3:

```csharp
static EventDto Project(in HighwayEvent evt, PayloadCapture capture, bool replayEnabled)
    => new(...) { Payload = replayEnabled && capture == PayloadCapture.Full && evt.Payload is not null
                    ? Convert.ToBase64String(evt.Payload) : null,
                  PayloadState = ... };  // "captured" | "headers-only" | "not-captured" | "disabled"
```

`PayloadState` exists so the UI can say *why* content is absent (Requirement 4 AC8):
"not captured" and "empty" must not look the same.

## Endpoints

All under the configured path prefix (default `/`).

| Method | Route | Purpose |
|---|---|---|
| GET | `/` | The page shell (embedded `index.html`) |
| GET | `/app.css`, `/app.js` | Embedded static assets |
| GET | `/api/recorder` | Server identity + recorder snapshot + name list |
| GET | `/api/events/{name}` | Query: `from`, `to`, `limit`, `node` |
| GET | `/api/stream/{name}` | SSE live tail |

`/api/events/{name}` accepts the same parameters as `HW.REPLAY`, reusing
`HwReplayCommand.TryParseTimestamp` so `-5min` means the same thing in both places. It
calls `FlightRecorder.Read` directly rather than issuing `HW.REPLAY` over a loopback
socket: same data, no socket, no RESP parsing, and no dependency on the broker's own
availability at the moment you most want to look at it.

`limit` is clamped to `ObservabilityOptions.ReplayMaxLimit` — the dashboard does not get a
larger budget than the protocol.

Responses are JSON via a `JsonSerializerContext` (source-generated), consistent with the
project's System.Text.Json-only rule and trim-friendly.

## Security

### Binding

`Bind` defaults to `IPAddress.Loopback`, matching `HighwayServerOptions.BindAddress`'s
precedent from 004.1. Kestrel is configured with exactly one endpoint from
`(Bind, Port)`; no `ASPNETCORE_URLS` or environment-based configuration is consulted, so
the bound address is the one in code and nothing else can widen it.

### API key

Optional. When `ApiKey` is set, a middleware ahead of every route accepts it from:

- the `X-Highway-Key` request header — for `curl` and scripts, and
- a `key` query parameter — because the browser `EventSource` API **cannot set request headers**, which is a genuine constraint of SSE rather than a shortcut.

On a successful presentation the response sets an `HttpOnly`, `SameSite=Strict`,
`Path`-scoped session cookie. The page therefore presents the key at most once and its
`EventSource` calls authenticate by cookie, keeping the key out of the query strings that
reverse proxies log. The query form remains available and is documented as the thing that
leaks.

Comparison is constant-time (`CryptographicOperations.FixedTimeEquals` over UTF-8 bytes).
Failure is `401` with a one-line body and no detail.

### The warning

Binding beyond loopback with no key is allowed — an operator may have a private network or
an authenticating proxy — but logs at **warning** level on startup, naming the address and
that payload content may be served. The UI carries a matching banner, so the exposure is
visible from both sides.

## The page

One HTML document, hash-routed, three views:

**Header** — broker RESP endpoint, dashboard URL, recorder enabled/disabled, and the
exposure banner when applicable. Requirement 6 AC7: an operator with four tabs open must be
able to tell which broker each one is.

**Overview** — recorder health as a small stat grid, then the name table (name, events,
bytes, capture mode, dropped). Drop counters and failures render in a warning colour when
non-zero and neutral grey when zero, so a healthy server is quiet and an unhealthy one is
not (Requirement 4 AC3).

**Name view** — filter bar (relative window buttons: 5 m / 15 m / 1 h / 6 h / all, plus
node and limit), a **Live** toggle, and the event table: time, type, node, request/message
id, size, status, error code. Failed rows carry a left border and their error code in the
error colour. Clicking a row expands a detail panel with full identifiers and the payload
— decoded as UTF-8 when it is valid UTF-8, hex otherwise, since Highway payloads are JSON
in practice but the recorder holds bytes.

Long values truncate with a middle ellipsis in the table and appear whole in the detail
panel (Requirement 6 AC6).

`<noscript>` explains that the dashboard needs JavaScript and that `HW.REPLAY` over RESP
is the scriptless equivalent (Requirement 6 AC8) — pointing at the real alternative rather
than just apologising.

Styling is one hand-written stylesheet, system font stack, dark and light via
`prefers-color-scheme`. No external requests of any kind, so the page works air-gapped.

## Configuration

```csharp
public sealed class DashboardOptions
{
    public bool Enabled { get; set; }                       // default: off
    public int Port { get; set; } = 7500;                   // matches product.md
    public IPAddress Bind { get; set; } = IPAddress.Loopback;
    public string PathBase { get; set; } = "";              // e.g. "/highway" behind a proxy
    public string? ApiKey { get; set; }
    public int MaxConcurrentStreams { get; set; } = 4;
    public int StreamBufferCapacity { get; set; } = 512;    // events buffered per stream
    public TimeSpan KeepAliveInterval { get; set; } = TimeSpan.FromSeconds(15);
}
```

`WithDashboard` has two overloads, matching the shape `product.md` already advertises:

```csharp
builder.WithDashboard(port: 7500);
builder.WithDashboard(d => { d.Port = 7500; d.ApiKey = key; });
```

Both set `Enabled = true` — calling `WithDashboard` *is* the opt-in, so there is no way to
configure a dashboard that silently does not run. Turning it off without deleting the call
is `WithDashboard(d => { d.Enabled = false; ... })`, which reads as deliberate.

`Validate()` follows `ObservabilityOptions.Validate`: port range, non-empty `ApiKey` if
present, positive stream limits, `PathBase` starting with `/` when non-empty. Called from
`Build()`, with messages naming the offending value.

`StreamBufferCapacity = 512` sits between "absorbs a burst" and "a stalled browser holds
half a megabyte of event references alive". Documented with that reasoning, because a
number without a rationale is a number nobody can safely change.

## Startup and failure

```
info: Highway dashboard listening on http://127.0.0.1:7500/ (API key required)
```

The URL is logged in pasteable form (Requirement 2 AC7). On failure:

```
error: Highway dashboard could not start on 127.0.0.1:7500 — address already in use.
       The broker is unaffected and continues to serve RESP on 127.0.0.1:6500.
```

Naming the port, the reason, and the broker's continued health, so the log line answers the
question it will actually raise.

## Testing

Integration tests use `HighwayTestServer` plus a real `HttpClient` against the real bound
port — no `TestServer`, because half of what is being tested (port binding, failure
isolation, SSE over a socket) is exactly what `TestServer` abstracts away.

| Test | Proves |
|---|---|
| `Disabled_OpensNoPort` | R2/R3: connect refused; nothing listening |
| `StartFailure_LeavesBrokerFunctional` | **R2 AC4** — occupy the port first, build the server, assert the dashboard failed *and* RPC still round-trips |
| `ApiKey_IsRequired_OnPagesApiAndStream` | R3 AC3, all three surfaces |
| `ApiKey_AcceptedViaHeaderOrQuery_ThenCookie` | the EventSource path works without leaking the key on every request |
| `HeadersOnly_YieldsNoContent` / `Off_YieldsNothing` | R3 AC5 |
| `ReplayDisabled_ServesHealthButNotContent` | R3 AC6 |
| `SlowConsumer_DoesNotDelayRecording` | **R5 AC3** — see below |
| `SlowConsumer_ReportsDrops` | R5 AC4: a `dropped` frame arrives |
| `StreamLimit_Refused_WithReason` | R5 AC5: 503 and a readable body |
| `Dispose_ReleasesPort_AndEndsStreams` | R2 AC3, R5 AC6 |
| `EventQuery_HonoursFilters` | R4 AC5 |
| Unit: `Names()`, observer add/remove, observer throw isolation | R7 |

`SlowConsumer_DoesNotDelayRecording` is the one that protects the product. It attaches an
observer that blocks for a second, fills the channel, then measures `Record` across many
calls and asserts the elapsed time stays in the same order of magnitude as the unobserved
path. It asserts a *bound*, not a benchmark figure — a timing assertion tight enough to be
precise would be flaky on CI, and one loose enough to be stable still catches the failure
mode that matters, which is "recording now waits on a browser".

## Risks

**A diagnostic tool that harms the thing it observes.** Mitigated structurally: the
recording path only ever does a non-blocking enqueue, streams are capped, buffers are
bounded, start failure is isolated, and a test asserts each of those.

**Payload exposure.** The dashboard shows exactly what `HW.REPLAY` shows and no more, with
a single shared projection enforcing it. Off by default, loopback by default, key
available, warning when exposed.

**Internal coupling to `Highway.Server`.** Accepted and stated above; the packages version
together.

**Scope creep into an operations console.** Requirement 3 AC8 makes every endpoint
read-only. The moment a "purge recorder" button exists, the dashboard can cause an
incident. Later steps add views, not verbs.

## Cross-references

- `docs/features/002-observability/design.md` — the recorder being read
- `docs/HIGHWAY-PROTOCOL.md` § Observability Commands — `HW.REPLAY` semantics mirrored here
- `docs/features/004.1-server-remediation/design.md` — bind-address precedent
- `docs/product/product.md` § Embedded Control Panel — the API shape this realises
