using System.Collections.Frozen;
using Highway.Abstractions;

namespace Highway.Client.Scanning;

/// <summary>
/// Thread-safe, immutable catalog using FrozenDictionary for O(1) lookups.
/// </summary>
internal sealed class ImmutableCatalog : ICatalog
{
    private readonly FrozenDictionary<string, ServiceDescriptor> _services;
    private readonly FrozenDictionary<string, ChannelDescriptor> _channels;
    private readonly FrozenDictionary<Type, string> _requestTypeToServiceName;
    private readonly FrozenDictionary<Type, string> _messageTypeToChannelName;

    public ImmutableCatalog(IReadOnlyList<ServiceDescriptor> services, IReadOnlyList<ChannelDescriptor> channels)
    {
        AllServices = services;
        AllChannels = channels;

        _services = services.ToFrozenDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
        _channels = channels.ToFrozenDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        _requestTypeToServiceName = services.ToFrozenDictionary(s => s.RequestType, s => s.Name);
        _messageTypeToChannelName = channels.ToFrozenDictionary(c => c.MessageType, c => c.Name);
    }

    public IReadOnlyList<ServiceDescriptor> AllServices { get; }
    public IReadOnlyList<ChannelDescriptor> AllChannels { get; }

    public ServiceDescriptor? GetServiceDescriptor(string serviceName)
        => _services.GetValueOrDefault(serviceName);

    public ChannelDescriptor? GetChannelDescriptor(string channelName)
        => _channels.GetValueOrDefault(channelName);

    public bool IsServiceRegistered(string serviceName)
        => _services.ContainsKey(serviceName);

    public bool IsChannelRegistered(string channelName)
        => _channels.ContainsKey(channelName);

    public string? GetServiceNameForRequestType(Type requestType)
        => _requestTypeToServiceName.GetValueOrDefault(requestType);

    public string? GetChannelNameForMessageType(Type messageType)
        => _messageTypeToChannelName.GetValueOrDefault(messageType);

    public CatalogInfo ToCatalogInfo()
    {
        var services = AllServices
            .Where(s => !s.IsInternal)
            .Select(s => new CatalogServiceEntry
            {
                Name = s.Name,
                RequestTypeName = s.RequestType.FullName ?? s.RequestType.Name,
                ResponseTypeName = s.ResponseType.FullName ?? s.ResponseType.Name
            })
            .ToList();

        var channels = AllChannels
            .Where(c => !c.IsInternal)
            .Select(c => new CatalogChannelEntry
            {
                Name = c.Name,
                SubscriberCount = c.Subscribers.Count
            })
            .ToList();

        return new CatalogInfo { Services = services, Channels = channels };
    }
}
