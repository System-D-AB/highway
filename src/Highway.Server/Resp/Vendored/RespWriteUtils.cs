// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.
//
// -----------------------------------------------------------------------------
// VENDORED from Garnet: libs/common/RespWriteUtils.cs (and the NumUtils digit
// primitives it depends on), feature 040 (037 D4 / R2.3 / R6.2).
//
// This is the ONE Garnet-derived source in the repository (037 R3.2). It is the
// RESP output formatter — pure byte writing, zero Tsavorite/storage coupling —
// which 037 D4 chose to copy rather than re-implement because it is free and
// low-risk. The reader (RespReader.cs) is ours; only the writer is vendored.
//
// Faithful subset: the method BODIES below are copied verbatim from the Garnet
// source (same RESP framing, byte-for-byte). Omitted are the methods Highway's
// replies never emit (maps, sets, push, RESP3 nulls/bools, doubles, verbatim
// strings, the padded/bulk-integer helpers) and the `MemoryResult<byte>` /
// `RespStrings` overloads, which reference Garnet types this project does not
// carry. The `NumUtils.CountDigits`/`WriteInt32`/`WriteInt64` primitives are
// copied verbatim from Garnet's libs/common/NumUtils.cs so the file compiles
// standalone. Namespace changed to Highway.Server.Resp.Vendored; nothing else.
//
// THIRD-PARTY-NOTICES.md carries the attribution entry.
// -----------------------------------------------------------------------------

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Highway.Server.Resp.Vendored;

/// <summary>
/// Utilities for writing RESP protocol (vendored from Garnet — see the file header).
/// </summary>
public static unsafe class RespWriteUtils
{
    /// <summary>
    /// Writes an array length
    /// </summary>
    public static bool TryWriteArrayLength(int len, ref byte* curr, byte* end)
    {
        var numDigits = NumUtils.CountDigits(len);
        var totalLen = 1 + numDigits + 2;
        if (totalLen > (int)(end - curr))
            return false;
        *curr++ = (byte)'*';
        NumUtils.WriteInt32(len, numDigits, ref curr);
        WriteNewline(ref curr);
        return true;
    }

    /// <summary>
    /// Writes a RESP2 null array (*-1\r\n)
    /// </summary>
    public static bool TryWriteNullArray(ref byte* curr, byte* end)
    {
        if (5 > (int)(end - curr))
            return false;

        *curr++ = (byte)'*';
        WriteBytes<uint>(ref curr, "-1\r\n"u8);
        return true;
    }

    /// <summary>
    /// Writes a simple string
    /// </summary>
    /// <param name="simpleString">An ASCII encoded simple string. The string mustn't contain a CR (\r) or LF (\n) bytes.</param>
    public static bool TryWriteSimpleString(ReadOnlySpan<byte> simpleString, ref byte* curr, byte* end)
    {
        // Simple strings are of the form "+OK\r\n"
        var totalLen = 1 + simpleString.Length + 2;
        if (totalLen > (int)(end - curr))
            return false;

        *curr++ = (byte)'+';
        simpleString.CopyTo(new Span<byte>(curr, simpleString.Length));
        curr += simpleString.Length;
        WriteNewline(ref curr);
        return true;
    }

    /// <summary>
    /// Write simple error
    /// </summary>
    /// <param name="errorString">An ASCII encoded error string. The string mustn't contain a CR (\r) or LF (\n) bytes.</param>
    public static bool TryWriteError(ReadOnlySpan<byte> errorString, ref byte* curr, byte* end)
    {
        var totalLen = 1 + errorString.Length + 2;
        if (totalLen > (int)(end - curr))
            return false;

        *curr++ = (byte)'-';
        errorString.CopyTo(new Span<byte>(curr, errorString.Length));
        curr += errorString.Length;
        WriteNewline(ref curr);
        return true;
    }

    /// <summary>
    /// Write length header of bulk string
    /// </summary>
    public static bool TryWriteBulkStringLength(int len, ref byte* curr, byte* end)
    {
        var itemDigits = NumUtils.CountDigits(len);
        var totalLen = 1 + itemDigits + 2;
        if (totalLen > (int)(end - curr))
            return false;

        *curr++ = (byte)'$';
        NumUtils.WriteInt32(len, itemDigits, ref curr);
        WriteNewline(ref curr);
        return true;
    }

    /// <summary>
    /// Write bulk string
    /// </summary>
    public static bool TryWriteBulkString(ReadOnlySpan<byte> item, ref byte* curr, byte* end)
    {
        var itemDigits = NumUtils.CountDigits(item.Length);
        int totalLen = 1 + itemDigits + 2 + item.Length + 2;
        if (totalLen > (int)(end - curr))
            return false;

        *curr++ = (byte)'$';
        NumUtils.WriteInt32(item.Length, itemDigits, ref curr);
        WriteNewline(ref curr);
        item.CopyTo(new Span<byte>(curr, item.Length));
        curr += item.Length;
        WriteNewline(ref curr);
        return true;
    }

    /// <summary>
    /// Write integer
    /// </summary>
    public static bool TryWriteInt32(int value, ref byte* curr, byte* end)
    {
        var integerLen = NumUtils.CountDigits((long)value);
        var sign = (byte)(value < 0 ? 1 : 0);

        var totalLen = 1 + sign + integerLen + 2;
        if (totalLen > (int)(end - curr))
            return false;

        *curr++ = (byte)':';
        NumUtils.WriteInt32(value, integerLen, ref curr);
        WriteNewline(ref curr);
        return true;
    }

    /// <summary>
    /// Write integer
    /// </summary>
    public static bool TryWriteInt64(long value, ref byte* curr, byte* end)
    {
        var integerLen = NumUtils.CountDigits(value);
        var sign = (byte)(value < 0 ? 1 : 0);

        var totalLen = 1 + sign + integerLen + 2;
        if (totalLen > (int)(end - curr))
            return false;

        *curr++ = (byte)':';
        NumUtils.WriteInt64(value, integerLen, ref curr);
        WriteNewline(ref curr);
        return true;
    }

    /// <summary>
    /// Write empty array
    /// </summary>
    public static bool TryWriteEmptyArray(ref byte* curr, byte* end)
    {
        if (4 > (int)(end - curr))
            return false;

        WriteBytes<uint>(ref curr, "*0\r\n"u8);
        return true;
    }

    /// <summary>
    /// Writes the contents of <paramref name="span"/> directly to <paramref name="curr"/>.
    /// </summary>
    public static bool TryWriteDirect(ReadOnlySpan<byte> span, ref byte* curr, byte* end)
    {
        if (span.Length > (int)(end - curr))
            return false;

        span.CopyTo(new Span<byte>(curr, span.Length));
        curr += span.Length;
        return true;
    }

    /// <summary>
    /// Writes newline (\r\n) to <paramref name="curr"/>
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void WriteNewline(ref byte* curr) => WriteBytes<ushort>(ref curr, "\r\n"u8);

    /// <summary>
    /// Writes <paramref name="bytes"/> to <paramref name="curr"/> as type <typeparamref name="T"/> sized value.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteBytes<T>(ref byte* curr, ReadOnlySpan<byte> bytes)
        where T : unmanaged
    {
        Unsafe.WriteUnaligned(curr, MemoryMarshal.Read<T>(bytes));
        curr += sizeof(T);
    }
}

/// <summary>
/// The integer digit-counting and writing primitives <see cref="RespWriteUtils"/> depends on,
/// vendored verbatim from Garnet's <c>libs/common/NumUtils.cs</c> (see the file header).
/// </summary>
internal static unsafe class NumUtils
{
    /// <summary>
    /// Writes 32-bit signed integer as ASCII.
    /// </summary>
    public static void WriteInt32(int value, int length, ref byte* result)
    {
        var isNegative = value < 0;
        if (value == 0)
        {
            *result++ = (byte)'0';
            return;
        }

        long v = value;
        if (isNegative)
        {
            *result++ = (byte)'-';
            v = -value;
        }

        result += length;
        do
        {
            *--result = (byte)((byte)'0' + (v % 10));
            v /= 10;
        } while (v > 0);
        result += length;
    }

    /// <summary>
    /// Writes 64-bit signed integer as ASCII.
    /// </summary>
    public static void WriteInt64(long value, int length, ref byte* result)
    {
        var isNegative = value < 0;
        if (value == long.MinValue)
        {
            *(long*)(result) = 3618417120593983789L;
            *(long*)(result + 8) = 3978706198986109744L;
            *(int*)(result + 8 + 8) = 942684213;
            result += 20;
            return;
        }

        if (value == 0)
        {
            *result++ = (byte)'0';
            return;
        }

        if (isNegative)
        {
            *result++ = 0x2d;
            value = -value;
        }

        result += length;
        do
        {
            *--result = (byte)((byte)'0' + (value % 10));
            value /= 10;
        } while (value > 0);
        result += length;
    }

    /// <summary>
    /// Counts the number of digits in a given integer. Doesn't count the sign as a digit.
    /// </summary>
    public static int CountDigits(int value)
    {
        value = value < 0 ? ((~value) + 1) : value;

        if (value < 10) return 1;
        if (value < 100) return 2;
        if (value < 1000) return 3;
        if (value < 100000000L)
        {
            if (value < 1000000)
            {
                if (value < 10000) return 4;
                return 5 + (value >= 100000 ? 1 : 0);
            }
            return 7 + (value >= 10000000L ? 1 : 0);
        }
        return 9 + (value >= 1000000000L ? 1 : 0);
    }

    /// <inheritdoc cref="CountDigits(int)"/>
    public static int CountDigits(long value)
    {
        if (value == long.MinValue) return 19;
        value = value < 0 ? -value : value;

        if (value < 10000000000L)//1 - 10000000000L
        {
            if (value < 100000) //1 - 100000
            {
                if (value < 100) //1 - 100
                {
                    if (value < 10) return 1; else return 2;
                }
                else//100 - 100000
                {
                    if (value < 10000)//100 - 10000
                    {
                        if (value < 1000) return 3; else return 4;
                    }
                    else//10000 - 100000
                    {
                        return 5;
                    }
                }
            }
            else // 100 000 - 10 000 000 000L
            {
                if (value < 10000000) // 100 000 - 10 000 000
                {
                    if (value < 1000000) return 6; else return 7;
                }
                else // 10 000 000 - 10 000 000 000L
                {
                    if (value < 1000000000)
                    {
                        if (value < 100000000) return 8; else return 9;
                    }
                    else // 1 000 000 000 - 10 000 000 000L
                    {
                        return 10;
                    }
                }
            }
        }
        else // 10 000 000 000L - 1 000 000 000 000 000 000L
        {
            if (value < 100000000000000L) //10 000 000 000L - 100 000 000 000 000L
            {
                if (value < 1000000000000L) // 10 000 000 000L - 1 000 000 000 000L
                {
                    if (value < 100000000000L) return 11; else return 12;
                }
                else // 1 000 000 000 000L - 100 000 000 000 000L
                {
                    if (value < 10000000000000L) // 1 000 000 000 000L - 10 000 000 000 000L
                    {
                        return 13;
                    }
                    else
                    {
                        return 14;
                    }
                }
            }
            else//100 000 000 000 000L - 1 000 000 000 000 000 000L
            {
                if (value < 10000000000000000L)//100 000 000 000 000L - 10 000 000 000 000 000L
                {
                    if (value < 1000000000000000L) return 15; else return 16;
                }
                else
                {
                    if (value < 1000000000000000000L)
                    {
                        if (value < 100000000000000000L) return 17; else return 18;
                    }
                    else
                    {
                        return 19;
                    }
                }
            }
        }
    }
}
