namespace Highway.Abstractions;

/// <summary>
/// Thrown when a <see cref="RetryAttribute"/> contains invalid or unparseable policy parameters (feature 073).
/// </summary>
public sealed class InvalidRetryPolicyException(Type contractType, string reason)
    : Exception($"Invalid [Retry] policy on contract '{contractType.FullName}': {reason}");
