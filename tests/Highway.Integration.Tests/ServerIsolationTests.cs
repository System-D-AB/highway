using FluentAssertions;
using Highway.Server;
using StackExchange.Redis;
using Xunit;

namespace Highway.Integration.Tests;

/// <summary>
/// Integration tests for concurrency isolation between test server instances
/// (004 Requirement 2 AC5 / 004.1 Requirement 4 AC6).
/// (Moved unchanged from DurabilityTests.cs by feature 004.1 Task 9 — the old
/// file's name did not match this content.)
/// </summary>
public class ServerIsolationTests
{
    [Fact]
    public void TwoTestServers_Isolated()
    {
        using var server1 = new HighwayTestServer();
        using var server2 = new HighwayTestServer();

        using var redis1 = ConnectionMultiplexer.Connect(server1.ConnectionString);
        using var redis2 = ConnectionMultiplexer.Connect(server2.ConnectionString);

        var db1 = redis1.GetDatabase();
        var db2 = redis2.GetDatabase();

        // Write to server 1
        db1.Execute("HW.CALL", "isolation.test", "req-iso-1", "payload-from-server1");

        // Server 2 should not see it
        var result = db2.Execute("HW.DEQUEUE", "isolation.test", "node-1");
        result.IsNull.Should().BeTrue("server2 should not see data written to server1");

        // Server 1 should see it
        var result1 = db1.Execute("HW.DEQUEUE", "isolation.test", "node-1");
        result1.IsNull.Should().BeFalse("server1 should see its own data");

        var arr = (RedisResult[])result1!;
        ((string)arr[0]!).Should().Be("req-iso-1");
        ((string)arr[1]!).Should().Be("payload-from-server1");
    }
}
