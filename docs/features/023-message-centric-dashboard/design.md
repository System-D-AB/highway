# Design: Messages, Not Protocol Events — with Safe Remediation

## Overview

Feature 023 keeps its original architectural decision: **the server aggregates and the browser renders**. It adds a deliberately narrow operational interface for correcting and replaying eligible queue dead letters without turning the dashboard into a general broker administration surface.

The page hierarchy is:

1. **Needs attention** — dead-lettered messages, retrying failures, then suspected stuck work.
2. **Recent messages** — one row per correlated message.
3. **Protocol events** — raw mechanics, still available but never the default.

The remediation design is evidence-preserving and conservative. An original dead letter is immutable. A correction creates a new queue attempt with a new message ID and lineage; it never changes the original bytes. Browser actions are limited to `Q` targets, including an exact derived subscriber-group queue. RPC/service dead letters remain read-only because no caller is waiting and replay can repeat side effects.

### Decisions retained and added

| Decision | Chosen |
|---|---|
| Correlation | Server-side by message ID scoped by entity; trace context remains optional enrichment |
| Aggregation | Server computes projections, outcomes and counts; browser only renders |
| Retention | Recorder window is stated and not extended |
| Entity page | Extends Feature 022 rather than creating a competing page |
| Event visibility | `Public` or `Internal` is a server-owned event-type property |
| Front end | No-build ES modules and Feature 022's one-active-view scheduler |
| Broker state | `IBrokerState` remains read-only by construction |
| Remediation seam | A separate narrow `IRemediationActions` boundary |
| Eligible actions | Corrected replay for `Q` targets only; exact derived group queues are addressed as queues |
| Evidence | Original raw dead-letter bytes move unchanged to bounded resolved evidence |
| Concurrency | Exact-item fingerprint + expected hash + client `actionId`; one atomic winner |
| Security | `WriteActionsEnabled=false`; named action credentials are separate from read `ApiKey` |
| Success language | Enqueue acceptance is `ReplayAccepted`, never business success |

### Verified findings that shape this design

The following findings were checked against the current implementation and authoritative project documents:

- `HW.DLQ PEEK` returns the durable original payload plus available failure fields. Current `REQUEUE` pops by head/count, is destructive, resets attempts, reuses the original ID, reconstructs an entry without the failure trailer, and records no remediation audit. It is not a safe primitive to wrap for browser editing.
- A dead letter contains the entire original queue entry plus bounded failure context. `FailureReporter` records exception type and bounded System.Text.Json detail (`message`, `node`, UTC `at`, optional `stack`, optional inner exception type). Message and stack can carry the explicit truncation marker; capture mode can withhold detail; a worker can fail to report it. The UI must never imply completeness that is not present.
- `IBrokerState` exposes reads only. This remains a compile-time boundary; action methods do not get added to it.
- The dashboard currently has one read API key/session cookie and no authenticated actor identity suitable for audit. A source IP does not establish identity.
- The current server-owned loopback broker connection is unavailable when client certificates are required. Remediation therefore reports itself disabled under mTLS unless a supported authenticated command transport is designed and implemented; this design does not claim an in-process mutation API exists.
- Highway is at-least-once. A replay can repeat side effects even when accepted exactly once by the remediation command.

Authoritative references: [`HW.DLQ` in the protocol](../../HIGHWAY-PROTOCOL.md#dead-letter-commands), [Feature 015 failure context](../015-recoverability/design.md), [Feature 020 read boundary](../020-dashboard-operations/design.md), [Feature 022 ES-module and scheduler decisions](../022-dashboard-catalogue/design.md), and [constraints C1.4, C4.4, C5, C6 and C7](../../product/constraints.md).

## Architecture

### User-facing information architecture

```text
ENTITY / NODE / MESSAGE VIEW
+------------------------------------------------------------------+
| NEEDS ATTENTION                                                   |
|  1. Dead-lettered       [inspect] [correct & replay, if eligible] |
|  2. Retrying failures   [inspect only]                            |
|  3. Suspected stuck     [diagnostic only; incomplete evidence?]   |
+------------------------------------------------------------------+
| RECENT MESSAGES                                                   |
|  one row per correlated message; started node -> completed node   |
+------------------------------------------------------------------+
| PROTOCOL EVENTS (diagnostics)                                     |
|  raw events, existing filters and SSE                              |
+------------------------------------------------------------------+
```

Failure rendering shows every field that is actually available. Exception stack is collapsed by default. Detail states are explicit:

- **available** — render the recorded fields;
- **truncated** — preserve and display the truncation marker for message or stack;
- **withheld by capture mode** — explain which capture policy withheld detail;
- **not reported** — explain that no failure report reached the broker;
- **incomplete recorder evidence** — identify facts inferred from only part of the retained event window.

### Data flow

```text
 clients / workers
       |
       | HW.* traffic and failure reports
       v
+----------------------- Highway.Server ----------------------------+
| durable queues/DLQs       FlightRecorder        node registry      |
|         |                      |                      |             |
|         +----------+-----------+----------------------+             |
|                    v                                                |
|        MessageProjection + AttentionProjection                      |
|                    |                                                |
|                    +---- read DTOs ----> dashboard HTTP GET         |
|                                                                     |
| exact DLQ item ----> atomic remediation command <---- IRemediationActions
|                         |                                           |
|                         +--> corrected Q entry (new ID)              |
|                         +--> bounded resolved evidence + audit       |
+---------------------------------------------------------------------+
                                  |
                                  v
                 no-build ES-module browser renderer
```

The two paths are intentionally separate. `IBrokerState` and projection services remain read-only. `IRemediationActions` exposes only the eligible correction operation and availability reason; it cannot purge, retire, change arbitrary targets, or touch in-flight work.

### Trust boundary

```text
UNTRUSTED BROWSER                         TRUSTED SERVER/BROKER
+---------------------------+             +-----------------------------+
| read session / ApiKey     | -- GET --> | read authorization          |
| named action credential   | -- POST -> | separate action auth        |
| CSRF custom token/header  |             | actor resolved from config  |
| copied JSON body          |             | syntax/envelope/size checks |
| confirmation + diff       |             | rate limit + audit          |
+---------------------------+             | exact atomic command        |
                                          +-----------------------------+

Never trusted as identity: free-text actor name, request body actor field, source IP.
Supplemental only: source IP. Never stored: read key, action secret, CSRF token.
```

Read access never implies write access. `ApiKey` alone cannot authorize any action. `WriteActionsEnabled` defaults to `false`; when enabled, a POST must authenticate against a named action credential configured separately from `ApiKey`. The server derives the audit actor from the matched credential record. Comparison is constant-time. Action secrets are accepted only in a header, never a URL, query string, log, audit record, cookie, or payload.

A same-origin anti-CSRF token is issued to the authenticated page and must be returned in a custom header on action POSTs. The action credential and CSRF token are separate checks. Action endpoints are rate-limited. The browser keeps an entered action secret in memory only for the action flow.

### Supported command path and mTLS

The action boundary calls a supported server command transport; it does not reach into Garnet state or add mutation to `IBrokerState`. The initial implementation may use the server-owned command connection only where `LoopbackConnection` supports the server security mode. With client-certificate-required mTLS, that path is unavailable today. The action capability must then be disabled with a reason such as `Remediation unavailable: the configured broker requires a client certificate and no authenticated dashboard command path is configured`.

No button is rendered as enabled merely because `WriteActionsEnabled` is true. Capability requires all of: explicit opt-in, valid action credentials configured, CSRF support, rate limiting, and a supported authenticated broker command path.

## Components and Interfaces

### Message projection and attention projection

`MessageProjection` retains Feature 023's correlation rules:

```text
RPC       requestId, scoped by service; reply joins from hw.replies
Queue     requestId/messageId, scoped by exact queue
Pub/Sub   published numeric ID normalized to group queue string ID
```

A published message timeline still aggregates every subscriber group for diagnosis. Remediation does not: replaying `orders.placed@billing` targets that exact derived queue and must not call publish or fan out to any other group.

`AttentionProjection` orders actionable information without conflating evidence:

1. **Dead-lettered** — durable DLQ evidence; corrected replay may be offered only for eligible `Q` targets and only if body capture permits editing.
2. **Retrying failure** — a recorded `DeliveryFailed` followed by evidence that attempts remain and the message is queued or claimed again. Inspect only.
3. **Suspected stuck** — a claim/in-flight state whose age exceeds the applicable expected lease/processing evidence without a terminal event. Inspect only.

Suspected stuck is diagnostic, not a declaration that a handler is hung. The projection uses recorder claim/renewal/terminal events together with current state such as in-flight count and oldest-claim age. If the claim began before the recorder window, state is unavailable, or renewal evidence aged out, it marks the suspicion as based on incomplete evidence. While any claim may still be executing, the dashboard offers no edit, replay, or force-requeue action. This prevents an operator from creating a concurrent duplicate side effect.

### Read-only and action boundaries

```csharp
internal interface IBrokerState
{
    // Existing read operations only. No action methods are added.
}

internal interface IRemediationActions
{
    RemediationAvailability Availability { get; }

    Task<RemediationResult> ReplayCorrectedQueueDeadLetterAsync(
        CorrectedReplayRequest request,
        AuthenticatedActionActor actor,
        string? sourceIp,
        CancellationToken cancellationToken = default);
}
```

These signatures are design shapes, not a second protocol definition. The wire command, arguments, replies, errors, keys and entry framings belong only in [`docs/HIGHWAY-PROTOCOL.md`](../../HIGHWAY-PROTOCOL.md).

### Dashboard action API

The browser action is a POST-only endpoint. Its body contains target, original fingerprint/ETag, expected hash, client-generated `actionId`, proposed corrected ID, and corrected payload bytes/JSON. It contains no actor name or credential. The response distinguishes:

- `ReplayAccepted` — atomic mutation committed; corrected copy is queued;
- `IdempotentReplay` — this `actionId` already committed; return the original result;
- `Conflict` — another action already resolved that original fingerprint;
- `ValidationRejected` — JSON/envelope/size/target validation failed;
- `Unavailable` — write feature or authenticated command path is disabled;
- `CapacityRejected` — bounded evidence/audit storage cannot accept the record;
- `Unauthorized` / `RateLimited` — HTTP security result before broker mutation.

Acceptance means only that a corrected queue entry was durably accepted. It is never labelled `processed`, `fixed`, or business success.

### Exact-item atomic command delta

A new exact-item remediation command is required; the dashboard must not wrap current count/head-based `HW.DLQ REQUEUE`. The command's request carries:

- exact target kind/name (`Q` only for this browser flow);
- stable fingerprint/ETag derived from the complete raw active dead-letter bytes;
- expected original cryptographic hash for optimistic concurrency;
- client-generated `actionId` idempotency key;
- a new corrected message ID;
- corrected payload bytes and their hash;
- authenticated actor and supplemental source IP passed from the trusted HTTP boundary, not accepted from browser free text.

The protocol document will define the final command grammar, RESP framing, replies, errors, keys and archive framing. This spec defines only the required semantics. Because this changes `HW.*` behavior and adds replies/errors/keys/framings, the same feature must update the protocol changelog, `docs/product/constraints.md`, command registration/conformance coverage, and integration tests.

The broker transaction performs one indivisible decision:

1. Look up the exact active DLQ item by fingerprint and verify its raw-byte hash.
2. Check `actionId` before target conflict handling.
3. Validate target eligibility and corrected payload limits.
4. Reserve bounded audit/evidence capacity.
5. Remove those exact original bytes from the active DLQ.
6. Append those bytes unchanged to the resolved-remediation archive with lineage/audit metadata.
7. Enqueue a corrected entry to the same exact queue with the proposed new ID, attempts reset for the new attempt, and no stale failure trailer.
8. Persist the action result for idempotent return.

If any step fails, none of steps 5–8 is visible. The command never acts by list head or count. Unknown or already-resolved exact fingerprints return a conflict/not-found result as specified by the authoritative protocol; they never fall back to another item.

### Concurrency and idempotency

```text
Operator A                Broker transaction                 Operator B
    | actionId=A, fp=F            |                              |
    |---------------------------->| lock exact target + indexes  |
    |                             | F active; A unseen            |
    |                             | archive F; enqueue corrected  |
    |<------ ReplayAccepted ------|                              |
    |                             |<----- actionId=B, fp=F -------|
    |                             | F already resolved by A       |
    |                             |------ Conflict; no enqueue --->|
    | actionId=A, fp=F            |                              |
    |---------------------------->| return stored result for A    |
    |<------ same result ----------|                              |
```

Exactly one different action ID can win for a fingerprint. A retry with the same `actionId` returns byte-for-byte equivalent result data, including the already assigned corrected ID, and never enqueues again. To provide that durable guarantee without unbounded growth, accepted action results live in the bounded resolved evidence store. The store applies backpressure: when its configured entry/byte capacity cannot retain a new original and audit result, the command rejects before changing the DLQ. It does not silently evict evidence or create an unbounded audit log.

### Payload editor and validation

The editor starts from a copy of the captured JSON payload body. The original raw bytes are never edited. Before confirmation, the browser may provide immediate syntax feedback, but server validation is authoritative and uses System.Text.Json terminology and behavior:

1. corrected content is valid JSON;
2. the Highway envelope has the supported `v` and `body` shape;
3. serialized corrected payload bytes do not exceed `MaxPayloadBytes`;
4. target is the original exact `Q` target and corrected ID is new/valid;
5. the expected fingerprint/hash still matches active evidence.

The broker cannot validate the expected CLR contract type. Contract assemblies and types live in clients, and the wire intentionally carries no CLR metadata. The confirmation and result state this limitation. No schema registry or CLR-type validation is introduced.

If `PayloadCapture` withheld the body, no editor or corrected replay control is shown. Failure type may still be present as metadata, but withheld detail is not reconstructed. Truncation in failure message/stack is presented as truncation, not as a complete diagnostic.

### Confirmation and accessibility

Confirmation is mandatory and shows: exact target, original ID and fingerprint, proposed corrected ID, a readable payload diff and original/corrected hashes, plus an at-least-once/side-effect warning. It does not claim contract validity or likely business success.

The flow is keyboard-operable; opening confirmation moves focus into the dialog, cancelling returns focus to the initiating control, completion moves focus to the result heading, and status changes use an `aria-live` region. Diff markup remains readable without color. Loading, empty, validation-error, authorization-error, conflict and unavailable states are explicit. An opt-in banner remains visible whenever write actions are enabled. The action layout stacks responsively without hiding target, identity, warning or confirmation information.

Feature 022's router, ES modules and `ViewScheduler` remain: only the active view polls. Actions do not introduce a second scheduler or a build step.

## Data Models

### Read projections

```csharp
internal sealed record MessageSummaryDto(
    string Id,
    string Entity,
    MessageOutcome Outcome,
    DateTimeOffset FirstSeen,
    TimeSpan? Duration,
    string? FailureType,
    string? StartedOnNode,
    DateTimeOffset? CompletedAt,
    string? CompletedOnNode,
    EvidenceCompleteness Evidence);

internal sealed record AttentionItemDto(
    AttentionKind Kind,              // DeadLettered | RetryingFailure | SuspectedStuck
    string TargetKind,
    string TargetName,
    string MessageId,
    string? DeadLetterFingerprint,
    EvidenceCompleteness Evidence,
    RemediationEligibility Eligibility,
    string? DisabledReason);
```

### Failure context

```csharp
internal sealed record FailureContextDto(
    string? ExceptionType,
    string? FirstExceptionType,
    string? Message,
    string? Node,
    DateTimeOffset? At,
    string? Stack,
    string? InnerExceptionType,
    FailureDetailState DetailState,  // Available | Withheld | NotReported | Malformed
    bool MessageTruncated,
    bool StackTruncated);
```

`MessageTruncated` and `StackTruncated` come from explicit bounded-detail markers/evidence. Absence of a stack is not automatically `truncated`; it can be not reported or withheld.

### Remediation request and result

```csharp
internal sealed record CorrectedReplayRequest(
    string ActionId,
    string TargetName,               // exact Q name; derived group queue allowed
    string OriginalMessageId,
    string ExpectedFingerprint,
    string ExpectedOriginalHash,
    string CorrectedMessageId,
    byte[] CorrectedPayload,
    string CorrectedPayloadHash);

internal sealed record RemediationResult(
    RemediationDisposition Disposition,
    string ActionId,
    string Actor,
    DateTimeOffset AcceptedAtUtc,
    string TargetName,
    string OriginalMessageId,
    string OriginalFingerprint,
    string OriginalHash,
    string? CorrectedMessageId,
    string? CorrectedHash,
    string ResultCode,
    string? ConflictActionId);
```

### Immutable resolved evidence and bounded audit

```csharp
internal sealed record ResolvedRemediationEvidence(
    byte[] OriginalRawDeadLetter,
    string OriginalFingerprint,
    string OriginalHash,
    string OriginalMessageId,
    string ActionId,
    string CredentialActor,
    DateTimeOffset AtUtc,
    string? SourceIp,
    string TargetKind,
    string TargetName,
    string CorrectedMessageId,
    string CorrectedHash,
    RemediationDisposition Acceptance,
    string ResultCode);
```

The fingerprint and hashes use a protocol-specified cryptographic digest over exact bytes (the design recommendation is SHA-256 with a canonical text encoding for ETags). The original archive field is the exact active dead-letter byte sequence, not a decode/re-encode. Secrets and CSRF material are prohibited from evidence and audit records.

The archive and action-result index are durable, bounded key structures declared in the same bounded-structure inventory as dead-letter storage. Capacity is measured in entries and bytes, configured/documented, and checked before mutation. Rejection on full capacity is explicit. No accepted audit result is silently discarded to make room.

### Remediation outcome state machine

```text
OriginalActive
      |
      | atomic exact-item acceptance
      v
ReplayAccepted  -- corrected copy queued; NOT business success
      |
      | claim/start evidence observed
      v
ProcessingObserved
   |             |                 |                    |
   | ack         | failure         | new DLQ            | evidence ages out /
   v             v                 v                    | observation timeout
Processed    FailedAgain    DeadLetteredAgain           v
                                                   OutcomeUnknown
```

The original fingerprint, action ID, corrected message ID and any later corrected-message dead letter form one lineage. `FailedAgain` reflects observed handler failure; `DeadLetteredAgain` links the new dead letter by corrected ID/fingerprint. If the recorder window or observation timeout expires without decisive evidence, the state is `OutcomeUnknown`, never `Failed`. Processing outcome is later observation and is not part of the atomic replay acceptance result.
