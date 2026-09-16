using System.Buffers.Binary;
using System.Text;
using Highway.Server.Internal;

namespace Highway.Server.Storage.Rocks;

/// <summary>
/// The herd roster (042-1a D5 / parent R13): who is in the set, at what priority, reachable
/// where. Stored as ordinary KV state at <c>repl:roster</c>, written only by the master —
/// which makes it WAL-ship to every standby for free, so a promoted node already holds the
/// current roster (parent R13.6 by construction).
///
/// <para>Encoding v1 (A-OD4): <c>[u8 format=1][u64 version][u16 count]</c> then per member
/// <c>[u16 len][nodeId utf8][i32 priority][u16 len][endpoint utf8]</c>, big-endian.</para>
/// </summary>
internal sealed record RosterRecord(ulong Version, IReadOnlyList<RosterMember> Members)
{
    public const string StoreName = "repl:roster";
    private const byte FormatVersion = 1;

    public static RosterRecord Empty { get; } = new(0, []);

    public byte[] Encode()
    {
        var u64 = new byte[8];
        var u16 = new byte[2];
        var i32 = new byte[4];

        var buffer = new List<byte>(64) { FormatVersion };
        BinaryPrimitives.WriteUInt64BigEndian(u64, Version);
        buffer.AddRange(u64);
        BinaryPrimitives.WriteUInt16BigEndian(u16, checked((ushort)Members.Count));
        buffer.AddRange(u16);

        foreach (var m in Members)
        {
            WriteString(buffer, m.NodeId);
            BinaryPrimitives.WriteInt32BigEndian(i32, m.Priority);
            buffer.AddRange(i32.ToArray());
            WriteString(buffer, m.Endpoint);
        }

        return [.. buffer];
    }

    public static RosterRecord Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length < 11 || data[0] != FormatVersion)
            throw new StorageFormatException(StoreName);

        var version = BinaryPrimitives.ReadUInt64BigEndian(data[1..9]);
        int count = BinaryPrimitives.ReadUInt16BigEndian(data[9..11]);
        var pos = 11;
        var members = new List<RosterMember>(count);
        for (var i = 0; i < count; i++)
        {
            var nodeId = ReadString(data, ref pos);
            if (pos + 4 > data.Length) throw new StorageFormatException(StoreName);
            var priority = BinaryPrimitives.ReadInt32BigEndian(data.Slice(pos, 4));
            pos += 4;
            var endpoint = ReadString(data, ref pos);
            members.Add(new RosterMember(nodeId, priority, endpoint));
        }

        return new RosterRecord(version, members);
    }

    private static void WriteString(List<byte> buffer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var u16 = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(u16, checked((ushort)bytes.Length));
        buffer.AddRange(u16);
        buffer.AddRange(bytes);
    }

    private static string ReadString(ReadOnlySpan<byte> data, ref int pos)
    {
        if (pos + 2 > data.Length) throw new StorageFormatException(StoreName);
        int len = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(pos, 2));
        pos += 2;
        if (pos + len > data.Length) throw new StorageFormatException(StoreName);
        var s = Encoding.UTF8.GetString(data.Slice(pos, len));
        pos += len;
        return s;
    }
}

internal readonly record struct RosterMember(string NodeId, int Priority, string Endpoint);

/// <summary>
/// The roster's one read and one write, shared by <c>HW.REPL.JOIN</c> and the master's
/// startup self-registration so both apply identical collision semantics: first announcer
/// wins; a re-announce by the same node updates it.
/// </summary>
internal static class RosterStore
{
    public static RosterRecord Read(Highway.Server.Storage.IHighwayStore store)
    {
        using var snap = store.Snapshot();
        var raw = store.Get(snap, Layout.HighwayKeyspace.Kv(RosterRecord.StoreName));
        return raw is null ? RosterRecord.Empty : RosterRecord.Decode(raw);
    }

    /// <summary>
    /// Upserts <paramref name="member"/>; false with the <paramref name="holder"/> named
    /// when its priority is held by a different live member (042-1 R13.3). On success the
    /// new record (version+1) is committed.
    /// </summary>
    public static bool TryUpsert(
        Highway.Server.Storage.IHighwayStore store,
        RosterMember member,
        out RosterRecord updated,
        out string? holder)
    {
        holder = null;

        using var snap = store.Snapshot();
        using var batch = store.NewBatch();
        var key = Layout.HighwayKeyspace.Kv(RosterRecord.StoreName);
        var raw = store.Get(snap, key);
        var roster = raw is null ? RosterRecord.Empty : RosterRecord.Decode(raw);

        foreach (var existing in roster.Members)
        {
            if (existing.Priority == member.Priority
                && !string.Equals(existing.NodeId, member.NodeId, StringComparison.Ordinal))
            {
                holder = existing.NodeId;
                updated = roster;
                return false;
            }
        }

        var members = roster.Members
            .Where(m => !string.Equals(m.NodeId, member.NodeId, StringComparison.Ordinal))
            .Append(member)
            .OrderBy(m => m.Priority)
            .ToList();

        updated = new RosterRecord(roster.Version + 1, members);
        store.Set(batch, key, updated.Encode());
        batch.Commit();
        return true;
    }
}
