using System.Net;
using FluentAssertions;
using Highway.Server;
using Highway.Server.Resp;
using Highway.Server.Storage;
using StackExchange.Redis;
using Xunit;

namespace Highway.Server.Tests.Resp;

/// <summary>
/// 040 T4 — GATE G0: the pinned StackExchange.Redis client connects to our RESP server, completes
/// its handshake, and round-trips a real HW.* command through the ported dispatch — no Garnet
/// anywhere. This is the settle-it-by-a-passing-test gate (037 D3/OD3). What the client sends on
/// connect (PING, ECHO, CONFIG GET, CLIENT, the tiebreaker GET) is answered by
/// <see cref="RespSession"/>; if this connects with default <c>ConfigurationOptions</c>, OD3 needs
/// no CommandMap/TieBreaker override — recorded in the feature notes.
/// </summary>
[Collection("resp-server")]
public class RespServerHandshakeTests
{
    private static async Task<RespServer> StartAsync(HighwayServerOptions? options = null, string? password = null)
    {
        var opts = options ?? new HighwayServerOptions();
        var auth = new PasswordAuthenticator(password, authDisabled: password is null);
        return await RespServer.StartAsync(new InMemoryStore(), opts, auth, IPAddress.Loopback, 0);
    }

    private static ConfigurationOptions ClientConfig(int port, string? password = null)
    {
        // Exactly what the Highway client builds: parse "localhost:port", AbortOnConnectFail=true,
        // nothing else. No CommandMap, no TieBreaker override — the defaults must work (OD3).
        var config = ConfigurationOptions.Parse($"localhost:{port}");
        config.AbortOnConnectFail = true;
        config.ConnectTimeout = 5000;
        if (password is not null) config.Password = password;
        return config;
    }

    [Fact]
    public async Task GateG0_SeRedis_Connects_And_RoundTripsHwCommand()
    {
        await using var server = await StartAsync();
        using var mux = await ConnectionMultiplexer.ConnectAsync(ClientConfig(server.Port));

        mux.IsConnected.Should().BeTrue("the pinned SE.Redis client must complete its handshake against our server");

        var db = mux.GetDatabase();
        var reply = await db.ExecuteAsync("HW.QSEND", "invoices", "m1", "the-body");
        reply.ToString().Should().Be("OK");

        // The command really ran against the store.
        using var snap = server.Store.Snapshot();
        server.Store.ListLength(snap, Highway.Server.Storage.Layout.HighwayKeyspace.ListPrefix(
            Highway.Server.Storage.Layout.HighwayNames.Queue("invoices"))).Should().Be(1);
    }

    [Fact]
    public async Task HwClaim_ReturnsTheEnqueuedMessage_OverTheWire()
    {
        await using var server = await StartAsync();
        using var mux = await ConnectionMultiplexer.ConnectAsync(ClientConfig(server.Port));
        var db = mux.GetDatabase();

        await db.ExecuteAsync("HW.QSEND", "orders", "m-7", "payload-7");
        var claim = await db.ExecuteAsync("HW.QCLAIM", "orders", "worker-1");

        var arr = (RedisResult[])claim!;
        arr.Should().HaveCount(2);
        arr[0].ToString().Should().Be("m-7");
        arr[1].ToString().Should().Be("payload-7");
    }

    [Fact]
    public async Task EmptyClaim_ReturnsNilArray()
    {
        await using var server = await StartAsync();
        using var mux = await ConnectionMultiplexer.ConnectAsync(ClientConfig(server.Port));
        var db = mux.GetDatabase();

        var claim = await db.ExecuteAsync("HW.QCLAIM", "empty-queue", "worker-1");
        claim.IsNull.Should().BeTrue("an empty claim is the RESP null array");
    }

    [Fact]
    public async Task UnknownCommand_SurfacesAsError_NamingTheSubset()
    {
        await using var server = await StartAsync();
        using var mux = await ConnectionMultiplexer.ConnectAsync(ClientConfig(server.Port));
        var db = mux.GetDatabase();

        var act = async () => await db.ExecuteAsync("FLUSHALL");
        (await act.Should().ThrowAsync<RedisServerException>()).Which.Message.Should().Contain("unknown command");
    }

    [Fact]
    public async Task Ping_RoundTrips()
    {
        await using var server = await StartAsync();
        using var mux = await ConnectionMultiplexer.ConnectAsync(ClientConfig(server.Port));
        (await mux.GetDatabase().PingAsync()).Should().BeGreaterThan(TimeSpan.Zero);
    }

    [Fact]
    public async Task GracefulShutdown_ClosesConnections_WithinTimeout()
    {
        var server = await StartAsync();
        using var mux = await ConnectionMultiplexer.ConnectAsync(ClientConfig(server.Port));
        mux.IsConnected.Should().BeTrue();

        // Dispose stops the host, draining connections within the host timeout (R3.4).
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await server.DisposeAsync();
        stopwatch.Stop();
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10), "shutdown must drain within the host timeout");
    }

    [Fact]
    public async Task Pipelined_Commands_AllRoundTrip()
    {
        // Two commands issued back-to-back exercise the handler's multi-frame-per-buffer path.
        await using var server = await StartAsync();
        using var mux = await ConnectionMultiplexer.ConnectAsync(ClientConfig(server.Port));
        var db = mux.GetDatabase();

        var t1 = db.ExecuteAsync("HW.QSEND", "q", "a", "1");
        var t2 = db.ExecuteAsync("HW.QSEND", "q", "b", "2");
        await Task.WhenAll(t1, t2);

        using var snap = server.Store.Snapshot();
        server.Store.ListLength(snap, Highway.Server.Storage.Layout.HighwayKeyspace.ListPrefix(
            Highway.Server.Storage.Layout.HighwayNames.Queue("q"))).Should().Be(2);
    }
}
