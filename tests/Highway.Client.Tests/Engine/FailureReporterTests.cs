using System.Text;
using System.Text.Json;
using FluentAssertions;
using Highway.Client.Engine;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Highway.Client.Tests.Engine;

/// <summary>
/// Feature 015 T6, T7 and T10 — building the failure context, bounding it, and the rule that
/// matters most: <b>a diagnostic write must never be able to break delivery.</b>
/// </summary>
public class FailureReporterTests
{
    private static readonly FailureTarget Target = new(FailureFamily.Queue, "orders", "node-a");

    private static JsonElement DetailOf(Exception ex, string node = "node-a")
        => JsonDocument.Parse(FailureReporter.BuildDetail(ex, node)).RootElement;

    // ---- the context ----------------------------------------------------------

    [Fact]
    public void TheContextCarriesMessageNodeAndTime()
    {
        var detail = DetailOf(new InvalidOperationException("the order was already shipped"));

        detail.GetProperty("message").GetString().Should().Be("the order was already shipped");
        detail.GetProperty("node").GetString().Should().Be("node-a");
        detail.GetProperty("at").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void AThrownExceptionCarriesItsStack()
    {
        Exception caught;
        try { throw new InvalidOperationException("boom"); }
        catch (Exception ex) { caught = ex; }

        var detail = DetailOf(caught);

        detail.TryGetProperty("stack", out var stack).Should().BeTrue();
        stack.GetString().Should().Contain(nameof(AThrownExceptionCarriesItsStack));
    }

    [Fact]
    public void AnInnerExceptionContributesItsTypeOnly()
    {
        var detail = DetailOf(new TimeoutException("outer", new System.IO.IOException("inner detail")));

        detail.GetProperty("inner").GetString().Should().Be("System.IO.IOException");

        // Deliberately not the whole chain: "TimeoutException wrapping an IOException" is the
        // sentence an operator needs; the full chain is the application's own logging's job.
        JsonSerializer.Serialize(detail).Should().NotContain("inner detail");
    }

    [Fact]
    public void AnExceptionWithNoStack_OmitsTheFieldRatherThanWritingNull()
    {
        var detail = DetailOf(new InvalidOperationException("never thrown"));

        detail.TryGetProperty("stack", out _).Should().BeFalse();
    }

    // ---- bounding, client-side ------------------------------------------------

    [Fact]
    public void AnOversizedMessage_IsCutBeforeTheWire()
    {
        var huge = new string('x', FailureReporter.MaxMessageChars * 3);

        var message = DetailOf(new InvalidOperationException(huge)).GetProperty("message").GetString()!;

        message.Length.Should().BeLessThan(huge.Length,
            "bytes the server would only discard must never be transmitted");
        message.Should().EndWith(FailureReporter.TruncationMarker,
            "a truncated field must never read as a complete one");
        message.Should().StartWith("xxx");
    }

    [Fact]
    public void TruncationKeepsTheFront()
    {
        // The top frames of a stack say where it threw. Cutting the tail keeps the useful half.
        var value = string.Concat("HEAD", new string('.', 5_000), "TAIL");

        var cut = FailureReporter.Truncate(value, 100);

        cut.Should().StartWith("HEAD");
        cut.Should().NotContain("TAIL");
    }

    [Fact]
    public void AShortValue_IsNotMarkedAsTruncated()
    {
        FailureReporter.Truncate("brief", 100).Should().Be("brief");
    }

    // ---- the rule that matters ------------------------------------------------

    /// <summary>
    /// T10/T14 — <b>reporting cannot break delivery.</b> Without this test the entire rule is
    /// unverified prose.
    /// </summary>
    [Fact]
    public async Task AFailingReport_IsSwallowedAndDoesNotMaskTheOriginal()
    {
        var connection = Substitute.For<IHighwayConnection>();
        connection
            .FailAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                       Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("the broker is unreachable"));

        var log = new CapturingLogger();
        var reporter = new FailureReporter(connection, log);
        var original = new TimeoutException("the handler timed out");

        // Must not throw. A consumer that dies because its diagnostics died is worse than one
        // with no diagnostics at all.
        await reporter.ReportAsync(Target, "msg-1", original, "it will be redelivered");

        // The original exception must still be visible - losing the diagnosis is survivable,
        // losing the thing being diagnosed is not.
        log.Entries.Should().Contain(e => e.Exception!.ToString().Contains("the handler timed out"));
        log.Entries.Should().Contain(e => e.Exception!.ToString().Contains("the broker is unreachable"));
    }

    [Fact]
    public async Task ASuccessfulReport_SendsTheWireKindForItsFamily()
    {
        var connection = Substitute.For<IHighwayConnection>();
        connection
            .FailAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                       Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        var reporter = new FailureReporter(connection, NullLogger.Instance);

        await reporter.ReportAsync(
            new FailureTarget(FailureFamily.Channel, "orders.shipped", "billing"),
            "42", new TimeoutException("t"), "acked anyway");

        await connection.Received(1).FailAsync(
            "Q", "orders.shipped", "billing", "42", "System.TimeoutException",
            Arg.Any<byte[]>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void EachFamilyMapsToTheGrammarHwDlqAlreadyParses()
    {
        // A public [Theory] cannot take an internal enum, and making the enum public to suit
        // a test would be the tail wagging the dog.
        new FailureTarget(FailureFamily.Service, "n", "s").WireKind.Should().Be("SVC");
        new FailureTarget(FailureFamily.Queue, "n", "s").WireKind.Should().Be("Q");
        new FailureTarget(FailureFamily.Channel, "n", "s").WireKind.Should().Be("Q");
    }

    /// <summary>Captures log entries; NullLogger cannot be asserted against.</summary>
    private sealed class CapturingLogger : ILogger
    {
        public readonly List<(LogLevel Level, string Message, Exception? Exception)> Entries = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception), exception));
    }
}
