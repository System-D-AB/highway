// 074: these tests make raw HW.CALLs to services no session declared, so fail-fast is off for their servers.
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
        using var server1 = new HighwayTestServer(o => o.RpcFailFast = false);
        using var server2 = new HighwayTestServer(o => o.RpcFailFast = false);

        using var redis1 = ConnectionMultiplexer.Connect(server1.ConnectionString);
        using var redis2 = ConnectionMultiplexer.Connect(server2.ConnectionString);

        var db1 = redis1.GetDatabase();
        var db2 = redis2.GetDatabase();

        // Write to server 1
        db1.Execute("HW.CALL", "isolation.test", "req-iso-1", "payload-from-server1");

        // Server 2 should not see it
        var result = db2.ClaimSvc("isolation.test", "node-1");
        result.Should().BeNull("server2 should not see data written to server1");

        // Server 1 should see it
        var result1 = db1.ClaimSvc("isolation.test", "node-1");
        result1.Should().NotBeNull("server1 should see its own data");

        result1!.Value.Id.Should().Be("req-iso-1");
        result1.Value.PayloadText.Should().Be("payload-from-server1");
    }
}
