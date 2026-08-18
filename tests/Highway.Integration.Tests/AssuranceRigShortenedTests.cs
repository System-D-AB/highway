namespace Highway.Integration.Tests;

using FluentAssertions;
using Highway.Assurance.Runner;
using Highway.Assurance.Runner.Models;
using Xunit;

public class AssuranceRigShortenedTests
{
    [Fact]
    public async Task AssuranceRig_ShortenedRun_PassesAllInvariantsUnder60Seconds()
    {
        var runId = "ci-short-" + Guid.NewGuid().ToString("N")[..8];
        var tempRunDir = Path.Combine(Path.GetTempPath(), "highway_assurance_ci", runId);

        try
        {
            var profile = RunProfile.CreateShortened();
            var orchestrator = new Orchestrator();

            var sw = System.Diagnostics.Stopwatch.StartNew();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var result = await orchestrator.ExecuteRunAsync(tempRunDir, profile, cts.Token);
            sw.Stop();

            sw.Elapsed.TotalSeconds.Should().BeLessThan(60, "The shortened assurance run must execute in under 60 seconds (R8.1)");
            var reportPath = Path.Combine(tempRunDir, "report.md");
            var reportContent = File.Exists(reportPath) ? File.ReadAllText(reportPath) : "No report.md found";

            result.Verdict.Should().Be("PASSED", $"Assurance Rig must pass all invariants.\n\nReport:\n{reportContent}");
            result.ExitCode.Should().Be(0);

            // Verify all invariants evaluated and passed
            result.Invariants.Should().ContainKey("I1_QueueCompleteness");
            result.Invariants.Should().ContainKey("I2_NoPhantoms");
            result.Invariants.Should().ContainKey("I3_RpcNeverSilent");
            result.Invariants.Should().ContainKey("I4_PubSubPerLiveGroup");
            result.Invariants.Should().ContainKey("I5_Duplicates");
            result.Invariants.Should().ContainKey("I6_DeadLetters");
            result.Invariants.Should().ContainKey("I7_NothingLeftBehind");

            foreach (var (name, inv) in result.Invariants)
            {
                inv.Passed.Should().BeTrue($"Invariant {name} must pass (Notes: {inv.Notes})");
            }

            // Verify activity occurred across all verbs
            result.TotalEventsByKind.GetValueOrDefault("sent").Should().BeGreaterThan(0);
            result.TotalEventsByKind.GetValueOrDefault("processed").Should().BeGreaterThan(0);
            result.TotalEventsByKind.GetValueOrDefault("published").Should().BeGreaterThan(0);
            result.TotalEventsByKind.GetValueOrDefault("received").Should().BeGreaterThan(0);
            result.TotalEventsByKind.GetValueOrDefault("executed").Should().BeGreaterThan(0);
            result.TotalEventsByKind.GetValueOrDefault("replied").Should().BeGreaterThan(0);

            File.Exists(Path.Combine(tempRunDir, "report.json")).Should().BeTrue();
            File.Exists(Path.Combine(tempRunDir, "report.md")).Should().BeTrue();
        }
        catch (Exception ex)
        {
            var brokerLog = Path.Combine(tempRunDir, "processes", "broker.stdout.log");
            if (File.Exists(brokerLog))
            {
                var content = File.ReadAllText(brokerLog);
                throw new Exception($"Test failed: {ex.Message}\nBroker Log:\n{content}", ex);
            }
            throw;
        }
        finally
        {
            // Do not delete on failure so logs can be inspected
        }
    }
}
