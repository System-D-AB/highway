# Feature: Messages, Not Protocol Events — with Safe Remediation

## Introduction

Feature 022 made the dashboard show what exists. Feature 023 changes the unit from protocol events to messages: one row for the work a developer submitted, its cross-node journey, its outcome, and the evidence available within the bounded recorder window.

The original architecture remains: **the server aggregates; the browser renders**. Correlation is by message ID scoped by entity, protocol mechanics remain available under diagnostics, node identity is an observed fact, and the front end remains no-build ES modules with one active-view scheduler.

This revision adds the user-approved operational interface. It is not a general write console. It permits only evidence-preserving corrected replay of eligible queue (`Q`) dead letters through a new exact-item atomic command. The original dead letter remains immutable evidence; suspected in-flight work and RPC dead letters are diagnostic-only; all write capability is separately authenticated, off by default, audited, bounded, and honest about later outcomes.

## Requirements

### Requirement 1: The Unit Is a Message

**User Story:** As a developer, I want one row per message I sent, not one row per protocol operation.

#### Acceptance Criteria

1. An entity page SHALL list one row per correlated message with identifier, started time and node, completed time and node or non-completion reason, developer-facing outcome, duration, and evidence completeness.
2. The default entity view SHALL show message outcomes rather than protocol event names.
3. Message outcomes SHALL use developer terms: processed, failed, dead-lettered, in flight, abandoned, incomplete, or the remediation outcome states defined in Requirement 13.
4. The server SHALL derive correlation, facts, outcomes, and counts; JavaScript SHALL only render server results.
5. WHEN retained events do not establish a complete lifecycle THEN the system SHALL mark the message evidence incomplete rather than infer a confident outcome.
6. Every event type SHALL be classified `Public` or `Internal` on the server.
7. Summary facts SHALL be built from developer-relevant Public facts while permitting Internal events, such as acknowledgement, to evidence those facts.
8. The browser SHALL NOT duplicate event classification or protocol-semantic outcome rules.
9. WHEN an acknowledgement evidences successful handling THEN the summary SHALL say processed and SHALL NOT expose acknowledged as the developer-facing outcome.

### Requirement 2: Counts Answer the First Question

**User Story:** As an operator, I want to see how much work succeeded and how much needs attention.

#### Acceptance Criteria

1. Every service, queue, channel, and derived subscriber-group queue SHALL show processed and failed counts.
2. Counts SHALL state the recorder window they cover and SHALL NOT imply lifetime totals.
3. A non-zero failure count SHALL be visually distinct and SHALL link to correlated messages.
4. Counts SHALL distinguish failed, dead-lettered, and refused outcomes.
5. A channel count SHALL aggregate its groups while each group's count remains visible.
