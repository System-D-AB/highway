using System.Text;
using FluentAssertions;
using Highway.Server.Internal;
using Xunit;

namespace Highway.Server.Tests;

/// <summary>
/// Feature 006 Task 10 — the registration record. Its whole purpose is letting
/// the liveness form refresh <c>seen</c> without touching the catalog, so that
/// is what these assert hardest.
/// </summary>
public class NodeRegistrationTests
{
    private static readonly byte[] Catalog =
        Encoding.UTF8.GetBytes("""{"services":[{"name":"orders.create"}],"channels":[]}""");

    [Fact]
    public void EncodeDecode_RoundTrips()
    {
        var encoded = NodeRegistration.Encode(12345L, Catalog);

        NodeRegistration.Decode(encoded, out var seen, out var catalog);

        seen.Should().Be(12345L);
        catalog.ToArray().Should().Equal(Catalog);
    }

    [Fact]
    public void Encode_EmptyCatalog_RoundTrips()
    {
        var encoded = NodeRegistration.Encode(1L, []);

        NodeRegistration.Decode(encoded, out var seen, out var catalog);

        seen.Should().Be(1L);
        catalog.Length.Should().Be(0, "a node that hosts nothing is a valid registration");
    }

    [Fact]
    public void Touch_RefreshesTimestamp_AndPreservesCatalogByteForByte()
    {
        var original = NodeRegistration.Encode(1000L, Catalog);

        var touched = NodeRegistration.Touch(original, 9999L);

        NodeRegistration.Decode(touched, out var seen, out var catalog);
        seen.Should().Be(9999L);
        catalog.ToArray().Should().Equal(Catalog,
            "the liveness form must never rewrite the catalog — that is the point of the two-form split");
    }

    [Fact]
    public void Decode_TruncatedRecord_Throws()
    {
        var act = () => NodeRegistration.Decode(new byte[] { 1, 2, 3 }, out _, out _);

        act.Should().Throw<InvalidDataException>();
    }

    [Theory]
    [InlineData(0, false)]      // just written
    [InlineData(29, false)]     // inside the window
    [InlineData(30, false)]     // exactly at the boundary — not yet stale
    [InlineData(31, true)]      // past it
    public void IsStale_BoundaryIsExclusive(int ageSeconds, bool expected)
    {
        var expiry = TimeSpan.FromSeconds(30);
        var seen = 0L;
        var now = TimeSpan.FromSeconds(ageSeconds).Ticks;

        NodeRegistration.IsStale(seen, now, expiry).Should().Be(expected);
    }

    [Fact]
    public void IsStale_ZeroExpiry_MeansNeverStale()
        => NodeRegistration.IsStale(0L, TimeSpan.FromDays(365).Ticks, TimeSpan.Zero)
            .Should().BeFalse("a zero expiry disables staleness entirely");

    [Fact]
    public void Age_ClockRunningBackwards_FloorsAtZero()
        => NodeRegistration.Age(seenTicks: 1000L, nowTicks: 500L)
            .Should().Be(TimeSpan.Zero, "clock skew must not produce a negative age");

    [Fact]
    public void Age_ReportsElapsed()
        => NodeRegistration.Age(0L, TimeSpan.FromSeconds(42).Ticks)
            .Should().Be(TimeSpan.FromSeconds(42));
}
