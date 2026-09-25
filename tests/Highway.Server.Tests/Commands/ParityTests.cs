using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;
using Highway.Server.Commands.Ported;
using Highway.Server.Commands.Runtime;
using Highway.Server.Internal;
using Highway.Server.Storage;
using Highway.Server.Storage.Layout;
using Xunit;

namespace Highway.Server.Tests.Commands;

/// <summary>
/// 039 T9 — the feature's exit (R2.2, R4). Two nets:
/// <list type="number">
///   <item><b>No engine type in a ported command</b> (037 R3.2 / Gate G1 extended to the
///         commands): the <c>Commands/Ported/</c> sources carry no <c>using Garnet</c>/
///         <c>using Tsavorite</c> directive and no <c>Garnet.</c>/<c>Tsavorite.</c> qualified
///         reference in code — the port stands entirely on <see cref="IHighwayStore"/>. Since
///         041 deleted the Garnet command layer, <c>GarnetRemovalGuardTests</c> sweeps the whole
///         server assembly; this scan stays scoped to <c>Ported/</c> as the focused command check.)</item>
///   <item><b>The R4.1 behavior suite end-to-end on <see cref="InMemoryStore"/> with no
///         socket</b>: sweep, attempt counting, dead-lettering, promotion, jobs and byte
///         accounting all exercised through the real commands, one store, no transport.</item>
/// </list>
/// </summary>
public class ParityTests
{
    // ============================ R2.2 — no engine type =======================

    [Fact]
    public void NoPortedCommand_ReferencesGarnetOrTsavorite()
    {
        var portedDir = LocatePortedSourceDir();
        var files = Directory.GetFiles(portedDir, "*.cs", SearchOption.AllDirectories);
        files.Should().NotBeEmpty();

        var offenders = new List<string>();
        foreach (var file in files)
        {
            var code = StripCommentsAndStrings(File.ReadAllText(file));

            // A using-directive or a qualified type reference to either engine namespace.
            if (Regex.IsMatch(code, @"\busing\s+Garnet\b") || Regex.IsMatch(code, @"\bGarnet\."))
                offenders.Add($"{Path.GetFileName(file)} → Garnet");
            if (Regex.IsMatch(code, @"\busing\s+Tsavorite\b") || Regex.IsMatch(code, @"\bTsavorite\."))
                offenders.Add($"{Path.GetFileName(file)} → Tsavorite");
        }

        offenders.Should().BeEmpty(
            "a ported command stands on IHighwayStore alone (037 R3.2); these still reference an engine type: "
            + string.Join(", ", offenders));
    }

    [Fact]
    public void TheServedSurface_IsProtocol5()
    {
        // 068: the 5.0 work surface — one claim path settled by token, the declarative session, and
        // Highway-owned reply/idempotency state behind HW.* commands. Behaviour lives in WorkProtocolTests.
        var names = Highway.Server.Resp.CommandDispatcher.RegisteredCommandSurface().Select(c => c.Name).ToList();
        names.Should().Contain(new[]
        {
            "HW.CALL", "HW.SEND", "HW.PUBLISH", "HW.CLAIM", "HW.ACK", "HW.TOUCH", "HW.FAIL",
            "HW.REPLY", "HW.RESULT", "HW.IDEM", "HW.SESSION", "HW.HEARTBEAT", "HW.DLQ", "HW.JOB",
            "HW.UNSUBSCRIBE", "HW.DISCOVER", "HW.STATS", "HW.REPLAY",
        });
        names.Should().NotContain(new[] { "HW.Q" + "SEND", "HW.QCLAIM", "HW.QACK", "HW.DEQUEUE", "HW.SUBSCRIBE" },
            "5.0 removed them (068)");
    }

    // ---- helpers -------------------------------------------------------------

    private static string StripCommentsAndStrings(string source)
    {
        source = Regex.Replace(source, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        source = Regex.Replace(source, @"//[^\n]*", " ");
        source = Regex.Replace(source, "\"(?:\\\\.|[^\"\\\\])*\"", "\"\"");
        source = Regex.Replace(source, "@\"(?:[^\"]|\"\")*\"", "\"\"");
        source = Regex.Replace(source, "'(?:\\\\.|[^'\\\\])'", "''");
        return source;
    }

    private static string LocatePortedSourceDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Highway.Server")))
            dir = dir.Parent;
        dir.Should().NotBeNull("the repo root (containing src/Highway.Server) must be locatable");
        var ported = Path.Combine(dir!.FullName, "src", "Highway.Server", "Commands", "Ported");
        Directory.Exists(ported).Should().BeTrue($"the ported command source directory must exist at {ported}");
        return ported;
    }
}
