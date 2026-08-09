namespace Highway.Server.Internal;

/// <summary>
/// Validation rules for Highway identifiers (service, channel, group, node,
/// request, and message identifiers). Payloads are exempt — they remain
/// byte-for-byte opaque.
///
/// <para>
/// The rules exist because mirror keys (<c>hw:svc:{service}:nodelist</c>,
/// <c>hw:ch:{channel}:grplist</c>) are newline-delimited strings: an identifier
/// containing <c>\n</c> splits into two entries and silently corrupts routing.
/// Banning the whole C0 control range plus DEL is cheaper to reason about than
/// banning <c>\n</c> alone and costs nothing for real identifiers.
/// </para>
///
/// <para>
/// <b>Feature 018:</b> <c>@</c> (0x40) is reserved for derived queue names
/// (<c>{channel}@{group}</c>). Without the reservation, a user-declared queue
/// named <c>orders.placed@billing</c> would collide with the <c>billing</c>
/// group of the <c>orders.placed</c> channel.
/// </para>
///
/// <para>
/// Validation operates on raw bytes <b>before</b> any string decode, so no key
/// is ever derived from an invalid value.
/// </para>
/// </summary>
internal static class Identifier
{
    /// <summary>The byte value of <c>@</c>, reserved for derived group-queue names (feature 018).</summary>
    public const byte AtSign = (byte)'@';

    /// <summary>
    /// Validates an identifier: non-empty, at most <paramref name="maxBytes"/>
    /// bytes, every byte ≥ 0x20, ≠ 0x7F, and ≠ <c>@</c> (0x40).
    /// </summary>
    public static bool IsValid(ReadOnlySpan<byte> id, int maxBytes)
    {
        if (id.IsEmpty || id.Length > maxBytes)
            return false;

        foreach (var b in id)
        {
            if (b < 0x20 || b == 0x7F || b == AtSign)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Validates a derived identifier that may contain <c>@</c> (feature 018).
    /// Used by consumption commands (<c>HW.QCLAIM</c>, <c>HW.QACK</c>, <c>HW.DLQ</c>,
    /// <c>HW.FAIL</c>) that operate on both user-declared queues and derived
    /// group queues (<c>{channel}@{group}</c>).
    /// </summary>
    public static bool IsValidAllowingAt(ReadOnlySpan<byte> id, int maxBytes)
    {
        if (id.IsEmpty || id.Length > maxBytes)
            return false;

        foreach (var b in id)
        {
            if (b < 0x20 || b == 0x7F)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Whether the rejection was specifically because of the reserved <c>@</c>
    /// character, so the error message can name the cause.
    /// </summary>
    public static bool ContainsAtSign(ReadOnlySpan<byte> id)
    {
        foreach (var b in id)
        {
            if (b == AtSign)
                return true;
        }
        return false;
    }
}
