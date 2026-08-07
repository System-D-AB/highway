using System.Buffers.Binary;

namespace Highway.Server.Internal;

/// <summary>
/// Binary framing for a node's registration record (<c>hw:reg:node:{nodeId}</c>).
///
/// <code>
/// [i64 BE seenTicksUtc][catalog json bytes]
/// </code>
///
/// <para>Binary rather than JSON deliberately: the <c>HW.HEARTBEAT</c> liveness
/// form must refresh <c>seen</c> while leaving the catalog byte-for-byte
/// untouched (Requirement 1 AC2, AC8). With a fixed 8-byte header that is a
/// copy of the tail; with a JSON envelope it would mean parsing and re-emitting
/// the catalog on every beat — exactly the cost this design exists to remove.</para>
///
/// <para>Consistent with <see cref="Envelope"/>: all multi-byte integers are
/// big-endian (network byte order).</para>
/// </summary>
internal static class NodeRegistration
{
    /// <summary>Size of the fixed header preceding the catalog bytes.</summary>
    public const int HeaderSize = 8;

    /// <summary>Encodes a registration record.</summary>
    public static byte[] Encode(long seenTicks, ReadOnlySpan<byte> catalog)
    {
        var buf = new byte[HeaderSize + catalog.Length];
        BinaryPrimitives.WriteInt64BigEndian(buf, seenTicks);
        catalog.CopyTo(buf.AsSpan(HeaderSize));
        return buf;
    }

    /// <summary>
    /// Decodes a registration record in place (zero allocation). The catalog is
    /// returned as a slice of <paramref name="data"/> — copy it if it must
    /// outlive the caller's buffer.
    /// </summary>
    public static void Decode(ReadOnlySpan<byte> data, out long seenTicks, out ReadOnlySpan<byte> catalog)
    {
        if (data.Length < HeaderSize)
            throw new InvalidDataException(
                $"Registration record too short ({data.Length} bytes); minimum is {HeaderSize}.");

        seenTicks = BinaryPrimitives.ReadInt64BigEndian(data);
        catalog = data[HeaderSize..];
    }

    /// <summary>
    /// Rewrites only the <c>seen</c> timestamp, preserving the catalog bytes
    /// exactly. This is the whole of the liveness-form write path.
    /// </summary>
    public static byte[] Touch(ReadOnlySpan<byte> existing, long seenTicks)
    {
        Decode(existing, out _, out var catalog);
        return Encode(seenTicks, catalog);
    }

    /// <summary>
    /// True when the record's last-seen timestamp is older than
    /// <paramref name="expiry"/> relative to <paramref name="nowTicks"/>.
    /// An expiry of <see cref="TimeSpan.Zero"/> or less means never stale.
    /// </summary>
    public static bool IsStale(long seenTicks, long nowTicks, TimeSpan expiry)
        => expiry > TimeSpan.Zero && nowTicks - seenTicks > expiry.Ticks;

    /// <summary>Convenience: decodes and evaluates staleness in one step.</summary>
    public static bool IsStale(ReadOnlySpan<byte> record, long nowTicks, TimeSpan expiry)
    {
        Decode(record, out var seen, out _);
        return IsStale(seen, nowTicks, expiry);
    }

    /// <summary>Age of the record, floored at zero (clock skew tolerance).</summary>
    public static TimeSpan Age(long seenTicks, long nowTicks)
        => nowTicks <= seenTicks ? TimeSpan.Zero : TimeSpan.FromTicks(nowTicks - seenTicks);
}
