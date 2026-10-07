using FluentAssertions;
using Highway.Abstractions;
using Highway.Server.Commands.Ported;
using Highway.Server.Storage.Boxes;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Highway.Server.Tests.Commands;

/// <summary>
/// 073 fixes (found in verification): a declared retry policy is stored exactly — <c>maxRequeues: 0</c> means
/// "never requeue", not the default 3 — so re-declaring it logs no "policy changed" warning; and a target declared
/// with the default policy (a contract without <c>[Retry]</c>) loses whatever was stored for it.
/// </summary>
public class SessionRetryPolicyTests
{
    private static readonly long T0 = new DateTime(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc).Ticks;

    private sealed class CapturingLogger : ILogger
    {
        public readonly List<(LogLevel Level, string Text)> Entries = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> f)
            => Entries.Add((level, f(state, ex)));
    }

    private static void Declare(CommandHarness h, long now, int maxAttempts, string backoff, int maxRequeues)
        => CommandHarness.AsText(h.Run(new HwSessionCommand(), now, "node-1",
            $$"""{"policies":[{"kind":"Q","name":"payments","maxAttempts":{{maxAttempts}},"backoff":"{{backoff}}","maxRequeues":{{maxRequeues}}}]}"""))
            .Should().Be("+OK\r\n");

    private static RetryPolicy Stored(CommandHarness h)
    {
        using var batch = h.Store.NewBatch();
        var box = Catalog.Find(h.Store, batch, h.Boxes, BoxKeys.Kind.Queue, "payments")!.Value;
        using var wb = new WorkBox(h.Store, h.Boxes, box, tracksBytes: true, batch);
        return wb.GetRetryPolicy();
    }

    private static int PolicyWarnings(CapturingLogger log)
        => log.Entries.Count(e => e.Level == LogLevel.Warning && e.Text.Contains("Retry policy"));

    [Fact]
    public void MaxRequeuesZero_IsStoredAsZero_AndReDeclaringItIsNotAChange()
    {
        using var h = new CommandHarness();
        var log = new CapturingLogger();
        h.Recorder.Logger = log;

        Declare(h, T0, 4, "exp:1s..30s:jitter", 0);
        Declare(h, T0 + 1, 4, "exp:1s..30s:jitter", 0);

        Stored(h).MaxRequeues.Should().Be(0, "0 means never requeue, not the default 3");
        PolicyWarnings(log).Should().Be(0, "the same policy declared twice did not change");
    }

    [Fact]
    public void AChangedPolicy_IsStored_AndWarnedOnce()
    {
        using var h = new CommandHarness();
        var log = new CapturingLogger();
        h.Recorder.Logger = log;

        Declare(h, T0, 4, "fixed:10s", 1);
        Declare(h, T0 + 1, 6, "fixed:10s", 1);

        Stored(h).MaxAttempts.Should().Be(6);
        PolicyWarnings(log).Should().Be(1);
    }

    [Fact]
    public void TheDefaultPolicy_ClearsWhatWasStored()
    {
        using var h = new CommandHarness();
        var log = new CapturingLogger();
        h.Recorder.Logger = log;

        Declare(h, T0, 4, "exp:1s..30s", 0);
        Declare(h, T0 + 1, 0, "none", 3);   // what a contract without [Retry] declares

        Stored(h).Should().Be(RetryPolicy.None, "removing [Retry] returns the target to the broker's defaults");
        PolicyWarnings(log).Should().Be(1, "the removal is a change, and says so");
    }

    [Fact]
    public void ALargeApplication_DeclaringAPolicyForEveryTarget_IsAccepted()
    {
        // The client declares a policy for every hosted service, queue and group (073 fix): the cap must fit that.
        using var h = new CommandHarness();
        var policies = string.Join(",", Enumerable.Range(0, 1500).Select(i => $$"""{"kind":"Q","name":"q{{i}}","maxAttempts":0,"backoff":"none","maxRequeues":3}"""));

        CommandHarness.AsText(h.Run(new HwSessionCommand(), T0, "node-1", $$"""{"policies":[{{policies}}]}""")).Should().Be("+OK\r\n");
    }

    [Fact]
    public void TheDefaultPolicy_NeverCreatesABox()
    {
        using var h = new CommandHarness();
        Declare(h, T0, 0, "none", 3);

        using var batch = h.Store.NewBatch();
        Catalog.Find(h.Store, batch, h.Boxes, BoxKeys.Kind.Queue, "payments").Should().BeNull("absent already reads as the default");
    }
}
