using System.Text;
using RocksDbSharp;

namespace Highway.Server.Storage.Rocks;

/// <summary>
/// Decodes a WAL tail into per-entity operation counts for the reconciliation report
/// (042 G9 / R4.2): what a demoted primary's unreplicated tail actually touched, by
/// key family and name, so an operator can replay it deliberately.
///
/// <para>The decoder reads the stable RocksDB <c>WriteBatch</c> record format
/// (12-byte header: 8B LE sequence + 4B LE count, then tagged records). Highway
/// stages Put/Delete/DeleteRange on the data column family, so those tags are the
/// ones that matter; an unknown tag stops that batch's decode and is reported, never
/// guessed at. This is why WAL compression must stay off (constraints C9 note): a
/// compressed WAL is opaque to this reader.</para>
/// </summary>
internal static class WalTailSummary
{
    private sealed record EntityOps(string Entity)
    {
        public int Puts;
        public int Deletes;
        public long PutBytes;
    }

    /// <summary>
    /// Summarizes every batch after <paramref name="afterSeq"/> as per-entity lines.
    /// Returns an empty list when the tail is empty; a first line starting with
    /// <c>tail-undecodable</c> when the WAL no longer reaches back that far.
    /// </summary>
    public static List<string> Describe(ReplicationSource source, ulong afterSeq)
    {
        var entities = new Dictionary<string, EntityOps>(StringComparer.Ordinal);
        var undecoded = 0;
        var batches = 0;

        try
        {
            foreach (var page in source.GetWalUpdates(afterSeq))
            {
                batches++;
                if (!TryDecodeBatch(page.Data, entities))
                    undecoded++;
            }
        }
        catch (Exception ex)
        {
            return [$"tail-undecodable: {ex.GetType().Name} reading WAL after seq {afterSeq} — the tail is preserved in the data directory, not lost"];
        }

        var lines = new List<string>();
        foreach (var ops in entities.Values.OrderBy(e => e.Entity, StringComparer.Ordinal))
        {
            lines.Add($"{ops.Entity}: puts={ops.Puts} bytes={ops.PutBytes} deletes={ops.Deletes}");
        }
        if (undecoded > 0)
            lines.Add($"undecoded-batches={undecoded} of {batches} (unknown record tag; raw WAL preserved)");
        return lines;
    }

    private static bool TryDecodeBatch(byte[] data, Dictionary<string, EntityOps> entities)
    {
        // WriteBatch rep: [8B LE sequence][4B LE count][records...]
        if (data.Length < 12) return false;
        var pos = 12;

        while (pos < data.Length)
        {
            var tag = data[pos++];
            bool hasCf, hasValue;
            switch (tag)
            {
                case 0x00: hasCf = false; hasValue = false; break; // Deletion
                case 0x01: hasCf = false; hasValue = true; break;  // Value (Put)
                case 0x02: hasCf = true; hasValue = true; break;   // CF Value
                case 0x03: hasCf = true; hasValue = false; break;  // CF Deletion
                case 0x04: hasCf = false; hasValue = true; break;  // Merge
                case 0x05: hasCf = true; hasValue = true; break;   // CF Merge
                case 0x07: hasCf = false; hasValue = false; break; // SingleDeletion
                case 0x08: hasCf = true; hasValue = false; break;  // CF SingleDeletion
                case 0x0E: hasCf = true; hasValue = true; break;   // CF RangeDeletion (begin,end)
                case 0x0F: hasCf = false; hasValue = true; break;  // RangeDeletion (begin,end)
                default: return false;                             // unknown — stop, report
            }

            if (hasCf && !TryReadVarint32(data, ref pos, out _)) return false;
            if (!TryReadSlice(data, ref pos, out var keyStart, out var keyLen)) return false;
            var valueLen = 0;
            if (hasValue)
            {
                if (!TryReadSlice(data, ref pos, out _, out valueLen)) return false;
            }

            var entity = EntityOf(data.AsSpan(keyStart, keyLen));
            if (!entities.TryGetValue(entity, out var ops))
                entities[entity] = ops = new EntityOps(entity);

            var isDelete = tag is 0x00 or 0x03 or 0x07 or 0x08 or 0x0E or 0x0F;
            if (isDelete) ops.Deletes++;
            else { ops.Puts++; ops.PutBytes += valueLen; }
        }

        return true;
    }

    /// <summary>
    /// Renders a store key as <c>family name</c> — e.g. <c>queue orders.create</c> — by
    /// undoing the 038 key encoding (family tag byte, then the escaped, 0x00 0x00-terminated
    /// name component).
    /// </summary>
    private static string EntityOf(ReadOnlySpan<byte> key)
    {
        if (key.Length < 2) return "(unrecognized key)";

        var family = key[0] switch
        {
            (byte)'q' => "list",
            (byte)'z' => "ordered-set",
            (byte)'s' => "set",
            (byte)'k' => "kv",
            (byte)'n' => "counter",
            _ => null,
        };
        if (family is null) return "(unrecognized key)";

        var name = new StringBuilder();
        var i = 1;
        while (i < key.Length)
        {
            var b = key[i];
            if (b == 0x00)
            {
                if (i + 1 < key.Length && key[i + 1] == 0xFF) { name.Append('\0'); i += 2; continue; }
                break; // 0x00 0x00 terminator (or truncated) — name complete
            }
            name.Append((char)b);
            i++;
        }

        return $"{family} {name}";
    }

    private static bool TryReadVarint32(byte[] data, ref int pos, out uint value)
    {
        value = 0;
        var shift = 0;
        while (pos < data.Length && shift <= 28)
        {
            var b = data[pos++];
            value |= (uint)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return true;
            shift += 7;
        }
        return false;
    }

    private static bool TryReadSlice(byte[] data, ref int pos, out int start, out int length)
    {
        start = 0;
        length = 0;
        if (!TryReadVarint32(data, ref pos, out var len)) return false;
        if (pos + len > data.Length) return false;
        start = pos;
        length = (int)len;
        pos += (int)len;
        return true;
    }
}
