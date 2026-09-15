using System.Text;
using FluentAssertions;
using Highway.Server.Commands.Runtime;
using Highway.Server.Storage;
using Highway.Server.Storage.Layout;
using Xunit;

namespace Highway.Server.Tests.Commands;

/// <summary>
/// 039 T1: the command runtime dispatches and replies in-process, with no socket
/// (037 R10 transport-seam proof). A toy command exercises the whole shape — validate,
/// lock, snapshot, batch, commit, reply, post-commit doorbell.
/// </summary>
public class CommandRuntimeTests
{
    /// <summary>
    /// A toy command: <c>PUT &lt;name&gt; &lt;value&gt;</c> stores a KV entry, replies +OK,
    /// and rings a doorbell. It uses only the runtime — no Garnet, no transport.
    /// </summary>
    private sealed class ToyPutCommand : HighwayCommand
    {
        private string _name = null!;
        private byte[] _value = [];

        protected override bool Parse(CommandContext ctx, CommandInput input)
        {
            var idx = 0;
            if (!TryReadIdentifier(input, ref idx, "name", ctx.Options.MaxIdentifierBytes, out _name))
                return false;
            if (!TryReadPayload(input, ref idx, ctx.Options.MaxPayloadBytes, out _value))
                return false;
            return true;
        }

        protected override void Run(CommandContext ctx, RespWriter writer)
        {
            using (ctx.Locks.Lock(_name))
            {
                using var batch = ctx.Store.NewBatch();
                ctx.Store.Set(batch, HighwayKeyspace.Kv(_name), _value);
                batch.Commit();
            }
            writer.SimpleString("OK");
        }

        protected override void AfterCommit(CommandContext ctx)
        {
            if (Failed) return;
            ctx.Doorbell.Ring($"toy:{_name}", _value);
        }

        public string StoredName => _name;
    }

    [Fact]
    public void ToyCommand_Dispatches_Replies_AndPersists()
    {
        using var h = new CommandHarness();
        var cmd = new ToyPutCommand();

        var reply = h.Run(cmd, "widgets", "hello");

        // +OK\r\n, byte-identical to what a Garnet command's WriteSimpleString produced.
        CommandHarness.AsText(reply).Should().Be("+OK\r\n");

        // It persisted through the real store.
        using var snap = h.Store.Snapshot();
        var stored = h.Store.Get(snap, HighwayKeyspace.Kv("widgets"));
        stored.Should().NotBeNull();
        Encoding.UTF8.GetString(stored!).Should().Be("hello");
    }

    [Fact]
    public void ToyCommand_RingsDoorbell_PostCommit()
    {
        using var h = new CommandHarness();
        h.Run(new ToyPutCommand(), "widgets", "hello");

        h.Doorbell.Rings.Should().ContainSingle();
        h.Doorbell.Rings[0].Channel.Should().Be("toy:widgets");
        Encoding.UTF8.GetString(h.Doorbell.Rings[0].Payload).Should().Be("hello");
    }

    [Fact]
    public void ToyCommand_InvalidArg_RepliesError_NoStoreWrite_NoDoorbell()
    {
        using var h = new CommandHarness();

        var reply = h.Run(new ToyPutCommand(), "", "hello"); // blank name → invalid

        CommandHarness.AsText(reply).Should().StartWith("-ERR HW_INVALID_ARG");
        h.Doorbell.Rings.Should().BeEmpty("a rejected command must never ring a doorbell");

        using var snap = h.Store.Snapshot();
        h.Store.Get(snap, HighwayKeyspace.Kv("")).Should().BeNull("a rejected command writes nothing");
    }

    [Fact]
    public void RespWriter_ShapesAreByteIdentical()
    {
        // Guard the byte fidelity the whole port depends on.
        var w = new RespWriter();

        w.SimpleString("OK");
        CommandHarness.AsText(w.ToArray()).Should().Be("+OK\r\n");

        w = new RespWriter();
        w.Integer(1);
        CommandHarness.AsText(w.ToArray()).Should().Be(":1\r\n");

        w = new RespWriter();
        w.Integer(0);
        CommandHarness.AsText(w.ToArray()).Should().Be(":0\r\n");

        w = new RespWriter();
        w.NullArray();
        CommandHarness.AsText(w.ToArray()).Should().Be("*-1\r\n");

        w = new RespWriter();
        w.EmptyArray();
        CommandHarness.AsText(w.ToArray()).Should().Be("*0\r\n");

        w = new RespWriter();
        w.Error("ERR HW_INVALID_ARG blank");
        CommandHarness.AsText(w.ToArray()).Should().Be("-ERR HW_INVALID_ARG blank\r\n");

        w = new RespWriter();
        w.ThreeIntegers(1, 2, 3);
        CommandHarness.AsText(w.ToArray()).Should().Be("*3\r\n:1\r\n:2\r\n:3\r\n");

        // [requestId, payload] two-element bulk-string array.
        w = new RespWriter();
        w.BulkStringArray(Encoding.UTF8.GetBytes("req-1"), Encoding.UTF8.GetBytes("body"));
        CommandHarness.AsText(w.ToArray()).Should().Be("*2\r\n$5\r\nreq-1\r\n$4\r\nbody\r\n");

        // Field array (HW.STATS shape): flat *2N of name/value bulk strings.
        w = new RespWriter();
        w.FieldArray([("kind", "queue"), ("depth", "3")]);
        CommandHarness.AsText(w.ToArray()).Should().Be("*4\r\n$4\r\nkind\r\n$5\r\nqueue\r\n$5\r\ndepth\r\n$1\r\n3\r\n");
    }
}
