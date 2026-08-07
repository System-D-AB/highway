using System.Buffers.Binary;
using System.Text;

namespace Highway.Server.Internal;

/// <summary>
/// Framing for dead-letter entries (feature 013).
///
/// <code>
/// [i64 deadLetteredTicksUtc][u16 attempts][u16 reasonLen][reason][original entry bytes]
/// </code>
///
/// <para><b>The original entry is kept whole.</b> An entry that reaches the dead-letter
/// list stripped of its payload and identifiers has been thrown away with extra steps —
/// the entire point is that someone can work out <i>why</i> it failed, and replay it once
/// they have. <c>HW.DLQ REQUEUE</c> pushes the original bytes straight back onto the live
/// queue with the attempt count reset.</para>
///
/// <para><b>The reason is a short code, not a message.</b> Today only
/// <see cref="MaxAttempts"/> exists, but carrying a code means a future cause can be added
/// without another framing change — the thing this feature had to break once and should
/// not have to break again.</para>
///
/// <para>No version byte: this list did not exist before feature 013, so there are no
/// legacy entries to distinguish. The entry it wraps carries its own version.</para>
/// </summary>
internal static class DeadLetter
{
    /// <summary>Delivery attempts were exhausted.</summary>
    public const string MaxAttempts = "MAX_ATTEMPTS";

    private const int HeaderSize = 8 + 2 + 2;

    /// <summary>Wraps a live-queue entry as a dead letter.</summary>
    public static byte[] Encode(
        long deadLetteredTicks,
        ushort attempts,
        string reason,
        ReadOnlySpan<byte> originalEntry)
    {
        var reasonBytes = Encoding.UTF8.GetBytes(reason);
        if (reasonBytes.Length > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(reason),
                $"reason length {reasonBytes.Length} exceeds u16 max ({ushort.MaxValue}).");

        var buf = new byte[HeaderSize + reasonBytes.Length + originalEntry.Length];
        BinaryPrimitives.WriteInt64BigEndian(buf, deadLetteredTicks);
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(8), attempts);
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(10), (ushort)reasonBytes.Length);
        reasonBytes.CopyTo(buf.AsSpan(HeaderSize));
        originalEntry.CopyTo(buf.AsSpan(HeaderSize + reasonBytes.Length));
        return buf;
    }

    /// <summary>Decodes a dead-letter entry in place (zero allocation).</summary>
    public static void Decode(
        ReadOnlySpan<byte> data,
        out long deadLetteredTicks,
        out ushort attempts,
        out ReadOnlySpan<byte> reason,
        out ReadOnlySpan<byte> originalEntry)
    {
        if (data.Length < HeaderSize)
            throw new InvalidDataException(
                $"Dead-letter entry too short ({data.Length} bytes); minimum is {HeaderSize}.");

        deadLetteredTicks = BinaryPrimitives.ReadInt64BigEndian(data);
        attempts          = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(8));
        var reasonLen     = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(10));

        if (data.Length < HeaderSize + reasonLen)
            throw new InvalidDataException(
                $"Dead-letter entry truncated: reason length {reasonLen} but only " +
                $"{data.Length - HeaderSize} bytes remain.");

        reason        = data.Slice(HeaderSize, reasonLen);
        originalEntry = data.Slice(HeaderSize + reasonLen);
    }
}
