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
        => Scan(assemblies, assemblies);

    /// <summary>
    /// Scans with the two assembly sets feature 024 separates: <b>contracts</b> come from the
    /// full reference closure in every <c>HostingMode</c> (that reach is load-bearing — it is
    /// what makes a caller-only node work); <b>handlers</b> come only from assemblies the
    /// process consented to host.
    ///
    /// <para>Handlers found in the excluded remainder are not discovered-and-dropped — they
    /// are recorded in <see cref="ScanResult.SkippedHandlerAssemblies"/> so the engine can say
    /// what is not being hosted and why. Silence never hides a skipped handler (013's rule,
    /// applied to <i>not</i> doing something).</para>
    /// </summary>
    public ScanResult Scan(
        IReadOnlyList<Assembly> contractAssemblies,
        IReadOnlyList<Assembly> handlerAssemblies)
    {
        var contractTypes = TypesOf(contractAssemblies);

        var handlerSet = handlerAssemblies.ToHashSet();
        var handlerTypes = ReferenceEquals(contractAssemblies, handlerAssemblies)
            ? contractTypes
            : TypesOf(handlerAssemblies);

        var excluded = contractAssemblies.Where(a => !handlerSet.Contains(a)).ToList();

        return Build(contractTypes, handlerTypes, DiscoverSkipped(excluded));
    }

    /// <summary>Internal method for testing — scans a specific list of types.</summary>
    internal ScanResult ScanTypes(IReadOnlyList<Type> types)
    {
        var filtered = types.Where(t => t.IsClass && !t.IsAbstract).ToList();
        return Build(filtered, filtered, []);
    }

    /// <summary>Internal method for testing — separate contract and handler type lists.</summary>
    internal ScanResult ScanTypes(IReadOnlyList<Type> contractTypes, IReadOnlyList<Type> handlerTypes)
        => Build(
            contractTypes.Where(t => t.IsClass && !t.IsAbstract).ToList(),
            handlerTypes.Where(t => t.IsClass && !t.IsAbstract).ToList(),
            []);

    private static List<Type> TypesOf(IReadOnlyList<Assembly> assemblies) => assemblies
        .SelectMany(a =>
        {
            try { return a.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { return ex.Types.OfType<Type>(); }
        })
        .Where(t => t.IsClass && !t.IsAbstract)
        .ToList();

    private static ScanResult Build(
        List<Type> contractTypes, List<Type> handlerTypes, IReadOnlyList<SkippedHandlerAssembly> skipped) => new()
    {
        Services = DiscoverServices(handlerTypes),
        Channels = DiscoverChannels(handlerTypes),
        RequestContracts = DiscoverRequestContracts(contractTypes),
        MessageContracts = DiscoverMessageContracts(contractTypes),
        Queues = DiscoverQueues(handlerTypes),
        QueueContracts = DiscoverQueueContracts(contractTypes),
        SkippedHandlerAssemblies = skipped,
    };

    /// <summary>
    /// Structural handler detection for the skip report. Deliberately NOT the full discovery
    /// pipeline: an excluded assembly is not being hosted, so its handlers must not be
    /// validated (a missing attribute there is not this process's error) — only named.
    /// </summary>
    internal static bool LooksLikeHandler(Type type)
        => type.GetInterfaces().Any(i => i.IsGenericType
               && (i.GetGenericTypeDefinition() == typeof(IProcess<>)
                || i.GetGenericTypeDefinition() == typeof(ISubscribe<>)))
           || FindGenericBaseType(type, typeof(AsyncService<,>)) is not null;

    private static IReadOnlyList<SkippedHandlerAssembly> DiscoverSkipped(List<Assembly> excluded)
        => excluded.Count == 0 ? [] : DiscoverSkippedFromTypes(TypesOf(excluded));

    /// <summary>Split out for tests: fixture types stand in for an excluded assembly's types.</summary>
    internal static IReadOnlyList<SkippedHandlerAssembly> DiscoverSkippedFromTypes(IEnumerable<Type> types)
    {
        return types
            .Where(LooksLikeHandler)
            .GroupBy(t => t.Assembly)
            .Select(g => new SkippedHandlerAssembly(
                g.Key.GetName().Name ?? g.Key.FullName ?? "<unknown>",
                [.. g.Select(t => t.FullName ?? t.Name).Order()]))
            .OrderBy(a => a.AssemblyName, StringComparer.Ordinal)
            .ToList();
    }

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
            // EVERY closed IProcess<> interface, not the first. FirstOrDefault here silently
            // dropped all but one queue from a class processing several -- no error, the
            // second queue's messages just waited for a processor that never came. Both
            // architecture reviews found it independently (F1; concerns.md 5.6).
            foreach (var impl in type.GetInterfaces()
                .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == processInterface))
            {
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
            // Same rule as queue processors: every closed ISubscribe<>, or a class listening
            // to two channels silently hears one.
            foreach (var iSubscribeImpl in type.GetInterfaces()
                .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == subscribeInterface))
            {
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
