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
    private readonly FrozenDictionary<Type, string> _messageTypeToQueueName;
    private readonly FrozenDictionary<string, QueueDescriptor> _queues;

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
    /// <param name="queues">Queues this node processes (feature 014).</param>
    /// <param name="queueContracts">
    /// Message type → queue name for every <c>[Queue]</c> contract in scope, processed here
    /// or not, so a node can send to a queue it does not process.
    /// </param>
    public ImmutableCatalog(
        IReadOnlyList<ServiceDescriptor> services,
        IReadOnlyList<ChannelDescriptor> channels,
        IReadOnlyDictionary<Type, string>? requestContracts = null,
        IReadOnlyDictionary<Type, string>? messageContracts = null,
        IReadOnlyList<QueueDescriptor>? queues = null,
        IReadOnlyDictionary<Type, string>? queueContracts = null)
    {
        AllServices = services;
        AllChannels = channels;
        AllQueues = queues ?? [];

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

        var queueMessages = new Dictionary<Type, string>();
        if (queueContracts is not null)
        {
            foreach (var (type, name) in queueContracts)
                queueMessages[type] = name;
        }
        foreach (var queue in AllQueues)
            queueMessages[queue.MessageType] = queue.Name;

        _messageTypeToQueueName = queueMessages.ToFrozenDictionary();
        _queues = AllQueues.ToFrozenDictionary(q => q.Name, StringComparer.OrdinalIgnoreCase);

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

    /// <inheritdoc/>
    public IReadOnlyList<QueueDescriptor> AllQueues { get; }

    /// <inheritdoc/>
    public string? GetQueueNameForMessageType(Type messageType)
        => _messageTypeToQueueName.TryGetValue(messageType, out var name) ? name : null;

    /// <inheritdoc/>
    public QueueDescriptor? GetQueue(string name)
        => _queues.TryGetValue(name, out var q) ? q : null;

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

        // Queues were added to CatalogInfo by feature 014 and never populated here, so the
        // node registry has been blind to them ever since: HW.DISCOVER could not answer "who
        // processes this queue?", and 022's catalogue showed a queue being actively processed
        // as "addressed but never declared". Found by running the dashboard against the samples.
        var queues = AllQueues
            .Select(q => new CatalogQueueEntry
            {
                Name = q.Name,
                MessageTypeName = q.MessageType.FullName ?? q.MessageType.Name
            })
            .ToList();

        // The can-use half (024): contract routes minus what this node itself provides — the
        // registry's question is what the node reaches OUT to. Populated HERE, in the same
        // method that builds the provides half, because Queues above was once added to the
        // DTO and not to this method, and the registry was blind to queues for two features.
        var provided = services.Select(s => s.Name)
            .Concat(queues.Select(q => q.Name))
            .Concat(channels.Select(c => c.Name))
            .ToHashSet(StringComparer.Ordinal);

        var uses = new CatalogUses
        {
            Services = Remote(_requestTypeToServiceName.Values, provided),
            Queues = Remote(_messageTypeToQueueName.Values, provided),
            Channels = Remote(_messageTypeToChannelName.Values, provided),
        };

        return new CatalogInfo { Services = services, Channels = channels, Queues = queues, Uses = uses };
    }

    private static IReadOnlyList<string> Remote(IEnumerable<string> routes, HashSet<string> provided)
        => [.. routes.Distinct(StringComparer.Ordinal).Where(r => !provided.Contains(r)).Order(StringComparer.Ordinal)];
}
