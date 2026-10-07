namespace Highway.Abstractions;

/// <summary>A request contract's <c>[Service(Timeout = …)]</c> is not a positive <c>TimeSpan</c> (074).</summary>
public sealed class InvalidServiceTimeoutException(Type requestType, string timeout)
    : Exception($"[Service] on '{requestType.FullName}' declares Timeout = \"{timeout}\", which is not a positive TimeSpan " +
                "(\"hh:mm:ss\", e.g. \"00:05:00\").");
