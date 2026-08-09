# Tasks: Dashboard — Operations Console

**T1 is a spike, and it decides the feature.** Whether Garnet's state can be read in-process,
read-only, at acceptable cost is an open question. Everything after it depends on the answer,
and guessing produces the 018 bug in a new place.

**Nothing here is a write operation.** If a task grows a requeue, purge or retire button, that
task is wrong — read-only first, writes get their own feature with confirmation and audit.

---

## Phase 0 — Settle the read path

### - [x] T1 — Spike: can the dashboard read broker state in-process? **No.**

**Answer: an in-process read path does not exist as supported API.** Falling back to C, the
server-owned connection, with mTLS as a documented limitation.

What the spike found:

- Garnet exposes `StoreApi` for admin operations (flush, commit, checkpoint) — **not** for
  reading keys or running commands.
- An `EmbeddedRespServer` pattern *does* exist, but only as `internal` scaffolding inside
  Garnet's **benchmark** project (`benchmark/BDN.benchmark/Embedded/`, ~400 lines across five
  files). It is not a supported API.
- Adopting it means **vendoring 400 lines of unsupported internals** and changing how Highway
  constructs its server — `GarnetServer` takes an `IGarnetServer[]`, so a TCP and an embedded
  transport could coexist, but every Garnet upgrade would then be able to break the dashboard's
  read path in ways the compiler would not necessarily catch.

That is more coupling than a read-only view is worth. `HighwayGarnetServer` already reaches for
one `protected` field (`storeWrapper`) and that has been stable; a whole vendored transport is a
different order of dependency.

**So: C — a connection the SERVER builds from its own options**, not one the dashboard assembles
from a connection string. That removes 018's actual failure mode, which was *mirroring* the
configuration and forgetting a setting, rather than the connection itself.

**What it still cannot do:** present a client certificate under
`Tls.ClientCertificateRequired`. Named in R1.2 and handled by degrading the state views with a
clear message — never by failing the broker, which is what 018 did.

<details><summary>The original task text</summary>

### T1 — Spike: can the dashboard read broker state in-process?</details>

*Requirements:* R1.1, R1.4, Open Decision 1
**Done when** one of these is true and written down:

- **In-process works.** A read-only path from `HighwayServer` to queue depth and DLQ contents,
  with a measured cost per read.
- **It does not.** The reason is recorded, and the fallback is a **server-owned connection**
  built from the server's own options at startup — transport matching by construction rather
  than by mirroring, with **mTLS as a named limitation**.

**Do not skip to the loopback connection.** 018's pre-018 check took that route, mirrored the
password and forgot TLS, and **no TLS-enabled server could start**. The fix made it
transport-aware and *still* cannot present a client certificate under mTLS. Whatever T1
concludes must be tested against all four security configurations (T2) before anything is built
on it.

### - [x] T2 — The security matrix, first

*Requirements:* R1.2
**Done when:** the chosen read path is proven against **open, password, TLS, and TLS with
`ClientCertificateRequired`** — the last one being the one that defeats a loopback connection.

**This lands before any view.** A read path that works on an open broker and fails on a secured
one is not a read path; it is a demo. Finding that out after four views are built means
rewriting four views.

**Done.** Six tests across open, password, TLS and mTLS. `LoopbackConnection` is the one place
that builds a self-connection from server options — the startup check from 018 now shares it, so
a transport setting added later is added once rather than mirrored by two callers who will
diverge.

> **Doing this first paid for itself immediately.** The TLS case failed on the first run — but in
> the *test*, whose seeding client connected in plaintext to a TLS listener, not in the read
> path. The read path was correct because it never assembles a connection string. Had this been
> written after the views, that failure would have arrived tangled up with four views' worth of
> other changes.

Under mTLS the read **degrades with a reason naming the setting**, and the broker keeps running.
018's version of this took the whole broker down.

### - [x] T3 — `IBrokerState`, narrow and read-only

*Requirements:* R1.1, R1.3, R1.4, R1.5
**Done when:** one interface returns DTOs for queue state and dead letters, a failure surfaces
as a failed read rather than an exception escaping into the host, and the polling cost is
measured and stated.

Read-only **by construction** — the interface exposes no mutation, so R1.4 is a compile-time
fact rather than a code-review promise.

**Done.** `IBrokerState` returns `StateResult<T>` — a value with either data or a reason it is
unavailable — so a failed read is a *return*, never an exception escaping into the host. The
connection is lazy: a broker must not fail to start because a diagnostic component could not
connect to it.

A subscriber group is identified by `@` in its derived name, which is the whole reason one view
covers both verbs (018).

---

## Phase 1 — Give the server the numbers to report

### - [ ] T4 — `HW.STATS` gains what is missing

`consumers` and `oldestClaimAge` per queue, and a `QUEUES` form returning one row each.

*Requirements:* R3.1, R3.4, R4.3, Open Decision 3
**Done when:** both are documented in `docs/HIGHWAY-PROTOCOL.md` in the same change and
`ProtocolConformanceTests` is green. That gate has fired six times.

**The dashboard must never learn the key layout.** `HighwayKeys` exists to centralise it, and a
second reader is a second thing to update when it changes — the drift this project keeps
finding. `consumers` and `oldestClaimAge` are the two facts R3.4 and R4.3 need and neither is
derivable from a depth number.

### - [ ] T5 — `Detail` and severity on recorder events

*Requirements:* R5.1, R5.2, R5.4
**Done when:** `HighwayEvent` has a `Detail` field, features 016, 017 and 019 move their prose
into it, and severity is derived **from the event type** — one mapping, one place to change,
rather than a parameter every call site can get wrong.

`"retired 1 group(s), discarded 41 message(s)"` is a message. It has been travelling in a field
named `ErrorCode` since 016.

---

## Phase 2 — The views

> Each view lands separately and works on its own. A half-finished console that shows queues but
> not dead letters is still worth more than none.

### - [ ] T6 — Queues

*Requirements:* R3.1, R3.2, R3.3, R3.5
**Done when:** every queue lists depth, **bytes as a proportion of `MaxQueueBytes`**, in-flight,
dead-letter and delayed counts — and **subscriber groups appear as queues, because they are**
(018). A channel is its groups.

Fullness is a proportion, not a number. "847 MB" means nothing without the limit beside it;
"83% of 1 GB" is actionable at a glance.

### - [ ] T7 — Dead letters

*Requirements:* R2.1–R2.6
**Done when:** entries show `failureType`, `attempts`, `reason`, `deadLetteredAt` and the
detail; the stack is **collapsed by default** and expandable; a dead letter with no context
**says so** rather than showing blanks; and the payload obeys 002's capture modes.

**`firstType` is rendered as a sentence**, not a row: *"the failure changed — started as
`TimeoutException`"*. It is the answer to the question an operator asks, and 015 added it
specifically to answer that. A `failureFirstType:` label beside eleven others wastes it.

### - [ ] T8 — What is about to go wrong

*Requirements:* R4.1, R4.2, R4.3
**Done when:** queues above a configurable fullness threshold are highlighted; a subscriber
group whose node is absent shows **"retires in ~11h unless it returns"**; and a handler past
half `MaxProcessingTime` is visible.

> **T8 is why this feature is worth building.** T6 and T7 make the last five features
> *diagnosable*; T8 makes them *predictable*. The retirement countdown is the highest-value
> item on the page — 017 made retirement automatic, and it destroys a subscriber's entire
> backlog. A countdown turns the largest single loss Highway can inflict from a surprise into a
> decision.

The uncertainty is phrased deliberately. A node that returns resets the clock, so an operator
who watches the countdown vanish has learned it came back.

### - [ ] T9 — Severity colouring

*Requirements:* R5.3
**Done when:** the event view styles on **severity**, not on "does this have an `ErrorCode`".

Today `NodeSuspect` — a warning that nothing has gone wrong *yet* — renders identically to a
failure. A signal that cries wolf is worse than no signal.

---

## Phase 3 — Conformance

### - [ ] T10 — Protocol and constraints

*Requirements:* R6.1, R6.2
**Done when:** `HW.STATS`'s new fields and forms are documented, `ProtocolConformanceTests` is
green, and `constraints.md` C7 covers the dashboard's own read path under the
observing-must-never-break rule.

### - [ ] T11 — Close what was waiting on this

*Requirements:* R6.3
**Done when:** 015's tasks.md marks R3.4 complete and removes the `ErrorCode` wart note (T5
fixed it), and 016's outstanding dashboard reference is closed.

Three features have carried "the dashboard cannot show this" notes. They close here or they
become permanent.

### - [ ] T12 — Samples and full verification

*Requirements:* R6.4, R6.5
**Done when:** the dashboard is exercised against a **secured** broker in the samples — not just
an open one, since T2 is the whole risk — the samples are re-run with a `RUNLOG.md` entry, all
tests pass, and `dotnet build --no-incremental` is warning-free.

**`--no-incremental` specifically.** An incremental build reported zero warnings while one
existed, twice in this project.

---

## Parallelization

```
LANE 0   T1, T2, T3     the read path        → blocks everything
LANE 1   T4, T5         server-side data     → can run beside lane 0 after T1
LANE 2   T6, T7, T8, T9 views                → needs 0 and 1
LANE 3   T10, T11, T12  conformance          → last

Order: 0 → 1 → 2 → 3

T1 is a genuine fork in the road, not a formality. Starting the views before it
resolves means building against a read path that may not exist.
```

---

## The line that must not move

**The dashboard must never be able to break the broker.** C7.1 applies to it as much as to the
recorder: a failed state read shows a failed view, never a failed server and never a failed
page. 018 already proved this class of bug is real — a startup check that opened a connection to
itself stopped every TLS-enabled broker from starting.

**And it is never a second source of truth.** Every number comes from `HW.STATS` or `HW.DLQ`.
A dashboard that computes its own answer is a dashboard that will eventually disagree with the
server, and the operator will believe the one with the nicer font.
