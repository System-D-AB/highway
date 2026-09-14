using System.Buffers.Binary;
using System.Text;

namespace Highway.Server.Storage.Layout;

/// <summary>
/// Order-preserving encoders for the three suffix shapes the keyspace needs: a
/// self-delimiting string component (names and members), a signed score (ordered-set
/// suffix), and an unsigned sequence (list suffix).
///
/// <para>Every encoder produces bytes whose <b>bytewise comparison equals the value's
/// natural order</b>, so a RocksDB prefix iterate returns entries in FIFO / by-score
/// order without a custom comparator. This is Layer A, ported from <c>stow-rocksdb</c>
/// (<c>reference/stow-engine/Encoding/StringEncoder.cs</c> and <c>Int64Encoder.cs</c>);
/// Highway needs only this subset.</para>
/// </summary>
internal static class KeyEncoding
{
    // -------------------------------------------------------------------------
    // String component — self-delimiting, ordinal order preserved.
    //   0x00 in the UTF-8 escapes to 0x00 0xFF; the component terminates with 0x00 0x00.
    //   This is what makes a compound key safe: ("ab","c") != ("a","bc"), and no name
    //   can contain the delimiter because the delimiter is an escaped sentinel.
    // -------------------------------------------------------------------------

    /// <summary>Writes <paramref name="value"/> as an escaped, terminated UTF-8 component.</summary>
    public static void WriteString(string value, ref KeyWriter writer)
    {
        var raw = Encoding.UTF8.GetBytes(value);
        WriteEscaped(raw, ref writer);
        // Terminator 0x00 0x00 — distinct from the 0x00 0xFF escape, so it can never
        // appear mid-component.
        writer.WriteByte(0x00);
        writer.WriteByte(0x00);
    }

    private static void WriteEscaped(ReadOnlySpan<byte> raw, ref KeyWriter writer)
    {
        foreach (var b in raw)
        {
            if (b == 0x00)
            {
                writer.WriteByte(0x00);
                writer.WriteByte(0xFF);
            }
            else
            {
                writer.WriteByte(b);
            }
        }
    }

    // -------------------------------------------------------------------------
    // Signed score — the ordered-set (z-family) suffix.
    //   Sign bit flipped so negatives sort below positives, big-endian so
    //   lexicographic byte order equals numeric order. A .NET tick count is a signed
    //   long; this keeps by-score range scans correct across the whole range.
    // -------------------------------------------------------------------------

    /// <summary>Writes <paramref name="score"/> (a signed i64, e.g. a tick count) order-preservingly.</summary>
    public static void WriteScore(long score, ref KeyWriter writer)
    {
        var encoded = (ulong)score ^ 0x8000_0000_0000_0000UL;
        Span<byte> buf = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(buf, encoded);
        writer.WriteBytes(buf);
    }

    /// <summary>Reads back a score written by <see cref="WriteScore"/>.</summary>
    public static long ReadScore(ReadOnlySpan<byte> encoded)
    {
        var raw = BinaryPrimitives.ReadUInt64BigEndian(encoded);
        return (long)(raw ^ 0x8000_0000_0000_0000UL);
    }

    // -------------------------------------------------------------------------
    // Sequence — the list (q-family) suffix.
    //   A list only ever appends at the tail and (for redeliver-to-head) prepends at
    //   the head, so the sequence space is signed: the tail grows up from 0, the head
    //   grows down below 0. Encoded exactly like a score so the two directions order
    //   correctly against each other and against 0.
    // -------------------------------------------------------------------------

    /// <summary>Writes a list sequence number order-preservingly (signed: head &lt; 0 &lt; tail).</summary>
    public static void WriteSequence(long seq, ref KeyWriter writer) => WriteScore(seq, ref writer);

    /// <summary>Reads back a sequence written by <see cref="WriteSequence"/>.</summary>
    public static long ReadSequence(ReadOnlySpan<byte> encoded) => ReadScore(encoded);
}
