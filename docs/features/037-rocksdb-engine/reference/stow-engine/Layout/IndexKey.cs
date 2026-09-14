using System;
using System.Buffers.Binary;
using Stow.Abstractions;
using Stow.Storage.Encoding;

namespace Stow.Storage.Layout;

/// <summary>
/// Builds index keys for the index column family (ordinary, non-unique indexes).
/// Format: &lt;coll:4&gt; &lt;idx:2&gt; &lt;encoded value&gt;… &lt;encoded id&gt;
///
/// The id at the end makes each entry unique (multiple documents can share
/// an index value) and provides id-ordering within a value prefix.
/// </summary>
public static class IndexKey
{
    /// <summary>
    /// Writes an index key into the provided KeyWriter.
    /// The encodedValues span must already contain the properly encoded index values.
    /// </summary>
    public static void Write(uint collectionCode, ushort indexId, ReadOnlySpan<byte> encodedValues, StowId documentId, ref KeyWriter writer)
    {
        // <coll:4>
        Span<byte> coll = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(coll, collectionCode);
        writer.WriteBytes(coll);

        // <idx:2>
        Span<byte> idx = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(idx, indexId);
        writer.WriteBytes(idx);

        // <encoded values>
        if (encodedValues.Length > 0)
            writer.WriteBytes(encodedValues);

        // <encoded id>
        IdEncoder.Write(documentId, ref writer);
    }

    /// <summary>
    /// Builds an index key as a byte array.
    /// </summary>
    public static byte[] Build(uint collectionCode, ushort indexId, ReadOnlySpan<byte> encodedValues, StowId documentId)
    {
        Span<byte> buffer = stackalloc byte[256];
        var writer = new KeyWriter(buffer);
        Write(collectionCode, indexId, encodedValues, documentId, ref writer);
        return writer.ToArray();
    }
}
