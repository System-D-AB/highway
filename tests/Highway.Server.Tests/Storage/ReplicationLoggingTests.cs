using Highway.Server.Storage.Crystal;
using FluentAssertions;
using Highway.Server;
using Highway.Server.Storage.Replication;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Highway.Server.Tests.Storage;

/// <summary>
/// Feature 047 — replication must be visible in the log. The feeder routes every role/topology
/// transition to its <see cref="HighwayReplication.Logger"/>, and logs a replica attach once —
/// without logging on the per-HELLO / per-ACK path that a healthy stream hammers.
/// </summary>
public class ReplicationLoggingTests
{
    private sealed class CapturingLogger : ILogger
    {
        public List<string> Messages { get; } = [];
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> fmt)
            => Messages.Add(fmt(state, ex));
        private sealed class NullScope : IDisposable { public static readonly NullScope Instance = new(); public void Dispose() { } }
    }

    [Fact]
    public void Promote_IsLogged()
    {
        var dir = TempDirs.NewTempDir();
        try
        {
            using var store = CrystalHighwayStore.Open(dir, replication: new HighwayReplicationOptions
            {
                StartAsReplica = true,
                Priority = 100,
            });
            var feeder = store.Replication;
            var log = new CapturingLogger();
            feeder.Logger = log;

            feeder.TryPromote("test", out var error).Should().BeTrue(error);

            log.Messages.Should().Contain(m => m.Contains("promote"),
                "a promotion is a mastership move an operator must see in the log");
        }
        finally { TempDirs.TryDelete(dir); }
    }

    [Fact]
    public void ReplicaAttach_IsLoggedOnce_NotPerHello()
    {
        var dir = TempDirs.NewTempDir();
        try
        {
            using var store = CrystalHighwayStore.Open(dir);   // a primary
            var feeder = store.Replication;
            var log = new CapturingLogger();
            feeder.Logger = log;

            feeder.Hello("r1", lastAppliedSeq: 0, replicaEpoch: feeder.Epoch);
            feeder.Hello("r1", lastAppliedSeq: 1, replicaEpoch: feeder.Epoch);   // same replica again
            feeder.Hello("r1", lastAppliedSeq: 2, replicaEpoch: feeder.Epoch);   // 076: the position rides the hello

            log.Messages.Count(m => m.Contains("attached")).Should().Be(1,
                "a replica attach logs once; subsequent HELLO/ACK on a healthy stream must not log");
        }
        finally { TempDirs.TryDelete(dir); }
    }

    [Fact]
    public void PlainHellos_LogNothing()
    {
        var dir = TempDirs.NewTempDir();
        try
        {
            using var store = CrystalHighwayStore.Open(dir);
            var feeder = store.Replication;
            feeder.Hello("r1", 0, feeder.Epoch);   // first attach, before the logger is watching

            var log = new CapturingLogger();
            feeder.Logger = log;

            feeder.Hello("r1", 1, feeder.Epoch);
            feeder.Hello("r1", lastAppliedSeq: 2, replicaEpoch: feeder.Epoch);   // 076: the position rides the hello
            feeder.Hello("r1", 3, feeder.Epoch);

            log.Messages.Should().BeEmpty("streaming traffic must not spam the log");
        }
        finally { TempDirs.TryDelete(dir); }
    }
}
