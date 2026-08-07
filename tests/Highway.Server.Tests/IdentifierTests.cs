using System.Text;
using FluentAssertions;
using Highway.Server.Internal;
using Xunit;

namespace Highway.Server.Tests;

/// <summary>
/// Feature 004.1 Task 2 — <see cref="Identifier"/> validation rules.
/// Requirement 3 AC1/AC4: non-empty, length-capped, no C0 control chars, no DEL.
/// </summary>
public class IdentifierTests
{
    private const int MaxBytes = 256;

    [Fact]
    public void IsValid_EmptySpan_ReturnsFalse()
        => Identifier.IsValid(ReadOnlySpan<byte>.Empty, MaxBytes).Should().BeFalse();

    [Fact]
    public void IsValid_SimpleAscii_ReturnsTrue()
        => Identifier.IsValid("orders.create"u8, MaxBytes).Should().BeTrue();

    // -------------------------------------------------------------------------
    // Boundary bytes
    // -------------------------------------------------------------------------

    [Fact]
    public void IsValid_Byte0x1F_Rejected()
        => Identifier.IsValid([0x1F], MaxBytes).Should().BeFalse("0x1F is the last C0 control byte");

    [Fact]
    public void IsValid_Byte0x20_Accepted()
        => Identifier.IsValid([0x20], MaxBytes).Should().BeTrue("0x20 (space) is the first printable byte");

    [Fact]
    public void IsValid_Byte0x7E_Accepted()
        => Identifier.IsValid([0x7E], MaxBytes).Should().BeTrue("0x7E (~) is the last printable ASCII byte");

    [Fact]
    public void IsValid_Byte0x7F_Rejected()
        => Identifier.IsValid([0x7F], MaxBytes).Should().BeFalse("DEL is rejected alongside the C0 range");

    [Fact]
    public void IsValid_HighUtf8Bytes_Accepted()
        => Identifier.IsValid([0x80, 0xC3, 0xA9, 0xFF], MaxBytes).Should().BeTrue("bytes above 0x7F are not control characters");

    // -------------------------------------------------------------------------
    // Embedded control characters (mirror-key corruption vectors)
    // -------------------------------------------------------------------------

    [Fact]
    public void IsValid_EmbeddedNewline_Rejected()
        => Identifier.IsValid("a\nb"u8, MaxBytes).Should().BeFalse("a newline splits mirror-key entries");

    [Fact]
    public void IsValid_EmbeddedTab_Rejected()
        => Identifier.IsValid("a\tb"u8, MaxBytes).Should().BeFalse();

    [Fact]
    public void IsValid_EmbeddedNull_Rejected()
        => Identifier.IsValid("a\0b"u8, MaxBytes).Should().BeFalse();

    [Fact]
    public void IsValid_EmbeddedDel_Rejected()
        => Identifier.IsValid("a\u007fb"u8, MaxBytes).Should().BeFalse();

    // -------------------------------------------------------------------------
    // Length cap
    // -------------------------------------------------------------------------

    [Fact]
    public void IsValid_AtLimit_Accepted()
    {
        var id = new byte[MaxBytes];
        Array.Fill(id, (byte)'a');
        Identifier.IsValid(id, MaxBytes).Should().BeTrue();
    }

    [Fact]
    public void IsValid_OverLimit_Rejected()
    {
        var id = new byte[MaxBytes + 1];
        Array.Fill(id, (byte)'a');
        Identifier.IsValid(id, MaxBytes).Should().BeFalse();
    }

    [Fact]
    public void IsValid_CustomLimit_Enforced()
        => Identifier.IsValid("abcdef"u8, maxBytes: 5).Should().BeFalse();

    // -------------------------------------------------------------------------
    // Multi-byte UTF-8
    // -------------------------------------------------------------------------

    [Fact]
    public void IsValid_MultiByteUtf8_Accepted()
    {
        var id = Encoding.UTF8.GetBytes("ordres-créés-日本語");
        Identifier.IsValid(id, MaxBytes).Should().BeTrue("valid UTF-8 continuation bytes are ≥ 0x80");
    }

    [Fact]
    public void IsValid_MultiByteUtf8_LengthCountsBytesNotCharacters()
    {
        // "é" is 2 bytes in UTF-8 — the cap counts bytes
        var id = Encoding.UTF8.GetBytes(new string('é', 200)); // 400 bytes
        Identifier.IsValid(id, MaxBytes).Should().BeFalse();
    }
}
