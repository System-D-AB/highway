using System.Buffers.Binary;
using System.Text;
using FluentAssertions;
using Highway.Server.Internal;
using Xunit;

namespace Highway.Server.Tests;

/// <summary>
/// Feature 015 T4 — the failure block is an <b>optional trailer</b> on every entry framing.
///
/// <para>013 changed the framings themselves and broke every entry already in storage. This
/// must not: an entry written without a block has to decode byte-for-byte as it always did.
/// That is the property most of these tests exist to hold.</para>
/// </summary>
public class FailureBlockTests
{
    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    private static readonly byte[] JsonPayload =
        Utf8("""{"v":1,"src":"n1","ts":"2026-08-08T00:00:00Z","body":{"Amount":42}}""");

    // ---- the additive property ------------------------------------------------

    [Fact]
    public void EntryWithoutABlock_DecodesExactlyAsBefore()
    {
        var entry = Envelope.EncodeRpcEntry(Utf8("req-1"), JsonPayload, attempts: 3);

        Envelope.DecodeRpcEntry(entry, out var id, out var payload, out var attempts);

        id.ToArray().Should().Equal(Utf8("req-1"));
        payload.ToArray().Should().Equal(JsonPayload, "the payload is the rest, and nothing was added to the rest");
        attempts.Should().Be(3);
        Envelope.TryGetFailureBlock(entry, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void AttachingABlock_LeavesTheUnderlyingEntryUntouched()
    {
        var bare = Envelope.EncodeRpcProcessingEntry(1234L, Utf8("msg-9"), JsonPayload, attempts: 2);
        var block = Envelope.EncodeFailureBlock(Utf8("TimeoutException"), [], Utf8("""{"m":"timed out"}"""));

        var withBlock = Envelope.WithFailureBlock(bare, block);

        // Every field the entry carried is still readable, and the payload does not
        // acquire the block's bytes.
        Envelope.DecodeRpcProcessingEntry(withBlock, out var ticks, out var id, out var payload, out var attempts);
        ticks.Should().Be(1234L);
        id.ToArray().Should().Equal(Utf8("msg-9"));
        payload.ToArray().Should().Equal(JsonPayload);
        attempts.Should().Be(2);

        // And the bare entry is recoverable byte-for-byte.
        Envelope.StripFailureBlock(withBlock).ToArray().Should().Equal(bare);
    }

    [Theory]
    [InlineData("rpc")]
    [InlineData("rpcproc")]
    public void EveryFramingCarriesTheBlockWithoutLosingItsPayload(string framing)
    {
        var block = Envelope.EncodeFailureBlock(Utf8("InvalidOperationException"), Utf8("TimeoutException"), Utf8("detail"));

        byte[] bare = framing switch
        {
            "rpc"       => Envelope.EncodeRpcEntry(Utf8("id"), JsonPayload, 1),
            _           => Envelope.EncodeRpcProcessingEntry(7L, Utf8("id"), JsonPayload, 1),
        };

        var withBlock = Envelope.WithFailureBlock(bare, block);
        ReadOnlySpan<byte> payload;

        switch (framing)
        {
            case "rpc":     Envelope.DecodeRpcEntry(withBlock, out _, out payload, out _); break;
            default:        Envelope.DecodeRpcProcessingEntry(withBlock, out _, out _, out payload, out _); break;
        }

        payload.ToArray().Should().Equal(JsonPayload,
            "the block rides on every framing, because the lease sweep re-encodes between them");
    }

    // ---- the block itself -----------------------------------------------------

    [Fact]
    public void BlockRoundTrips()
    {
        var block = Envelope.EncodeFailureBlock(
            Utf8("System.TimeoutException"), Utf8("System.IO.IOException"), Utf8("""{"stack":"at X()"}"""));

        Envelope.DecodeFailureBlock(block, out var type, out var firstType, out var detail);

        Encoding.UTF8.GetString(type).Should().Be("System.TimeoutException");
        Encoding.UTF8.GetString(firstType).Should().Be("System.IO.IOException");
        Encoding.UTF8.GetString(detail).Should().Be("""{"stack":"at X()"}""");
    }

    [Fact]
    public void EmptyFirstType_MeansTheFailureNeverChangedShape()
    {
        var block = Envelope.EncodeFailureBlock(Utf8("TimeoutException"), [], Utf8("d"));

        Envelope.DecodeFailureBlock(block, out var type, out var firstType, out _);

        firstType.Length.Should().Be(0,
            "storing a copy of the current type would answer nothing - the field exists to say " +
            "the failure CHANGED");
        Encoding.UTF8.GetString(type).Should().Be("TimeoutException");
    }

    [Fact]
    public void ASecondReport_ReplacesTheBlockRatherThanAccumulating()
    {
        var bare = Envelope.EncodeRpcProcessingEntry(1L, Utf8("id"), JsonPayload);

        var once = Envelope.WithFailureBlock(bare,
            Envelope.EncodeFailureBlock(Utf8("A"), [], Utf8("first")));
        var twice = Envelope.WithFailureBlock(once,
            Envelope.EncodeFailureBlock(Utf8("B"), Utf8("A"), Utf8("second")));

        Envelope.TryGetFailureBlock(twice, out var block, out var withoutBlock).Should().BeTrue();
        withoutBlock.ToArray().Should().Equal(bare, "attempts must not stack up trailers");

        Envelope.DecodeFailureBlock(block, out var type, out var firstType, out _);
        Encoding.UTF8.GetString(type).Should().Be("B");
        Encoding.UTF8.GetString(firstType).Should().Be("A");
    }

    // ---- the guards -----------------------------------------------------------

    [Fact]
    public void APayloadEndingInTheMagic_IsNotMistakenForABlock()
    {
        // The one way a trailer can be misread: a payload whose own last bytes look like one.
        // Deliberately construct that, with a length field that cannot fit.
        var hostile = new byte[16];
        BinaryPrimitives.WriteUInt32BigEndian(hostile.AsSpan(8), uint.MaxValue);   // absurd blockLen
        BinaryPrimitives.WriteUInt32BigEndian(hostile.AsSpan(12), Envelope.FailureMagic);

        Envelope.TryGetFailureBlock(hostile, out _, out var withoutBlock).Should().BeFalse(
            "the declared length has to fit inside the entry, or the magic was a coincidence");
        withoutBlock.ToArray().Should().Equal(hostile);
    }

    [Fact]
    public void JsonPayloads_CannotEndInTheMagic()
    {
        // Not a behaviour test - a statement of why the collision window is closed. A JSON
        // envelope ends with '}', so the last trailer byte is chosen to be anything else.
        var magicLastByte = (byte)(Envelope.FailureMagic & 0xFF);

        magicLastByte.Should().NotBe((byte)'}');
        JsonPayload[^1].Should().Be((byte)'}');
    }

    [Fact]
    public void ATruncatedBlock_IsRefusedRatherThanMisread()
    {
        var block = Envelope.EncodeFailureBlock(Utf8("SomeException"), [], Utf8("detail"));
        var truncated = block.AsSpan(0, block.Length - 3).ToArray();

        var decode = () => Envelope.DecodeFailureBlock(truncated, out _, out _, out _);

        decode.Should().Throw<InvalidDataException>().WithMessage("*truncated*");
    }

    [Fact]
    public void AnEntryShorterThanATrailer_IsNotProbedOutOfBounds()
    {
        Envelope.TryGetFailureBlock([1, 2, 3], out _, out var withoutBlock).Should().BeFalse();
        withoutBlock.ToArray().Should().Equal([1, 2, 3]);

        Envelope.TryGetFailureBlock([], out _, out _).Should().BeFalse();
    }
}
