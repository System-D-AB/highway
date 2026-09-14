# Document layout — MetaRecord, body separation, the meta/body split

*Authority: mechanics and costs. For **why** the layout is shaped this way see V8, V10,
V12.4; for per-index entry encoding see [`index-structures.md`](index-structures.md);
for key layouts see [`keyspace.md`](keyspace.md).*

**Engine:** RocksDB 11.1.2.3412. Implementation: `src/Stow.Storage/Layout/MetaRecord.cs`.

---

## 1 · Overview

Every stored document lives across two column families:

| CF | Key | Contains | Temperature |
|---|---|---|---|
| `meta` | `<coll:4><encoded id>` | MetaRecord: envelope + indexed values ± inlined body | hot |
| `body` | `<coll:4><encoded id>` | raw payload (only when body exceeds the inline threshold) | cold |

The split exists so the hot path — stale-entry computation, CAS checks, index
maintenance — **never reads large payloads**. A 1 MB document must not be read to
discover three indexed values.

## 2 · MetaRecord format

The format version byte is always first (V12.4). Without it the envelope can never
change — costs 1 byte now, a full rewrite of every document later.

```
Offset  Size  Field
──────  ────  ─────
[0]     1B    format version  — THIS record layout's version (currently 1)
[1]     1B    flags           — bit0=body inlined, bit1=has TTL, bit2=tombstone
[2..9]  8B    sequence        — last-write sequence = document version (V8's spine)
[10..13] 4B   catalog gen     — index set this document was written under
[14..21] 8B   TTL             — absolute UTC ticks (present ONLY if bit1 set)
[..]    var   indexed values  — encoded, in catalog index order
[..]    var   body            — inlined when under the size threshold
```

**Minimum header: 14 bytes** (no TTL). With TTL: 22 bytes. Total per-document measured
at **50.5 bytes** (separated mode, 3 indexes) —
see `../specs/v2-002-storage-foundation/artifacts/meta-footprint.md`.

## 3 · Flags

| Bit | Meaning |
|-----|---------|
| 0 | Body inlined in this meta record |
| 1 | TTL field present (8 bytes follow the generation) |
| 2 | Tombstone (deleted, awaiting compaction reclaim) |

## 4 · Indexed values block

The indexed values block stores the encoded values for all active indexes, in catalog
index order. Format: for each index, `<len:2><encoded bytes>`. `len=0` means the field
was missing (no entry was written for that index — R2.8).

**Why indexed values live in meta, not body:**

1. **Stale-entry computation reads meta, never body.** The write path must know which
   index entries the previous version created in order to delete them. Those entries are
   exactly the indexed values + the catalog generation at write time. Reading body would
   turn a ~50 B hot read into a multi-KB cold read on every save.

2. **The catalog generation in the envelope tells which index set was active.** If indexes
   have changed since the document was written, the stale-entry computer knows exactly
   which entries the old version created — by reading the values block under the old
   generation's index definitions.

## 5 · Body separation

The body **inlines** in the meta record when it is under the threshold, and **separates**
into the `body` CF when above.

**Threshold: 4096 bytes** (measured, `artifacts/inline-threshold.md`).

Below this size, inlining wins:
- One I/O operation instead of two for reads.
- Better cache locality for small documents.

Above this size, separation wins:
- The meta CF stays smaller — more envelopes fit in the block cache.
- Large bodies don't pollute the hot working set.

**Read path:** `Get` reads `meta`. If `flags & bit0` is set, the body is already there.
Otherwise, a second point read fetches it from the `body` CF. Both reads happen under
the same snapshot for consistency (R5.5).

## 6 · Sequence (the spine)

The sequence field (bytes 2–9) is the `asOf` value returned to callers (V8). It is also:
- The CAS token for optimistic concurrency (R4.3).
- The WAL position for change feeds and replication.
- The index-applier checkpoint for knowing when a rebuild has caught up (V13.3).

One number, serving every consistency mechanism in the system.

## 7 · Catalog generation (bytes 10–13)

Records which index set was active when this document was last written. On the next save,
the stale-entry computer compares the stored generation against the current one:
- Same generation: the indexed values block maps 1:1 to the current indexes.
- Different generation: the block was written under a different index set; the computer
  resolves which entries to delete using the historical index definitions.

This is what makes adding or removing an index safe without rewriting every document.

## 8 · Format versioning (V12.4)

Byte 0 is the format version. The reader checks it first:
- Version 0: invalid (never written).
- Version 1: current layout.
- Unknown higher version: `FormatVersionException` — the database was written by a newer
  StowDB version. This is the forward-compatibility guard that prevents silent misreads.

This one byte is what makes the meta record evolvable: a future version can add fields,
widen others, or reorder — and old code will refuse cleanly rather than corrupt silently.
