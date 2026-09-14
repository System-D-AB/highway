# Index structures — per-kind representation, encoding, maintenance, cost

*Authority: mechanics and measurements. For **why** indexes are this way see V1, V7,
V10, V12.4, V14; for the physical layout decisions see D38 (measured in spec `003`);
for operations see the research doc Part 2.*

**Engine:** RocksDB 11.1.2.3412. Implementation:
`src/Stow.Storage/Encoding/`, `src/Stow.Storage/Layout/IndexKey.cs`,
`src/Stow.Storage/Catalog/IndexDefinition.cs`.

---

## 1 · Representation per kind (D38)

| Kind | Key layout | Value | Query shapes |
|---|---|---|---|
| **Equality** | `<coll:4><idx:2><encoded value><encoded id>` | empty | `term`, `terms` (IN), `exists`, `contains`, `Distinct`, facets |
| **Range** | `<coll:4><idx:2><encoded value><encoded id>` | empty | `range`, `Min`/`Max` (O(1)), `OrderBy` |
| **Unique** | `<coll:4><idx:2><encoded value>` | `<encoded id>` | `term` (O(1)), constraint enforcement |
| **Compound** | `<coll:4><idx:2><comp1><comp2>…<encoded id>` | empty | leading-prefix queries, compound sort |

**Equality** is a per-value posting list: one key per (value, document id). Within a
single value, entries are ordered by id — which is what makes multi-index `AND` a
sorted-merge intersection rather than a hash join.

**Range** uses the same key layout as equality. The difference is in queries: ordered
iteration across values gives range scans, and `SeekFirst`/`SeekLast` within the index
range gives O(1) `Min`/`Max`.

**Unique** omits the document id from the key. The key *is* the constraint — a second
document with the same value collides on the same key. The id lives in the value, and
the holder id is returned in `UniqueConstraintException` (design § 6).

**Compound** encodes multiple fields as self-delimiting components (§ 3). The
leading-prefix rule is a physical property of lexicographic byte ordering, published
as `requiresPrefix` in `_queryable` (V7).

## 2 · Order-preserving encoders

Every encoder produces bytes whose bytewise comparison equals the native value ordering.
Verified per type in the property-based order test (task B11).

| Type | Encoding | Key properties |
|---|---|---|
| `long`/`int`/`short` | big-endian, sign bit flipped (`v ^ 0x8000…`) | negatives sort before positives |
| `double`/`float` | IEEE bits; positives: flip sign; negatives: flip all | `-0.0` normalised to `0.0`; `NaN` refused |
| `string` | UTF-8, `0x00` escaped to `0x00 0xFF`, terminated `0x00 0x00` | ordinal byte order preserved |
| `bool` | one byte: `0x00`/`0x01` | false < true |
| `DateTime` | UTC ticks as `long` | inherits `long` encoding |
| `Guid` | 16 bytes, big-endian field order (not `ToByteArray()`) | v7 Guids sort by time |
| `enum` | underlying integral type | inherits `long` |

**`-0.0` normalisation** prevents a document stored as `-0.0` from landing at a different
key than `0.0` — an equality query for `0.0` would silently miss it. Found by the
bake-off's encoding probe.

**`NaN` refused at encode time.** It has no position in any ordering; indexing it
produces a phantom entry no query can correctly match.

## 3 · Compound key delimitation

Concatenating raw component bytes is provably wrong: `("ab","c")` and `("a","bc")`
produce byte-identical keys. The fix:

- Each component is self-delimiting: `0x00` in a value escapes to `0x00 0xFF`.
- Each component terminates with `0x00 0x00`.

This makes compound ordering lexicographic by component, which is the physical property
the leading-prefix (ESR) rule rests on.

**Type tag per component:** `0x00` = null (sorts before all values), `0x01` = value.
With types enforced (V10.1) no wider tag is needed — enforcement makes the format
smaller rather than larger.

## 4 · Null vs missing

| Case | Physical |
|---|---|
| Field absent from document | **no index entry** |
| Field present and null | entry with `0x00` type tag, sorting before all values |

Two different questions: `exists(field)` is answerable from entry presence;
`field == null` is a normal equality lookup. This is a small improvement over Mongo,
where null and absent are famously entangled.

## 5 · Collation modes

Declared per index, fixed at creation, changed only by rebuild (C27).

| Mode | Encoding | Case | `prefix` support | Size |
|---|---|---|---|---|
| **`ordinal`** (default) | UTF-8, escaped | sensitive | ✅ | 1.0× |
| `ordinal-ci` | lowercased UTF-8, escaped | insensitive | ✅ | 1.0× |
| `culture:<tag>[:ci]` | ICU sort key bytes | per tag | ❌ (C29) | 1.3–2.0× |

**C29:** a culture-collated index cannot serve `prefix` queries because
`sortkey("ab")` is not a prefix of `sortkey("abc")` — ordering holds but the prefix
relationship does not. `_queryable` omits `prefix` for such indexes.

## 6 · Descending and mixed direction

- **Whole-index descending:** no special encoding. RocksDB iterators support
  `SeekForPrev`/`Prev`, so `ORDER BY x DESC` reads the same index backwards.
- **Mixed-direction compound** (`status ASC, createdAt DESC`): the descending
  component's bytes are inverted at encode time (`0xFF ^ byte`). This is the only case
  that needs it.

## 7 · Encoder version (V12.4)

Each index definition carries an **encoder version** (uint16). At open, if the stored
version differs from the current code version, the index is marked for rebuild. This
makes shipping an encoder fix survivable: a version bump → automatic background rebuild
→ alias swap (V13) → no user action, no downtime, no lost queries.

## 8 · Index lifecycle

| State | Meaning |
|---|---|
| `Building` | Background applier replaying from sequence 0; not serving queries |
| `Ready` | Fully caught up; serving queries and maintained on every write |
| `Retired` | Entries being reclaimed lazily at compaction; id permanently reserved |
| `Swapping` | Replacement built beside it; name will repoint when caught up (V13.2) |

**Never-reused ids.** Index ids (uint16) come from a monotonic counter in the catalog.
A dropped index's id is retired permanently — surviving entries from before the
`DeleteRange` completes at compaction would alias into a replacement as valid-looking
wrong data if the id were reused (§ 1.8 of the research).

## 9 · Measured query execution

Measured at 200 000 documents, 5 statuses, 20 regions
(`../research/2026-08-13-v2-data-structures-and-execution.md`):

| Operation | Physical | Measured |
|---|---|---|
| `id IN (1, 2, 99)` | K point reads on `meta` (no index) | 1.31 µs/id |
| `status IN (1, 3)` | K seeks, merge streams | 80 000 ids in 20.4 ms |
| `AND` across two indexes | sorted-merge intersection | 40k × 10k → 10k in 10.9 ms |
| `Distinct` (skip-scan) | read value, seek past its prefix, repeat | **5 values in 0.1 ms** (O(distinct), not O(entries)) |
| `Min`/`Max` | seek-first / seek-last | **O(1)** |
| `DeleteRange` (drop index) | tombstone the key range | ~1 ms |

**Skip-scan** is the key result: `Distinct` is O(distinct values), not O(entries) —
confirmed against 200 000 index entries and scaling measurements (`v2-003` artifact `skipscan-scaling.md`).
Exact facet counts walk entries under each distinct value (bounded by query `take`, per B5).
This is what makes the explorer drill-down practical.

## 10 · Index amplification and caps

Multikey paths (arrays) produce N entries per document. Nested arrays of arrays multiply
combinatorially. Measured caps (`artifacts/index-amplification.md`):

- **Single array:** linear in array length; cap at published limit (C31).
- **Nested `a[].b[].c`:** 100 × 100 = 10 000 entries for one document. Capped or
  refused at index-declaration time — before the cost is ever paid.

Encoded value size cap: **~1 KB per component.** Exceeded values are refused (never
truncated) — truncation silently makes equality queries wrong (C1 posture).
