using FluentAssertions;
using StackExchange.Redis;
using Xunit;

namespace Highway.Server.Tests;

/// <summary>
/// The client''s idempotency dedup path (HighwayConnection: SET…PX…NX claim, SETEX record,
/// GET prior, UNLINK release) rides bare keys the 040 server must keep serving with the
/// client unmodified (037 R1/D2). This pins the exact wire surface SE.Redis emits for it —
/// found operation-by-operation during the 040 fixture swap (SETEX and UNLINK were the
/// surprises). If SE.Redis changes what it emits, this fails before the client does.
/// </summary>
public class IdempotencyWireSurfaceTests
{
    [Fact]
    public async Task SetNxPx_GetDel_RoundTrip()
    {
        using var server = new HighwayTestServer();
        var mux = await ConnectionMultiplexer.ConnectAsync(server.ConnectionString);
        var db = mux.GetDatabase();

        var claimed = await db.StringSetAsync(
            "hw:idem:svc:req1", " hw:in-progress", TimeSpan.FromSeconds(30), When.NotExists);
        claimed.Should().BeTrue("first NX claim must succeed");

        var again = await db.StringSetAsync(
            "hw:idem:svc:req1", " hw:in-progress", TimeSpan.FromSeconds(30), When.NotExists);
        again.Should().BeFalse("second NX claim must find the live value");

        var record = await db.StringSetAsync(
            "hw:idem:svc:req1", "final-response", TimeSpan.FromSeconds(30));
        record.Should().BeTrue("recording overwrites the marker");

        ((string?)await db.StringGetAsync("hw:idem:svc:req1")).Should().Be("final-response");

        (await db.KeyDeleteAsync("hw:idem:svc:req1")).Should().BeTrue();
        ((string?)await db.StringGetAsync("hw:idem:svc:req1")).Should().BeNull();
    }
}
