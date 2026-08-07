namespace Highway.Abstractions.Exceptions;

/// <summary>
/// Thrown during assembly scanning when two channel types register with the same channel name.
/// </summary>
public sealed class ChannelAlreadyAddedException(string channelName)
    : Exception($"A channel with name '{channelName}' has already been registered. " +
                $"Each channel name must be unique within a single Highway node.");
