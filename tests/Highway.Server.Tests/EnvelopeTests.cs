using System.Text;
using FluentAssertions;
using Highway.Server.Internal;
using Xunit;

namespace Highway.Server.Tests;

/// <summary>
/// Task 3 — <see cref="Envelope"/> encode/decode round-trips and corrupt-input handling.
/// </summary>
public class EnvelopeTests
{
    private static readonly byte[] SampleRequestId = Encoding.UTF8.GetBytes("req-abc-123");
    private static readonly byte[] SamplePayload   = Encoding.UTF8.GetBytes("{\"hello\":\"world\"}");

    // =========================================================================
    // RPC queue entry
    // =========================================================================

    [Fact]
    public void RpcEntry_RoundTrip_PreservesRequestIdAndPayload()
    {
        var encoded = Envelope.EncodeRpcEntry(SampleRequestId, SamplePayload);

        Envelope.DecodeRpcEntry(encoded, out var requestId, out var payload, out _);

        requestId.ToArray().Should().Equal(SampleRequestId);
        payload.ToArray().Should().Equal(SamplePayload);
    }

    [Fact]
    public void RpcEntry_EmptyPayload_RoundTrips()
    {
        var encoded = Envelope.EncodeRpcEntry(SampleRequestId, ReadOnlySpan<byte>.Empty);

        Envelope.DecodeRpcEntry(encoded, out var requestId, out var payload, out _);

        requestId.ToArray().Should().Equal(SampleRequestId);
        payload.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void RpcEntry_Truncated_ThrowsInvalidDataException()
    {
        var encoded = Envelope.EncodeRpcEntry(SampleRequestId, SamplePayload);
        var truncated = encoded.AsSpan(0, 1).ToArray(); // only 1 byte — can't even read u16

        var act = () => Envelope.DecodeRpcEntry(truncated, out _, out _, out _);
        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void RpcEntry_TruncatedRequestId_ThrowsInvalidDataException()
    {
        // Write a u16 that claims requestIdLen = 100 but only 5 bytes follow.
        var buf = new byte[7];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(buf, 100);
        buf[2] = 0x01; buf[3] = 0x02; buf[4] = 0x03; buf[5] = 0x04; buf[6] = 0x05;

        var act = () => Envelope.DecodeRpcEntry(buf, out _, out _, out _);
        act.Should().Throw<InvalidDataException>();
    }

    // =========================================================================
    // RPC processing entry
    // =========================================================================

    [Fact]
    public void RpcProcessingEntry_RoundTrip_PreservesAllFields()
    {
        var claimTicks = DateTime.UtcNow.Ticks;

        var encoded = Envelope.EncodeRpcProcessingEntry(claimTicks, SampleRequestId, SamplePayload);
        Envelope.DecodeRpcProcessingEntry(encoded, out var ticks, out var requestId, out var payload, out _);

        ticks.Should().Be(claimTicks);
        requestId.ToArray().Should().Equal(SampleRequestId);
        payload.ToArray().Should().Equal(SamplePayload);
    }

    [Fact]
    public void RpcProcessingEntry_Truncated_ThrowsInvalidDataException()
    {
        var encoded = Envelope.EncodeRpcProcessingEntry(42L, SampleRequestId, SamplePayload);
        var truncated = encoded.AsSpan(0, 5).ToArray();

        var act = () => Envelope.DecodeRpcProcessingEntry(truncated, out _, out _, out _, out _);
        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void GetRequestId_ExtractsFromProcessingEntry()
    {
        var claimTicks = DateTime.UtcNow.Ticks;
        var encoded = Envelope.EncodeRpcProcessingEntry(claimTicks, SampleRequestId, SamplePayload);

        var requestId = Envelope.GetRequestId(encoded);

        requestId.ToArray().Should().Equal(SampleRequestId);
    }

    // =========================================================================
    // Channel entry
    // =========================================================================

    [Fact]
    public void ChannelEntry_RoundTrip_PreservesAll()
    {
        const long messageId = 42L;
        var encoded = Envelope.EncodeChannelEntry(messageId, SamplePayload);

        Envelope.DecodeChannelEntry(encoded, out var id, out var payload, out _);

        id.Should().Be(messageId);
        payload.ToArray().Should().Equal(SamplePayload);
    }

    [Fact]
    public void ChannelEntry_EmptyPayload_RoundTrips()
    {
        var encoded = Envelope.EncodeChannelEntry(1L, ReadOnlySpan<byte>.Empty);
        Envelope.DecodeChannelEntry(encoded, out var id, out var payload, out _);
        id.Should().Be(1L);
        payload.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void ChannelEntry_Truncated_ThrowsInvalidDataException()
    {
        var encoded = Envelope.EncodeChannelEntry(1L, SamplePayload);
        var truncated = encoded.AsSpan(0, 4).ToArray();

        var act = () => Envelope.DecodeChannelEntry(truncated, out _, out _, out _);
        act.Should().Throw<InvalidDataException>();
    }

    // =========================================================================
    // =========================================================================



    // =========================================================================
    // Group processing entry
    // =========================================================================

    [Fact]
    public void GroupProcessingEntry_RoundTrip_PreservesAll()
    {
        var receiveTicks = DateTime.UtcNow.Ticks;
        const long messageId = 99L;

        var encoded = Envelope.EncodeGroupProcessingEntry(receiveTicks, messageId, SamplePayload);
        Envelope.DecodeGroupProcessingEntry(encoded, out var rt, out var id, out var payload, out _);

        rt.Should().Be(receiveTicks);
        id.Should().Be(messageId);
        payload.ToArray().Should().Equal(SamplePayload);
    }

    [Fact]
    public void GroupProcessingEntry_Truncated_ThrowsInvalidDataException()
    {
        var encoded = Envelope.EncodeGroupProcessingEntry(1L, 2L, SamplePayload);
        var truncated = encoded.AsSpan(0, 7).ToArray();

        var act = () => Envelope.DecodeGroupProcessingEntry(truncated, out _, out _, out _, out _);
        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void GetMessageId_ExtractsFromGroupProcessingEntry()
    {
        const long messageId = 55L;
        var encoded = Envelope.EncodeGroupProcessingEntry(DateTime.UtcNow.Ticks, messageId, SamplePayload);

        var id = Envelope.GetMessageId(encoded);
        id.Should().Be(messageId);
    }

    // =========================================================================
    // Big-endian encoding verification (endianness correctness)
    // =========================================================================

    [Fact]
    public void ChannelEntry_MessageId_IsStoredBigEndian()
    {
        // Layout since feature 013: [u8 version][u16 attempts][i64 messageId][payload].
        // If the message ID is big-endian, 1 is stored as 00 .. 01 in bytes 3..10.
        var encoded = Envelope.EncodeChannelEntry(1L, ReadOnlySpan<byte>.Empty);
        encoded.Should().HaveCountGreaterThanOrEqualTo(11);
        encoded[0].Should().Be(Envelope.FormatVersion);
        encoded[1..3].Should().Equal(new byte[] { 0, 0 }, "attempts default to zero");
        encoded[3..11].Should().Equal(new byte[] { 0, 0, 0, 0, 0, 0, 0, 1 });
    }

}
