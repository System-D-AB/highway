using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Threading;
using RocksDbSharp;
using Stow.Abstractions;
using Stow.Storage.Catalog;
using Stow.Storage.Encoding;
using Stow.Storage.Layout;

namespace Stow.Storage.Engine;

/// <summary>
/// The write path — Save/Insert/Update/Delete as atomic WriteBatch operations.
/// All writes go through this class. No direct Put calls outside a batch (design § 4).
/// </summary>
public sealed class WritePath
{
    private readonly RocksDb _db;
    private readonly CatalogStore _catalog;
    private readonly StripedLock _lock;
    private readonly InlineThreshold _inlineThreshold;
    private readonly ColumnFamilyHandle _metaCf;
    private readonly ColumnFamilyHandle _bodyCf;
    private readonly ColumnFamilyHandle _indexCf;
    private readonly ColumnFamilyHandle _uniqCf;
    private readonly ColumnFamilyHandle _catalogCf;

    private readonly WriteOptions _writeOptions;

    public WritePath(
        RocksDb db,
        CatalogStore catalog,
        StripedLock stripedLock,
        InlineThreshold inlineThreshold,
        DurabilityMode durabilityMode = DurabilityMode.Buffered)
    {
        _db = db;
        _catalog = catalog;
        _lock = stripedLock;
        _inlineThreshold = inlineThreshold;
        _metaCf = catalog.MetaColumnFamily;
        _bodyCf = catalog.BodyColumnFamily;
        _indexCf = catalog.IndexColumnFamily;
        _uniqCf = catalog.UniqColumnFamily;
        _catalogCf = catalog.CatalogColumnFamily;
        _writeOptions = new WriteOptions().SetSync(durabilityMode == DurabilityMode.Strict);
    }

    /// <summary>
    /// Save (upsert): create if absent, update if present. Optional CAS via expectedSequence.
    /// </summary>
    public SaveResult Save(string collectionName, StowId id, byte[] document, long? expectedSequence = null, string database = null)
    {
        var collection = GetCollection(collectionName, database);
        var indexes = _catalog.GetIndexes(collection.DatabaseName, collectionName);
        var docKey = DocKey.Build(collection.Code, id);
        var encodedId = EncodeId(id);

        // Extract new indexed values (before locking — pure computation)
        var newEncodedValues = ExtractAllIndexedValues(document, indexes, id);

        // Acquire all required locks in a consistent order (R4.5).
        // Includes the document stripe AND all unique value stripes to prevent lost updates.
        using var locks = AcquireAllLocks(collection.Code, id, indexes, newEncodedValues);

        // Read previous meta
        var metaBytes = _db.Get(docKey, _metaCf);
        MetaRecord? previousMeta = null;
        List<StaleEntry> staleEntries;

        if (metaBytes != null)
        {
            staleEntries = StaleEntryComputer.ComputeFromMeta(metaBytes, indexes, out previousMeta);
        }
        else
        {
            staleEntries = new List<StaleEntry>();
        }

        // CAS guard
        if (expectedSequence.HasValue)
        {
            long actualSeq = previousMeta?.Sequence ?? 0;
            if (actualSeq != expectedSequence.Value)
                throw new ConcurrencyException(id, expectedSequence.Value, actualSeq);
        }

        // Check unique constraints (now safe — we hold the unique key locks)
        CheckUniqueConstraints(collection.Code, id, encodedId, indexes, newEncodedValues);

        // Determine outcome
        var outcome = previousMeta.HasValue ? WriteOutcome.Updated : WriteOutcome.Inserted;
        long delta = previousMeta.HasValue ? 0 : 1; // Only increment for new documents

        // Build and write the batch
        long storedSequence = WriteDocumentBatch(collection, id, docKey, encodedId, document, indexes,
            staleEntries, newEncodedValues, delta, null, previousMeta?.Sequence ?? 0);

        return new SaveResult(storedSequence, SequenceSource.EngineSequenceAfterCommit(_db), outcome);
    }

    /// <summary>
    /// Insert: fail if document already exists.
    /// </summary>
    public SaveResult Insert(string collectionName, StowId id, byte[] document, long? expectedSequence = null, string database = null)
    {
        var collection = GetCollection(collectionName, database);
        var indexes = _catalog.GetIndexes(collection.DatabaseName, collectionName);
        var docKey = DocKey.Build(collection.Code, id);
        var encodedId = EncodeId(id);

        // Extract new indexed values (before locking — pure computation)
        var newEncodedValues = ExtractAllIndexedValues(document, indexes, id);

        // Acquire all required locks in a consistent order (R4.5).
        using var locks = AcquireAllLocks(collection.Code, id, indexes, newEncodedValues);

        // Read previous meta — must NOT exist
        var metaBytes = _db.Get(docKey, _metaCf);
        if (metaBytes != null)
            throw new DocumentExistsException(id, alreadyExists: true);

        // CAS guard (on Insert, expectedSequence should be 0 if provided)
        if (expectedSequence.HasValue && expectedSequence.Value != 0)
            throw new ConcurrencyException(id, expectedSequence.Value, 0);

        // Check unique constraints
        CheckUniqueConstraints(collection.Code, id, encodedId, indexes, newEncodedValues);

        // Build and write the batch — delta = +1 for insert
        long storedSequence = WriteDocumentBatch(collection, id, docKey, encodedId, document, indexes,
            new List<StaleEntry>(), newEncodedValues, 1, null, previousRevision: 0);

        return new SaveResult(storedSequence, SequenceSource.EngineSequenceAfterCommit(_db), WriteOutcome.Inserted);
    }

    /// <summary>
    /// Update: fail if document does NOT exist.
    /// </summary>
    public SaveResult Update(string collectionName, StowId id, byte[] document, long? expectedSequence = null, string database = null)
    {
        var collection = GetCollection(collectionName, database);
        var indexes = _catalog.GetIndexes(collection.DatabaseName, collectionName);
        var docKey = DocKey.Build(collection.Code, id);
        var encodedId = EncodeId(id);

        // Extract new indexed values (before locking — pure computation)
        var newEncodedValues = ExtractAllIndexedValues(document, indexes, id);

        // Acquire all required locks in a consistent order (R4.5).
        using var locks = AcquireAllLocks(collection.Code, id, indexes, newEncodedValues);

        // Read previous meta — must exist
        var metaBytes = _db.Get(docKey, _metaCf);
        if (metaBytes == null)
            throw new DocumentExistsException(id, alreadyExists: false);

        var staleEntries = StaleEntryComputer.ComputeFromMeta(metaBytes, indexes, out var previousMeta);

        // CAS guard
        if (expectedSequence.HasValue)
        {
            long actualSeq = previousMeta?.Sequence ?? 0;
            if (actualSeq != expectedSequence.Value)
                throw new ConcurrencyException(id, expectedSequence.Value, actualSeq);
        }

        // Check unique constraints
        CheckUniqueConstraints(collection.Code, id, encodedId, indexes, newEncodedValues);

        // Build and write the batch — delta = 0 for update (count doesn't change)
        long storedSequence = WriteDocumentBatch(collection, id, docKey, encodedId, document, indexes,
            staleEntries, newEncodedValues, 0, null, previousMeta?.Sequence ?? 0);

        return new SaveResult(storedSequence, SequenceSource.EngineSequenceAfterCommit(_db), WriteOutcome.Updated);
    }

    /// <summary>
    /// Delete: removes document, body, all index entries, unique claims, and decrements counter.
    /// Fails if document doesn't exist.
    /// </summary>
    public SaveResult Delete(string collectionName, StowId id, string database = null)
    {
        var collection = GetCollection(collectionName, database);
        var indexes = _catalog.GetIndexes(collection.DatabaseName, collectionName);
        var docKey = DocKey.Build(collection.Code, id);
        var encodedId = EncodeId(id);

        using var _ = _lock.Lock(collection.Code, id);

        // Read previous meta — must exist
        var metaBytes = _db.Get(docKey, _metaCf);
        if (metaBytes == null)
            throw new DocumentExistsException(id, alreadyExists: false);

        var staleEntries = StaleEntryComputer.ComputeFromMeta(metaBytes, indexes, out var previousMeta);

        // Build delete batch
        using var batch = new global::RocksDbSharp.WriteBatch();

        // Delete stale index entries
        foreach (var stale in staleEntries)
        {
            IndexBuilder.DeleteIndexEntry(collection.Code, stale.Index, stale.EncodedValues, encodedId, batch, _indexCf, _uniqCf);
        }

        // Delete meta
        batch.Delete(docKey, _metaCf);

        // Delete body (always — it may or may not exist in body CF, Delete is idempotent)
        batch.Delete(docKey, _bodyCf);

        // Counter decrement
        var counterKey = CounterMergeOperator.BuildCounterKey(collectionName);
        var decrementDelta = CounterMergeOperator.EncodeDelta(-1);
        batch.Merge(counterKey, decrementDelta, _catalogCf);

        _db.Write(batch, _writeOptions);

        // Read AFTER commit: a safe lower bound on the engine position, for read-your-writes.
        // A deleted document has no revision; only the engine position is meaningful.
        long sequence = SequenceSource.EngineSequenceAfterCommit(_db);
        return new SaveResult(revision: 0, engineSequence: sequence, WriteOutcome.Deleted);
    }

    /// <summary>
    /// SaveMany: N independent save operations with per-document results.
    /// Individual failures don't abort other operations.
    /// </summary>
    public DocumentResult[] SaveMany(string collectionName, (StowId id, byte[] document)[] documents)
    {
        var results = new DocumentResult[documents.Length];
        for (int i = 0; i < documents.Length; i++)
        {
            try
            {
                var result = Save(collectionName, documents[i].id, documents[i].document);
                results[i] = DocumentResult.Success(documents[i].id, result);
            }
            catch (StowException ex)
            {
                results[i] = DocumentResult.Failure(documents[i].id, ex);
            }
        }
        return results;
    }

    /// <summary>
    /// InsertMany: N independent insert operations with per-document results.
    /// </summary>
    public DocumentResult[] InsertMany(string collectionName, (StowId id, byte[] document)[] documents)
    {
        var results = new DocumentResult[documents.Length];
        for (int i = 0; i < documents.Length; i++)
        {
            try
            {
                var result = Insert(collectionName, documents[i].id, documents[i].document);
                results[i] = DocumentResult.Success(documents[i].id, result);
            }
            catch (StowException ex)
            {
                results[i] = DocumentResult.Failure(documents[i].id, ex);
            }
        }
        return results;
    }

    /// <summary>
    /// DeleteMany: N independent delete operations with per-document results.
    /// </summary>
    public DocumentResult[] DeleteMany(string collectionName, StowId[] ids)
    {
        var results = new DocumentResult[ids.Length];
        for (int i = 0; i < ids.Length; i++)
        {
            try
            {
                var result = Delete(collectionName, ids[i]);
                results[i] = DocumentResult.Success(ids[i], result);
            }
            catch (StowException ex)
            {
                results[i] = DocumentResult.Failure(ids[i], ex);
            }
        }
        return results;
    }

    /// <summary>
    /// Builds and writes the atomic WriteBatch for a save/insert/update operation.
    /// Returns the sequence number stored in the meta record (used as CAS token).
    /// </summary>
    private long WriteDocumentBatch(
        CollectionRecord collection,
        StowId id,
        byte[] docKey,
        byte[] encodedId,
        byte[] document,
        IReadOnlyList<IndexDefinition> indexes,
        List<StaleEntry> staleEntries,
        IndexedValueSet newValues,
        long counterDelta,
        long? ttlTicks,
        long previousRevision)
    {
        using var batch = new global::RocksDbSharp.WriteBatch();

        // 1. Delete stale index entries
        foreach (var stale in staleEntries)
        {
            IndexBuilder.DeleteIndexEntry(collection.Code, stale.Index, stale.EncodedValues, encodedId, batch, _indexCf, _uniqCf);
        }

        // 2. Build the indexed values block for the meta record
        var indexedValuesBlock = BuildIndexedValuesBlock(newValues);

        // 3. Determine body inlining
        bool shouldInline = _inlineThreshold.ShouldInline(document.Length);

        // 4. Write meta record
        //
        // The document revision is a PER-DOCUMENT counter, not the engine sequence.
        //
        // The engine sequence is global, so predicting it (GetLatestSequenceNumber() + 1) is
        // unsound however carefully this document is locked: the striped lock guards the
        // document, not the counter, and a concurrent write to any other document advances it.
        // That was defect B1.
        //
        // A revision needs no global uniqueness and no global order - only to differ from this
        // document's previous value. That is exactly what the lock we already hold guarantees.
        // See the V8 amendment (2026-08-14).
        long writeSequence = previousRevision + 1;

        int metaSize = MetaRecord.MinHeaderSize
            + (ttlTicks.HasValue ? MetaRecord.TtlSize : 0)
            + indexedValuesBlock.Length
            + (shouldInline ? document.Length : 0);

        var metaBuffer = new byte[metaSize];
        MetaRecord.Write(
            metaBuffer,
            sequence: writeSequence,
            catalogGeneration: _catalog.Generation,
            ttlTicks: ttlTicks,
            indexedValues: indexedValuesBlock,
            inlinedBody: shouldInline ? document : ReadOnlySpan<byte>.Empty);

        batch.Put(docKey, metaBuffer, _metaCf);

        // 5. Write body (if over inline threshold)
        if (!shouldInline)
        {
            batch.Put(docKey, document, _bodyCf);
        }
        else
        {
            // Delete any previously separated body
            batch.Delete(docKey, _bodyCf);
        }

        // 6. Write new index entries
        for (int i = 0; i < newValues.Count; i++)
        {
            var entry = newValues[i];
            if (entry.EncodedValues == null)
                continue; // Missing field — no entry

            IndexBuilder.WriteIndexEntry(collection.Code, entry.Index, entry.EncodedValues, encodedId, batch, _indexCf, _uniqCf);
        }

        // 7. Counter merge (increment for insert, no change for update)
        if (counterDelta != 0)
        {
            var counterKey = CounterMergeOperator.BuildCounterKey(collection.Name);
            var deltaBytes = CounterMergeOperator.EncodeDelta(counterDelta);
            batch.Merge(counterKey, deltaBytes, _catalogCf);
        }

        // Atomic write
        _db.Write(batch, _writeOptions);
        return writeSequence;
    }

    /// <summary>
    /// Acquires all required striped locks for a write operation in a consistent order (R4.5).
    /// Includes the document stripe AND all unique value stripes.
    /// Locks are acquired in ascending stripe order to prevent deadlocks.
    /// Duplicate stripes (document stripe = a unique stripe) are acquired only once.
    /// </summary>
    private CompositeLease AcquireAllLocks(
        uint collectionCode,
        StowId id,
        IReadOnlyList<IndexDefinition> indexes,
        IndexedValueSet newValues)
    {
        // Collect all required stripe indices
        var stripeSet = new SortedSet<int>();

        // Document stripe
        int docStripe = _lock.GetStripeIndex(collectionCode, id);
        stripeSet.Add(docStripe);

        // Unique value stripes
        for (int i = 0; i < newValues.Count; i++)
        {
            var entry = newValues[i];
            if (!entry.Index.IsUnique || entry.EncodedValues == null)
                continue;

            var uniqKey = UniqKey.Build(collectionCode, entry.Index.Id, entry.EncodedValues);
            int hash = GetUniqKeyHash(uniqKey);
            int stripe = (hash & 0x7FFF_FFFF) % _lock.StripeCount;
            stripeSet.Add(stripe);
        }

        // Acquire in sorted order (prevents deadlocks)
        var leases = new List<IDisposable>(stripeSet.Count);
        foreach (int stripe in stripeSet)
        {
            leases.Add(_lock.LockByStripe(stripe));
        }

        return new CompositeLease(leases);
    }

    private static int GetUniqKeyHash(byte[] key)
    {
        unchecked
        {
            int hash = 17;
            for (int i = 0; i < key.Length; i++)
                hash = hash * 31 + key[i];
            return hash;
        }
    }

    /// <summary>
    /// Checks unique constraints under the striped lock (E7).
    /// For each unique index, reads the current holder from the uniq CF.
    /// If it exists and the holder differs from this document, throws UniqueConstraintException.
    /// </summary>
    private void CheckUniqueConstraints(
        uint collectionCode,
        StowId documentId,
        byte[] encodedDocId,
        IReadOnlyList<IndexDefinition> indexes,
        IndexedValueSet newValues)
    {
        for (int i = 0; i < newValues.Count; i++)
        {
            var entry = newValues[i];
            if (!entry.Index.IsUnique || entry.EncodedValues == null)
                continue;

            // Build the unique key: <coll:4><idx:2><encoded values>
            var uniqKey = UniqKey.Build(collectionCode, entry.Index.Id, entry.EncodedValues);

            // Read current holder
            var existingHolder = _db.Get(uniqKey, _uniqCf);
            if (existingHolder != null)
            {
                // Check if the holder is a different document
                if (!SpanEquals(existingHolder, encodedDocId))
                {
                    // Decode the holder id for the error message
                    var holderId = DecodeHolderId(existingHolder);
                    throw new UniqueConstraintException(
                        documentId,
                        entry.Index.Name,
                        Convert.ToBase64String(entry.EncodedValues),
                        holderId);
                }
            }
        }
    }

    /// <summary>
    /// Extracts indexed values for all active indexes from the document.
    /// </summary>
    private IndexedValueSet ExtractAllIndexedValues(
        byte[] document, IReadOnlyList<IndexDefinition> indexes, StowId documentId)
    {
        var values = new IndexedValue[indexes.Count];
        int count = 0;

        for (int i = 0; i < indexes.Count; i++)
        {
            var index = indexes[i];

            // Ready **and Building**. A building index must be maintained from the moment it is
            // declared, or every write during the build is lost from it permanently - the index
            // is then marked Ready and answers queries with documents missing. It was skipped
            // here (review finding B2), and the window was the whole build.
            if (index.State != IndexState.Ready && index.State != IndexState.Building)
                continue;

            var encoded = ValueExtractor.Extract(document, index, documentId);
            values[count++] = new IndexedValue(index, encoded);
        }

        return new IndexedValueSet(values, count);
    }

    /// <summary>
    /// Builds the indexed values block for the meta record.
    /// Format: for each index (in order): &lt;len:2&gt;&lt;encoded bytes&gt;
    /// len=0 means field was missing (no entry).
    /// </summary>
    private byte[] BuildIndexedValuesBlock(IndexedValueSet values)
    {
        int totalSize = 0;
        for (int i = 0; i < values.Count; i++)
        {
            totalSize += 2; // length prefix
            if (values[i].EncodedValues != null)
                totalSize += values[i].EncodedValues.Length;
        }

        var block = new byte[totalSize];
        int pos = 0;

        for (int i = 0; i < values.Count; i++)
        {
            var encoded = values[i].EncodedValues;
            ushort len = encoded != null ? (ushort)encoded.Length : (ushort)0;
            BinaryPrimitives.WriteUInt16BigEndian(block.AsSpan(pos, 2), len);
            pos += 2;

            if (encoded != null)
            {
                encoded.CopyTo(block, pos);
                pos += encoded.Length;
            }
        }

        return block;
    }

    /// <summary>
    /// Resolves a collection, scoped when the caller knows its database (018 R3.1). A null
    /// database resolves by name and throws on an ambiguous one rather than picking.
    /// </summary>
    private CollectionRecord GetCollection(string name, string database = null)
    {
        return (database == null ? _catalog.GetCollection(name) : _catalog.GetCollection(database, name))
            ?? throw new InvalidOperationException($"Collection '{name}' does not exist.");
    }

    private static byte[] EncodeId(StowId id)
    {
        Span<byte> buffer = stackalloc byte[128];
        var writer = new KeyWriter(buffer);
        IdEncoder.Write(id, ref writer);
        return writer.ToArray();
    }

    private static bool SpanEquals(byte[] a, byte[] b)
    {
        return a.AsSpan().SequenceEqual(b.AsSpan());
    }

    private static StowId DecodeHolderId(byte[] encodedId)
    {
        // Best effort decode: try as Guid (16 bytes), then long (8 bytes), then string
        if (encodedId.Length == 16)
        {
            // GuidEncoder writes in big-endian field order via TryWriteBytes(bigEndian: true)
            // So we need to read it back with big-endian interpretation
            return StowId.From(new Guid(encodedId, bigEndian: true));
        }
        else if (encodedId.Length == 8)
        {
            // Could be a long with sign bit flipped
            ulong raw = BinaryPrimitives.ReadUInt64BigEndian(encodedId);
            long value = (long)(raw ^ 0x8000_0000_0000_0000UL);
            return StowId.From(value);
        }
        else
        {
            // String — decode by removing escape sequences and terminator
            return StowId.From(DecodeStringId(encodedId));
        }
    }

    private static string DecodeStringId(byte[] encoded)
    {
        // Reverse the string encoding: remove 0x00 0xFF escapes and 0x00 0x00 terminator
        var bytes = new List<byte>(encoded.Length);
        for (int i = 0; i < encoded.Length; i++)
        {
            if (encoded[i] == 0x00)
            {
                if (i + 1 < encoded.Length)
                {
                    if (encoded[i + 1] == 0xFF)
                    {
                        bytes.Add(0x00);
                        i++; // skip the 0xFF
                    }
                    else if (encoded[i + 1] == 0x00)
                    {
                        break; // terminator
                    }
                }
            }
            else
            {
                bytes.Add(encoded[i]);
            }
        }
        return System.Text.Encoding.UTF8.GetString(bytes.ToArray());
    }
}

/// <summary>
/// An indexed value for a single index — the encoded bytes (or null if field was missing).
/// </summary>
internal readonly struct IndexedValue
{
    public IndexedValue(IndexDefinition index, byte[] encodedValues)
    {
        Index = index;
        EncodedValues = encodedValues;
    }

    public IndexDefinition Index { get; }
    public byte[] EncodedValues { get; }
}

/// <summary>
/// A set of indexed values for all active indexes in a collection.
/// </summary>
internal readonly struct IndexedValueSet
{
    private readonly IndexedValue[] _values;
    private readonly int _count;

    public IndexedValueSet(IndexedValue[] values, int count)
    {
        _values = values;
        _count = count;
    }

    public int Count => _count;
    public IndexedValue this[int index] => _values[index];
}

/// <summary>
/// Holds multiple disposable lock leases and disposes them all together.
/// Used to release all unique constraint locks at once when the save completes.
/// </summary>
internal sealed class CompositeLease : IDisposable
{
    private List<IDisposable> _leases;

    public CompositeLease(List<IDisposable> leases)
    {
        _leases = leases;
    }

    public void Dispose()
    {
        var leases = Interlocked.Exchange(ref _leases, null);
        if (leases == null) return;

        // Release in reverse order to maintain lock ordering discipline
        for (int i = leases.Count - 1; i >= 0; i--)
        {
            leases[i].Dispose();
        }
    }
}
