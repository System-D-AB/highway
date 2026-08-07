namespace Highway.Abstractions.Exceptions;

/// <summary>
/// Thrown during assembly scanning when a service's response type does not derive from Output.
/// </summary>
public sealed class ServiceOutputTypeShouldImplementOutputException(Type type)
    : Exception($"The output type '{type.FullName}' does not derive from Highway.Abstractions.Output. " +
                $"Any response from a Highway service must inherit from the Output base class.");
