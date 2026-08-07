namespace Highway.Abstractions.Exceptions;

/// <summary>
/// Thrown during assembly scanning when two services register with the same name on the same node.
/// </summary>
public sealed class ServiceWithSameNameAlreadyExistsException(string serviceName)
    : Exception($"A service with name '{serviceName}' has already been registered. " +
                $"Each service name must be unique within a single Highway node.");
