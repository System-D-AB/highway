using System.Buffers;
using System.Text;
using FluentAssertions;
using Highway.Server.Resp;
using Xunit;

namespace Highway.Server.Tests.Resp;

/// <summary>
/// 040 T1 — the reader's suite, written before the reader (037 T4.1, R1.1). The contract:
/// <list type="bullet">
///   <item>a frame fed <b>one byte at a time</b> parses identically to the whole frame, returning
///         <see cref="RespReadStatus.Incomplete"/> on every partial and never throwing;</item>
///   <item>a malformed frame returns <see cref="RespReadStatus.ProtocolError"/> with a legible
///         reason and consumes nothing — the reader is stateless, so a bad frame cannot corrupt
///         connection state;</item>
///   <item>a frame whose declared sizes exceed the max is refused, never buffered without bound.</item>
/// </list>
/// </summary>
public class RespReaderTests
{
    private const int MaxFrame = 1024 * 1024;

    // ---- frame builders ------------------------------------------------------

    /// <summary>Builds a RESP2 request frame (array of bulk strings) from string args.</summary>
    private static byte[] Frame(params string[] args)
    {
        var sb = new StringBuilder();
        sb.Append('*').Append(args.Length).Append("\r\n");
        foreach (var a in args)
        {
            var bytes = Encoding.UTF8.GetByteCount(a);
            sb.Append('$').Append(bytes).Append("\r\n").Append(a).Append("\r\n");
        }
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static RespReadStatus ReadWhole(byte[] frame, out IReadOnlyList<byte[]> args, out string? error)
        => RespReader.TryReadCommand(new ReadOnlySequence<byte>(frame), MaxFrame, out args, out _, out error);

    // ---- whole-frame parsing -------------------------------------------------

    [Fact]
    public void WholeFrame_ParsesCommandAndArgs()
    {
        var status = ReadWhole(Frame("HW.CALL", "orders", "req-1", "payload"), out var args, out var error);

        status.Should().Be(RespReadStatus.Complete);
        error.Should().BeNull();
        args.Select(a => Encoding.UTF8.GetString(a)).Should().Equal("HW.CALL", "orders", "req-1", "payload");
    }

    [Fact]
    public void WholeFrame_SingleElement_Parses()
    {
        var status = ReadWhole(Frame("PING"), out var args, out _);
        status.Should().Be(RespReadStatus.Complete);
        args.Should().ContainSingle().Which.Should().Equal("PING"u8.ToArray());
    }

    [Fact]
    public void WholeFrame_EmptyBulkStringArg_Parses()
    {
        // A zero-length argument ($0\r\n\r\n) is legal — it is how an absent optional is sent.
        var status = ReadWhole(Frame("HW.PUBLISH", "news", ""), out var args, out _);
        status.Should().Be(RespReadStatus.Complete);
        args.Should().HaveCount(3);
        args[2].Should().BeEmpty();
    }

    [Fact]
    public void WholeFrame_BinaryPayload_PreservedByteForByte()
    {
        // A payload with embedded CRLF and NUL must survive — length-prefixed, not line-delimited.
        var payload = new byte[] { 0x00, (byte)'\r', (byte)'\n', 0xFF, (byte)'a' };
        var frame = BuildFrameWithRawArg("HW.QSEND", "q", "m1", payload);

        var status = RespReader.TryReadCommand(new ReadOnlySequence<byte>(frame), MaxFrame, out var args, out _, out _);
        status.Should().Be(RespReadStatus.Complete);
        args[3].Should().Equal(payload);
    }

    // ---- byte-by-byte equivalence (the heart of R1.1) ------------------------

    [Theory]
    [InlineData("PING")]
    [InlineData("HW.CALL", "orders", "req-1", "payload")]
    [InlineData("HW.QSEND", "invoices", "m-42", "the-body-with-some-length")]
    [InlineData("AUTH", "default", "s3cr3t")]
    public void ByteByByte_ParsesIdenticallyToWholeFrame(params string[] args)
    {
        var frame = Frame(args);

        // Every strict prefix of the frame must be Incomplete (never Error, never Complete early).
        for (var take = 1; take < frame.Length; take++)
        {
            var partial = new ReadOnlySequence<byte>(frame, 0, take);
            var status = RespReader.TryReadCommand(partial, MaxFrame, out _, out _, out var error);
            status.Should().Be(RespReadStatus.Incomplete,
                $"a {take}-byte prefix of a {frame.Length}-byte frame is not yet a whole frame");
            error.Should().BeNull("a partial frame is not an error");
        }

        // The whole frame parses to the expected args.
        var whole = RespReader.TryReadCommand(new ReadOnlySequence<byte>(frame), MaxFrame, out var parsed, out _, out _);
        whole.Should().Be(RespReadStatus.Complete);
        parsed.Select(a => Encoding.UTF8.GetString(a)).Should().Equal(args);
    }

    [Fact]
    public void ByteByByte_AcrossSegments_ParsesIdentically()
    {
        // Feed the frame as a multi-segment sequence split at every byte boundary — the reader must
        // not assume a single contiguous span.
        var frame = Frame("HW.DEQUEUE", "orders", "worker-1");
        var segmented = SplitEveryByte(frame);

        var status = RespReader.TryReadCommand(segmented, MaxFrame, out var args, out _, out _);
        status.Should().Be(RespReadStatus.Complete);
        args.Select(a => Encoding.UTF8.GetString(a)).Should().Equal("HW.DEQUEUE", "orders", "worker-1");
    }

    [Fact]
    public void Complete_ReportsConsumedPositionAtFrameEnd_LeavingTrailingBytes()
    {
        // Two frames back to back: the first parse consumes exactly the first frame, leaving the second.
        var first = Frame("PING");
        var second = Frame("HW.STATS");
        var both = first.Concat(second).ToArray();

        var status = RespReader.TryReadCommand(new ReadOnlySequence<byte>(both), MaxFrame, out var args, out var consumed, out _);
        status.Should().Be(RespReadStatus.Complete);
        args[0].Should().Equal("PING"u8.ToArray());

        var remaining = new ReadOnlySequence<byte>(both).Slice(consumed);
        remaining.Length.Should().Be(second.Length, "only the first frame was consumed");

        var next = RespReader.TryReadCommand(remaining, MaxFrame, out var nextArgs, out _, out _);
        next.Should().Be(RespReadStatus.Complete);
        nextArgs[0].Should().Equal("HW.STATS"u8.ToArray());
    }

    // ---- malformed corpus ----------------------------------------------------

    [Fact]
    public void Malformed_NotAnArray_IsProtocolError()
    {
        var status = RespReader.TryReadCommand(new ReadOnlySequence<byte>("$4\r\nPING\r\n"u8.ToArray()), MaxFrame, out _, out _, out var error);
        status.Should().Be(RespReadStatus.ProtocolError);
        error.Should().NotBeNull();
    }

    [Fact]
    public void Malformed_NullArray_IsProtocolError()
    {
        var status = RespReader.TryReadCommand(new ReadOnlySequence<byte>("*-1\r\n"u8.ToArray()), MaxFrame, out _, out _, out var error);
        status.Should().Be(RespReadStatus.ProtocolError);
        error.Should().Contain("null array");
    }

    [Fact]
    public void Malformed_EmptyArray_IsProtocolError()
    {
        var status = RespReader.TryReadCommand(new ReadOnlySequence<byte>("*0\r\n"u8.ToArray()), MaxFrame, out _, out _, out var error);
        status.Should().Be(RespReadStatus.ProtocolError);
        error.Should().Contain("empty array");
    }

    [Fact]
    public void Malformed_NonNumericLength_IsProtocolError()
    {
        var status = RespReader.TryReadCommand(new ReadOnlySequence<byte>("*x\r\n"u8.ToArray()), MaxFrame, out _, out _, out var error);
        status.Should().Be(RespReadStatus.ProtocolError);
        error.Should().Contain("integer");
    }

    [Fact]
    public void Malformed_BulkStringWrongTypeByte_IsProtocolError()
    {
        // Array says 1 element but the element is not a bulk string ('+' instead of '$').
        var status = RespReader.TryReadCommand(new ReadOnlySequence<byte>("*1\r\n+OK\r\n"u8.ToArray()), MaxFrame, out _, out _, out var error);
        status.Should().Be(RespReadStatus.ProtocolError);
        error.Should().Contain("'$'");
    }

    [Fact]
    public void Malformed_CrWithoutLf_IsProtocolError()
    {
        var status = RespReader.TryReadCommand(new ReadOnlySequence<byte>("*1\rX"u8.ToArray()), MaxFrame, out _, out _, out var error);
        status.Should().Be(RespReadStatus.ProtocolError);
        error.Should().Contain("LF");
    }

    [Fact]
    public void Malformed_BulkStringMissingTrailingCrlf_IsProtocolError()
    {
        // $4\r\nPING then 'XX' where CRLF should be.
        var status = RespReader.TryReadCommand(new ReadOnlySequence<byte>("*1\r\n$4\r\nPINGXX"u8.ToArray()), MaxFrame, out _, out _, out var error);
        status.Should().Be(RespReadStatus.ProtocolError);
        error.Should().Contain("CRLF");
    }

    [Fact]
    public void Malformed_DoesNotThrow_OnAnyPrefixOfGarbage()
    {
        var garbage = Encoding.ASCII.GetBytes("*3\r\n$999\r\nnot-enough");
        for (var take = 0; take <= garbage.Length; take++)
        {
            var slice = new ReadOnlySequence<byte>(garbage, 0, take);
            var act = () => RespReader.TryReadCommand(slice, MaxFrame, out _, out _, out _);
            act.Should().NotThrow($"prefix length {take} must never throw");
        }
    }

    // ---- max-frame bound -----------------------------------------------------

    [Fact]
    public void OversizedBulkString_IsRefused_NotBuffered()
    {
        const int max = 64;
        // First element is a valid 7-byte command; the second declares a 1 MB bulk string
        // against a 64-byte cap and must be refused before any bytes are buffered.
        var frame = Encoding.ASCII.GetBytes($"*2\r\n$7\r\nHW.QSEN\r\n${1024 * 1024}\r\n");
        var status = RespReader.TryReadCommand(new ReadOnlySequence<byte>(frame), max, out _, out _, out var error);
        status.Should().Be(RespReadStatus.ProtocolError);
        error.Should().Contain("limit");
    }

    [Fact]
    public void OversizedArrayCount_IsRefused()
    {
        const int max = 64;
        var frame = Encoding.ASCII.GetBytes($"*{max + 1}\r\n");
        var status = RespReader.TryReadCommand(new ReadOnlySequence<byte>(frame), max, out _, out _, out var error);
        status.Should().Be(RespReadStatus.ProtocolError);
        error.Should().Contain("limit");
    }

    [Fact]
    public void UnterminatedLength_PastTheBound_IsRefused_NotAwaitedForever()
    {
        const int max = 16;
        // A run of digits longer than the cap with no CRLF: must refuse, not sit on Incomplete.
        var frame = Encoding.ASCII.GetBytes("*" + new string('9', max + 8));
        var status = RespReader.TryReadCommand(new ReadOnlySequence<byte>(frame), max, out _, out _, out var error);
        status.Should().Be(RespReadStatus.ProtocolError);
        error.Should().Contain("limit");
    }

    // ---- helpers -------------------------------------------------------------

    private static byte[] BuildFrameWithRawArg(string cmd, string a1, string a2, byte[] rawLast)
    {
        var head = new StringBuilder();
        head.Append("*4\r\n");
        AppendBulk(head, cmd);
        AppendBulk(head, a1);
        AppendBulk(head, a2);
        head.Append('$').Append(rawLast.Length).Append("\r\n");
        var prefix = Encoding.UTF8.GetBytes(head.ToString());
        return [.. prefix, .. rawLast, (byte)'\r', (byte)'\n'];

        static void AppendBulk(StringBuilder sb, string s)
            => sb.Append('$').Append(Encoding.UTF8.GetByteCount(s)).Append("\r\n").Append(s).Append("\r\n");
    }

    /// <summary>Splits a buffer into a linked sequence of one-byte segments.</summary>
    private static ReadOnlySequence<byte> SplitEveryByte(byte[] data)
    {
        if (data.Length == 0) return ReadOnlySequence<byte>.Empty;

        var first = new ByteSegment(new ReadOnlyMemory<byte>(data, 0, 1));
        var last = first;
        for (var i = 1; i < data.Length; i++)
            last = last.Append(new ReadOnlyMemory<byte>(data, i, 1));
        return new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
    }

    private sealed class ByteSegment : ReadOnlySequenceSegment<byte>
    {
        public ByteSegment(ReadOnlyMemory<byte> memory) => Memory = memory;

        public ByteSegment Append(ReadOnlyMemory<byte> memory)
        {
            var seg = new ByteSegment(memory) { RunningIndex = RunningIndex + Memory.Length };
            Next = seg;
            return seg;
        }
    }
}
