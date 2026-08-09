using System.Reflection;
using Highway.Abstractions;
using Highway.Abstractions.Exceptions;

namespace Highway.Client.Scanning;

/// <summary>
/// Default implementation of <see cref="ITypeScanner"/> that discovers services and channels
/// by walking base types and interfaces in the provided assemblies.
/// </summary>
internal sealed class DefaultTypeScanner : ITypeScanner
{
    public ScanResult Scan(IReadOnlyList<Assembly> assemblies)
    {
        var allTypes = assemblies
            .SelectMany(a =>
            {
                try { return a.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { return ex.Types.OfType<Type>(); }
            })
            .Where(t => t.IsClass && !t.IsAbstract)
            .ToList();

        return Build(allTypes);
    }

    /// <summary>Internal method for testing — scans a specific list of types.</summary>
    internal ScanResult ScanTypes(IReadOnlyList<Type> types)
        => Build(types.Where(t => t.IsClass && !t.IsAbstract).ToList());

    private static ScanResult Build(List<Type> allTypes) => new()
    {
        Services = DiscoverServices(allTypes),
        Channels = DiscoverChannels(allTypes),
        RequestContracts = DiscoverRequestContracts(allTypes),
        MessageContracts = DiscoverMessageContracts(allTypes),
        Queues = DiscoverQueues(allTypes),
        QueueContracts = DiscoverQueueContracts(allTypes),
    };

    /// <summary>
    /// Every message type carrying <c>[Queue]</c>, whether or not this node processes it
    /// (feature 014).
    ///
    /// <para>Addressing derives from the <b>contract</b>, never from local processors. A
    /// node that only sends must still know that <c>GenerateInvoice</c> addresses
    /// <c>"invoices"</c> — deriving it from what happens to be hosted is precisely the
    /// caller-only defect feature 010 found in the samples.</para>
    /// </summary>
    private static IReadOnlyDictionary<Type, string> DiscoverQueueContracts(List<Type> allTypes)
    {
        var contracts = new Dictionary<Type, string>();

        foreach (var type in allTypes)
        {
            if (!typeof(ISend).IsAssignableFrom(type)) continue;

            // A type implementing ISend without the attribute is a mistake rather than a
            // deliberate opt-out: unlike IReturn<>, ISend has no meaning outside Highway.
            var attribute = type.GetCustomAttribute<QueueAttribute>()
                            ?? throw new QueueAttributeMissingException(type);

            RejectReservedCharacter("Queue", attribute.Name);
            contracts[type] = attribute.Name;
        }

        return contracts;
    }

    /// <summary>
    /// Queues this node processes: one <c>IProcess&lt;T&gt;</c> implementation each.
    /// </summary>
    private static IReadOnlyList<QueueDescriptor> DiscoverQueues(List<Type> allTypes)
    {
        var processInterface = typeof(IProcess<>);
        var byMessageType = new Dictionary<Type, QueueDescriptor>();

        foreach (var type in allTypes)
        {
            var impl = type.GetInterfaces()
                .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == processInterface);

            if (impl is null) continue;

            var messageType = impl.GetGenericArguments()[0];

            var attribute = messageType.GetCustomAttribute<QueueAttribute>()
                            ?? throw new QueueAttributeMissingException(messageType);

            RejectReservedCharacter("Queue", attribute.Name);

            if (byMessageType.TryGetValue(messageType, out var existing))
                throw new DuplicateQueueProcessorException(messageType, existing.ProcessorType, type);

            byMessageType[messageType] = new QueueDescriptor
            {
                Name = attribute.Name,
                MessageType = messageType,
                ProcessorType = type,
                Lifetime = type.GetCustomAttribute<ServiceLifetimeAttribute>()?.Lifetime
                           ?? HighwayServiceLifetime.Scoped,
            };
        }

        return [.. byMessageType.Values];
    }

    /// <summary>
    /// Every request type carrying <c>[Service]</c>, whether or not this node
    /// implements the service.
    ///
    /// <para>This is what lets a caller-only node call anything at all. Deriving
    /// addressing from local implementations instead meant a process that
    /// referenced a contracts library but hosted nothing found none of its own
    /// contracts, and every call returned <c>SERVICE_NOT_FOUND</c> for services
    /// running fine elsewhere.</para>
    ///
    /// <para>A type implementing <c>IReturn&lt;&gt;</c> <em>without</em> the
    /// attribute is skipped rather than rejected — it is a deliberate way to
    /// express "not a Highway service", and the local-catalog 404 path depends
    /// on it.</para>
    /// </summary>
    private static IReadOnlyDictionary<Type, string> DiscoverRequestContracts(List<Type> allTypes)
    {
        var contracts = new Dictionary<Type, string>();

        foreach (var type in allTypes)
        {
            if (type.GetCustomAttribute<ServiceAttribute>() is not { } attribute)
                continue;

            var implementsIReturn = type.GetInterfaces()
                .Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IReturn<>));

            if (implementsIReturn)
                contracts[type] = attribute.Name;
        }

        return contracts;
    }

    /// <summary>
    /// Every message type carrying <c>[Channel]</c>, whether or not this node
    /// subscribes to it — so a node can publish to a channel it does not consume.
    /// </summary>
    private static IReadOnlyDictionary<Type, string> DiscoverMessageContracts(List<Type> allTypes)
    {
        var contracts = new Dictionary<Type, string>();

        foreach (var type in allTypes)
        {
            if (type.GetCustomAttribute<ChannelAttribute>() is not { } attribute)
                continue;

            if (typeof(IPublish).IsAssignableFrom(type))
            {
                RejectReservedCharacter("Channel", attribute.Name);
                contracts[type] = attribute.Name;
            }
        }

        return contracts;
    }

    private static IReadOnlyList<ServiceDescriptor> DiscoverServices(List<Type> allTypes)
    {
        var serviceType = typeof(AsyncService<,>);
        var results = new Dictionary<string, ServiceDescriptor>(StringComparer.OrdinalIgnoreCase);

        foreach (var type in allTypes)
        {
            var baseType = FindGenericBaseType(type, serviceType);
            if (baseType is null) continue;

            var genericArgs = baseType.GetGenericArguments();
            var requestType = genericArgs[0];
            var responseType = genericArgs[1];

            // Validate request type implements IReturn<TRes>
            if (!ImplementsIReturn(requestType, responseType))
                throw new ServiceInputTypeShouldImplementIReturnException(requestType);

            // Validate response type derives from Output
            if (!typeof(Output).IsAssignableFrom(responseType))
                throw new ServiceOutputTypeShouldImplementOutputException(responseType);

            // Validate response type can be constructed for timeout/error results (feature 005)
            if (responseType.GetConstructor(Type.EmptyTypes) is null)
                throw new ResponseTypeRequiresParameterlessConstructorException(responseType);

            // Validate [Service] attribute on request type
            var serviceAttr = requestType.GetCustomAttribute<ServiceAttribute>();
            if (serviceAttr is null)
                throw new ServiceAttributeNotFoundException(requestType);

            // Check uniqueness
            if (results.ContainsKey(serviceAttr.Name))
                throw new ServiceWithSameNameAlreadyExistsException(serviceAttr.Name);

            // Read lifetime
            var lifetimeAttr = type.GetCustomAttribute<ServiceLifetimeAttribute>();
            var lifetime = lifetimeAttr?.Lifetime ?? HighwayServiceLifetime.Scoped;

            var isInternal = type.FullName?.Contains("Highway.Internal") == true
                          || type.FullName?.Contains("Highway.Private") == true;

            results[serviceAttr.Name] = new ServiceDescriptor
            {
                Name = serviceAttr.Name,
                ImplementationType = type,
                RequestType = requestType,
                ResponseType = responseType,
                Lifetime = lifetime,
                IsInternal = isInternal
            };
        }

        return results.Values.ToList();
    }

    private static IReadOnlyList<ChannelDescriptor> DiscoverChannels(List<Type> allTypes)
    {
        var subscribeInterface = typeof(ISubscribe<>);
        var channelMap = new Dictionary<string, (Type MessageType, List<SubscriberDescriptor> Subscribers, bool IsInternal)>(StringComparer.OrdinalIgnoreCase);

        foreach (var type in allTypes)
        {
            var iSubscribeImpl = type.GetInterfaces()
                .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == subscribeInterface);

            if (iSubscribeImpl is null) continue;

            var messageType = iSubscribeImpl.GetGenericArguments()[0];

            // Validate [Channel] attribute on message type
            var channelAttr = messageType.GetCustomAttribute<ChannelAttribute>();
            if (channelAttr is null)
                throw new ChannelAttributeMissingException(messageType);

            RejectReservedCharacter("Channel", channelAttr.Name);

            // Read lifetime
            var lifetimeAttr = type.GetCustomAttribute<ServiceLifetimeAttribute>();
            var lifetime = lifetimeAttr?.Lifetime ?? HighwayServiceLifetime.Scoped;

            var subscriber = new SubscriberDescriptor
            {
                ImplementationType = type,
                Lifetime = lifetime
            };

            if (channelMap.TryGetValue(channelAttr.Name, out var existing))
            {
                // Validate same message type for same channel name
                if (existing.MessageType != messageType)
                    throw new ChannelAlreadyAddedException(channelAttr.Name);

                existing.Subscribers.Add(subscriber);
            }
            else
            {
                var isInternal = type.FullName?.Contains("Highway.Internal") == true
                              || type.FullName?.Contains("Highway.Private") == true;

                channelMap[channelAttr.Name] = (messageType, new List<SubscriberDescriptor> { subscriber }, isInternal);
            }
        }

        return channelMap.Select(kvp => new ChannelDescriptor
        {
            Name = kvp.Key,
            MessageType = kvp.Value.MessageType,
            Subscribers = kvp.Value.Subscribers,
            IsInternal = kvp.Value.IsInternal
        }).ToList();
    }

    private static Type? FindGenericBaseType(Type type, Type genericBaseType)
    {
        var current = type.BaseType;
        while (current is not null)
        {
            if (current.IsGenericType && current.GetGenericTypeDefinition() == genericBaseType)
                return current;
            current = current.BaseType;
        }
        return null;
    }

    private static bool ImplementsIReturn(Type requestType, Type responseType)
    {
        var expectedInterface = typeof(IReturn<>).MakeGenericType(responseType);
        return expectedInterface.IsAssignableFrom(requestType);
    }

    /// <summary>
    /// Rejects a name containing the <c>@</c> character at startup, before any traffic
    /// can derive a colliding queue key (feature 018).
    /// </summary>
    private static void RejectReservedCharacter(string attributeName, string name)
    {
        if (name.Contains('@'))
            throw new ReservedCharacterException(attributeName, name, '@');
    }
}
