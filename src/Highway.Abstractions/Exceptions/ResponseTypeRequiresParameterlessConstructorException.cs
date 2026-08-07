namespace Highway.Abstractions.Exceptions;

/// <summary>
/// Thrown during assembly scanning when a service's response type (a subclass of
/// Output) has no public parameterless constructor. The client engine must be able
/// to construct response instances for timeout (504) and transport-failure paths,
/// so every response type needs a parameterless constructor.
/// </summary>
public sealed class ResponseTypeRequiresParameterlessConstructorException(Type responseType)
    : Exception($"The response type '{responseType.FullName}' must declare a public parameterless constructor. " +
                $"Highway constructs response instances for timeout and transport-failure results.");
