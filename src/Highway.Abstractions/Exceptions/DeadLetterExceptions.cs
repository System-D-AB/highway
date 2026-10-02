namespace Highway.Abstractions;

/// <summary>
/// Two <see cref="IDeadLetter{T}"/> handlers were discovered for the same contract type.
/// At most one dead-letter handler is allowed per contract (feature 073).
/// </summary>
public sealed class DuplicateDeadLetterHandlerException(Type messageType, Type first, Type second)
    : Exception($"Both '{first.FullName}' and '{second.FullName}' handle dead letters for '{messageType.FullName}'. " +
                "At most one dead-letter handler is permitted per contract.");

/// <summary>
/// A dead-letter handler was discovered for a contract type that this application neither handles nor references.
/// </summary>
public sealed class UnknownDeadLetterContractException(Type messageType, Type handlerType)
    : Exception($"The dead-letter handler '{handlerType.FullName}' handles '{messageType.FullName}', " +
                "but this contract is neither referenced nor handled in the application.");
