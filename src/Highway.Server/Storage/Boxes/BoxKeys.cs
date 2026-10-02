using System.Buffers.Binary;
using Highway.Server.Storage.Layout;

namespace Highway.Server.Storage.Boxes;

/// <summary>
/// Key encoding for the box layout (feature 065, storage format 4). Every key begins with a 4-byte
/// big-endian box id and a 1-byte space, so a box is one contiguous range and each space inside it is a
/// contiguous sub-range: <c>[box:4][space:1][rest]</c>.
///
/// <para>Box 0 is the system box (catalog, allocator, format, and — under space 0x10 — the family keys of
/// <see cref="HighwayKeyspace"/>). Ids 1–15 are reserved; work boxes start at 16 and are never reused.
/// Sequences and ticks are written big-endian unsigned, so key order is numeric order; both are
/// non-negative by construction.</para>
/// </summary>
internal static class BoxKeys
{
    public const uint SystemBox = 0;
    public const uint FirstWorkBox = 16;

    // ---- system box spaces ----
    public const byte NameIndex = 0x01;      // [kind:1][name] -> box id (4)
    public const byte Meta = 0x02;           // [box:4] -> metadata JSON
    public const byte Allocator = 0x03;      // -> next box id (u64)
    public const byte CatalogVersion = 0x04; // -> u64
    public const byte Format = 0x05;         // -> storage format (u32)
    public const byte Families = 0x10;       // HighwayKeyspace family keys
    public const byte ExpiryIndex = 0x13;    // [expiresAt:8][family key] -> empty

    // ---- work box spaces ----
    public const byte Ready = 0x01;          // [seq:8] -> queue entry
    public const byte InFlight = 0x02;       // [seq:8] -> InFlightRecord
    public const byte Lease = 0x03;          // [claimTicks:8][seq:8] -> empty
    public const byte Delayed = 0x04;        // [due:8][seq:8] -> queue entry
    public const byte Dead = 0x05;           // [seq:8] -> DeadLetter record
    public const byte Counters = 0x06;       // [counter:1] -> i64
    public const byte Workers = 0x07;        // [node] -> last claim ticks (i64)
    public const byte Schedules = 0x08;      // [job] -> JobScheduleRecord
    public const byte ScheduleIndex = 0x09;  // [nextFire:8][job] -> empty
    public const byte NodeIndex = 0x0A;      // [node][seq:8] -> empty

    /// <summary>Box kinds as stored in the name index. Queues include group queues (<c>{channel}@{group}</c>).</summary>
    public enum Kind : byte { Service = 1, Queue = 2, DeadFeed = 3 }

    // ---- builders ----

    /// <summary>The whole box: <c>[box:4]</c>.</summary>
    public static byte[] Box(uint box)
    {
        var k = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(k, box);
        return k;
    }

    /// <summary>One space of a box: <c>[box:4][space]</c> — the prefix every scan of that space is bounded to.</summary>
    public static byte[] Space(uint box, byte space)
    {
        var k = new byte[5];
        BinaryPrimitives.WriteUInt32BigEndian(k, box);
        k[4] = space;
        return k;
    }

    public static byte[] Seq(uint box, byte space, ulong seq)
    {
        var k = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(k, box);
        k[4] = space;
        BinaryPrimitives.WriteUInt64BigEndian(k.AsSpan(5), seq);
        return k;
    }

    public static byte[] TicksSeq(uint box, byte space, long ticks, ulong seq)
    {
        var k = new byte[21];
        BinaryPrimitives.WriteUInt32BigEndian(k, box);
        k[4] = space;
        BinaryPrimitives.WriteUInt64BigEndian(k.AsSpan(5), Unsigned(ticks));
        BinaryPrimitives.WriteUInt64BigEndian(k.AsSpan(13), seq);
        return k;
    }

    /// <summary>A seek position inside a time-ordered space: every key at or after <paramref name="ticks"/>.</summary>
    public static byte[] TicksFloor(uint box, byte space, long ticks)
    {
        var k = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(k, box);
        k[4] = space;
        BinaryPrimitives.WriteUInt64BigEndian(k.AsSpan(5), Unsigned(ticks));
        return k;
    }

    public static byte[] Name(uint box, byte space, string name) => Compose(box, space, name, tail: null);

    public static byte[] NameSeq(uint box, byte space, string name, ulong seq)
    {
        Span<byte> tail = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(tail, seq);
        return Compose(box, space, name, tail);
    }

    public static byte[] TicksName(uint box, byte space, long ticks, string name)
    {
        var head = TicksFloor(box, space, ticks);
        var writer = new KeyWriter(stackalloc byte[256]);
        writer.WriteBytes(head);
        KeyEncoding.WriteString(name, ref writer);
        return writer.ToArray();
    }

    public static byte[] Counter(uint box, byte counter)
    {
        var k = new byte[6];
        BinaryPrimitives.WriteUInt32BigEndian(k, box);
        k[4] = Counters;
        k[5] = counter;
        return k;
    }

    public static byte[] NameIndexKey(Kind kind, string name)
    {
        var writer = new KeyWriter(stackalloc byte[256]);
        writer.WriteBytes([0, 0, 0, 0, NameIndex, (byte)kind]);
        KeyEncoding.WriteString(name, ref writer);
        return writer.ToArray();
    }

    public static byte[] MetaKey(uint box)
    {
        var k = new byte[9];
        k[4] = Meta;
        BinaryPrimitives.WriteUInt32BigEndian(k.AsSpan(5), box);
        return k;
    }

    public static byte[] SystemKey(byte space) => [0, 0, 0, 0, space];

    // ---- decoders ----

    public static uint BoxOf(ReadOnlySpan<byte> key) => BinaryPrimitives.ReadUInt32BigEndian(key);

    /// <summary>The trailing 8-byte sequence of a <see cref="Seq"/>, <see cref="TicksSeq"/> or <see cref="NameSeq"/> key.</summary>
    public static ulong TrailingSeq(ReadOnlySpan<byte> key) => BinaryPrimitives.ReadUInt64BigEndian(key[^8..]);

    /// <summary>The ticks of a <see cref="TicksSeq"/> / <see cref="TicksName"/> key.</summary>
    public static long TicksAt(ReadOnlySpan<byte> key) => (long)BinaryPrimitives.ReadUInt64BigEndian(key.Slice(5, 8));

    /// <summary>The name encoded after the 13-byte head of a <see cref="TicksName"/> key.</summary>
    public static string NameAfterTicks(ReadOnlySpan<byte> key) => KeyEncoding.ReadString(key[13..], out _);

    public static long ReadI64(byte[]? value) => value is { Length: 8 } ? BinaryPrimitives.ReadInt64BigEndian(value) : 0;

    public static byte[] I64(long value)
    {
        var v = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(v, value);
        return v;
    }

    private static ulong Unsigned(long ticks) => ticks <= 0 ? 0UL : (ulong)ticks;

    private static byte[] Compose(uint box, byte space, string name, scoped ReadOnlySpan<byte> tail)
    {
        var writer = new KeyWriter(stackalloc byte[256]);
        Span<byte> head = stackalloc byte[5];
        BinaryPrimitives.WriteUInt32BigEndian(head, box);
        head[4] = space;
        writer.WriteBytes(head);
        KeyEncoding.WriteString(name, ref writer);
        if (!tail.IsEmpty) writer.WriteBytes(tail);
        return writer.ToArray();
    }
}
