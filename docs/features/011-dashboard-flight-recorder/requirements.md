# Feature: Dashboard — Flight Recorder View

## Introduction

Highway.Server gains an embedded web dashboard on its own port: a small set of pages showing what the broker is doing. This is the first step of a multi-step feature. **This step delivers the hosting foundation and the flight recorder view.** Server settings and the catalog view follow in later features.

The dashboard is **opt-in in the strongest possible sense**: it lives in a separate package, so an application that does not reference it contains no dashboard code at all, and an application that references it but configures nothing serves no page.

`docs/product/product.md` § "Embedded Control Panel (Web Dashboard)" describes this capability and is currently marked *Not built*, including a `WithDashboard(...)` builder method that has never existed. This feature makes that section real, and the API it delivers deliberately matches the shape the product document already promised where that shape is sound.

### Where this step stops

| Area | Step |
|---|---|
| Hosting, security, shell, **flight recorder view** | **011 — this feature** |
| Server settings view | later |
| Catalog view — services and channels, OpenAPI/Swagger-style | later |

Splitting this way puts the risky parts first: process hosting, the security posture, and live streaming off a hot path. The later views are mostly presentation over data that already exists.

## Glossary

- **Dashboard** — the embedded web UI served by `Highway.Server.Dashboard` on its own HTTP port.
- **Dashboard port** — the HTTP port, distinct from the RESP port the broker serves protocol traffic on.
- **Name** — a service or channel name; the unit the flight recorder buckets by.
- **Live tail** — a Server-Sent Events stream pushing newly recorded events to an open page.
- **Recording hot path** — `FlightRecorder.Record`, called from a command's `Finalize` between transaction commit and the client reply. Measured at 80 ns per append (feature 002).

## Requirements

### Requirement 1: A Separate, Opt-In Package

**User Story:** As someone hosting a Highway broker in a console app, I want the dashboard to cost me nothing unless I ask for it, so that adding a broker does not drag a web stack into my process.

**Why this matters.** `Highway.Server` today depends only on Garnet and `Highway.Abstractions`. ASP.NET Core appears nowhere in the graph. Adding a web host to `Highway.Server` itself would put the ASP.NET shared runtime behind every consumer, including the many that will never open a browser.

#### Acceptance Criteria

1. The dashboard ships as a separate project and package, `Highway.Server.Dashboard`
2. `Highway.Server` gains **no** new package or framework reference from this feature
3. An application that does not reference the dashboard package contains no dashboard code and no web-host dependency
4. The dashboard is enabled through an extension method on `HighwayServerBuilder` supplied by the dashboard package, so the call site reads as though it were built in
5. Referencing the package but configuring nothing serves **no page** and opens **no port** — "no configuration → no dashboard" holds at both the dependency level and the runtime level
6. Highway.Server exposes only what the dashboard genuinely needs; the flight recorder's public surface does not grow for the benefit of the UI

### Requirement 2: Hosting and Lifecycle

**User Story:** As an operator, I want the dashboard to start and stop with the broker on a port I choose, so that it behaves like part of the server rather than a separate thing to manage.

#### Acceptance Criteria

1. The dashboard listens on its own configurable HTTP port, entirely separate from the RESP port
2. It starts when the server starts and stops when the server stops, with no additional call from the application
3. Disposing the server releases the dashboard port; no listener or background task outlives it
4. A dashboard that fails to start — port in use, address unavailable — **does not prevent the broker from serving RESP traffic**. The failure is logged clearly and the broker carries on. A diagnostic UI must never take down the thing it exists to diagnose
5. The failure message names the port and the reason, so the cause is obvious without a debugger
6. The dashboard can be mounted under a path prefix, so it can sit behind a reverse proxy without rewriting links
7. Startup logs the dashboard URL in a form that can be pasted into a browser

### Requirement 3: Exposure and Access Control

**User Story:** As an operator, I want the dashboard closed by default and hard to expose accidentally, because it shows message payloads and Highway has no authentication.

**The risk, stated plainly.** The flight recorder retains payload content under the default `Full` capture mode. A dashboard serving that content is a data-exposure surface, on a system with no authentication anywhere.

#### Acceptance Criteria

1. The dashboard is **disabled by default**
2. When enabled without an explicit bind address it binds **loopback only**, consistent with the RESP port's secure-by-default stance from feature 004.1
3. An optional API key can be required; when set, every request — pages, API, and stream — must present it, and requests without it are refused
4. Binding beyond loopback **without** an API key is permitted but produces a prominent startup warning naming the risk, so the choice is deliberate rather than accidental
5. The dashboard **never shows more than `HW.REPLAY` would**: per-name capture modes are honoured, so a name recorded `HeadersOnly` shows sizes and no content, and a name recorded `Off` shows nothing
6. When `ReplayEnabled` is false the dashboard serves recorder health but **refuses event content**, matching the server's own switch rather than working around it
7. The exposure and both mitigations are documented where an operator will actually meet them: the configuration API's XML docs, the dashboard's own UI, and the samples
8. Every endpoint in this feature is **read-only**. No endpoint mutates server state, and none is added "for convenience"

### Requirement 4: The Flight Recorder View

**User Story:** As a developer debugging a live incident, I want to open a page and see what the broker has been doing, so that I can find the problem without composing RESP commands.

#### Acceptance Criteria

1. An overview lists every name the recorder is holding events for, with each name's event count, and links through to that name's events
2. Recorder health is displayed — enabled state, names, total events, approximate bytes, both drop counters, and recording failures — the same data `HW.STATS RECORDER` reports
3. Drop counters and a non-zero failure count are visually distinguishable from healthy zeros, because their whole purpose is to be noticed
4. Selecting a name shows its events newest-relevant-first, with timestamp, event type, node, identifiers, payload size, and error code
5. Events can be filtered by time window, node, and result count, mapping to the same parameters `HW.REPLAY` accepts
6. Relative time windows are offered as the common case (last 5 minutes, last hour), because that is what an operator reaches for during an incident
7. A **failed** event is visually distinct from a successful one and shows its error code, since failures are the reason to look
8. An event's payload can be inspected when it was captured, and its absence is explained — "not captured" reads differently from "empty"
9. An empty result says which filter produced it, rather than showing a blank table
10. A name with no recorded events, an unknown name, and a disabled recorder each produce a clear explanation rather than an error page

### Requirement 5: Live Tailing

**User Story:** As a developer watching a problem happen, I want new events to appear as they occur, so that I can correlate what I do with what the broker sees.

#### Acceptance Criteria

1. A name's event view can stream new events as they are recorded, using Server-Sent Events
2. Streaming can be started and stopped from the page, and stopping closes the connection rather than leaving it idle
3. **Streaming must not slow the recording hot path.** Delivery to a stream is a non-blocking hand-off; a slow, stalled, or disconnected browser cannot delay, block, or fail a command being recorded
4. When a consumer cannot keep up, events are dropped for that consumer rather than buffered without bound, and the page is told it missed events rather than silently showing a gap
5. The number of concurrent streams is bounded, and exceeding the bound is refused with a clear reason rather than degrading the server
6. A disconnected browser's resources are released promptly, and no stream survives dashboard shutdown
7. Streamed events honour the same capture-mode and `ReplayEnabled` rules as the query view — the stream is not a way around Requirement 3
8. The page remains usable with streaming off; live tailing is an enhancement to the view, not the only way to see data

### Requirement 6: The User Interface

**User Story:** As someone opening this on a server, I want a page that loads and works, with no build step for contributors and no reliance on the internet.

#### Acceptance Criteria

1. The UI is served from resources embedded in the dashboard assembly — no files to deploy alongside it
2. **No external assets.** No CDN scripts, stylesheets, fonts, or images: the dashboard must work on an air-gapped host, and a diagnostic tool that phones out is not one
3. **No client-side build step.** No npm, no bundler, no transpiler in the contributor workflow. Hand-written HTML, CSS and JavaScript
4. No SPA framework. The pages are small enough that one would add more to learn than it saves
5. The UI is readable at a normal terminal-adjacent window size and does not require horizontal scrolling to read an event row
6. Long payloads and long identifiers are truncated in list views with a way to see the whole value, so one large message cannot make the page unusable
7. The dashboard states what it is and which broker it is attached to, so an operator with several tabs open knows which is which
8. It degrades honestly without JavaScript: rather than appearing broken, it says what is required

### Requirement 7: What the Recorder Must Expose

**User Story:** As the dashboard, I need to enumerate names and observe new events, which the recorder cannot currently do.

#### Acceptance Criteria

1. The recorder can enumerate the names it is holding events for, with each name's event count
2. The recorder can notify an observer when an event is recorded, for live tailing
3. Notification is non-blocking by construction: an observer cannot block, slow, or fail the recording path, and an observer that throws is isolated and counted, never propagated (matching the existing "recording never fails an operation" guarantee)
4. With no observers attached, notification costs no more than a branch — an application without the dashboard pays nothing
5. These additions do not change the recorder's existing behaviour, and feature 002's tests continue to pass unchanged
6. The additions are internal to `Highway.Server` and visible to the dashboard assembly; the public API of `Highway.Server` does not grow

### Requirement 8: Configuration

**User Story:** As an operator, I want dashboard settings configured in the same style as everything else on the server.

#### Acceptance Criteria

1. Configuration is reached through the `HighwayServerBuilder` extension, consistent with the existing `With*` methods
2. Options cover at minimum: enabled, port, bind address, path prefix, API key, stream limits
3. Options are validated at build time with messages naming the offending value, consistent with `HighwayServerOptions.Validate` and `HighwayOptionsValidator`
4. Every option has a documented default, and every non-obvious default carries its rationale
5. Enabling the dashboard with nothing else set produces a working, loopback-only page — the common case needs one line

### Requirement 9: Testing

**User Story:** As a contributor, I want the dashboard's guarantees tested, especially the ones that protect the broker from it.

#### Acceptance Criteria

1. Integration tests exercise the dashboard over real HTTP against an embedded server, with no external infrastructure
2. A test proves the dashboard **disabled** opens no port and adds no cost
3. A test proves a **dashboard start failure leaves the broker fully functional** (Requirement 2 AC4) — the most important test in this feature
4. A test proves API-key enforcement covers pages, API, and stream, and that a missing or wrong key is refused
5. A test proves capture modes are honoured: `HeadersOnly` yields no content through the dashboard, `Off` yields nothing at all
6. A test proves `ReplayEnabled = false` refuses event content while still serving health
7. A test proves a **slow or stalled stream consumer cannot delay recording** (Requirement 5 AC3), since this is the guarantee that protects the hot path
8. A test proves stream drops are reported to the consumer rather than silently swallowed
9. A test proves disposal releases the port and leaves no background task running
10. Unit tests cover the recorder's new enumeration and notification, including observer isolation on throw

### Requirement 10: Living Conformance and No Regression

#### Acceptance Criteria

1. All 518 existing tests pass
2. `dotnet build` produces zero warnings
3. **No protocol change.** The dashboard reads the recorder in-process and adds no `HW.*` command, so `docs/HIGHWAY-PROTOCOL.md` needs no new command — but any statement it makes that this feature affects is updated, and `ProtocolConformanceTests` stays green
4. The samples are updated and re-run within this feature, and `samples/RUNLOG.md` gains an entry — the broker sample enables the dashboard so it is demonstrable, not merely described
5. `docs/product/product.md`'s status table is updated: the control panel moves from "Not built" to partially built, naming exactly what this step delivers and what it does not
6. Any defect the sample run exposes is fixed in the library with a regression test, never worked around in the sample

## Non-Goals

- **Server settings and catalog views.** Deliberately deferred to later steps of this multi-step feature; this step carries the hosting and security risk so those can be presentation.
- **Authentication beyond a shared API key.** No users, roles, sessions, or identity provider. Highway has no identity model, and inventing one here would be the wrong place.
- **Any mutation.** No purging the recorder, no editing settings, no replaying traffic. Read-only, so the dashboard can never be the cause of an incident.
- **A general metrics or charting system.** Counters and event lists, not time-series graphs.
- **HTTPS termination.** Put it behind a reverse proxy. Certificate management is not a broker's job.
- **Client-side build tooling.** Explicitly excluded by Requirement 6 rather than merely absent.
- **Historical retention beyond the recorder.** The dashboard shows what the recorder holds, and the recorder is volatile. It is a window, not a store.

## Cross-References

- Product intent: `docs/product/product.md` § "Embedded Control Panel (Web Dashboard)" — including the `WithDashboard(...)` shape this feature makes real
- The recorder this view reads: `docs/features/002-observability/design.md`
- Equivalent data over the wire: `docs/HIGHWAY-PROTOCOL.md` § "Observability Commands"
- Secure-by-default precedent for bind addresses: `docs/features/004.1-server-remediation/design.md`
- Living-conformance obligations: `.kiro/steering/spec-workflow.md`
