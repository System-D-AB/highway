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

        var services = DiscoverServices(allTypes);
        var channels = DiscoverChannels(allTypes);

        return new ScanResult { Services = services, Channels = channels };
    }

    /// <summary>Internal method for testing — scans a specific list of types.</summary>
    internal ScanResult ScanTypes(IReadOnlyList<Type> types)
    {
        var allTypes = types.Where(t => t.IsClass && !t.IsAbstract).ToList();
        var services = DiscoverServices(allTypes);
        var channels = DiscoverChannels(allTypes);
        return new ScanResult { Services = services, Channels = channels };
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
}
