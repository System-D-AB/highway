# <Capability>

> **Status:** current as of vX.Y.Z
> **Protocol:** the exact commands, replies and keys live in
> [`docs/HIGHWAY-PROTOCOL.md`](../HIGHWAY-PROTOCOL.md) — this doc explains *how it works and why*,
> and never restates the wire surface.

*One or two sentences: what this capability is and the problem it exists to solve.*

## Overview

*The shape of the thing: what a user gets, the model (competing consumers, single-writer,
at-least-once…), and where it sits in Highway. Enough that someone new understands what this
does before any mechanism.*

## How it works

*The design — the mechanism, the key data, the important flows. Name the moving parts and the
invariants that hold them together. This is the meat of the doc: the "why it's built this way,"
including the trade-offs that shaped it.*

## Wire surface

*A short pointer, not a copy: which `HW.*` command family serves this, linking the relevant
section of [`docs/HIGHWAY-PROTOCOL.md`](../HIGHWAY-PROTOCOL.md). The protocol file is the single
source of truth for arguments, replies, errors and keys — this section only says which part to read.*

## Guarantees & limits

- **What it guarantees** — durability, ordering, delivery semantics, idempotency.
- **Bounded costs** — caps, windows, back-pressure behaviour. Name them; link
  [`constraints.md`](../product/constraints.md) where the guarantee is tracked line by line.
- **What it is not** — the explicit non-goals, so nobody infers a promise that isn't made.

---

*Authoring notes (delete when writing):*
- *One doc per capability, named by topic (`durable-queues.md`, `replication.md`), not by feature
  number. These are evergreen: they describe Highway as it is now, and are corrected when the code
  moves — not a changelog of how it got built.*
- *Never restate the protocol (link `HIGHWAY-PROTOCOL.md`) or a guarantee (link `constraints.md`);
  a second copy is a second thing to get wrong.*
- *Keep it to roughly one to two screens. Deep design history stays in the maintainer's local
  working notes.*
