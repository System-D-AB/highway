using FluentAssertions;
using StackExchange.Redis;
using Xunit;

namespace Highway.Server.Tests;

/// <summary>
/// Feature 044 T3 — the <c>hw:cache:*</c> raw-key family on the stock RESP surface. When the
/// cache is enabled the routed commands (GET/SET/DEL/PTTL) hit the broker-local cache store and
/// honour the TTL policy (default when none, clamped to max). When it is disabled the family is
/// invisible — a cache GET misses and a cache SET is refused, exactly as any non-served key —
/// so an enabled-off broker is byte-identical to a pre-044 one. No new <c>HW.*</c> command.
/// </summary>
public class CacheWireSurfaceTests
{
    private static async Task<IDatabase> ConnectAsync(HighwayTestServer server)
    {
        var mux = await ConnectionMultiplexer.ConnectAsync(server.ConnectionString);
        return mux.GetDatabase();
    }

    [Fact]
    public async Task Enabled_SetGetDel_RoundTrips()
    {
        using var server = new HighwayTestServer(o => o.Cache.Enabled = true);
        var db = await ConnectAsync(server);

        (await db.StringGetAsync("hw:cache:user:1")).IsNull.Should().BeTrue("a cold cache misses");

        (await db.StringSetAsync("hw:cache:user:1", "payload")).Should().BeTrue();
        ((string?)await db.StringGetAsync("hw:cache:user:1")).Should().Be("payload");

        (await db.KeyDeleteAsync("hw:cache:user:1")).Should().BeTrue();
        (await db.StringGetAsync("hw:cache:user:1")).IsNull.Should().BeTrue("removed → miss");
    }

    [Fact]
    public async Task Enabled_NoCallerTtl_AppliesTheDefault()
    {
        using var server = new HighwayTestServer(o =>
        {
            o.Cache.Enabled = true;
            o.Cache.DefaultTtl = TimeSpan.FromHours(6);
            o.Cache.MaxTtl = TimeSpan.FromDays(7);
        });
        var db = await ConnectAsync(server);

        await db.StringSetAsync("hw:cache:k", "v"); // no expiry → broker default
        var ttl = await db.KeyTimeToLiveAsync("hw:cache:k");

        ttl.Should().NotBeNull();
        ttl!.Value.Should().BeGreaterThan(TimeSpan.FromHours(5))
            .And.BeLessThanOrEqualTo(TimeSpan.FromHours(6), "the default TTL is applied when the caller sets none");
    }

    [Fact]
    public async Task Enabled_CallerTtl_IsClampedToMax()
    {
        using var server = new HighwayTestServer(o =>
        {
            o.Cache.Enabled = true;
            o.Cache.DefaultTtl = TimeSpan.FromHours(1);
            o.Cache.MaxTtl = TimeSpan.FromHours(2);
        });
        var db = await ConnectAsync(server);

        // Ask for 10 days; the broker clamps to the 2h max.
        await db.StringSetAsync("hw:cache:k", "v", TimeSpan.FromDays(10));
        var ttl = await db.KeyTimeToLiveAsync("hw:cache:k");

        ttl.Should().NotBeNull();
        ttl!.Value.Should().BeLessThanOrEqualTo(TimeSpan.FromHours(2), "a caller TTL is clamped to maxTtl")
            .And.BeGreaterThan(TimeSpan.FromHours(1));
    }

    [Fact]
    public async Task Enabled_HonoursAShortCallerTtl()
    {
        using var server = new HighwayTestServer(o => o.Cache.Enabled = true);
        var db = await ConnectAsync(server);

        await db.StringSetAsync("hw:cache:k", "v", TimeSpan.FromMinutes(5));
        var ttl = await db.KeyTimeToLiveAsync("hw:cache:k");

        ttl.Should().NotBeNull();
        ttl!.Value.Should().BeGreaterThan(TimeSpan.FromMinutes(4))
            .And.BeLessThanOrEqualTo(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public async Task Disabled_CacheGet_Misses_AndCacheSet_IsRefused()
    {
        using var server = new HighwayTestServer(); // cache off by default
        var db = await ConnectAsync(server);

        (await db.StringGetAsync("hw:cache:k")).IsNull.Should().BeTrue(
            "with the cache off, a hw:cache key is just an unserved key → null");

        var set = async () => await db.StringSetAsync("hw:cache:k", "v");
        (await set.Should().ThrowAsync<RedisServerException>())
            .WithMessage("*not writable over the wire*", "a cache SET is refused when the cache is off");
    }

    [Fact]
    public async Task Enabled_IdempotencyAndReplyKeys_AreUnchanged()
    {
        using var server = new HighwayTestServer(o => o.Cache.Enabled = true);
        var db = await ConnectAsync(server);

        // Idempotency claim/record path still works alongside the cache family.
        (await db.StringSetAsync("hw:idem:svc:req", "marker", TimeSpan.FromSeconds(30), When.NotExists))
            .Should().BeTrue();
        (await db.StringSetAsync("hw:idem:svc:req", "marker", TimeSpan.FromSeconds(30), When.NotExists))
            .Should().BeFalse("the live idempotency marker blocks a second NX claim");
    }

    [Fact]
    public async Task Enabled_AnUnservedKey_IsStillRefused()
    {
        using var server = new HighwayTestServer(o => o.Cache.Enabled = true);
        var db = await ConnectAsync(server);

        var set = async () => await db.StringSetAsync("some:random:key", "v");
        (await set.Should().ThrowAsync<RedisServerException>())
            .WithMessage("*not writable over the wire*");
    }
}
