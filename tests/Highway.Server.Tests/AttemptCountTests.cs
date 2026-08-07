using System.Buffers.Binary;
using FluentAssertions;
using Highway.Server.Internal;
using Xunit;

namespace Highway.Server.Tests;

/// <summary>
/// Feature 013, T1 and T2 — the delivery-attempt count and the pre-013 storage guard.
///
/// <para>The count is what bounds redelivery: without it a permanently failing message is
/// requeued for the life of the deployment, and — the queue being FIFO — is retried ahead
/// of everything behind it.</para>
/// </summary>
public class AttemptCountTests
{
    private static byte[] Id(string s) => System.Text.Encoding.UTF8.GetBytes(s);

    [Fact]
    public void RpcEntry_RoundTripsTheAttemptCount()
    {
        var encoded = Envelope.EncodeRpcEntry(Id("req-1"), "body"u8, attempts: 3);
        Envelope.DecodeRpcEntry(encoded, out var requestId, out var payload, out var attempts);

        attempts.Should().Be(3);
        requestId.ToArray().Should().Equal(Id("req-1"));
        payload.ToArray().Should().Equal("body"u8.ToArray());
    }

    [Fact]
    public void RpcProcessingEntry_RoundTripsTheAttemptCount()
    {
        var encoded = Envelope.EncodeRpcProcessingEntry(1234L, Id("req-2"), "body"u8, attempts: 7);
        Envelope.DecodeRpcProcessingEntry(
            encoded, out var claimTicks, out var requestId, out var payload, out var attempts);

        claimTicks.Should().Be(1234L);
        attempts.Should().Be(7);
        requestId.ToArray().Should().Equal(Id("req-2"));
        payload.ToArray().Should().Equal("body"u8.ToArray());
    }

    [Fact]
    public void ChannelEntry_RoundTripsTheAttemptCount()
    {
        var encoded = Envelope.EncodeChannelEntry(42L, "body"u8, attempts: 2);
        Envelope.DecodeChannelEntry(encoded, out var messageId, out var payload, out var attempts);

        messageId.Should().Be(42L);
        attempts.Should().Be(2);
        payload.ToArray().Should().Equal("body"u8.ToArray());
    }

    [Fact]
    public void GroupProcessingEntry_RoundTripsTheAttemptCount()
    {
        var encoded = Envelope.EncodeGroupProcessingEntry(99L, 42L, "body"u8, attempts: 5);
        Envelope.DecodeGroupProcessingEntry(
            encoded, out var receiveTicks, out var messageId, out var payload, out var attempts);

        receiveTicks.Should().Be(99L);
        messageId.Should().Be(42L);
        attempts.Should().Be(5);
        payload.ToArray().Should().Equal("body"u8.ToArray());
    }

    [Fact]
    public void AttemptCount_DefaultsToZero()
    {
        Envelope.DecodeRpcEntry(
            Envelope.EncodeRpcEntry(Id("r"), "b"u8), out _, out _, out var attempts);

        attempts.Should().Be(0, "a first enqueue is not a delivery attempt");
    }

    /// <summary>
    /// A wrapped counter would silently restore the infinite-retry bug this field exists
    /// to fix, so the count saturates instead.
    /// </summary>
    [Fact]
    public void AttemptCount_Saturates_RatherThanWrapping()
    {
        Envelope.NextAttempt(0).Should().Be(1);
        Envelope.NextAttempt(Envelope.MaxAttempts).Should().Be(Envelope.MaxAttempts);
        Envelope.NextAttempt((ushort)(Envelope.MaxAttempts - 1)).Should().Be(Envelope.MaxAttempts);
    }

    // -------------------------------------------------------------------------
    // T2 — the pre-013 storage guard
    // -------------------------------------------------------------------------

    /// <summary>
    /// The whole point of the version byte. A pre-013 entry read as a current one does not
    /// fail on its own — it reinterprets its leading bytes and produces a corrupt payload,
    /// which is far worse than an error.
    /// </summary>
    [Fact]
    public void LegacyRpcEntry_IsRefused_NotMisparsed()
    {
        // Pre-013 layout: [u16 requestIdLen][requestId][payload]
        var requestId = Id("req-legacy");
        var legacy = new byte[2 + requestId.Length + 4];
        BinaryPrimitives.WriteUInt16BigEndian(legacy, (ushort)requestId.Length);
        requestId.CopyTo(legacy.AsSpan(2));
        "body"u8.CopyTo(legacy.AsSpan(2 + requestId.Length));

        Envelope.IsLegacyEntry(legacy).Should().BeTrue();

        var act = () => Envelope.DecodeRpcEntry(legacy, out _, out _, out _);
        act.Should().Throw<InvalidDataException>()
            .WithMessage("*pre-013 storage format*");
    }

    [Fact]
    public void LegacyChannelEntry_IsRefused_NotMisparsed()
    {
        // Pre-013 layout: [i64 messageId][payload]
        var legacy = new byte[8 + 4];
        BinaryPrimitives.WriteInt64BigEndian(legacy, 7L);
        "body"u8.CopyTo(legacy.AsSpan(8));

        Envelope.IsLegacyEntry(legacy).Should().BeTrue();

        var act = () => Envelope.DecodeChannelEntry(legacy, out _, out _, out _);
        act.Should().Throw<InvalidDataException>()
            .WithMessage("*pre-013 storage format*");
    }

    [Fact]
    public void CurrentEntries_AreNotMistakenForLegacy()
    {
        Envelope.IsLegacyEntry(Envelope.EncodeRpcEntry(Id("r"), "b"u8)).Should().BeFalse();
        Envelope.IsLegacyEntry(Envelope.EncodeChannelEntry(1L, "b"u8)).Should().BeFalse();
        Envelope.IsLegacyEntry(Envelope.EncodeRpcProcessingEntry(1L, Id("r"), "b"u8)).Should().BeFalse();
        Envelope.IsLegacyEntry(Envelope.EncodeGroupProcessingEntry(1L, 1L, "b"u8)).Should().BeFalse();
    }

    /// <summary>
    /// The version byte only stays unambiguous while a legacy RPC entry's leading byte —
    /// the high half of a u16 identifier length — cannot reach 0xFF. Guards the reasoning
    /// rather than trusting it to stay true if the identifier cap is ever raised.
    /// </summary>
    [Fact]
    public void DefaultIdentifierCap_KeepsTheVersionByteUnambiguous()
        => new HighwayServerOptions().MaxIdentifierBytes
            .Should().BeLessThan(Envelope.MaxUnambiguousIdentifierBytes);

}
