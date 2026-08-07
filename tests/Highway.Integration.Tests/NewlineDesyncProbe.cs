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
/// <b>Correction (feature 002).</b> These tests were written for a supposed
/// upstream Garnet parser quirk: a rejected <c>HW.*</c> command carrying a raw
/// newline appeared to desync subsequent custom-command parsing on the same
/// session. That diagnosis was wrong.
///
/// The real cause was Highway's own: Garnet caches one procedure instance per
/// session, and <c>HighwayCommandBase</c> never cleared its captured validation
/// error, so <em>any</em> rejection — newline or not — was replayed for every
/// later invocation of that command on that connection. Garnet's parser was
/// never involved.
///
/// The tests are kept because newline handling is still worth pinning, and
/// because the file records how a Highway bug spent two features attributed to
/// someone else's code. The session-isolation guarantee itself is covered by
/// <c>SessionStateIsolationTests</c>.
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
    public void RejectedCommand_WithNewlineArg_DoesNotAffectTheNextCommand()
    {
        // C1: rejected — group contains a raw newline
        var bad = Probe(() => _db.Execute("HW.SUBSCRIBE", "probe.ch", "a\nb").ToString());
        bad.Should().StartWith("ERR: ERR HW_INVALID_ARG");

        // C2: the follow-up now SUCCEEDS.
        //
        // This assertion used to expect a failure, attributed to an upstream
        // Garnet parser quirk. That diagnosis was wrong. The cause was Highway's
        // own state leak: Garnet caches one procedure instance per session, and
        // HighwayCommandBase never cleared its captured error, so the rejection
        // above was replayed for every later invocation on this connection. It
        // had nothing to do with newlines — any rejection did it. Feature 002
        // found and fixed it (see SessionStateIsolationTests).
        var clean = Probe(() => _db.Execute("HW.SUBSCRIBE", "probe.ch", "clean-group").ToString());
        clean.Should().StartWith("OK:",
            "a rejected command must not affect the next command on the same connection");

        // C3: PING still works — framing is intact, only custom-command arg
        // parsing is affected.
        var ping = Probe(() => { _db.Ping(); return "ping-ok"; });
        ping.Should().StartWith("OK:");

        // C4: a fresh connection is completely unaffected.
        using var fresh = ConnectionMultiplexer.Connect(_server.ConnectionString);
        fresh.GetDatabase().Execute("HW.SUBSCRIBE", "probe.ch", "clean-group").ToString()
            .Should().Be("OK");

        _output.WriteLine($"C1 rejected \\n subscribe  : {bad}");
        _output.WriteLine($"C2 same-conn follow-up    : {clean} (was a Highway bug, now fixed)");
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
