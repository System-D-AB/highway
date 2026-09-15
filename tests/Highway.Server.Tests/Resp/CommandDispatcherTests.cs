using System.Text;
using FluentAssertions;
using Highway.Server;
using Highway.Server.Commands.Runtime;
using Highway.Server.Observability;
using Highway.Server.Resp;
using Highway.Server.Storage;
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
        var options = new HighwayServerOptions();
        var recorder = new FlightRecorder(options.Observability);
        return new CommandDispatcher(store, new StripedLock(), new NullDoorbell(), recorder, options);
    }

    private static byte[][] Frame(params string[] tokens) => [.. tokens.Select(Encoding.UTF8.GetBytes)];

    [Fact]
    public void Dispatch_RunsAPortedCommand_AgainstTheStore()
    {
        var dispatcher = NewDispatcher(out var store);
        var writer = new RespWriter();

        dispatcher.Dispatch(Frame("HW.QSEND", "invoices", "m1", "body"), writer);

        Encoding.UTF8.GetString(writer.ToArray()).Should().Be("+OK\r\n");
        using var snap = store.Snapshot();
        store.ListLength(snap, Highway.Server.Storage.Layout.HighwayKeyspace.ListPrefix(
            Highway.Server.Storage.Layout.HighwayNames.Queue("invoices"))).Should().Be(1);
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
        reply.Should().Contain("HW.QSEND", "the error names the served subset");
    }

    [Theory]
    [InlineData("HW.DISCOVER")]                          // arity 2: too few (only name)
    [InlineData("HW.DISCOVER", "a", "b")]                // arity 2: too many
    [InlineData("HW.QSEND", "q")]                        // arity -4: too few
    public void Dispatch_WrongArity_Errors(params string[] tokens)
    {
        var dispatcher = NewDispatcher(out _);
        var writer = new RespWriter();
        dispatcher.Dispatch(Frame(tokens), writer);
        Encoding.UTF8.GetString(writer.ToArray()).Should().Contain("wrong number of arguments");
    }

    [Fact]
    public void ServedCommands_AreExactlyTheEighteen()
    {
        var dispatcher = NewDispatcher(out _);
        dispatcher.ServedCommands.Should().BeEquivalentTo(new[]
        {
            "HW.CALL", "HW.REPLY", "HW.DEQUEUE", "HW.ACK", "HW.SUBSCRIBE", "HW.UNSUBSCRIBE",
            "HW.PUBLISH", "HW.HEARTBEAT", "HW.DISCOVER", "HW.STATS", "HW.REPLAY", "HW.DLQ",
            "HW.QSEND", "HW.QCLAIM", "HW.QACK", "HW.FAIL", "HW.TOUCH", "HW.JOB",
        });
    }

    private sealed class NullDoorbell : IDoorbell
    {
        public int Ring(string channel, ReadOnlySpan<byte> payload) => 0;
    }
}
