using System.Text;
using FluentAssertions;
using Highway.Server.Commands.Runtime;
using Xunit;

namespace Highway.Server.Tests.Resp;

/// <summary>
/// 040 T3 — the vendored writer (<c>RespWriteUtils</c>, via <see cref="RespWriter"/>) produces
/// exactly the RESP2 reply bytes HW.* uses, and each reply type round-trips: writer → bytes →
/// a RESP2 reply decoder → the same logical value (R2.2). The request reader
/// (<c>RespReaderTests</c>) only parses inbound arrays-of-bulk-strings; replies are the outbound
/// half, so this test carries a small RESP2 reply decoder to close the loop.
/// </summary>
public class RespWriterRoundTripTests
{
    // ---- exact-bytes assertions (the vendored formatter's output is byte-identical) ----

    [Fact]
    public void SimpleString_IsExactBytes()
        => Write(w => w.SimpleString("OK")).Should().Equal("+OK\r\n"u8.ToArray());

    [Fact]
    public void Error_IsExactBytes()
        => Write(w => w.Error("ERR HW_INVALID_ARG queue is blank"))
            .Should().Equal("-ERR HW_INVALID_ARG queue is blank\r\n"u8.ToArray());

    [Theory]
    [InlineData(0, ":0\r\n")]
    [InlineData(1, ":1\r\n")]
    [InlineData(-1, ":-1\r\n")]
    [InlineData(42, ":42\r\n")]
    [InlineData(long.MaxValue, ":9223372036854775807\r\n")]
    [InlineData(long.MinValue, ":-9223372036854775808\r\n")]
    public void Integer_IsExactBytes(long value, string expected)
        => Write(w => w.Integer(value)).Should().Equal(Encoding.ASCII.GetBytes(expected));

    [Fact]
    public void NullArray_IsExactBytes()
        => Write(w => w.NullArray()).Should().Equal("*-1\r\n"u8.ToArray());

    [Fact]
    public void EmptyArray_IsExactBytes()
        => Write(w => w.EmptyArray()).Should().Equal("*0\r\n"u8.ToArray());

    [Fact]
    public void ThreeIntegers_IsExactBytes()
        => Write(w => w.ThreeIntegers(1, 2, 3)).Should().Equal("*3\r\n:1\r\n:2\r\n:3\r\n"u8.ToArray());

    [Fact]
    public void BulkStringArray_IsExactBytes()
        => Write(w => w.BulkStringArray("id"u8.ToArray(), "payload"u8.ToArray()))
            .Should().Equal("*2\r\n$2\r\nid\r\n$7\r\npayload\r\n"u8.ToArray());

    // ---- round-trip through a RESP2 reply decoder ----------------------------

    [Fact]
    public void SimpleString_RoundTrips()
    {
        var reply = Decode(Write(w => w.SimpleString("REGISTER")));
        reply.Kind.Should().Be(RespKind.SimpleString);
        reply.Text.Should().Be("REGISTER");
    }

    [Fact]
    public void Error_RoundTrips()
    {
        var reply = Decode(Write(w => w.Error("ERR HW_QUEUE_FULL over budget")));
        reply.Kind.Should().Be(RespKind.Error);
        reply.Text.Should().Be("ERR HW_QUEUE_FULL over budget");
    }

    [Fact]
    public void Integer_RoundTrips()
    {
        var reply = Decode(Write(w => w.Integer(-9876543210)));
        reply.Kind.Should().Be(RespKind.Integer);
        reply.Integer.Should().Be(-9876543210);
    }

    [Fact]
    public void NullArray_RoundTrips()
    {
        var reply = Decode(Write(w => w.NullArray()));
        reply.Kind.Should().Be(RespKind.NullArray);
    }

    [Fact]
    public void BulkStringArray_RoundTrips_PreservingBinary()
    {
        var id = "m-1"u8.ToArray();
        var payload = new byte[] { 0x00, (byte)'\r', (byte)'\n', 0xFF };
        var reply = Decode(Write(w => w.BulkStringArray(id, payload)));

        reply.Kind.Should().Be(RespKind.Array);
        reply.Items.Should().HaveCount(2);
        reply.Items![0].Bytes.Should().Equal(id);
        reply.Items![1].Bytes.Should().Equal(payload, "a bulk string is length-prefixed, so embedded CRLF/NUL survive");
    }

    [Fact]
    public void FieldArray_RoundTrips()
    {
        var reply = Decode(Write(w => w.FieldArray([("depth", "3"), ("inFlight", "1")])));
        reply.Kind.Should().Be(RespKind.Array);
        reply.Items.Should().HaveCount(4);
        Text(reply.Items![0]).Should().Be("depth");
        Text(reply.Items![1]).Should().Be("3");
        Text(reply.Items![2]).Should().Be("inFlight");
        Text(reply.Items![3]).Should().Be("1");
    }

    [Fact]
    public void ArrayOfArrays_RoundTrips()
    {
        var rows = new List<IReadOnlyList<byte[]>>
        {
            new[] { "node-1"u8.ToArray(), "5"u8.ToArray() },
            new[] { "node-2"u8.ToArray(), "12"u8.ToArray() },
        };
        var reply = Decode(Write(w => w.ArrayOfArrays(rows)));

        reply.Kind.Should().Be(RespKind.Array);
        reply.Items.Should().HaveCount(2);
        reply.Items![0].Items.Should().HaveCount(2);
        Text(reply.Items![0].Items![0]).Should().Be("node-1");
        Text(reply.Items![1].Items![1]).Should().Be("12");
    }

    // ---- helpers -------------------------------------------------------------

    private static byte[] Write(Action<RespWriter> act)
    {
        var w = new RespWriter();
        act(w);
        w.HasReply.Should().BeTrue();
        return w.ToArray();
    }

    private static string Text(RespValue v) => Encoding.UTF8.GetString(v.Bytes!);

    // -- a minimal RESP2 reply decoder, test-side only --

    private enum RespKind { SimpleString, Error, Integer, BulkString, Array, NullArray, NullBulk }

    private sealed class RespValue
    {
        public RespKind Kind;
        public string? Text;
        public long Integer;
        public byte[]? Bytes;
        public List<RespValue>? Items;
    }

    private static RespValue Decode(byte[] data)
    {
        var pos = 0;
        var value = DecodeOne(data, ref pos);
        pos.Should().Be(data.Length, "the reply must be consumed exactly, no trailing bytes");
        return value;
    }

    private static RespValue DecodeOne(byte[] data, ref int pos)
    {
        var type = (char)data[pos++];
        switch (type)
        {
            case '+':
                return new RespValue { Kind = RespKind.SimpleString, Text = ReadLine(data, ref pos) };
            case '-':
                return new RespValue { Kind = RespKind.Error, Text = ReadLine(data, ref pos) };
            case ':':
                return new RespValue { Kind = RespKind.Integer, Integer = long.Parse(ReadLine(data, ref pos)) };
            case '$':
            {
                var len = int.Parse(ReadLine(data, ref pos));
                if (len < 0) return new RespValue { Kind = RespKind.NullBulk };
                var bytes = data[pos..(pos + len)];
                pos += len;
                ExpectCrlf(data, ref pos);
                return new RespValue { Kind = RespKind.BulkString, Bytes = bytes };
            }
            case '*':
            {
                var count = int.Parse(ReadLine(data, ref pos));
                if (count < 0) return new RespValue { Kind = RespKind.NullArray };
                var items = new List<RespValue>(count);
                for (var i = 0; i < count; i++) items.Add(DecodeOne(data, ref pos));
                return new RespValue { Kind = RespKind.Array, Items = items };
            }
            default:
                throw new FormatException($"unexpected RESP type byte '{type}'");
        }
    }

    private static string ReadLine(byte[] data, ref int pos)
    {
        var start = pos;
        while (data[pos] != (byte)'\r') pos++;
        var s = Encoding.UTF8.GetString(data, start, pos - start);
        ExpectCrlf(data, ref pos);
        return s;
    }

    private static void ExpectCrlf(byte[] data, ref int pos)
    {
        if (data[pos] != (byte)'\r' || data[pos + 1] != (byte)'\n')
            throw new FormatException("expected CRLF");
        pos += 2;
    }
}
