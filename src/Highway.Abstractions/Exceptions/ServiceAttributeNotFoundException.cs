namespace Highway.Abstractions.Exceptions;

/// <summary>
/// Thrown during assembly scanning when a type implements IReturn&lt;T&gt; but is missing the [Service] attribute.
/// </summary>
public sealed class ServiceAttributeNotFoundException(Type type)
    : Exception($"The type '{type.FullName}' implements IReturn<T> but is missing the [Service] attribute. " +
                $"A Highway request type must be decorated with [Service(\"name\")].");
