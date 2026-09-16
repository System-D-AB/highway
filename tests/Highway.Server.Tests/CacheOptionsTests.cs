using FluentAssertions;
using Highway.Server;
using Xunit;

namespace Highway.Server.Tests;

/// <summary>
/// Feature 044 T2 — the cache option surface. Off by default (a broker gains no cache unless
/// asked), with the owner's TTL policy (24h default / 7d max) and a validator that refuses a
/// configuration a cache cannot honour.
/// </summary>
public class CacheOptionsTests
{
    [Fact]
    public void Cache_IsDisabledByDefault()
    {
        var opts = new HighwayServerOptions();
        opts.Cache.Enabled.Should().BeFalse("the cache is opt-in — a default broker carries no cache");
    }

    [Fact]
    public void Defaults_AreTheOwnersTtlPolicy()
    {
        var cache = new CacheOptions();
        cache.DefaultTtl.Should().Be(TimeSpan.FromHours(24));
        cache.MaxTtl.Should().Be(TimeSpan.FromDays(7));
        cache.MaxSizeBytes.Should().Be(256L * 1024 * 1024);
        cache.SweepInterval.Should().Be(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void Validate_IsANoOp_WhenDisabled()
    {
        // A nonsense config is tolerated as long as the cache is off — nothing is constructed.
        var cache = new CacheOptions { Enabled = false, DefaultTtl = TimeSpan.Zero, MaxTtl = TimeSpan.Zero };
        cache.Invoking(c => c.Validate()).Should().NotThrow();
    }

    [Fact]
    public void Validate_Rejects_NonPositiveDefaultTtl()
    {
        var cache = new CacheOptions { Enabled = true, DefaultTtl = TimeSpan.Zero };
        cache.Invoking(c => c.Validate()).Should().Throw<InvalidOperationException>()
            .WithMessage("*DefaultTtl*");
    }

    [Fact]
    public void Validate_Rejects_MaxTtlBelowDefault()
    {
        var cache = new CacheOptions
        {
            Enabled = true,
            DefaultTtl = TimeSpan.FromHours(24),
            MaxTtl = TimeSpan.FromHours(1),
        };
        cache.Invoking(c => c.Validate()).Should().Throw<InvalidOperationException>()
            .WithMessage("*MaxTtl*");
    }

    [Fact]
    public void Validate_Rejects_NonPositiveSizeCap()
    {
        var cache = new CacheOptions { Enabled = true, MaxSizeBytes = 0 };
        cache.Invoking(c => c.Validate()).Should().Throw<InvalidOperationException>()
            .WithMessage("*MaxSizeBytes*");
    }

    [Fact]
    public void Validate_Accepts_TheDefaults()
    {
        var cache = new CacheOptions { Enabled = true };
        cache.Invoking(c => c.Validate()).Should().NotThrow();
    }
}
