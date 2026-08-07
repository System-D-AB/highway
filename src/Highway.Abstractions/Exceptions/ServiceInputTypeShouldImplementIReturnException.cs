namespace Highway.Abstractions.Exceptions;

/// <summary>
/// Thrown during assembly scanning when an AsyncService's input type does not implement IReturn&lt;T&gt;.
/// </summary>
public sealed class ServiceInputTypeShouldImplementIReturnException(Type type)
    : Exception($"The input type '{type.FullName}' does not implement IReturn<T>. " +
                $"Any input to a Highway service must implement IReturn<TResponse>.");
