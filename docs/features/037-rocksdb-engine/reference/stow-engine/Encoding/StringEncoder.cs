using System;

namespace Stow.Storage.Encoding;

/// <summary>
/// Order-preserving string encoder.
///
/// Encoding: UTF-8 bytes with escaping:
///   Every 0x00 byte in the UTF-8 → 0x00 0xFF
///   Terminated with 0x00 0x00
///
/// This ensures:
///   1. No embedded NUL can be confused with the terminator.
///   2. Compound keys are self-delimiting: ("ab","c") ≠ ("a","bc").
///   3. Ordinal order is preserved (byte comparison = string comparison for ordinal).
/// </summary>
public static class StringEncoder
{
    public const int Version = 1;

    public static void Write(string value, ref KeyWriter writer)
    {
        Write(value, CollationMode.Ordinal, null, ref writer);
    }

    public static void Write(string value, CollationMode mode, string cultureTag, ref KeyWriter writer)
    {
        ReadOnlySpan<byte> raw;

        switch (mode)
        {
            case CollationMode.Ordinal:
                raw = GetUtf8Bytes(value);
                WriteEscapedAndTerminated(raw, ref writer);
                break;

            case CollationMode.OrdinalCaseInsensitive:
                raw = GetUtf8Bytes(value.ToLowerInvariant());
                WriteEscapedAndTerminated(raw, ref writer);
                break;

            case CollationMode.Culture:
                WriteCultureSortKey(value, cultureTag, ref writer);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(mode));
        }
    }

    /// <summary>
    /// Writes the escaped bytes used to bound a starts-with query, without the string
    /// terminator. Culture sort keys deliberately have no partial form because they do not
    /// preserve the source string's prefix relationship (C29).
    /// </summary>
    public static void WritePrefix(string value, CollationMode mode, ref KeyWriter writer)
    {
        ReadOnlySpan<byte> raw = mode switch
        {
            CollationMode.Ordinal => GetUtf8Bytes(value),
            CollationMode.OrdinalCaseInsensitive => GetUtf8Bytes(value.ToLowerInvariant()),
            CollationMode.Culture => throw new NotSupportedException(
                "Culture-collated strings cannot be used for prefix queries (C29)."),
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };

        WriteEscaped(raw, ref writer);
    }

    private static byte[] GetUtf8Bytes(string value)
    {
        return System.Text.Encoding.UTF8.GetBytes(value);
    }

    private static void WriteCultureSortKey(string value, string cultureTag, ref KeyWriter writer)
    {
        var culture = System.Globalization.CultureInfo.GetCultureInfo(cultureTag);
        var sortKey = culture.CompareInfo.GetSortKey(value);
        var keyData = sortKey.KeyData;
        // Culture sort keys are already self-terminating (end with 0x01 0x01 0x00),
        // but we still use our own termination for consistency in compound keys.
        WriteEscapedAndTerminated(keyData, ref writer);
    }

    internal static void WriteEscapedAndTerminated(ReadOnlySpan<byte> raw, ref KeyWriter writer)
    {
        WriteEscaped(raw, ref writer);

        // Terminator: 0x00 0x00
        writer.WriteByte(0x00);
        writer.WriteByte(0x00);
    }

    private static void WriteEscaped(ReadOnlySpan<byte> raw, ref KeyWriter writer)
    {
        for (int i = 0; i < raw.Length; i++)
        {
            byte b = raw[i];
            if (b == 0x00)
            {
                writer.WriteByte(0x00);
                writer.WriteByte(0xFF);
            }
            else
            {
                writer.WriteByte(b);
            }
        }
    }
}
