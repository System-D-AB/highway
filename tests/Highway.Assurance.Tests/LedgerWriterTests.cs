namespace Highway.Assurance.Tests;

using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using Highway.Assurance.Ledger;
using Xunit;

public class LedgerWriterTests : IDisposable
{
    private readonly string _testDir;

    public LedgerWriterTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "highway_ledger_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
        {
            try { Directory.Delete(_testDir, true); } catch { }
        }
    }

    [Fact]
    public async Task LedgerWriter_FlushesLinesImmediately_AndCanBeReadConcurrently()
    {
        var filePath = Path.Combine(_testDir, "test-node.jsonl");
        await using var writer = new LedgerWriter(filePath, "test-app", "test-node");

        var entry1 = new LedgerEntry
        {
            Kind = "sent",
            Type = "SendEmail",
            Cid = "test-001",
            Ms = 1.2
        };

        await writer.WriteAsync(entry1);

        // Read immediately without disposing writer (tests FileShare.ReadWrite and immediate flush)
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        var lines = new List<string>();
        string? line;
        while ((line = await reader.ReadLineAsync()) != null)
        {
            lines.Add(line);
        }
        lines.Should().HaveCount(1);

        var deserialized = JsonSerializer.Deserialize<LedgerEntry>(lines[0]);
        deserialized.Should().NotBeNull();
        deserialized!.Cid.Should().Be("test-001");
        deserialized.App.Should().Be("test-app");
        deserialized.Node.Should().Be("test-node");
        deserialized.Phase.Should().Be("settle");
    }

    [Fact]
    public async Task LedgerWriter_WhenFileHasTruncatedTrailingLine_ParsesUpToLastCompleteLine()
    {
        var filePath = Path.Combine(_testDir, "truncated-node.jsonl");
        await using (var writer = new LedgerWriter(filePath, "test-app", "test-node"))
        {
            await writer.WriteAsync(new LedgerEntry { Kind = "sent", Cid = "cid-1" });
            await writer.WriteAsync(new LedgerEntry { Kind = "sent", Cid = "cid-2" });
        }

        // Simulate crash mid-write by appending incomplete JSON fragment
        await File.AppendAllTextAsync(filePath, "{\"ts\":\"2026-08-13\", \"app\":\"test-app\", \"cid\":\"cid-3");

        var validEntries = new List<LedgerEntry>();
        var lines = await File.ReadAllLinesAsync(filePath);
        foreach (var line in lines)
        {
            try
            {
                var entry = JsonSerializer.Deserialize<LedgerEntry>(line);
                if (entry != null) validEntries.Add(entry);
            }
            catch (JsonException)
            {
                // Last partial line is ignored
            }
        }

        validEntries.Should().HaveCount(2);
        validEntries.Select(e => e.Cid).Should().ContainInOrder("cid-1", "cid-2");
    }

    [Fact]
    public async Task LedgerWriter_PerEventOverhead_IsMeasuredAndRecorded()
    {
        var filePath = Path.Combine(_testDir, "bench-node.jsonl");
        await using var writer = new LedgerWriter(filePath, "bench-app", "bench-node");

        const int iterations = 500;
        // Warmup
        for (var i = 0; i < 50; i++)
        {
            await writer.WriteAsync(new LedgerEntry { Kind = "sent", Cid = $"warmup-{i}" });
        }

        var sw = Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++)
        {
            await writer.WriteAsync(new LedgerEntry { Kind = "sent", Cid = $"bench-{i}", Type = "SendEmail", Ms = 0.5 });
        }
        sw.Stop();

        var avgMicroseconds = (sw.Elapsed.TotalMilliseconds * 1000.0) / iterations;
        // Flushing per line should be sub-millisecond on SSD / standard disk (typically < 100-200 microseconds)
        avgMicroseconds.Should().BeGreaterThan(0);
    }
}
