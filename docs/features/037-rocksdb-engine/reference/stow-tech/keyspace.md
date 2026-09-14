# Keyspace — ordered keys, prefix iteration, range semantics

*Authority: mechanics and costs. For **why** the layout is shaped this way see V14, V15,
D39; for per-index structure see [`index-structures.md`](index-structures.md); for the
`meta` record format see [`document-layout.md`](document-layout.md).*

**Engine:** RocksDB 11.1.2.3412. Keys are byte-comparable; all ordering derives from
bytewise comparison of the encoded key — no custom comparators (Part 1b.1).

---

## 1 · Overview

RocksDB is an **ordered keyspace**. Every key lives in a global byte-sorted sequence
within its column family. This is the fundamental difference from v1's Garnet hash
keyspace: there is prefix iteration, range scanning, `SeekForPrev`, and `DeleteRange` —
and no concept of hash slots, cluster shards, or hash tags.

**Clients never compose keys.** Addressing is by logical `database/collection` paths and
document ids; the server resolves those to physical keys via the catalog.

## 2 · Key families

Every key is scoped to a column family. Within a family, the leading 4 bytes are the
**collection code** (uint32, big-endian) — giving every collection its own contiguous
key range.

| Column family | Key layout | Value |
|---|---|---|
| `meta` | `<coll:4><encoded id>` | MetaRecord (envelope + indexed values ± body) |
| `body` | `<coll:4><encoded id>` | raw payload bytes |
| `index` | `<coll:4><idx:2><encoded value>…<encoded id>` | (empty) |
| `uniq` | `<coll:4><idx:2><encoded value>…` | `<encoded id>` |
| `catalog` | UTF-8 key strings (`gen`, `col|…`, `idx|…`, etc.) | structure records |

The `<coll:4>` prefix is globally unique across databases (V15) — databases are a
catalog grouping, not a key prefix.

## 3 · Collection codes

Each collection is assigned a **uint32 code** from a monotonic counter at creation.
Codes are globally unique across all databases in one RocksDB instance.

**Never reused.** A dropped collection's code is retired permanently. Reuse would let
surviving compaction tombstones alias into a replacement as valid-looking wrong data —
the same hazard as index id reuse (§ 1.8 of the index doc).

The 4-byte prefix means:
- Prefix scans for "all documents in collection X" are a seek to `<code>` and iterate
  while the prefix matches.
- `DeleteRange` bounds for collection drops are exactly `[<code><0x00…>, <code><0xFF…>]`
  per column family.
- No hash tags, no slot assignment, no cluster routing — the collection code *is* the
  physical namespace.

## 4 · Document ids

Document ids encode with the same order-preserving encoders as index values. The id type
is fixed at collection creation (V12.3).

| Id type | Encoded form | Key ordering |
|---|---|---|
| `Guid` (v7) | 16 bytes, big-endian field order | time-ordered — inserts cluster at the tail |
| `long` / `int` | 8 bytes, sign-bit flipped | numeric |
| `string` | UTF-8, escaped (`0x00→0x00 0xFF`), terminated (`0x00 0x00`) | ordinal |

The variable-width id costs nothing: the 4-byte collection prefix stays fixed, so every
prefix scan and `DeleteRange` bound is unaffected.

## 5 · Index keys

**Ordinary index:** `<coll:4><idx:2><encoded value components><encoded doc id>` → empty.
The document id in the key ensures uniqueness and provides the id-ordering-within-a-value
property that makes multi-index intersection a sorted merge.

**Unique index:** `<coll:4><idx:2><encoded value components>` → `<encoded doc id>`.
The id is absent from the key — that *is* the constraint. A second document with the
same value collides on the same key rather than creating a new entry.

See [`index-structures.md`](index-structures.md) for encoding details, compound
delimitation, and query execution.

## 6 · Seek and range semantics

| Operation | Physical |
|---|---|
| Point read | exact `Get` on the key |
| Prefix scan | `Seek(<prefix>)`, iterate while prefix matches |
| Range scan | `Seek(<lower bound>)`, iterate while `key ≤ <upper bound>` |
| Reverse scan | `SeekForPrev(<upper bound>)`, `Prev()` while `key ≥ <lower bound>` |
| `DeleteRange` | `[start, end)` — all keys in the half-open range are tombstoned in one operation |

There is no `SCAN` cursor, no slot redirection, and no cluster routing. Every operation
is a direct key-range operation on the local store.

## 7 · `DeleteRange` for collection drops

Dropping a collection issues one `DeleteRange` per column family (`meta`, `body`,
`index`, `uniq`). Bounds: `[<code><0x00…0x00>, <code+1><0x00…0x00>)` — the 4-byte
code boundary is exact. Measured at **~1 ms for 50 000 keys** in the bake-off.
Reclamation happens at compaction (C24).

## 8 · Databases are catalog metadata, not key structure

A database is a named grouping in the catalog. It is **not** in the key (V15.2) —
nothing ever scans "all collections in a database" as a hot path. Collection codes
are globally unique, so the physical key layout is unchanged regardless of which
database a collection belongs to.

Drop database = enumerate collections from the catalog → `DeleteRange` each → remove
catalog records.
