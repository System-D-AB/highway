# Design: Assembly Scanning & Service Catalog

## Overview

The assembly scanning pipeline runs once at startup during `AddHighway()`. It produces an immutable `ICatalog` that the engine uses for all subsequent dispatch. All reflection is front-loaded — runtime dispatch uses pre-compiled delegates with zero reflection.

## Architecture

```
AddHighway(options)
    │
    ▼
┌─────────────────────────────┐
│  AssemblySource             │  Collects assemblies to scan
│  (IAssemblySource)          │  (AppDomain + explicit + exclusions)
└─────────────┬───────────────┘
              │ IEnumerable<Assembly>
              ▼
┌─────────────────────────────┐
│  TypeScanner                │  Finds AsyncService<,> and ISubscribe<> types
│  (ITypeScanner)             │  Validates attributes and constraints
└─────────────┬───────────────┘
              │ (IEnumerable<ServiceDescriptor>, IEnumerable<ChannelDescriptor>)
              ▼
┌─────────────────────────────┐
│  DelegateCompiler           │  Compiles ExecuteAsync/SubscribeAsync delegates
│  (IDelegateCompiler)        │  One per service, one per subscriber
└─────────────┬───────────────┘
              │ ServiceDescriptor with Func<>, ChannelDescriptor with Func<>
              ▼
┌─────────────────────────────┐
│  Catalog                    │  Immutable dictionary of services + channels
│  (ICatalog)                 │  O(1) lookup by name
└─────────────┬───────────────┘
              │
              ▼
┌─────────────────────────────┐
│  DI Registration            │  Registers each service/subscriber type
│                             │  in IServiceCollection with correct lifetime
└─────────────────────────────┘
```

## Component Design

### IAssemblySource

```csharp
namespace Highway.Client.Scanning;

/// <summary>
/// Provides the set of assemblies to scan for Highway services and channels.
/// </summary>
public interface IAssemblySource
{
    IReadOnlyList<Assembly> GetAssemblies();
}
```

**Default implementation:** `DefaultAssemblySource`

```csharp
internal sealed class DefaultAssemblySource(HighwayOptions options) : IAssemblySource
{
    public IReadOnlyList<Assembly> GetAssemblies()
    {
        var highwayAbstractions = typeof(IReturn<>).Assembly.GetName();

        var assemblies = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic)
            .Where(a => !IsExcluded(a))
            .Where(a => ReferencesHighway(a, highwayAbstractions))
            .ToList();

        // Add explicitly specified assemblies
        foreach (var additional in options.AdditionalAssemblies)
        {
            if (!assemblies.Contains(additional))
                assemblies.Add(additional);
        }

        return assemblies;
    }
}
```

### ITypeScanner

```csharp
namespace Highway.Client.Scanning;

public interface ITypeScanner
{
    ScanResult Scan(IReadOnlyList<Assembly> assemblies);
}

public sealed class ScanResult
{
    public required IReadOnlyList<ServiceDescriptor> Services { get; init; }
    public required IReadOnlyList<ChannelDescriptor> Channels { get; init; }
}
```

**Validation rules (throw on failure):**

| Rule | Exception |
|---|---|
| Input type must implement `IReturn<TRes>` | `ServiceInputTypeShouldImplementIReturnException` |
| Output type must derive from `Output` | `ServiceOutputTypeShouldImplementOutputException` |
| Input type must have `[Service]` | `ServiceAttributeNotFoundException` |
| Service name must be unique | `ServiceWithSameNameAlreadyExistsException` |
| Message type must have `[Channel]` | `ChannelAttributeMissingException` |
| Channel name must be unique per message type | `ChannelAlreadyAddedException` |

### ServiceDescriptor

```csharp
namespace Highway.Client.Scanning;

/// <summary>
/// Immutable metadata about a discovered service.
/// </summary>
public sealed class ServiceDescriptor
{
    /// <summary>Service name from [Service] attribute.</summary>
    public required string Name { get; init; }

    /// <summary>The AsyncService&lt;T,TRes&gt; implementation type.</summary>
    public required Type ImplementationType { get; init; }

    /// <summary>The request type (T) implementing IReturn&lt;TRes&gt;.</summary>
    public required Type RequestType { get; init; }

    /// <summary>The response type (TRes) deriving from Output.</summary>
    public required Type ResponseType { get; init; }

    /// <summary>DI lifetime for this service.</summary>
    public required HighwayServiceLifetime Lifetime { get; init; }

    /// <summary>Pre-compiled delegate: (service, request, ct) → Task&lt;Output&gt;</summary>
    public required Func<object, object, CancellationToken, Task<object>> InvokeDelegate { get; init; }

    /// <summary>Whether this is an internal Highway service (hidden from heartbeat).</summary>
    public bool IsInternal { get; init; }
}
```

### ChannelDescriptor

```csharp
namespace Highway.Client.Scanning;

/// <summary>
/// Immutable metadata about a discovered channel.
/// </summary>
public sealed class ChannelDescriptor
{
    /// <summary>Channel name from [Channel] attribute.</summary>
    public required string Name { get; init; }

    /// <summary>The message type (T) implementing IPublish.</summary>
    public required Type MessageType { get; init; }

    /// <summary>All subscriber descriptors for this channel.</summary>
    public required IReadOnlyList<SubscriberDescriptor> Subscribers { get; init; }

    /// <summary>Whether this is an internal Highway channel.</summary>
    public bool IsInternal { get; init; }
}

public sealed class SubscriberDescriptor
{
    /// <summary>The ISubscribe&lt;T&gt; implementation type.</summary>
    public required Type ImplementationType { get; init; }

    /// <summary>DI lifetime for this subscriber.</summary>
    public required HighwayServiceLifetime Lifetime { get; init; }

    /// <summary>Pre-compiled delegate: (subscriber, message, ct) → Task</summary>
    public required Func<object, object, CancellationToken, Task> InvokeDelegate { get; init; }
}
```

### ICatalog

```csharp
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
    CatalogInfo ToCatalogInfo();
}
```

**Implementation:** `ImmutableCatalog` backed by `FrozenDictionary<string, ServiceDescriptor>` and `FrozenDictionary<string, ChannelDescriptor>` for O(1) lookup with zero overhead.

### IDelegateCompiler

```csharp
namespace Highway.Client.Scanning;

internal interface IDelegateCompiler
{
    Func<object, object, CancellationToken, Task<object>> CompileServiceDelegate(Type serviceType, Type requestType, Type responseType);
    Func<object, object, CancellationToken, Task> CompileSubscriberDelegate(Type subscriberType, Type messageType);
}
```

**Implementation:** Uses `Expression.Lambda` to compile a strongly-typed call to `ExecuteAsync`/`SubscribeAsync`, cast to the generic delegate signature. This happens once at startup per type.

```csharp
// Pseudocode for service delegate compilation:
// (object svc, object req, CancellationToken ct) =>
//     ((AsyncService<TReq, TRes>)svc).ExecuteAsync((TReq)req, ct).ContinueWith(t => (object)t.Result)
```

### CatalogInfo (for Heartbeat)

```csharp
namespace Highway.Abstractions;

/// <summary>
/// Serializable catalog summary sent via HW.HEARTBEAT.
/// </summary>
public sealed class CatalogInfo
{
    public required IReadOnlyList<CatalogServiceEntry> Services { get; init; }
    public required IReadOnlyList<CatalogChannelEntry> Channels { get; init; }
}

public sealed class CatalogServiceEntry
{
    public required string Name { get; init; }
    public required string RequestTypeName { get; init; }
    public required string ResponseTypeName { get; init; }
}

public sealed class CatalogChannelEntry
{
    public required string Name { get; init; }
    public required int SubscriberCount { get; init; }
}
```

## DI Registration Flow

```csharp
public static IServiceCollection AddHighway(this IServiceCollection services, Action<HighwayOptions> configure)
{
    var options = new HighwayOptions();
    configure(options);

    if (string.IsNullOrEmpty(options.Server))
        throw new InvalidOperationException("HighwayOptions.Server is required. Use HighwayTestServer for testing.");

    // 1. Scan
    var assemblySource = new DefaultAssemblySource(options);
    var typeScanner = new DefaultTypeScanner();
    var delegateCompiler = new ExpressionDelegateCompiler();
    var scanResult = typeScanner.Scan(assemblySource.GetAssemblies());

    // 2. Compile delegates
    foreach (var service in scanResult.Services)
        service.InvokeDelegate = delegateCompiler.CompileServiceDelegate(...);
    foreach (var channel in scanResult.Channels)
        foreach (var sub in channel.Subscribers)
            sub.InvokeDelegate = delegateCompiler.CompileSubscriberDelegate(...);

    // 3. Build catalog
    var catalog = new ImmutableCatalog(scanResult.Services, scanResult.Channels);
    services.AddSingleton<ICatalog>(catalog);

    // 4. Register discovered types in DI
    foreach (var service in scanResult.Services)
        services.Add(new SD(service.ImplementationType, MapLifetime(service.Lifetime)));
    foreach (var channel in scanResult.Channels)
        foreach (var sub in channel.Subscribers)
            services.Add(new SD(sub.ImplementationType, MapLifetime(sub.Lifetime)));

    // 5. Register Highway infrastructure
    services.AddSingleton(options);
    services.AddSingleton<IHighwayClient, HighwayClient>();

    return services;
}
```

**Note:** `AddHighway()` without a server configuration throws. There is no local-only mode. For testing use `HighwayTestServer` which provides an embedded in-process server.

## Dispatch Flow (Runtime)

There is no local dispatch. All calls go through the server (see feature 005/006). The catalog's role at runtime is:

1. **Worker loop routing** — The client knows which service queues to `HW.DEQUEUE` from (based on catalog entries)
2. **Service execution** — When work arrives from the server, the engine uses the catalog to: create a scope → resolve the service → call the compiled delegate → return result
3. **Subscriber dispatch** — When a pub/sub message arrives, the engine fans out to local subscribers using the catalog

```
Server delivers work (HW.DEQUEUE / HW.RECEIVE)
    │
    ▼
1. catalog.GetServiceDescriptor(name) → ServiceDescriptor
2. Create IServiceScope from IServiceScopeFactory
3. Resolve service instance from scope.ServiceProvider
4. Call descriptor.InvokeDelegate(instance, request, cancellationToken)
5. Dispose scope
6. Send HW.REPLY with result
```

## Pub/Sub Dispatch Flow

When a message arrives from the server (via `HW.RECEIVE`):

```
Message arrives from server
    │
    ▼
1. Get channel name from message envelope
2. catalog.GetChannelDescriptor(name) → ChannelDescriptor
3. For each subscriber in descriptor.Subscribers:
   a. Create IServiceScope
   b. Resolve subscriber instance
   c. Call subscriber.InvokeDelegate(instance, message, cancellationToken)
   d. Catch exceptions (swallow per subscriber, don't abort siblings)
   e. Dispose scope
4. Send HW.RACK acknowledgment
```

## Options Extension

```csharp
public sealed class HighwayOptions
{
    public string NodeName { get; set; } = ...;
    public string? Server { get; set; }
    public TimeSpan CallTimeout { get; set; } = TimeSpan.FromSeconds(30);

    // Assembly scanning
    public List<Assembly> AdditionalAssemblies { get; } = [];
    public List<Func<Assembly, bool>> ExcludedAssemblies { get; } = [];
}
```

## Performance Characteristics

| Operation | Old (v0.8) | New |
|---|---|---|
| Startup scan | Fast (reflection) | Same + delegate compilation (~50ms total) |
| Service dispatch | `ObjectMethodExecutor.Create` per call + reflection | Compiled delegate (zero-alloc invocation) |
| Attribute lookup | `GetCustomAttribute` per call | Pre-cached `Type → name` dictionary |
| DI resolution | Root provider (no scopes) | Proper scope per request |
| Catalog lookup | Dictionary | `FrozenDictionary` (optimized for read-heavy) |

## File Layout

```
src/Highway.Client/
├── Scanning/
│   ├── IAssemblySource.cs
│   ├── DefaultAssemblySource.cs
│   ├── ITypeScanner.cs
│   ├── DefaultTypeScanner.cs
│   ├── IDelegateCompiler.cs
│   ├── ExpressionDelegateCompiler.cs
│   ├── ServiceDescriptor.cs
│   ├── ChannelDescriptor.cs
│   ├── SubscriberDescriptor.cs
│   ├── ScanResult.cs
│   ├── ICatalog.cs
│   └── ImmutableCatalog.cs
├── HighwayOptions.cs (updated)
├── HighwayClient.cs
└── ServiceCollectionExtensions.cs  (AddHighway)

src/Highway.Abstractions/
├── CatalogInfo.cs
├── CatalogServiceEntry.cs
└── CatalogChannelEntry.cs
```
