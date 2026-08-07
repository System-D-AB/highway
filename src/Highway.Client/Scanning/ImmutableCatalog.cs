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

    /// <param name="services">Services this node hosts.</param>
    /// <param name="channels">Channels this node has local subscribers for.</param>
    /// <param name="requestContracts">
    /// Request type → service name for every <c>[Service]</c> contract in scope,
    /// hosted here or not. Callers address services through this map, so a node
    /// that hosts nothing can still call.
    /// </param>
    /// <param name="messageContracts">
    /// Message type → channel name for every <c>[Channel]</c> contract in scope,
    /// subscribed here or not, so a node can publish to a channel it does not consume.
    /// </param>
    public ImmutableCatalog(
        IReadOnlyList<ServiceDescriptor> services,
        IReadOnlyList<ChannelDescriptor> channels,
        IReadOnlyDictionary<Type, string>? requestContracts = null,
        IReadOnlyDictionary<Type, string>? messageContracts = null)
    {
        AllServices = services;
        AllChannels = channels;

        _services = services.ToFrozenDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
        _channels = channels.ToFrozenDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);

        // Addressing is derived from the contracts, not from what happens to be
        // hosted locally. Hosted implementations are folded in afterwards so a
        // locally hosted service is always addressable even if its contract was
        // somehow missed.
        var requests = new Dictionary<Type, string>();
        if (requestContracts is not null)
        {
            foreach (var (type, name) in requestContracts)
                requests[type] = name;
        }
        foreach (var service in services)
            requests[service.RequestType] = service.Name;

        var messages = new Dictionary<Type, string>();
        if (messageContracts is not null)
        {
            foreach (var (type, name) in messageContracts)
                messages[type] = name;
        }
        foreach (var channel in channels)
            messages[channel.MessageType] = channel.Name;

        _requestTypeToServiceName = requests.ToFrozenDictionary();
        _messageTypeToChannelName = messages.ToFrozenDictionary();
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
