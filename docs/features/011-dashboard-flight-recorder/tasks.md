# Tasks: Dashboard — Flight Recorder View

Ordered so the risky parts land first: the seam, then the hot-path guarantee, then
hosting and security, and only then the UI. Each task names the requirements it
fulfils and what "done" means.

---

## Phase 1 — The seam in Highway.Server

### - [ ] T1 — Recorder name enumeration

Add `RecorderName` and `FlightRecorder.Names()` per the design.

*Requirements:* R7.1, R7.5, R7.6
**Done when:** `Names()` returns one entry per live buffer with count, bytes, capture
mode and capacity drops; disabled names are absent; unit tests cover empty, populated,
and disabled-name cases; all 002 recorder tests still pass unchanged.

### - [ ] T2 — Recorder observers

Add `IRecorderObserver`, copy-on-write `Subscribe`/`Unsubscribe`, the `volatile` array
read in `Record`, per-observer `try`/`catch`, and an `ObserverFailures` counter surfaced
in `RecorderSnapshot`.

*Requirements:* R7.2, R7.3, R7.4, R7.5
**Done when:** an observer sees recorded events; an observer that throws is counted and
**does not prevent later observers in the same array from being notified**; unsubscribe
stops delivery; with no observers `Record` does no extra allocation.

### - [ ] T3 — Prove the notification path costs nothing when unused

Extend the 002 write-path measurement to compare `Record` with zero observers against the
pre-change baseline.

*Requirements:* R7.4
**Done when:** the no-observer path is within noise of the 80 ns baseline, and the figure
is recorded in the design document. If it is not, T2's structure is wrong and is fixed
before continuing rather than accepted.

### - [ ] T4 — Component hosting seam

Add `IHighwayServerComponent`, `HighwayComponentContext`, the internal factory list on
`HighwayServerBuilder`, construction in `HighwayServer`, start-with-isolation in `Start()`,
and disposal **before** the recorder in `Dispose()`. Add
`InternalsVisibleTo("Highway.Server.Dashboard")`.

*Requirements:* R1.4, R1.6, R2.2, R2.3, R2.4
**Done when:** a test component starts and disposes with the server; a component whose
`Start` throws is logged and the broker still serves RPC; disposal order is asserted, not
assumed.

### - [ ] T5 — Prove the isolation gate fires

Deliberately make the test component throw during `Start` and confirm the broker test
still passes; then confirm removing the `try`/`catch` makes it fail.

*Requirements:* R2.4
**Done when:** both directions are observed. The guarantee that a dashboard cannot take
down the broker is asserted by a test that has been seen to fail without the protection.

---

## Phase 2 — The dashboard project

### - [ ] T6 — Project, packaging, and the disabled default

Create `src/Highway.Server.Dashboard` with `FrameworkReference Microsoft.AspNetCore.App`,
add it to `Highway.slnx` and `Directory.Packages.props` as needed, and add
`DashboardOptions` with `Validate()`.

*Requirements:* R1.1, R1.2, R1.3, R8.2, R8.3, R8.4
**Done when:** the solution builds with zero warnings; `Highway.Server.csproj` is
unchanged apart from `InternalsVisibleTo`; a test asserts `Highway.Server`'s reference set
has not grown, so the packaging promise is enforced rather than remembered.

### - [ ] T7 — `WithDashboard` and the component

Both overloads, plus `DashboardComponent` owning a `WebApplication` bound to exactly
`(Bind, Port)` with `PathBase` applied, starting Kestrel inside `Start()` without throwing
and logging a pasteable URL.

*Requirements:* R1.4, R1.5, R2.1, R2.6, R2.7, R8.1, R8.5
**Done when:** `WithDashboard(port)` serves a page on loopback; not calling it opens no
port; a start failure logs the port, the reason, and the broker's continued health.

### - [ ] T8 — Access control

The API-key middleware (header, query, then session cookie), constant-time comparison, 401
without detail, and the non-loopback-without-key startup warning.

*Requirements:* R3.1, R3.2, R3.3, R3.4, R3.7
**Done when:** all three surfaces — page, API, stream — are covered; a wrong key is
refused; the cookie removes the need to repeat the key; the warning fires only for a
non-loopback bind with no key.

---

## Phase 3 — Reading the recorder

### - [ ] T9 — DTOs and the shared projection

`Dto.cs`, the source-generated `JsonSerializerContext`, and the single `Project` method
enforcing capture mode and `ReplayEnabled`, including `PayloadState`.

*Requirements:* R3.5, R3.6, R4.8
**Done when:** `Full` yields content, `HeadersOnly` yields a size and no content, `Off`
yields no events, and `ReplayEnabled = false` yields no content — with the state
distinguishable in each case. Both the query and stream paths call this one method; a
second projection anywhere is a defect.

### - [ ] T10 — `GET /api/recorder`

Server identity, recorder snapshot including both drop counters, failures, observer
failures, and the name list.

*Requirements:* R4.1, R4.2, R3.6, R3.8
**Done when:** the payload matches `HW.STATS RECORDER` for the fields they share, and
still responds usefully when the recorder is disabled.

### - [ ] T11 — `GET /api/events/{name}`

Filters mapped to `FlightRecorder.Read`, reusing `HwReplayCommand.TryParseTimestamp`,
clamped to `ReplayMaxLimit`.

*Requirements:* R4.4, R4.5, R4.6, R4.10, R3.8
**Done when:** absolute and relative windows, node filter and limit all work; an unknown
name, an empty range and a disabled recorder each return a clear, non-error result; a
limit above the maximum is clamped rather than rejected.

---

## Phase 4 — Live tailing

### - [ ] T12 — Stream subscriber and backpressure

`EventStream` with the bounded `FullMode.Wait` channel written only via `TryWrite`, name
matching before enqueue, and a drop counter.

*Requirements:* R5.3, R5.4
**Done when:** a full channel drops and counts instead of blocking, and unit tests cover
both the fast and stalled consumer.

### - [ ] T13 — SSE endpoint and lifecycle

`GET /api/stream/{name}` via `TypedResults.ServerSentEvents`, keep-alive frames, `dropped`
frames, `RequestAborted` teardown, and `StreamRegistry` enforcing the concurrency cap and
cancelling everything on disposal.

*Requirements:* R5.1, R5.2, R5.5, R5.6, R5.7
**Done when:** a real `HttpClient` receives events as they are recorded; a browser
disconnect releases the subscriber; the cap returns 503 with a readable reason; disposal
ends live streams promptly.

### - [ ] T14 — Prove streaming cannot delay recording

Write `SlowConsumer_DoesNotDelayRecording` and confirm it fails if `TryWrite` is replaced
with a blocking `Write`.

*Requirements:* R5.3, R9.7
**Done when:** both directions are observed. This is the test that protects the broker
from this feature; it is not done until it has been seen to catch the failure it exists
for.

---

## Phase 5 — The page

### - [ ] T15 — Shell, styling, and static serving

`index.html`, `app.css`, `app.js` as embedded resources, served with correct content types
and `Cache-Control: no-store`; header with broker identity and exposure banner;
`<noscript>` fallback; dark and light.

*Requirements:* R6.1, R6.2, R6.3, R6.4, R6.5, R6.7, R6.8, R3.7
**Done when:** the page loads from the assembly alone; a search of the built assets finds
no `http://` or `https://` reference to any external host, asserted by a test rather than
checked by eye.

### - [ ] T16 — Overview view

Recorder health stat grid and the name table, with drops and failures visually distinct
when non-zero.

*Requirements:* R4.1, R4.2, R4.3

### - [ ] T17 — Name view

Filter bar with relative windows, event table with failure styling, row detail with full
identifiers and payload decoding (UTF-8 when valid, hex otherwise), truncation with a way
to see the whole value, and an empty state naming the filter that produced it.

*Requirements:* R4.4, R4.6, R4.7, R4.8, R4.9, R6.6

### - [ ] T18 — Live toggle

`EventSource` wiring, start/stop that genuinely closes the connection, a visible gap marker
on `dropped`, and full usability with live off.

*Requirements:* R5.1, R5.2, R5.8

---

## Phase 6 — Conformance

### - [ ] T19 — Full test pass

*Requirements:* R9 (all), R10.1, R10.2
**Done when:** all 518 existing tests plus the new ones pass, and `dotnet build` is
warning-free.

### - [ ] T20 — Documentation

Update `docs/product/product.md` — the control panel moves from "Not built" to partially
built, naming what this step delivers and what it does not. Confirm
`docs/HIGHWAY-PROTOCOL.md` needs no change and that `ProtocolConformanceTests` is green.
Add the dashboard to the roadmap.

*Requirements:* R10.3, R10.5

### - [ ] T21 — Samples

Enable the dashboard in `Highway.Samples.Broker` (loopback, no key, with a comment on why
that is safe there and what to change in production), document it in `samples/README.md`,
run all three sample processes, exercise the dashboard in a browser, and append a
`samples/RUNLOG.md` entry.

*Requirements:* R10.4, R10.6
**Done when:** the run log records what was verified and any defect found — fixed **in the
library with a regression test**, never worked around in the sample.

---

## Deferred to later steps

Server settings view and the catalog view (services and channels, OpenAPI/Swagger-style)
are out of scope here by design. This step carries the hosting, security and hot-path risk
so that those steps are largely presentation over data that already exists.
