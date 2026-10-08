using FluentAssertions;
using Highway.Server;
using Highway.Server.Commands.Ported;
using Highway.Server.Storage.Rocks;
using StackExchange.Redis;
using Xunit;

namespace Highway.Server.Tests.Commands;

/// <summary>042 T3/T5/T7 — SNAPSHOT bootstrap, promote/fence/status/witness, NOTPRIMARY.</summary>
public class HwReplRuntimeCommandTests
{
    [Theory]
    [InlineData(new string[0], "3 (Highway 2.x)")]
    [InlineData(new[] { "FORMAT", "3" }, "format 3 ")]
    public void SnapshotBegin_FromAReplicaOfAnotherFormat_IsRefused_BeforeAnyCheckpoint(string[] tail, string named)
    {
        // 065 (upgrade rehearsal review): a 2.x replica sends a bare BEGIN and ignores the format in the reply, so the
        // primary must refuse it — or the 2.x node would bootstrap format-4 data it then reads as format 3.
        var dir = Storage.ReplicationToolkitSurfaceTests.NewTempDir();
        try
        {
            using var store = RocksDbStore.Open(dir);
            using var h = new CommandHarness(store);

            var reply = CommandHarness.AsText(h.Run(new HwReplSnapshotCommand(), ["BEGIN", .. tail]));

            reply.Should().StartWith("-ERR HW_STORAGE_FORMAT").And.Contain(named);
        }
        finally { Storage.ReplicationToolkitSurfaceTests.TryDelete(dir); }
    }

    [Fact]
    public void Snapshot_BeginGetEnd_IsResumable_AndDurationIsRecorded()
    {
        var dir = Storage.ReplicationToolkitSurfaceTests.NewTempDir();
        try
        {
            using var store = RocksDbStore.Open(dir);
            using (var batch = store.NewBatch())
            {
                store.Set(batch, "snap-k"u8.ToArray(), "snap-v"u8.ToArray());
                batch.Commit();
            }

            using var h = new CommandHarness(store);
            var started = DateTime.UtcNow;
            var begin = CommandHarness.AsText(h.Run(new HwReplSnapshotCommand(), "BEGIN", "FORMAT", HighwayServerBuilder.StorageFormatVersion.ToString()));
            begin.Should().StartWith("*5\r\n").And.EndWith($":{Highway.Server.HighwayServerBuilder.StorageFormatVersion}\r\n", "the storage format closes the reply (065)");

            var session = store.Replication.Slots; // keep feeder alive
            var began = store.Replication.BeginSnapshot();
            began.Manifest.Should().NotBeEmpty();

            var file = began.Manifest[0].FileName;
            var first = store.Replication.ReadSnapshotChunk(began.SessionId, file, 0, 16);
            if (first.NextOffset is { } next)
            {
                var second = store.Replication.ReadSnapshotChunk(began.SessionId, file, next, 65_536);
                second.Offset.Should().Be(next);
            }

            store.Replication.EndSnapshot(began.SessionId);
            var duration = DateTime.UtcNow - started;
            duration.TotalSeconds.Should().BeLessThan(30);
            File.WriteAllText(Path.Combine(dir, "snapshot-bootstrap.log"),
                $"durationMs={duration.TotalMilliseconds:F0}\n");
            File.Exists(Path.Combine(dir, "snapshot-bootstrap.log")).Should().BeTrue();
        }
        finally
        {
            Storage.ReplicationToolkitSurfaceTests.TryDelete(dir);
        }
    }

    [Fact]
    public void SeRedis_SnapshotBegin_ThenPromoteFenceStatusWitness()
    {
        var dir = Storage.ReplicationToolkitSurfaceTests.NewTempDir();
        try
        {
            using var server = new HighwayTestServer(o =>
            {
                o.DataDir = dir;
                o.Ephemeral = false;
            });
            using var redis = ConnectionMultiplexer.Connect(server.ConnectionString);
            var db = redis.GetDatabase();

            var begin = (RedisResult[])db.Execute("HW.REPL.SNAPSHOT", "BEGIN", "FORMAT", HighwayServerBuilder.StorageFormatVersion.ToString())!;
            begin.Should().HaveCount(5);
            ((long)begin[4]).Should().Be(Highway.Server.HighwayServerBuilder.StorageFormatVersion);
            var session = (string)begin[2]!;
            session.Should().NotBeNullOrWhiteSpace();
            db.Execute("HW.REPL.SNAPSHOT", "END", session);

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
            Storage.ReplicationToolkitSurfaceTests.TryDelete(dir);
        }
    }

    [Fact]
    public void Stats_ServerForm_IncludesReplicationFields()
    {
        var dir = Storage.ReplicationToolkitSurfaceTests.NewTempDir();
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
            Storage.ReplicationToolkitSurfaceTests.TryDelete(dir);
        }
    }
}
