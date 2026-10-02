using System.Text;
using FluentAssertions;
using Highway.Server.Internal;
using Highway.Server.Observability;
using Highway.Server.Storage;
using Highway.Server.Storage.Boxes;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Highway.Server.Tests;

public class DeadLetterSweeperTests
{
    private sealed class TestLogger : ILogger
    {
        public readonly List<string> Warnings = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
                Warnings.Add(formatter(state, exception));
        }
    }

    [Fact]
    public void DeadLetterSweeper_WarnsOnce_WhenDeadLettersExceedAgeWarningWithoutFeed()
    {
        using var store = new InMemoryStore();
        var options = new HighwayServerOptions
        {
            DeadLetterAgeWarning = TimeSpan.FromHours(24),
        };
        var logger = new TestLogger();
        var locks = new StripedLock(16);
        var rt = new BoxRuntime();

        var boxId = Catalog.Ensure(store, rt, locks, BoxKeys.Kind.Queue, "orders.unattended", 1000);
        using (var wb = new WorkBox(store, rt, boxId, tracksBytes: true))
        {
            // Create an entry that aged 25 hours ago
            var agedTicks = DateTime.UtcNow.AddHours(-25).Ticks;
            var payload = Envelope.EncodeRpcEntry("msg-aged"u8.ToArray(), "payload"u8.ToArray(), 3);
            var deadRecord = DeadLetter.Encode(agedTicks, 3, DeadLetter.MaxAttempts, payload);
            wb.PushDead(deadRecord, 100);
            wb.Commit();
        }

        using var sweeper = new DeadLetterSweeper(store, options, () => logger);

        // First sweep: should log a warning
        sweeper.Sweep();
        logger.Warnings.Should().HaveCount(1);
        logger.Warnings[0].Should().Contain("orders.unattended");
        logger.Warnings[0].Should().Contain("a list nobody reads");

        // Second sweep: should NOT log again (once per target)
        sweeper.Sweep();
        logger.Warnings.Should().HaveCount(1);
    }

    [Fact]
    public void DeadLetterSweeper_DoesNotWarn_WhenFeedIsDeclaredOrEntryIsFresh()
    {
        using var store = new InMemoryStore();
        var options = new HighwayServerOptions
        {
            DeadLetterAgeWarning = TimeSpan.FromHours(24),
        };
        var logger = new TestLogger();
        var locks = new StripedLock(16);
        var rt = new BoxRuntime();

        // Target 1: Has active feed (FeedOn > 0)
        var b1 = Catalog.Ensure(store, rt, locks, BoxKeys.Kind.Queue, "orders.withfeed", 1000);
        using (var wb1 = new WorkBox(store, rt, b1, tracksBytes: true))
        {
            wb1.SetFeedOn(1);
            var agedTicks = DateTime.UtcNow.AddHours(-25).Ticks;
            var record1 = DeadLetter.Encode(agedTicks, 3, DeadLetter.MaxAttempts, "data"u8.ToArray());
            wb1.PushDead(record1, 100);
            wb1.Commit();
        }

        // Target 2: Fresh dead letter (1 hour old < 24h)
        var b2 = Catalog.Ensure(store, rt, locks, BoxKeys.Kind.Queue, "orders.fresh", 1000);
        using (var wb2 = new WorkBox(store, rt, b2, tracksBytes: true))
        {
            var freshTicks = DateTime.UtcNow.AddHours(-1).Ticks;
            var record2 = DeadLetter.Encode(freshTicks, 3, DeadLetter.MaxAttempts, "data"u8.ToArray());
            wb2.PushDead(record2, 100);
            wb2.Commit();
        }

        using var sweeper = new DeadLetterSweeper(store, options, () => logger);
        sweeper.Sweep();

        logger.Warnings.Should().BeEmpty();
    }
}
