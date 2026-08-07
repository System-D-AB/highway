using System.Buffers.Binary;

namespace Highway.Server.Internal;

/// <summary>
/// Binary framing for all Highway queue and processing-list entries.
///
/// All multi-byte integers are <b>big-endian</b> (network byte order).
///
/// Entry wire formats:
/// <code>
/// RPC queue entry:          [u16 BE requestIdLen][requestId bytes][payload bytes]
/// RPC processing entry:     [i64 BE claimTicksUtc] + RPC queue entry
/// Channel entry:            [i64 BE messageId][payload bytes]
/// Backlog entry:            [i64 BE publishTicksUtc] + Channel entry
/// Group processing entry:   [i64 BE receiveTicksUtc] + Channel entry
/// </code>
///
/// Encode methods return a freshly allocated <c>byte[]</c>.
/// Decode methods work over a <see cref="ReadOnlySpan{T}"/> without allocation.
/// All decode methods throw <see cref="InvalidDataException"/> on truncated or
/// otherwise corrupt input.
/// </summary>
internal static class Envelope
{
    // -------------------------------------------------------------------------
    // RPC queue entry:  [u16 requestIdLen][requestId][payload]
    // -------------------------------------------------------------------------

    /// <summary>Encodes an RPC queue entry.</summary>
    public static byte[] EncodeRpcEntry(
        ReadOnlySpan<byte> requestId,
        ReadOnlySpan<byte> payload)
    {
        if (requestId.Length > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(requestId),
                $"requestId length {requestId.Length} exceeds u16 max ({ushort.MaxValue}).");

        // [u16 requestIdLen][requestId][payload]
        var buf = new byte[2 + requestId.Length + payload.Length];
        BinaryPrimitives.WriteUInt16BigEndian(buf, (ushort)requestId.Length);
        requestId.CopyTo(buf.AsSpan(2));
        payload.CopyTo(buf.AsSpan(2 + requestId.Length));
        return buf;
    }

    /// <summary>Decodes an RPC queue entry in place (zero allocation).</summary>
    public static void DecodeRpcEntry(
        ReadOnlySpan<byte> data,
        out ReadOnlySpan<byte> requestId,
        out ReadOnlySpan<byte> payload)
    {
        const int headerSize = 2;
        if (data.Length < headerSize)
            throw new InvalidDataException(
                $"RPC entry too short ({data.Length} bytes); minimum is {headerSize}.");

        var idLen = BinaryPrimitives.ReadUInt16BigEndian(data);
        if (data.Length < headerSize + idLen)
            throw new InvalidDataException(
                $"RPC entry truncated: requestId length {idLen} but only {data.Length - headerSize} bytes remain.");

        requestId = data.Slice(headerSize, idLen);
        payload   = data.Slice(headerSize + idLen);
    }

    // -------------------------------------------------------------------------
    // RPC processing entry:  [i64 claimTicksUtc][u16 requestIdLen][requestId][payload]
    // -------------------------------------------------------------------------

    /// <summary>Encodes an RPC processing entry.</summary>
    public static byte[] EncodeRpcProcessingEntry(
        long claimTicks,
        ReadOnlySpan<byte> requestId,
        ReadOnlySpan<byte> payload)
    {
        if (requestId.Length > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(requestId),
                $"requestId length {requestId.Length} exceeds u16 max ({ushort.MaxValue}).");

        // [i64 claimTicks][u16 requestIdLen][requestId][payload]
        var buf = new byte[8 + 2 + requestId.Length + payload.Length];
        BinaryPrimitives.WriteInt64BigEndian(buf, claimTicks);
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(8), (ushort)requestId.Length);
        requestId.CopyTo(buf.AsSpan(10));
        payload.CopyTo(buf.AsSpan(10 + requestId.Length));
        return buf;
    }

    /// <summary>Decodes an RPC processing entry in place (zero allocation).</summary>
    public static void DecodeRpcProcessingEntry(
        ReadOnlySpan<byte> data,
        out long claimTicks,
        out ReadOnlySpan<byte> requestId,
        out ReadOnlySpan<byte> payload)
    {
        const int headerSize = 8 + 2; // i64 + u16
        if (data.Length < headerSize)
            throw new InvalidDataException(
                $"RPC processing entry too short ({data.Length} bytes); minimum is {headerSize}.");

        claimTicks = BinaryPrimitives.ReadInt64BigEndian(data);
        var idLen  = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(8));
        if (data.Length < headerSize + idLen)
            throw new InvalidDataException(
                $"RPC processing entry truncated: requestId length {idLen} but only {data.Length - headerSize} bytes remain.");

        requestId = data.Slice(headerSize, idLen);
        payload   = data.Slice(headerSize + idLen);
    }

    // -------------------------------------------------------------------------
    // Channel entry:  [i64 messageId][payload]
    // -------------------------------------------------------------------------

    /// <summary>Encodes a channel (pub/sub delivery) entry.</summary>
    public static byte[] EncodeChannelEntry(long messageId, ReadOnlySpan<byte> payload)
    {
        var buf = new byte[8 + payload.Length];
        BinaryPrimitives.WriteInt64BigEndian(buf, messageId);
        payload.CopyTo(buf.AsSpan(8));
        return buf;
    }

    /// <summary>Decodes a channel entry in place (zero allocation).</summary>
    public static void DecodeChannelEntry(
        ReadOnlySpan<byte> data,
        out long messageId,
        out ReadOnlySpan<byte> payload)
    {
        if (data.Length < 8)
            throw new InvalidDataException(
                $"Channel entry too short ({data.Length} bytes); minimum is 8.");

        messageId = BinaryPrimitives.ReadInt64BigEndian(data);
        payload   = data.Slice(8);
    }

    // -------------------------------------------------------------------------
    // Backlog entry:  [i64 publishTicksUtc][i64 messageId][payload]
    // -------------------------------------------------------------------------

    /// <summary>Encodes a backlog entry.</summary>
    public static byte[] EncodeBacklogEntry(
        long publishTicks,
        long messageId,
        ReadOnlySpan<byte> payload)
    {
        var buf = new byte[8 + 8 + payload.Length];
        BinaryPrimitives.WriteInt64BigEndian(buf, publishTicks);
        BinaryPrimitives.WriteInt64BigEndian(buf.AsSpan(8), messageId);
        payload.CopyTo(buf.AsSpan(16));
        return buf;
    }

    /// <summary>Decodes a backlog entry in place (zero allocation).</summary>
    public static void DecodeBacklogEntry(
        ReadOnlySpan<byte> data,
        out long publishTicks,
        out long messageId,
        out ReadOnlySpan<byte> payload)
    {
        if (data.Length < 16)
            throw new InvalidDataException(
                $"Backlog entry too short ({data.Length} bytes); minimum is 16.");

        publishTicks = BinaryPrimitives.ReadInt64BigEndian(data);
        messageId    = BinaryPrimitives.ReadInt64BigEndian(data.Slice(8));
        payload      = data.Slice(16);
    }

    // -------------------------------------------------------------------------
    // Group processing entry:  [i64 receiveTicksUtc][i64 messageId][payload]
    // -------------------------------------------------------------------------

    /// <summary>Encodes a group processing entry.</summary>
    public static byte[] EncodeGroupProcessingEntry(
        long receiveTicks,
        long messageId,
        ReadOnlySpan<byte> payload)
    {
        var buf = new byte[8 + 8 + payload.Length];
        BinaryPrimitives.WriteInt64BigEndian(buf, receiveTicks);
        BinaryPrimitives.WriteInt64BigEndian(buf.AsSpan(8), messageId);
        payload.CopyTo(buf.AsSpan(16));
        return buf;
    }

    /// <summary>Decodes a group processing entry in place (zero allocation).</summary>
    public static void DecodeGroupProcessingEntry(
        ReadOnlySpan<byte> data,
        out long receiveTicks,
        out long messageId,
        out ReadOnlySpan<byte> payload)
    {
        if (data.Length < 16)
            throw new InvalidDataException(
                $"Group processing entry too short ({data.Length} bytes); minimum is 16.");

        receiveTicks = BinaryPrimitives.ReadInt64BigEndian(data);
        messageId    = BinaryPrimitives.ReadInt64BigEndian(data.Slice(8));
        payload      = data.Slice(16);
    }

    // -------------------------------------------------------------------------
    // ID extraction helpers (for ACK / RACK matching)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Extracts the requestId portion from an RPC <em>processing</em> entry
    /// (the one in <c>hw:svc:{service}:proc:{nodeId}</c>) without decoding the
    /// full entry.  Used by <c>HW.ACK</c> for processing-list scan-and-remove.
    /// </summary>
    public static ReadOnlySpan<byte> GetRequestId(ReadOnlySpan<byte> processingEntry)
    {
        DecodeRpcProcessingEntry(processingEntry, out _, out var requestId, out _);
        return requestId;
    }

    /// <summary>
    /// Extracts the messageId from a group <em>processing</em> entry
    /// (the one in <c>hw:ch:{channel}:grp:{group}:proc</c>) without allocating.
    /// Used by <c>HW.RACK</c> for processing-list scan-and-remove.
    /// </summary>
    public static long GetMessageId(ReadOnlySpan<byte> processingEntry)
    {
        DecodeGroupProcessingEntry(processingEntry, out _, out var messageId, out _);
        return messageId;
    }
}
