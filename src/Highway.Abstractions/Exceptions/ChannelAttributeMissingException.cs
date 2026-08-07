namespace Highway.Abstractions.Exceptions;

/// <summary>
/// Thrown during assembly scanning when a type implements IPublish but is missing the [Channel] attribute.
/// </summary>
public sealed class ChannelAttributeMissingException(Type type)
    : Exception($"The type '{type.FullName}' implements IPublish but is missing the [Channel] attribute. " +
                $"A Highway message type must be decorated with [Channel(\"name\")].");
