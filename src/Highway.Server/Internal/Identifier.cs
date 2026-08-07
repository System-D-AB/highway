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
/// Validation operates on raw bytes <b>before</b> any string decode, so no key
/// is ever derived from an invalid value.
/// </para>
/// </summary>
internal static class Identifier
{
    /// <summary>
    /// Validates an identifier: non-empty, at most <paramref name="maxBytes"/>
    /// bytes, and every byte ≥ 0x20 and ≠ 0x7F.
    /// </summary>
    public static bool IsValid(ReadOnlySpan<byte> id, int maxBytes)
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
}
