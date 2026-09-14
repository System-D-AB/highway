using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using RocksDbSharp;
using Stow.Abstractions;
using Stow.Storage.Catalog;
using Stow.Storage.Encoding;
using Stow.Storage.Layout;
using Stow.Storage.Query;

namespace Stow.Storage.Engine;

/// <summary>
/// The read path — Get/GetMany/GetRaw/Exists operations.
/// Every read creates a snapshot scoped to the request and releases it before returning (design § 5).
/// The snapshot's sequence number becomes asOf on the result (R5.5).
/// </summary>
public sealed class ReadPath
{
    private readonly RocksDb _db;
    private readonly CatalogStore _catalog;
    private readonly ColumnFamilyHandle _metaCf;
    private readonly ColumnFamilyHandle _bodyCf;

    public ReadPath(RocksDb db, CatalogStore catalog)
    {
        _db = db;
        _catalog = catalog;
        _metaCf = catalog.MetaColumnFamily;
        _bodyCf = catalog.BodyColumnFamily;
    }

    /// <summary>
    /// Get: reads meta, then body only when separated. Returns null if document not found (R5.1).
    /// </summary>
    public ReadResult? Get(string collectionName, StowId id, string database = null)
    {
        var collection = GetCollection(collectionName, database);
        var indexes = _catalog.GetIndexes(collection.DatabaseName, collectionName);
        var docKey = DocKey.Build(collection.Code, id);

        using var snapshot = _db.CreateSnapshot();
        var readOpts = new ReadOptions().SetSnapshot(snapshot);

        long asOf = SequenceSource.ExactSnapshotSequence(snapshot);

        var metaBytes = _db.Get(docKey, _metaCf, readOpts);
        if (metaBytes == null)
            return null;

        int readyIndexCount = 0;
        for (int i = 0; i < indexes.Count; i++)
            if (indexes[i].State == IndexState.Ready || indexes[i].State == IndexState.Building) readyIndexCount++;

        var body = ExtractBody(metaBytes, docKey, readyIndexCount, readOpts);
        return new ReadResult(body, MetaRecord.ReadSequence(metaBytes), asOf);
    }


    /// <summary>
    /// GetMany/id IN: batches primary-key reads from meta under one snapshot, bypassing
    /// index selection and index cursors entirely (R1.7, R5.2). Results preserve request
    /// order; null entries indicate missing documents.
    /// </summary>
    public ReadResult?[] GetMany(string collectionName, StowId[] ids, string database = null)
    {
        var collection = GetCollection(collectionName, database);
        var indexes = _catalog.GetIndexes(collection.DatabaseName, collectionName);
        var results = new ReadResult?[ids.Length];

        if (ids.Length == 0)
            return results;

        using var snapshot = _db.CreateSnapshot();
        var readOpts = new ReadOptions().SetSnapshot(snapshot);

        long asOf = SequenceSource.ExactSnapshotSequence(snapshot);

        var docKeys = new byte[ids.Length][];
        var metaColumnFamilies = new ColumnFamilyHandle[ids.Length];
        for (int i = 0; i < ids.Length; i++)
        {
            docKeys[i] = DocKey.Build(collection.Code, ids[i]);
            metaColumnFamilies[i] = _metaCf;
        }

        var metaResults = _db.MultiGet(docKeys, metaColumnFamilies, readOpts);

        int readyIndexCount = 0;
        for (int j = 0; j < indexes.Count; j++)
            if (indexes[j].State == IndexState.Ready) readyIndexCount++;

        for (int i = 0; i < ids.Length; i++)
        {
            var metaBytes = metaResults[i].Value;
            if (metaBytes == null)
            {
                results[i] = null;
                continue;
            }

            var body = ExtractBody(metaBytes, docKeys[i], readyIndexCount, readOpts);
            results[i] = new ReadResult(body, MetaRecord.ReadSequence(metaBytes), asOf);
        }

        return results;
    }

    /// <summary>
    /// GetRaw: returns the raw document bytes without deserialisation (R5.3, V11.4 repair path).
    /// Returns null if document not found.
    /// </summary>
    public ReadResult? GetRaw(string collectionName, StowId id)
    {
        var collection = GetCollection(collectionName);
        var indexes = _catalog.GetIndexes(collection.DatabaseName, collectionName);
        var docKey = DocKey.Build(collection.Code, id);

        using var snapshot = _db.CreateSnapshot();
        var readOpts = new ReadOptions().SetSnapshot(snapshot);

        long asOf = SequenceSource.ExactSnapshotSequence(snapshot);

        var metaBytes = _db.Get(docKey, _metaCf, readOpts);
        if (metaBytes == null)
            return null;

        int readyIndexCount = 0;
        for (int j = 0; j < indexes.Count; j++)
            if (indexes[j].State == IndexState.Ready) readyIndexCount++;

        var body = ExtractBody(metaBytes, docKey, readyIndexCount, readOpts);
        return new ReadResult(body, MetaRecord.ReadSequence(metaBytes), asOf);
    }


    /// <summary>
    /// Exists: answers from meta key presence only — no body read (R5.4).
    /// </summary>
    public ExistsResult Exists(string collectionName, StowId id)
    {
        var collection = GetCollection(collectionName);
        var docKey = DocKey.Build(collection.Code, id);

        using var snapshot = _db.CreateSnapshot();
        var readOpts = new ReadOptions().SetSnapshot(snapshot);

        long asOf = SequenceSource.ExactSnapshotSequence(snapshot);

        var metaBytes = _db.Get(docKey, _metaCf, readOpts);
        return new ExistsResult(metaBytes != null, asOf);
    }

    /// <summary>
    /// Extracts the body bytes from a meta record, reading from the body CF when separated.
    /// </summary>
    private byte[] ExtractBody(byte[] metaBytes, byte[] docKey, int indexCount, ReadOptions readOpts)
    {
        // Check the IsBodyInlined flag (bit0 of flags byte at offset 1)
        byte flags = metaBytes[1];
        bool isBodyInlined = (flags & MetaRecord.FlagBodyInlined) != 0;

        if (isBodyInlined)
        {
            return MetaRecord.TryReadInlinedBody(metaBytes, indexCount, out var body) ? body : Array.Empty<byte>();
        }
        else
        {
            // Body is separated — read from body CF
            var bodyBytes = _db.Get(docKey, _bodyCf, readOpts);
            return bodyBytes ?? Array.Empty<byte>();
        }
    }

    /// <summary>
    /// Scans documents in primary key order (R6.15, E14).
    /// Works for any collection without requiring secondary indexes.
    /// </summary>
    public (IReadOnlyList<QueryItem> Items, string NextToken, long AsOf) ScanDocuments(
        string collectionName,
        int take = 100,
        string after = null,
        string database = null)
    {
        var collection = GetCollection(collectionName, database);
        var indexes = _catalog.GetIndexes(collection.DatabaseName, collectionName);

        using var snapshot = _db.CreateSnapshot();
        long asOf = SequenceSource.ExactSnapshotSequence(snapshot);
        var readOpts = new ReadOptions().SetSnapshot(snapshot);

        Span<byte> prefixBuf = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(prefixBuf, collection.Code);
        byte[] collPrefix = prefixBuf.ToArray();

        byte[] startKey;
        if (!string.IsNullOrWhiteSpace(after))
        {
            var afterToken = PagingToken.Decode(after);
            startKey = DocKey.Build(collection.Code, afterToken.LastId);
        }
        else
        {
            startKey = collPrefix;
        }

        using var iter = _db.NewIterator(_metaCf, readOpts);
        iter.Seek(startKey);

        if (!string.IsNullOrWhiteSpace(after) && iter.Valid())
        {
            iter.Next();
        }

        int readyIndexCount = 0;
        for (int j = 0; j < indexes.Count; j++)
            if (indexes[j].State == IndexState.Ready) readyIndexCount++;

        var items = new List<QueryItem>();
        StowId lastId = default;

        while (items.Count < take && iter.Valid())
        {
            var key = iter.Key();
            if (key.Length < 4 || !key.AsSpan().StartsWith(collPrefix))
                break;

            var idBytes = key.AsSpan(4);
            var id = IdDecoder.Decode(idBytes, collection.IdType);
            var metaBytes = iter.Value();
            var body = ExtractBody(metaBytes, key, readyIndexCount, readOpts);

            items.Add(new QueryItem(id, body, MetaRecord.ReadSequence(metaBytes), asOf));
            lastId = id;
            iter.Next();
        }


        string nextToken = null;
        if (iter.Valid() && iter.Key().Length >= 4 && iter.Key().AsSpan().StartsWith(collPrefix) && lastId != default)
        {
            var token = new PagingToken(asOf, DocKey.Build(collection.Code, lastId), lastId);
            nextToken = token.Encode();
        }

        return (items, nextToken, asOf);

    }

    /// <summary>
    /// Resolves a collection, scoped when the caller knows its database (018 R3.1).
    ///
    /// A null database resolves by name across the instance, which is unambiguous only while a
    /// name is unique — <see cref="CatalogStore.GetCollection(string)"/> throws rather than guess
    /// when it is not. The engine always supplies one.
    /// </summary>
    private CollectionRecord GetCollection(string name, string database = null)
    {
        var record = database == null
            ? _catalog.GetCollection(name)
            : _catalog.GetCollection(database, name);

        return record ?? throw new InvalidOperationException(
            database == null
                ? $"Collection '{name}' does not exist."
                : $"Collection '{name}' does not exist in database '{database}'.");
    }
}

