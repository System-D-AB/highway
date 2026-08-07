using FluentAssertions;
using Highway.Server;
using StackExchange.Redis;
using Xunit;
using Xunit.Abstractions;

namespace Highway.Integration.Tests;

/// <summary>
/// Feature 004.1 — regression tests documenting the session behavior around raw
/// newline bytes in command arguments (research.md § "Finding 6").
///
/// Known upstream quirk: a REJECTED custom HW.* command whose argument contains
/// a literal newline can desync subsequent custom-command parsing on the SAME
/// session (the follow-up command reads a shifted/blank argument), while PING
/// and stock commands keep working and a fresh connection is unaffected.
/// Accepted commands with newlines (e.g. payloads) never desync. Highway 005
/// clients validate identifiers client-side and never send control characters,
/// so this is unreachable in normal operation.
/// </summary>
public class NewlineDesyncProbe : IDisposable
{
    private readonly HighwayTestServer _server = new();
    private readonly ConnectionMultiplexer _redis;
    private readonly IDatabase _db;
    private readonly ITestOutputHelper _output;

    public NewlineDesyncProbe(ITestOutputHelper output)
    {
        _output = output;
        _redis = ConnectionMultiplexer.Connect(_server.ConnectionString);
        _db = _redis.GetDatabase();
    }

    public void Dispose()
    {
        _redis.Dispose();
        _server.Dispose();
    }

    private string Probe(Func<string> step)
    {
        try
        {
            return "OK: " + step();
        }
        catch (Exception ex)
        {
            return "ERR: " + ex.Message.Split('\n')[0];
        }
    }

    [Fact]
    public void PlainSetWithNewline_DoesNotDesync()
    {
        var set = Probe(() => _db.StringSet("k", "a\nb") ? "set-ok" : "set-false");
        var get = Probe(() => ((string?)_db.StringGet("probe:key2")) ?? "(nil)");
        var ping = Probe(() => { _db.Ping(); return "ping-ok"; });

        _output.WriteLine($"A1 plain SET with \\n value : {set}");
        _output.WriteLine($"A2 GET after               : {get}");
        _output.WriteLine($"A3 PING after              : {ping}");

        set.Should().StartWith("OK:");
        get.Should().StartWith("OK:", "plain Garnet commands never desync the session");
    }

    [Fact]
    public void AcceptedHwCall_NewlineInPayload_DoesNotDesync()
    {
        var call = Probe(() => _db.Execute("HW.CALL", "probe.svc", "req-1", "pay\nload")!.ToString());
        var deq = Probe(() =>
        {
            var r = _db.Execute("HW.DEQUEUE", "probe.svc", "node-1");
            if (r is null) return "nil";
            var arr = (RedisResult[])r!;
            return arr.Length + " elements";
        });
        var ping = Probe(() => { _db.Ping(); return "ping-ok"; });

        _output.WriteLine($"B1 HW.CALL \\n in payload  : {call}");
        _output.WriteLine($"B2 HW.DEQUEUE after       : {deq}");
        _output.WriteLine($"B3 PING after             : {ping}");

        call.Should().StartWith("OK:");
        deq.Should().Be("OK: 2 elements",
            "payloads are byte-opaque; an accepted command with a newline must not desync");
    }

    [Fact]
    public void RejectedCommand_WithNewlineArg_DocumentsKnownSessionDesync()
    {
        // C1: rejected — group contains a raw newline
        var bad = Probe(() => _db.Execute("HW.SUBSCRIBE", "probe.ch", "a\nb").ToString());
        bad.Should().StartWith("ERR: ERR HW_INVALID_ARG");

        // C2: KNOWN QUIRK — the next custom command on the same session is
        // mis-parsed. Asserted (not "hoped") so any future Garnet bump that
        // fixes or changes this behavior is surfaced loudly.
        var clean = Probe(() => _db.Execute("HW.SUBSCRIBE", "probe.ch", "clean-group").ToString());
        clean.Should().StartWith("ERR:",
            "documents the upstream desync; flip this assertion when Garnet fixes it");

        // C3: PING still works — framing is intact, only custom-command arg
        // parsing is affected.
        var ping = Probe(() => { _db.Ping(); return "ping-ok"; });
        ping.Should().StartWith("OK:");

        // C4: a fresh connection is completely unaffected.
        using var fresh = ConnectionMultiplexer.Connect(_server.ConnectionString);
        fresh.GetDatabase().Execute("HW.SUBSCRIBE", "probe.ch", "clean-group").ToString()
            .Should().Be("OK");

        _output.WriteLine($"C1 rejected \\n subscribe  : {bad}");
        _output.WriteLine($"C2 same-conn follow-up    : {clean} (known desync)");
        _output.WriteLine($"C3 PING after             : {ping}");
        _output.WriteLine("C4 fresh connection       : OK");
    }

    [Fact]
    public void Publish_NewlineInChannel_DoesNotDesync()
    {
        var pub = Probe(() => _db.Publish(RedisChannel.Literal("a\nb"), "msg").ToString());
        var ping = Probe(() => { _db.Ping(); return "ping-ok"; });

        _output.WriteLine($"D1 PUBLISH \\n channel     : {pub}");
        _output.WriteLine($"D2 PING after             : {ping}");

        ping.Should().StartWith("OK:", "stock PUBLISH with a newline never desyncs");
    }
}
