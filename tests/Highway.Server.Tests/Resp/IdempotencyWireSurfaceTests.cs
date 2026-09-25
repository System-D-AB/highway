using FluentAssertions;
using StackExchange.Redis;
using Xunit;

namespace Highway.Server.Tests;

/// <summary>
/// The <c>[Idempotent]</c> gate over the wire (5.0: <c>HW.IDEM</c>, replacing stock SET NX / SETEX / GET /
/// DEL on <c>hw:idem:*</c>). Pins the exact replies StackExchange.Redis surfaces to the client:
/// <c>CLAIMED</c> / <c>BUSY</c> as simple strings, a completed run as a two-element array.
/// </summary>
public class IdempotencyWireSurfaceTests
{
    [Fact]
    public async Task Begin_End_Abort_RoundTrip()
    {
        using var server = new HighwayTestServer();
        var mux = await ConnectionMultiplexer.ConnectAsync(server.ConnectionString);
        var db = mux.GetDatabase();

        (await db.ExecuteAsync("HW.IDEM", "BEGIN", "svc", "req1", "30")).ToString().Should().Be("CLAIMED");
        (await db.ExecuteAsync("HW.IDEM", "BEGIN", "svc", "req1", "30")).ToString().Should().Be("BUSY",
            "a second claim finds the live in-progress marker");

        (await db.ExecuteAsync("HW.IDEM", "END", "svc", "req1", "30", "final-response")).ToString().Should().Be("OK");
        var done = (RedisResult[])(await db.ExecuteAsync("HW.IDEM", "BEGIN", "svc", "req1", "30"))!;
        done.Select(r => r.ToString()).Should().Equal("DONE", "final-response");

        (await db.ExecuteAsync("HW.IDEM", "ABORT", "svc", "req1")).ToString().Should().Be("OK");
        (await db.ExecuteAsync("HW.IDEM", "BEGIN", "svc", "req1", "30")).ToString().Should().Be("CLAIMED", "released");
    }
}
