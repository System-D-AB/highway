using FluentAssertions;
using Highway.Abstractions.Observability;
using Highway.Server.Commands.Ported;
using Highway.Server.Commands.Runtime;
using Highway.Server.Internal;
using Highway.Server.Storage.Boxes;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Highway.Server.Tests.Commands;

/// <summary>
/// 074 Part A: every RPC request carries a deadline the broker fixed with its own clock (T2), and a call to a
/// service nobody has ever served fails at once (T5).
/// </summary>
public class RpcDeadlineTests
{
    private static readonly long T0 = new DateTime(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc).Ticks;
    private static long At(double seconds) => T0 + TimeSpan.FromSeconds(seconds).Ticks;
    private static string Text(byte[] b) => CommandHarness.AsText(b);

    internal sealed class CapturingLogger : ILogger
    {
        public readonly List<(LogLevel Level, string Text)> Entries = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> f)
        {
            lock (Entries) Entries.Add((level, f(state, ex)));
        }
    }

    /// <summary>The stored deadline of the single request queued on <paramref name="service"/>, read through a claim.</summary>
    internal static long StoredDeadline(CommandHarness h, string service, long now)
    {
        using var batch = h.Store.NewBatch();
        var box = Catalog.Find(h.Store, batch, h.Boxes, BoxKeys.Kind.Service, service)!.Value;
        using var wb = new WorkBox(h.Store, h.Boxes, box, tracksBytes: false, batch);
        var claimed = wb.Claim("probe", now, 1, service);
        claimed.Should().ContainSingle();
        Envelope.TryGetDeadline(wb.ReadInFlight(claimed[0].Token)!.Entry, out var deadline).Should().BeTrue();
        return deadline;
    }

    private static long Held(CommandHarness h, string service)
    {
        using var batch = h.Store.NewBatch();
        if (Catalog.Find(h.Store, batch, h.Boxes, BoxKeys.Kind.Service, service) is not { } box) return 0;
        using var wb = new WorkBox(h.Store, h.Boxes, box, tracksBytes: false, batch);
        return wb.Ready + wb.Delayed;
    }

    // ---- T2: the deadline ---------------------------------------------------------------------------

    [Fact]
    public void ACallWithoutTimeout_GetsTheBrokerDefault_FromTheBrokersClock()
    {
        using var h = new CommandHarness();

        Text(h.Run(new HwCallCommand(), At(0), "prices", "r-1", "p")).Should().Be("+OK\r\n");

        StoredDeadline(h, "prices", At(1)).Should().Be(At(30), "DefaultRpcTimeout is 30 s, measured from acceptance");
    }

    [Fact]
    public void ACallWithTimeout_GetsThatDeadline()
    {
        using var h = new CommandHarness();

        Text(h.Run(new HwCallCommand(), At(0), "prices", "r-1", "p", "TIMEOUT", "2500")).Should().Be("+OK\r\n");

        StoredDeadline(h, "prices", At(1)).Should().Be(At(2.5));
    }

    [Fact]
    public void ATimeoutAboveTheMaximum_IsClamped_AndWarnedOncePerService()
    {
        var log = new CapturingLogger();
        using var h = new CommandHarness(options: new HighwayServerOptions { MaxRpcTimeout = TimeSpan.FromMinutes(5) });
        h.Recorder.Logger = log;
        var shared = new RpcOutcomeLog();

        for (var i = 0; i < 3; i++)
            Text(h.RunWith(new HwCallCommand(), At(0), shared, "prices", $"r-{i}", "p", "TIMEOUT", "86400000")).Should().Be("+OK\r\n");

        StoredDeadline(h, "prices", At(1)).Should().Be(At(300));
        log.Entries.Count(e => e.Level == LogLevel.Warning && e.Text.Contains("clamped")).Should().Be(1);
    }

    [Theory]
    [InlineData("TIMEOUT", "0")]
    [InlineData("TIMEOUT", "-5")]
    [InlineData("TIMEOUT", "abc")]
    [InlineData("TIMEOUT")]
    [InlineData("WAIT", "10")]
    public void ABadTimeout_IsAnInvalidArgument(params string[] tail)
    {
        using var h = new CommandHarness();
        Text(h.Run(new HwCallCommand(), At(0), ["prices", "r-1", "p", .. tail])).Should().StartWith("-ERR HW_INVALID_ARG");
        Held(h, "prices").Should().Be(0);
    }

    [Fact]
    public void ARepeatedCall_IsSuppressedUntilItsOwnDeadline_NotAFixedWindow()
    {
        using var h = new CommandHarness();
        h.Calls = new RecentIds<byte>(TimeSpan.FromHours(1));

        Text(h.Run(new HwCallCommand(), At(0), "prices", "r-1", "p", "TIMEOUT", "20000")).Should().Be("+OK\r\n");
        Text(h.Run(new HwCallCommand(), At(19), "prices", "r-1", "p", "TIMEOUT", "1000")).Should().Be("+OK\r\n");
        Held(h, "prices").Should().Be(1, "before its deadline the repeat is the same call");

        Text(h.Run(new HwCallCommand(), At(21), "prices", "r-1", "p")).Should().Be("+OK\r\n");
        Held(h, "prices").Should().Be(2, "after its deadline its caller has gone: a later call with the id is new work");
    }

    // ---- T5: fail fast ------------------------------------------------------------------------------

    [Fact]
    public void ACallToAServiceNobodyHasEverServed_FailsAtOnce_AndQueuesNothing()
    {
        using var h = new CommandHarness(rpcFailFast: true);

        Text(h.Run(new HwCallCommand(), At(0), "prices", "r-1", "p")).Should().StartWith("-ERR HW_NO_WORKERS").And.Contain("'prices'");

        using var batch = h.Store.NewBatch();
        Catalog.Find(h.Store, batch, h.Boxes, BoxKeys.Kind.Service, "prices").Should().BeNull("a refused call creates nothing");
        h.Recorder.Read("prices", DateTimeOffset.MinValue, DateTimeOffset.MaxValue, null, 10)
            .Should().ContainSingle(e => e.EventType == HighwayEventType.RpcFailFast);
    }

    [Fact]
    public void AServiceThatHasBeenServed_IsQueued_EvenWithEveryWorkerGone()
    {
        using var h = new CommandHarness(rpcFailFast: true);
        Text(h.Run(new HwSessionCommand(), At(0), "worker-1", """{"catalog":{"services":[{"name":"prices"}]}}""")).Should().Be("+OK\r\n");
        h.Run(new HwHeartbeatCommand(), At(1), "worker-1", "BYE");   // the only worker leaves

        Text(h.Run(new HwCallCommand(), At(2), "prices", "r-1", "p")).Should().Be("+OK\r\n",
            "a served service whose workers are restarting must wait for them; its deadline decides");
        Held(h, "prices").Should().Be(1);
    }

    [Fact]
    public void WithFailFastOff_ANeverServedServiceIsQueued()
    {
        using var h = new CommandHarness(rpcFailFast: false);
        Text(h.Run(new HwCallCommand(), At(0), "prices", "r-1", "p")).Should().Be("+OK\r\n");
        Held(h, "prices").Should().Be(1);
    }
}
