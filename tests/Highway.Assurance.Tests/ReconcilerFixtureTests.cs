namespace Highway.Assurance.Tests;

using FluentAssertions;
using Highway.Assurance.Reconciler;
using Xunit;

public class ReconcilerFixtureTests : IDisposable
{
    private readonly string _baseTestDir;

    public ReconcilerFixtureTests()
    {
        _baseTestDir = Path.Combine(Path.GetTempPath(), "highway_reconciler_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_baseTestDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_baseTestDir))
        {
            try { Directory.Delete(_baseTestDir, true); } catch { }
        }
    }

    private string CreateRunDir(string name)
    {
        var runDir = Path.Combine(_baseTestDir, name);
        Directory.CreateDirectory(Path.Combine(runDir, "ledgers"));
        Directory.CreateDirectory(Path.Combine(runDir, "broker"));
        return runDir;
    }

    [Fact]
    public async Task Reconciler_WhenPlantedLoss_FailsI1_AndNamesOffendingCid()
    {
        var runDir = CreateRunDir("planted_loss");
        var edgeLedger = Path.Combine(runDir, "ledgers", "edge-1.jsonl");
        var mailerLedger = Path.Combine(runDir, "ledgers", "mailer-1.jsonl");

        await File.WriteAllLinesAsync(edgeLedger,
        [
            "{\"ts\":\"2026-08-13T09:14:00Z\",\"app\":\"edge\",\"node\":\"edge-1\",\"phase\":\"steady\",\"kind\":\"sent\",\"type\":\"SendEmail\",\"cid\":\"email-good-1\"}",
            "{\"ts\":\"2026-08-13T09:14:01Z\",\"app\":\"edge\",\"node\":\"edge-1\",\"phase\":\"steady\",\"kind\":\"sent\",\"type\":\"SendEmail\",\"cid\":\"email-LOST-PLANTED\"}"
        ]);

        await File.WriteAllLinesAsync(mailerLedger,
        [
            "{\"ts\":\"2026-08-13T09:14:02Z\",\"app\":\"notifications\",\"node\":\"mailer-1\",\"phase\":\"steady\",\"kind\":\"processed\",\"type\":\"SendEmail\",\"cid\":\"email-good-1\",\"attempt\":1}"
        ]);

        var engine = new ReconcilerEngine();
        var result = await engine.ReconcileRunDirectoryAsync(runDir);

        result.Verdict.Should().Be("FAILED");
        result.ExitCode.Should().Be(1);

        var i1 = result.Invariants["I1_QueueCompleteness"];
        i1.Passed.Should().BeFalse();
        i1.Verdict.Should().Be("FAIL");
        i1.OffendingCids.Should().Contain("email-LOST-PLANTED");
        i1.OffendingLedgerLines.Should().ContainKey("email-LOST-PLANTED");
    }

    [Fact]
    public async Task Reconciler_WhenPlantedPhantom_FailsI2_AndNamesPhantomCid()
    {
        var runDir = CreateRunDir("planted_phantom");
        var edgeLedger = Path.Combine(runDir, "ledgers", "edge-1.jsonl");
        var mailerLedger = Path.Combine(runDir, "ledgers", "mailer-1.jsonl");

        await File.WriteAllLinesAsync(edgeLedger,
        [
            "{\"ts\":\"2026-08-13T09:14:00Z\",\"app\":\"edge\",\"node\":\"edge-1\",\"phase\":\"steady\",\"kind\":\"sent\",\"type\":\"SendEmail\",\"cid\":\"email-legit\"}"
        ]);

        await File.WriteAllLinesAsync(mailerLedger,
        [
            "{\"ts\":\"2026-08-13T09:14:01Z\",\"app\":\"notifications\",\"node\":\"mailer-1\",\"phase\":\"steady\",\"kind\":\"processed\",\"type\":\"SendEmail\",\"cid\":\"email-legit\",\"attempt\":1}",
            "{\"ts\":\"2026-08-13T09:14:02Z\",\"app\":\"notifications\",\"node\":\"mailer-1\",\"phase\":\"steady\",\"kind\":\"processed\",\"type\":\"SendEmail\",\"cid\":\"email-PHANTOM-PLANTED\",\"attempt\":1}"
        ]);

        var engine = new ReconcilerEngine();
        var result = await engine.ReconcileRunDirectoryAsync(runDir);

        result.Verdict.Should().Be("FAILED");
        var i2 = result.Invariants["I2_NoPhantoms"];
        i2.Passed.Should().BeFalse();
        i2.OffendingCids.Should().Contain("email-PHANTOM-PLANTED");
    }

    [Fact]
    public async Task Reconciler_WhenPlantedSilentRpc_FailsI3_AndNamesSilentCid()
    {
        var runDir = CreateRunDir("planted_silent_rpc");
        var edgeLedger = Path.Combine(runDir, "ledgers", "edge-1.jsonl");

        await File.WriteAllLinesAsync(edgeLedger,
        [
            "{\"ts\":\"2026-08-13T09:14:00Z\",\"app\":\"edge\",\"node\":\"edge-1\",\"phase\":\"steady\",\"kind\":\"executed\",\"type\":\"ValidateAccount\",\"cid\":\"rpc-ok-1\"}",
            "{\"ts\":\"2026-08-13T09:14:01Z\",\"app\":\"edge\",\"node\":\"edge-1\",\"phase\":\"steady\",\"kind\":\"replied\",\"type\":\"ValidateAccount\",\"cid\":\"rpc-ok-1\",\"status\":200}",
            "{\"ts\":\"2026-08-13T09:14:02Z\",\"app\":\"edge\",\"node\":\"edge-1\",\"phase\":\"steady\",\"kind\":\"executed\",\"type\":\"ValidateAccount\",\"cid\":\"rpc-SILENT-PLANTED\"}"
        ]);

        var engine = new ReconcilerEngine();
        var result = await engine.ReconcileRunDirectoryAsync(runDir);

        result.Verdict.Should().Be("FAILED");
        var i3 = result.Invariants["I3_RpcNeverSilent"];
        i3.Passed.Should().BeFalse();
        i3.OffendingCids.Should().Contain("rpc-SILENT-PLANTED");
    }

    [Fact]
    public async Task Reconciler_WhenPlantedMissedPubSub_FailsI4_AndNamesMissingGroup()
    {
        var runDir = CreateRunDir("planted_missed_pubsub");
        var edgeLedger = Path.Combine(runDir, "ledgers", "edge-1.jsonl");
        var notifsLedger = Path.Combine(runDir, "ledgers", "notifications-1.jsonl");

        await File.WriteAllLinesAsync(edgeLedger,
        [
            "{\"ts\":\"2026-08-13T09:14:00Z\",\"app\":\"edge\",\"node\":\"edge-1\",\"phase\":\"steady\",\"kind\":\"published\",\"type\":\"UserSignedUp\",\"cid\":\"pub-MISSED-PLANTED\"}"
        ]);

        // Notifications never wrote a 'received' event for this cid
        await File.WriteAllLinesAsync(notifsLedger,
        [
            "{\"ts\":\"2026-08-13T09:14:05Z\",\"app\":\"notifications\",\"node\":\"notifications-subs-1\",\"phase\":\"steady\",\"kind\":\"phase\",\"type\":\"steady\"}"
        ]);

        var engine = new ReconcilerEngine();
        var result = await engine.ReconcileRunDirectoryAsync(runDir);

        result.Verdict.Should().Be("FAILED");
        var i4 = result.Invariants["I4_PubSubPerLiveGroup"];
        i4.Passed.Should().BeFalse();
        i4.OffendingCids.Should().Contain("pub-MISSED-PLANTED");
    }

    [Fact]
    public async Task Reconciler_WhenExpectedMiss_PassesI4_WithNotes()
    {
        var runDir = CreateRunDir("expected_miss");
        var edgeLedger = Path.Combine(runDir, "ledgers", "edge-1.jsonl");

        await File.WriteAllLinesAsync(edgeLedger,
        [
            "{\"ts\":\"2026-08-13T09:14:00Z\",\"app\":\"edge\",\"node\":\"edge-1\",\"phase\":\"settle\",\"kind\":\"published\",\"type\":\"UserSignedUp\",\"cid\":\"pub-before-group-reg\"}",
            "{\"ts\":\"2026-08-13T09:14:01Z\",\"app\":\"edge\",\"node\":\"edge-1\",\"phase\":\"settle\",\"kind\":\"expected-miss\",\"type\":\"UserSignedUp\",\"cid\":\"pub-before-group-reg\"}"
        ]);

        var engine = new ReconcilerEngine();
        var result = await engine.ReconcileRunDirectoryAsync(runDir);

        var i4 = result.Invariants["I4_PubSubPerLiveGroup"];
        i4.Passed.Should().BeTrue();
        i4.Verdict.Should().Be("PASS_WITH_NOTES");
        i4.ExpectedMissCount.Should().Be(1);
    }

    [Fact]
    public async Task Reconciler_WhenDuplicatesPresent_CountsAndPassesI5()
    {
        var runDir = CreateRunDir("planted_duplicates");
        var edgeLedger = Path.Combine(runDir, "ledgers", "edge-1.jsonl");
        var mailer1Ledger = Path.Combine(runDir, "ledgers", "mailer-1.jsonl");
        var mailer2Ledger = Path.Combine(runDir, "ledgers", "mailer-2.jsonl");

        await File.WriteAllLinesAsync(edgeLedger,
        [
            "{\"ts\":\"2026-08-13T09:14:00Z\",\"app\":\"edge\",\"node\":\"edge-1\",\"phase\":\"turbulence\",\"kind\":\"sent\",\"type\":\"SendEmail\",\"cid\":\"dup-cid-1\"}"
        ]);

        await File.WriteAllLinesAsync(mailer2Ledger,
        [
            "{\"ts\":\"2026-08-13T09:14:01Z\",\"app\":\"notifications\",\"node\":\"mailer-2\",\"phase\":\"turbulence\",\"kind\":\"processed\",\"type\":\"SendEmail\",\"cid\":\"dup-cid-1\",\"attempt\":1}"
        ]);

        await File.WriteAllLinesAsync(mailer1Ledger,
        [
            "{\"ts\":\"2026-08-13T09:14:16Z\",\"app\":\"notifications\",\"node\":\"mailer-1\",\"phase\":\"turbulence\",\"kind\":\"processed\",\"type\":\"SendEmail\",\"cid\":\"dup-cid-1\",\"attempt\":2}"
        ]);

        var engine = new ReconcilerEngine();
        var result = await engine.ReconcileRunDirectoryAsync(runDir);

        var i5 = result.Invariants["I5_Duplicates"];
        i5.Passed.Should().BeTrue();
        i5.DuplicateCount.Should().Be(1);
        i5.ProcessedCount.Should().Be(2);
        i5.Verdict.Should().Be("PASS_WITH_NOTES");
    }

    [Fact]
    public async Task Reconciler_WhenDeadLetterPresent_FailsI6()
    {
        var runDir = CreateRunDir("planted_dlq");
        var edgeLedger = Path.Combine(runDir, "ledgers", "edge-1.jsonl");
        await File.WriteAllLinesAsync(edgeLedger, ["{\"ts\":\"2026-08-13T09:14:00Z\",\"app\":\"edge\",\"node\":\"edge-1\",\"phase\":\"settle\",\"kind\":\"phase\",\"type\":\"settle\"}"]);

        var dlqFile = Path.Combine(runDir, "broker", "dlq.json");
        await File.WriteAllTextAsync(dlqFile, "[{\"id\":\"01J...\",\"reason\":\"max_attempts_exceeded\"}]");

        var engine = new ReconcilerEngine();
        var result = await engine.ReconcileRunDirectoryAsync(runDir);

        var i6 = result.Invariants["I6_DeadLetters"];
        i6.Passed.Should().BeFalse();
        i6.ProcessedCount.Should().Be(1);
    }

    [Fact]
    public async Task Reconciler_WhenNonZeroQueueDepth_FailsI7()
    {
        var runDir = CreateRunDir("planted_undrained");
        var edgeLedger = Path.Combine(runDir, "ledgers", "edge-1.jsonl");
        await File.WriteAllLinesAsync(edgeLedger, ["{\"ts\":\"2026-08-13T09:14:00Z\",\"app\":\"edge\",\"node\":\"edge-1\",\"phase\":\"settle\",\"kind\":\"phase\",\"type\":\"settle\"}"]);

        var statsFile = Path.Combine(runDir, "broker", "stats-samples.jsonl");
        await File.WriteAllLinesAsync(statsFile,
        [
            "{\"ts\":\"2026-08-13T09:14:00Z\",\"queues\":{\"email.send\":{\"depth\":12}}}"
        ]);

        var engine = new ReconcilerEngine();
        var result = await engine.ReconcileRunDirectoryAsync(runDir);

        var i7 = result.Invariants["I7_NothingLeftBehind"];
        i7.Passed.Should().BeFalse();
        i7.ProcessedCount.Should().Be(12);
    }

    [Fact]
    public async Task Reconciler_WhenCleanRun_PassesAllInvariants()
    {
        var runDir = CreateRunDir("clean_run");
        var edgeLedger = Path.Combine(runDir, "ledgers", "edge-1.jsonl");
        var accountsLedger = Path.Combine(runDir, "ledgers", "accounts-1.jsonl");
        var notifsLedger = Path.Combine(runDir, "ledgers", "notifications-1.jsonl");
        var mailerLedger = Path.Combine(runDir, "ledgers", "mailer-1.jsonl");

        await File.WriteAllLinesAsync(edgeLedger,
        [
            "{\"ts\":\"2026-08-13T09:14:00Z\",\"app\":\"edge\",\"node\":\"edge-1\",\"phase\":\"steady\",\"kind\":\"sent\",\"type\":\"SendEmail\",\"cid\":\"edge-001\"}",
            "{\"ts\":\"2026-08-13T09:14:01Z\",\"app\":\"edge\",\"node\":\"edge-1\",\"phase\":\"steady\",\"kind\":\"published\",\"type\":\"UserSignedUp\",\"cid\":\"edge-002\"}",
            "{\"ts\":\"2026-08-13T09:14:02Z\",\"app\":\"edge\",\"node\":\"edge-1\",\"phase\":\"steady\",\"kind\":\"executed\",\"type\":\"ValidateAccount\",\"cid\":\"edge-003\"}",
            "{\"ts\":\"2026-08-13T09:14:03Z\",\"app\":\"edge\",\"node\":\"edge-1\",\"phase\":\"steady\",\"kind\":\"replied\",\"type\":\"ValidateAccount\",\"cid\":\"edge-003\",\"status\":200,\"ms\":4.2}",
            "{\"ts\":\"2026-08-13T09:14:04Z\",\"app\":\"edge\",\"node\":\"edge-1\",\"phase\":\"steady\",\"kind\":\"received\",\"type\":\"EmailDispatched\",\"cid\":\"notif-001\",\"group\":\"edge\"}"
        ]);

        await File.WriteAllLinesAsync(accountsLedger,
        [
            "{\"ts\":\"2026-08-13T09:14:00Z\",\"app\":\"accounts\",\"node\":\"accounts-1\",\"phase\":\"steady\",\"kind\":\"sent\",\"type\":\"SendEmail\",\"cid\":\"acc-001\"}"
        ]);

        await File.WriteAllLinesAsync(notifsLedger,
        [
            "{\"ts\":\"2026-08-13T09:14:01Z\",\"app\":\"notifications\",\"node\":\"notifications-subs-1\",\"phase\":\"steady\",\"kind\":\"received\",\"type\":\"UserSignedUp\",\"cid\":\"edge-002\",\"group\":\"notifications-subs\"}",
            "{\"ts\":\"2026-08-13T09:14:02Z\",\"app\":\"notifications\",\"node\":\"notifications-subs-1\",\"phase\":\"steady\",\"kind\":\"published\",\"type\":\"EmailDispatched\",\"cid\":\"notif-001\"}"
        ]);

        await File.WriteAllLinesAsync(mailerLedger,
        [
            "{\"ts\":\"2026-08-13T09:14:05Z\",\"app\":\"notifications\",\"node\":\"mailer-1\",\"phase\":\"steady\",\"kind\":\"processed\",\"type\":\"SendEmail\",\"cid\":\"edge-001\",\"attempt\":1}",
            "{\"ts\":\"2026-08-13T09:14:06Z\",\"app\":\"notifications\",\"node\":\"mailer-1\",\"phase\":\"steady\",\"kind\":\"processed\",\"type\":\"SendEmail\",\"cid\":\"acc-001\",\"attempt\":1}"
        ]);

        var statsFile = Path.Combine(runDir, "broker", "stats-samples.jsonl");
        await File.WriteAllLinesAsync(statsFile,
        [
            "{\"ts\":\"2026-08-13T09:14:00Z\",\"queues\":{\"email.send\":{\"depth\":2}}}",
            "{\"ts\":\"2026-08-13T09:14:10Z\",\"queues\":{\"email.send\":{\"depth\":0}}}"
        ]);

        var engine = new ReconcilerEngine();
        var result = await engine.ReconcileRunDirectoryAsync(runDir);

        result.Verdict.Should().Be("PASSED");
        result.ExitCode.Should().Be(0);
        result.Invariants.Values.Should().OnlyContain(i => i.Passed);

        File.Exists(Path.Combine(runDir, "report.json")).Should().BeTrue();
        File.Exists(Path.Combine(runDir, "report.md")).Should().BeTrue();
    }
}
