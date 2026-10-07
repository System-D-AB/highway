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

/// <summary>
/// A dead-letter handler was discovered for an RPC request contract (074). RPC has no dead letters: a request ends
/// with an answer, an error or a timeout, and one that expired or failed its last attempt is removed and recorded
/// (<c>RpcExpired</c>, <c>RpcFailedFinal</c>) — never parked for a handler to run after its caller has gone.
/// </summary>
public sealed class RpcDeadLetterHandlerException(Type messageType, Type handlerType, string service)
    : Exception($"The dead-letter handler '{handlerType.FullName}' handles '{messageType.FullName}', the request of " +
                $"service '{service}', but RPC has no dead letters: an expired or failed request is removed and recorded " +
                "(RpcExpired, RpcFailedFinal), never parked. Remove the handler; for work that must eventually happen, " +
                "use a queue (ISend) and handle its dead letters instead.");

/// <summary>
/// A dead-letter handler was discovered for a published contract this application does not subscribe to (073). A
/// subscriber group's dead letters belong to the group's own queue, and this application's group has none for a
/// channel it does not subscribe to — the handler could never be called.
/// </summary>
public sealed class DeadLetterWithoutSubscriberException(Type messageType, Type handlerType, string channel)
    : Exception($"The dead-letter handler '{handlerType.FullName}' handles '{messageType.FullName}', published on " +
                $"channel '{channel}', but this application has no ISubscribe<{messageType.Name}>. Dead letters of a " +
                "channel belong to each subscriber group, so only an application that subscribes can handle them: add a " +
                "subscriber, or move the handler to the application that has one.");
