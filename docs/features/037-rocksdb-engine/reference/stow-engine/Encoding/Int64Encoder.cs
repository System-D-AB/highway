using System;
using System.Buffers.Binary;

namespace Stow.Storage.Encoding;

/// <summary>
/// Order-preserving encoder for signed integers.
/// Sign bit is flipped so negatives sort below positives in unsigned byte comparison.
/// Big-endian for lexicographic byte order = numeric order.
/// </summary>
public static class Int64Encoder
{
    public const int Version = 1;

    public static void Write(long value, ref KeyWriter writer)
    {
        // Flip sign bit: makes negatives sort below positives.
        ulong encoded = (ulong)value ^ 0x8000_0000_0000_0000UL;
        Span<byte> buf = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(buf, encoded);
        writer.WriteBytes(buf);
    }

    public static void Write(int value, ref KeyWriter writer)
    {
        uint encoded = (uint)value ^ 0x8000_0000U;
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buf, encoded);
        writer.WriteBytes(buf);
    }

    public static void Write(short value, ref KeyWriter writer)
    {
        ushort encoded = (ushort)((ushort)value ^ 0x8000);
        Span<byte> buf = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(buf, encoded);
        writer.WriteBytes(buf);
    }
}
