using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Highway.Server.Storage.Layout;
using Xunit;

namespace Highway.Server.Tests.Storage;

/// <summary>
/// Property tests for the order-preserving encoders (038 T1, R3.1). The invariants
/// that matter: round-trip fidelity, <b>bytewise comparison equals natural order</b>
/// (the whole reason a RocksDB prefix iterate returns FIFO / by-score order without a
/// custom comparator), and self-delimiting compound keys. These are exercised over the
/// existing draft <see cref="KeyEncoding"/> / <see cref="KeyWriter"/> unchanged.
/// </summary>
public class KeyEncodingTests
{
    // -- helpers: KeyWriter is a ref struct, so wrap each encode in a local --

    private static byte[] EncodeScore(long score)
    {
        Span<byte> buf = stackalloc byte[16];
        var w = new KeyWriter(buf);
        KeyEncoding.WriteScore(score, ref w);
        return w.ToArray();
    }

    private static byte[] EncodeString(string value)
    {
        Span<byte> buf = stackalloc byte[256];
        var w = new KeyWriter(buf);
        KeyEncoding.WriteString(value, ref w);
        return w.ToArray();
    }

    private static byte[] EncodeStringThenString(string a, string b)
    {
        Span<byte> buf = stackalloc byte[256];
        var w = new KeyWriter(buf);
        KeyEncoding.WriteString(a, ref w);
        KeyEncoding.WriteString(b, ref w);
        return w.ToArray();
    }

    private static int Cmp(byte[] x, byte[] y) => x.AsSpan().SequenceCompareTo(y);

    // ---------------------------------------------------------------------
    // Score (int64) — round-trip + byte order == numeric order
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(-1L)]
    [InlineData(long.MaxValue)]
    [InlineData(long.MinValue)]
    [InlineData(638_000_000_000_000_000L)] // a plausible .NET tick count
    [InlineData(-638_000_000_000_000_000L)]
    public void Score_RoundTrips(long value)
    {
        var encoded = EncodeScore(value);
        encoded.Length.Should().Be(8);
        KeyEncoding.ReadScore(encoded).Should().Be(value);
    }

    [Fact]
    public void Score_ByteOrder_EqualsNumericOrder()
    {
        // A representative spread including both signs and the extremes.
        var values = new long[]
        {
            long.MinValue, -1_000_000_000_000L, -5, -1, 0, 1, 5,
            1_000_000_000_000L, 638_000_000_000_000_000L, long.MaxValue,
        };

        // For every ordered pair, the bytewise comparison of the encodings must have
        // the same sign as the numeric comparison. This is THE property the z-family
        // range-by-score depends on.
        for (var i = 0; i < values.Length; i++)
        {
            for (var j = 0; j < values.Length; j++)
            {
                var byteCmp = Math.Sign(Cmp(EncodeScore(values[i]), EncodeScore(values[j])));
                var numCmp = Math.Sign(values[i].CompareTo(values[j]));
                byteCmp.Should().Be(numCmp,
                    $"encoding of {values[i]} vs {values[j]} must order like the numbers");
            }
        }
    }

    [Fact]
    public void Score_NegativesSortBelowPositives()
    {
        Cmp(EncodeScore(-1), EncodeScore(0)).Should().BeNegative();
        Cmp(EncodeScore(long.MinValue), EncodeScore(long.MaxValue)).Should().BeNegative();
    }

    // Sequence encoding is score encoding — assert they are identical so a future
    // divergence is caught.
    [Theory]
    [InlineData(0L)]
    [InlineData(42L)]
    [InlineData(-7L)]
    public void Sequence_IsScoreEncoding(long value)
    {
        Span<byte> buf = stackalloc byte[16];
        var w = new KeyWriter(buf);
        KeyEncoding.WriteSequence(value, ref w);
        var seqBytes = w.ToArray();

        seqBytes.Should().Equal(EncodeScore(value));
        KeyEncoding.ReadSequence(seqBytes).Should().Be(value);
    }

    // ---------------------------------------------------------------------
    // String — ordinal byte order preserved
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("invoices")]
    [InlineData("orders@billing")]
    [InlineData("unicode-→-λ-字")]
    public void String_Encodes_NonEmpty(string value)
    {
        var encoded = EncodeString(value);
        // Always terminated by 0x00 0x00.
        encoded.Length.Should().BeGreaterThanOrEqualTo(2);
        encoded[^1].Should().Be(0x00);
        encoded[^2].Should().Be(0x00);
    }

    [Fact]
    public void String_ByteOrder_EqualsOrdinalOrder()
    {
        var values = new[] { "", "a", "aa", "ab", "b", "invoices", "orders", "orders@billing", "z" };

        for (var i = 0; i < values.Length; i++)
        {
            for (var j = 0; j < values.Length; j++)
            {
                var byteCmp = Math.Sign(Cmp(EncodeString(values[i]), EncodeString(values[j])));
                var ordCmp = Math.Sign(string.CompareOrdinal(values[i], values[j]));
                byteCmp.Should().Be(ordCmp,
                    $"encoding of '{values[i]}' vs '{values[j]}' must order ordinally");
            }
        }
    }

    // ---------------------------------------------------------------------
    // Self-delimiting compound keys — the ("ab","c") != ("a","bc") property
    // ---------------------------------------------------------------------

    [Fact]
    public void Compound_IsSelfDelimiting()
    {
        // The classic ambiguity a naive concatenation would create.
        var abc = EncodeStringThenString("ab", "c");
        var a_bc = EncodeStringThenString("a", "bc");

        abc.Should().NotEqual(a_bc,
            "the terminator must keep ('ab','c') distinct from ('a','bc')");
    }

    [Fact]
    public void Compound_EmbeddedNull_DoesNotCollideWithTerminator()
    {
        // A name containing a literal NUL must not be confusable with the 0x00 0x00
        // terminator — the escape (0x00 -> 0x00 0xFF) exists for exactly this.
        var withNull = EncodeStringThenString("a\0b", "c");
        var twoParts = EncodeStringThenString("a", "b");

        // The point: the NUL inside "a\0b" is escaped, so the first component does not
        // terminate early. Round-trip is not exposed for strings, so we assert the
        // distinctness that matters for keying.
        withNull.Should().NotEqual(twoParts);

        // And the escaped form must still order correctly against a plain name.
        Cmp(EncodeString("a\0b"), EncodeString("ab")).Should().NotBe(0);
    }

    [Fact]
    public void Compound_PrefixName_DoesNotAliasSibling()
    {
        // Under the physical layout, a queue "ab" and a queue "abc" must occupy
        // disjoint key ranges — a DeleteRange over "ab"'s prefix must not touch "abc".
        // The terminator after the name is what guarantees this: "ab"<0x00 0x00> can
        // never be a prefix of "abc"<0x00 0x00> because the 3rd byte differs ('c' vs 0x00).
        var ab = EncodeString("ab");
        var abc = EncodeString("abc");

        StartsWith(abc, ab).Should().BeFalse(
            "encoded 'ab' must not be a prefix of encoded 'abc' — else DeleteRange would over-delete");
    }

    private static bool StartsWith(byte[] whole, byte[] prefix)
        => whole.Length >= prefix.Length && whole.AsSpan(0, prefix.Length).SequenceEqual(prefix);

    // ---------------------------------------------------------------------
    // KeyWriter overflow — the stackalloc -> pooled-array growth path
    // ---------------------------------------------------------------------

    [Fact]
    public void KeyWriter_GrowsBeyondInitialBuffer()
    {
        // Start with a tiny buffer and write far past it, forcing the pooled-array grow.
        var big = new string('x', 500);
        Span<byte> tiny = stackalloc byte[8];
        var w = new KeyWriter(tiny);
        KeyEncoding.WriteString(big, ref w);
        var encoded = w.ToArray();

        // 500 ASCII bytes (no escaping needed) + 2-byte terminator.
        encoded.Length.Should().Be(502);
        encoded[^1].Should().Be(0x00);
        encoded[^2].Should().Be(0x00);
    }

    [Fact]
    public void KeyWriter_Growth_PreservesContent()
    {
        // Same value written into a tiny buffer (grows) and a large buffer (no grow)
        // must produce identical bytes.
        var value = new string('y', 300);

        Span<byte> tiny = stackalloc byte[4];
        var w1 = new KeyWriter(tiny);
        KeyEncoding.WriteString(value, ref w1);
        var fromTiny = w1.ToArray();

        Span<byte> large = stackalloc byte[512];
        var w2 = new KeyWriter(large);
        KeyEncoding.WriteString(value, ref w2);
        var fromLarge = w2.ToArray();

        fromTiny.Should().Equal(fromLarge);
    }
}
