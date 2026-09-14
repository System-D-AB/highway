using System;
using System.Buffers.Binary;
using Stow.Abstractions;
using Stow.Storage.Encoding;

namespace Stow.Storage.Layout;

/// <summary>
/// Builds document keys for the meta and body column families.
/// Format: &lt;coll:4&gt; &lt;encoded id&gt;
/// The collection code is a 4-byte globally unique value (V15).
/// </summary>
public static class DocKey
{
    /// <summary>
    /// Writes a document key into the provided KeyWriter.
    /// </summary>
    public static void Write(uint collectionCode, StowId id, ref KeyWriter writer)
    {
        // <coll:4> — big-endian for consistent prefix scans
        Span<byte> coll = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(coll, collectionCode);
        writer.WriteBytes(coll);

        // <encoded id>
        IdEncoder.Write(id, ref writer);
    }

    /// <summary>
    /// Builds a document key as a byte array.
    /// </summary>
    public static byte[] Build(uint collectionCode, StowId id)
    {
        Span<byte> buffer = stackalloc byte[128];
        var writer = new KeyWriter(buffer);
        Write(collectionCode, id, ref writer);
        return writer.ToArray();
    }
}
