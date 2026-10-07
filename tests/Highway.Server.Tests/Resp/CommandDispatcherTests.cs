using System.Diagnostics.Metrics;
using System.Text;
using FluentAssertions;
using Highway.Server;
using Highway.Server.Commands.Runtime;
using Highway.Server.Observability;
using Highway.Server.Resp;
using Highway.Server.Storage;
using Highway.Server.Storage.Rocks;
using Highway.Server.Tests.Commands;
using Highway.Server.Tests.Storage;
using Xunit;

namespace Highway.Server.Tests.Resp;

/// <summary>
/// 040 T4 — the dispatcher: name→ported command, arity contract, served-subset errors. Dispatch
/// runs the real ported commands against an <see cref="InMemoryStore"/>, no socket (037 R10).
/// </summary>
public class CommandDispatcherTests
{
    private static CommandDispatcher NewDispatcher(out IHighwayStore store)
    {
        store = new InMemoryStore();
        var options = new HighwayServerOptions { RpcFailFast = false };   // 074: these call services no session declared
        var recorder = new FlightRecorder(options.Observability);
        return new CommandDispatcher(store, new StripedLock(), new NullDoorbell(), recorder, options);
    }

    private static byte[][] Frame(params string[] tokens) => [.. tokens.Select(Encoding.UTF8.GetBytes)];

    [Fact]
    public void Dispatch_RunsAPortedCommand_AgainstTheStore()
    {
        var dispatcher = NewDispatcher(out var store);
        var writer = new RespWriter();

        dispatcher.Dispatch(Frame("HW.SEND", "invoices", "m1", "body"), writer);

        Encoding.UTF8.GetString(writer.ToArray()).Should().Be("+OK\r\n");
        new StoreInspector(store).ListLength("hw:q:invoices:q").Should().Be(1);
    }

    [Fact]
    public void Dispatch_LowercaseCommandName_IsAccepted()
    {
        var dispatcher = NewDispatcher(out _);
        var writer = new RespWriter();
        dispatcher.Dispatch(Frame("hw.stats", "server"), writer);
        Encoding.UTF8.GetString(writer.ToArray()).Should().NotStartWith("-ERR HW_INVALID_ARG unknown command");
    }

    [Fact]
    public void Dispatch_UnknownCommand_ErrorsNamingTheSubset()
    {
        var dispatcher = NewDispatcher(out _);
        var writer = new RespWriter();
        dispatcher.Dispatch(Frame("HW.FLY", "x"), writer);

        var reply = Encoding.UTF8.GetString(writer.ToArray());
        reply.Should().StartWith("-ERR HW_INVALID_ARG unknown command 'HW.FLY'");
        reply.Should().Contain("HW.SEND", "the error names the served subset");
    }

    [Theory]
    [InlineData("HW.DISCOVER")]                          // arity 2: too few (only name)
    [InlineData("HW.DISCOVER", "a", "b")]                // arity 2: too many
    [InlineData("HW.SEND", "q")]                        // arity -4: too few
    public void Dispatch_WrongArity_Errors(params string[] tokens)
    {
        var dispatcher = NewDispatcher(out _);
        var writer = new RespWriter();
        dispatcher.Dispatch(Frame(tokens), writer);
        Encoding.UTF8.GetString(writer.ToArray()).Should().Contain("wrong number of arguments");
    }

    [Fact]
    public void ServedCommands_IncludeTheReplicationFamily()
    {
        var dispatcher = NewDispatcher(out _);
        dispatcher.ServedCommands.Should().BeEquivalentTo(new[]
        {
            "HW.CALL", "HW.CANCEL", "HW.REPLY", "HW.RESULT", "HW.SEND", "HW.PUBLISH", "HW.CLAIM", "HW.ACK",
            "HW.TOUCH", "HW.FAIL", "HW.DLQ", "HW.IDEM", "HW.SESSION", "HW.HEARTBEAT", "HW.UNSUBSCRIBE",
            "HW.JOB", "HW.DISCOVER", "HW.STATS", "HW.REPLAY",
            "HW.REPL.HELLO", "HW.REPL.PULL", "HW.REPL.ACK",
            "HW.REPL.SNAPSHOT", "HW.REPL.PROMOTE", "HW.REPL.FENCE", "HW.REPL.STATUS", "HW.REPL.WITNESS",
            "HW.REPL.JOIN", "HW.REPL.GOODBYE",
        });
    }

    [Fact]
    public void Dispatch_MovesTheMetrics_ForSendClaimAndCall()
    {
        // 051 T2: the delivery counters ride the real command AfterCommit sites, and HW.CALL's
        // request counter rides the dispatcher — proven end to end through Dispatch, isolated on a
        // unique meter name so a parallel embedded server's Highway.Server meter cannot leak in.
        var meterName = "Highway.Server.Test." + Guid.NewGuid().ToString("N");
        var store = new InMemoryStore();
        var options = new HighwayServerOptions { RpcFailFast = false };   // 074: these call services no session declared
        var recorder = new FlightRecorder(options.Observability);
        using var metrics = new HighwayMetrics(replication: null, () => [], recorder, meterName);
        var dispatcher = new CommandDispatcher(
            store, new StripedLock(), new NullDoorbell(), recorder, options, replication: null, cache: null, metrics: metrics);

        var totals = new Dictionary<string, long>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (inst, l) => { if (inst.Meter.Name == meterName) l.EnableMeasurementEvents(inst); },
        };
        listener.SetMeasurementEventCallback<long>((inst, m, _, _) =>
            totals[inst.Name] = totals.GetValueOrDefault(inst.Name) + m);
        listener.Start();

        dispatcher.Dispatch(Frame("HW.SEND", "invoices", "m1", "body"), new RespWriter());
        dispatcher.Dispatch(Frame("HW.CLAIM", "Q", "invoices", "worker1"), new RespWriter());
        dispatcher.Dispatch(Frame("HW.CALL", "orders.get", "r1", "body"), new RespWriter());

        totals.GetValueOrDefault("highway.messages.published").Should().Be(1);
        totals.GetValueOrDefault("highway.messages.delivered").Should().Be(1);
        totals.GetValueOrDefault("highway.rpc.requests").Should().Be(1);
    }

    [Fact]
    public void ASuppressedCallReSend_IsCountedAsADuplicate_NotAsASecondRpcRequest()
    {
        // 074 review: the dispatcher times and counts every HW.CALL; a re-send the broker suppressed is not a request.
        var meterName = "Highway.Server.Test." + Guid.NewGuid().ToString("N");
        var store = new InMemoryStore();
        var options = new HighwayServerOptions { RpcFailFast = false };   // 074: these call services no session declared
        var recorder = new FlightRecorder(options.Observability);
        using var metrics = new HighwayMetrics(replication: null, () => [], recorder, meterName);
        var dispatcher = new CommandDispatcher(
            store, new StripedLock(), new NullDoorbell(), recorder, options, replication: null, cache: null, metrics: metrics);

        var totals = new Dictionary<string, long>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (inst, l) => { if (inst.Meter.Name == meterName) l.EnableMeasurementEvents(inst); },
        };
        listener.SetMeasurementEventCallback<long>((inst, m, _, _) =>
            totals[inst.Name] = totals.GetValueOrDefault(inst.Name) + m);
        listener.Start();

        for (var i = 0; i < 3; i++)
            dispatcher.Dispatch(Frame("HW.CALL", "orders.get", "r1", "body"), new RespWriter());

        totals.GetValueOrDefault("highway.rpc.requests").Should().Be(1, "one call, re-sent twice, is one request");
        totals.GetValueOrDefault("highway.messages.duplicates").Should().Be(2);
    }

    [Fact]
    public void AnEpochChange_ForgetsRememberedIds_SoAReSendAfterReturningToPrimaryIsStored()
    {
        // 074 review: a broker demoted and promoted again without restarting must not suppress a re-send on the
        // strength of an id remembered under the old epoch — that write may not have survived the failover.
        var dir = ReplicationToolkitSurfaceTests.NewTempDir();
        try
        {
            using var store = RocksDbStore.Open(dir, replication: new HighwayReplicationOptions
            {
                StartAsReplica = true,
                Priority = 100,
            });
            var feeder = store.Replication;
            var options = new HighwayServerOptions { RpcFailFast = false };   // 074: these call services no session declared
            var dispatcher = new CommandDispatcher(
                store, new StripedLock(), new NullDoorbell(), new FlightRecorder(options.Observability), options, replication: feeder);

            feeder.TryPromote("test", out var error).Should().BeTrue(error);
            Reply(dispatcher, "HW.CALL", "orders.get", "r1", "body").Should().Be("+OK\r\n");
            Reply(dispatcher, "HW.CALL", "orders.get", "r1", "body").Should().Be("+OK\r\n");
            CallDuplicates(dispatcher).Should().Be("1", "within one epoch the re-send is suppressed");

            feeder.ObserveHigherEpoch(feeder.Epoch + 1, "primary promoted elsewhere");
            feeder.TryPromote("test", out error).Should().BeTrue(error);

            Reply(dispatcher, "HW.CALL", "orders.get", "r1", "body").Should().Be("+OK\r\n");
            CallDuplicates(dispatcher).Should().Be("1", "the new epoch forgot r1, so its re-send was stored, not suppressed as a second duplicate");
        }
        finally { ReplicationToolkitSurfaceTests.TryDelete(dir); }
    }

    private static string Reply(CommandDispatcher dispatcher, params string[] tokens)
    {
        var writer = new RespWriter();
        dispatcher.Dispatch(Frame(tokens), writer);
        return Encoding.UTF8.GetString(writer.ToArray());
    }

    private static string CallDuplicates(CommandDispatcher dispatcher)
    {
        var writer = new RespWriter();
        dispatcher.Dispatch(Frame("HW.STATS"), writer);
        return DuplicateSafetyTests.StatField(writer.ToArray(), "callDuplicates");
    }

    private sealed class NullDoorbell : IDoorbell
    {
        public int Ring(string channel, ReadOnlySpan<byte> payload) => 0;
    }
}
