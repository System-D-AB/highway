using FluentAssertions;
using Highway.Server;
using Highway.Server.Internal;
using Xunit;

namespace Highway.Integration.Tests;

/// <summary>
/// The AOF registration-manifest guard (substrate review finding A1, fixed on user instruction
/// without a feature spec).
///
/// <para>Garnet's AOF records custom transactions by <b>registration position</b>. Reorder or
/// remove a command and an old AOF does not fail to recover — it replays the <i>wrong</i>
/// procedures against the durable queues, silently. These tests prove the guard writes the
/// manifest on first durable start, tolerates the one compatible change (appending), and
/// refuses the incompatible ones before recovery can run.</para>
/// </summary>
public class CommandManifestTests : IDisposable
{
    private readonly string _dataDir =
        Path.Combine(Path.GetTempPath(), $"highway-manifest-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dataDir))
                Directory.Delete(_dataDir, recursive: true);
        }
        catch
        {
            // Best-effort cleanup of temp AOF data
        }
    }

    private string ManifestPath => Path.Combine(_dataDir, CommandManifest.FileName);

    private string[] ManifestNames() => File.ReadAllLines(ManifestPath)
        .Select(l => l.Trim())
        .Where(l => l.Length > 0 && !l.StartsWith('#'))
        .ToArray();

    [Fact]
    public void FirstDurableStart_WritesTheManifest_AndRestartAcceptsIt()
    {
        using (new HighwayTestServer(o => o.DataDir = _dataDir))
        {
            File.Exists(ManifestPath).Should().BeTrue("the first durable start must record the registration order");
            ManifestNames().Should().StartWith("HW.CALL").And.OnlyHaveUniqueItems();
        }

        // Same build, same order — the restart must be uneventful.
        using var restarted = new HighwayTestServer(o => o.DataDir = _dataDir);
    }

    [Fact]
    public void MemoryOnlyServer_WritesNoManifest()
    {
        using var server = new HighwayTestServer(); // no DataDir
        File.Exists(ManifestPath).Should().BeFalse("there is nothing durable to guard");
    }

    [Fact]
    public void ReorderedRegistrations_RefuseToStart_NamingTheDivergence()
    {
        using (new HighwayTestServer(o => o.DataDir = _dataDir)) { }

        // Simulate a build whose CommandTable swapped its first two entries — the exact
        // change that would make every AOF record of those commands replay as the other.
        var names = ManifestNames();
        (names[0], names[1]) = (names[1], names[0]);
        File.WriteAllLines(ManifestPath, names);

        var act = () => new HighwayTestServer(o => o.DataDir = _dataDir);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*registration position*", "the operator must learn WHY this is dangerous")
            .WithMessage("*position 0*", "and WHERE the orders diverge")
            .WithMessage("*delete the directory*", "and what the remedies are");
    }

    [Fact]
    public void RemovedCommand_RefusesToStart()
    {
        using (new HighwayTestServer(o => o.DataDir = _dataDir)) { }

        // A data directory that knows MORE commands than the build: the stored list with one
        // extra name at the end is what a future build that deleted a command would face.
        File.AppendAllLines(ManifestPath, ["HW.REMOVED-IN-THIS-BUILD"]);

        var act = () => new HighwayTestServer(o => o.DataDir = _dataDir);

        act.Should().Throw<InvalidOperationException>().WithMessage("*were removed*");
    }

    [Fact]
    public void OlderManifest_MissingNewlyAppendedCommands_IsAcceptedAndExtended()
    {
        using (new HighwayTestServer(o => o.DataDir = _dataDir)) { }

        // A manifest from an older build = a strict prefix of today's table. Appending is the
        // one compatible change: every stored position still means what it meant.
        var full = ManifestNames();
        File.WriteAllLines(ManifestPath, full.Take(full.Length - 2));

        using (new HighwayTestServer(o => o.DataDir = _dataDir))
        {
            ManifestNames().Should().Equal(full, "the accepted prefix must be extended back to the full table");
        }
    }
}
