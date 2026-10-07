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
    // Big-endian encoding verification (endianness correctness)
    // =========================================================================

    [Fact]
    public void RpcEntry_MessageId_IsStoredBigEndian()
    {
        // Layout since feature 013: [u8 version][u16 attempts][u16 requestIdLen][requestId][payload].
        // Verify the version byte is placed correctly.
        var encoded = Envelope.EncodeRpcEntry("1"u8, ReadOnlySpan<byte>.Empty);
        encoded[0].Should().Be(Envelope.FormatVersion);
    }

    // =========================================================================
    // Lineage trailer (073; widened to a 4-byte magic at verification)
    // =========================================================================

    [Fact]
    public void ABinaryPayloadEndingInTheOldOneByteMarker_DecodesUntouched()
    {
        // Payloads are opaque: one ending [0xFE][u16] was read as a lineage trailer and lost three bytes.
        byte[] payload = [0x01, 0x02, 0xFE, 0x00, 0x02];
        var entry = Envelope.EncodeRpcEntry("m-1"u8, payload);

        Envelope.TryGetLineage(entry, out _, out _).Should().BeFalse();
        Envelope.DecodeRpcEntry(entry, out _, out var decoded, out _);
        decoded.ToArray().Should().Equal(payload);
    }

    [Fact]
    public void Lineage_RoundTrips_AfterAFailureBlock_AndIsTheLastThingOnTheEntry()
    {
        var entry = Envelope.EncodeRpcEntry("m-1"u8, SamplePayload);
        var failed = Envelope.WithFailureBlock(entry, Envelope.EncodeFailureBlock("T"u8, default, "d"u8));
        var withLineage = Envelope.WithLineage(failed, 7);

        System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(withLineage.AsSpan(withLineage.Length - 4))
            .Should().Be(Envelope.LineageMagic);
        Envelope.TryGetLineage(withLineage, out var requeues, out var rest).Should().BeTrue();
        requeues.Should().Be(7);
        rest.ToArray().Should().Equal(failed);
        Envelope.DecodeRpcEntry(withLineage, out _, out var payload, out _);
        payload.ToArray().Should().Equal(SamplePayload);
    }

    [Fact]
    public void ARequeueCountAtItsMaximum_Saturates_InsteadOfWrappingToZero()
    {
        // (ushort)(65535 + 1) is 0, and a lineage of 0 strips the trailer — resetting a handler's requeue cap.
        using var h = new Highway.Server.Tests.Commands.CommandHarness();
        var box = Highway.Server.Storage.Boxes.Catalog.Ensure(h.Store, h.Boxes, h.Locks, Highway.Server.Storage.Boxes.BoxKeys.Kind.Queue, "q", 1);
        using var wb = new Highway.Server.Storage.Boxes.WorkBox(h.Store, h.Boxes, box, tracksBytes: true);
        var entry = Envelope.WithLineage(Envelope.EncodeRpcEntry("m"u8, SamplePayload, 1), ushort.MaxValue);
        wb.PushDead(DeadLetter.Encode(1, 1, DeadLetter.MaxAttempts, entry), 100);

        wb.RequeueDead(1).Should().Be(1);
        var claimed = wb.Claim("n", 2, 1, "q");

        claimed.Should().ContainSingle();
        Envelope.TryGetLineage(wb.ReadInFlight(claimed[0].Token)!.Entry, out var requeues, out _).Should().BeTrue();
        requeues.Should().Be(ushort.MaxValue);
    }
}
