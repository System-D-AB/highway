using System.Text.RegularExpressions;
using FluentAssertions;
using Highway.Server;
using Highway.Server.Internal;
using Highway.Server.Observability;
using StackExchange.Redis;
using Xunit;

namespace Highway.Server.Tests;

/// <summary>
/// Feature 007 — keeps <c>docs/HIGHWAY-PROTOCOL.md</c> and the implementation
/// unable to disagree.
///
/// <para>Two command tables in this repository had already drifted from shipped
/// code before anyone noticed, which is why this exists. The protocol file is the
/// definition; these tests read it at run time — not a copy — and check a running
/// server against it in <b>both</b> directions:</para>
///
/// <list type="number">
///   <item>every command the file documents is registered, with the documented arity;</item>
///   <item>every command the server registers appears in the file.</item>
/// </list>
///
/// <para>Reply shapes, error codes, keys and invariants are prose and are not
/// machine-checked. The command surface is, because that is the part a
/// well-meaning change is most likely to alter silently.</para>
/// </summary>
public class ProtocolConformanceTests
{
    private const string ProtocolFileName = "docs/HIGHWAY-PROTOCOL.md";

    // ──────────────────────────────────────────────────────────────────
    // Locating and parsing the protocol file
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Walks up from the test binary to the repository root (the directory
    /// holding Highway.slnx) and reads the protocol file.
    /// </summary>
    private static string ReadProtocolFile()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Highway.slnx")))
            dir = dir.Parent;

        dir.Should().NotBeNull("the repository root (containing Highway.slnx) must be locatable from the test binary");

        var path = Path.Combine(dir!.FullName, ProtocolFileName);

        // Never skip when the file is missing: a conformance test that quietly
        // passes because it could not find its input is worse than no test.
        File.Exists(path).Should().BeTrue($"the protocol file must exist at {ProtocolFileName}");

        return File.ReadAllText(path);
    }

    /// <summary>
    /// Extracts the Command Index. Deliberately lenient: it reads only rows whose
    /// first cell is a backticked HW.* name and takes the name and arity, so
    /// editing prose around the table cannot break the test.
    /// </summary>
    internal static IReadOnlyList<(string Name, int Arity)> ParseCommandIndex(string markdown)
    {
        var start = markdown.IndexOf("## Command Index", StringComparison.Ordinal);
        start.Should().BeGreaterThan(-1, "the protocol file must contain a '## Command Index' section");

        // The section ends at the next top-level heading.
        var end = markdown.IndexOf("\n## ", start + 1, StringComparison.Ordinal);
        var section = end > start ? markdown[start..end] : markdown[start..];

        var rows = new List<(string, int)>();
        foreach (var line in section.Split('\n'))
        {
            var match = Regex.Match(line.Trim(), @"^\|\s*`(HW\.[A-Z]+)`\s*\|\s*(-?\d+)\s*\|");
            if (match.Success)
                rows.Add((match.Groups[1].Value, int.Parse(match.Groups[2].Value)));
        }

        return rows;
    }

    private static IReadOnlyList<(string Name, int Arity)> RegisteredCommands()
    {
        var opts = new HighwayServerOptions();
        var garnetOpts = HighwayServerBuilder.BuildGarnetOptions(opts);
        using var garnet = new HighwayGarnetServer(garnetOpts);
        var doorbell = new DoorbellBridge(garnet);
        using var recorder = new FlightRecorder(opts.Observability);

        return [.. HighwayServer.CommandTable(opts, doorbell, recorder).Select(c => (c.Name, c.Arity))];
    }

    // ──────────────────────────────────────────────────────────────────
    // The index itself
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public void ProtocolFile_Exists_AndContainsACommandIndex()
        => ParseCommandIndex(ReadProtocolFile()).Should().NotBeEmpty(
            "the Command Index is what makes this document enforceable rather than decorative");

    [Fact]
    public void CommandIndex_ListsEachCommandExactlyOnce()
    {
        var names = ParseCommandIndex(ReadProtocolFile()).Select(c => c.Name).ToList();

        names.Should().OnlyHaveUniqueItems("a duplicated row makes the index ambiguous");
    }

    // ──────────────────────────────────────────────────────────────────
    // Direction 1 — everything documented is implemented
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public void EveryDocumentedCommand_IsRegistered_WithTheDocumentedArity()
    {
        var documented = ParseCommandIndex(ReadProtocolFile());
        var registered = RegisteredCommands().ToDictionary(c => c.Name, c => c.Arity, StringComparer.Ordinal);

        foreach (var (name, arity) in documented)
        {
            registered.Should().ContainKey(name,
                $"{name} is in the Command Index but the server does not register it — " +
                "either implement it or remove the row");

            registered[name].Should().Be(arity,
                $"{name} is documented with arity {arity} but registered with arity {registered[name]}");
        }
    }

    /// <summary>
    /// Proves the documented commands actually answer on the wire, not merely
    /// that a table in one file matches a table in another.
    ///
    /// <para>Each is invoked with no arguments. What matters is only that the
    /// server does not answer "unknown command": a registered command either
    /// rejects the arity or, where the arity permits no arguments, succeeds.
    /// Both prove registration. Asserting an error would be wrong — negative
    /// arity is a <i>minimum</i>, so <c>HW.STATS</c> (arity -1) legitimately
    /// succeeds with none.</para>
    /// </summary>
    [Fact]
    public void EveryDocumentedCommand_AnswersOnTheWire()
    {
        var documented = ParseCommandIndex(ReadProtocolFile());

        using var server = new HighwayTestServer();
        using var redis = ConnectionMultiplexer.Connect(server.ConnectionString);
        var db = redis.GetDatabase();

        foreach (var (name, _) in documented)
        {
            try
            {
                db.Execute(name);
            }
            catch (RedisServerException ex)
            {
                ex.Message.Should().NotContain("unknown command",
                    $"{name} is in the Command Index but the server does not recognise it");
            }
        }
    }

    // ──────────────────────────────────────────────────────────────────
    // Direction 2 — everything implemented is documented
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public void EveryRegisteredCommand_AppearsInTheCommandIndex()
    {
        var documented = ParseCommandIndex(ReadProtocolFile())
            .ToDictionary(c => c.Name, c => c.Arity, StringComparer.Ordinal);

        foreach (var (name, arity) in RegisteredCommands())
        {
            documented.Should().ContainKey(name,
                $"{name} is registered but absent from the Command Index in {ProtocolFileName} — " +
                "a command that exists but is undocumented is exactly the drift this test prevents");

            documented[name].Should().Be(arity,
                $"{name} is registered with arity {arity} but documented with arity {documented[name]}");
        }
    }

    [Fact]
    public void CommandIndex_And_Registrations_AreTheSameSize()
    {
        var documented = ParseCommandIndex(ReadProtocolFile());
        var registered = RegisteredCommands();

        documented.Count.Should().Be(registered.Count,
            "the Command Index and the registration table describe the same command surface");
    }
}
