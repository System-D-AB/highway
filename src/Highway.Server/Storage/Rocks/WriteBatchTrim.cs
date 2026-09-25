using System.Buffers.Binary;

namespace Highway.Server.Storage.Rocks;

/// <summary>
/// Drops the leading operations of a serialized <c>WriteBatch</c> so it starts at a given sequence
/// (feature 062).
///
/// <para><b>Why.</b> Concurrent committers are merged by RocksDB's group commit into one WAL record, while
/// the <see cref="ReplicationTail"/> holds each committer's batch separately. A replica fed from the tail
/// can therefore stand at a watermark <i>inside</i> a WAL record. If it later falls back to the WAL (the
/// primary restarted, or the tail evicted its cursor), <c>GetUpdatesSince</c> returns that whole record;
/// the replica would see a first sequence at or below its watermark and skip it — losing the rest. So
/// the WAL path trims such a record to begin at <c>fromSeq + 1</c>.</para>
///
/// <para><b>Format</b> (RocksDB <c>write_batch.cc</c>): <c>[8B LE sequence][4B LE count]</c>, then records,
/// each a tag byte and varint32-length-prefixed fields. Every Put / Delete / SingleDelete / Merge /
/// DeleteRange consumes one sequence; LogData consumes none. Any other tag (2PC markers, blob index,
/// wide columns, timestamps) is refused — the caller then answers with a gap and the replica re-syncs
/// from a snapshot, the safe outcome. Highway writes only Put, Delete and DeleteRange.</para>
/// </summary>
internal static class WriteBatchTrim
{
    private const int HeaderSize = 12;

    /// <summary>
    /// A copy of <paramref name="data"/>[..<paramref name="length"/>] without its operations below
    /// <paramref name="startSeq"/>, re-headed at <paramref name="startSeq"/>. False when the batch cannot
    /// be parsed with certainty or does not contain <paramref name="startSeq"/>.
    /// </summary>
    public static bool TryStartAt(ReadOnlySpan<byte> batch, ulong startSeq, out byte[] trimmed)
    {
        trimmed = [];
        if (batch.Length < HeaderSize) return false;
        var seq = BinaryPrimitives.ReadUInt64LittleEndian(batch);
        var count = BinaryPrimitives.ReadUInt32LittleEndian(batch[8..]);
        if (startSeq < seq || startSeq >= seq + count) return false;

        var skip = startSeq - seq;
        var pos = HeaderSize;
        ulong consumed = 0;
        while (consumed < skip)
        {
            if (!TrySkipRecord(batch, ref pos, out var usesSeq)) return false;
            if (usesSeq) consumed++;
        }

        // Verify the remainder parses completely before handing it out.
        var keep = pos;
        ulong rest = 0;
        for (var p = pos; p < batch.Length;)
        {
            if (!TrySkipRecord(batch, ref p, out var usesSeq)) return false;
            if (usesSeq) rest++;
        }
        if (rest != count - skip) return false;

        trimmed = new byte[HeaderSize + batch.Length - keep];
        BinaryPrimitives.WriteUInt64LittleEndian(trimmed, startSeq);
        BinaryPrimitives.WriteUInt32LittleEndian(trimmed.AsSpan(8), (uint)rest);
        batch[keep..].CopyTo(trimmed.AsSpan(HeaderSize));
        return true;
    }

    private static bool TrySkipRecord(ReadOnlySpan<byte> b, ref int pos, out bool usesSeq)
    {
        usesSeq = true;
        if (pos >= b.Length) return false;
        var tag = b[pos++];
        switch (tag)
        {
            case 0x00:  // Deletion: key
            case 0x07:  // SingleDeletion: key
                return SkipSlice(b, ref pos);
            case 0x01:  // Value: key, value
            case 0x02:  // Merge: key, value
            case 0x0F:  // RangeDeletion: begin, end
                return SkipSlice(b, ref pos) && SkipSlice(b, ref pos);
            case 0x04:  // ColumnFamilyDeletion: cf, key
            case 0x08:  // ColumnFamilySingleDeletion: cf, key
                return SkipVarint(b, ref pos) && SkipSlice(b, ref pos);
            case 0x05:  // ColumnFamilyValue: cf, key, value
            case 0x06:  // ColumnFamilyMerge: cf, key, value
            case 0x0E:  // ColumnFamilyRangeDeletion: cf, begin, end
                return SkipVarint(b, ref pos) && SkipSlice(b, ref pos) && SkipSlice(b, ref pos);
            case 0x03:  // LogData: blob, no sequence
                usesSeq = false;
                return SkipSlice(b, ref pos);
            default:
                return false;
        }
    }

    private static bool SkipSlice(ReadOnlySpan<byte> b, ref int pos)
    {
        if (!TryReadVarint32(b, ref pos, out var len)) return false;
        if (len > (uint)(b.Length - pos)) return false;
        pos += (int)len;
        return true;
    }

    private static bool SkipVarint(ReadOnlySpan<byte> b, ref int pos) => TryReadVarint32(b, ref pos, out _);

    private static bool TryReadVarint32(ReadOnlySpan<byte> b, ref int pos, out uint value)
    {
        value = 0;
        for (var shift = 0; shift <= 28 && pos < b.Length; shift += 7)
        {
            var x = b[pos++];
            value |= (uint)(x & 0x7F) << shift;
            if ((x & 0x80) == 0) return true;
        }
        return false;
    }
}
