using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using Highway.Server;
using Highway.Server.Internal;
using StackExchange.Redis;
using Xunit;
using Xunit.Abstractions;

namespace Highway.Integration.Tests;

/// <summary>
/// Feature 004.1 Task 7 — Requirement 4: configurable embedded test server.
/// Also carries the Task 6 bind-to-any-interface test (Requirement 8 AC3).
/// </summary>
public class TestServerTests
{
    private readonly ITestOutputHelper _output;

    public TestServerTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void ConfigureDelegate_ReachesOptions()
    {
        // Small payload cap set through the delegate → enforced by HW.CALL
        using var server = new HighwayTestServer(o => o.MaxPayloadBytes = 16);
        using var redis = ConnectionMultiplexer.Connect(server.ConnectionString);
        var db = redis.GetDatabase();

        var act = () => db.Execute("HW.CALL", "svc", "req-1", new string('x', 64));

        act.Should().Throw<RedisServerException>()
            .WithMessage("ERR HW_PAYLOAD_TOO_LARGE*");
    }

    [Fact]
    public void ConfigureDelegate_ReachesReceiveDefaults()
    {
        using var server = new HighwayTestServer(o => o.ReceiveDefaultCount = 2);
        using var redis = ConnectionMultiplexer.Connect(server.ConnectionString);
        var db = redis.GetDatabase();

        db.Execute("HW.SUBSCRIBE", "ch", "grp");
        db.Execute("HW.PUBLISH", "ch", "m1");
        db.Execute("HW.PUBLISH", "ch", "m2");
        db.Execute("HW.PUBLISH", "ch", "m3");

        // No COUNT arg → the configured default (2) applies
        var result = (RedisResult[])db.Execute("HW.RECEIVE", "ch", "grp")!;
        result.Should().HaveCount(2);
    }

    [Fact]
    public void ConfigureDelegate_CannotChangePort()
    {
        using var server = new HighwayTestServer(o => o.Port = 12345);

        server.Port.Should().NotBe(12345);
        server.ConnectionString.Should().Be($"localhost:{server.Port}");

        // The connection string still works — the probed port is the live one
        using var redis = ConnectionMultiplexer.Connect(server.ConnectionString);
        redis.GetDatabase().Ping();
    }

    [Fact]
    public void Restart_MemoryOnly_KeepsConnectionString_StateIsGone()
    {
        using var server = new HighwayTestServer();
        var connectionString = server.ConnectionString;

        using (var redis = ConnectionMultiplexer.Connect(connectionString))
        {
            var db = redis.GetDatabase();
            db.Execute("HW.CALL", "restart.svc", "req-1", "payload");
        }

        server.Restart();

        server.ConnectionString.Should().Be(connectionString);

        using (var redis = ConnectionMultiplexer.Connect(server.ConnectionString))
        {
            var db = redis.GetDatabase();
            var result = db.Execute("HW.DEQUEUE", "restart.svc", "node-1");
            result.IsNull.Should().BeTrue("memory-only state is lost across restart");
        }
    }

    [Fact]
    public void Restart_DurableMode_RecoversState()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), $"highway-0041-{Guid.NewGuid():N}");
        try
        {
            using var server = new HighwayTestServer(o => o.DataDir = dataDir);
            var connectionString = server.ConnectionString;

            using (var redis = ConnectionMultiplexer.Connect(connectionString))
            {
                redis.GetDatabase().Execute("HW.CALL", "durable.svc", "req-1", "survivor");
            }

            server.Restart();

            using (var redis = ConnectionMultiplexer.Connect(connectionString))
            {
                var result = redis.GetDatabase().Execute("HW.DEQUEUE", "durable.svc", "node-1");
                result.IsNull.Should().BeFalse("AOF state must survive restart with a data directory");
                var arr = (RedisResult[])result!;
                ((string)arr[0]!).Should().Be("req-1");
                ((string)arr[1]!).Should().Be("survivor");
            }
        }
        finally
        {
            TryDeleteDirectory(dataDir);
        }
    }

    [Fact]
    public void TwoConcurrentInstances_Isolated()
    {
        using var serverA = new HighwayTestServer();
        using var serverB = new HighwayTestServer();

        serverA.Port.Should().NotBe(serverB.Port);

        using var redisA = ConnectionMultiplexer.Connect(serverA.ConnectionString);
        using var redisB = ConnectionMultiplexer.Connect(serverB.ConnectionString);

        redisA.GetDatabase().Execute("HW.CALL", "iso.svc", "req-A", "payload-A");

        var fromA = redisA.GetDatabase().Execute("HW.DEQUEUE", "iso.svc", "node-1");
        var fromB = redisB.GetDatabase().Execute("HW.DEQUEUE", "iso.svc", "node-1");

        fromA.IsNull.Should().BeFalse();
        fromB.IsNull.Should().BeTrue("instances must not share state");
    }

    // -------------------------------------------------------------------------
    // Feature 004.1 Task 6 — Requirement 8 AC3: bind to all interfaces
    // -------------------------------------------------------------------------

    [Fact]
    public void BindToAny_ReachableViaNonLoopbackLocalAddress()
    {
        var nonLoopback = Dns.GetHostAddresses(Dns.GetHostName())
            .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork
                                 && !IPAddress.IsLoopback(a));

        if (nonLoopback is null)
        {
            _output.WriteLine("SKIPPED: environment has no non-loopback local IPv4 address to connect through.");
            return;
        }

        var port = EphemeralPort.Probe();
        using var server = new HighwayServerBuilder()
            .WithPort(port)
            .WithBindAddress(IPAddress.Any)
            .Build();
        server.Start();

        using var redis = ConnectionMultiplexer.Connect($"{nonLoopback}:{port}");
        redis.GetDatabase().Ping();

        _output.WriteLine($"Connected through non-loopback address {nonLoopback}:{port}");
    }

    private static void TryDeleteDirectory(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // Best-effort cleanup of temp test data
        }
    }
}
