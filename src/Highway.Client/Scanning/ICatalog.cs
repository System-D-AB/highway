using Highway.Abstractions;

namespace Highway.Client.Scanning;

/// <summary>
/// Immutable catalog of all discovered services and channels. Built once at startup.
/// </summary>
public interface ICatalog
{
    ServiceDescriptor? GetServiceDescriptor(string serviceName);
    ChannelDescriptor? GetChannelDescriptor(string channelName);
    bool IsServiceRegistered(string serviceName);
    bool IsChannelRegistered(string channelName);
    IReadOnlyList<ServiceDescriptor> AllServices { get; }
    IReadOnlyList<ChannelDescriptor> AllChannels { get; }

    /// <summary>
    /// Generates a serializable catalog info for heartbeat, excluding internal services/channels.
    /// </summary>
    CatalogInfo ToCatalogInfo();

    /// <summary>
    /// Gets the service name for a given request type. Used to avoid per-call reflection.
    /// </summary>
    string? GetServiceNameForRequestType(Type requestType);

    /// <summary>
    /// Gets the channel name for a given message type. Used to avoid per-call reflection.
    /// </summary>
    string? GetChannelNameForMessageType(Type messageType);
}
