using Highway.Server.Storage.Crystal;
using FluentAssertions;
using Highway.Server;
using Highway.Server.Commands.Ported;
using Highway.Server.Storage.Replication;
using StackExchange.Redis;
using Xunit;

namespace Highway.Server.Tests.Commands;

/// <summary>042 T5/T7 — promote/fence/status/witness, NOTPRIMARY (076: snapshot bootstrap is Crystal's).</summary>
public class HwReplRuntimeCommandTests
{
    [Fact]
    public void SeRedis_PromoteFenceStatusWitness()
    {
        var dir = Storage.TempDirs.NewTempDir();
        try
        {
            using var server = new HighwayTestServer(o =>
            {
                o.DataDir = dir;
                o.Ephemeral = false;
            });
            using var redis = ConnectionMultiplexer.Connect(server.ConnectionString);
            var db = redis.GetDatabase();

            var status = (RedisResult[])db.Execute("HW.REPL.STATUS")!;
            status.Length.Should().BeGreaterThan(0);

            db.Execute("HW.REPL.WITNESS").ToString().Should().Be("OK");
            db.Execute("HW.REPL.FENCE", "test").ToString().Should().Be("OK");

            var call = () => db.Execute("HW.SEND", "q", "m1", "payload");
            call.Should().Throw<RedisServerException>().Which.Message.Should().StartWith("NOTPRIMARY");

            var epoch = db.Execute("HW.REPL.PROMOTE", "unfence-via-promote");
            ((int)epoch).Should().BeGreaterThan(0);
        }
        finally
        {
            Storage.TempDirs.TryDelete(dir);
        }
    }

    [Fact]
    public void Stats_ServerForm_IncludesReplicationFields()
    {
        var dir = Storage.TempDirs.NewTempDir();
        try
        {
            using var server = new HighwayTestServer(o =>
            {
                o.DataDir = dir;
                o.Ephemeral = false;
            });
            using var redis = ConnectionMultiplexer.Connect(server.ConnectionString);
            var stats = (RedisResult[])redis.GetDatabase().Execute("HW.STATS")!;
            var fields = new Dictionary<string, string>();
            for (var i = 0; i + 1 < stats.Length; i += 2)
                fields[(string)stats[i]!] = (string)stats[i + 1]!;

            fields.Should().ContainKey("repl.role");
            fields["repl.role"].Should().Be("Primary");
            fields.Should().ContainKey("repl.epoch");
        }
        finally
        {
            Storage.TempDirs.TryDelete(dir);
        }
    }
}
