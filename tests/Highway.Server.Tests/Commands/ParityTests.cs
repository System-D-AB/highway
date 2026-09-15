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
    public void EveryHwCommandName_HasAPortedImplementation()
    {
        // 18 files cover the 23 HW.* names (Heartbeat=3 forms, Job=3, Dlq/Stats multi-form).
        var portedDir = LocatePortedSourceDir();
        var names = Directory.GetFiles(portedDir, "Hw*Command.cs")
            .Select(Path.GetFileNameWithoutExtension)
            .ToList();

        names.Should().Contain(new[]
        {
            "HwQSendCommand", "HwCallCommand", "HwReplyCommand", "HwPublishCommand",
            "HwDequeueCommand", "HwQClaimCommand", "HwAckCommand", "HwQAckCommand",
            "HwFailCommand", "HwTouchCommand", "HwDlqCommand", "HwReplayCommand",
            "HwStatsCommand", "HwJobCommand", "HwSubscribeCommand", "HwUnsubscribeCommand",
            "HwHeartbeatCommand", "HwDiscoverCommand",
        }, "all 23 HW.* command names must have a ported home");
    }

    // ============================ R4.1 — behavior suite, in-process ===========

    [Fact]
    public void Behavior_SendClaimAck_RoundTrips_WithByteAccounting()
    {
        using var h = new CommandHarness();
        var t0 = 10_000_000_000_000L;

        h.Run(new HwQSendCommand(), t0, "invoices", "m1", "body-1").Should().Equal("+OK\r\n"u8.ToArray());

        // The queue carries bytes now.
        using (var snap = h.Store.Snapshot())
            h.Store.ReadByteCounter(snap, HighwayNames.QueueBytes("invoices")).Should().BeGreaterThan(0);

        // Claim it.
        var claim = h.Run(new HwQClaimCommand(), t0, "invoices", "worker-1");
        CommandHarness.AsText(claim).Should().StartWith("*2\r\n").And.Contain("m1").And.Contain("body-1");

        // Bytes returned to zero after the claim moved it to the processing list.
        using (var snap = h.Store.Snapshot())
            h.Store.ReadByteCounter(snap, HighwayNames.QueueBytes("invoices")).Should().Be(0);

        // Ack it — gone from the processing list.
        h.Run(new HwQAckCommand(), t0, "invoices", "worker-1", "m1").Should().Equal(":1\r\n"u8.ToArray());

        using var after = h.Store.Snapshot();
        h.Store.ListLength(after, HighwayKeyspace.ListPrefix(HighwayNames.QueueProcessing("invoices", "worker-1")))
            .Should().Be(0);
    }

    [Fact]
    public void Behavior_Promotion_DelayedMessageBecomesClaimableAtItsTime()
    {
        using var h = new CommandHarness();
        var t0 = 10_000_000_000_000L;
        var due = t0 + TimeSpan.FromMinutes(10).Ticks;

        h.Run(new HwQSendCommand(), t0, "invoices", "later", "body", "AT", due.ToString());

        // Before its time: nothing to claim (promotion sees nothing due).
        CommandHarness.AsText(h.Run(new HwQClaimCommand(), t0, "invoices", "w1")).Should().Be("*-1\r\n");

        // At its time: promotion moves it into the live queue and the same claim returns it.
        CommandHarness.AsText(h.Run(new HwQClaimCommand(), due, "invoices", "w1"))
            .Should().StartWith("*2\r\n").And.Contain("later");
    }

    [Fact]
    public void Behavior_LeaseSweep_RedeliversThenDeadLetters_AttemptsCounted()
    {
        // Attempt limit 2: first expiry requeues (attempt→1), second requeues (→2), third
        // exceeds the limit (→3 > 2) and dead-letters.
        var opts = new HighwayServerOptions { Lease = TimeSpan.FromMinutes(5), MaxDeliveryAttempts = 2 };
        using var h = new CommandHarness(options: opts);
        var t = 10_000_000_000_000L;
        var step = TimeSpan.FromMinutes(6).Ticks;

        h.Run(new HwQSendCommand(), t, "invoices", "m1", "body");

        // Each claim sweeps the previous claimant's expired lease before popping.
        h.Run(new HwQClaimCommand(), t, "invoices", "w1");            // attempt 0 claimed
        h.Run(new HwQClaimCommand(), t += step, "invoices", "w2");    // sweep → requeue (1), claim
        h.Run(new HwQClaimCommand(), t += step, "invoices", "w3");    // sweep → requeue (2), claim
        var last = h.Run(new HwQClaimCommand(), t += step, "invoices", "w4"); // sweep → dead-letter (3>2), empty

        CommandHarness.AsText(last).Should().Be("*-1\r\n", "the message dead-lettered rather than being claimed again");

        using var snap = h.Store.Snapshot();
        h.Store.ListLength(snap, HighwayKeyspace.ListPrefix(HighwayNames.QueueDeadLetter("invoices")))
            .Should().Be(1, "the exhausted message is now a dead letter");
    }

    [Fact]
    public void Behavior_DlqPeekRequeue_RoundTrips()
    {
        var opts = new HighwayServerOptions { Lease = TimeSpan.FromMinutes(5), MaxDeliveryAttempts = 1 };
        using var h = new CommandHarness(options: opts);
        var t = 10_000_000_000_000L;
        var step = TimeSpan.FromMinutes(6).Ticks;

        h.Run(new HwQSendCommand(), t, "invoices", "m1", "body");
        h.Run(new HwQClaimCommand(), t, "invoices", "w1");
        h.Run(new HwQClaimCommand(), t += step, "invoices", "w2"); // requeue (attempt 1)
        h.Run(new HwQClaimCommand(), t += step, "invoices", "w3"); // dead-letter (2 > 1)

        // PEEK is non-destructive.
        var peek = h.Run(new HwDlqCommand(), "PEEK", "Q", "invoices", "COUNT", "10");
        CommandHarness.AsText(peek).Should().StartWith("*1\r\n");
        using (var snap = h.Store.Snapshot())
            h.Store.ListLength(snap, HighwayKeyspace.ListPrefix(HighwayNames.QueueDeadLetter("invoices"))).Should().Be(1);

        // REQUEUE moves it back to the live queue.
        h.Run(new HwDlqCommand(), "REQUEUE", "Q", "invoices", "COUNT", "10");
        using var after = h.Store.Snapshot();
        h.Store.ListLength(after, HighwayKeyspace.ListPrefix(HighwayNames.QueueDeadLetter("invoices"))).Should().Be(0);
        h.Store.ListLength(after, HighwayKeyspace.ListPrefix(HighwayNames.Queue("invoices"))).Should().Be(1);
    }

    [Fact]
    public void Behavior_Jobs_FireOnClaim_AndReArm()
    {
        using var h = new CommandHarness();
        var t0 = 10_000_000_000_000L;

        h.Run(new HwJobCommand(), t0, "SET", "nightly", "cleanup", "every:1", "occurrence");

        // Due one second later: the claim fires the occurrence and hands it to the worker.
        var claimAt = t0 + TimeSpan.FromSeconds(2).Ticks;
        CommandHarness.AsText(h.Run(new HwQClaimCommand(), claimAt, "nightly", "w1"))
            .Should().StartWith("*2\r\n").And.Contain("occurrence");

        // Re-armed: still one schedule.
        using var snap = h.Store.Snapshot();
        h.Store.SortedSetLength(snap, HighwayKeyspace.SortedSetPrefix(HighwayNames.JobSchedules("nightly")))
            .Should().Be(1);
    }

    [Fact]
    public void Behavior_PubSub_FanOutThenSubscriberClaims()
    {
        using var h = new CommandHarness();
        var t0 = 10_000_000_000_000L;

        h.Run(new HwSubscribeCommand(), t0, "news", "billing", "node-1");
        h.Run(new HwPublishCommand(), t0, "news", "headline").Should().Equal(":1\r\n"u8.ToArray());

        // The subscriber group claims its copy from the derived queue {channel}@{group}.
        CommandHarness.AsText(h.Run(new HwQClaimCommand(), t0, "news@billing", "node-1"))
            .Should().StartWith("*2\r\n").And.Contain("headline");
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
