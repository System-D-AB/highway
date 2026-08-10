using System.Buffers.Binary;
using System.Text;

namespace Highway.Server.Internal;

/// <summary>
/// One recurring-job schedule as stored in <c>hw:job:{queue}:schedules</c> (feature 028).
///
/// <para>The sorted set's <b>score</b> is <c>nextFireTicks</c>; the <b>member</b> is this
/// record. Firing therefore replaces the member (new <c>lastFire</c>) at a new score in the
/// same transaction — which is what makes fire-and-re-arm atomic.</para>
///
/// <para><b>Framing, versioned from byte one</b> (the 013 lesson):</para>
/// <code>
/// [u8 version=1][u16 nameLen][name utf8][u16 exprLen][expr utf8][i64 lastFireTicks][i64 nextFireTicks][template payload]
/// (nextFire is ALSO the sorted-set score; carried in the record because the range API
/// returns members without scores -- found by probe, not assumed)
/// </code>
/// </summary>
internal static class JobScheduleRecord
{
    public const byte CurrentVersion = 1;

    public static byte[] Encode(
        string jobName, string expression, long lastFireTicks, long nextFireTicks, ReadOnlySpan<byte> template)
    {
        var name = Encoding.UTF8.GetBytes(jobName);
        var expr = Encoding.UTF8.GetBytes(expression);

        var buf = new byte[1 + 2 + name.Length + 2 + expr.Length + 8 + 8 + template.Length];
        var at = 0;

        buf[at++] = CurrentVersion;
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(at), (ushort)name.Length); at += 2;
        name.CopyTo(buf.AsSpan(at)); at += name.Length;
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(at), (ushort)expr.Length); at += 2;
        expr.CopyTo(buf.AsSpan(at)); at += expr.Length;
        BinaryPrimitives.WriteInt64BigEndian(buf.AsSpan(at), lastFireTicks); at += 8;
        BinaryPrimitives.WriteInt64BigEndian(buf.AsSpan(at), nextFireTicks); at += 8;
        template.CopyTo(buf.AsSpan(at));

        return buf;
    }

    public static void Decode(
        ReadOnlySpan<byte> record,
        out string jobName, out string expression, out long lastFireTicks, out long nextFireTicks,
        out ReadOnlySpan<byte> template)
    {
        if (record.Length < 1 || record[0] != CurrentVersion)
            throw new InvalidDataException(
                $"Job schedule record has version {(record.Length > 0 ? record[0] : -1)}; this build reads " +
                $"version {CurrentVersion}. Refusing rather than misparsing (013's rule).");

        var at = 1;
        var nameLen = BinaryPrimitives.ReadUInt16BigEndian(record.Slice(at)); at += 2;
        jobName = Encoding.UTF8.GetString(record.Slice(at, nameLen)); at += nameLen;
        var exprLen = BinaryPrimitives.ReadUInt16BigEndian(record.Slice(at)); at += 2;
        expression = Encoding.UTF8.GetString(record.Slice(at, exprLen)); at += exprLen;
        lastFireTicks = BinaryPrimitives.ReadInt64BigEndian(record.Slice(at)); at += 8;
        nextFireTicks = BinaryPrimitives.ReadInt64BigEndian(record.Slice(at)); at += 8;
        template = record[at..];
    }

    /// <summary>Just the job name — for the find-by-name scans SET/DEL do.</summary>
    public static string PeekName(ReadOnlySpan<byte> record)
    {
        Decode(record, out var name, out _, out _, out _, out _);
        return name;
    }
}
