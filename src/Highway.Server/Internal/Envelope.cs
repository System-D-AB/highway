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

        // An entry may carry a failure block (015), a lineage trailer (073) and, innermost, an RPC deadline (074).
        // They are trailers, so they must come off before "payload is the rest" is true again.
        DecodeRpcFraming(StripDeadline(StripFailureBlock(data)), out requestId, out payload, out attempts);
    }

    /// <summary>
    /// Decodes an RPC entry whose payload is opaque to the very end — no trailers read or removed. A dead-letter
    /// feed entry (073) carries a dead-letter record as its payload, and that record ends with the ORIGINAL
    /// entry's failure block and lineage: stripping them as the feed entry's own trailers hands the handler a
    /// record without the failure that killed the message or its requeue count.
    /// </summary>
    public static void DecodeRpcEntryRaw(
        ReadOnlySpan<byte> data,
        out ReadOnlySpan<byte> requestId,
        out ReadOnlySpan<byte> payload,
        out ushort attempts)
    {
        RequireCurrentFormat(data, "RPC entry");
        DecodeRpcFraming(data, out requestId, out payload, out attempts);
    }

    private static void DecodeRpcFraming(
        ReadOnlySpan<byte> data,
        out ReadOnlySpan<byte> requestId,
        out ReadOnlySpan<byte> payload,
        out ushort attempts)
    {
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

        // An entry may carry a failure block (015). It is a trailer, so it must come off
        // before "payload is the rest" is true again.
        data = StripFailureBlock(data);

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
    // Failure block (015): an OPTIONAL trailer on any entry above.
    //
    //   <entry as framed above> [block][u32 blockLen][u32 magic]
    //   block := [u16 typeLen][type][u16 firstTypeLen][firstType][u32 detailLen][detail]
    //
    // A trailer, not a header, and not a new field in any framing. Every framing
    // above ends with "payload is the rest", so there is nowhere to put a length
    // without moving bytes that already exist in storage. Reading it from the END
    // keeps an entry without a block decoding byte-for-byte as it does today,
    // which is the difference between this and 013's breaking attempt count.
    //
    // Collision: a payload would have to END with the 8 trailer bytes to be
    // misread. Payloads are JSON envelopes, so their last byte is '}' (0x7D);
    // the magic's last byte is deliberately not that. The declared length is
    // bounds-checked too, so a coincidence has to survive both.
    // -------------------------------------------------------------------------

    /// <summary>Marks the last four bytes of an entry that carries a failure block.</summary>
    public const uint FailureMagic = 0xFE15FA11;

    /// <summary>
    /// Marks the last four bytes of an entry that carries a lineage trailer (feature 073). Four bytes, as for the
    /// failure block: a payload is opaque, and a one-byte marker would misread any binary payload ending in it —
    /// silently cutting three bytes off. The last byte is deliberately not <c>}</c>, a JSON envelope's last byte.
    /// </summary>
    public const uint LineageMagic = 0xFE11EA9E;

    private const int FailureTrailer = 4 + 4;   // [u32 blockLen][u32 magic]
    private const int FailureBlockHeader = 2 + 2 + 4;
    private const int LineageTrailerLen = LineageTrailerLength;

    /// <summary>Bytes a lineage trailer adds to an entry: [u16 requeues][u32 magic].</summary>
    public const int LineageTrailerLength = 2 + 4;

    /// <summary>
    /// Reads the requeue lineage count if this entry carries a lineage trailer (feature 073).
    /// Framing: <c>[...entry...][failure block?][u16 requeues][u32 magic 0xFE11EA9E]</c>
    /// </summary>
    public static bool TryGetLineage(
        ReadOnlySpan<byte> entry,
        out ushort requeues,
        out ReadOnlySpan<byte> withoutLineage)
    {
        requeues = 0;
        withoutLineage = entry;

        if (entry.Length < LineageTrailerLen)
            return false;

        if (BinaryPrimitives.ReadUInt32BigEndian(entry.Slice(entry.Length - 4)) != LineageMagic)
            return false;

        requeues = BinaryPrimitives.ReadUInt16BigEndian(entry.Slice(entry.Length - LineageTrailerLen));
        withoutLineage = entry.Slice(0, entry.Length - LineageTrailerLen);
        return true;
    }

    /// <summary>Removes the lineage trailer, if present.</summary>
    public static ReadOnlySpan<byte> StripLineage(ReadOnlySpan<byte> entry)
        => TryGetLineage(entry, out _, out var withoutLineage) ? withoutLineage : entry;

    /// <summary>
    /// Returns <paramref name="entry"/> with a lineage trailer carrying <paramref name="requeues"/>.
    /// If <paramref name="requeues"/> is 0, any existing lineage trailer is stripped.
    /// </summary>
    public static byte[] WithLineage(ReadOnlySpan<byte> entry, ushort requeues)
    {
        var bare = StripLineage(entry);
        if (requeues == 0) return bare.ToArray();

        var buf = new byte[bare.Length + LineageTrailerLen];
        bare.CopyTo(buf);
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(bare.Length), requeues);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(bare.Length + 2), LineageMagic);
        return buf;
    }

    // -------------------------------------------------------------------------
    // RPC deadline (074): the innermost trailer on a service entry.
    //
    //   <entry>[i64 acceptedTicks][i64 deadlineTicks][u32 magic 0xFE0DEAD1] [failure block?] [lineage?]
    //
    // Innermost, so the failure block and lineage code is unchanged: they strip and re-add themselves around it,
    // and StripFailureBlock leaves it in place. Both times are broker ticks — set by the broker that accepted the
    // call, never compared with a client's clock. Accepted gives queue wait and age; the deadline ends the
    // request. Queue and group entries never carry it.
    // -------------------------------------------------------------------------

    /// <summary>Marks the last four bytes of an RPC entry's deadline trailer (074). Last byte not <c>}</c>.</summary>
    public const uint DeadlineMagic = 0xFE0DEAD1;

    /// <summary>Bytes a deadline trailer adds to an entry: [i64 acceptedTicks][i64 deadlineTicks][u32 magic].</summary>
    public const int DeadlineTrailerLength = 8 + 8 + 4;

    /// <summary>
    /// Returns <paramref name="entry"/> (an entry with no failure block or lineage) with a deadline trailer,
    /// replacing any it already had.
    /// </summary>
    public static byte[] WithDeadline(ReadOnlySpan<byte> entry, long acceptedTicks, long deadlineTicks)
    {
        var bare = StripDeadline(entry);
        var buf = new byte[bare.Length + DeadlineTrailerLength];
        bare.CopyTo(buf);
        BinaryPrimitives.WriteInt64BigEndian(buf.AsSpan(bare.Length), acceptedTicks);
        BinaryPrimitives.WriteInt64BigEndian(buf.AsSpan(bare.Length + 8), deadlineTicks);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(bare.Length + 16), DeadlineMagic);
        return buf;
    }

    /// <summary>Reads an RPC entry's deadline (broker ticks), whatever other trailers ride outside it.</summary>
    public static bool TryGetDeadline(ReadOnlySpan<byte> entry, out long deadlineTicks)
        => TryGetDeadline(entry, out _, out deadlineTicks);

    /// <summary>Reads when the broker accepted an RPC entry and its deadline (both broker ticks).</summary>
    public static bool TryGetDeadline(ReadOnlySpan<byte> entry, out long acceptedTicks, out long deadlineTicks)
    {
        var body = StripFailureBlock(entry);
        acceptedTicks = deadlineTicks = 0;
        if (body.Length < RpcHeader + DeadlineTrailerLength
            || BinaryPrimitives.ReadUInt32BigEndian(body.Slice(body.Length - 4)) != DeadlineMagic)
            return false;
        acceptedTicks = BinaryPrimitives.ReadInt64BigEndian(body.Slice(body.Length - DeadlineTrailerLength));
        deadlineTicks = BinaryPrimitives.ReadInt64BigEndian(body.Slice(body.Length - DeadlineTrailerLength + 8));
        return true;
    }

    private static ReadOnlySpan<byte> StripDeadline(ReadOnlySpan<byte> body)
        => body.Length >= RpcHeader + DeadlineTrailerLength
           && BinaryPrimitives.ReadUInt32BigEndian(body.Slice(body.Length - 4)) == DeadlineMagic
            ? body.Slice(0, body.Length - DeadlineTrailerLength)
            : body;

    /// <summary>
    /// Removes the lineage and failure trailers, if present, so the remaining span is the entry exactly
    /// as it would have been framed without them. Called at the head of every decode. An RPC deadline, the
    /// innermost trailer, stays.
    /// </summary>
    public static ReadOnlySpan<byte> StripFailureBlock(ReadOnlySpan<byte> entry)
    {
        var withoutLineage = StripLineage(entry);
        return TryGetFailureBlock(withoutLineage, out _, out var withoutBlock) ? withoutBlock : withoutLineage;
    }

    /// <summary>
    /// Reads the failure block, if this entry has one. <paramref name="withoutBlock"/> is the
    /// entry with the block and trailer removed.
    /// </summary>
    public static bool TryGetFailureBlock(
        ReadOnlySpan<byte> entry,
        out ReadOnlySpan<byte> block,
        out ReadOnlySpan<byte> withoutBlock)
    {
        block = default;
        entry = StripLineage(entry);
        withoutBlock = entry;

        if (entry.Length < FailureTrailer)
            return false;

        if (BinaryPrimitives.ReadUInt32BigEndian(entry.Slice(entry.Length - 4)) != FailureMagic)
            return false;

        var blockLen = BinaryPrimitives.ReadUInt32BigEndian(entry.Slice(entry.Length - 8));

        // Bounds check, not an assumption: the magic may be a coincidence in payload bytes,
        // and a length that does not fit proves it was.
        if (blockLen > (uint)(entry.Length - FailureTrailer))
            return false;

        var start = entry.Length - FailureTrailer - (int)blockLen;
        block = entry.Slice(start, (int)blockLen);
        withoutBlock = entry.Slice(0, start);
        return true;
    }

    /// <summary>
    /// Builds a failure block. <paramref name="firstType"/> is empty when the failure has
    /// never changed shape — the field exists to answer "did this fail the same way every
    /// time?", so storing a copy of <paramref name="type"/> would answer nothing.
    /// </summary>
    public static byte[] EncodeFailureBlock(
        ReadOnlySpan<byte> type,
        ReadOnlySpan<byte> firstType,
        ReadOnlySpan<byte> detail)
    {
        if (type.Length > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(type));
        if (firstType.Length > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(firstType));

        var buf = new byte[FailureBlockHeader + type.Length + firstType.Length + detail.Length];
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(0), (ushort)type.Length);
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(2), (ushort)firstType.Length);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(4), (uint)detail.Length);
        type.CopyTo(buf.AsSpan(FailureBlockHeader));
        firstType.CopyTo(buf.AsSpan(FailureBlockHeader + type.Length));
        detail.CopyTo(buf.AsSpan(FailureBlockHeader + type.Length + firstType.Length));
        return buf;
    }

    /// <summary>Decodes a failure block in place (zero allocation).</summary>
    public static void DecodeFailureBlock(
        ReadOnlySpan<byte> block,
        out ReadOnlySpan<byte> type,
        out ReadOnlySpan<byte> firstType,
        out ReadOnlySpan<byte> detail)
    {
        if (block.Length < FailureBlockHeader)
            throw new InvalidDataException(
                $"Failure block too short ({block.Length} bytes); minimum is {FailureBlockHeader}.");

        int typeLen   = BinaryPrimitives.ReadUInt16BigEndian(block.Slice(0));
        int firstLen  = BinaryPrimitives.ReadUInt16BigEndian(block.Slice(2));
        var detailLen = BinaryPrimitives.ReadUInt32BigEndian(block.Slice(4));

        var total = (long)FailureBlockHeader + typeLen + firstLen + detailLen;
        if (block.Length < total)
            throw new InvalidDataException(
                $"Failure block truncated: declared {total} bytes but only {block.Length} present.");

        type      = block.Slice(FailureBlockHeader, typeLen);
        firstType = block.Slice(FailureBlockHeader + typeLen, firstLen);
        detail    = block.Slice(FailureBlockHeader + typeLen + firstLen, (int)detailLen);
    }

    /// <summary>
    /// Copies the failure block and lineage from <paramref name="source"/> onto <paramref name="rebuilt"/>,
    /// or returns <paramref name="rebuilt"/> unchanged when there are none.
    ///
    /// <para><b>Call this at every point an entry is re-encoded.</b> An entry is rebuilt from
    /// its decoded parts in four places — claim (RPC, queue and channel) and the lease sweep's
    /// requeue — and every one of them drops the trailer, because the trailer is not one of the
    /// parts. Missing a single site loses the failure history silently, on a path nobody
    /// watches, which is exactly how this was found: the sweep was wired first and the claim
    /// was not, so the block survived the requeue and then vanished at the next claim.</para>
    /// </summary>
    public static byte[] CarryFailureBlock(ReadOnlySpan<byte> source, byte[] rebuilt)
    {
        // 074: an RPC deadline rides every re-encode too — a redelivered request keeps its caller's deadline.
        if (TryGetDeadline(source, out var accepted, out var deadline)) rebuilt = WithDeadline(rebuilt, accepted, deadline);
        TryGetLineage(source, out var requeues, out _);
        var withBlock = TryGetFailureBlock(source, out var block, out _) ? WithFailureBlock(rebuilt, block) : rebuilt;
        return requeues > 0 ? WithLineage(withBlock, requeues) : withBlock;
    }

    /// <summary>
    /// Returns <paramref name="entry"/> carrying <paramref name="block"/>, replacing any block
    /// it already had. Attaching to an entry that has none is what makes the trailer additive;
    /// replacing is what makes a second report cheap. Preserves any existing lineage trailer.
    /// </summary>
    public static byte[] WithFailureBlock(ReadOnlySpan<byte> entry, ReadOnlySpan<byte> block)
    {
        TryGetLineage(entry, out var requeues, out _);
        var bare = StripFailureBlock(entry);
        var buf = new byte[bare.Length + block.Length + FailureTrailer];
        bare.CopyTo(buf);
        block.CopyTo(buf.AsSpan(bare.Length));
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(bare.Length + block.Length), (uint)block.Length);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(bare.Length + block.Length + 4), FailureMagic);
        return requeues > 0 ? WithLineage(buf, requeues) : buf;
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
}
