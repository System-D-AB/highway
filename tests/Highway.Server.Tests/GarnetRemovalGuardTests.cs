using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Highway.Server.Tests;

/// <summary>
/// Feature 041 T4 — the two permanent guards that keep Garnet gone.
///
/// <para>Garnet was removed in feature 041. These tests stay in the suite forever so a
/// well-meaning change cannot quietly reintroduce it: the first asserts no source in
/// <c>src/Highway.Server</c> references a Garnet or Tsavorite type; the second asserts the
/// Microsoft copyright header appears in exactly one file — the vendored RESP writer that
/// 037 R2.3 sanctioned as the sole Garnet-derived source.</para>
/// </summary>
public class GarnetRemovalGuardTests
{
    // The one sanctioned exception: the vendored RESP output formatter (037 R2.3 / R6.2).
    private const string VendoredWriterRelative = @"Resp\Vendored\RespWriteUtils.cs";

    /// <summary>
    /// 037 R3.2 / R2 — no engine type on the server's surface. The whole server assembly's
    /// source (not just <c>Commands/Ported/</c>) stands on <see cref="Highway.Server.Storage.IHighwayStore"/>
    /// and the RESP transport alone; not a single <c>using Garnet</c>/<c>using Tsavorite</c> or
    /// qualified <c>Garnet.</c>/<c>Tsavorite.</c> reference survives — except the vendored writer,
    /// which is byte-writing only and carries no Garnet type.
    /// </summary>
    [Fact]
    public void NoServerSource_ReferencesGarnetOrTsavorite()
    {
        var serverDir = LocateServerSourceDir();
        var files = Directory.GetFiles(serverDir, "*.cs", SearchOption.AllDirectories);
        files.Should().NotBeEmpty();

        var offenders = new List<string>();
        foreach (var file in files)
        {
            var code = StripCommentsAndStrings(File.ReadAllText(file));

            if (Regex.IsMatch(code, @"\busing\s+Garnet\b") || Regex.IsMatch(code, @"\bGarnet\."))
                offenders.Add($"{RelativeName(serverDir, file)} → Garnet");
            if (Regex.IsMatch(code, @"\busing\s+Tsavorite\b") || Regex.IsMatch(code, @"\bTsavorite\."))
                offenders.Add($"{RelativeName(serverDir, file)} → Tsavorite");
        }

        offenders.Should().BeEmpty(
            "Garnet was removed in feature 041; the server stands on IHighwayStore + the RESP transport. "
            + "These files still reference an engine type: " + string.Join(", ", offenders));
    }

    /// <summary>
    /// 037 R2.3 — the Microsoft copyright header marks exactly one file, the vendored RESP writer.
    /// Any second occurrence means another Garnet-derived source was copied in without a notice.
    /// </summary>
    [Fact]
    public void MicrosoftCopyrightHeader_AppearsInExactlyTheVendoredWriter()
    {
        var serverDir = LocateServerSourceDir();
        var files = Directory.GetFiles(serverDir, "*.cs", SearchOption.AllDirectories);

        var carriers = files
            .Where(f => Regex.IsMatch(File.ReadAllText(f),
                @"Copyright \(c\) Microsoft Corporation|Licensed to the \.NET Foundation"))
            .Select(f => RelativeName(serverDir, f))
            .ToList();

        carriers.Should().ContainSingle(
            "037 R2.3: the vendored RESP writer is the ONLY Garnet-derived source, and it alone carries "
            + "the Microsoft copyright header. Found: " + string.Join(", ", carriers));
        carriers[0].Should().Be(VendoredWriterRelative,
            "the sole Microsoft-copyright file must be the sanctioned vendored writer");
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

    private static string RelativeName(string root, string file)
        => Path.GetRelativePath(root, file);

    private static string LocateServerSourceDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Highway.Server")))
            dir = dir.Parent;
        dir.Should().NotBeNull("the repo root (containing src/Highway.Server) must be locatable");
        var server = Path.Combine(dir!.FullName, "src", "Highway.Server");
        Directory.Exists(server).Should().BeTrue($"the server source directory must exist at {server}");
        return server;
    }
}
