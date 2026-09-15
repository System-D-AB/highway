# Feature 037 — The physical layout Highway adopts

*Authority: the on-disk key layout and column-family structure the RocksDB engine uses.
This realizes OD4 (design.md) and is the concrete answer to "what replaces Garnet's
List/SortedSet/Set objects." For the seam that sits above it see
[`../../../src/Highway.Server/Storage/IHighwayStore.cs`](../../../src/Highway.Server/Storage/IHighwayStore.cs);
for the prior art it borrows from see [`reference/README.md`](reference/README.md).*

**Engine:** RocksDB via `RocksDbSharp`. Keys are byte-comparable; all ordering derives
from bytewise comparison of the encoded key — no custom comparators.

---

## 1 · The decision: Highway is not a document store underneath

The sibling `stow-rocksdb` project layered two things on RocksDB, and only one of them is
Highway's:

- **Layer A — physical KV mechanics.** Prefix-partitioned ordered keyspace, order-preserving
  encoders, one `WriteBatch` per operation, snapshot reads, a striped lock, a counter merge
  operator, `DeleteRange`, WAL recovery mode. **Semantics-agnostic.** Highway adopts this
  layer whole.

- **Layer B — document-store semantics.** Collections and collection codes, a catalog, the
  `meta`/`body` split and `MetaRecord`, secondary indexes, unique constraints, CAS-by-version,
  the query surface. **This exists to answer content queries** — "find documents whose fields
  match." Highway issues no content query in any of its 23 commands: a message is claimed by
  FIFO position, a lease is swept by expiry order, a reply is fetched by exact request id, a
  group is found by exact name. **Highway drops Layer B entirely.**

So there is **no collection → document model** under Highway. The unit is not a document with
an incidental id; it is an **opaque entry at an ordered position**, where the position is the
whole point. There is nothing to query *inside* an entry, so none of the machinery that exists
to make that possible is carried.

### What that means concretely

| Stow (Layer B) | Why it exists | Highway |
|---|---|---|
| Collection code `<coll:4>` from a catalog | Namespace a document set, allocated by `CollectionRecord` | **Dropped.** The prefix is a fixed structure-kind tag + the logical name off the wire. No catalog, no code allocation, no reuse hazard |
| `meta`/`body` split, `MetaRecord` (version, flags, TTL, indexed values, inlined body) | Read metadata without the payload, for index maintenance | **Dropped.** Highway always wants the whole entry; the value *is* the entry bytes, already self-framed by `Envelope` |
| `index` + `uniq` column families, `IndexKey`/`UniqKey` | Serve content queries and unique constraints | **Dropped.** No content query and no unique-by-field exists |
| CAS-by-sequence, per-document version | Optimistic concurrency on a *mutable* document | **Dropped.** A Highway entry is immutable once enqueued; "concurrency" is the per-queue lock deciding who claims |
| catalog CF, `DatabaseRecord`, `CollectionRecord` | Resolve names → codes, hold index definitions | **Dropped.** Highway's "registry" (`hw:reg:*`) is ordinary data, not schema |

---

## 2 · The four key families

Every Highway structure is a prefix-partitioned key range: a **one-byte family tag**, then the
logical name (order-preserving encoded), then a family-specific ordered suffix. The tag replaces
stow's 4-byte collection code — Highway has a handful of structure *kinds*, not an open set of
collections, so a fixed tag is enough and needs no allocator.

| Family | Tag | Key layout | Value | The ordered operation |
|---|---|---|---|---|
| **KV** | `k` | `k` `<name>` | value | point get |
| **List** | `q` | `q` `<name>` `<seq:8 BE>` | entry bytes | seek-first-on-prefix = pop head |
| **Ordered set** | `z` | `z` `<name>` `<score:8 order-preserving>` `<member>` | ∅ | range-by-score = "what is due" |
| **Membership set** | `s` | `s` `<name>` `<member>` | ∅ | prefix scan = members |

Plus two internal keys the families need:

| Purpose | Key layout | Value |
|---|---|---|
| Per-list sequence allocator | `n` `<name>` | i64 counter (via the merge operator) |
| List head cursor (optional; see §5) | folded into the seq counter | — |

`<name>` is the logical Highway name exactly as it arrives on the wire — `invoices`,
`orders@billing`, a request id — encoded with the order-preserving string encoder
(`0x00 → 0x00 0xFF`, terminated `0x00 0x00`) so the name and the suffix that follows it cannot
collide (`("ab","c") ≠ ("a","bc")`). This is the single reason a compound key is safe without a
delimiter the name might itself contain.

### Why a tag and not stow's collection code

Stow allocates a 4-byte code per collection because collections are an *open, user-defined set*
that must be created, dropped, and never reused. Highway's structure kinds are a *closed set of
four*, fixed in code. A one-byte tag distinguishes them; the logical name distinguishes instances
within a kind. No counter, no catalog record, no drop-and-retire bookkeeping — the simplification
the "no Layer B" decision buys.

---

## 3 · Every current key, mapped

The Garnet keyspace (`HighwayKeys.cs`) maps onto the four families as follows. **The mirror keys
disappear** — `nodelist`, `grplist`, `job:index`, `grp:members`, `node:subs`, `node:channels` all
existed only because a Garnet `Prepare` could not read a Set without a watch conflict (the 004.1
rule). With no `Prepare` and no watch, a set is read with `SetMembers`, so each mirror collapses
into the set it mirrored (T3.3).

### Lists (family `q`)

| Concept | Garnet key | Adopted layout |
|---|---|---|
| RPC request queue | `hw:svc:{service}:q` | `q` `svc:{service}:q` `<seq>` |
| Work queue / derived group queue | `hw:q:{queue}:q` | `q` `q:{queue}:q` `<seq>` |
| Processing list (per node) | `hw:q:{queue}:proc:{node}` | `q` `q:{queue}:proc:{node}` `<seq>` |
| RPC processing list | `hw:svc:{service}:proc:{node}` | `q` `svc:{service}:proc:{node}` `<seq>` |
| Queue DLQ | `hw:q:{queue}:dlq` | `q` `q:{queue}:dlq` `<seq>` |
| Service DLQ | `hw:svc:{service}:dlq` | `q` `svc:{service}:dlq` `<seq>` |

FIFO is the seq order. `ListRightPush` allocates the next seq **in the same batch** (§5);
`ListLeftPop` seeks the family prefix and takes the first key; `ListLeftPush` (redeliver-to-head)
writes a seq below the current head.

### Ordered sets (family `z`)

| Concept | Garnet key | Adopted layout | Score |
|---|---|---|---|
| Delayed / deferred set | `hw:q:{queue}:delayed` | `z` `q:{queue}:delayed` `<score>` `<member>` | absolute delivery ticks |
| Job schedules | `hw:job:{queue}:schedules` | `z` `job:{queue}:schedules` `<score>` `<member>` | next-fire ticks |

The score is a signed .NET tick count encoded order-preservingly (§4), so a range-by-score is a
prefix iterate. **This is what replaces Garnet's `SortedSet` object** — and it kills the culture
bug the current code carries (Garnet stored the score as a double and formatted it with the
current culture, returning a tick count as `6,39E+17` on a European machine — see
`HwQClaimCommand.PromoteDueMessages`). The score is now bytes.

### Membership sets (family `s`)

| Concept | Garnet set key | Adopted layout | Mirror that collapses |
|---|---|---|---|
| Service worker nodes | `hw:svc:{service}:nodes` | `s` `svc:{service}:nodes` `<member>` | `…:nodelist` |
| Queue worker nodes | `hw:q:{queue}:nodes` | `s` `q:{queue}:nodes` `<member>` | `…:nodelist` |
| Channel groups | `hw:ch:{channel}:groups` | `s` `ch:{channel}:groups` `<member>` | `…:grplist` |
| Group members (025) | `hw:grp:members:{ch}@{grp}` | `s` `grp:{ch}@{grp}:members` `<member>` | *(was itself a mirror)* |
| Node subscriptions (025) | `hw:reg:node:{node}:subs` | `s` `reg:node:{node}:subs` `<member>` | *(was itself a mirror)* |
| Node channels (017) | `hw:reg:node:{node}:channels` | `s` `reg:node:{node}:channels` `<member>` | *(was itself a mirror)* |
| Registered node list | `hw:reg:nodes` | `s` `reg:nodes` `<member>` | *(was itself a mirror)* |
| Service discovery index | `hw:reg:svc:{service}` | `s` `reg:svc:{service}` `<member>` | *(was itself a mirror)* |
| Job index | `hw:job:index` | `s` `job:index` `<member>` | *(was itself a mirror)* |

Membership is a point get on `s` `<name>` `<member>`; add is a put of an empty value; enumerate
is a prefix scan. The seven mirror keys become plain sets.

### KV (family `k`)

| Concept | Garnet key | Adopted layout | Note |
|---|---|---|---|
| RPC reply slot | `hw:rep:{requestId}` | `k` `rep:{requestId}` | the only expiring key — `SetEx` |
| Channel message-ID seq | `hw:ch:{channel}:seq` | `n` `ch:{channel}:seq` | counter family (§5) |
| Node registration record | `hw:reg:node:{node}` | `k` `reg:node:{node}` | binary header + catalog bytes |
| Byte counter (016) | `hw:q:{queue}:bytes` | `n` `q:{queue}:bytes` | counter family (§5) — no longer GET/SET |

Doorbell channels (`hw:door:*`) are **not stored** — they are RESP pub/sub topics, handled by the
socket layer (design §4.4), never written to RocksDB. They stay exactly as they are.

---

## 4 · Score and sequence encoding — order-preserving

The one hard requirement of families `q` and `z`: the suffix must sort under bytewise comparison
in the same order as its numeric value, so a prefix iterate returns entries in FIFO / by-score
order.

- **Sequence (`q` suffix):** an unsigned monotone i64, big-endian. A list only ever appends, so
  an unsigned counter is enough and its natural big-endian order is FIFO.
- **Score (`z` suffix):** a *signed* .NET tick count. The sign bit is flipped and the value is
  written big-endian (`v ^ 0x8000_0000_0000_0000`), so negatives sort below positives and the
  bytewise order equals the numeric order. This is stow's `Int64Encoder` verbatim
  (`reference/stow-engine/Encoding/Int64Encoder.cs`).
- **Name and member components:** UTF-8 with `0x00 → 0x00 0xFF` escaping and a `0x00 0x00`
  terminator, so a compound key is self-delimiting. This is stow's `StringEncoder`
  (`reference/stow-engine/Encoding/StringEncoder.cs`).

All three write through one `KeyWriter` (stackalloc-first, pool-on-overflow), which is the single
allocation control point.

---

## 5 · Sequence allocation — the trap, and where it lives

A `q`-family key needs a monotone per-list sequence. **It is allocated inside the same batch as
the push it belongs to**, from the per-list counter key `n` `<name>` via the merge-operator
`Increment` — never from RocksDB's global engine sequence, and never from a separate transaction.

This is the **B1 trap** documented in `reference/stow-engine/Engine/SequenceSource.cs`: the global
engine sequence advances on *any* write, so it cannot be reserved under a per-list lock; two
concurrent pushes to different lists would both read the same "next" and collide. A per-list
counter, incremented in-batch, is reserved by the same batch atomicity that commits the entry.

`ListLeftPush` (redeliver-to-head) needs a seq *below* the current head. Two workable schemes:
1. a signed seq with the head growing downward from 0 and the tail upward — head-push decrements a
   low-water counter, tail-push increments a high-water counter; or
2. a single unsigned seq space with head-push reusing gaps below the lowest live key.

> **Chosen: scheme 1 (feature 038 T4, 2026-09-15).** The seq is a *signed* i64 encoded
> order-preservingly (§4), so a negative head seq sorts below every non-negative tail seq
> automatically — no separate comparator, no gap-reuse bookkeeping. Two `n`-family counters
> per list beside it: a high-water the tail increments from 0 up, a low-water the head
> decrements from 0 down (`ListSequence(name)` and `ListSequence(name) + ":low"`). Proven on
> both stores by `Seq_MultipleHeadPushes_PopInReverseInsertionOrder` and the concurrent
> tail-push tests (038 T4).

---

## 6 · Column families

Column families split for **operational** reasons — compaction and iteration locality — not for
the hot/cold metadata reason stow's `meta`/`body` split existed (Highway always reads the whole
entry, so there is no metadata to separate). The split:

| # | Name | Contents | Why separate |
|---|---|---|---|
| 0 | `default` | unused | RocksDB requires it |
| 1 | `data` | families `q`, `z`, `s`, `k`, `n` — the live working set | the hot path; churns and compacts constantly |
| 2 | `dlq` | dead-letter lists (the `q` `…:dlq` keys) | cold, bounded, rarely read; keeping it out of `data` stops a growing DLQ from polluting the live block cache |

This is the minimal split that keeps a poison-message backlog from evicting live queue keys from
cache. Whether the registry (`k`/`s` `reg:*`) earns its own third CF is a T2.3 decision — it is
tiny and rarely written, so it likely rides in `data`. The family tag is inside the key, so a key
names its family regardless of which CF holds it; the CF choice is a placement decision, not a
namespacing one.

**The order is fixed and asserted on open** — a batch addresses a CF by handle, and a mismatched
order writes to the wrong family silently. This is stow's `ColumnFamilies.AssertOrder` discipline
(`reference/stow-engine/Layout/ColumnFamilies.cs`).

---

## 7 · Retirement, decommission, and C4.6

A whole structure's teardown — a retired subscriber group, a decommissioned node's key ranges — is
one `DeleteRange` over the family+name prefix `[prefix, prefix++)`, not the enumerate-and-`DELETE`
loop the Garnet commands run. On an ordered keyspace a prefix is a contiguous range, so this is a
single operation (measured elsewhere at ~1 ms for 50 000 keys).

**This is the mechanical answer to C4.6.** A drained queue's range is *physically reclaimed* at the
next compaction, not merely logically truncated the way Garnet's `TruncateUntil` left retired AOF
segments on disk forever. Reclamation is compaction — what an LSM does for a living. The
`SustainedTraffic_DoesNotGrowTheLogWithoutBound` test that C4.6 currently fails is re-pointed here
(T6.3).

---

## 8 · What is unchanged above this layer

Nothing in this document touches the wire. `HW.*` names, argument order and reply shapes are
identical; `Highway.Client` is untouched; entry framing (`Envelope` — attempt count, claim
timestamp, failure block, the leading format-version byte and `StorageFormatException` guard) is
preserved exactly and treated as opaque bytes by every family here. The physical layout changes
underneath; the observable behaviour does not.
