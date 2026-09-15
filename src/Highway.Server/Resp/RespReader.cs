using System.Buffers;

namespace Highway.Server.Resp;

/// <summary>
/// Outcome of one attempt to read a RESP request frame off a pipe (040 T2, R1).
/// </summary>
internal enum RespReadStatus
{
    /// <summary>A whole command frame was parsed; <c>consumed</c> marks its end.</summary>
    Complete,

    /// <summary>The buffer holds only part of a frame — read more bytes and retry. Never an error.</summary>
    Incomplete,

    /// <summary>The bytes are not a valid RESP request frame; the connection must be closed with the error.</summary>
    ProtocolError,
}

/// <summary>
/// The RESP2 request reader — the byte-facing half of the socket Garnet used to be (037 R6.1).
/// It parses <b>an array of bulk strings</b> (the only request shape a Highway client sends: a
/// command name followed by its arguments) off a <see cref="ReadOnlySequence{T}"/> drawn from
/// the Kestrel pipe, one frame at a time.
///
/// <para><b>Tests-first (040 T1).</b> Its contract is fixed by <c>RespReaderTests</c> before the
/// body exists: feeding a frame one byte at a time must parse identically to feeding it whole
/// (<see cref="RespReadStatus.Incomplete"/> on every partial, never a throw), a malformed frame
/// must return <see cref="RespReadStatus.ProtocolError"/> without corrupting any connection
/// state (the reader is stateless — the caller owns the pipe position), and a frame whose
/// declared sizes exceed the max-frame bound must be refused rather than buffered without bound
/// (037 R6.1 / R1.3).</para>
///
/// <para><b>Stateless by design.</b> <see cref="TryReadCommand"/> takes a
/// <see cref="ReadOnlySequence{T}"/> and reports how much it consumed; the caller advances the
/// pipe only on <see cref="RespReadStatus.Complete"/>. Partial input leaves the buffer untouched
/// so the next read simply sees more of the same frame. This is what makes "one byte at a time"
/// equivalent to "all at once" — there is no half-parsed state to corrupt.</para>
/// </summary>
internal static class RespReader
{
    /// <summary>
    /// Attempts to read one command frame (an array of bulk strings) from <paramref name="buffer"/>.
    /// </summary>
    /// <param name="buffer">The bytes available so far.</param>
    /// <param name="maxFrameBytes">
    /// The largest a single bulk string (or the declared array count's implied size) may be before
    /// the frame is refused. Aligned with the server's payload cap so an oversized frame closes the
    /// connection rather than buffering without bound.
    /// </param>
    /// <param name="args">On <see cref="RespReadStatus.Complete"/>, the command name plus arguments, in order.</param>
    /// <param name="consumed">On <see cref="RespReadStatus.Complete"/>, the position just past the frame.</param>
    /// <param name="error">On <see cref="RespReadStatus.ProtocolError"/>, a legible reason.</param>
    public static RespReadStatus TryReadCommand(
        in ReadOnlySequence<byte> buffer,
        int maxFrameBytes,
        out IReadOnlyList<byte[]> args,
        out SequencePosition consumed,
        out string? error)
    {
        args = [];
        consumed = buffer.Start;
        error = null;

        var reader = new SequenceReader<byte>(buffer);

        // A request is always an array: *<count>\r\n
        if (!TryReadTypedLength(ref reader, (byte)'*', maxFrameBytes, out var count, out var status, out error))
            return status;

        if (count < 0)
        {
            // *-1 (null array) is a valid RESP value but never a command — a client that sends it
            // is confused, and we say so rather than dispatching an empty command.
            error = "a command frame must be a non-empty array of bulk strings, not a null array";
            return RespReadStatus.ProtocolError;
        }

        if (count == 0)
        {
            error = "a command frame must name a command; received an empty array";
            return RespReadStatus.ProtocolError;
        }

        var elements = new byte[count][];
        for (var i = 0; i < count; i++)
        {
            if (!TryReadBulkString(ref reader, maxFrameBytes, out elements[i], out status, out error))
                return status;
        }

        args = elements;
        consumed = reader.Position;
        return RespReadStatus.Complete;
    }

    /// <summary>Reads a <c>$&lt;len&gt;\r\n&lt;bytes&gt;\r\n</c> bulk string.</summary>
    private static bool TryReadBulkString(
        ref SequenceReader<byte> reader, int maxFrameBytes,
        out byte[] value, out RespReadStatus status, out string? error)
    {
        value = [];

        if (!TryReadTypedLength(ref reader, (byte)'$', maxFrameBytes, out var len, out status, out error))
            return false;

        if (len < 0)
        {
            // A null bulk string ($-1) is not a valid command argument.
            error = "a command argument cannot be a null bulk string";
            status = RespReadStatus.ProtocolError;
            return false;
        }

        // Need len bytes plus the trailing CRLF.
        if (reader.Remaining < len + 2)
        {
            status = RespReadStatus.Incomplete;
            return false;
        }

        var payload = new byte[len];
        if (!reader.TryCopyTo(payload))
        {
            status = RespReadStatus.Incomplete;
            return false;
        }
        reader.Advance(len);

        if (!TryConsumeCrlf(ref reader, out status, out error))
            return false;

        value = payload;
        status = RespReadStatus.Complete;
        return true;
    }

    /// <summary>
    /// Reads a type byte (<paramref name="typeByte"/>), then a base-10 length terminated by CRLF.
    /// Returns the parsed length (which may be -1 for RESP null). Bounds the length against
    /// <paramref name="maxFrameBytes"/>.
    /// </summary>
    private static bool TryReadTypedLength(
        ref SequenceReader<byte> reader, byte typeByte, int maxFrameBytes,
        out long length, out RespReadStatus status, out string? error)
    {
        length = 0;
        error = null;

        if (!reader.TryPeek(out var prefix))
        {
            status = RespReadStatus.Incomplete;
            return false;
        }

        if (prefix != typeByte)
        {
            error = $"expected RESP type byte '{(char)typeByte}' but found '{Printable(prefix)}'";
            status = RespReadStatus.ProtocolError;
            return false;
        }
        reader.Advance(1);

        // The digits up to CRLF.
        if (!TryReadLine(ref reader, maxFrameBytes, out var line, out status, out error))
            return false;

        if (line.Length == 0)
        {
            error = "missing length after RESP type byte";
            status = RespReadStatus.ProtocolError;
            return false;
        }

        if (!TryParseInt64(line, out length))
        {
            error = "RESP length is not a base-10 integer";
            status = RespReadStatus.ProtocolError;
            return false;
        }

        if (length > maxFrameBytes)
        {
            error = $"RESP frame declares {length} bytes, exceeding the {maxFrameBytes}-byte limit";
            status = RespReadStatus.ProtocolError;
            return false;
        }

        status = RespReadStatus.Complete;
        return true;
    }

    /// <summary>
    /// Reads bytes up to (and consuming) the next CRLF, returning the bytes before it. Enforces
    /// <paramref name="maxFrameBytes"/> on the line length so a client cannot stream an unbounded
    /// run of digits with no terminator.
    /// </summary>
    private static bool TryReadLine(
        ref SequenceReader<byte> reader, int maxFrameBytes,
        out ReadOnlySpan<byte> line, out RespReadStatus status, out string? error)
    {
        line = default;
        error = null;

        // The reader is stateless across calls: on Incomplete the caller discards everything and
        // retries from the buffer's start, so there is nothing to rewind here — a failed line read
        // simply propagates Incomplete and the whole frame is re-parsed next time more bytes arrive.
        if (!reader.TryReadTo(out ReadOnlySequence<byte> lineSeq, (byte)'\r', advancePastDelimiter: false))
        {
            // No CR yet. Refuse rather than wait forever if the unterminated run already blew the
            // cap; otherwise ask for more bytes.
            if (reader.Remaining > maxFrameBytes + 2)
            {
                error = $"RESP line exceeds the {maxFrameBytes}-byte limit with no terminator";
                status = RespReadStatus.ProtocolError;
                return false;
            }
            status = RespReadStatus.Incomplete;
            return false;
        }

        // Consume the CR, then require an LF.
        reader.Advance(1);
        if (!reader.TryRead(out var lf))
        {
            status = RespReadStatus.Incomplete;
            return false;
        }
        if (lf != (byte)'\n')
        {
            error = "RESP line CR was not followed by LF";
            status = RespReadStatus.ProtocolError;
            return false;
        }

        line = lineSeq.IsSingleSegment ? lineSeq.FirstSpan : lineSeq.ToArray();
        status = RespReadStatus.Complete;
        return true;
    }

    /// <summary>Consumes a bare CRLF (the terminator after a bulk string's bytes).</summary>
    private static bool TryConsumeCrlf(ref SequenceReader<byte> reader, out RespReadStatus status, out string? error)
    {
        error = null;
        if (reader.Remaining < 2)
        {
            status = RespReadStatus.Incomplete;
            return false;
        }
        reader.TryRead(out var cr);
        reader.TryRead(out var lf);
        if (cr != (byte)'\r' || lf != (byte)'\n')
        {
            error = "expected CRLF terminating a bulk string";
            status = RespReadStatus.ProtocolError;
            return false;
        }
        status = RespReadStatus.Complete;
        return true;
    }

    private static bool TryParseInt64(ReadOnlySpan<byte> digits, out long value)
    {
        value = 0;
        var negative = false;
        var i = 0;
        if (digits.Length > 0 && digits[0] == (byte)'-')
        {
            negative = true;
            i = 1;
            if (digits.Length == 1) return false;
        }
        for (; i < digits.Length; i++)
        {
            var d = digits[i];
            if (d < (byte)'0' || d > (byte)'9') return false;
            value = value * 10 + (d - (byte)'0');
        }
        if (negative) value = -value;
        return true;
    }

    private static char Printable(byte b) => b is >= 0x20 and < 0x7f ? (char)b : '?';
}
