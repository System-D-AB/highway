using System.Buffers.Binary;

namespace Highway.Server.Storage;

/// <summary>
/// The value framing for <c>SetEx</c> keys (OD5, 038 T5): a value written with an absolute
/// expiry is stored as <c>[0x01 marker][8-byte expiry ticks BE][value]</c>. The marker
/// distinguishes an expiring value from a plain <c>Set</c> value on the same KV family, so a
/// sweep can identify expiring keys without misreading a registration record.
///
/// <para><see cref="Unwrap"/> is the filter-on-read: it returns the value if not yet expired,
/// or <c>null</c> if expired (an expired reply slot reads as gone). <see cref="IsExpired"/>
/// drives the physical sweep. All time comparisons take <c>now</c> as a parameter — the store
/// never reads a clock (037 R5.1).</para>
///
/// <para>Shared by both <see cref="InMemoryStore"/> and the RocksDB store so expiry behaves
/// identically on each — the property the contract suite rests on.</para>
/// </summary>
internal static class ExpiryFraming
{
    private const byte Marker = 0x01;
    private const int HeaderLen = 1 + 8; // marker + ticks

    /// <summary>The KV family prefix (<c>k</c>) a sweep scans for expiring keys.</summary>
    public static readonly byte[] KvFamilyPrefix = [(byte)'k'];

    /// <summary>Wraps a value with its absolute expiry.</summary>
    public static byte[] Wrap(byte[] value, long expiresAtTicks)
    {
        var r = new byte[HeaderLen + value.Length];
        r[0] = Marker;
        BinaryPrimitives.WriteInt64BigEndian(r.AsSpan(1, 8), expiresAtTicks);
        value.CopyTo(r, HeaderLen);
        return r;
    }

    /// <summary>
    /// Returns the unwrapped value if the framed bytes are not expired at
    /// <paramref name="nowTicks"/>; <c>null</c> if <paramref name="framed"/> is null or expired.
    /// A value without the marker is returned as-is (defensive — a plain value read through the
    /// expiry path is simply not an expiring value).
    /// </summary>
    public static byte[]? Unwrap(byte[]? framed, long nowTicks)
    {
        if (framed is null) return null;
        if (framed.Length < HeaderLen || framed[0] != Marker) return framed; // not framed → plain value

        var expiry = BinaryPrimitives.ReadInt64BigEndian(framed.AsSpan(1, 8));
        if (nowTicks >= expiry) return null; // expired → gone
        return framed[HeaderLen..];
    }

    /// <summary>
    /// The absolute expiry ticks of a framed value, or <c>null</c> for a plain (non-expiring)
    /// value. Serves the wire-level TTL read (PTTL on idempotency keys, 040 fixture swap).
    /// </summary>
    public static long? TryReadExpiry(byte[] framed)
        => framed.Length >= HeaderLen && framed[0] == Marker
            ? BinaryPrimitives.ReadInt64BigEndian(framed.AsSpan(1, 8))
            : null;

    /// <summary>True when <paramref name="framed"/> is an expiring value that has expired at <paramref name="nowTicks"/>.</summary>
    public static bool IsExpired(byte[] framed, long nowTicks)
    {
        if (framed.Length < HeaderLen || framed[0] != Marker) return false; // not an expiring value
        var expiry = BinaryPrimitives.ReadInt64BigEndian(framed.AsSpan(1, 8));
        return nowTicks >= expiry;
    }
}
