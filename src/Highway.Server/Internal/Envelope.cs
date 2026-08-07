using System.Buffers.Binary;

namespace Highway.Server.Internal;

/// <summary>
/// Binary framing for all Highway queue and processing-list entries.
///
/// All multi-byte integers are <b>big-endian</b> (network byte order).
///
/// Entry wire formats:
/// <code>
/// RPC queue entry:          [u8 0xFF][u16 attempts][u16 requestIdLen][requestId][payload]
/// RPC processing entry:     [u8 0xFF][i64 claimTicksUtc][u16 attempts][u16 requestIdLen][requestId][payload]
/// Channel entry:            [u8 0xFF][u16 attempts][i64 messageId][payload]
/// Group processing entry:   [u8 0xFF][i64 receiveTicksUtc][u16 attempts][i64 messageId][payload]
/// </code>
///
/// <para><b>The attempt count</b> (feature 013) is what bounds redelivery. It is
/// incremented when an entry is requeued after a lease expiry, and an entry that
/// exceeds <c>MaxDeliveryAttempts</c> is dead-lettered instead of requeued. It lives
/// <i>in the entry</i> rather than in a side key so that incrementing it is atomic
/// with the move that caused it — a count kept beside the entry would be lost by
/// exactly the crash it exists to survive.</para>
///
/// <para><b>The version byte</b> exists so a pre-013 entry is <i>refused</i> rather
/// than misparsed. Without it, an old entry read as a new one would reinterpret its
/// leading bytes as an attempt count, read a wrong length, and hand a corrupt payload
/// to an application — far worse than an error. <c>0xFF</c> is unambiguous against
/// every legacy leading byte:</para>
///
/// <list type="bullet">
///   <item><description>legacy RPC entry — high byte of a u16 request-ID length, bounded by <c>MaxIdentifierBytes</c> (validated below 0xFF00)</description></item>
///   <item><description>legacy channel entry — high byte of a message-ID counter that starts at 1</description></item>
///   <item><description>legacy processing entry — high byte of a .NET tick count, currently 0x08</description></item>
/// </list>
///
/// <para><b>Every entry format is now versioned.</b> The backlog was the last unversioned
/// one, and it was removed with the backlog itself — a publish with no registered
/// subscriber is delivered to nobody.</para>
///
/// Encode methods return a freshly allocated <c>byte[]</c>.
/// Decode methods work over a <see cref="ReadOnlySpan{T}"/> without allocation.
/// All decode methods throw <see cref="InvalidDataException"/> on truncated,
/// pre-013, or otherwise corrupt input.
/// </summary>
internal static class Envelope
{
    /// <summary>
    /// Leading byte of every versioned entry. See the type remarks for why this
    /// value cannot collide with any pre-013 entry.
    /// </summary>
    public const byte FormatVersion = 0xFF;

    /// <summary>
    /// Highest <see cref="HighwayServerOptions.MaxIdentifierBytes"/> for which
    /// <see cref="FormatVersion"/> stays unambiguous against a legacy RPC entry,
    /// whose leading byte is the high half of a u16 identifier length.
    /// </summary>
    public const int MaxUnambiguousIdentifierBytes = 0xFF00 - 1;

    /// <summary>
    /// Largest representable attempt count. The count saturates here rather than
    /// wrapping — a wrapped counter would silently restore the infinite-retry bug
    /// this field exists to fix.
    /// </summary>
    public const ushort MaxAttempts = ushort.MaxValue;

    /// <summary>Increments an attempt count without wrapping past <see cref="MaxAttempts"/>.</summary>
    public static ushort NextAttempt(ushort attempts)
        => attempts == MaxAttempts ? MaxAttempts : (ushort)(attempts + 1);

    /// <summary>
    /// Throws when <paramref name="data"/> is a pre-013 entry, with a message an
    /// operator can act on. Called at the head of every versioned decode.
    /// </summary>
    private static void RequireCurrentFormat(ReadOnlySpan<byte> data, string entryKind)
    {
        if (data.Length == 0)
            throw new InvalidDataException($"{entryKind} is empty.");

        if (data[0] != FormatVersion)
            throw new InvalidDataException(
                $"{entryKind} is in the pre-013 storage format (leading byte 0x{data[0]:X2}). " +
                "Drain the queue with the previous version, or delete the data directory. " +
                "Refusing rather than misparsing, which would deliver a corrupt payload.");
    }

    /// <summary>
    /// Whether <paramref name="data"/> looks like a pre-013 entry. Lets a command
    /// report a storage-format problem without catching an exception to find out.
    /// </summary>
    public static bool IsLegacyEntry(ReadOnlySpan<byte> data)
        => data.Length > 0 && data[0] != FormatVersion;

    // -------------------------------------------------------------------------
    // RPC queue entry:  [u8 ver][u16 attempts][u16 requestIdLen][requestId][payload]
    // -------------------------------------------------------------------------

    private const int RpcHeader = 1 + 2 + 2;

    /// <summary>Encodes an RPC queue entry.</summary>
    public static byte[] EncodeRpcEntry(
        ReadOnlySpan<byte> requestId,
        ReadOnlySpan<byte> payload,
        ushort attempts = 0)
    {
        if (requestId.Length > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(requestId),
                $"requestId length {requestId.Length} exceeds u16 max ({ushort.MaxValue}).");

        var buf = new byte[RpcHeader + requestId.Length + payload.Length];
        buf[0] = FormatVersion;
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(1), attempts);
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(3), (ushort)requestId.Length);
        requestId.CopyTo(buf.AsSpan(RpcHeader));
        payload.CopyTo(buf.AsSpan(RpcHeader + requestId.Length));
        return buf;
    }

    /// <summary>Decodes an RPC queue entry in place (zero allocation).</summary>
    public static void DecodeRpcEntry(
        ReadOnlySpan<byte> data,
        out ReadOnlySpan<byte> requestId,
        out ReadOnlySpan<byte> payload,
        out ushort attempts)
    {
        RequireCurrentFormat(data, "RPC entry");

        if (data.Length < RpcHeader)
            throw new InvalidDataException(
                $"RPC entry too short ({data.Length} bytes); minimum is {RpcHeader}.");

        attempts  = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(1));
        var idLen = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(3));
        if (data.Length < RpcHeader + idLen)
            throw new InvalidDataException(
                $"RPC entry truncated: requestId length {idLen} but only {data.Length - RpcHeader} bytes remain.");

        requestId = data.Slice(RpcHeader, idLen);
        payload   = data.Slice(RpcHeader + idLen);
    }

    // -------------------------------------------------------------------------
    // RPC processing entry:
    //   [u8 ver][i64 claimTicksUtc][u16 attempts][u16 requestIdLen][requestId][payload]
    // -------------------------------------------------------------------------

    private const int RpcProcHeader = 1 + 8 + 2 + 2;

    /// <summary>Encodes an RPC processing entry.</summary>
    public static byte[] EncodeRpcProcessingEntry(
        long claimTicks,
        ReadOnlySpan<byte> requestId,
        ReadOnlySpan<byte> payload,
        ushort attempts = 0)
    {
        if (requestId.Length > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(requestId),
                $"requestId length {requestId.Length} exceeds u16 max ({ushort.MaxValue}).");

        var buf = new byte[RpcProcHeader + requestId.Length + payload.Length];
        buf[0] = FormatVersion;
        BinaryPrimitives.WriteInt64BigEndian(buf.AsSpan(1), claimTicks);
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(9), attempts);
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(11), (ushort)requestId.Length);
        requestId.CopyTo(buf.AsSpan(RpcProcHeader));
        payload.CopyTo(buf.AsSpan(RpcProcHeader + requestId.Length));
        return buf;
    }

    /// <summary>Decodes an RPC processing entry in place (zero allocation).</summary>
    public static void DecodeRpcProcessingEntry(
        ReadOnlySpan<byte> data,
        out long claimTicks,
        out ReadOnlySpan<byte> requestId,
        out ReadOnlySpan<byte> payload,
        out ushort attempts)
    {
        RequireCurrentFormat(data, "RPC processing entry");

        if (data.Length < RpcProcHeader)
            throw new InvalidDataException(
                $"RPC processing entry too short ({data.Length} bytes); minimum is {RpcProcHeader}.");

        claimTicks = BinaryPrimitives.ReadInt64BigEndian(data.Slice(1));
        attempts   = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(9));
        var idLen  = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(11));
        if (data.Length < RpcProcHeader + idLen)
            throw new InvalidDataException(
                $"RPC processing entry truncated: requestId length {idLen} but only {data.Length - RpcProcHeader} bytes remain.");

        requestId = data.Slice(RpcProcHeader, idLen);
        payload   = data.Slice(RpcProcHeader + idLen);
    }

    // -------------------------------------------------------------------------
    // Channel entry:  [u8 ver][u16 attempts][i64 messageId][payload]
    // -------------------------------------------------------------------------

    private const int ChannelHeader = 1 + 2 + 8;

    /// <summary>Encodes a channel (pub/sub delivery) entry.</summary>
    public static byte[] EncodeChannelEntry(long messageId, ReadOnlySpan<byte> payload, ushort attempts = 0)
    {
        var buf = new byte[ChannelHeader + payload.Length];
        buf[0] = FormatVersion;
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(1), attempts);
        BinaryPrimitives.WriteInt64BigEndian(buf.AsSpan(3), messageId);
        payload.CopyTo(buf.AsSpan(ChannelHeader));
        return buf;
    }

    /// <summary>Decodes a channel entry in place (zero allocation).</summary>
    public static void DecodeChannelEntry(
        ReadOnlySpan<byte> data,
        out long messageId,
        out ReadOnlySpan<byte> payload,
        out ushort attempts)
    {
        RequireCurrentFormat(data, "Channel entry");

        if (data.Length < ChannelHeader)
            throw new InvalidDataException(
                $"Channel entry too short ({data.Length} bytes); minimum is {ChannelHeader}.");

        attempts  = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(1));
        messageId = BinaryPrimitives.ReadInt64BigEndian(data.Slice(3));
        payload   = data.Slice(ChannelHeader);
    }

    // -------------------------------------------------------------------------
    // Group processing entry:
    //   [u8 ver][i64 receiveTicksUtc][u16 attempts][i64 messageId][payload]
    // -------------------------------------------------------------------------

    private const int GroupProcHeader = 1 + 8 + 2 + 8;

    /// <summary>Encodes a group processing entry.</summary>
    public static byte[] EncodeGroupProcessingEntry(
        long receiveTicks,
        long messageId,
        ReadOnlySpan<byte> payload,
        ushort attempts = 0)
    {
        var buf = new byte[GroupProcHeader + payload.Length];
        buf[0] = FormatVersion;
        BinaryPrimitives.WriteInt64BigEndian(buf.AsSpan(1), receiveTicks);
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(9), attempts);
        BinaryPrimitives.WriteInt64BigEndian(buf.AsSpan(11), messageId);
        payload.CopyTo(buf.AsSpan(GroupProcHeader));
        return buf;
    }

    /// <summary>Decodes a group processing entry in place (zero allocation).</summary>
    public static void DecodeGroupProcessingEntry(
        ReadOnlySpan<byte> data,
        out long receiveTicks,
        out long messageId,
        out ReadOnlySpan<byte> payload,
        out ushort attempts)
    {
        RequireCurrentFormat(data, "Group processing entry");

        if (data.Length < GroupProcHeader)
            throw new InvalidDataException(
                $"Group processing entry too short ({data.Length} bytes); minimum is {GroupProcHeader}.");

        receiveTicks = BinaryPrimitives.ReadInt64BigEndian(data.Slice(1));
        attempts     = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(9));
        messageId    = BinaryPrimitives.ReadInt64BigEndian(data.Slice(11));
        payload      = data.Slice(GroupProcHeader);
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
        DecodeRpcProcessingEntry(processingEntry, out _, out var requestId, out _, out _);
        return requestId;
    }

    /// <summary>
    /// Extracts the messageId from a group <em>processing</em> entry
    /// (the one in <c>hw:ch:{channel}:grp:{group}:proc</c>) without allocating.
    /// Used by <c>HW.RACK</c> for processing-list scan-and-remove.
    /// </summary>
    public static long GetMessageId(ReadOnlySpan<byte> processingEntry)
    {
        DecodeGroupProcessingEntry(processingEntry, out _, out var messageId, out _, out _);
        return messageId;
    }
}
