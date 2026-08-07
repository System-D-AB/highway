using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Highway.Server.Tests;

/// <summary>
/// Captures log entries so a test can assert on them.
///
/// <para>Used where the log line <i>is</i> the behaviour rather than a side effect —
/// feature 012's unauthenticated-server notice, where the distinction between
/// informational and warning is a deliberate design decision and would otherwise be
/// untested.</para>
/// </summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<LogEntry> _entries = new();

    public IReadOnlyCollection<LogEntry> Entries => [.. _entries];

    public ILogger CreateLogger(string categoryName) => new Capturing(_entries);

    public void Dispose() { }

    internal readonly record struct LogEntry(LogLevel Level, string Message);

    private sealed class Capturing(ConcurrentQueue<LogEntry> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => entries.Enqueue(new LogEntry(logLevel, formatter(state, exception)));
    }
}
